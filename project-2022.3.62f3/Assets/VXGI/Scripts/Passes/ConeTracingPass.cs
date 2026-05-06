using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class ConeTracingPass : ScriptableRenderPass
{
    static class ShaderIDs
    {
        public static readonly int ScreenMaxMipLevel = Shader.PropertyToID("ScreenMaxMipLevel");
        public static readonly int ScreenMaxStepNum = Shader.PropertyToID("ScreenMaxStepNum");
        public static readonly int ScreenAlphaAtten = Shader.PropertyToID("ScreenAlphaAtten");
        public static readonly int ScreenScale = Shader.PropertyToID("ScreenScale");
        public static readonly int ScreenConeAngle = Shader.PropertyToID("ScreenConeAngle");
        public static readonly int ScreenFirstStep = Shader.PropertyToID("ScreenFirstStep");
        public static readonly int ScreenStepScale = Shader.PropertyToID("ScreenStepScale");
        public static readonly int ScreenConeTraceLighting = Shader.PropertyToID("ScreenConeTraceLighting");
        public static readonly int EnableTemporalFilter = Shader.PropertyToID("EnableTemporalFilter");
        public static readonly int ConeTraceQuality = Shader.PropertyToID("ConeTraceQuality");
        public static readonly int ScreenResolution = Shader.PropertyToID("ScreenResolution");
        public static readonly int BlueNoiseResolution = Shader.PropertyToID("BlueNoiseResolution");
        public static readonly int NoiseLUT = Shader.PropertyToID("NoiseLUT");
        public static readonly int BlueNoiseScale = Shader.PropertyToID("BlueNoiseScale");
        public static readonly int RandomUV = Shader.PropertyToID("RandomUV");
    }

    VoxelGIRendererFeature m_Feature;
    RenderTexture m_ConeTracingRT;

    public ConeTracingPass(VoxelGIRendererFeature feature)
    {
        m_Feature = feature;
        renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);

        int w = renderingData.cameraData.camera.pixelWidth;
        int h = renderingData.cameraData.camera.pixelHeight;
        m_ConeTracingRT = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
    }

    public override void OnCameraCleanup(CommandBuffer cmd)
    {
        if (m_ConeTracingRT != null)
        {
            RenderTexture.ReleaseTemporary(m_ConeTracingRT);
            m_ConeTracingRT = null;
        }
    }

    public RenderTexture GetConeTraceResult()
    {
        return m_ConeTracingRT;
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (m_Feature?.Resources?.GiMaterial == null || m_ConeTracingRT == null) return;

        VoxelGIProfiler.BeginSample("ConeTracingPass");

        var res = m_Feature.Resources;
        var ctCfg = m_Feature.ConeTracingCfg;
        var tCfg = m_Feature.TemporalFilterCfg;
        var iCfg = m_Feature.IndirectLightingCfg;
        Camera camera = renderingData.cameraData.camera;

        CommandBuffer cmd = CommandBufferPool.Get("VoxelGI/ScreenTrace");
        cmd.Clear();

        cmd.SetGlobalFloat(ShaderIDs.ScreenMaxMipLevel, m_Feature.MipLevel);
        cmd.SetGlobalInt(ShaderIDs.ScreenMaxStepNum, ctCfg.ScreenMaxStepNum);
        cmd.SetGlobalFloat(ShaderIDs.ScreenAlphaAtten, ctCfg.ScreenAlphaAtten);
        cmd.SetGlobalFloat(ShaderIDs.ScreenScale, ctCfg.ScreenScale);
        cmd.SetGlobalFloat(ShaderIDs.ScreenConeAngle, ctCfg.ScreenConeAngle);
        cmd.SetGlobalFloat(ShaderIDs.ScreenFirstStep, ctCfg.ScreenFirstStep);
        cmd.SetGlobalFloat(ShaderIDs.ScreenStepScale, ctCfg.ScreenStepScale);

        if (iCfg.EnableSecondBounce)
            cmd.SetGlobalTexture(ShaderIDs.ScreenConeTraceLighting, res.UavIndirectLighting);
        else
            cmd.SetGlobalTexture(ShaderIDs.ScreenConeTraceLighting, res.UavDirectLighting);

        cmd.SetGlobalInt(ShaderIDs.EnableTemporalFilter, tCfg.EnableTemporalFilter ? 1 : 0);

        // 传递档位给 Shader
        cmd.SetGlobalInt(ShaderIDs.ConeTraceQuality, (int)ctCfg.ConeTraceQuality);

        Vector4 screenRes = new Vector4(camera.pixelWidth, camera.pixelHeight,
            1.0f / camera.pixelWidth, 1.0f / camera.pixelHeight);
        cmd.SetGlobalVector(ShaderIDs.ScreenResolution, screenRes);

        if (tCfg.BlueNoiseLUT != null)
        {
            Vector4 blueNoiseRes = new Vector4(tCfg.BlueNoiseLUT.width, tCfg.BlueNoiseLUT.height,
                1.0f / tCfg.BlueNoiseLUT.width, 1.0f / tCfg.BlueNoiseLUT.height);
            cmd.SetGlobalVector(ShaderIDs.BlueNoiseResolution, blueNoiseRes);
            cmd.SetGlobalTexture(ShaderIDs.NoiseLUT, tCfg.BlueNoiseLUT);
        }

        cmd.SetGlobalVector(ShaderIDs.BlueNoiseScale,
            new Vector4(tCfg.BlueNoiseScale.x, tCfg.BlueNoiseScale.y,
                1f / tCfg.BlueNoiseScale.x, 1f / tCfg.BlueNoiseScale.y));

        // 使用黄金比例（1.61803398875... 的小数部分）生成无尽低差异序列，替代容易产生共振的 Halton 循环
        const float GoldenRatioConjugate = 0.618033988749895f;

        var history = m_Feature.GetOrCreateTemporalHistory(camera);
        float jitterX, jitterY;

        if (tCfg.JitterMode == VoxelGIRendererFeature.JitterMode.Halton)
        {
            jitterX = GetHaltonValue(history.ConeTraceCount, 2);
            jitterY = GetHaltonValue(history.ConeTraceCount, 3);
        }
        else
        {
            jitterX = (history.RandomOffsetIndex * GoldenRatioConjugate) % 1.0f;
            jitterY = (history.RandomOffsetIndex * GoldenRatioConjugate * GoldenRatioConjugate) % 1.0f;
        }

        history.RandomOffsetIndex = (history.RandomOffsetIndex + 1) % 10000;
        history.ConeTraceCount = (history.ConeTraceCount + 1) % Mathf.Max(1, tCfg.HaltonValueCount);
        
        cmd.SetGlobalVector(ShaderIDs.RandomUV, new Vector4(jitterX, jitterY, 0, 0));
        // // 传递反向VP矩阵用于计算世界坐标 (使用URP自带宏不需要这个了)
        // var gpuProj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
        // var viewProj = gpuProj * camera.worldToCameraMatrix;
        // cmd.SetGlobalMatrix(ShaderIDs.CameraInvViewProj, viewProj.inverse);

        cmd.SetRenderTarget(m_ConeTracingRT);
        cmd.ClearRenderTarget(true, true, Color.black);
        cmd.DrawMesh(res.QuadMesh, Matrix4x4.identity, res.GiMaterial, 0, 2);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        VoxelGIProfiler.EndSample("ConeTracingPass");
    }

    float GetHaltonValue(int index, int radix)
    {
        float result = 0f;
        float fraction = 1f / radix;
        while (index > 0)
        {
            result += (index % radix) * fraction;
            index /= radix;
            fraction /= radix;
        }
        return result;
    }
}
