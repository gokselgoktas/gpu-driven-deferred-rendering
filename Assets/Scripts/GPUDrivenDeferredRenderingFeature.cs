using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

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

        private static readonly int _vertexBufferID = Shader.PropertyToID("_VERTEX_BUFFER");
        private static readonly int _indexBufferID = Shader.PropertyToID("_INDEX_BUFFER");

        private static readonly int _indirectArgumentsBufferID = Shader.PropertyToID("_INDIRECT_ARGUMENTS");

        private ComputeShader _computeShader;

        private GraphicsBuffer _vertexBuffer;
        private GraphicsBuffer _indexBuffer;

        private GraphicsBuffer _indirectArgumentsBuffer;

        private Material _material;

        private UniversalRenderer _universalRenderer;

        private object _deferredLights;

        private RTHandle[] _gBufferAttachments;
        private RTHandle _depthAttachment;

        public RenderPass()
        {
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += OnAssemblyReload;
#endif
        }

        ~RenderPass()
        {
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= OnAssemblyReload;
#endif

            Dispose();
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            if (_computeShader == null)
            {
                _computeShader = (ComputeShader)Resources.Load("Shaders/GPUDrivenBufferGeneration");

                if (_computeShader == null)
                {
                    Debug.LogError("GPUDrivenBufferGeneration.compute not found");
                    return;
                }
            }

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

            _vertexBuffer ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, Marshal.SizeOf(typeof(Vertex)));
            _indexBuffer ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, sizeof(uint));

            _indirectArgumentsBuffer ??= new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1,
                GraphicsBuffer.IndirectDrawIndexedArgs.size);
        }

        private void ConfigureGBuffer(ScriptableRenderer scriptableRenderer)
        {
            if (_universalRenderer != scriptableRenderer)
            {
                if (scriptableRenderer is not UniversalRenderer universalRenderer)
                {
                    Debug.LogError("ScriptableRenderer is not UniversalRenderer");
                    return;
                }

                _universalRenderer = universalRenderer;
            }

            if (_universalRenderer == null)
            {
                Debug.LogError("UniversalRenderer is null");
                return;
            }

            if (_deferredLights == null)
            {
                var field = typeof(UniversalRenderer).GetField("m_DeferredLights",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (field == null)
                {
                    Debug.LogError("UniversalRenderer.m_DeferredLights not found");
                    return;
                }

                _deferredLights = field.GetValue(_universalRenderer);
            }

            if (_deferredLights == null)
            {
                Debug.LogError("UniversalRenderer.m_DeferredLights is null");
                return;
            }

            if (_gBufferAttachments == null)
            {
                var type = _deferredLights.GetType();
                var property = type.GetProperty("GbufferAttachments", BindingFlags.NonPublic | BindingFlags.Instance);

                if (property == null)
                {
                    Debug.LogError("DeferredLights.GbufferAttachments not found");
                    return;
                }

                _gBufferAttachments = property.GetValue(_deferredLights) as RTHandle[];
            }

            if (_depthAttachment?.referenceSize != _gBufferAttachments?.First()?.referenceSize)
                _depthAttachment = null;

            if (_depthAttachment == null)
            {
                var type = _deferredLights.GetType();
                var property = type.GetProperty("DepthAttachment", BindingFlags.NonPublic | BindingFlags.Instance);

                if (property == null)
                {
                    Debug.LogError("DeferredLights.DepthAttachment not found");
                    return;
                }

                _depthAttachment = property.GetValue(_deferredLights) as RTHandle;
            }

            ConfigureTarget(_gBufferAttachments, _depthAttachment);
        }

        public override void Execute(ScriptableRenderContext scriptableRenderContext, ref RenderingData renderingData)
        {
            if (_computeShader == null || _vertexBuffer == null || _indirectArgumentsBuffer == null)
                return;

            var cmd = CommandBufferPool.Get("GPU-Driven Rendering");
            var scriptableRenderer = renderingData.cameraData.renderer;

            cmd.SetComputeBufferParam(_computeShader, 0, _vertexBufferID, _vertexBuffer);
            cmd.SetComputeBufferParam(_computeShader, 0, _indexBufferID, _indexBuffer);

            cmd.DispatchCompute(_computeShader, 0, 1, 1, 1);

            cmd.SetComputeBufferParam(_computeShader, 1, _indirectArgumentsBufferID, _indirectArgumentsBuffer);

            cmd.DispatchCompute(_computeShader, 1, 1, 1, 1);

            ConfigureGBuffer(scriptableRenderer);

            _material.SetBuffer(_vertexBufferID, _vertexBuffer);

            cmd.DrawProceduralIndirect(_indexBuffer, Matrix4x4.identity, _material, 0, MeshTopology.Triangles,
                _indirectArgumentsBuffer, 0);

            scriptableRenderContext.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
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
            _vertexBuffer = null;

            _indexBuffer?.Release();
            _indexBuffer = null;

            _indirectArgumentsBuffer?.Release();
            _indirectArgumentsBuffer = null;

            if (_material != null)
            {
                CoreUtils.Destroy(_material);
                _material = null;
            }
        }
    }

    private RenderPass _renderPass;

    public override void Create()
    {
        _renderPass = new()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingGbuffer
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        _ = renderingData;

        renderer.EnqueuePass(_renderPass);
    }
}
