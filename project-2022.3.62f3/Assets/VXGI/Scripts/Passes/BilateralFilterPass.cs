using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class BilateralFilterPass : ScriptableRenderPass
{
    VoxelGIRendererFeature m_Feature;
    RenderTexture m_UavBilateralFilter;
    int m_LastWidth;
    int m_LastHeight;

    public BilateralFilterPass(VoxelGIRendererFeature feature)
    {
        m_Feature = feature;
        renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
    }

    // 确保RT存在且尺寸匹配，不匹配则重建
    void EnsureRT(int w, int h)
    {
        if (m_UavBilateralFilter != null && m_UavBilateralFilter.IsCreated() && m_LastWidth == w && m_LastHeight == h)
            return;

        ReleaseRT();
        m_UavBilateralFilter = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf);
        m_UavBilateralFilter.enableRandomWrite = true;
        m_UavBilateralFilter.Create();
        m_LastWidth = w;
        m_LastHeight = h;
    }

    void ReleaseRT()
    {
        if (m_UavBilateralFilter != null)
        {
            m_UavBilateralFilter.Release();
            if (Application.isPlaying)
                Object.Destroy(m_UavBilateralFilter);
            else
                Object.DestroyImmediate(m_UavBilateralFilter);
            m_UavBilateralFilter = null;
        }
    }

    public RenderTexture GetFilteredResult()
    {
        return m_UavBilateralFilter;
    }

    public void Cleanup()
    {
        ReleaseRT();
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (!m_Feature.BilateralFilterCfg.EnableBilateralFilter) return;
        if (m_Feature?.Resources?.VXGIComputeShader == null) return;

        VoxelGIProfiler.BeginSample("BilateralPass");

        Camera camera = renderingData.cameraData.camera;
        var res = m_Feature.Resources;
        var bCfg = m_Feature.BilateralFilterCfg;

        var temporalPass = FindTemporalFilterPass();
        RenderTexture inputRT;

        if (m_Feature.TemporalFilterCfg.EnableTemporalFilter && temporalPass != null)
        {
            inputRT = temporalPass.GetFilteredResult(camera);
        }
        else
        {
            var coneTracingPass = FindConeTracingPass();
            inputRT = coneTracingPass?.GetConeTraceResult();
        }

        if (inputRT == null) return;

        // 在Dispatch前确保输出RT有效
        EnsureRT(camera.pixelWidth, camera.pixelHeight);

        CommandBuffer cmd = CommandBufferPool.Get("VoxelGI/Bilateral");
        cmd.Clear();

        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdBilateralFiltering,
            ShaderIDs.WholeIndirectLight, inputRT);
        cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdBilateralFiltering,
            ShaderIDs.OutBilateralFilter, m_UavBilateralFilter);

        // URP在Compute Shader中无法直接绑定名为 "_CameraDepthTexture" 的RT给Texture2D对象
        // 因为URP管线内部管理它。必须通过CommandBuffer的全局属性或者明确获取它的RT标识
        // 但更安全的做法是：Shader里直接把 _CameraDepthTexture 声明为全局Texture2D，这里不需要再手动SetComputeTextureParam
        // cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdBilateralFiltering,
        //     ShaderIDs._CameraDepthTexture, ShaderIDs._CameraDepthTexture);
        // cmd.SetComputeTextureParam(res.VXGIComputeShader, res.ComputeKernelIdBilateralFiltering,
        //     ShaderIDs._CameraNormalsTexture, ShaderIDs._CameraNormalsTexture);

        cmd.SetComputeVectorParam(res.VXGIComputeShader, "ScreenResolution",
            new Vector4(camera.pixelWidth, camera.pixelHeight, 1.0f / camera.pixelWidth, 1.0f / camera.pixelHeight));
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "SampleRadius", bCfg.BilateralSamplerRadius);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "FarClip", camera.farClipPlane);
        cmd.SetComputeFloatParam(res.VXGIComputeShader, "NearClip", camera.nearClipPlane);
        cmd.SetComputeVectorParam(res.VXGIComputeShader, "BilaterialThreshold",
            new Vector4(bCfg.DepthThresholdLowerBound, bCfg.DepthThresholdUpperBound,
                bCfg.NormalThresholdLowerBound, bCfg.NormalThresholdUpperBound));

        cmd.DispatchCompute(res.VXGIComputeShader, res.ComputeKernelIdBilateralFiltering,
            camera.pixelWidth / 8 + 1, camera.pixelHeight / 8 + 1, 1);

        cmd.ClearRandomWriteTargets();

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        VoxelGIProfiler.EndSample("BilateralPass");
    }

    static class ShaderIDs
    {
        public static readonly int WholeIndirectLight = Shader.PropertyToID("WholeIndirectLight");
        public static readonly int OutBilateralFilter = Shader.PropertyToID("OutBilateralFilter");
        public static readonly int _CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
        public static readonly int _CameraNormalsTexture = Shader.PropertyToID("_CameraNormalsTexture");
    }

    ConeTracingPass FindConeTracingPass()
    {
        return m_Feature.GetConeTracingPass();
    }

    TemporalFilterPass FindTemporalFilterPass()
    {
        return m_Feature.GetTemporalFilterPass();
    }
}
