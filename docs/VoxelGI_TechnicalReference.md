# VoxelGI 技术参考文档

## 一、概述

基于体素的全局光照（Voxel-based Global Illumination）方案，运行在 URP 14（Unity 2022.3）上。通过体素化场景写入 3D GBuffer，Compute Shader 计算直接/间接光照，屏幕空间 Cone Tracing 采样 3D 光照纹理，最终与直接光合成输出。

**优化状态（当前）**：
- Voxelization 阶段已完成 DrawRenderers 优化（0.135ms → 0.03ms）。
- 3D GBuffer 已由 4 张收敛为 2 张主纹理（`UavAlbedo`、`UavNormal`），并新增临时累积纹理与回填 Pass。
- Lighting 资源命名已统一为 `UavDirectLighting` / `UavIndirectLighting`。
- 间接光 Cone 数量已开放面板配置（VeryLow/Low/Mid/High）。

---

## 二、项目文件结构

```
Assets/VXGI/
├── Scripts/
│   ├── VoxelGIRendererFeature.cs    # 主入口：RendererFeature，包含所有配置和资源管理
│   ├── VoxelGIVolume.cs             # 场景中的体素化区域定义组件（单例）
│   ├── VoxelGIProfiler.cs           # CPU性能统计工具
│   └── Passes/
│       ├── VoxelizationPass.cs      # Pass 1: 体素化 + 阴影图
│       ├── VoxelLightingComputePass.cs  # Pass 2: 直接光照 + Mipmap + 间接光照
│       ├── ConeTracingPass.cs       # Pass 3: 屏幕空间 Cone Tracing
│       ├── TemporalFilterPass.cs    # Pass 4: 时间性滤波
│       ├── BilateralFilterPass.cs   # Pass 5: 双边滤波降噪
│       └── CombinePass.cs           # Pass 6: 合成最终图像
└── Shaders/
    ├── VoxelGI_URP.shader           # Hidden shader，GI管线内部使用（主用于ConeTracing/Temporal/Combine/Debug）
    ├── Lit.shader                   # 场景物体材质shader（含 Forward + Voxelization + VoxelGI_Shadow + Depth相关Pass）
    ├── VoxelGICompute.compute       # 主Compute Shader（6个Kernel）
    └── VXGIBlocker.shader           # 遮挡体shader（屏幕不可见 + 仅参与VoxelGI_Shadow）
```

---

## 三、管线执行流程

所有 Pass 的 `renderPassEvent = RenderPassEvent.AfterRenderingOpaques`，按 `AddRenderPasses` 中 `EnqueuePass` 的顺序执行：

```
1. VoxelizationPass         → 体素化场景 + 渲染阴影图
2. VoxelLightingComputePass → Compute: 直接光照 + Mipmap + 间接光照 + Mipmap
3. ConeTracingPass          → 屏幕空间 Cone Tracing
4. TemporalFilterPass       → 时间累积滤波
5. BilateralFilterPass      → 双边滤波降噪
6. CombinePass              → 合成间接光与直接光到最终画面
```

---

## 四、资源定义

### 3D 纹理（体素空间，默认 256³）

| 资源名 | 格式 | Mip | 用途 |
|--------|------|-----|------|
| `UavAlbedo` | RInt | 无 | 体素化 Albedo GBuffer（原子操作移动平均） |
| `UavNormal` | RInt | 无 | 体素化 Normal GBuffer |
| `TmpOpacityAccum` | RInt | 无 | 体素化阶段临时累积 Opacity（仅回填阶段使用） |
| `TmpEmissiveAccum` | RInt | 无 | 体素化阶段临时累积 EmissiveIntensity（仅回填阶段使用） |
| `UavDirectLighting` | ARGBHalf | 有 | 直接光照结果 |
| `UavIndirectLighting` | ARGBHalf | 有 | 间接光照结果（二次反弹） |
| `LightingPingPongRT` | ARGBHalf | 有 | Mipmap 生成中间缓冲 |

