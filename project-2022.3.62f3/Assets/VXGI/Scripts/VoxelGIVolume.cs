using UnityEngine;
using Sirenix.OdinInspector;
#if UNITY_EDITOR
using UnityEditor;
using Unity.Collections;
using UnityEngine.Rendering;
#endif

/// <summary>
/// 在场景中放置此组件来定义体素化GI的作用区域。
/// Position = 区域中心，Scale = 区域大小（取最大分量作为立方体边长）。
/// </summary>
[ExecuteAlways]
public class VoxelGIVolume : MonoBehaviour
{
    // 单例引用，供管线内快速访问
    static VoxelGIVolume s_Instance;
    public static VoxelGIVolume Instance => s_Instance;

    void OnEnable()
    {
        s_Instance = this;
    }

    void OnDisable()
    {
        if (s_Instance == this)
            s_Instance = null;
    }

    /// <summary>
    /// 获取体素化区域中心（世界空间坐标）
    /// </summary>
    public Vector3 GetCenter()
    {
        return transform.position;
    }

    /// <summary>
    /// 获取体素化区域尺寸（支持长方体，返回xyz三轴的实际尺寸）
    /// </summary>
    public Vector3 GetSize()
    {
        return transform.lossyScale;
    }

    /// <summary>
    /// 获取体素化区域最大边长（用于某些需要单一数值的场合，如ShadowMapRange计算）
    /// </summary>
    public float GetMaxRange()
    {
        var s = transform.lossyScale;
        return Mathf.Max(s.x, s.y, s.z);
    }

    /// <summary>
    /// [已弃用] 请使用GetSize()获取完整尺寸，或GetMaxRange()获取最大边长
    /// </summary>
    [System.Obsolete("请使用GetSize()获取完整尺寸，或GetMaxRange()获取最大边长")]
    public float GetRange()
    {
        return GetMaxRange();
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Vector3 size = GetSize();
        // 选中时用半透明白色再画一次，方便调整位置和大小
        Gizmos.color = new Color(1, 1, 1, 0.15f);
        Gizmos.DrawCube(transform.position, size);
        Gizmos.color = new Color(1, 1, 1, 0.5f);
        Gizmos.DrawWireCube(transform.position, size);
    }

    void OnDrawGizmos()
    {
        // 1. 绘制绿色体素化区域线框（支持长方体）
        Vector3 size = GetSize();
        Gizmos.color = Color.green;
        Gizmos.DrawWireCube(transform.position, size);

        // 2. 绘制黄色阴影相机可视化
        var feature = FindFeature();
        if (feature == null) return;

        var sunLight = feature.GetSunLight();
        if (sunLight == null) return;

        Gizmos.color = Color.yellow;

        // 计算阴影相机参数(与VoxelizationPass中的计算逻辑一致)
        Vector3 origin = transform.position;
        float shadowRange = feature.ShadowMapRange;
        Vector3 shadowCamPos = origin - sunLight.transform.forward * shadowRange;
        Vector3 lightDir = sunLight.transform.forward;

        // 计算near/far(使用实际的长方体尺寸)
        Bounds voxelBounds = new Bounds(origin, size);
        (float near, float far) = FitNearFarToShadowCasters(voxelBounds, lightDir, shadowCamPos);

        // 2.1 黄色球体标记阴影相机位置
        Gizmos.DrawSphere(shadowCamPos, 0.5f);

        // 2.2 中心线和箭头
        Vector3 nearCenter = shadowCamPos + lightDir * near;
        Vector3 farCenter = shadowCamPos + lightDir * far;
        Gizmos.DrawLine(shadowCamPos, farCenter);

        // 绘制箭头(在far平面位置)
        Vector3 right = Vector3.Cross(lightDir, Vector3.up).normalized;
        Vector3 up = Vector3.Cross(right, lightDir).normalized;
        float arrowSize = shadowRange * 0.2f;
        Gizmos.DrawLine(farCenter, farCenter - lightDir * arrowSize + right * arrowSize * 0.5f);
        Gizmos.DrawLine(farCenter, farCenter - lightDir * arrowSize - right * arrowSize * 0.5f);
        Gizmos.DrawLine(farCenter, farCenter - lightDir * arrowSize + up * arrowSize * 0.5f);
        Gizmos.DrawLine(farCenter, farCenter - lightDir * arrowSize - up * arrowSize * 0.5f);

        // 2.3 正交视锥体线框
        Vector3[] nearCorners = new Vector3[4]
        {
            nearCenter + right * shadowRange + up * shadowRange,
            nearCenter - right * shadowRange + up * shadowRange,
            nearCenter - right * shadowRange - up * shadowRange,
            nearCenter + right * shadowRange - up * shadowRange
        };

        Vector3[] farCorners = new Vector3[4]
        {
            farCenter + right * shadowRange + up * shadowRange,
            farCenter - right * shadowRange + up * shadowRange,
            farCenter - right * shadowRange - up * shadowRange,
            farCenter + right * shadowRange - up * shadowRange
        };

        // 绘制near平面
        for (int i = 0; i < 4; i++)
        {
            Gizmos.DrawLine(nearCorners[i], nearCorners[(i + 1) % 4]);
        }

        // 绘制far平面
        for (int i = 0; i < 4; i++)
        {
            Gizmos.DrawLine(farCorners[i], farCorners[(i + 1) % 4]);
        }

        // 绘制连接线
        for (int i = 0; i < 4; i++)
        {
            Gizmos.DrawLine(nearCorners[i], farCorners[i]);
        }
    }

