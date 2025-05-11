Shader "Hidden/Universal Render Pipeline/GPU-Driven Deferred Rendering"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Unlit.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityGBuffer.hlsl"

    struct Vertex
    {
        float3 position;
        float4 color;
    };

    struct Varyings
    {
        float4 position : SV_POSITION;
        float2 uv : TEXCOORD0;

        float4 color: COLOR;
    };

    StructuredBuffer<Vertex> _VERTEX_BUFFER;

    Varyings project(in uint id : SV_VertexID)
    {
        Vertex vertex = _VERTEX_BUFFER[id];

        Varyings output;

        output.position = TransformObjectToHClip(vertex.position.xyz);
        output.uv = vertex.position.xy;

        output.color = vertex.color;

        return output;
    }

    FragmentOutput render(in Varyings varyings)
    {
        FragmentOutput output;

        output.GBuffer0.rgb = varyings.color.rgb;
        output.GBuffer0.a = 0.0;

        output.GBuffer1.rgb = 1.0;
        output.GBuffer1.a = 1.0;

        output.GBuffer2.rgb = PackNormal(float3(0.0, 0.0, 1.0));
        output.GBuffer2.a = 0.5;

        output.GBuffer3 = float4(varyings.color.rgb /* * bakedGICoefficient + emission */, 1.0);

#if _RENDER_PASS_ENABLED
        output.GBuffer4 = varyings.position.z;
#endif

#if OUTPUT_SHADOWMASK
    output.GBUFFER_SHADOWMASK = inputData.shadowMask; // will have unity_ProbesOcclusion value if subtractive lighting is used (baked)
#endif

#ifdef _WRITE_RENDERING_LAYERS
    uint renderingLayers = GetMeshRenderingLayer();
    output.GBUFFER_LIGHT_LAYERS = float4(EncodeMeshRenderingLayer(renderingLayers), 0.0, 0.0, 0.0);
#endif

        return output;
    }
    ENDHLSL

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalRenderPipeline"
            "RenderType" = "Opaque"
        }

        Cull Off

        Pass
        {
            Tags
            {
                "LightMode" = "UniversalGBuffer"
            }

            HLSLPROGRAM
            #pragma target 5.0

            #pragma vertex project
            #pragma fragment render
            ENDHLSL
        }
    }
}
