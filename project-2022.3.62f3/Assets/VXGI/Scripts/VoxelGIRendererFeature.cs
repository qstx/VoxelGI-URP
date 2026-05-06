using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class VoxelGIRendererFeature : ScriptableRendererFeature
{
    public enum IndirectConeTraceQuality
    {
        VeryLow = 0,
        Low = 1,
        Mid = 2,
        High = 3
    }

    [Serializable]
    public class VoxelizationConfig
    {
        public int ShadowMapResolution = 1024;
        public int VoxelTextureResolution = 256;
        public float VoxelSize = 0.1f;
        public int StableMipLevel = 6;
        public bool EnableConservativeRasterization = false;
        [Range(0.0f, 3.0f)] public float ConsevativeRasterizeScale = 1.5f;
        public LayerMask VoxelizationLayerMask = ~0;
    }

    [Serializable]
    public class DirectLightingConfig
    {
        [Range(0.0f, 10.0f)] public float LightIndensityMulti = 1.5f;
        [Range(0.0f, 10.0f)] public float EmissiveMulti = 1.5f;
        public float ShadowSunBias = 0.25f;
        public float ShadowNormalBias = 1f;
    }

    [Serializable]
    public class IndirectLightingConfig
    { 
        public bool EnableSecondBounce = true;
        public IndirectConeTraceQuality ConeTraceQuality = IndirectConeTraceQuality.VeryLow;
        [Range(1, 32)] public int IndirectLightingMaxStepNum = 12;
        [Range(1.0f, 10.0f)] public float IndirectLightingAlphaAtten = 2f;
        [Range(0.0f, 10.0f)] public float IndirectLightingScale = 2f;
        [Range(0.5f, 3f)] public float IndirectLightingFirstStep = 1f;
        [Range(1f, 3f)] public float IndirectLightingStepScale = 1f;
        [Range(20f, 150f)] public float IndirectLightingConeAngle = 120f;
        [Range(0, 5)] public int IndirectLightingMinMipLevel = 0;
    }

    [Serializable]
    public class ConeTracingConfig
    {
        public enum ScreenConeTraceQuality
        {
            VeryLow, // 1 Cone
            Low,     // 2 Cones
            Mid,     // 4 Cones
            High     // 8 Cones
        }
        
        public ScreenConeTraceQuality ConeTraceQuality = ScreenConeTraceQuality.High;
        [Range(1, 32)] public int ScreenMaxStepNum = 32;
        [Range(1.0f, 10.0f)] public float ScreenAlphaAtten = 5f;
        [Range(0.0f, 10.0f)] public float ScreenScale = 1f;
        [Range(0.5f, 3f)] public float ScreenFirstStep = 0.9f;
        [Range(1f, 3f)] public float ScreenStepScale = 1.2f;
        [Range(20f, 150f)] public float ScreenConeAngle = 120f;
    }

    public enum JitterMode
    {
        GoldenRatio = 0,
        Halton = 1
    }

    [Serializable]
    public class TemporalFilterConfig
    {
        public bool EnableTemporalFilter = true;
        public Texture2D BlueNoiseLUT;
        [Range(0f, 1f)] public float TemporalBlendAlpha = 0.005f;
        public float ClampAABBScale = 1.2f;
        public Vector2 BlueNoiseScale = new Vector2(1, 1);
        public JitterMode JitterMode = JitterMode.GoldenRatio;
        [Range(2, 64)] public int HaltonValueCount = 8;
    }

    [Serializable]
    public class BilateralFilterConfig
    {
        public bool EnableBilateralFilter = true;
        [Range(0f, 10f)] public float BilateralSamplerRadius = 6.0f;
        public float DepthThresholdLowerBound = 0.1f;
        public float DepthThresholdUpperBound = 0.2f;
        [Range(0f, 1f)] public float NormalThresholdLowerBound = 0.7f;
        [Range(0f, 1f)] public float NormalThresholdUpperBound = 1f;
    }

    [Serializable]
    public class DebugConfig
    {
        public bool DebugMode = false;
        public VoxelGIDebugType DebugType;
        [Range(0, 10)] public int DirectLightingDebugMipLevel = 0;
        [Range(0, 10)] public int IndirectLightingDebugMipLevel = 0;
        [Range(0.01f, 0.5f)] public float RayStepSize = 0.03f;
    }

    public VoxelizationConfig VoxelizationCfg = new VoxelizationConfig();
    public DirectLightingConfig DirectLightingCfg = new DirectLightingConfig();
    public IndirectLightingConfig IndirectLightingCfg = new IndirectLightingConfig();
    public ConeTracingConfig ConeTracingCfg = new ConeTracingConfig();
    public TemporalFilterConfig TemporalFilterCfg = new TemporalFilterConfig();
    public BilateralFilterConfig BilateralFilterCfg = new BilateralFilterConfig();
    public DebugConfig DebugCfg = new DebugConfig();

    public class VoxelGIResources
    {
        public Material GiMaterial;
        public ComputeShader VXGIComputeShader;
        public int ComputeKernelIdDirectLighting;
        public int ComputeKernelIdMipmap;
        public int ComputeKernelIdCopyTexture3D;
        public int ComputeKernelIdIndirectLighting;
        public int ComputeKernelIdBilateralFiltering;
        public int ComputeKernelIdOverwriteGBufferAlpha;

        public RenderTexture UavAlbedo;
        public RenderTexture UavNormal;
        public RenderTexture TmpOpacityAccum;
        public RenderTexture TmpEmissiveAccum;
        // 方案A：将Opacity并入Albedo.a，Emissive强度并入Normal.a，保留旧字段注释便于回退对比
        // public RenderTexture UavEmissive;
        // public RenderTexture UavOpacity;
        public RenderTexture UavDirectLighting;
        public RenderTexture UavIndirectLighting;
        public RenderTexture LightingPingPongRT;
        public RenderTexture ShadowDepth;
        public RenderTextureDescriptor GBufferDesc3D;
        public RenderTextureDescriptor ShadowDepthDesc;
        public RenderTextureDescriptor LightingDesc;

        public Mesh QuadMesh;

        // VoxelizationPass中计算的ShadowVP矩阵，供VoxelLightingComputePass使用
        public Matrix4x4 ShadowVPMatrix;

        public void Release()
        {
            void SafeRelease(RenderTexture rt)
            {
                if (rt != null)
                {
                    rt.Release();
                    if (Application.isPlaying)
                        UnityEngine.Object.Destroy(rt);
                    else
                        UnityEngine.Object.DestroyImmediate(rt);
                }
            }

            SafeRelease(UavAlbedo);
            SafeRelease(UavNormal);
            SafeRelease(TmpOpacityAccum);
            SafeRelease(TmpEmissiveAccum);
            // 方案A：旧版独立Emissive/Opacity纹理释放逻辑保留注释，便于回退
            // SafeRelease(UavEmissive);
            // SafeRelease(UavOpacity);
            SafeRelease(UavDirectLighting);
            SafeRelease(UavIndirectLighting);
            SafeRelease(LightingPingPongRT);
            SafeRelease(ShadowDepth);
        }
    }

    public class TemporalHistoryData
    {
        public RenderTexture RT0;
        public RenderTexture RT1;
        public bool PingPongFlag;
        public Matrix4x4 PreLocalToWorld;
        public int RandomOffsetIndex;
        public int ConeTraceCount;
        public bool NeedsClear = true;
    }

    public VoxelGIResources Resources;

    private VoxelizationPass m_VoxelizationPass;
    private VoxelLightingComputePass m_LightingPass;
    private ConeTracingPass m_ConeTracingPass;
    private TemporalFilterPass m_TemporalPass;
    private BilateralFilterPass m_BilateralPass;
    private CombinePass m_CombinePass;

    private Dictionary<Camera, TemporalHistoryData> m_TemporalHistory;

    static Mesh BuildQuadMesh()
    {
        var mesh = new Mesh();
        mesh.vertices = new Vector3[]
        {
            new Vector3(-1, -1, 0.5f),
            new Vector3(-1, 1, 0.5f),
            new Vector3(1, 1, 0.5f),
            new Vector3(-1, -1, 0.5f),
            new Vector3(1, 1, 0.5f),
            new Vector3(1, -1, 0.5f)
        };
        mesh.uv = new Vector2[]
        {
            new Vector2(0, 0),
            new Vector2(0, 1),
            new Vector2(1, 1),
            new Vector2(0, 0),
            new Vector2(1, 1),
            new Vector2(1, 0)
        };
        mesh.normals = new Vector3[]
        {
            new Vector3(0, 0, -1),
            new Vector3(0, 0, -1),
            new Vector3(0, 0, -1),
            new Vector3(0, 0, -1),
            new Vector3(0, 0, -1),
            new Vector3(0, 0, -1)
        };
        mesh.SetIndices(new int[] { 0, 1, 2, 3, 4, 5 }, MeshTopology.Triangles, 0);
        return mesh;
    }

    public override void Create()
    {
        Resources = new VoxelGIResources();
        m_TemporalHistory = new Dictionary<Camera, TemporalHistoryData>();

        var shader = Shader.Find("Hidden/VoxelGI_URP");
        if (shader == null)
        {
            Debug.LogError("VoxelGI: Failed to find Hidden/VoxelGI_URP shader");
            return;
        }
        Resources.GiMaterial = CoreUtils.CreateEngineMaterial(shader);

        var computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/VXGI/Shaders/VoxelGICompute.compute");
        if (computeShader == null)
        {
            Debug.LogError("VoxelGI: Failed to load VoxelGICompute compute shader");
            return;
        }
        Resources.VXGIComputeShader = computeShader;

        Resources.ComputeKernelIdDirectLighting = Resources.VXGIComputeShader.FindKernel("VoxelDirectLighting");
        Resources.ComputeKernelIdMipmap = Resources.VXGIComputeShader.FindKernel("MipmapGeneration");
        Resources.ComputeKernelIdCopyTexture3D = Resources.VXGIComputeShader.FindKernel("CopyTexture3D");
        Resources.ComputeKernelIdIndirectLighting = Resources.VXGIComputeShader.FindKernel("VoxelIndirectLighting");
        Resources.ComputeKernelIdBilateralFiltering = Resources.VXGIComputeShader.FindKernel("BilateralFiltering");
        Resources.ComputeKernelIdOverwriteGBufferAlpha = Resources.VXGIComputeShader.FindKernel("OverwriteGBufferAlpha");

        Resources.QuadMesh = BuildQuadMesh();

        BuildTextureDescriptors();
        Build3DTextures();
        BuildShadowDepth();

        m_VoxelizationPass = new VoxelizationPass(this);
        m_LightingPass = new VoxelLightingComputePass(this);
        m_ConeTracingPass = new ConeTracingPass(this);
        m_TemporalPass = new TemporalFilterPass(this);
        m_BilateralPass = new BilateralFilterPass(this);
        m_CombinePass = new CombinePass(this);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (Resources?.GiMaterial == null || Resources?.VXGIComputeShader == null)
            return;

        // 过滤Preview等非正常渲染相机，避免Compute Shader属性未绑定的报错
        var cameraType = renderingData.cameraData.cameraType;
        if (cameraType != CameraType.Game && cameraType != CameraType.SceneView)
            return;

        renderer.EnqueuePass(m_VoxelizationPass);
        renderer.EnqueuePass(m_LightingPass);
        renderer.EnqueuePass(m_ConeTracingPass);
        renderer.EnqueuePass(m_TemporalPass);
        renderer.EnqueuePass(m_BilateralPass);
        renderer.EnqueuePass(m_CombinePass);
    }

    public ConeTracingPass GetConeTracingPass() { return m_ConeTracingPass; }
    public TemporalFilterPass GetTemporalFilterPass() { return m_TemporalPass; }
    public BilateralFilterPass GetBilateralFilterPass() { return m_BilateralPass; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (Resources != null)
            {
                Resources.Release();
                if (Resources.GiMaterial != null)
                    CoreUtils.Destroy(Resources.GiMaterial);
                if (Resources.QuadMesh != null)
                    CoreUtils.Destroy(Resources.QuadMesh);
            }
            if (m_TemporalHistory != null)
            {
                foreach (var kvp in m_TemporalHistory)
                    ReleaseTemporalHistory(kvp.Value);
                m_TemporalHistory.Clear();
            }
            // 释放BilateralFilterPass持久化的RT
            m_BilateralPass?.Cleanup();
        }
        base.Dispose(disposing);
    }

    void BuildTextureDescriptors()
    {
        Resources.GBufferDesc3D = new RenderTextureDescriptor(VoxelizationCfg.VoxelTextureResolution, VoxelizationCfg.VoxelTextureResolution, RenderTextureFormat.RInt)
        {
            volumeDepth = VoxelizationCfg.VoxelTextureResolution,
            dimension = TextureDimension.Tex3D,
            enableRandomWrite = true,
            msaaSamples = 1
        };

        int mipLevel = (int)Mathf.Log(VoxelizationCfg.VoxelTextureResolution, 2) + 1;
        Resources.LightingDesc = new RenderTextureDescriptor(VoxelizationCfg.VoxelTextureResolution, VoxelizationCfg.VoxelTextureResolution, RenderTextureFormat.ARGBHalf)
        {
            volumeDepth = VoxelizationCfg.VoxelTextureResolution,
            dimension = TextureDimension.Tex3D,
            enableRandomWrite = true,
            msaaSamples = 1,
            useMipMap = true,
            mipCount = mipLevel
        };

        // 使用RFloat作为ShadowDepth的color格式，将深度值写入color target
        // 配合shader中BlendOp Max保留最大深度值（近=1白，远=0黑），完全不依赖硬件depth buffer
        Resources.ShadowDepthDesc = new RenderTextureDescriptor(VoxelizationCfg.ShadowMapResolution, VoxelizationCfg.ShadowMapResolution, RenderTextureFormat.RFloat)
        {
            depthBufferBits = 0,
            msaaSamples = 1,
            volumeDepth = 1,
            dimension = TextureDimension.Tex2D,
            sRGB = false
        };
    }

    void Build3DTextures()
    {
        Resources.UavAlbedo = CreateRT3D(Resources.GBufferDesc3D);
        Resources.UavNormal = CreateRT3D(Resources.GBufferDesc3D);
        Resources.TmpOpacityAccum = CreateRT3D(Resources.GBufferDesc3D);
        Resources.TmpEmissiveAccum = CreateRT3D(Resources.GBufferDesc3D);
        // 方案A：旧版独立Emissive/Opacity纹理创建逻辑保留注释，便于回退
        // Resources.UavEmissive = CreateRT3D(Resources.GBufferDesc3D);
        // Resources.UavOpacity = CreateRT3D(Resources.GBufferDesc3D);

        Resources.UavDirectLighting = CreateRT3D(Resources.LightingDesc);
        Resources.UavIndirectLighting = CreateRT3D(Resources.LightingDesc);
        Resources.LightingPingPongRT = CreateRT3D(Resources.LightingDesc);
    }

    void BuildShadowDepth()
    {
        Resources.ShadowDepth = new RenderTexture(Resources.ShadowDepthDesc);
        Resources.ShadowDepth.Create();
    }

    RenderTexture CreateRT3D(RenderTextureDescriptor desc)
    {
        var rt = new RenderTexture(desc);
        rt.Create();
        return rt;
    }

    public TemporalHistoryData GetOrCreateTemporalHistory(Camera camera)
    {
        if (!m_TemporalHistory.TryGetValue(camera, out var data))
        {
            data = new TemporalHistoryData();
            data.RT0 = RenderTexture.GetTemporary(camera.pixelWidth, camera.pixelHeight, 0, RenderTextureFormat.ARGBHalf);
            data.RT1 = RenderTexture.GetTemporary(camera.pixelWidth, camera.pixelHeight, 0, RenderTextureFormat.ARGBHalf);
            data.PingPongFlag = false;
            data.NeedsClear = true;
            data.RandomOffsetIndex = 0;
            data.ConeTraceCount = 0;
            m_TemporalHistory[camera] = data;
        }
        return data;
    }

    public void ReleaseTemporalHistory(TemporalHistoryData data)
    {
        if (data != null)
        {
            if (data.RT0 != null) RenderTexture.ReleaseTemporary(data.RT0);
            if (data.RT1 != null) RenderTexture.ReleaseTemporary(data.RT1);
        }
    }

    public TemporalHistoryData GetTemporalHistory(Camera camera)
    {
        m_TemporalHistory.TryGetValue(camera, out var data);
        return data;
    }

    // 旧版：VoxelSize * Resolution
    // public float VoxelizationRange
    // {
    //     get { return VoxelizationCfg.VoxelSize * VoxelizationCfg.VoxelTextureResolution; }
    // }

    /// <summary>
    /// 体素化区域边长。优先从场景中的VoxelGIVolume读取Scale，无Volume时回退到VoxelSize * Resolution。
    /// [已弃用] 请使用VoxelizationSize获取完整的xyz尺寸
    /// </summary>
    [System.Obsolete("请使用VoxelizationSize获取完整的xyz尺寸")]
    public float VoxelizationRange
    {
        get
        {
            var volume = VoxelGIVolume.Instance;
            if (volume != null)
                return volume.GetMaxRange();
            return VoxelizationCfg.VoxelSize * VoxelizationCfg.VoxelTextureResolution;
        }
    }

    /// <summary>
    /// 体素化区域尺寸（支持长方体）。优先从VoxelGIVolume读取，无Volume时回退到立方体。
    /// </summary>
    public Vector3 VoxelizationSize
    {
        get
        {
            var volume = VoxelGIVolume.Instance;
            if (volume != null)
                return volume.GetSize();
            float size = VoxelizationCfg.VoxelSize * VoxelizationCfg.VoxelTextureResolution;
            return Vector3.one * size;
        }
    }

    /// <summary>
    /// 实际体素大小 = 区域边长 / 纹理分辨率，由Volume的Scale自动决定。
    /// 对于长方体，取最大边长作为基准。
    /// </summary>
    public float VoxelSize
    {
        get
        {
            var size = VoxelizationSize;
            float maxSize = Mathf.Max(size.x, size.y, size.z);
            return maxSize / VoxelizationCfg.VoxelTextureResolution;
        }
    }

    // 自动计算ShadowMapRange：体素化区域在任意太阳角度下的最大投影半径 = 半对角线 + 外扩
    public float ShadowMapRange
    {
        get
        {
            var size = VoxelizationSize;
            float halfDiagonal = size.magnitude * 0.5f;
            return halfDiagonal;
        }
    }

    // 旧版：跟随相机 + snap到网格
    // public Vector3 GetOriginPos(Camera camera)
    // {
    //     float step = VoxelizationCfg.VoxelSize * Mathf.Pow(2, VoxelizationCfg.StableMipLevel);
    //     Vector3 fixedCameraPos = camera.transform.position / step;
    //     Vector3Int intPosition = new Vector3Int((int)fixedCameraPos.x, (int)fixedCameraPos.y, (int)fixedCameraPos.z);
    //     fixedCameraPos.x = intPosition.x * step;
    //     fixedCameraPos.y = intPosition.y * step;
    //     fixedCameraPos.z = intPosition.z * step;
    //     return fixedCameraPos;
    // }

    /// <summary>
    /// 从场景中的VoxelGIVolume获取体素化区域中心。
    /// 若场景中没有Volume则回退到相机位置。
    /// </summary>
    public Vector3 GetOriginPos(Camera camera)
    {
        var volume = VoxelGIVolume.Instance;
        if (volume != null)
        {
            return volume.GetCenter();
        }
        // 回退：没有Volume时仍使用相机位置（不再snap）
        return camera.transform.position;
    }

    public int MipLevel
    {
        get { return (int)Mathf.Log(VoxelizationCfg.VoxelTextureResolution, 2) + 1; }
    }

    public Light GetSunLight()
    {
        var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
        foreach (var l in lights)
        {
            if (l.type == LightType.Directional && l.isActiveAndEnabled)
                return l;
        }
        return null;
    }
}
