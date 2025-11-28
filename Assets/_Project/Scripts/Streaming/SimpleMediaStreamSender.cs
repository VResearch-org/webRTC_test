using System;
using System.Collections;
using Unity.WebRTC;
using Unity.Netcode;
using UnityEngine;

public class SimpleMediaStreamSender : MonoBehaviour
{
    [SerializeField] private RenderTexture cameraStream;
    [SerializeField] private int maxRetries = 3;
    [SerializeField] private float retryDelay = 5f;
    [SerializeField] private float connectionTimeout = 15f;

    private string roomId => UserManager.userName;

    private RTCPeerConnection connection;
    private VideoStreamTrack videoStreamTrack;
    private AudioStreamTrack audioStreamTrack; // Sender's microphone audio
    private AudioSource microphoneAudioSource; // AudioSource for microphone capture
    private RTCRtpSender audioSender; // Track the audio sender for dynamic enable/disable
    private AudioSource receivedAudioSource; // Audio source for playing received audio
    private NetcodeWebRTCSignaling signaling;

    private bool answerReceived = false;
    private SessionDescription remoteAnswer;
    private int currentRetryCount = 0;
    private bool isConnectionEstablished = false;
    private Coroutine connectionTimeoutCoroutine;
    private Coroutine webRTCUpdateCoroutine; // Track the WebRTC.Update() coroutine
    private bool isChangingResolution = false; // Prevent multiple simultaneous resolution changes
    private bool senderAudioEnabled = false; // Track sender audio state
    private bool isNegotiating = false; // Track if we're currently negotiating

    void Start()
    {
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
            if (connectionTimeoutCoroutine != null)
            {
                StopCoroutine(connectionTimeoutCoroutine);
            }
            connectionTimeoutCoroutine = StartCoroutine(ConnectionTimeoutCheck());

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
                            true // isSender = true
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
                if (state == RTCIceConnectionState.Connected)
                {
                    isConnectionEstablished = true;
                    currentRetryCount = 0;
                    if (connectionTimeoutCoroutine != null)
                    {
                        StopCoroutine(connectionTimeoutCoroutine);
                        connectionTimeoutCoroutine = null;
                    }
                }
                else if (state == RTCIceConnectionState.Failed || state == RTCIceConnectionState.Disconnected)
                {
                    HandleConnectionFailure();
                }
            };

            connection.OnConnectionStateChange = state =>
            {
                Debug.Log($"Connection state changed to: {state}");
                if (state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Disconnected)
                {
                    HandleConnectionFailure();
                }
            };

            connection.OnNegotiationNeeded = () =>
            {
                if (!isNegotiating)
                {
                    StartCoroutine(CreateAndSendOffer());
                }
            };

            // Handle incoming tracks (audio from receiver)
            connection.OnTrack = e =>
            {
                try
                {
                    if (e.Track is AudioStreamTrack audio)
                    {
                        // Stop any existing received audio first to prevent feedback
                        if (receivedAudioSource != null)
                        {
                            receivedAudioSource.Stop();
                            receivedAudioSource.SetTrack(null);
                        }
                        else
                        {
                            receivedAudioSource = gameObject.AddComponent<AudioSource>();
                            receivedAudioSource.loop = false;
                            receivedAudioSource.playOnAwake = false;
                        }
                        
                        // Mute received audio if we're sending audio to prevent echo
                        receivedAudioSource.mute = senderAudioEnabled;
                        receivedAudioSource.SetTrack(audio);
                        receivedAudioSource.Play();
                        Debug.Log($"[SimpleMediaStreamSender] Received audio from receiver (muted: {receivedAudioSource.mute})");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SimpleMediaStreamSender] Error handling audio track: {ex.Message}");
                }
            };

            videoStreamTrack = new VideoStreamTrack(cameraStream);
            connection.AddTrack(videoStreamTrack);

            // Start WebRTC.Update() coroutine and track it
            if (webRTCUpdateCoroutine != null)
            {
                StopCoroutine(webRTCUpdateCoroutine);
            }
            webRTCUpdateCoroutine = StartCoroutine(WebRTC.Update());

