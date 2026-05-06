Shader "VoxelGI/VoxelShadowOnly"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "Queue" = "Geometry" "IgnoreProjector" = "True" "RenderType" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        LOD 200

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
        };

        struct InvisibleVaryings
        {
            float4 positionCS : SV_POSITION;
        };

        struct VoxelShadowVaryings
        {
            float4 positionCS : SV_POSITION;
            float depth : TEXCOORD0;
        };

        float4x4 WorldToShadowVP;
        float4x4 WorldToShadowVPLinear;

        InvisibleVaryings InvisibleVert(Attributes input)
        {
            InvisibleVaryings output;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            return output;
        }

        half4 InvisibleFrag(InvisibleVaryings input) : SV_Target
        {
            return 0;
        }

        VoxelShadowVaryings VoxelShadowVert(Attributes input)
        {
            VoxelShadowVaryings output;
            float4 posW = mul(UNITY_MATRIX_M, input.positionOS);
            output.positionCS = mul(WorldToShadowVP, posW);
            float4 linearClip = mul(WorldToShadowVPLinear, posW);
            output.depth = 1.0 - (linearClip.z / linearClip.w * 0.5 + 0.5);
            return output;
        }

        float VoxelShadowFrag(VoxelShadowVaryings input) : SV_Target
        {
            return input.depth;
        }
        ENDHLSL

        Pass
        {
            Name "Invisible"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite Off
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex InvisibleVert
            #pragma fragment InvisibleFrag
            ENDHLSL
        }

        Pass
        {
            Name "VoxelGI_Shadow"
            Tags { "LightMode" = "VoxelGI_Shadow" }
            Cull Back
            ZWrite Off
            ZTest Off
            BlendOp Max
            Blend One One

            HLSLPROGRAM
            #pragma target 5.0
            #pragma enable_d3d11_debug_symbols
            #pragma vertex VoxelShadowVert
            #pragma fragment VoxelShadowFrag
            ENDHLSL
        }
    }
    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
