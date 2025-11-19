using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class MirrorToWebRTCFeature : ScriptableRendererFeature
{
    [Header("Output Texture (used by WebRTC) - should simply mirror what main camera sees")]
    public RenderTexture webrtcRenderTexture;

    private MirrorToWebRTCPass _mirrorPass;

    public override void Create()
    {
        _mirrorPass = new MirrorToWebRTCPass("Mirror To WebRTC");
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_mirrorPass == null || webrtcRenderTexture == null)
        {
            return;
        }

        var camera = renderingData.cameraData.camera;
        if (camera == null || camera != Camera.main)
        {
            return;
        }

        if (!webrtcRenderTexture.IsCreated())
        {
            webrtcRenderTexture.Create();
        }

        _mirrorPass.Setup(webrtcRenderTexture);
        renderer.EnqueuePass(_mirrorPass);
    }

    private class MirrorToWebRTCPass : ScriptableRenderPass
    {
        private readonly string _profilerTag;
        private RenderTexture _targetTexture;

        public MirrorToWebRTCPass(string profilerTag)
        {
            _profilerTag = profilerTag;
            renderPassEvent = RenderPassEvent.AfterRendering;
        }

        public void Setup(RenderTexture targetTexture)
        {
            _targetTexture = targetTexture;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_targetTexture == null || !_targetTexture.IsCreated())
            {
                return;
            }

            var camera = renderingData.cameraData.camera;
            if (camera == null || camera != Camera.main)
            {
                return;
            }

            var renderer = renderingData.cameraData.renderer;
            if (renderer == null)
            {
                return;
            }

            var cmd = CommandBufferPool.Get(_profilerTag);
            var sourceIdentifier = renderer.cameraColorTargetHandle.nameID;
            cmd.Blit(sourceIdentifier, new RenderTargetIdentifier(_targetTexture));
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
