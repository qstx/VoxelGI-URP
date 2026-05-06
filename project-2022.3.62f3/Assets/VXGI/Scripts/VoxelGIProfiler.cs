using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

/// <summary>
/// VoxelGI性能分析工具，统计各Pass的CPU耗时
/// 
/// 使用方法：
/// 1. 性能监控默认启用，每60帧自动输出统计报告到Console
/// 2. 调整输出间隔：VoxelGIProfiler.ReportInterval = 120; // 改为120帧输出一次
/// 3. 手动输出报告：VoxelGIProfiler.LogReport();
/// 4. 禁用监控：VoxelGIProfiler.Enabled = false;
/// 5. 重置统计数据：VoxelGIProfiler.Reset();
/// 
/// 注意：此工具测量的是CPU端耗时（包括FindObjectsByType、GetComponent、CommandBuffer构建等），
///      不包括GPU执行时间。要测量GPU耗时请使用Unity Profiler的GPU模块或FrameDebugger。
/// </summary>
public static class VoxelGIProfiler
{
    class TimingData
    {
        public long TotalTicks;
        public int SampleCount;
        public double AverageMs => SampleCount > 0 ? (TotalTicks / (double)SampleCount) / 10000.0 : 0;
        public double LastMs;
    }

    static readonly Dictionary<string, TimingData> s_Timings = new Dictionary<string, TimingData>();
    static readonly Dictionary<string, Stopwatch> s_ActiveTimers = new Dictionary<string, Stopwatch>();
    static int s_FrameCount = 0;
    static int s_ReportInterval = 60; // 每60帧输出一次统计
    static bool s_Enabled = true;

    public static bool Enabled
    {
        get { return s_Enabled; }
        set { s_Enabled = value; }
    }

    public static int ReportInterval
    {
        get { return s_ReportInterval; }
        set { s_ReportInterval = Mathf.Max(1, value); }
    }

    /// <summary>
    /// 开始计时
    /// </summary>
    public static void BeginSample(string name)
    {
        if (!s_Enabled) return;

        if (!s_ActiveTimers.TryGetValue(name, out var sw))
        {
            sw = new Stopwatch();
            s_ActiveTimers[name] = sw;
        }
        sw.Restart();
    }

    /// <summary>
    /// 结束计时
    /// </summary>
    public static void EndSample(string name)
    {
        if (!s_Enabled) return;

        if (s_ActiveTimers.TryGetValue(name, out var sw))
        {
            sw.Stop();

            if (!s_Timings.TryGetValue(name, out var data))
            {
                data = new TimingData();
                s_Timings[name] = data;
            }

            data.TotalTicks += sw.ElapsedTicks;
            data.SampleCount++;
            data.LastMs = sw.ElapsedTicks / 10000.0;
        }
    }

    /// <summary>
    /// 每帧调用，自动输出统计报告
    /// </summary>
    public static void OnFrameEnd()
    {
        if (!s_Enabled) return;

        s_FrameCount++;
        if (s_FrameCount >= s_ReportInterval)
        {
            LogReport();
            Reset();
        }
    }

    /// <summary>
    /// 输出统计报告
    /// </summary>
    public static void LogReport()
    {
        if (s_Timings.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine($"=== VoxelGI性能统计 (采样{s_FrameCount}帧) ===");

        // 按层级组织数据
        var voxelizationTotal = 0.0;
        var lightingTotal = 0.0;
        var otherTotal = 0.0;

        sb.AppendLine("【Voxelization Pass】");
        if (s_Timings.TryGetValue("VoxelizationPass", out var voxPass))
        {
            voxelizationTotal = voxPass.AverageMs;
            sb.AppendLine($"  总耗时: {voxPass.AverageMs:F3}ms (最后一帧: {voxPass.LastMs:F3}ms)");
        }
        if (s_Timings.TryGetValue("  ShadowMap", out var shadowData))
        {
            sb.AppendLine($"    ├─ ShadowMap: {shadowData.AverageMs:F3}ms");
            if (s_Timings.TryGetValue("    ComputeShadowBounds", out var computeBounds))
                sb.AppendLine($"    │    ├─ 计算包围盒: {computeBounds.AverageMs:F3}ms");
            if (s_Timings.TryGetValue("    DrawRenderers_Shadow", out var drawShadow))
                sb.AppendLine($"    │    └─ DrawRenderers: {drawShadow.AverageMs:F3}ms");
        }
        if (s_Timings.TryGetValue("  Voxelize", out var voxelData))
        {
            sb.AppendLine($"    └─ Voxelize: {voxelData.AverageMs:F3}ms");
            if (s_Timings.TryGetValue("    FindObjects_Voxel", out var findVoxel))
                sb.AppendLine($"         ├─ 查找物体: {findVoxel.AverageMs:F3}ms");
            if (s_Timings.TryGetValue("    DrawCalls_Voxel", out var drawVoxel))
                sb.AppendLine($"         └─ 绘制调用: {drawVoxel.AverageMs:F3}ms (平均{drawVoxel.SampleCount / (double)s_FrameCount:F1}次/帧)");
        }

        sb.AppendLine("\n【Lighting Pass】");
        if (s_Timings.TryGetValue("LightingPass", out var lightPass))
        {
            lightingTotal = lightPass.AverageMs;
            sb.AppendLine($"  总耗时: {lightPass.AverageMs:F3}ms (最后一帧: {lightPass.LastMs:F3}ms)");
        }
        if (s_Timings.TryGetValue("  DirectLighting", out var directData))
            sb.AppendLine($"    ├─ Direct: {directData.AverageMs:F3}ms");
        if (s_Timings.TryGetValue("  IndirectLighting", out var indirectData))
            sb.AppendLine($"    └─ Indirect: {indirectData.AverageMs:F3}ms");

        sb.AppendLine("\n【其他Pass】");
        if (s_Timings.TryGetValue("ConeTracingPass", out var coneData))
        {
            otherTotal += coneData.AverageMs;
            sb.AppendLine($"  ScreenTrace: {coneData.AverageMs:F3}ms");
        }
        if (s_Timings.TryGetValue("TemporalPass", out var tempData))
        {
            otherTotal += tempData.AverageMs;
            sb.AppendLine($"  Temporal: {tempData.AverageMs:F3}ms");
        }
        if (s_Timings.TryGetValue("BilateralPass", out var bilData))
        {
            otherTotal += bilData.AverageMs;
            sb.AppendLine($"  Bilateral: {bilData.AverageMs:F3}ms");
        }
        if (s_Timings.TryGetValue("CombinePass", out var combData))
        {
            otherTotal += combData.AverageMs;
            sb.AppendLine($"  Combine: {combData.AverageMs:F3}ms");
        }

        var total = voxelizationTotal + lightingTotal + otherTotal;
        sb.AppendLine($"\n【总计】 {total:F3}ms/帧 ({1000.0 / total:F1} FPS理论上限)");
        sb.AppendLine("================================================");

        UnityEngine.Debug.Log(sb.ToString());
    }

    /// <summary>
    /// 重置统计数据
    /// </summary>
    public static void Reset()
    {
        s_Timings.Clear();
        s_FrameCount = 0;
    }
}