### 2D 纹理

| 资源名 | 格式 | 用途 |
|--------|------|------|
| `ShadowDepth` | RFloat (1024²) | 阴影深度图（BlendOp Max） |
| `ConeTracingRT` | ARGBHalf (全分辨率) | Cone Tracing 屏幕输出 |
| `TemporalHistory PingPong` | ARGBHalf (全分辨率) | 时间滤波历史帧 |
| `BilateralFilterRT` | ARGBHalf (全分辨率,UAV) | 双边滤波输出 |

### 其他

| 资源名 | 用途 |
|--------|------|
| `QuadMesh` | 全屏四边形 Mesh |
| `GiMaterial` | VoxelGI_URP.shader 的材质实例 |
| `ComputeShader` | VoxelGICompute.compute |

---

## 五、各 Pass 详细说明

### Pass 1: VoxelizationPass

**文件**: `Assets/VXGI/Scripts/Passes/VoxelizationPass.cs`

#### 阶段 A: RenderShadowMap

| 项目 | 内容 |
|------|------|
| **输入** | 场景不透明物体（DrawRenderers） |
| **ShaderTag** | `"VoxelGI_Shadow"`（物体使用自身材质对应Pass，不 override） |
| **输出** | `ShadowDepth` (RFloat 2D), `ShadowVPMatrix` |
| **特殊** | BlendOp Max 保留最大深度；正交投影基于体素化区域 Bounds 自动计算 near/far |

#### 阶段 B: RenderVoxel

| 项目 | 内容 |
|------|------|
| **输入** | 场景不透明物体（DrawRenderers） |
| **ShaderTag** | `"VoxelGI_Voxelization"`（物体使用自己的 VoxelGI/Lit 材质，不 override） |
| **Layer过滤** | 通过 `VoxelizationLayerMask` 仅体素化指定 Layer |
| **输出** | `UavAlbedo`(u1), `UavNormal`(u2), `TmpOpacityAccum`(u3), `TmpEmissiveAccum`(u4) |
| **UAV 绑定** | `SetRandomWriteTarget(1~4, ...)` |
| **算法** | Geometry Shader 三轴投影 + 保守光栅化 + InterlockedMax 原子写入 |
| **矩阵** | VoxelizationForwardVP / RightVP / UpVP（三轴正交投影） |

#### 阶段 C: OverwritePackedAlpha（新增）

| 项目 | 内容 |
|------|------|
| **Kernel** | `OverwriteGBufferAlpha` (8,8,8) |
| **输入** | `UavAlbedo`, `UavNormal`, `TmpOpacityAccum`, `TmpEmissiveAccum` |
| **输出** | 回填主 GBuffer：`UavAlbedo.a = Opacity`，`UavNormal.a = EmissiveIntensity(0~1)` |
| **说明** | 保持 `MovingAverage` 的权重语义正确，同时把业务通道写回最终 `a` 通道 |

---

### Pass 2: VoxelLightingComputePass

**文件**: `Assets/VXGI/Scripts/Passes/VoxelLightingComputePass.cs`

#### 阶段 A: ComputeDirectLighting

| 项目 | 内容 |
|------|------|
| **Kernel** | `VoxelDirectLighting` (numthreads 4,4,4) |
| **输入** | `UavAlbedo`, `UavNormal`, `ShadowDepth`, `ShadowVPMatrix`, SunLight 参数 |
| **输出** | `UavDirectLighting` (mip 0) |
| **Dispatch** | (voxelRes/4+1)³ |
| **后处理** | GenerateMipChain → `UavDirectLighting` 完整 mip chain |
| **通道语义** | `albedo.a` 作为 opacity，`normal.a` 解压为 emissiveIntensity（0~5）并与 albedo 组合发光色 |

#### 阶段 B: ComputeIndirectLighting（EnableSecondBounce 时执行）

