using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class VoxelLightingComputePass : ScriptableRenderPass
{
    private VoxelGIRendererFeature m_Feature;

    private Camera m_Camera;
    static class ComputeKeywords
    {
        public const string IndirectConeTraceLow = "INDIRECT_CONE_TRACE_LOW";
        public const string IndirectConeTraceMid = "INDIRECT_CONE_TRACE_MID";
        public const string IndirectConeTraceHigh = "INDIRECT_CONE_TRACE_HIGH";
    }
    static class ShaderIDs
    {
        public static readonly int RWAlbedo = Shader.PropertyToID("RWAlbedo");
        public static readonly int RWNormal = Shader.PropertyToID("RWNormal");
        public static readonly int RWEmissive = Shader.PropertyToID("RWEmissive");
        public static readonly int RWOpacity = Shader.PropertyToID("RWOpacity");
        public static readonly int ShadowDepth = Shader.PropertyToID("ShadowDepth");
        public static readonly int OutRadiance = Shader.PropertyToID("OutRadiance");
        public static readonly int VoxelLighting = Shader.PropertyToID("VoxelLighting");
        public static readonly int OutIndirectRadiance = Shader.PropertyToID("OutIndirectRadiance");
    }

    public VoxelLightingComputePass(VoxelGIRendererFeature feature)
    {
        m_Feature = feature;
        renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (m_Feature?.Resources?.VXGIComputeShader == null) return;

        VoxelGIProfiler.BeginSample("LightingPass");

        m_Camera = renderingData.cameraData.camera;

        CommandBuffer cmd = CommandBufferPool.Get("VoxelGI/Lighting");
        cmd.Clear();

        ComputeDirectLighting(cmd);
        if (m_Feature.IndirectLightingCfg.EnableSecondBounce)
            ComputeIndirectLighting(cmd);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        VoxelGIProfiler.EndSample("LightingPass");
    }

    void ComputeDirectLighting(CommandBuffer cmd)
    {
        var res = m_Feature.Resources;
        var cfg = m_Feature.VoxelizationCfg;
        var dcfg = m_Feature.DirectLightingCfg;
        int voxelRes = cfg.VoxelTextureResolution;
        int mipLevel = m_Feature.MipLevel;

        VoxelGIProfiler.BeginSample("  DirectLighting");
        cmd.BeginSample("Direct");

        cmd.SetRenderTarget(res.UavDirectLighting, 0, CubemapFace.Unknown, -1);
        cmd.ClearRenderTarget(true, true, Color.black);

        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdDirectLighting, ShaderIDs.RWAlbedo, res.UavAlbedo);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdDirectLighting, ShaderIDs.RWNormal, res.UavNormal);
        // 方案A：旧版独立Emissive/Opacity输入绑定保留注释，便于回退
        // cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdDirectLighting, ShaderIDs.RWEmissive, res.UavEmissive);
        // cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdDirectLighting, ShaderIDs.RWOpacity, res.UavOpacity);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdDirectLighting, ShaderIDs.ShadowDepth, res.ShadowDepth);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdDirectLighting, ShaderIDs.OutRadiance, res.UavDirectLighting);

        var voxelToWorld = Matrix4x4.TRS(
            m_Feature.GetOriginPos(m_Camera) - new Vector3(m_Feature.VoxelizationRange, m_Feature.VoxelizationRange, m_Feature.VoxelizationRange) * 0.5f,
            Quaternion.identity,
            Vector3.one * m_Feature.VoxelSize);

        cmd.SetComputeMatrixParam(res.VXGIComputeShader, "VoxelToWorld", voxelToWorld);
        cmd.SetComputeMatrixParam(res.VXGIComputeShader, "WorldToVoxel", voxelToWorld.inverse);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "VoxelTextureResolution", voxelRes);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "VoxelSize", m_Feature.VoxelSize);

        var sunLight = m_Feature.GetSunLight();
        if (sunLight != null)
        {
            cmd.SetComputeVectorParam(res.VXGIComputeShader, "SunLightColor", sunLight.color);
            cmd.SetComputeVectorParam(res.VXGIComputeShader, "SunLightDir", sunLight.transform.forward);
            cmd.SetComputeFloatParam(res.VXGIComputeShader, "SunLightIntensity", sunLight.intensity);
        }

        cmd.SetComputeFloatParam(res.VXGIComputeShader, "LightIndensityMulti", dcfg.LightIndensityMulti);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "EmissiveMulti", dcfg.EmissiveMulti);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "ShadowSunBias", dcfg.ShadowSunBias);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "ShadowNormalBias", dcfg.ShadowNormalBias);

        // 设置ShadowVP矩阵（从VoxelizationPass中计算并存储）
        cmd.SetComputeMatrixParam(res.VXGIComputeShader, "gWorldToShadowVP", res.ShadowVPMatrix);

        cmd.DispatchCompute(res.VXGIComputeShader, res.ComputeKernelIdDirectLighting,
            voxelRes / 4 + 1, voxelRes / 4 + 1, voxelRes / 4 + 1);

        cmd.ClearRandomWriteTargets();
        cmd.EndSample("Direct");
        VoxelGIProfiler.EndSample("  DirectLighting");

        cmd.BeginSample("DirectMipmap");
        GenerateMipChain(cmd, res.UavDirectLighting, voxelRes, mipLevel);
        cmd.EndSample("DirectMipmap");
    }

    void ComputeIndirectLighting(CommandBuffer cmd)
    {
        var res = m_Feature.Resources;
        var cfg = m_Feature.VoxelizationCfg;
        var icfg = m_Feature.IndirectLightingCfg;
        int voxelRes = cfg.VoxelTextureResolution;
        int mipLevel = m_Feature.MipLevel;

        VoxelGIProfiler.BeginSample("  IndirectLighting");
        cmd.BeginSample("Indirect");

        cmd.SetRenderTarget(res.UavIndirectLighting, 0, CubemapFace.Unknown, -1);
        cmd.ClearRenderTarget(true, true, Color.black);

        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdIndirectLighting, ShaderIDs.RWAlbedo, res.UavAlbedo);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdIndirectLighting, ShaderIDs.RWNormal, res.UavNormal);
        // 方案A：旧版独立Opacity输入绑定保留注释，便于回退
        // cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdIndirectLighting, ShaderIDs.RWOpacity, res.UavOpacity);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdIndirectLighting, ShaderIDs.VoxelLighting, res.UavDirectLighting);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdIndirectLighting, ShaderIDs.OutIndirectRadiance, res.UavIndirectLighting);

        cmd.SetComputeFloatParam(res.VXGIComputeShader, "IndirectLightingMaxMipLevel", mipLevel);
        cmd.SetComputeIntParam(res.VXGIComputeShader, "IndirectLightingMaxStepNum", icfg.IndirectLightingMaxStepNum);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "IndirectLightingAlphaAtten", icfg.IndirectLightingAlphaAtten);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "IndirectLightingScale", icfg.IndirectLightingScale);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "IndirectLightingFirstStep", icfg.IndirectLightingFirstStep);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "IndirectLightingStepScale", icfg.IndirectLightingStepScale);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "IndirectLightingConeAngle", icfg.IndirectLightingConeAngle);
        cmd.SetComputeIntParam(res.VXGIComputeShader, "IndirectLightingMinMipLevel", icfg.IndirectLightingMinMipLevel);
        ApplyIndirectConeTraceKeywords(res.VXGIComputeShader, icfg.ConeTraceQuality);

        cmd.DispatchCompute(res.VXGIComputeShader, res.ComputeKernelIdIndirectLighting,
            voxelRes / 8 + 1, voxelRes / 8 + 1, voxelRes / 8 + 1);

        cmd.EndSample("Indirect");
        VoxelGIProfiler.EndSample("  IndirectLighting");

        cmd.BeginSample("IndirectMipmap");
        GenerateMipChain(cmd, res.UavIndirectLighting, voxelRes, mipLevel);
        cmd.EndSample("IndirectMipmap");
    }

    void GenerateMipChain(CommandBuffer cmd, RenderTexture target, int voxelRes, int mipLevel)
    {
        var res = m_Feature.Resources;
        for (int i = 0; i < mipLevel - 1; i++)
        {
            int currentRes = (int)((voxelRes + 0.01f) / Mathf.Pow(2f, i + 1f));
            int groupNum = Mathf.CeilToInt(currentRes / 8f);

            cmd.SetComputeIntParam(res.VXGIComputeShader, "DstRes", currentRes);
            cmd.SetComputeIntParam(res.VXGIComputeShader, "SrcMipLevel", i);
            cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdMipmap, Shader.PropertyToID("MipmapSrc"), target);
            cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdMipmap, Shader.PropertyToID("MipmapDst"), res.LightingPingPongRT, i + 1);

            cmd.DispatchCompute(res.VXGIComputeShader, res.ComputeKernelIdMipmap, groupNum, groupNum, groupNum);
            cmd.ClearRandomWriteTargets();

            CopyTexture3D(cmd, res.LightingPingPongRT, target, i + 1);
        }
    }

    void CopyTexture3D(CommandBuffer cmd, RenderTexture src, RenderTexture dst, int mipLevel)
    {
        var res = m_Feature.Resources;
        int mipRes = (int)((src.width + 0.01f) / Mathf.Pow(2f, mipLevel));
        int groupNum = Mathf.Max(1, Mathf.CeilToInt(mipRes / 8f));

        cmd.SetComputeIntParam(res.VXGIComputeShader, "CopyMipLevel", mipLevel);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdCopyTexture3D, Shader.PropertyToID("TexSrc"), src);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdCopyTexture3D, Shader.PropertyToID("TexDst"), dst, mipLevel);
        cmd.DispatchCompute(res.VXGIComputeShader, res.ComputeKernelIdCopyTexture3D, groupNum, groupNum, groupNum);
        cmd.ClearRandomWriteTargets();
    }

    void ApplyIndirectConeTraceKeywords(ComputeShader computeShader, VoxelGIRendererFeature.IndirectConeTraceQuality quality)
    {
        // 先清空旧keyword，避免跨帧保留上一次选择
        computeShader.DisableKeyword(ComputeKeywords.IndirectConeTraceLow);
        computeShader.DisableKeyword(ComputeKeywords.IndirectConeTraceMid);
        computeShader.DisableKeyword(ComputeKeywords.IndirectConeTraceHigh);

        switch (quality)
        {
            case VoxelGIRendererFeature.IndirectConeTraceQuality.Low:
                computeShader.EnableKeyword(ComputeKeywords.IndirectConeTraceLow);
                break;
            case VoxelGIRendererFeature.IndirectConeTraceQuality.Mid:
                computeShader.EnableKeyword(ComputeKeywords.IndirectConeTraceMid);
                break;
            case VoxelGIRendererFeature.IndirectConeTraceQuality.High:
                computeShader.EnableKeyword(ComputeKeywords.IndirectConeTraceHigh);
                break;
            default:
                // VeryLow: 不启用任何keyword，走默认1 cone
                break;
        }
    }
}