            SetupSignalingListeners();
        }
        catch (Exception e)
        {
            Debug.LogError($"Error initializing connection: {e.Message}");
            HandleConnectionFailure();
        }
    }

    private IEnumerator ConnectionTimeoutCheck()
    {
        yield return new WaitForSeconds(connectionTimeout);
        
        if (!isConnectionEstablished)
        {
            Debug.LogWarning("Connection timeout reached without successful connection");
            HandleConnectionFailure();
        }
    }

    private void HandleConnectionFailure()
    {
        if (currentRetryCount < maxRetries)
        {
            currentRetryCount++;
            Debug.Log($"Connection failed. Retrying ({currentRetryCount}/{maxRetries})...");
            StartCoroutine(RetryConnection());
        }
        else
        {
            Debug.LogError("Maximum retry attempts reached. Connection failed.");
        }
    }

    private IEnumerator RetryConnection()
    {
        yield return new WaitForSeconds(retryDelay);
        
        // Clean up existing connection
        if (connection != null)
        {
            connection.Close();
            connection = null;
        }
        
        if (videoStreamTrack != null)
        {
            videoStreamTrack.Stop();
            videoStreamTrack = null;
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

        audioSender = null;

        // Reset state
        answerReceived = false;
        isConnectionEstablished = false;
        senderAudioEnabled = false;
        
        // Reinitialize connection
        InitializeConnection();
    }

    private void SetupSignalingListeners()
    {
        if (signaling == null) return;

        signaling.OnAnswerReceived += (json) =>
        {
            if (!answerReceived)
            {
                try
                {
                    remoteAnswer = SessionDescription.FromJSON(json);
                    answerReceived = true;
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error processing answer: {e.Message}");
                    HandleConnectionFailure();
                }
            }
        };

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
                HandleConnectionFailure();
            }
        };

        signaling.OnResolutionChangeRequested += (width, height) =>
        {
            ChangeResolution(width, height);
        };

        signaling.OnSenderAudioControlRequested += (enabled) =>
        {
            // Receiver requested to enable/disable sender audio
            SetSenderAudioEnabled(enabled);
        };
    }

    IEnumerator CreateAndSendOffer()
    {
        if (isNegotiating)
        {
            Debug.LogWarning("[SimpleMediaStreamSender] Negotiation already in progress, skipping");
            yield break;
        }

        isNegotiating = true;
        
        yield return new WaitUntil(() => connection.SignalingState == RTCSignalingState.Stable);

        // Wait for a moment to ensure the connection is ready
        yield return new WaitForSeconds(0.1f);

        var offerOp = connection.CreateOffer();
        yield return offerOp;

        if (offerOp.IsError)
        {
            Debug.LogError($"Error creating offer: {offerOp.Error.message}");
            isNegotiating = false;
            yield break;
        }

        var offerDesc = offerOp.Desc;
        var setLocalOp = connection.SetLocalDescription(ref offerDesc);
        yield return setLocalOp;

        if (setLocalOp.IsError)
        {
            Debug.LogError($"Error setting local description: {setLocalOp.Error.message}");
            isNegotiating = false;
            yield break;
        }

        try
        {
            var session = new SessionDescription
            {
                SessionType = offerDesc.type.ToString(),
                Sdp = offerDesc.sdp
            };

            if (signaling != null && signaling.IsReady())
            {
                signaling.SendOffer(session.ConvertToJSON());
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error sending offer: {e.Message}");
        }
        
        // Wait for answer or timeout
        yield return new WaitUntil(() => connection.SignalingState == RTCSignalingState.Stable || connection.ConnectionState == RTCPeerConnectionState.Closed);
        isNegotiating = false;
    }

    void Update()
    {
        if (answerReceived)
        {
            answerReceived = false;
            StartCoroutine(SetRemoteAnswer());
        }
    }

    IEnumerator SetRemoteAnswer()
    {
        RTCSessionDescription answerDesc = new RTCSessionDescription
        {
            type = RTCSdpType.Answer,
            sdp = remoteAnswer.Sdp
        };

        var setRemoteOp = connection.SetRemoteDescription(ref answerDesc);
        yield return setRemoteOp;

        if (setRemoteOp.IsError)
        {
            Debug.LogError($"Error setting remote description: {setRemoteOp.Error.message}");
        }
    }

    /// <summary>
    /// Change the RenderTexture resolution. Called when client requests resolution change.
    /// Recreates the VideoStreamTrack so WebRTC picks up the new resolution.
    /// </summary>
    private void ChangeResolution(int width, int height)
    {
        if (cameraStream == null)
        {
            Debug.LogError("[SimpleMediaStreamSender] Cannot change resolution: cameraStream is null");
            return;
        }

        if (width <= 0 || height <= 0)
        {
            Debug.LogError($"[SimpleMediaStreamSender] Invalid resolution: {width}x{height}");
            return;
        }

        if (connection == null || connection.ConnectionState != RTCPeerConnectionState.Connected)
        {
            Debug.LogWarning("[SimpleMediaStreamSender] Cannot change resolution: WebRTC connection not established");
            return;
        }

        if (isChangingResolution)
        {
            Debug.LogWarning("[SimpleMediaStreamSender] Resolution change already in progress. Ignoring request.");
            return;
        }

        // Check if resolution is actually changing
        if (cameraStream.width == width && cameraStream.height == height)
        {
            Debug.Log($"[SimpleMediaStreamSender] Resolution is already {width}x{height}. No change needed.");
            return;
        }

        Debug.Log($"[SimpleMediaStreamSender] Changing resolution to {width}x{height}");

        // Resize the RenderTexture
        cameraStream.Release();
        cameraStream.width = width;
        cameraStream.height = height;
        cameraStream.Create();

        // Recreate the VideoStreamTrack with the new RenderTexture
        // This is necessary because VideoStreamTrack doesn't automatically update when RenderTexture changes
        StartCoroutine(RecreateVideoTrack());
    }

    private IEnumerator RecreateVideoTrack()
    {
        isChangingResolution = true;

        if (connection == null || videoStreamTrack == null)
        {
            Debug.LogError("[SimpleMediaStreamSender] Cannot recreate video track: connection or track is null");
            isChangingResolution = false;
            yield break;
        }

        Debug.Log("[SimpleMediaStreamSender] Recreating VideoStreamTrack with new resolution...");

        // Find the sender for the current track
        RTCRtpSender videoSender = null;
        var senders = connection.GetSenders();
        foreach (var sender in senders)
        {
            if (sender.Track == videoStreamTrack)
            {
                videoSender = sender;
                break;
            }
        }

        if (videoSender == null)
        {
            Debug.LogError("[SimpleMediaStreamSender] Could not find sender for current video track!");
            isChangingResolution = false;
            yield break;
        }

        // Store reference to old track for cleanup
        var oldTrack = videoStreamTrack;

        // Create new VideoStreamTrack with the resized RenderTexture FIRST
        var newTrack = new VideoStreamTrack(cameraStream);

        // Use ReplaceTrack instead of Remove/Add to avoid accumulation
        // ReplaceTrack returns bool (true if successful)
        bool replaceSuccess = videoSender.ReplaceTrack(newTrack);
        
        if (!replaceSuccess)
        {
            Debug.LogError("[SimpleMediaStreamSender] Failed to replace track");
            newTrack.Dispose();
            isChangingResolution = false;
            yield break;
        }
        
        // Wait a frame for the replacement to take effect
        yield return null;

        // Now stop and dispose the old track
        oldTrack.Stop();
        oldTrack.Dispose();
        videoStreamTrack = newTrack;

        Debug.Log($"[SimpleMediaStreamSender] VideoStreamTrack replaced. Waiting for negotiation to complete...");
        
        // Wait for negotiation to complete before allowing another change
        yield return new WaitUntil(() => connection.SignalingState == RTCSignalingState.Stable);
        
        // Wait a bit more to ensure everything is settled
        yield return new WaitForSeconds(0.5f);

        // Verify we only have one video track
        var finalSenders = connection.GetSenders();
        var videoTrackCount = 0;
        foreach (var sender in finalSenders)
        {
            if (sender.Track != null && sender.Track is VideoStreamTrack)
            {
                videoTrackCount++;
            }
        }

        if (videoTrackCount != 1)
        {
            Debug.LogWarning($"[SimpleMediaStreamSender] Warning: Expected 1 video track, found {videoTrackCount}!");
        }

        isChangingResolution = false;
        Debug.Log($"[SimpleMediaStreamSender] Resolution change complete. New resolution: {cameraStream.width}x{cameraStream.height}");
    }

    void OnDestroy()
    {
        if (connectionTimeoutCoroutine != null)
        {
            StopCoroutine(connectionTimeoutCoroutine);
        }

        if (signaling != null)
        {
            signaling.OnAnswerReceived -= null;
            signaling.OnIceCandidateReceived -= null;
            signaling.OnResolutionChangeRequested -= null;
            signaling.OnSenderAudioControlRequested -= null;
        }

        if (webRTCUpdateCoroutine != null)
        {
            StopCoroutine(webRTCUpdateCoroutine);
            webRTCUpdateCoroutine = null;
        }

        if (connection != null)
        {
            connection.Close();
            connection = null;
        }
        
        if (videoStreamTrack != null)
        {
            videoStreamTrack.Stop();
            videoStreamTrack.Dispose();
            videoStreamTrack = null;
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
    }

    /// <summary>
    /// Public method to enable/disable sender audio transmission (client's microphone).
    /// Can be attached to a UI Toggle's OnValueChanged event.
    /// </summary>
    /// <param name="enabled">True to enable sender audio, false to disable</param>
    public void SetSenderAudioEnabled(bool enabled)
    {
        if (senderAudioEnabled == enabled)
        {
            return; // No change needed
        }

        senderAudioEnabled = enabled;

        if (connection == null || connection.ConnectionState != RTCPeerConnectionState.Connected)
        {
            Debug.LogWarning("[SimpleMediaStreamSender] Cannot toggle audio: WebRTC connection not established");
            senderAudioEnabled = false;
            return;
        }

        // Update received audio mute state to prevent echo
        if (receivedAudioSource != null)
        {
            receivedAudioSource.mute = enabled;
            Debug.Log($"[SimpleMediaStreamSender] Received audio muted: {enabled} (to prevent echo)");
        }

        if (enabled)
        {
            // Enable sender audio
            StartCoroutine(EnableSenderAudio());
        }
        else
        {
            // Disable sender audio
            StartCoroutine(DisableSenderAudio());
        }
    }

    private IEnumerator EnableSenderAudio()
    {
        if (audioStreamTrack != null)
        {
            Debug.LogWarning("[SimpleMediaStreamSender] Audio track already exists");
            yield break;
        }

        // Wait for any ongoing negotiation to complete
        while (isNegotiating)
        {
            yield return new WaitForSeconds(0.1f);
        }

        // Create AudioSource for microphone capture
        if (microphoneAudioSource == null)
        {
            microphoneAudioSource = gameObject.AddComponent<AudioSource>();
            microphoneAudioSource.loop = true;
            microphoneAudioSource.playOnAwake = false;
            microphoneAudioSource.mute = false; // Ensure mic is not muted
            
            // Start microphone capture
            string microphoneName = Microphone.devices.Length > 0 ? Microphone.devices[0] : null;
            if (string.IsNullOrEmpty(microphoneName))
            {
                Debug.LogError("[SimpleMediaStreamSender] No microphone device found");
                senderAudioEnabled = false;
                yield break;
            }
            
            microphoneAudioSource.clip = Microphone.Start(microphoneName, true, 10, 48000);
            if (microphoneAudioSource.clip == null)
            {
                Debug.LogError("[SimpleMediaStreamSender] Failed to start microphone");
                senderAudioEnabled = false;
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
        bool trackCreated = false;
        try
        {
            audioStreamTrack = new AudioStreamTrack(microphoneAudioSource);
            audioSender = connection.AddTrack(audioStreamTrack);
            trackCreated = true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[SimpleMediaStreamSender] Failed to enable sender audio: {e.Message}");
            senderAudioEnabled = false;
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
                microphoneAudioSource.Stop();
                Destroy(microphoneAudioSource);
                microphoneAudioSource = null;
            }
            yield break;
        }
        
        if (trackCreated)
        {
            // Wait for negotiation to complete
            yield return new WaitUntil(() => connection.SignalingState == RTCSignalingState.Stable || !isConnectionEstablished);
            
            Debug.Log("[SimpleMediaStreamSender] Sender audio enabled");
        }
    }

    private IEnumerator DisableSenderAudio()
    {
        if (audioSender == null || audioStreamTrack == null)
        {
            // Clean up any remaining resources
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
                microphoneAudioSource.Stop();
                Destroy(microphoneAudioSource);
                microphoneAudioSource = null;
            }
            audioSender = null;
            yield break;
        }

        bool trackRemoved = false;
        try
        {
            // Stop the track first
            audioStreamTrack.Stop();
            
            // Remove from connection (this will trigger renegotiation)
            connection.RemoveTrack(audioSender);
            trackRemoved = true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[SimpleMediaStreamSender] Failed to disable sender audio: {e.Message}");
        }
        
        if (trackRemoved)
        {
            // Wait for negotiation to complete
            yield return new WaitUntil(() => connection.SignalingState == RTCSignalingState.Stable || !isConnectionEstablished);
            
            // Dispose the track
            if (audioStreamTrack != null)
            {
                audioStreamTrack.Dispose();
                audioStreamTrack = null;
            }
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
            
            // Unmute received audio now that we're not sending
            if (receivedAudioSource != null)
            {
                receivedAudioSource.mute = false;
            }
            
            Debug.Log("[SimpleMediaStreamSender] Sender audio disabled");
        }
    }
}