| 项目 | 内容 |
|------|------|
| **Kernel** | `VoxelIndirectLighting` (numthreads 8,8,8) |
| **输入** | `UavAlbedo`, `UavNormal`, `VoxelLighting`(=`UavDirectLighting` 带 mip) |
| **输出** | `UavIndirectLighting` (mip 0) |
| **Dispatch** | (voxelRes/8+1)³ |
| **算法** | Fibonacci 球面分布 + 半球 Cone Tracing 采样直接光照 3D 纹理 |
| **后处理** | GenerateMipChain → `UavIndirectLighting` 完整 mip chain |
| **质量档位** | `ConeTraceQuality`（VeryLow=1, Low=4, Mid=8, High=16） |
| **能量处理** | 多 cone 累积后按权重归一，避免 cone 数增加导致整体偏亮 |

#### Mipmap 生成子步骤

| Kernel | 说明 |
|--------|------|
| `MipmapGeneration` (8,8,8) | 从源 mip 2×2×2 平均降采样到 PingPong RT |
| `CopyTexture3D` (8,8,8) | 将 PingPong RT 拷贝回目标纹理的对应 mip level |

---

### Pass 3: ConeTracingPass

**文件**: `Assets/VXGI/Scripts/Passes/ConeTracingPass.cs`

| 项目 | 内容 |
|------|------|
| **输入** | `ScreenConeTraceLighting`(=`UavIndirectLighting` 或 `UavDirectLighting`), `_CameraDepthTexture`, `_CameraNormalsTexture`, BlueNoiseLUT |
| **输出** | `m_ConeTracingRT` (ARGBHalf 2D) |
| **Shader Pass** | GiMaterial pass index **2**（Name: "ConeTracing"） |
| **ConfigureInput** | `ScriptableRenderPassInput.Depth \| ScriptableRenderPassInput.Normal` |
| **算法** | 从屏幕像素重建世界坐标，沿法线方向在 3D 光照纹理中 Cone Tracing 采样 |
| **抖动** | 黄金比例低差异序列 + 蓝噪声，配合 Temporal Filter 使用 |

**注意**: URP 14 中 DepthNormals prepass 生成的纹理名为 `_CameraNormalsTexture`（R8G8B8A8_SNorm，view space 法线直接存储），**不是** Built-in 管线的 `_CameraDepthNormalsTexture`。

---

### Pass 4: TemporalFilterPass

**文件**: `Assets/VXGI/Scripts/Passes/TemporalFilterPass.cs`

| 项目 | 内容 |
|------|------|
| **输入** | `CurrentScreenIrradiance`(=ConeTracingRT), `HistoricalScreenIrradiance`(PingPong 历史帧), `_CameraMotionVectorsTexture` |
| **输出** | PingPong RT0/RT1 交替写入 (ARGBHalf 2D) |
| **Shader Pass** | GiMaterial pass index **3**（Name: "TemporalFilter"） |
| **ConfigureInput** | `ScriptableRenderPassInput.Motion` |
| **算法** | Motion Vector 重投影 + YCoCg AABB Clamp + 指数混合 |
| **参数** | BlendAlpha=0.005, ClampAABBScale=1.2 |

---

### Pass 5: BilateralFilterPass

**文件**: `Assets/VXGI/Scripts/Passes/BilateralFilterPass.cs`

| 项目 | 内容 |
|------|------|
| **Kernel** | `BilateralFiltering` (numthreads 8,8,1) |
| **输入** | `WholeIndirectLight`(Temporal 输出或 ConeTracing 输出), `_CameraDepthTexture`, `_CameraNormalsTexture` |
| **输出** | `m_UavBilateralFilter` (ARGBHalf 2D, enableRandomWrite) |
| **Dispatch** | (width/8+1, height/8+1, 1) |
| **算法** | Poisson Disk 采样 + 深度/法线加权双边滤波 |

---

### Pass 6: CombinePass

**文件**: `Assets/VXGI/Scripts/Passes/CombinePass.cs`

#### 正常模式

