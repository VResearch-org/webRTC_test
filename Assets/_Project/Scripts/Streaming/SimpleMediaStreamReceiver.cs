using System.Collections;
using Unity.WebRTC;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using System;
using TMPro;
using System.Linq;
using System.Reflection;

public class SimpleMediaStreamReceiver : MonoBehaviour
{
    [SerializeField] private RawImage receiveImage;
    [SerializeField] private TextMeshProUGUI statsText;

    private string RoomId => UserManager.userName;

    private RTCPeerConnection connection;
    private NetcodeWebRTCSignaling signaling;
    private bool offerReceived = false;
    private SessionDescription remoteOffer;

    // Audio streaming
    private AudioStreamTrack audioStreamTrack; // Receiver's microphone audio
    private AudioSource microphoneAudioSource; // AudioSource for microphone capture
    private RTCRtpSender audioSender; // Track the audio sender for dynamic enable/disable
    private AudioSource receivedAudioSource; // Audio source for playing received audio
    private bool receiverAudioEnabled = false; // Track receiver audio state

    // Statistics tracking
    private int frameCount = 0;
    private int totalFramesReceived = 0;
    private float frameCountTime = 0f;
    private float currentFramerate = 0f;
    private RTCPeerConnectionState connectionState = RTCPeerConnectionState.New;
    private RTCIceConnectionState iceConnectionState = RTCIceConnectionState.New;
    private long bytesReceived = 0;
    private long previousBytesReceived = 0;
    private float bandwidthKbps = 0f;
    private float rttMs = 0f;
    private int videoWidth = 0;
    private int videoHeight = 0;
    private Coroutine statsUpdateCoroutine;
    private VideoStreamTrack currentVideoTrack;
    private Texture previousTexture = null;
    private ulong previousTextureNativePtr = 0;
    private float lastFrameTime = 0f;
    private float[] frameTimeHistory = new float[120]; // Track last 120 frame times
    private int frameTimeIndex = 0;
    private int validFrameTimeCount = 0;
    private float lastStatsUpdateTime = 0f;

    void Start()
    {
        frameCountTime = Time.time;
        // Wait for Netcode to be ready
        StartCoroutine(WaitForNetcodeAndInitialize());
    }

    private IEnumerator WaitForNetcodeAndInitialize()
    {
        // Wait for NetworkManager to be available
        while (NetworkManager.Singleton == null)
        {
            yield return null;
        }

        // Wait for signaling component
        while (NetcodeWebRTCSignaling.Instance == null || !NetcodeWebRTCSignaling.Instance.IsReady())
        {
            yield return null;
        }

        signaling = NetcodeWebRTCSignaling.Instance;
        InitializeConnection();
    }

