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
    private NetcodeWebRTCSignaling signaling;

    private bool answerReceived = false;
    private SessionDescription remoteAnswer;
    private int currentRetryCount = 0;
    private bool isConnectionEstablished = false;
    private Coroutine connectionTimeoutCoroutine;

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

            connection.OnNegotiationNeeded = () => StartCoroutine(CreateAndSendOffer());

            videoStreamTrack = new VideoStreamTrack(cameraStream);
            connection.AddTrack(videoStreamTrack);

            StartCoroutine(WebRTC.Update());

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

        // Reset state
        answerReceived = false;
        isConnectionEstablished = false;
        
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
    }

    IEnumerator CreateAndSendOffer()
    {
        yield return new WaitUntil(() => connection.SignalingState == RTCSignalingState.Stable);

        // Wait for a moment to ensure the connection is ready
        yield return new WaitForSeconds(1f);

        var offerOp = connection.CreateOffer();
        yield return offerOp;

        if (offerOp.IsError)
        {
            Debug.LogError($"Error creating offer: {offerOp.Error.message}");
            yield break;
        }

        var offerDesc = offerOp.Desc;
        var setLocalOp = connection.SetLocalDescription(ref offerDesc);
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

        Debug.Log($"[SimpleMediaStreamSender] Changing resolution to {width}x{height}");

        // Simply resize the RenderTexture - WebRTC should handle it automatically
        cameraStream.Release();
        cameraStream.width = width;
        cameraStream.height = height;
        cameraStream.Create();

        Debug.Log($"[SimpleMediaStreamSender] Resolution changed to {width}x{height}. Testing without renegotiation...");
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
        }

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
    }
}