| 项目 | 内容 |
|------|------|
| **输入** | `SceneDirect`(camera color target 副本), `VXGIIndirect`(最终间接光结果) |
| **输出** | 写入 camera color target |
| **Shader Pass** | GiMaterial pass index **4**（Name: "Combine"） |
| **逻辑** | 先 Blit colorTarget 到临时 RT 避免读写反馈，然后合成 |

#### 调试模式

| 项目 | 内容 |
|------|------|
| **Shader Pass** | GiMaterial pass index **5**（Name: "VoxelVisualization"） |
| **算法** | Ray Marching 穿过 3D 纹理可视化各通道 |

---

## 六、Hidden/VoxelGI_URP Shader Pass 索引

**文件**: `Assets/VXGI/Shaders/VoxelGI_URP.shader`

| Index | Name | LightMode | 用途 |
|:-----:|------|-----------|------|
| 0 | Voxelization | VoxelGI_Voxelization | 已废弃空壳（保留以避免PassIndex变化） |
| 1 | VoxelShadow | VoxelGI_Shadow | 已废弃空壳（已迁移到 Lit.shader） |
| 2 | ConeTracing | - | 屏幕空间 Cone Tracing |
| 3 | TemporalFilter | - | 时间滤波 |
| 4 | Combine | - | 最终合成 |
| 5 | VoxelVisualization | - | 调试可视化 |

---

## 七、VoxelGI/Lit 材质 Shader

**文件**: `Assets/VXGI/Shaders/Lit.shader`

场景物体使用的用户材质 shader，包含 6 个 Pass（并将 Voxelization/Shadow 的实现统一维护在同一份 `HLSLINCLUDE` 中）：

| Pass | LightMode | 说明 |
|------|-----------|------|
| ForwardLit | UniversalForward | URP PBR + 内联自定义发光：`albedo * _VoxelEmissiveMask * _VoxelEmissiveIntensity`（最小化PBR实现） |
| VoxelGI_Voxelization | VoxelGI_Voxelization | 体素化 Pass（不再依赖外部 `VoxelGI.hlsl`） |
| VoxelGI_Shadow | VoxelGI_Shadow | 体素阴影 Pass（线性深度写入 `ShadowDepth`） |
| ShadowCaster | ShadowCaster | URP 标准阴影投射 |
| DepthOnly | DepthOnly | 深度预通道 |
| DepthNormals | DepthNormals | 深度法线预通道（生成 `_CameraNormalsTexture`） |

---

## 八、Compute Shader Kernel 汇总

**文件**: `Assets/VXGI/Shaders/VoxelGICompute.compute`

| Kernel | Thread Group | 用途 |
|--------|:-----------:|------|
| `VoxelDirectLighting` | (4,4,4) | 体素直接光照 + 阴影采样 |
| `MipmapGeneration` | (8,8,8) | 3D 纹理 Mipmap 降采样 |
| `CopyTexture3D` | (8,8,8) | 3D 纹理 Mip 级别拷贝 |
| `VoxelIndirectLighting` | (8,8,8) | 体素间接光照（Fibonacci 半球 Cone Tracing） |
| `BilateralFiltering` | (8,8,1) | 屏幕空间双边滤波降噪 |
| `OverwriteGBufferAlpha` | (8,8,8) | 回填主 GBuffer 的 `a` 通道（Opacity / EmissiveIntensity） |

---

## 九、配置结构体

定义在 `Assets/VXGI/Scripts/VoxelGIRendererFeature.cs` 中，通过 Odin Inspector 暴露面板：

### VoxelizationConfig
| 字段 | 默认值 | 说明 |
|------|--------|------|
| ShadowMapResolution | 1024 | 阴影图分辨率 |
| VoxelTextureResolution | 256 | 体素纹理分辨率 |
| VoxelSize | 0.1f | 体素大小（仅无 `VoxelGIVolume` 时作为回退值） |
| VoxelizationLayerMask | Everything | 体素化阶段参与的 Layer 过滤掩码 |
| StableMipLevel | 6 | 稳定 Mip 级别 |
| EnableConservativeRasterization | false | 保守光栅化开关 |
| ConsevativeRasterizeScale | 1.5f | 保守光栅化缩放 |

