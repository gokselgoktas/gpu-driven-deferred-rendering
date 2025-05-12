using System;
using System.Runtime.InteropServices;

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public sealed class GPUDrivenDeferredRenderingFeature : ScriptableRendererFeature
{
    private sealed class RenderPass : ScriptableRenderPass
    {
        [Serializable]
        private struct Vertex
        {
            internal Vector3 Position;
            internal Vector4 Color;
        }

        private sealed class ComputePassData
        {
            internal ComputeShader ComputeShader;

            internal BufferHandle VertexBufferHandle;
            internal BufferHandle IndexBufferHandle;

            internal BufferHandle IndirectArgumentsBufferHandle;
        }

        private sealed class RasterRenderPassData
        {
            internal BufferHandle VertexBufferHandle;
            internal BufferHandle IndexBufferHandle;

            internal BufferHandle IndirectArgumentsBufferHandle;

            internal Material Material;
        }

        private static readonly int _vertexBufferID = Shader.PropertyToID("_VERTEX_BUFFER");
        private static readonly int _indexBufferID = Shader.PropertyToID("_INDEX_BUFFER");

        private static readonly int _indirectArgumentsBufferID = Shader.PropertyToID("_INDIRECT_ARGUMENTS");

        private ComputeShader _computeShader;

        private GraphicsBuffer _vertexBuffer;
        private GraphicsBuffer _indexBuffer;

        private GraphicsBuffer _indirectArgumentsBuffer;

        private Material _material;

        public RenderPass()
        {
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += OnAssemblyReload;
#endif

            _vertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, Marshal.SizeOf(typeof(Vertex)));
            _indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, sizeof(uint));

            _indirectArgumentsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1,
                GraphicsBuffer.IndirectDrawIndexedArgs.size);
        }

        ~RenderPass()
        {
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= OnAssemblyReload;
#endif

            Dispose();
        }

        private void AddComputePass(RenderGraph renderGraph)
        {
            if (_vertexBuffer == null || _indexBuffer == null || _indirectArgumentsBuffer == null)
                return;

            if (_computeShader == null)
            {
                _computeShader = (ComputeShader)Resources.Load("Shaders/GPUDrivenBufferGeneration");

                if (_computeShader == null)
                {
                    Debug.LogError("GPUDrivenBufferGeneration.compute not found.");
                    return;
                }

                return;
            }

            using var builder =
                renderGraph.AddComputePass<ComputePassData>("GPU-Driven Buffer Generation", out var computePassData);

            computePassData.ComputeShader = _computeShader;

            computePassData.VertexBufferHandle = renderGraph.ImportBuffer(_vertexBuffer);
            builder.UseBuffer(computePassData.VertexBufferHandle, AccessFlags.Write);

            computePassData.IndexBufferHandle = renderGraph.ImportBuffer(_indexBuffer);
            builder.UseBuffer(computePassData.IndexBufferHandle, AccessFlags.Write);

            computePassData.IndirectArgumentsBufferHandle = renderGraph.ImportBuffer(_indirectArgumentsBuffer);
            builder.UseBuffer(computePassData.IndirectArgumentsBufferHandle, AccessFlags.Write);

            builder.SetRenderFunc(
                (ComputePassData computePassData, ComputeGraphContext computeGraphContext) =>
                {
                    var cmd = computeGraphContext.cmd;
                    var computeShader = computePassData.ComputeShader;

                    cmd.SetComputeBufferParam(computeShader, 0, _vertexBufferID, computePassData.VertexBufferHandle);
                    cmd.SetComputeBufferParam(computeShader, 0, _indexBufferID, computePassData.IndexBufferHandle);

                    cmd.SetComputeBufferParam(computeShader, 0, _indirectArgumentsBufferID,
                        computePassData.IndirectArgumentsBufferHandle);

                    cmd.DispatchCompute(computePassData.ComputeShader, 0, 1, 1, 1);

                    cmd.SetComputeBufferParam(computeShader, 1, _indirectArgumentsBufferID,
                        computePassData.IndirectArgumentsBufferHandle);

                    cmd.DispatchCompute(computeShader, 1, 1, 1, 1);
                }
            );
        }

        private void AddRasterRenderPass(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_vertexBuffer == null || _indexBuffer == null || _indirectArgumentsBuffer == null)
                return;

            if (_material == null)
            {
                var shader = Shader.Find("Hidden/Universal Render Pipeline/GPU-Driven Deferred Rendering");

                if (shader == null || shader.isSupported == false)
                {
                    Debug.LogError("GPUDrivenDeferredRendering.shader not found or not supported");
                    return;
                }

                _material = CoreUtils.CreateEngineMaterial(shader);

                if (_material == null)
                {
                    Debug.LogError("Could not create Material");
                    return;
                }
            }

            using var builder = renderGraph.AddRasterRenderPass<RasterRenderPassData>(
                "GPU-Driven Deferred Rendering", out var rasterRenderPassData);

            var universalResourceData = frameData.Get<UniversalResourceData>();
            var gBuffer = universalResourceData.gBuffer;

            for (int i = 0; i < gBuffer.Length; ++i)
                builder.SetRenderAttachment(gBuffer[i], i);

            builder.SetRenderAttachmentDepth(universalResourceData.cameraDepth);

            rasterRenderPassData.VertexBufferHandle = renderGraph.ImportBuffer(_vertexBuffer);
            rasterRenderPassData.IndexBufferHandle = renderGraph.ImportBuffer(_indexBuffer);

            rasterRenderPassData.IndirectArgumentsBufferHandle = renderGraph.ImportBuffer(_indirectArgumentsBuffer);

            rasterRenderPassData.Material = _material;

            builder.UseBuffer(rasterRenderPassData.VertexBufferHandle, AccessFlags.Read);
            builder.UseBuffer(rasterRenderPassData.IndexBufferHandle, AccessFlags.Read);

            builder.UseBuffer(rasterRenderPassData.IndirectArgumentsBufferHandle, AccessFlags.Read);

            builder.SetRenderFunc(
                (RasterRenderPassData rasterRenderPassData, RasterGraphContext rasterGraphContext) =>
                {
                    var cmd = rasterGraphContext.cmd;
                    var material = rasterRenderPassData.Material;

                    material.SetBuffer(_vertexBufferID, rasterRenderPassData.VertexBufferHandle);

                    cmd.DrawProceduralIndirect(rasterRenderPassData.IndexBufferHandle, Matrix4x4.identity, material, 0,
                        MeshTopology.Triangles, rasterRenderPassData.IndirectArgumentsBufferHandle, 0);
                }
            );
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            AddComputePass(renderGraph);
            AddRasterRenderPass(renderGraph, frameData);
        }

#if UNITY_EDITOR
        private void OnAssemblyReload()
        {
            Dispose();
        }
#endif

        private void Dispose()
        {
            _vertexBuffer?.Release();
            _indexBuffer?.Release();

            _indirectArgumentsBuffer?.Release();

            DestroyImmediate(_material);

            _vertexBuffer = null;
            _indexBuffer = null;

            _indirectArgumentsBuffer = null;

            _material = null;
        }
    }

    private RenderPass _renderPass;

    public override void Create()
    {
        _renderPass = new()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingGbuffer,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer scriptableRenderer, ref RenderingData renderingData)
    {
        scriptableRenderer.EnqueuePass(_renderPass);
    }
}
