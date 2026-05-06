Shader "VoxelGI/Lit"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo (RGBA)", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1,1,1,1)
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Range(0.0, 2.0)) = 1.0
        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _VoxelEmissiveMask("Voxel Emissive Mask", 2D) = "white" {}
        _VoxelEmissiveIntensity("Voxel Emissive Intensity", Range(0.0, 5.0)) = 0.0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull Mode", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float4 _BaseMap_ST;
            float _BumpScale;
            float _Metallic;
            float _Smoothness;
            float _VoxelEmissiveIntensity;
        CBUFFER_END

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap);
        SAMPLER(sampler_BumpMap);
        TEXTURE2D(_VoxelEmissiveMask);
        SAMPLER(sampler_VoxelEmissiveMask);
        float3 _LightDirection;
        float3 _LightPosition;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 tangentOS : TANGENT;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float4 tangentWS : TEXCOORD2;
            float2 uv : TEXCOORD3;
            half fogFactor : TEXCOORD4;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        struct DepthVaryings
        {
            float4 positionCS : SV_POSITION;
            float3 normalWS : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        half3 SampleNormalTS(float2 uv)
        {
            half3 n = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv).xyz * 2.0h - 1.0h;
            n.xy *= _BumpScale;
            n.z = sqrt(saturate(1.0h - dot(n.xy, n.xy)));
            return n;
        }

        half3 GetNormalWSFromTS(half3 normalTS, float3 normalWS, float4 tangentWS)
        {
            float3 bitangentWS = tangentWS.w * cross(normalWS, tangentWS.xyz);
            return normalize(TransformTangentToWorld(normalTS, half3x3(tangentWS.xyz, bitangentWS, normalWS)));
        }

        Varyings ForwardVertex(Attributes input)
        {
            Varyings output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
            VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);
            output.positionCS = positionInputs.positionCS;
            output.positionWS = positionInputs.positionWS;
            output.normalWS = normalInputs.normalWS;
            output.tangentWS = float4(normalInputs.tangentWS, input.tangentOS.w * GetOddNegativeScale());
            output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
            output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
            return output;
        }

        half4 ForwardFragment(Varyings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
            half emissiveMask = SAMPLE_TEXTURE2D(_VoxelEmissiveMask, sampler_VoxelEmissiveMask, input.uv).r;
            half3 normalTS = SampleNormalTS(input.uv);
            half3 normalWS = GetNormalWSFromTS(normalTS, input.normalWS, input.tangentWS);
            SurfaceData surfaceData = (SurfaceData)0;
            surfaceData.albedo = baseSample.rgb;
            surfaceData.alpha = 1.0h;
            surfaceData.metallic = _Metallic;
            surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
            surfaceData.smoothness = _Smoothness;
            surfaceData.normalTS = normalTS;
            surfaceData.occlusion = 1.0h;
            surfaceData.emission = baseSample.rgb * saturate(emissiveMask) * _VoxelEmissiveIntensity;
            surfaceData.clearCoatMask = 0.0h;
            surfaceData.clearCoatSmoothness = 0.0h;
            InputData inputData = (InputData)0;
            inputData.positionWS = input.positionWS;
            inputData.normalWS = NormalizeNormalPerPixel(normalWS);
            inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
            inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            inputData.fogCoord = input.fogFactor;
            inputData.vertexLighting = VertexLighting(input.positionWS, inputData.normalWS);
            inputData.bakedGI = SampleSH(inputData.normalWS);
            inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
            inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);
            half4 color = UniversalFragmentPBR(inputData, surfaceData);
            color.rgb = MixFog(color.rgb, inputData.fogCoord);
            color.a = 1.0h;
            return color;
        }

        DepthVaryings DepthVertex(Attributes input)
        {
            DepthVaryings output = (DepthVaryings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
            VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);
            output.positionCS = positionInputs.positionCS;
            output.normalWS = normalInputs.normalWS;
            return output;
        }

        DepthVaryings ShadowVertex(Attributes input)
        {
            DepthVaryings output = (DepthVaryings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
            float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
            float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
            output.positionCS = positionCS;
            output.normalWS = normalWS;
            return output;
        }

        half4 ShadowFragment(DepthVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return 0.0h;
        }

        half DepthFragment(DepthVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return input.positionCS.z;
        }

        half4 DepthNormalsFragment(DepthVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return half4(NormalizeNormalPerPixel(input.normalWS), 0.0h);
        }

        // ==========================================================================================================
        // VoxelGI_Voxelization Pass Functions
        // ==========================================================================================================
        #define MOVING_AVERAGE_MAX 255.0
        float4x4 VoxelizationForwardVP;
        float4x4 VoxelizationRightVP;
        float4x4 VoxelizationUpVP;
        float4x4 WorldToVoxel;
        uniform RWTexture3D<uint> OutAlbedo : register(u1);
        uniform RWTexture3D<uint> OutNormal : register(u2);
        uniform RWTexture3D<uint> OutTmpOpacityAccum : register(u3);
        uniform RWTexture3D<uint> OutTmpEmissiveAccum : register(u4);
        int VoxelTextureResolution;
        float HalfPixelSize;
        int EnableConservativeRasterization;

        uint EncodeGbuffer(float4 value)
        {
            uint res = (uint(value.x * 255.0f) << 24) + (uint(value.y * 255.0f) << 16) + (uint(value.z * 255.0f) << 8) + uint(value.w * 255.0f);
            return res;
        }

        float4 DecodeGbuffer(uint value)
        {
            float4 res = float4(0.0f, 0.0f, 0.0f, 0.0f);
            res.w = (value & 255) / 255.0f;
            value = value >> 8;
            res.z = (value & 255) / 255.0f;
            value = value >> 8;
            res.y = (value & 255) / 255.0f;
            value = value >> 8;
            res.x = (value & 255) / 255.0f;
            return res;
        }

        void MovingAverage(uniform RWTexture3D<uint> outUav, int3 uvw, float4 val)
        {
            uint newVal = EncodeGbuffer(val);
            uint prevStoredVal = 0xFFFFFFFF;
            uint curStoredVal;
            InterlockedCompareExchange(outUav[uvw], prevStoredVal, newVal, curStoredVal);
            while (curStoredVal != prevStoredVal)
            {
                prevStoredVal = curStoredVal;
                float4 gbuffer = DecodeGbuffer(curStoredVal);
                gbuffer.w *= MOVING_AVERAGE_MAX;
                gbuffer.xyz = gbuffer.xyz * gbuffer.w;
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

        struct VoxelizationVsInput
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float2 uv : TEXCOORD0;
        };

        struct VoxelizationGsInput
        {
            float4 posH : POSITION;
            float4 posW : POSITION1;
            float2 uv : TEXCOORD0;
            float3 normal : NORMAL;
        };

        struct VoxelizationFsInput
        {
            float4 posH : SV_POSITION;
            float4 posW : POSITION1;
            float2 uv : TEXCOORD0;
            float3 normal : NORMAL;
            float4 aabb : TEXCOORD1;
        };

        VoxelizationGsInput VoxelizationVs(VoxelizationVsInput v)
        {
            VoxelizationGsInput o;
            o.posW = mul(UNITY_MATRIX_M, v.vertex);
            o.uv = v.uv;
            o.normal = TransformObjectToWorldNormal(v.normal);
            if (abs(o.normal.x) > abs(o.normal.y))
            {
                if (abs(o.normal.x) > abs(o.normal.z))
                {
                    o.posH = mul(VoxelizationRightVP, o.posW);
                }
                else
                {
                    o.posH = mul(VoxelizationForwardVP, o.posW);
                }
            }
            else
            {
                if (abs(o.normal.z) > abs(o.normal.y))
                {
                    o.posH = mul(VoxelizationForwardVP, o.posW);
                }
                else
                {
                    o.posH = mul(VoxelizationUpVP, o.posW);
                }
            }
            return o;
        }

        [maxvertexcount(3)]
        void VoxelizationGs(triangle VoxelizationGsInput i[3], inout TriangleStream<VoxelizationFsInput> triStream)
        {
            int j;
            if (EnableConservativeRasterization == 0)
            {
                for (j = 0; j < 3; j++)
                {
                    VoxelizationFsInput o = (VoxelizationFsInput)0;
                    o.posH = i[j].posH;
                    o.posW = i[j].posW;
                    o.uv = i[j].uv;
                    o.normal = i[j].normal;
                    triStream.Append(o);
                }
                return;
            }

            float4 vertex[3];
            float2 texCoord[3];
            for (j = 0; j < 3; ++j)
            {
                vertex[j] = i[j].posH / i[j].posH.w;
                texCoord[j] = i[j].uv;
            }

            float3 clipTriangleNormal = normalize(cross(vertex[2].xyz - vertex[0].xyz, vertex[1].xyz - vertex[0].xyz));
            if (clipTriangleNormal.z > 0.0f)
            {
                float4 tempVertex = vertex[2];
                float2 tempTexC = texCoord[2];
                vertex[2] = vertex[1];
                vertex[1] = tempVertex;
                texCoord[2] = texCoord[1];
                texCoord[1] = tempTexC;
            }

            float4 trianglePlane;
            trianglePlane.xyz = normalize(cross(vertex[2].xyz - vertex[0].xyz, vertex[1].xyz - vertex[0].xyz));
            trianglePlane.w = -dot(vertex[0].xyz, trianglePlane.xyz);
            if (trianglePlane.z > 0.001f)
            {
                return;
            }

            float4 aabb = float4(1.0f, 1.0f, -1.0f, -1.0f);
            for (j = 0; j < 3; j++)
            {
                aabb.xy = min(aabb.xy, vertex[j].xy);
                aabb.zw = max(aabb.zw, vertex[j].xy);
            }
            aabb += float4(-HalfPixelSize.xx, HalfPixelSize.xx);

            float3 plane[3];
            for (j = 0; j < 3; j++)
            {
                plane[j] = cross(vertex[(j + 2) % 3].xyw, vertex[(j + 1) % 3].xyw);
                plane[j].z -= dot(HalfPixelSize.xx, abs(plane[j].xy));
            }

            float3 intersect[3];
            for (j = 0; j < 3; j++)
            {
                intersect[j] = cross(plane[(j + 1) % 3], plane[(j + 2) % 3]);
                if (intersect[j].z != 0.0f)
                {
                    intersect[j] /= intersect[j].z;
                }
            }

            for (j = 0; j < 3; j++)
            {
                vertex[j].xyz = intersect[j];
                vertex[j].w = 1.0f;
                vertex[j].z = -(trianglePlane.x * intersect[j].x + trianglePlane.y * intersect[j].y + trianglePlane.w) / trianglePlane.z;
            }

            [unroll]
            for (j = 0; j < 3; j++)
            {
                VoxelizationFsInput o = (VoxelizationFsInput)0;
                o.posH = vertex[j];
                o.posW = i[j].posW;
                o.uv = texCoord[j];
                o.normal = i[j].normal;
                o.aabb = aabb;
                triStream.Append(o);
            }
        }

        half4 VoxelizationFs(VoxelizationFsInput i) : SV_Target
        {
            if (EnableConservativeRasterization != 0)
            {
                float2 inputPos = i.posH.xy;
                inputPos /= VoxelTextureResolution;
                inputPos = inputPos * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f);
                if ((inputPos.x < i.aabb.x || inputPos.y < i.aabb.y || inputPos.x > i.aabb.z || inputPos.y > i.aabb.w))
                {
                    discard;
                }
            }

            float4 albedo = SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, i.uv, 0) * _BaseColor;
            i.normal = (i.normal + float3(1.0f, 1.0f, 1.0f)) * 0.5f;
            float emissiveMask = SAMPLE_TEXTURE2D_LOD(_VoxelEmissiveMask, sampler_VoxelEmissiveMask, i.uv, 0).r;
            float emissiveValue = saturate(emissiveMask) * saturate(_VoxelEmissiveIntensity / 5.0f);
            float4 normalA = float4(i.normal, albedo.a);
            float4 posV = mul(WorldToVoxel, i.posW);
            int3 uvw = int3(posV.xyz);
            MovingAverage(OutAlbedo, uvw, albedo);
            MovingAverage(OutNormal, uvw, normalA);
            ScalarMax(OutTmpOpacityAccum, uvw, albedo.a);
            ScalarMax(OutTmpEmissiveAccum, uvw, emissiveValue);
            return half4(1.0h, 0.0h, 0.0h, 1.0h);
        }

        // ==========================================================================================================
        // VoxelGI_Shadow Pass Functions
        // ==========================================================================================================
        float4x4 WorldToShadowVP;
        float4x4 WorldToShadowVPLinear;

        struct ShadowVsInput
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float2 texcoord : TEXCOORD0;
        };

        struct ShadowFsInput
        {
            float4 vertex : SV_POSITION;
            float depth : TEXCOORD0;
        };

        ShadowFsInput ShadowVs(ShadowVsInput v)
        {
            ShadowFsInput o;
            float4 posW = mul(UNITY_MATRIX_M, v.vertex);
            o.vertex = mul(WorldToShadowVP, posW);
            float4 linearClip = mul(WorldToShadowVPLinear, posW);
            o.depth = 1.0f - (linearClip.z / linearClip.w * 0.5f + 0.5f);
            return o;
        }

        float ShadowFs(ShadowFsInput i) : SV_Target
        {
            return i.depth;
        }
        ENDHLSL

        // --------------------------------------------------------------------------------------------------------------
        // Passes
        // --------------------------------------------------------------------------------------------------------------

        Pass
        {
            Name "ForwardLit"
            Tags
            {
                "LightMode" = "UniversalForward"
            }
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles gles3 glcore
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma vertex ForwardVertex
            #pragma fragment ForwardFragment
            ENDHLSL
        }

        // 体素化Pass
        Pass
        {
            Name "VoxelGI_Voxelization"
            Tags
            {
                "LightMode" = "VoxelGI_Voxelization"
            }
            Cull Off
            ZWrite Off
            ZTest Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma require geometry
            #pragma enable_d3d11_debug_symbols
            #pragma vertex VoxelizationVs
            #pragma geometry VoxelizationGs
            #pragma fragment VoxelizationFs
            ENDHLSL
        }

        // 体素阴影Pass
        Pass
        {
            Name "VoxelGI_Shadow"
            Tags
            {
                "LightMode" = "VoxelGI_Shadow"
            }
            Cull Back
            ZWrite Off
            ZTest Off
            BlendOp Max
            Blend One One

            HLSLPROGRAM
            #pragma target 5.0
            #pragma enable_d3d11_debug_symbols
            #pragma vertex ShadowVs
            #pragma fragment ShadowFs
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }
            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode" = "DepthNormals"
            }
            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma vertex DepthVertex
            #pragma fragment DepthNormalsFragment
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