### DirectLightingConfig
| 字段 | 默认值 | 说明 |
|------|--------|------|
| LightIndensityMulti | 1.5f | 光照强度乘数 |
| EmissiveMulti | 1.5f | 自发光乘数 |
| ShadowSunBias | 0.25f | 阴影太阳偏移 |
| ShadowNormalBias | 1f | 阴影法线偏移 |

### IndirectLightingConfig
| 字段 | 默认值 | 说明 |
|------|--------|------|
| EnableSecondBounce | true | 二次反弹开关 |
| ConeTraceQuality | VeryLow | 间接光 cone 数质量档位（VeryLow/Low/Mid/High） |
| IndirectLightingMaxStepNum | 12 | 最大步进次数 |
| IndirectLightingAlphaAtten | 2f | Alpha 衰减 |
| IndirectLightingScale | 2f | 间接光强度缩放 |
| IndirectLightingFirstStep | 1f | 首步长度 |
| IndirectLightingStepScale | 1f | 步长递增系数 |
| IndirectLightingConeAngle | 120f | 锥体角度 |
| IndirectLightingMinMipLevel | 0 | 间接光采样最小 Mip 级别 |

### ConeTracingConfig
| 字段 | 默认值 | 说明 |
|------|--------|------|
| BlueNoiseLUT | - | 蓝噪声纹理 |
| ScreenMaxStepNum | 32 | 屏幕最大步进 |
| ScreenAlphaAtten | 5f | Alpha 衰减 |
| ScreenScale | 1f | 缩放 |
| ScreenFirstStep | 0.9f | 首步 |
| ScreenStepScale | 1.2f | 步长递增 |
| ScreenConeAngle | 120f | 锥体角度 |

### TemporalFilterConfig
| 字段 | 默认值 | 说明 |
|------|--------|------|
| EnableTemporalFilter | true | 开关 |
| TemporalBlendAlpha | 0.005f | 混合权重 |
| ClampAABBScale | 1.2f | AABB 钳制缩放 |
| BlueNoiseScale | (1,1) | 蓝噪声缩放 |
| HaltonValueCount | 8 | Halton 序列长度 |

### BilateralFilterConfig
| 字段 | 默认值 | 说明 |
|------|--------|------|
| EnableBilateralFilter | true | 开关 |
| BilateralSamplerRadius | 6.0f | 采样半径 |
| DepthThresholdLowerBound | 0.1f | 深度阈值下界 |
| DepthThresholdUpperBound | 0.2f | 深度阈值上界 |
| NormalThresholdLowerBound | 0.7f | 法线阈值下界 |
| NormalThresholdUpperBound | 1f | 法线阈值上界 |

### DebugConfig
| 字段 | 说明 |
|------|------|
| DebugMode | 调试开关 |
| DebugType | 枚举：Albedo/Normal/Emissive/Lighting/IndirectLighting/ConeTrace/TemporalFilter/BilateralFilter |
| DirectLightingDebugMipLevel | 调试用 Mip 级别 |
| RayStepSize | Ray Marching 步长 |

---

## 十、数据流概览

