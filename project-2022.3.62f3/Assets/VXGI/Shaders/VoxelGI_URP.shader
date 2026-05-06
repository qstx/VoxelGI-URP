Shader "Hidden/VoxelGI_URP"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo (RGB)", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1,1,1,1)
        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0,0)
        _EmissionMap("Emission Map", 2D) = "white" {}
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    #define MOVING_AVERAGE_MAX 255.0
    #define EMISSIVE_SIG_BIT 16
    #define EMISSIVE_EXP_BIT 8
    #define USE_YCOCG_CLAMP 1

    // Voxelization - 全局属性
    float4x4 ObjWorld;
    float4x4 VoxelizationForwardVP;
    float4x4 VoxelizationRightVP;
    float4x4 VoxelizationUpVP;
    float4x4 VoxelToWorld;
    float4x4 WorldToVoxel;
    uniform RWTexture3D<uint> OutAlbedo : register(u1);
    uniform RWTexture3D<uint> OutNormal : register(u2);
    uniform RWTexture3D<uint> OutTmpOpacityAccum : register(u3);
    uniform RWTexture3D<uint> OutTmpEmissiveAccum : register(u4);
    // 方案A：将Opacity并入Albedo.a，Emissive强度并入Normal.a，保留旧UAV声明注释便于回退
    // uniform RWTexture3D<uint> OutEmissive : register(u3);
    // uniform RWTexture3D<uint> OutOpacity : register(u4);
    float3 CameraPosW;
    float4x4 CameraView;
    float4x4 CameraViewProj;
    float4x4 CameraInvView;
    float4x4 CameraInvViewProj;
    float4x4 CameraReprojectInvViewProj;
    float CameraFielfOfView; // degree
    float CameraAspect;
    int VoxelTextureResolution;
    float VoxelSize;
    float RayStepSize;

    // Shadowmapping
    float4x4 WorldToShadowVP;
    float4x4 WorldToShadowVPLinear; // 不带反向Z的VP，用于计算写入color target的线性深度[0,1]
    SamplerState point_clamp_sampler;
    SamplerState linear_clamp_sampler;
    SamplerState sampler_point_repeat;
    SamplerState sampler_linear_repeat;

    // Cone Tracing
    TEXTURE2D(_CameraDepthTexture);
    SAMPLER(sampler_CameraDepthTexture);
    TEXTURE2D(_CameraNormalsTexture);
    SAMPLER(sampler_CameraNormalsTexture);
    Texture3D<float4> ScreenConeTraceLighting;
    float ScreenMaxMipLevel;
    int ScreenMaxStepNum;
    float ScreenAlphaAtten;
    float ScreenScale;
    float ScreenConeAngle;
    float ScreenFirstStep;
    float ScreenStepScale;
    int EnableTemporalFilter;
    int ConeTraceQuality; // 0:VeryLow(1), 1:Low(2), 2:Mid(4), 3:High(8)
    float3 ConeTraceDirection;
    float2 RandomUV;
    TEXTURE2D(NoiseLUT);
    SAMPLER(sampler_NoiseLUT);
    // 强制点采样器，防止蓝噪声在小数级偏移时被双线性过滤模糊成灰色，破坏 Temporal 抖动
    // 注意: sampler_point_repeat 已在前面 Shadowmapping 部分定义过，这里不再重复定义
    float4 ScreenResolution;
    float4 BlueNoiseResolution;
    float4 BlueNoiseScale;

    // Temporal Filter
    TEXTURE2D(_CameraMotionVectorsTexture);
    SAMPLER(sampler_CameraMotionVectorsTexture);
    TEXTURE2D(CurrentScreenIrradiance);
    SAMPLER(sampler_CurrentScreenIrradiance);
    TEXTURE2D(HistoricalScreenIrradiance);
    SAMPLER(sampler_HistoricalScreenIrradiance);
    float BlendAlpha;
    float TemporalClampAABBScale;

    // Combine
    TEXTURE2D(SceneDirect);
    SAMPLER(sampler_SceneDirect);
    TEXTURE2D(VXGIIndirect);
    SAMPLER(sampler_VXGIIndirect);

    // Debug
    Texture3D<uint> VoxelTexAlbedo;
    Texture3D<uint> VoxelTexNormal;
    // 方案A：调试时Emissive/Opacity从Albedo/Normal解码得到，保留旧声明注释便于回退
    // Texture3D<uint> VoxelTexEmissive;
    // Texture3D<uint> VoxelTexOpacity;
    Texture3D<half4> VoxelTexDirectLighting;
    Texture3D<half4> VoxelTexIndirectLighting;
    float EmissiveMulti;
    int VisualizeDebugType;
    float HalfPixelSize;
    int EnableConservativeRasterization;
    int DirectLightingDebugMipLevel;
    int IndirectLightingDebugMipLevel;
    TEXTURE2D(ScreenConeTraceIrradiance);
    SAMPLER(sampler_ScreenConeTraceIrradiance);
    TEXTURE2D(ScreenBlendIrradiance);
    SAMPLER(sampler_ScreenBlendIrradiance);
    TEXTURE2D(ScreenBilateralFiltering);
    SAMPLER(sampler_ScreenBilateralFiltering);

    // --------------------------------------------------------------------------------------------------------------
    // Util.
    // --------------------------------------------------------------------------------------------------------------

    float3 RgbToHsl(float3 c)
    {
        float4 K = float4(0.0f, -1.0f / 3.0f, 2.0f / 3.0f, -1.0f);
        float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
        float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
        float d = q.x - min(q.w, q.y);
        float e = 1.0e-10;
        return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
    }

    float3 HslToRgb(float3 c)
    {
        float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
        float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
        return abs(c.z * lerp(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y));
    }

    float3 RgbToYcocg(float3 c)
    {
        float tmp = (c.r + c.b) / 2.0;
        float y = (c.g + tmp) / 2.0;
        float co = (c.r - c.b) / 2.0;
        float cg = (c.g - tmp) / 2.0;
        return float3(y, co, cg);
    }

    float3 YcocgToRgb(float3 c)
    {
        float r = c.x + c.y - c.z;
        float g = c.x + c.z;
        float b = c.x - c.y - c.z;
        return float3(r, g, b);
    }

    uint EncodeGbuffer(float4 value)
    {
        uint res = (uint(value.x * 255.f) << 24) + (uint(value.y * 255.f) << 16)
            + (uint(value.z * 255.f) << 8) + uint(value.w * 255.f);
        return res;
    }

    float4 DecodeGbuffer(uint value)
    {
        float4 res = float4(0.f, 0.f, 0.f, 0.f);
        res.w = (value & 255) / 255.f;
        value = value >> 8;
        res.z = (value & 255) / 255.f;
        value = value >> 8;
        res.y = (value & 255) / 255.f;
        value = value >> 8;
        res.x = (value & 255) / 255.f;
        return res;
    }

    uint EncodeFloat2ToUint248(float2 value)
    {
        uint res = asuint(value.x);
        res = res & 0x7FFFFF00;
        res = res + uint(value.y * 255);
        return res;
    }

    float2 DecodeUint248ToFloat2(uint value)
    {
        float2 res = float2(0.f, 0.f);
        res.y = (value & 255) / 255.f;
        res.x = asfloat(value & 0x00);
        return res;
    }

    uint EncodeEmissive(float4 value)
    {
        uint res = uint(clamp(value.x * 255.f / 10.f, 0.f, 255.f)) << 24;
        res = res + uint(clamp(value.y * 255.f / 10.f, 0.f, 255.f)) << 16;
        res = res + uint(clamp(value.z * 255.f / 10.f, 0.f, 255.f)) << 8;
        res = res + uint(clamp(value.y * 255.f, 0.f, 255.f));
        return res;
    }

    float4 DecodeEmissive(uint value)
    {
        float4 res = float4(0.f, 0.f, 0.f, 0.f);
        res.w = (value & 255) / 255.f;
        value = value >> 8;
        res.z = (value & 255) / 255.f * 10.f;
        value = value >> 8;
        res.y = (value & 255) / 255.f * 10.f;
        value = value >> 8;
        res.x = (value & 255) / 255.f * 10.f;
        return res;
    }

    void MovingAverage(uniform RWTexture3D<uint> outUav, int3 uvw, float4 val)
    {
        uint newVal = 0;
        newVal = EncodeGbuffer(val);
        uint prevStoredVal = 0xFFFFFFFF;
        uint curStoredVal;

        InterlockedCompareExchange(outUav[uvw], prevStoredVal, newVal, curStoredVal);
        while (curStoredVal != prevStoredVal)
        {
            prevStoredVal = curStoredVal;
            float4 gbuffer = float4(0.f, 0.f, 0.f, 0.f);
            gbuffer = DecodeGbuffer(curStoredVal);
            gbuffer.w *= MOVING_AVERAGE_MAX;
            gbuffer.xyz = (gbuffer.xyz * gbuffer.w);
            float4 curValF = gbuffer + val;
            curValF.xyz /= max(curValF.w, 0.001f);
            curValF.w /= MOVING_AVERAGE_MAX;
            curValF.w += 0.001f;
            newVal = EncodeGbuffer(curValF);
            InterlockedCompareExchange(outUav[uvw], prevStoredVal, newVal, curStoredVal);
        }
    }

    void ScalarMax(uniform RWTexture3D<uint> outUav, int3 uvw, float val)
    {
        uint packedVal = (uint)round(saturate(val) * 65535.0f);
        InterlockedMax(outUav[uvw], packedVal);
    }

    void OpacityMoveingAvg(uniform RWTexture3D<uint> outUav, int3 uvw, float2 val)
    {
        uint newVal = EncodeFloat2ToUint248(val);
        uint prevStoredVal = 0xFFFFFFFF;
        uint curStoredVal;

        InterlockedCompareExchange(outUav[uvw], prevStoredVal, newVal, curStoredVal);
        while (curStoredVal != prevStoredVal)
        {
            prevStoredVal = curStoredVal;
            float2 gbuffer = DecodeUint248ToFloat2(curStoredVal);
            gbuffer.y *= MOVING_AVERAGE_MAX;
            gbuffer.x = (gbuffer.x * gbuffer.y);
            float2 curValF = gbuffer + val;
            curValF.x /= max(curValF.y, 0.001f);
            curValF.y /= MOVING_AVERAGE_MAX;
            curValF.y += 0.001f;
            newVal = EncodeFloat2ToUint248(curValF);
            InterlockedCompareExchange(outUav[uvw], prevStoredVal, newVal, curStoredVal);
        }
    }

    float4 UnityClipToClipPos(float4 pos)
    {
        pos.y = -pos.y;
        return pos;
    }

    float CalcMipLevel(float size)
    {
        return size <= 1.0 ? size : log2(size) + 1;
    }

    float3x3 GetTangentBasis(float3 TangentZ)
    {
        float3 UpVector = abs(TangentZ.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
        float3 TangentX = normalize(cross(UpVector, TangentZ));
        float3 TangentY = cross(TangentZ, TangentX);
        return float3x3(TangentX, TangentY, TangentZ);
    }

    float TextureSDF(float3 position)
    {
        position = .5f - abs(position - .5f);
        return min(min(position.x, position.y), position.z);
    }

    static half2 NeighborUVNoise[9] =
    {
        half2(-1.0, -1.0),
        half2(-1.0, 0.0),
        half2(-1.0, 1.0),
        half2(0.0, -1.0),
        half2(0.0, 1.0),
        half2(1.0, -1.0),
        half2(1.0, 0.0),
        half2(1.0, 1.0),
        half2(0.0, 0.0),
    };

    static float3 Fibonacci_Lattice_Hemisphere_1[1] =
    {
        float3(0.0, 0.0, 1.0)
    };

    static float3 Fibonacci_Lattice_Hemisphere_4[4] =
    {
        float3(-0.731585503467728, -0.670192249370187, 0.125),
        float3(0.0810458159223954, 0.923475270768781, 0.375),
        float3(0.474962433620099, -0.619504387918014, 0.625),
        float3(-0.476722366176565, 0.0843254741286256, 0.875),
    };

    static float3 Fibonacci_Lattice_Hemisphere_8[8] =
    {
        float3(-0.735927295315164, -0.674169686362497, 0.0625),
        float3(0.0858751947674414, 0.978503551819642, 0.1875),
        float3(0.577966879673501, -0.75385544768243, 0.3125),
        float3(-0.885472495182538, 0.156627616578975, 0.4375),
        float3(0.697614586708622, 0.443765296537886, 0.5625),
        float3(-0.188520590528854, -0.701287200044783, 0.6875),
        float3(-0.26869090798331, 0.517347993102423, 0.8125),
        float3(0.326869977432545, -0.119372391503427, 0.9375),
    };

    static float3 Fibonacci_Lattice_Hemisphere_16[16] =
    {
        float3(-0.737008746736654, -0.675160384452218, 0.03125),
        float3(0.0870406817287227, 0.991783674610648, 0.09375),
        float3(0.600965734598756, -0.783853381276229, 0.15625),
        float3(-0.960864647623557, 0.169963426793115, 0.21875),
        float3(0.80969671855586, 0.515062774290544, 0.28125),
        float3(-0.243784330107323, -0.906865556680881, 0.34375),
        float3(-0.421159310808654, 0.810916624825992, 0.40625),
        float3(0.829731504061828, -0.303016614507021, 0.46875),
        float3(-0.783119519232434, -0.323260353426092, 0.53125),
        float3(0.341047499504824, 0.72879869688516, 0.59375),
        float3(0.225822703319255, -0.71995836279995, 0.65625),
        float3(-0.601554193574072, 0.34861295112696, 0.71875),
        float3(0.609658853079947, 0.134031788622115, 0.78125),
        float3(-0.308692885622987, -0.43908386427396, 0.84375),
        float3(-0.0543268871447994, 0.419236838592646, 0.90625),
        float3(0.189662913959254, -0.159848104675922, 0.96875),
    };

    bool IsInsideVoxelgrid(const float3 p)
    {
        return abs(p.x) < 1.1f && abs(p.y) < 1.1f && abs(p.z) < 1.1f;
    }

    // --------------------------------------------------------------------------------------------------------------
    // Cone Tracing.
    // --------------------------------------------------------------------------------------------------------------

    #define INDIRECT_CONE_TRACE_MID 1
    #if INDIRECT_CONE_TRACE_VERY_LOW
    #define CONE_COUNT 1
    #define Fibonacci_Lattice_Hemisphere Fibonacci_Lattice_Hemisphere_1
    #elif INDIRECT_CONE_TRACE_LOW
    #define CONE_COUNT 4
    #define Fibonacci_Lattice_Hemisphere Fibonacci_Lattice_Hemisphere_4
    #elif INDIRECT_CONE_TRACE_MID
    #define CONE_COUNT 8
    #define Fibonacci_Lattice_Hemisphere Fibonacci_Lattice_Hemisphere_8
    #elif INDIRECT_CONE_TRACE_HIGH
    #define CONE_COUNT 16
    #define Fibonacci_Lattice_Hemisphere Fibonacci_Lattice_Hemisphere_16
    #endif

    struct ConeTracingVsInput
    {
        float3 vertex : POSITION;
        float2 uv : TEXCOORD0;
    };

    struct ConeTracingFsInput
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    float3 UniformSampleDiskConcentric(int index, int maxCount, float2 hashNoise)
    {
        // Golden ratio conjugate
        const float phi = 0.618033988749895f;
        float2 uv = float2(
            frac((index + 0.5f) / maxCount + hashNoise.x),
            frac((index + 0.5f) * phi + hashNoise.y)
        );

        float r = sqrt(uv.x);
        float theta = 2.0f * PI * uv.y;

        float x = r * cos(theta);
        float y = r * sin(theta);
        float z = sqrt(max(0.0f, 1.0f - x * x - y * y));

        return float3(x, y, z);
    }

    float3 CalculateScreenIrradiance(float4 voxelPos, float3 normal, float2 hashNoise)
    {
        // if (TextureSDF(voxelPos / VoxelTextureResolution) < 0.0)
        // {
        //     return float3(0.f, 0.f, 0.f);
        // }

        normal = normalize(normal);
        float3 origin = voxelPos.xyz / VoxelTextureResolution;

        float3x3 TangentBasis = GetTangentBasis(normal);
        float coneTan = tan(ScreenConeAngle * 3.14159265f / 360.f);
        float offset, sampleRadius, step, ndotl;
        float3 coordinate, coneDir;
        float4 coneColor, resultColor = float4(0.f, 0.f, 0.f, 0.f);
        int stepNum;

        // 根据外部传递的档位动态决定 Cone 数量
        int dynamicConeCount = 8;
        if (ConeTraceQuality == 0) dynamicConeCount = 1;
        else if (ConeTraceQuality == 1) dynamicConeCount = 2;
        else if (ConeTraceQuality == 2) dynamicConeCount = 4;

        for (int coneIndex = 0; coneIndex < dynamicConeCount; ++coneIndex)
        {
            coneColor = float4(0.f, 0.f, 0.f, 0.f);
            step = ScreenFirstStep / VoxelTextureResolution;
            // 第4点：蓝噪声抖动步进起点
            offset = step * (0.5f + hashNoise.x);
            sampleRadius = offset * coneTan;
            coneDir = UniformSampleDiskConcentric(coneIndex, dynamicConeCount, hashNoise);
            coneDir = normalize(mul(coneDir, TangentBasis));

            // 第3点：Ray-AABB计算最大步进距离
            float3 invDir = 1.0f / coneDir;
            float3 t0 = (0.0f - origin) * invDir;
            float3 t1 = (1.0f - origin) * invDir;
            float3 tmax = max(t0, t1);
            float maxTraceDist = min(min(tmax.x, tmax.y), tmax.z);

            coordinate = origin + offset * coneDir;
            stepNum = 0;
            [loop]
            // while (coneColor.a < 0.95f && TextureSDF(coordinate) > 0.0f && stepNum <= ScreenMaxStepNum)
            while (coneColor.a < 0.95f && offset < maxTraceDist && stepNum <= ScreenMaxStepNum)
            {
                float mip = clamp(CalcMipLevel(sampleRadius * VoxelTextureResolution), 0.0, ScreenMaxMipLevel);
                float4 sampledRadiance = ScreenConeTraceLighting.SampleLevel(linear_clamp_sampler, coordinate, mip);
                coneColor += (1.f - pow(coneColor.a, ScreenAlphaAtten)) * sampledRadiance;

                step *= ScreenStepScale;
                offset += step;
                sampleRadius = offset * coneTan;
                coordinate = origin + offset * coneDir;
                stepNum++;
            }

            ndotl = dot(coneDir, normal);
            resultColor += coneColor * ndotl;
        }

        // 非 Temporal 模式下，发射了 N 根光线累加，必须除以 N (即 dynamicConeCount) 进行均值归一化
        // 这样才能保证不管选哪一档（2,4,8），最终的平均亮度基准是一致的
        return resultColor.xyz / dynamicConeCount;
    }

    float2 UniformSampleDiskConcentric(float2 E)
    {
        float2 p = 2 * E - 1;
        float Radius;
        float Phi;
        if (abs(p.x) > abs(p.y))
        {
            Radius = p.x;
            Phi = (PI / 4) * (p.y / p.x);
        }
        else
        {
            Radius = p.y;
            Phi = (PI / 2) - (PI / 4) * (p.x / p.y);
        }
        return float2(Radius * cos(Phi), Radius * sin(Phi));
    }

    float3 CalculateTemporalScreenIrradiance(float4 voxelPos, float3 normal, float2 hashNoise)
    {
        // if (TextureSDF(voxelPos / VoxelTextureResolution) < 0.0)
        // {
        //     return float3(0.f, 0.f, 0.f);
        // }

        normal = normalize(normal);
        float3 origin = voxelPos.xyz / VoxelTextureResolution;

        float3x3 TangentBasis = GetTangentBasis(normal);
        float coneTan = tan(ScreenConeAngle * 3.14159265f / 360.f);
        float offset, sampleRadius, step, ndotl;
        float3 coordinate, coneDir;
        float4 coneColor, resultColor = float4(0.f, 0.f, 0.f, 0.f);
        int stepNum = 0;

        coneColor = float4(0.f, 0.f, 0.f, 0.f);
        step = ScreenFirstStep / VoxelTextureResolution;
        // 第4点：蓝噪声抖动步进起点
        offset = step * (0.5f + hashNoise.y);
        sampleRadius = offset * coneTan;

        coneDir.xy = UniformSampleDiskConcentric(hashNoise);
        coneDir.z = sqrt(1 - dot(coneDir.xy, coneDir.xy));
        coneDir = normalize(mul(coneDir, TangentBasis));

        // 第3点：Ray-AABB计算最大步进距离
        float3 invDir = 1.0f / coneDir;
        float3 t0 = (0.0f - origin) * invDir;
        float3 t1 = (1.0f - origin) * invDir;
        float3 tmax = max(t0, t1);
        float maxTraceDist = min(min(tmax.x, tmax.y), tmax.z);

        coordinate = origin + offset * coneDir;
        [loop]
        // while (coneColor.a < 0.95f && TextureSDF(coordinate) > 0.0f && stepNum <= ScreenMaxStepNum)
        while (coneColor.a < 0.95f && offset < maxTraceDist && stepNum <= ScreenMaxStepNum)
        {
            float mip = clamp(CalcMipLevel(sampleRadius * VoxelTextureResolution), 0.0, ScreenMaxMipLevel);
            float4 sampledRadiance = ScreenConeTraceLighting.SampleLevel(linear_clamp_sampler, coordinate, mip);
            coneColor += (1.f - pow(coneColor.a, ScreenAlphaAtten)) * sampledRadiance;

            step *= ScreenStepScale;
            offset += step;
            sampleRadius = offset * coneTan;
            coordinate = origin + offset * coneDir;
            stepNum++;
        }

        ndotl = dot(coneDir, normal);
        resultColor = coneColor * ndotl;

        int dynamicConeCount = 8;
        if (ConeTraceQuality == 0) dynamicConeCount = 1;
        else if (ConeTraceQuality == 1) dynamicConeCount = 2;
        else if (ConeTraceQuality == 2) dynamicConeCount = 4;

        // Temporal 模式每帧只发射一根光线
        // 之前为了能量补偿我们把它乘了 COUNT，现在由于非 Temporal 模式已经做了除以 N 均值归一化，
        // 所以单根光线（能量1）直接代表的就是“半球的平均亮度估计值”
        // 这里不再需要乘 dynamicConeCount，Temporal 平滑后自然就是平均亮度。
        return resultColor.xyz;
    }

    ConeTracingFsInput ConeTracingVs(ConeTracingVsInput v)
    {
        ConeTracingFsInput o;
        o.pos = UnityClipToClipPos(float4(v.vertex, 1.f));
        o.uv = v.uv;
        return o;
    }

    float4 ConeTracingFs(ConeTracingFsInput i) : SV_Target
    {
        float depth = SAMPLE_TEXTURE2D(_CameraDepthTexture, sampler_CameraDepthTexture, i.uv).r;

        // 第2点：天空盒/背景的 Early Out 剔除
        #if UNITY_REVERSED_Z
        if (depth == 0.0) return float4(0.0, 0.0, 0.0, 1.0);
        #else
        if (depth == 1.0) return float4(0.0, 0.0, 0.0, 1.0);
        #endif

        // URP14的_CameraNormalsTexture直接存储view space法线（R8G8B8A8_SNorm，无需stereographic解码）
        float3 viewNormal = normalize(SAMPLE_TEXTURE2D(_CameraNormalsTexture, sampler_CameraNormalsTexture, i.uv).xyz);

        // 使用我们自己传入的CameraInvView矩阵，将view space法线变换到world space
        float3 N = mul((float3x3)CameraInvView, viewNormal);

        // 第5点：使用URP标准的 ComputeWorldSpacePosition
        // float4 clipPos = float4(mad(2.0, float2(i.uv.x, 1 - i.uv.y), -1.0), depth, 1.0);
        // float4 worldPos = mul(CameraReprojectInvViewProj, clipPos);
        // worldPos /= worldPos.w;
        float3 worldPos = ComputeWorldSpacePosition(i.uv, depth, UNITY_MATRIX_I_VP);
        
        float3 voxelPos = mul(WorldToVoxel, float4(worldPos, 1.0)).xyz;

        float3 screenIrradiance;
        if (EnableTemporalFilter > 0)
        {
            float2 noiseUV = (i.uv * ScreenResolution.xy * BlueNoiseResolution.zw * BlueNoiseScale.xy) + RandomUV;
            // 强制使用点采样，保证蓝噪声的高频特性
            float2 dirNoise = SAMPLE_TEXTURE2D_LOD(NoiseLUT, sampler_point_repeat, noiseUV, 0).xy;
            screenIrradiance = CalculateTemporalScreenIrradiance(float4(voxelPos, 1.f), N, dirNoise);
        }
        else
        {
            // 传递固定的 (0,0) 避免在没有 Temporal 时因为 RandomUV 跳动导致画面闪烁
            screenIrradiance = CalculateScreenIrradiance(float4(voxelPos, 1.f), N, float2(0.f, 0.f));
        }
        float3 traceColor = screenIrradiance * ScreenScale;

        return float4(traceColor, 1.f);
    }

    // --------------------------------------------------------------------------------------------------------------
    // Temporal Filter.
    // --------------------------------------------------------------------------------------------------------------
    struct TemporalFilterVsInput
    {
        float3 vertex : POSITION;
        float2 uv : TEXCOORD0;
    };

    struct TemporalFilterFsInput
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    TemporalFilterFsInput TemporalFilterVs(TemporalFilterVsInput v)
    {
        TemporalFilterFsInput o;
        o.pos = UnityClipToClipPos(float4(v.vertex, 1.f));
        o.uv = v.uv;
        return o;
    }

    float4 TemporalFilterFs(TemporalFilterFsInput i) : SV_Target
    {
        float3 traceColor = SAMPLE_TEXTURE2D(CurrentScreenIrradiance, sampler_CurrentScreenIrradiance, i.uv).rgb;

        float2 velocity = SAMPLE_TEXTURE2D(_CameraMotionVectorsTexture, sampler_CameraMotionVectorsTexture, i.uv).rg;
        float2 historicalUV = i.uv - velocity;
        float3 historicalColor = SAMPLE_TEXTURE2D(HistoricalScreenIrradiance, sampler_HistoricalScreenIrradiance, historicalUV).rgb;

    #if USE_YCOCG_CLAMP
        traceColor = RgbToYcocg(traceColor);
        historicalColor = RgbToYcocg(historicalColor);
    #endif

        // 使用 Variance Clipping (方差裁剪) 替代 min/max
        float3 m1 = float3(0.0, 0.0, 0.0); // 一阶矩 (均值)
        float3 m2 = float3(0.0, 0.0, 0.0); // 二阶矩 (平方均值)

        int k;
        for (k = 0; k < 9; ++k)
        {
            float2 neighborUV = i.uv + float2(NeighborUVNoise[k].x * ScreenResolution.z, NeighborUVNoise[k].y * ScreenResolution.w);
            float3 neighborColor = SAMPLE_TEXTURE2D(CurrentScreenIrradiance, sampler_CurrentScreenIrradiance, neighborUV).rgb;
    #if USE_YCOCG_CLAMP
            neighborColor = RgbToYcocg(neighborColor);
    #endif
            m1 += neighborColor;
            m2 += neighborColor * neighborColor;
        }

        m1 /= 9.0;
        m2 /= 9.0;

        // 计算标准差: sqrt(E[x^2] - E[x]^2)
        float3 variance = abs(m2 - m1 * m1); // abs 防止浮点数精度导致的负数
        float3 stdDev = sqrt(variance);

        // TemporalClampAABBScale 现在作为 Gamma 参数 (通常在 1.0 - 2.0 之间)
        float gamma = TemporalClampAABBScale;
        float3 boxMin = m1 - gamma * stdDev;
        float3 boxMax = m1 + gamma * stdDev;

        // 为了保留一些高光细节，也可以结合原始 traceColor
        // 但核心是用均值和方差构建的 box 来 clamp
        historicalColor = clamp(historicalColor, boxMin, boxMax);

    #if USE_YCOCG_CLAMP
        historicalColor = YcocgToRgb(historicalColor);
        traceColor = YcocgToRgb(traceColor); // 恢复 traceColor
    #endif

        BlendAlpha = 1.f - saturate((1.f - BlendAlpha) * (1 - length(velocity) * 30));
        float3 resultColor = lerp(historicalColor, traceColor, BlendAlpha);
        return float4(resultColor, 1.f);
    }

    // --------------------------------------------------------------------------------------------------------------
    // Combine.
    // --------------------------------------------------------------------------------------------------------------
    struct CombineVsInput
    {
        float3 vertex : POSITION;
        float2 uv : TEXCOORD0;
    };

    struct CombineFsInput
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    CombineFsInput CombineVs(CombineVsInput v)
    {
        CombineFsInput o;
        o.pos = UnityClipToClipPos(float4(v.vertex, 1.f));
        o.uv = v.uv;
        return o;
    }

    float4 CombineFs(CombineFsInput i) : SV_Target
    {
        float3 sceneColor = SAMPLE_TEXTURE2D(SceneDirect, sampler_SceneDirect, i.uv).rgb;
        float3 vxgiColor = SAMPLE_TEXTURE2D(VXGIIndirect, sampler_VXGIIndirect, i.uv).rgb;

        return float4(sceneColor + vxgiColor, 1.f);
    }

    // --------------------------------------------------------------------------------------------------------------
    // Debug.
    // --------------------------------------------------------------------------------------------------------------
    struct VoxelizationDebugVsInput
    {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
    };

    struct VoxelizationDebugFsInput
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    struct VoxelizationVsInput
    {
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        float2 uv : TEXCOORD0;
    };

    VoxelizationDebugFsInput VoxelizationDebugVs(VoxelizationVsInput v)
    {
        VoxelizationDebugFsInput o;
        o.pos = UnityClipToClipPos(v.vertex);
        o.uv = v.uv;
        return o;
    }

    float4 VoxelizationDebugFs(VoxelizationDebugFsInput fsIn) : SV_Target
    {
        float4 accumulatedColor = float4(0.f, 0.f, 0.f, 0.f);
        float fov = tan(CameraFielfOfView * 3.1415926f / 360.f);
        float3 rayDirView = float3(fov * CameraAspect * (fsIn.uv.x * 2.0f - 1.0f), fov * (fsIn.uv.y * 2.0f - 1.0f), -1.f);
        float3 rayDirW = normalize(mul((float3x3)CameraInvView, normalize(rayDirView)));

        int totalSamples = VoxelTextureResolution * VoxelSize / RayStepSize;
        [loop]
        for (int i = 0; i < totalSamples; ++i)
        {
            float4 rayWorld = float4(CameraPosW + rayDirW * RayStepSize * i, 1.f);
            float3 uvwLerp = mul(WorldToVoxel, rayWorld).xyz;
            uint3 uvw = uvwLerp;
            uvwLerp /= VoxelTextureResolution;
            float4 albedoPacked = DecodeGbuffer(VoxelTexAlbedo[uvw]);
            float4 normalPacked = DecodeGbuffer(VoxelTexNormal[uvw]);
            float opacity = albedoPacked.a;
            // 方案A：Normal.a存储的是0~5压缩值，调试时解压回真实强度
            float emissiveIntensity = normalPacked.a * 5.f;
            // 旧版：float opacity = DecodeGbuffer(VoxelTexOpacity[uvw]).x;
            float4 texSample = float4(0.f, 0.f, 0.f, 0.f);
            switch (VisualizeDebugType)
            {
            case 0: // albedo
                texSample = albedoPacked;
                texSample.a = opacity;
                break;
            case 1: // normal
                texSample = normalPacked;
                texSample.rgb = (texSample.rgb * 2.f) - float3(1.f, 1.f, 1.f);
                texSample.a = opacity;
                break;
            case 2: // emissive
                texSample.rgb = albedoPacked.rgb * emissiveIntensity * EmissiveMulti;
                texSample.a = opacity;
                // 旧版：texSample = DecodeGbuffer(VoxelTexEmissive[uvw]);
                // 旧版：texSample.rgb *= EmissiveMulti;
                break;
            case 3: // lighting
                texSample = VoxelTexDirectLighting.SampleLevel(linear_clamp_sampler, uvwLerp, DirectLightingDebugMipLevel);
                break;
            case 4: // indirectlighting
                texSample = VoxelTexIndirectLighting.SampleLevel(linear_clamp_sampler, uvwLerp, IndirectLightingDebugMipLevel);
                break;
            case 5: // cone trace
                float3 traceColor = SAMPLE_TEXTURE2D(ScreenConeTraceIrradiance, sampler_ScreenConeTraceIrradiance, fsIn.uv).rgb;
                return float4(traceColor, 1.f);
            case 6: // TAA
                float3 blendColor = SAMPLE_TEXTURE2D(ScreenBlendIrradiance, sampler_ScreenBlendIrradiance, fsIn.uv).rgb;
                return float4(blendColor, 1.f);
            case 7: // BilateralFiltering
                float3 filterColor = SAMPLE_TEXTURE2D(ScreenBilateralFiltering, sampler_ScreenBilateralFiltering, fsIn.uv).rgb;
                return float4(filterColor, 1.f);
            default:
                break;
            }

            if (texSample.a > 0.0001f)
            {
                accumulatedColor.rgb = accumulatedColor.rgb + (1.f - accumulatedColor.a) * texSample.rgb;
                accumulatedColor.a = accumulatedColor.a + (1.f - accumulatedColor.a) * texSample.a;
            }

            if (accumulatedColor.a > 0.95f)
            {
                break;
            }
        }
        return accumulatedColor;
    }
    ENDHLSL

    // --------------------------------------------------------------------------------------------------------------
    // Passes
    // --------------------------------------------------------------------------------------------------------------

    SubShader
    {
        pass // 已废弃
        {
            Name "Voxelization"
            Tags { "LightMode" = "VoxelGI_Voxelization" }

            // Cull Off
            // ZWrite Off
            // ZTest Off

            // HLSLPROGRAM
            //     #pragma enable_d3d11_debug_symbols
            //     #pragma require geometry
            //     #pragma target 5.0
            //     #pragma vertex VoxelizationVs
            //     #pragma geometry VoxelizationGs
            //     #pragma fragment VoxelizationFs
            // ENDHLSL
        }

        pass // 已废弃
        {
            Name "VoxelShadow"
            Tags { "LightMode" = "VoxelGI_Shadow" }

            // 已迁移到 VoxelGI/Lit.shader 的 VoxelGI_Shadow Pass
            // 保留空壳Pass，避免删除后引发后续PassIndex变化
            // Cull Back
            // ZWrite Off
            // ZTest Off
            // BlendOp Max
            // Blend One One

            // HLSLPROGRAM
            //     #pragma enable_d3d11_debug_symbols
            //     #pragma target 5.0
            //     #pragma vertex ShadowVs
            //     #pragma fragment ShadowFs
            // ENDHLSL
        }

        pass
        {
            Name "ConeTracing"

            Cull Back
            ZWrite On
            ZTest Off

            HLSLPROGRAM
                #pragma enable_d3d11_debug_symbols
                #pragma target 5.0
                #pragma vertex ConeTracingVs
                #pragma fragment ConeTracingFs
            ENDHLSL
        }

        pass
        {
            Name "TemporalFilter"

            Cull Back
            ZWrite On
            ZTest Off

            HLSLPROGRAM
                #pragma enable_d3d11_debug_symbols
                #pragma target 5.0
                #pragma vertex TemporalFilterVs
                #pragma fragment TemporalFilterFs
            ENDHLSL
        }

        pass
        {
            Name "Combine"

            Cull Back
            ZWrite On
            ZTest Off

            HLSLPROGRAM
                #pragma enable_d3d11_debug_symbols
                #pragma target 5.0
                #pragma vertex CombineVs
                #pragma fragment CombineFs
            ENDHLSL
        }
        
        pass
        {
            Name "VoxelVisualization"

            Cull Off
            ZWrite Off
            ZTest Off

            HLSLPROGRAM
                #pragma enable_d3d11_debug_symbols
                #pragma target 5.0
                #pragma vertex VoxelizationDebugVs
                #pragma fragment VoxelizationDebugFs
            ENDHLSL
        }
    }
}