    private void InitializeConnection()
    {
        try
        {

            var config = new RTCConfiguration
            {
                iceServers = WebRTCConfig.IceServers.ToArray(),
                iceTransportPolicy = RTCIceTransportPolicy.All,
                bundlePolicy = RTCBundlePolicy.BundlePolicyMaxCompat,
                iceCandidatePoolSize = 10
            };

            connection = new RTCPeerConnection(ref config);

            connection.OnIceCandidate = candidate =>
            {
                try
                {
                    if (signaling != null && signaling.IsReady())
                    {
                        signaling.SendIceCandidate(
                            candidate.Candidate,
                            candidate.SdpMid,
                            candidate.SdpMLineIndex ?? 0,
                            false // isSender = false (receiver)
                        );
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error handling ICE candidate: {e.Message}");
                }
            };

            connection.OnIceConnectionChange = state =>
            {
                Debug.Log($"ICE state changed to: {state}");
                iceConnectionState = state;
            };

            connection.OnConnectionStateChange = state =>
            {
                Debug.Log($"Connection state changed to: {state}");
                connectionState = state;
            };

            connection.OnTrack = e =>
            {
                try
                {
                    if (e.Track is VideoStreamTrack video)
                    {
                        currentVideoTrack = video;
                        video.OnVideoReceived += tex =>
                        {
                            if (receiveImage != null)
                            {
                                receiveImage.texture = tex;
                            }
                            
                            // Track framerate - count every frame received
                            frameCount++;
                            totalFramesReceived++;
                            float currentTime = Time.time;
                            
                            if (lastFrameTime > 0)
                            {
                                float frameDelta = currentTime - lastFrameTime;
                                if (frameDelta > 0 && frameDelta < 1.0f) // Only count reasonable frame times
                                {
                                    frameTimeHistory[frameTimeIndex] = frameDelta;
                                    frameTimeIndex = (frameTimeIndex + 1) % frameTimeHistory.Length;
                                    if (validFrameTimeCount < frameTimeHistory.Length)
                                        validFrameTimeCount++;
                                }
                            }
                            lastFrameTime = currentTime;
                            
                            if (tex != null)
                            {
                                videoWidth = tex.width;
                                videoHeight = tex.height;
                                // Track texture by native pointer to detect actual updates
                                try
                                {
                                    IntPtr nativePtr = tex.GetNativeTexturePtr();
                                    previousTextureNativePtr = (ulong)nativePtr.ToInt64();
                                }
                                catch { }
                            }
                        };
                        
                        // Start statistics collection once we have a track
                        if (statsUpdateCoroutine == null)
                        {
                            statsUpdateCoroutine = StartCoroutine(UpdateStatistics());
                        }
                    }
                    else if (e.Track is AudioStreamTrack audio)
                    {
                        // Play audio from sender (client)
                        if (receivedAudioSource == null)
                        {
                            receivedAudioSource = gameObject.AddComponent<AudioSource>();
                            receivedAudioSource.loop = false;
                            receivedAudioSource.playOnAwake = false;
                        }
                        receivedAudioSource.SetTrack(audio);
                        receivedAudioSource.Play();
                        Debug.Log("[SimpleMediaStreamReceiver] Received audio from sender");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error handling track: {ex.Message}");
                }
            };

            StartCoroutine(WebRTC.Update());

            SetupSignalingListeners();
        }
        catch (Exception e)
        {
            Debug.LogError($"Error initializing connection: {e.Message}");
        }
    }

    private void SetupSignalingListeners()
    {
        if (signaling == null) return;

        // Check if offer already exists
        if (signaling.HasOffer() && !offerReceived)
        {
            try
            {
                var json = signaling.GetCurrentOffer();
                remoteOffer = SessionDescription.FromJSON(json);
                offerReceived = true;
                if (receiveImage != null)
                {
                    receiveImage.gameObject.SetActive(true);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Receiver] Error parsing initial offer: {e.Message}");
            }
        }

        // Listen for new offers
        signaling.OnOfferReceived += (json) =>
        {
            if (!offerReceived)
            {
                try
                {
                    remoteOffer = SessionDescription.FromJSON(json);
                    offerReceived = true;
                    if (receiveImage != null)
                    {
                        receiveImage.gameObject.SetActive(true);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error processing offer: {e.Message}");
                }
            }
        };

        // Listen for ICE candidates from sender
        signaling.OnIceCandidateReceived += (candidate, sdpMid, sdpMLineIndex) =>
        {
            try
            {
                var init = new RTCIceCandidateInit
                {
                    sdpMid = sdpMid,
                    sdpMLineIndex = sdpMLineIndex,
                    candidate = candidate
                };
                connection.AddIceCandidate(new RTCIceCandidate(init));
            }
            catch (Exception e)
            {
                Debug.LogError($"Error processing ICE candidate: {e.Message}");
            }
        };
    }

    void Update()
    {
        if (offerReceived)
        {
            offerReceived = false;
            StartCoroutine(CreateAndSendAnswer());
        }
        
        // Update resolution from texture if available
        if (receiveImage != null && receiveImage.texture != null)
        {
            videoWidth = receiveImage.texture.width;
            videoHeight = receiveImage.texture.height;
        }
    }

    IEnumerator CreateAndSendAnswer()
    {
        RTCSessionDescription offerDesc = new RTCSessionDescription
        {
            type = RTCSdpType.Offer,
            sdp = remoteOffer.Sdp
        };

        var setRemoteOp = connection.SetRemoteDescription(ref offerDesc);
        yield return setRemoteOp;

        if (setRemoteOp.IsError)
        {
            Debug.LogError($"Error setting remote description: {setRemoteOp.Error.message}");
            yield break;
        }

        // Add receiver audio track if enabled (before creating answer)
        if (receiverAudioEnabled && audioStreamTrack == null)
        {
            // Create AudioSource for microphone capture
            if (microphoneAudioSource == null)
            {
                microphoneAudioSource = gameObject.AddComponent<AudioSource>();
                microphoneAudioSource.loop = true;
                microphoneAudioSource.playOnAwake = false;
                
                // Start microphone capture
                string microphoneName = Microphone.devices.Length > 0 ? Microphone.devices[0] : null;
                if (string.IsNullOrEmpty(microphoneName))
                {
                    Debug.LogError("[SimpleMediaStreamReceiver] No microphone device found");
                    yield break;
                }
                
                microphoneAudioSource.clip = Microphone.Start(microphoneName, true, 10, 48000);
                if (microphoneAudioSource.clip == null)
                {
                    Debug.LogError("[SimpleMediaStreamReceiver] Failed to start microphone");
                    yield break;
                }
                
                // Wait for microphone to start (yield outside try-catch)
                while (!(Microphone.GetPosition(microphoneName) > 0))
                {
                    yield return null;
                }
                
                microphoneAudioSource.Play();
            }
            
            // Create AudioStreamTrack from the AudioSource (in try-catch for safety)
            try
            {
                audioStreamTrack = new AudioStreamTrack(microphoneAudioSource);
                audioSender = connection.AddTrack(audioStreamTrack);
                Debug.Log("[SimpleMediaStreamReceiver] Receiver audio track added to answer");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SimpleMediaStreamReceiver] Failed to add audio track: {e.Message}");
            }
        }

        var answerOp = connection.CreateAnswer();
        yield return answerOp;

        if (answerOp.IsError)
        {
            Debug.LogError($"Error creating answer: {answerOp.Error.message}");
            yield break;
        }

        var answerDesc = answerOp.Desc;
        var setLocalOp = connection.SetLocalDescription(ref answerDesc);
        yield return setLocalOp;

        if (setLocalOp.IsError)
        {
            Debug.LogError($"Error setting local description: {setLocalOp.Error.message}");
            yield break;
        }

        try
        {
            var session = new SessionDescription
            {
                SessionType = answerDesc.type.ToString(),
                Sdp = answerDesc.sdp
            };

            if (signaling != null && signaling.IsReady())
            {
                signaling.SendAnswer(session.ConvertToJSON());
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error sending answer: {e.Message}");
        }
    }

    private IEnumerator UpdateStatistics()
    {
        // Wait a bit for connection to establish
        yield return new WaitForSeconds(1f);
        
        while (connection != null)
        {
            yield return new WaitForSeconds(0.5f); // Update every 0.5 seconds

            // Calculate framerate using multiple methods
            float deltaTime = Time.time - frameCountTime;
            if (deltaTime >= 1.0f)
            {
                if (frameCount > 0)
                {
                    currentFramerate = frameCount / deltaTime;
                }
                frameCount = 0;
                frameCountTime = Time.time;
            }
            
            // Calculate FPS from frame time history (more accurate and responsive)
            if (validFrameTimeCount > 0)
            {
                float avgFrameTime = 0f;
                int validFrames = 0;
                for (int i = 0; i < validFrameTimeCount; i++)
                {
                    int idx = (frameTimeIndex - validFrameTimeCount + i + frameTimeHistory.Length) % frameTimeHistory.Length;
                    if (frameTimeHistory[idx] > 0 && frameTimeHistory[idx] < 1.0f)
                    {
                        avgFrameTime += frameTimeHistory[idx];
                        validFrames++;
                    }
                }
                if (validFrames > 0)
                {
                    avgFrameTime /= validFrames;
                    if (avgFrameTime > 0.001f) // Avoid division by very small numbers
                    {
                        float calculatedFPS = 1f / avgFrameTime;
                        // Use the frame time history method if we have enough data (more accurate)
                        if (validFrames >= 5)
                        {
                            currentFramerate = calculatedFPS;
                        }
                        else if (currentFramerate <= 0 && validFrames > 0)
                        {
                            // Use it even with less data if we don't have a better estimate
                            currentFramerate = calculatedFPS;
                        }
                    }
                }
            }

            // Get WebRTC statistics
            if (connection != null)
            {
                var statsOp = connection.GetStats();
                yield return statsOp;

                if (!statsOp.IsError && statsOp.Value != null)
                {
                    try
                    {
                        var stats = statsOp.Value;
                        
                        // Debug: Log all stat types to see what's available
                        bool foundVideoStats = false;
                        
                        // Find inbound RTP stats for video
                        foreach (var stat in stats.Stats.Values)
                        {
                            try
                            {
                                var statType = stat.GetType();
                                string statName = statType.Name;
                                
                                // Try multiple property access methods
                                var allProps = statType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                                
                                // Check if it's an inbound RTP stream stats
                                if (statName.Contains("InboundRtp") || statName.Contains("RTCInboundRtp") || 
                                    statName.Contains("inbound-rtp") || statName.Contains("RTCInboundRTPStreamStats"))
                                {
                                    // Try to get mediaType property
                                    PropertyInfo mediaTypeProp = null;
                                    foreach (var prop in allProps)
                                    {
                                        if (prop.Name.Equals("mediaType", StringComparison.OrdinalIgnoreCase) ||
                                            prop.Name.Equals("MediaType", StringComparison.OrdinalIgnoreCase))
                                        {
                                            mediaTypeProp = prop;
                                            break;
                                        }
                                    }
                                    
                                    if (mediaTypeProp != null)
                                    {
                                        var mediaType = mediaTypeProp.GetValue(stat)?.ToString();
                                        if (mediaType != null && (mediaType == "video" || mediaType.Contains("video")))
                                        {
                                            foundVideoStats = true;
                                            
                                            // Get bytesReceived - try multiple property names and variations
                                            PropertyInfo bytesProp = null;
                                            string[] possibleNames = { "bytesReceived", "BytesReceived", "bytes_received", 
                                                                      "bytes", "Bytes", "totalBytesReceived", "TotalBytesReceived" };
                                            
                                            foreach (var propName in possibleNames)
                                            {
                                                foreach (var prop in allProps)
                                                {
                                                    if (prop.Name.Equals(propName, StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        bytesProp = prop;
                                                        break;
                                                    }
                                                }
                                                if (bytesProp != null) break;
                                            }
                                            
                                            if (bytesProp != null)
                                            {
                                                try
                                                {
                                                    var bytesValue = bytesProp.GetValue(stat);
                                                    if (bytesValue != null)
                                                    {
                                                        long bytes = 0;
                                                        if (bytesValue is long l) bytes = l;
                                                        else if (bytesValue is ulong ul) bytes = (long)ul;
                                                        else if (bytesValue is int i) bytes = i;
                                                        else if (bytesValue is uint ui) bytes = ui;
                                                        else if (bytesValue is double d) bytes = (long)d;
                                                        else if (bytesValue is float f) bytes = (long)f;
                                                        else if (!long.TryParse(bytesValue.ToString(), out bytes))
                                                        {
                                                            // Try as string with number
                                                            string str = bytesValue.ToString();
                                                            if (str.Contains("."))
                                                            {
                                                                if (double.TryParse(str, out double db)) bytes = (long)db;
                                                            }
                                                        }
                                                        
                                                        if (bytes > 0)
                                                        {
                                                            bytesReceived = bytes;
                                                            
                                                            // Calculate bandwidth
                                                            if (previousBytesReceived > 0)
                                                            {
                                                                long bytesDelta = bytesReceived - previousBytesReceived;
                                                                if (bytesDelta > 0)
                                                                {
                                                                    bandwidthKbps = (bytesDelta * 8) / 1000f / 0.5f;
                                                                }
                                                            }
                                                            previousBytesReceived = bytesReceived;
                                                        }
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    // Skip if we can't parse
                                                }
                                            }
                                            
                                            // Get RTT - try multiple property names
                                            PropertyInfo rttProp = null;
                                            foreach (var prop in allProps)
                                            {
                                                if (prop.Name.Equals("roundTripTime", StringComparison.OrdinalIgnoreCase) ||
                                                    prop.Name.Equals("RoundTripTime", StringComparison.OrdinalIgnoreCase) ||
                                                    prop.Name.Equals("rtt", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    rttProp = prop;
                                                    break;
                                                }
                                            }
                                            
                                            if (rttProp != null)
                                            {
                                                var rttValue = rttProp.GetValue(stat);
                                                if (rttValue != null)
                                                {
                                                    float rtt = 0f;
                                                    if (rttValue is float f) rtt = f;
                                                    else if (rttValue is double d) rtt = (float)d;
                                                    else float.TryParse(rttValue.ToString(), out rtt);
                                                    
                                                    if (rtt > 0) rttMs = rtt * 1000f;
                                                }
                                            }
                                        }
                                    }
                                }
                                
                                // Check for ICE candidate pair stats for RTT
                                if (statName.Contains("IceCandidatePair") || statName.Contains("RTCIceCandidatePair") ||
                                    statName.Contains("candidate-pair") || statName.Contains("RTCIceCandidatePairStats"))
                                {
                                    PropertyInfo rttProp = null;
                                    var allProps2 = statType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                                    foreach (var prop in allProps2)
                                    {
                                        if (prop.Name.Equals("currentRoundTripTime", StringComparison.OrdinalIgnoreCase) ||
                                            prop.Name.Equals("CurrentRoundTripTime", StringComparison.OrdinalIgnoreCase) ||
                                            prop.Name.Equals("rtt", StringComparison.OrdinalIgnoreCase))
                                        {
                                            rttProp = prop;
                                            break;
                                        }
                                    }
                                    
                                    if (rttProp != null)
                                    {
                                        var rttValue = rttProp.GetValue(stat);
                                        if (rttValue != null)
                                        {
                                            float rtt = 0f;
                                            if (rttValue is float f) rtt = f;
                                            else if (rttValue is double d) rtt = (float)d;
                                            else float.TryParse(rttValue.ToString(), out rtt);
                                            
                                            if (rtt > 0) rttMs = rtt * 1000f;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // Skip stats we can't parse
                                continue;
                            }
                        }
                        
                        // If we still don't have bandwidth, try estimating from texture size and framerate
                        if (bandwidthKbps <= 0 && currentFramerate > 0 && videoWidth > 0 && videoHeight > 0)
                        {
                            // Rough estimate: width * height * 3 bytes (RGB) * framerate * compression factor (0.1-0.3 for video codecs)
                            float estimatedBytesPerFrame = videoWidth * videoHeight * 3f * 0.15f; // Assume 15% compression
                            float estimatedBytesPerSecond = estimatedBytesPerFrame * currentFramerate;
                            bandwidthKbps = (estimatedBytesPerSecond * 8) / 1000f;
                        }
                    }
                    catch (Exception e)
                    {
                        // Silently handle stats errors to avoid spam
                        // Debug.LogWarning($"Error parsing stats: {e.Message}");
                    }
                }
            }

            // Update UI text
            UpdateStatsText();
        }
    }

    private void UpdateStatsText()
    {
        if (statsText == null) return;

        string stats = "=== Stream Statistics ===\n\n";
        
        stats += $"<b>Connection State:</b> {connectionState}\n";
        stats += $"<b>ICE State:</b> {iceConnectionState}\n";
        stats += $"<b>Latency (RTT):</b> {rttMs:F0} ms\n";
        stats += $"<b>Bandwidth:</b> {bandwidthKbps:F1} kbps\n";
        
        if (videoWidth > 0 && videoHeight > 0)
        {
            stats += $"<b>Resolution:</b> {videoWidth}x{videoHeight}\n";
        }

        statsText.text = stats;
    }

    private string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        else if (bytes < 1024 * 1024)
            return $"{bytes / 1024f:F2} KB";
        else
            return $"{bytes / (1024f * 1024f):F2} MB";
    }

    void OnDestroy()
    {
        if (statsUpdateCoroutine != null)
        {
            StopCoroutine(statsUpdateCoroutine);
            statsUpdateCoroutine = null;
        }

        if (signaling != null)
        {
            signaling.OnOfferReceived -= null;
            signaling.OnIceCandidateReceived -= null;
        }

        if (connection != null)
        {
            connection.Close();
            connection = null;
        }

        if (audioStreamTrack != null)
        {
            audioStreamTrack.Stop();
            audioStreamTrack.Dispose();
            audioStreamTrack = null;
        }

        if (microphoneAudioSource != null)
        {
            if (Microphone.devices.Length > 0)
            {
                Microphone.End(Microphone.devices[0]);
            }
            Destroy(microphoneAudioSource);
            microphoneAudioSource = null;
        }

        if (receivedAudioSource != null)
        {
            Destroy(receivedAudioSource);
            receivedAudioSource = null;
        }
        
        if (receiveImage != null)
        {
            receiveImage.texture = null;
        }
    }

    /// <summary>
    /// Public method to enable/disable sender audio transmission (client's microphone).
    /// Can be attached to a UI Toggle's OnValueChanged event.
    /// This sends a command from receiver to sender to control sender's audio.
    /// </summary>
    /// <param name="enabled">True to enable sender audio, false to disable</param>
    public void SetSenderAudioEnabled(bool enabled)
    {
        if (signaling == null || !signaling.IsReady())
        {
            Debug.LogWarning("[SimpleMediaStreamReceiver] Cannot control sender audio: Signaling not ready");
            return;
        }

        // Send command to sender via signaling
        signaling.RequestSenderAudioControl(enabled);
        Debug.Log($"[SimpleMediaStreamReceiver] Requested sender audio to be {(enabled ? "enabled" : "disabled")}");
    }

    /// <summary>
    /// Public method to enable/disable receiver audio transmission (therapist's microphone).
    /// Can be attached to a UI Toggle's OnValueChanged event.
    /// Note: If called before connection is established, audio will be added when answer is created.
    /// </summary>
    /// <param name="enabled">True to enable receiver audio, false to disable</param>
    public void SetReceiverAudioEnabled(bool enabled)
    {
        if (receiverAudioEnabled == enabled)
        {
            return; // No change needed
        }

        receiverAudioEnabled = enabled;

        if (connection == null)
        {
            // Connection not established yet - will be added when answer is created
            Debug.Log($"[SimpleMediaStreamReceiver] Receiver audio will be enabled/disabled when connection is established");
            return;
        }

        if (connection.ConnectionState != RTCPeerConnectionState.Connected)
        {
            Debug.LogWarning("[SimpleMediaStreamReceiver] Cannot toggle audio: WebRTC connection not established");
            receiverAudioEnabled = false;
            return;
        }

        if (enabled)
        {
            // Enable receiver audio
            StartCoroutine(EnableReceiverAudio());
        }
        else
        {
            // Disable receiver audio
            StartCoroutine(DisableReceiverAudio());
        }
    }

    private IEnumerator EnableReceiverAudio()
    {
        if (audioStreamTrack != null)
        {
            Debug.LogWarning("[SimpleMediaStreamReceiver] Audio track already exists");
            yield break;
        }

        // Create AudioSource for microphone capture
        if (microphoneAudioSource == null)
        {
            microphoneAudioSource = gameObject.AddComponent<AudioSource>();
            microphoneAudioSource.loop = true;
            microphoneAudioSource.playOnAwake = false;
            
            // Start microphone capture
            string microphoneName = Microphone.devices.Length > 0 ? Microphone.devices[0] : null;
            if (string.IsNullOrEmpty(microphoneName))
            {
                Debug.LogError("[SimpleMediaStreamReceiver] No microphone device found");
                receiverAudioEnabled = false;
                yield break;
            }
            
            microphoneAudioSource.clip = Microphone.Start(microphoneName, true, 10, 48000);
            if (microphoneAudioSource.clip == null)
            {
                Debug.LogError("[SimpleMediaStreamReceiver] Failed to start microphone");
                receiverAudioEnabled = false;
                yield break;
            }
            
            // Wait for microphone to start (yield outside try-catch)
            while (!(Microphone.GetPosition(microphoneName) > 0))
            {
                yield return null;
            }
            
            microphoneAudioSource.Play();
        }
        
        // Create AudioStreamTrack from the AudioSource (in try-catch for safety)
        try
        {
            audioStreamTrack = new AudioStreamTrack(microphoneAudioSource);
            audioSender = connection.AddTrack(audioStreamTrack);
            Debug.Log("[SimpleMediaStreamReceiver] Receiver audio enabled");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SimpleMediaStreamReceiver] Failed to enable receiver audio: {e.Message}");
            receiverAudioEnabled = false;
            if (audioStreamTrack != null)
            {
                audioStreamTrack.Dispose();
                audioStreamTrack = null;
            }
            if (microphoneAudioSource != null)
            {
                if (Microphone.devices.Length > 0)
                {
                    Microphone.End(Microphone.devices[0]);
                }
                Destroy(microphoneAudioSource);
                microphoneAudioSource = null;
            }
        }
    }

    private IEnumerator DisableReceiverAudio()
    {
        if (audioSender == null || audioStreamTrack == null)
        {
            yield break;
        }

        try
        {
            connection.RemoveTrack(audioSender);
            audioStreamTrack.Stop();
            audioStreamTrack.Dispose();
            audioStreamTrack = null;
            audioSender = null;
            
            // Stop microphone
            if (microphoneAudioSource != null)
            {
                if (Microphone.devices.Length > 0)
                {
                    Microphone.End(Microphone.devices[0]);
                }
                microphoneAudioSource.Stop();
                Destroy(microphoneAudioSource);
                microphoneAudioSource = null;
            }
            
            Debug.Log("[SimpleMediaStreamReceiver] Receiver audio disabled");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SimpleMediaStreamReceiver] Failed to disable receiver audio: {e.Message}");
        }
    }
}