    // 计算near/far(与VoxelizationPass中的FitNearFarToShadowCasters逻辑一致)
    (float near, float far) FitNearFarToShadowCasters(Bounds bounds, Vector3 lightForward, Vector3 shadowCamPos)
    {
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

        float minDist = float.MaxValue;
        float maxDist = float.MinValue;

        foreach (var corner in corners)
        {
            Vector3 toCorner = corner - shadowCamPos;
            float dist = Vector3.Dot(toCorner, lightForward);
            minDist = Mathf.Min(minDist, dist);
            maxDist = Mathf.Max(maxDist, dist);
        }

        float margin = (maxDist - minDist) * 0.1f;
        minDist = minDist - margin;
        maxDist = maxDist + margin;

        if (minDist < 0.01f)
        {
            minDist = 0.01f;
        }

        return (minDist, maxDist);
    }

    /// <summary>
    /// 将体素化3D纹理（RInt格式，RGBA打包到uint32）保存为Texture3D asset，方便在Inspector中查看调试。
    /// </summary>
    [Button("保存体素化3D纹理到Assets"), PropertyOrder(10)]
    void SaveVoxelTextures()
    {
        var feature = FindFeature();
        if (feature == null)
        {
            Debug.LogError("[VoxelGIVolume] 找不到VoxelGIRendererFeature，无法保存");
            return;
        }

        var res = feature.Resources;
        if (res == null)
        {
            Debug.LogError("[VoxelGIVolume] RendererFeature.Resources为null，请确保管线已初始化");
            return;
        }

        string folder = "Assets/VoxelGI_Debug";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder("Assets", "VoxelGI_Debug");

        SaveRIntTexture3D(res.UavAlbedo, $"{folder}/VoxelAlbedo.asset", "Albedo");
        SaveRIntTexture3D(res.UavNormal, $"{folder}/VoxelNormal.asset", "Normal");
        // 方案A：旧版独立Emissive/Opacity纹理导出逻辑保留注释，便于回退
        // SaveRIntTexture3D(res.UavEmissive, $"{folder}/VoxelEmissive.asset", "Emissive");
        // SaveRIntTexture3D(res.UavOpacity, $"{folder}/VoxelOpacity.asset", "Opacity");

        AssetDatabase.Refresh();
        // 方案A：当前仅导出2张GBuffer（Albedo/Normal）
        Debug.Log($"[VoxelGIVolume] 2个体素化3D纹理已保存到 {folder}/");
    }

    VoxelGIRendererFeature FindFeature()
    {
        // 从当前URP asset中找到VoxelGIRendererFeature
        var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
        if (pipeline == null) return null;

        // 通过反射获取RendererData列表中的Feature
        var prop = typeof(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset)
            .GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (prop == null) return null;

        var dataList = prop.GetValue(pipeline) as UnityEngine.Rendering.Universal.ScriptableRendererData[];
        if (dataList == null) return null;

        foreach (var data in dataList)
        {
            if (data == null) continue;
            foreach (var f in data.rendererFeatures)
            {
                if (f is VoxelGIRendererFeature vf)
                    return vf;
            }
        }
        return null;
    }

    /// <summary>
    /// 读取RInt格式的3D RenderTexture，解包uint32为RGBA，保存为Texture3D asset
    /// </summary>
    void SaveRIntTexture3D(RenderTexture rt, string path, string label)
    {
        if (rt == null)
        {
            Debug.LogWarning($"[VoxelGIVolume] {label} 纹理为null，跳过");
            return;
        }

        int size = rt.width;
        // 使用AsyncGPUReadback同步等待，不指定格式让它使用RT的原生格式（RInt）
        var request = AsyncGPUReadback.Request(rt, 0);
        request.WaitForCompletion();

        if (request.hasError)
        {
            Debug.LogError($"[VoxelGIVolume] GPU Readback失败: {label}");
            return;
        }

        var rawData = request.GetData<int>();
        var tex = new Texture3D(size, size, size, TextureFormat.RGBA32, false);
        var colors = new Color32[size * size * size];

        for (int i = 0; i < rawData.Length; i++)
        {
            uint packed = (uint)rawData[i];
            // EncodeGbuffer: x<<24 | y<<16 | z<<8 | w
            byte r = (byte)((packed >> 24) & 0xFF);
            byte g = (byte)((packed >> 16) & 0xFF);
            byte b = (byte)((packed >> 8) & 0xFF);
            byte a = (byte)(packed & 0xFF);
            colors[i] = new Color32(r, g, b, a);
        }

        tex.SetPixels32(colors);
        tex.Apply();

        // 保存或覆盖asset
        var existing = AssetDatabase.LoadAssetAtPath<Texture3D>(path);
        if (existing != null)
            AssetDatabase.DeleteAsset(path);

        AssetDatabase.CreateAsset(tex, path);
        Debug.Log($"[VoxelGIVolume] {label}: 保存成功 ({size}x{size}x{size}) → {path}");
    }
#endif
}
