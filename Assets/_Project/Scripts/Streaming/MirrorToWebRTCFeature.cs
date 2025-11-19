using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class MirrorToWebRTCFeature : ScriptableRendererFeature
{
    [Header("Output Texture (Used by WebRTC)")]
    public RenderTexture webrtcRenderTexture;

    [Header("Performance Settings")]
    [Tooltip("How many frames to skip between blits. Higher values reduce performance impact but may make streaming less smooth.")]
    public int frameSkipCount = 3;

    private MirrorToWebRTCPass _pass;
    private RTHandle _destinationHandle;
    private int _frameCounter;

    // ------------------------------------------------
    // 1) The Pass
    // ------------------------------------------------
    class MirrorToWebRTCPass : ScriptableRenderPass
    {
        private RTHandle _sourceHandle;
        private RTHandle _destinationHandle;
        private int _frameSkipCount;
        private int _frameCounter;

        public MirrorToWebRTCPass(RTHandle destination, int frameSkipCount)
        {
            _destinationHandle = destination;
            _frameSkipCount = frameSkipCount;
            _frameCounter = 0;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            // We grab the final camera target handle each frame
            _sourceHandle = renderingData.cameraData.renderer.cameraColorTargetHandle;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_sourceHandle == null || _destinationHandle == null)
                return;

            // Only blit every N frames
            if (_frameCounter % _frameSkipCount != 0)
            {
                _frameCounter++;
                return;
            }
            _frameCounter++;

            CommandBuffer cmd = CommandBufferPool.Get("BlitToWebRTC");

            // Recommended URP-friendly blit call
            Blitter.BlitCameraTexture(cmd, _sourceHandle, _destinationHandle);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // We do NOT release _destinationHandle here 
            // because it's a persistent user asset
            _sourceHandle = null;
        }
    }

    // ------------------------------------------------
    // 2) Create / Add Pass
    // ------------------------------------------------
    public override void Create()
    {
        // If you have not assigned it in inspector, bail out
        if (webrtcRenderTexture == null)
        {
            Debug.LogError("WebRTC RenderTexture not assigned in MirrorToWebRTCFeature!");
            return;
        }

        // Log RenderTexture configuration
        Debug.Log($"WebRTC RenderTexture configuration: {webrtcRenderTexture.width}x{webrtcRenderTexture.height}, " +
                  $"format: {webrtcRenderTexture.format}, depth: {webrtcRenderTexture.depth}, " +
                  $"anti-aliasing: {webrtcRenderTexture.antiAliasing}");

        // Create a permanent RTHandle for the user's RenderTexture
        _destinationHandle = RTHandles.Alloc(webrtcRenderTexture);

        // Create our pass and set it to run after the camera finishes
        _pass = new MirrorToWebRTCPass(_destinationHandle, frameSkipCount)
        {
            renderPassEvent = RenderPassEvent.AfterRendering
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // If there's no user texture assigned, do nothing
        if (webrtcRenderTexture == null) return;

        // Enqueue the pass
        renderer.EnqueuePass(_pass);
    }

    // Optionally, if your feature might be disabled/unloaded, 
    // you could release the handle here, BUT only if you're sure
    // you don't need that persistent RT asset at runtime:
    /*
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (_destinationHandle != null)
        {
            _destinationHandle.Release();
            _destinationHandle = null;
        }
    }
    */
}
