using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class CombinePass : ScriptableRenderPass
{
    static class ShaderIDs
    {
        public static readonly int SceneDirect = Shader.PropertyToID("SceneDirect");
        public static readonly int VXGIIndirect = Shader.PropertyToID("VXGIIndirect");
        public static readonly int EnableTemporalFilter = Shader.PropertyToID("EnableTemporalFilter");
        public static readonly int VoxelTexAlbedo = Shader.PropertyToID("VoxelTexAlbedo");
        public static readonly int VoxelTexNormal = Shader.PropertyToID("VoxelTexNormal");
        public static readonly int VoxelTexDirectLighting = Shader.PropertyToID("VoxelTexDirectLighting");
        public static readonly int VoxelTexIndirectLighting = Shader.PropertyToID("VoxelTexIndirectLighting");
        public static readonly int ScreenConeTraceIrradiance = Shader.PropertyToID("ScreenConeTraceIrradiance");
        public static readonly int ScreenBilateralFiltering = Shader.PropertyToID("ScreenBilateralFiltering");
        public static readonly int ScreenBlendIrradiance = Shader.PropertyToID("ScreenBlendIrradiance");
        public static readonly int EmissiveMulti = Shader.PropertyToID("EmissiveMulti");
        public static readonly int VoxelSize = Shader.PropertyToID("VoxelSize");
        public static readonly int RayStepSize = Shader.PropertyToID("RayStepSize");
        public static readonly int VisualizeDebugType = Shader.PropertyToID("VisualizeDebugType");
        public static readonly int DirectLightingDebugMipLevel = Shader.PropertyToID("DirectLightingDebugMipLevel");
        public static readonly int IndirectLightingDebugMipLevel = Shader.PropertyToID("IndirectLightingDebugMipLevel");
    }

    private VoxelGIRendererFeature m_Feature;
    private RenderTexture m_RTCombine;

    public CombinePass(VoxelGIRendererFeature feature)
    {
        m_Feature = feature;
        renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        int w = renderingData.cameraData.camera.pixelWidth;
        int h = renderingData.cameraData.camera.pixelHeight;
        m_RTCombine = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
    }

    public override void OnCameraCleanup(CommandBuffer cmd)
    {
        if (m_RTCombine != null)
        {
            RenderTexture.ReleaseTemporary(m_RTCombine);
            m_RTCombine = null;
        }
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (m_Feature?.Resources?.GiMaterial == null || m_RTCombine == null) return;

        VoxelGIProfiler.BeginSample("CombinePass");

        Camera camera = renderingData.cameraData.camera;
        var res = m_Feature.Resources;
        var debugCfg = m_Feature.DebugCfg;
        var colorTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

        CommandBuffer cmd = CommandBufferPool.Get(debugCfg.DebugMode ? "VoxelGI/Debug" : "VoxelGI/Combine");
        cmd.Clear();

        if (debugCfg.DebugMode)
        {
            RenderDebug(cmd, camera, colorTarget);
        }
        else
        {
            RenderNormalCombine(cmd, camera, colorTarget);
        }

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        VoxelGIProfiler.EndSample("CombinePass");
        VoxelGIProfiler.OnFrameEnd();
    }

    void RenderNormalCombine(CommandBuffer cmd, Camera camera, RTHandle colorTarget)
    {
        var res = m_Feature.Resources;
        var history = m_Feature.GetTemporalHistory(camera);
        RenderTexture indirectSource = ResolveIndirectSource(camera);

        // 先将colorTarget复制到临时RT，避免读写同一个RT导致反馈循环（每帧累加直到全白）
        var sceneDirectCopy = RenderTexture.GetTemporary(camera.pixelWidth, camera.pixelHeight, 0, RenderTextureFormat.ARGBHalf);
        cmd.Blit(colorTarget, sceneDirectCopy);

        cmd.SetGlobalTexture(ShaderIDs.SceneDirect, sceneDirectCopy);
        cmd.SetGlobalTexture(ShaderIDs.VXGIIndirect, indirectSource);
        cmd.SetGlobalInt(ShaderIDs.EnableTemporalFilter,
            m_Feature.TemporalFilterCfg.EnableTemporalFilter ? 1 : 0);
        
        cmd.SetRenderTarget(m_RTCombine);
        cmd.ClearRenderTarget(true, true, Color.black);
        cmd.DrawMesh(res.QuadMesh, Matrix4x4.identity, res.GiMaterial, 0, 4);

        cmd.Blit(m_RTCombine, colorTarget);
        RenderTexture.ReleaseTemporary(sceneDirectCopy);
    }

    void RenderDebug(CommandBuffer cmd, Camera camera, RTHandle colorTarget)
    {
        var res = m_Feature.Resources;
        var debugCfg = m_Feature.DebugCfg;

        cmd.SetGlobalTexture(ShaderIDs.VoxelTexAlbedo, res.UavAlbedo);
        cmd.SetGlobalTexture(ShaderIDs.VoxelTexNormal, res.UavNormal);
        // 方案A：旧版独立Emissive/Opacity调试纹理绑定保留注释，便于回退
        // cmd.SetGlobalTexture(Shader.PropertyToID("VoxelTexEmissive"), res.UavEmissive);
        // cmd.SetGlobalTexture(Shader.PropertyToID("VoxelTexOpacity"), res.UavOpacity);
        cmd.SetGlobalTexture(ShaderIDs.VoxelTexDirectLighting, res.UavDirectLighting);
        cmd.SetGlobalTexture(ShaderIDs.VoxelTexIndirectLighting, res.UavIndirectLighting);

        var conePass = FindConeTracingPass();
        if (conePass != null)
            cmd.SetGlobalTexture(ShaderIDs.ScreenConeTraceIrradiance, conePass.GetConeTraceResult());

        var bilateralPass = FindBilateralFilterPass();
        if (bilateralPass != null)
            cmd.SetGlobalTexture(ShaderIDs.ScreenBilateralFiltering, bilateralPass.GetFilteredResult());

        var temporalPass = FindTemporalFilterPass();
        if (temporalPass != null)
        {
            var history = m_Feature.GetTemporalHistory(camera);
            if (history != null)
                cmd.SetGlobalTexture(ShaderIDs.ScreenBlendIrradiance,
                    history.PingPongFlag ? history.RT0 : history.RT1);
        }

        cmd.SetGlobalFloat(ShaderIDs.EmissiveMulti, m_Feature.DirectLightingCfg.EmissiveMulti);
        cmd.SetGlobalFloat(ShaderIDs.VoxelSize, m_Feature.VoxelSize);
        cmd.SetGlobalFloat(ShaderIDs.RayStepSize, debugCfg.RayStepSize);
        cmd.SetGlobalInt(ShaderIDs.VisualizeDebugType, (int)debugCfg.DebugType);
        cmd.SetGlobalInt(ShaderIDs.DirectLightingDebugMipLevel,
            Mathf.Clamp(debugCfg.DirectLightingDebugMipLevel, 0, m_Feature.MipLevel - 1));
        cmd.SetGlobalInt(ShaderIDs.IndirectLightingDebugMipLevel,
            Mathf.Clamp(debugCfg.IndirectLightingDebugMipLevel, 0, m_Feature.MipLevel - 1));

        var tempRT = RenderTexture.GetTemporary(camera.pixelWidth, camera.pixelHeight, 0, RenderTextureFormat.ARGBHalf);
        cmd.SetRenderTarget(tempRT);
        cmd.ClearRenderTarget(true, true, Color.black);
        cmd.DrawMesh(res.QuadMesh, Matrix4x4.identity, res.GiMaterial, 0, 5);
        cmd.Blit(tempRT, colorTarget);
        RenderTexture.ReleaseTemporary(tempRT);
    }

    RenderTexture ResolveIndirectSource(Camera camera)
    {
        if (m_Feature.BilateralFilterCfg.EnableBilateralFilter)
        {
            var bilateralPass = FindBilateralFilterPass();
            if (bilateralPass != null)
                return bilateralPass.GetFilteredResult();
        }
        if (m_Feature.TemporalFilterCfg.EnableTemporalFilter)
        {
            var temporalPass = FindTemporalFilterPass();
            if (temporalPass != null)
                return temporalPass.GetFilteredResult(camera);
        }
        var conePass = FindConeTracingPass();
        return conePass?.GetConeTraceResult();
    }

    ConeTracingPass FindConeTracingPass() { return m_Feature.GetConeTracingPass(); }
    TemporalFilterPass FindTemporalFilterPass() { return m_Feature.GetTemporalFilterPass(); }
    BilateralFilterPass FindBilateralFilterPass() { return m_Feature.GetBilateralFilterPass(); }
}
