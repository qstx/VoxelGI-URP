using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class TemporalFilterPass : ScriptableRenderPass
{
    private VoxelGIRendererFeature m_Feature;
    private bool m_Executed;

    public TemporalFilterPass(VoxelGIRendererFeature feature)
    {
        m_Feature = feature;
        renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        ConfigureInput(ScriptableRenderPassInput.Motion);
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (!m_Feature.TemporalFilterCfg.EnableTemporalFilter) return;
        if (m_Feature?.Resources?.GiMaterial == null) return;

        VoxelGIProfiler.BeginSample("TemporalPass");

        Camera camera = renderingData.cameraData.camera;
        var history = m_Feature.GetOrCreateTemporalHistory(camera);
        var coneTracingPass = FindConeTracingPass();

        if (coneTracingPass == null) return;

        RenderTexture coneResult = coneTracingPass.GetConeTraceResult();
        if (coneResult == null) return;

        CommandBuffer cmd = CommandBufferPool.Get("VoxelGI/Temporal");
        cmd.Clear();

        cmd.SetGlobalFloat(Shader.PropertyToID("BlendAlpha"), m_Feature.TemporalFilterCfg.TemporalBlendAlpha);
        cmd.SetGlobalFloat(Shader.PropertyToID("TemporalClampAABBScale"), m_Feature.TemporalFilterCfg.ClampAABBScale);
        cmd.SetGlobalTexture(Shader.PropertyToID("CurrentScreenIrradiance"), coneResult);

        RenderTexture src, dst;
        if (history.PingPongFlag)
        {
            src = history.RT0;
            dst = history.RT1;
        }
        else
        {
            src = history.RT1;
            dst = history.RT0;
        }

        if (history.NeedsClear)
        {
            cmd.SetRenderTarget(src);
            cmd.ClearRenderTarget(false, true, Color.black);
            cmd.SetRenderTarget(dst);
            cmd.ClearRenderTarget(false, true, Color.black);
            history.NeedsClear = false;
        }

        cmd.SetGlobalTexture(Shader.PropertyToID("HistoricalScreenIrradiance"), src);
        cmd.SetRenderTarget(dst);
        cmd.DrawMesh(m_Feature.Resources.QuadMesh, Matrix4x4.identity, m_Feature.Resources.GiMaterial, 0, 3);

        history.PingPongFlag = !history.PingPongFlag;
        m_Executed = true;

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        VoxelGIProfiler.EndSample("TemporalPass");
    }

    public RenderTexture GetFilteredResult(Camera camera)
    {
        if (!m_Feature.TemporalFilterCfg.EnableTemporalFilter || !m_Executed)
        {
            var coneTracingPass = FindConeTracingPass();
            return coneTracingPass?.GetConeTraceResult();
        }
        var history = m_Feature.GetTemporalHistory(camera);
        if (history == null) return null;
        return history.PingPongFlag ? history.RT0 : history.RT1;
    }

    ConeTracingPass FindConeTracingPass()
    {
        return m_Feature.GetConeTracingPass();
    }
}
