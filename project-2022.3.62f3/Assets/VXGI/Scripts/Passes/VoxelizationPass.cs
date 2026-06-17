using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class VoxelizationPass : ScriptableRenderPass
{
    static class ShaderIDs
    {
        public static readonly int TmpOpacityAccum = Shader.PropertyToID("TmpOpacityAccum");
        public static readonly int TmpEmissiveAccum = Shader.PropertyToID("TmpEmissiveAccum");
        public static readonly int PackedAlbedoTex = Shader.PropertyToID("PackedAlbedoTex");
        public static readonly int PackedNormalTex = Shader.PropertyToID("PackedNormalTex");
    }

    private VoxelGIRendererFeature m_Feature;
    RenderTexture m_DummyTarget;
    RenderTextureDescriptor m_DummyDesc;

    Matrix4x4 m_ForwordViewMatrix;
    Matrix4x4 m_RightViewMatrix;
    Matrix4x4 m_UpViewMatrix;
    Matrix4x4 m_ShadowViewMatrix;
    Matrix4x4 m_VoxelProj;
    Matrix4x4 m_ShadowProj;
    Vector3 m_OriginPos;
    Vector3 m_VoxelSize;  // 体素化区域的xyz尺寸（支持长方体）

    public VoxelizationPass(VoxelGIRendererFeature feature)
    {
        m_Feature = feature;
        renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        m_DummyDesc = new RenderTextureDescriptor(m_Feature.VoxelizationCfg.VoxelTextureResolution,
            m_Feature.VoxelizationCfg.VoxelTextureResolution, RenderTextureFormat.R8);

        m_DummyTarget = RenderTexture.GetTemporary(m_DummyDesc);
    }

    public override void OnCameraCleanup(CommandBuffer cmd)
    {
        if (m_DummyTarget != null) { RenderTexture.ReleaseTemporary(m_DummyTarget); m_DummyTarget = null; }
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (m_Feature?.Resources?.GiMaterial == null) return;

        VoxelGIProfiler.BeginSample("VoxelizationPass");

        Camera camera = renderingData.cameraData.camera;
        var res = m_Feature.Resources;

        m_VoxelSize = m_Feature.VoxelizationSize;
        m_OriginPos = m_Feature.GetOriginPos(camera);
        m_VoxelProj = Matrix4x4.Ortho(-m_VoxelSize.x * 0.5f, m_VoxelSize.x * 0.5f,
            -m_VoxelSize.y * 0.5f, m_VoxelSize.y * 0.5f, -m_VoxelSize.z, m_VoxelSize.z);

        UpdateMatrices();

        CommandBuffer cmd = CommandBufferPool.Get("VoxelGI/Voxelization");

        SetCameraParams(cmd, camera);
        RenderShadowMap(cmd, context, ref renderingData);
        RenderVoxel(cmd, context, ref renderingData);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        VoxelGIProfiler.EndSample("VoxelizationPass");
    }

    void SetCameraParams(CommandBuffer cmd, Camera camera)
    {
        cmd.SetGlobalVector(Shader.PropertyToID("CameraPosW"), camera.transform.position);
        var vp = camera.projectionMatrix * camera.worldToCameraMatrix;
        cmd.SetGlobalMatrix(Shader.PropertyToID("CameraView"), camera.worldToCameraMatrix);
        cmd.SetGlobalMatrix(Shader.PropertyToID("CameraViewProj"), vp);
        cmd.SetGlobalMatrix(Shader.PropertyToID("CameraInvView"), camera.cameraToWorldMatrix);
        cmd.SetGlobalMatrix(Shader.PropertyToID("CameraReprojectInvViewProj"),
            (GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix).inverse);
        cmd.SetGlobalMatrix(Shader.PropertyToID("CameraInvViewProj"), vp.inverse);
        cmd.SetGlobalFloat(Shader.PropertyToID("CameraFielfOfView"), camera.fieldOfView);
        cmd.SetGlobalFloat(Shader.PropertyToID("CameraAspect"), camera.aspect);
        cmd.SetGlobalInt(Shader.PropertyToID("VoxelTextureResolution"), m_Feature.VoxelizationCfg.VoxelTextureResolution);
    }

    void UpdateMatrices()
    {
        var forwardPos = m_OriginPos - Vector3.forward * (m_VoxelSize.z * 0.5f);
        var rightPos = m_OriginPos - Vector3.right * (m_VoxelSize.x * 0.5f);
        var upPos = m_OriginPos - Vector3.up * (m_VoxelSize.y * 0.5f);

        m_ForwordViewMatrix = BuildViewMatrix(forwardPos, m_OriginPos, Vector3.up);
        m_RightViewMatrix = BuildViewMatrix(rightPos, m_OriginPos, Vector3.up);
        m_UpViewMatrix = BuildViewMatrix(upPos, m_OriginPos, -Vector3.forward);

        // Shadow矩阵现在在RenderShadowMap中动态计算（基于ShadowCaster包围盒）
    }

    // 构建View矩阵（前方=+Z，用于体素化正交投影）
    // 注意：配合Matrix4x4.Ortho的near/far对称设置使用
    Matrix4x4 BuildViewMatrix(Vector3 eye, Vector3 target, Vector3 up)
    {
        var rot = Quaternion.LookRotation((target - eye).normalized, up);
        return Matrix4x4.TRS(eye, rot, Vector3.one).inverse;
    }

    // 构建标准View矩阵（前方=-Z，和Unity Camera.worldToCameraMatrix一致）
    // 用于Shadow投影，配合GL.GetGPUProjectionMatrix使用
    Matrix4x4 BuildShadowViewMatrix(Vector3 eye, Vector3 target, Vector3 up)
    {
        var rot = Quaternion.LookRotation((target - eye).normalized, up);
        var trs = Matrix4x4.TRS(eye, rot, Vector3.one).inverse;
        // Unity相机惯例：view space中前方为-Z，需要翻转第三行
        trs.SetRow(2, -trs.GetRow(2));
        return trs;
    }

    void RenderShadowMap(CommandBuffer cmd, ScriptableRenderContext context, ref RenderingData renderingData)
    {
        var sunLight = m_Feature.GetSunLight();
        if (sunLight == null) return;

        VoxelGIProfiler.BeginSample("  ShadowMap");

        // 使用体素化区域的Bounds直接计算near/far（无需遍历场景物体）
        VoxelGIProfiler.BeginSample("    ComputeShadowBounds");
        Bounds voxelBounds = new Bounds(m_OriginPos, m_VoxelSize);
        
        // 阴影相机位置：使用固定距离放在体素化区域后方
        float shadowRange = m_Feature.ShadowMapRange;
        Vector3 shadowCamPos = m_OriginPos - sunLight.transform.forward * shadowRange;
        
        // 基于固定相机位置计算near/far
        (float near, float far) = FitNearFarToShadowCasters(voxelBounds, sunLight.transform.forward, shadowCamPos);
        
        m_ShadowProj = Matrix4x4.Ortho(-shadowRange, shadowRange, -shadowRange, shadowRange, near, far);
        m_ShadowViewMatrix = BuildShadowViewMatrix(shadowCamPos, m_OriginPos, Vector3.up);
        VoxelGIProfiler.EndSample("    ComputeShadowBounds");

        // GPU版本的VP矩阵用于SV_POSITION（带Y翻转+反向Z，确保片元不被clip）
        var gpuShadowProj = GL.GetGPUProjectionMatrix(m_ShadowProj, true);
        var shadowVP = gpuShadowProj * m_ShadowViewMatrix;

        // OpenGL惯例的VP矩阵（不带反向Z，不带Y翻转）
        // 用于ShadowFs计算线性深度[0,1]写入color target，以及Compute Shader采样比较
        var shadowVPLinear = m_ShadowProj * m_ShadowViewMatrix;
        m_Feature.Resources.ShadowVPMatrix = shadowVPLinear;

        // 提交设置命令
        cmd.SetRenderTarget(m_Feature.Resources.ShadowDepth);
        cmd.ClearRenderTarget(false, true, Color.black);
        cmd.SetGlobalMatrix("WorldToShadowVP", shadowVP);
        cmd.SetGlobalMatrix("WorldToShadowVPLinear", shadowVPLinear);
        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();

        // 使用DrawRenderers替代手动遍历
        VoxelGIProfiler.BeginSample("    DrawRenderers_Shadow");
        
        var sortingSettings = new SortingSettings(renderingData.cameraData.camera)
        {
            criteria = SortingCriteria.CommonOpaque
        };
        
        // 使用材质自身的VoxelGI_Shadow pass绘制体素阴影图，不再依赖overrideMaterial
        var drawSettings = new DrawingSettings(new ShaderTagId("VoxelGI_Shadow"), sortingSettings)
        {
            perObjectData = PerObjectData.None
        };
        
        var filterSettings = new FilteringSettings(RenderQueueRange.opaque)
        {
            renderingLayerMask = uint.MaxValue,
            layerMask = -1
        };

        context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filterSettings);
        VoxelGIProfiler.EndSample("    DrawRenderers_Shadow");

        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();

        VoxelGIProfiler.EndSample("  ShadowMap");
    }

    // 根据包围盒在Light方向上计算最紧凑的near/far（直接使用体素化区域Bounds，无需遍历场景）
    (float near, float far) FitNearFarToShadowCasters(Bounds bounds, Vector3 lightForward, Vector3 shadowCamPos)
    {
        // 包围盒的8个顶点
        Vector3 center = bounds.center;
        Vector3 extents = bounds.extents;
        Vector3[] corners = new Vector3[8]
        {
            center + new Vector3(-extents.x, -extents.y, -extents.z),
            center + new Vector3(-extents.x, -extents.y, extents.z),
            center + new Vector3(-extents.x, extents.y, -extents.z),
            center + new Vector3(-extents.x, extents.y, extents.z),
            center + new Vector3(extents.x, -extents.y, -extents.z),
            center + new Vector3(extents.x, -extents.y, extents.z),
            center + new Vector3(extents.x, extents.y, -extents.z),
            center + new Vector3(extents.x, extents.y, extents.z)
        };

        // 投影所有顶点到Light方向,找到最近和最远点
        float minDist = float.MaxValue;
        float maxDist = float.MinValue;

        foreach (var corner in corners)
        {
            Vector3 toCorner = corner - shadowCamPos;
            float dist = Vector3.Dot(toCorner, lightForward);
            minDist = Mathf.Min(minDist, dist);
            maxDist = Mathf.Max(maxDist, dist);
        }

        // 留一些margin避免裁切
        float margin = (maxDist - minDist) * 0.1f;
        minDist = minDist - margin;
        maxDist = maxDist + margin;
        
        // 确保near至少为正值
        if (minDist < 0.01f)
        {
            minDist = 0.01f;
        }

        return (minDist, maxDist);
    }

    void RenderVoxel(CommandBuffer cmd, ScriptableRenderContext context, ref RenderingData renderingData)
    {
        var res = m_Feature.Resources;
        int voxelRes = m_Feature.VoxelizationCfg.VoxelTextureResolution;

        VoxelGIProfiler.BeginSample("  Voxelize");

        cmd.SetRenderTarget(res.UavAlbedo, 0, CubemapFace.Unknown, -1);
        cmd.ClearRenderTarget(true, true, Color.black);
        cmd.SetRenderTarget(res.UavNormal, 0, CubemapFace.Unknown, -1);
        cmd.ClearRenderTarget(true, true, Color.black);
        cmd.SetRenderTarget(res.TmpOpacityAccum, 0, CubemapFace.Unknown, -1);
        cmd.ClearRenderTarget(true, true, Color.black);
        cmd.SetRenderTarget(res.TmpEmissiveAccum, 0, CubemapFace.Unknown, -1);
        cmd.ClearRenderTarget(true, true, Color.black);
        // 方案A：旧版独立Emissive/Opacity纹理清理逻辑保留注释，便于回退
        // cmd.SetRenderTarget(res.UavEmissive, 0, CubemapFace.Unknown, -1);
        // cmd.ClearRenderTarget(true, true, Color.black);
        // cmd.SetRenderTarget(res.UavOpacity, 0, CubemapFace.Unknown, -1);
        // cmd.ClearRenderTarget(true, true, Color.black);

        // GL.GetGPUProjectionMatrix(proj, false)：修正Z范围（OpenGL z[-1,1] → DX11 z[0,1]），不翻转Y
        // renderIntoTexture=false：不翻转Y，因为GS中的保守光栅化逻辑依赖NDC的xy方向
        var gpuVoxelProj = GL.GetGPUProjectionMatrix(m_VoxelProj, false);
        cmd.SetGlobalMatrix(Shader.PropertyToID("VoxelizationForwardVP"), gpuVoxelProj * m_ForwordViewMatrix);
        cmd.SetGlobalMatrix(Shader.PropertyToID("VoxelizationRightVP"), gpuVoxelProj * m_RightViewMatrix);
        cmd.SetGlobalMatrix(Shader.PropertyToID("VoxelizationUpVP"), gpuVoxelProj * m_UpViewMatrix);

        var voxelToWorld = Matrix4x4.TRS(
            m_OriginPos - m_VoxelSize * 0.5f,
            Quaternion.identity,
            Vector3.one * m_Feature.VoxelSize);
        cmd.SetGlobalMatrix(Shader.PropertyToID("VoxelToWorld"), voxelToWorld);
        cmd.SetGlobalMatrix(Shader.PropertyToID("WorldToVoxel"), voxelToWorld.inverse);

        cmd.SetGlobalFloat(Shader.PropertyToID("HalfPixelSize"),
            m_Feature.VoxelizationCfg.ConsevativeRasterizeScale / voxelRes);
        cmd.SetGlobalInt(Shader.PropertyToID("EnableConservativeRasterization"),
            m_Feature.VoxelizationCfg.EnableConservativeRasterization ? 1 : 0);

        // 先SetRenderTarget再SetRandomWriteTarget（之前这个顺序DrawMesh能正常出图）
        cmd.SetRenderTarget(m_DummyTarget, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
        cmd.ClearRenderTarget(true, true, Color.black);

        cmd.SetRandomWriteTarget(1, res.UavAlbedo);
        cmd.SetRandomWriteTarget(2, res.UavNormal);
        cmd.SetRandomWriteTarget(3, res.TmpOpacityAccum);
        cmd.SetRandomWriteTarget(4, res.TmpEmissiveAccum);
        // 方案A：旧版独立Emissive/Opacity UAV绑定逻辑保留注释，便于回退
        // cmd.SetRandomWriteTarget(3, res.UavEmissive);
        // cmd.SetRandomWriteTarget(4, res.UavOpacity);

        // 提交设置命令
        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();

        // 使用DrawRenderers自动批处理和剔除
        VoxelGIProfiler.BeginSample("    DrawRenderers_Voxel");
        
        var sortingSettings = new SortingSettings(renderingData.cameraData.camera)
        {
            criteria = SortingCriteria.CommonOpaque
        };
        
        // 使用自定义LightMode tag来匹配shader pass
        // 不使用overrideMaterial，让物体使用自己的VoxelGI/Lit材质
        var drawSettings = new DrawingSettings(new ShaderTagId("VoxelGI_Voxelization"), sortingSettings)
        {
            perObjectData = PerObjectData.None,
            enableDynamicBatching = false,
            enableInstancing = true  // 启用GPU Instancing
        };
        
        var filterSettings = new FilteringSettings(RenderQueueRange.opaque)
        {
            renderingLayerMask = uint.MaxValue,
            // 仅体素化用户指定Layer，便于排除Blocker等几何体
            layerMask = m_Feature.VoxelizationCfg.VoxelizationLayerMask.value,
            excludeMotionVectorObjects = false
        };

        // 绘制渲染器
        context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filterSettings);
        VoxelGIProfiler.EndSample("    DrawRenderers_Voxel");

        cmd.ClearRandomWriteTargets();
        VoxelGIProfiler.EndSample("  Voxelize");

        cmd.BeginSample("OverwritePackedAlpha");
        OverwritePackedAlpha(cmd, res, voxelRes);
        cmd.EndSample("OverwritePackedAlpha");
    }

    void OverwritePackedAlpha(CommandBuffer cmd, VoxelGIRendererFeature.VoxelGIResources res, int voxelRes)
    {
        VoxelGIProfiler.BeginSample("    OverwritePackedAlpha");
        int groupNum = Mathf.CeilToInt(voxelRes / 8f);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdOverwriteGBufferAlpha, ShaderIDs.PackedAlbedoTex, res.UavAlbedo);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdOverwriteGBufferAlpha, ShaderIDs.PackedNormalTex, res.UavNormal);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdOverwriteGBufferAlpha, ShaderIDs.TmpOpacityAccum, res.TmpOpacityAccum);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdOverwriteGBufferAlpha, ShaderIDs.TmpEmissiveAccum, res.TmpEmissiveAccum);
        cmd.DispatchCompute(res.VXGIComputeShader, res.ComputeKernelIdOverwriteGBufferAlpha, groupNum, groupNum, groupNum);
        VoxelGIProfiler.EndSample("    OverwritePackedAlpha");
    }
}