```
场景物体 (VoxelGI/Lit 材质)
  │
  ├─[VoxelizationPass: ShadowMap]────→ ShadowDepth (2D RFloat)
  │   ShaderTag: "VoxelGI_Shadow"（物体自身材质）
  │
  ├─[VoxelizationPass: Voxelize]─────→ UavAlbedo/UavNormal + TmpOpacityAccum/TmpEmissiveAccum (3D RInt)
  │   ShaderTag: "VoxelGI_Voxelization"（物体自身材质 + LayerMask过滤）
  │
  ├─[Compute: OverwriteGBufferAlpha]→ 回填 UavAlbedo.a=Opacity, UavNormal.a=EmissiveIntensity(0~1)
  │
  ├─[Compute: VoxelDirectLighting]───→ UavDirectLighting (3D ARGBHalf + Mips)
  │   输入: 3D GBuffer + ShadowDepth + SunLight参数
  │
  ├─[Compute: VoxelIndirectLighting]─→ UavIndirectLighting (3D ARGBHalf + Mips)
  │   输入: 3D GBuffer + UavDirectLighting(带Mip)
  │
  ├─[ConeTracingPass]────────────────→ ConeTracingRT (2D ARGBHalf)
  │   输入: UavIndirectLighting(或UavDirectLighting) + _CameraDepthTexture + _CameraNormalsTexture + BlueNoise
  │   GiMaterial Pass 2
  │
  ├─[TemporalFilterPass]────────────→ PingPong RT (2D ARGBHalf)
  │   输入: ConeTracingRT + History + _CameraMotionVectorsTexture
  │   GiMaterial Pass 3
  │
  ├─[Compute: BilateralFiltering]───→ BilateralFilterRT (2D ARGBHalf UAV)
  │   输入: Temporal输出 + Depth + Normals
  │
  └─[CombinePass]───────────────────→ Camera Color Target
      输入: SceneDirect(colorTarget副本) + 最终间接光
      GiMaterial Pass 4 (正常) / Pass 5 (调试)
```

---

## 十一、VoxelGIVolume 组件

**文件**: `Assets/VXGI/Scripts/VoxelGIVolume.cs`

- 单例模式（`Instance`），场景中放置一个即可
- `transform.position` = 体素化区域中心
- `transform.lossyScale` = 体素化区域 xyz 尺寸（支持长方体）
- 提供 Gizmos 可视化（绿色体素区域线框 + 黄色阴影相机视锥体）
- VoxelSize = min(scale.x, scale.y, scale.z) / VoxelTextureResolution

---

## 十二、关键注意事项

1. **URP 法线纹理**: URP 14 中 `ConfigureInput(Normal)` 生成的纹理名为 `_CameraNormalsTexture`（view space 法线），而非 Built-in 管线的 `_CameraDepthNormalsTexture`。
2. **DepthNormals Pass**: 场景物体 shader 必须包含 `LightMode = "DepthNormals"` 的 Pass，否则法线纹理内容为空。
3. **UAV 写入**: 体素化使用 `SetRandomWriteTarget` 绑定 3D 纹理到 u1~u4，通过 `InterlockedMax` 原子操作写入。
4. **SRP DrawRenderers**: 体素化阶段使用 `context.DrawRenderers` 配合 `ShaderTagId("VoxelGI_Voxelization")`，物体使用自身材质的对应 Pass，无需 overrideMaterial。
5. **体素阴影绘制**: ShadowMap 阶段使用 `ShaderTagId("VoxelGI_Shadow")` 绘制物体自身材质的体素阴影 Pass；`Hidden/VoxelGI_URP` 中旧 `VoxelShadow` 仅保留空壳以稳定索引。
6. **Blocker 语义**: `VXGIBlocker.shader` 采用双 Pass：`UniversalForward` 的不可见 Pass（`ColorMask 0`）+ `VoxelGI_Shadow` 阴影 Pass，实现“只遮挡，不注入体素”。
7. **Mipmap 生成**: 使用 Compute Shader 手动生成 3D 纹理 Mip Chain（Unity 不支持 3D 纹理自动生成 Mipmap）。
8. **Temporal History**: 每个相机维护独立的时间滤波历史（通过 `GetOrCreateTemporalHistory`）。
9. **GBuffer A 通道语义**: `MovingAverage` 过程中的 `w` 用作融合权重，最终业务值通过 `OverwriteGBufferAlpha` 回填到 `UavAlbedo.a`（Opacity）和 `UavNormal.a`（EmissiveIntensity）。
10. **命名统一**: 当前光照体素纹理命名为 `UavDirectLighting` 与 `UavIndirectLighting`，调试绑定同名 `VoxelTexDirectLighting` / `VoxelTexIndirectLighting`。
