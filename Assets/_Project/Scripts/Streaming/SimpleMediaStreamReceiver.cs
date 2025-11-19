using System.Collections;
using Unity.WebRTC;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using System;

public class SimpleMediaStreamReceiver : MonoBehaviour
{
    [SerializeField] private RawImage receiveImage;

    private string RoomId => UserManager.userName;

    private RTCPeerConnection connection;
    private NetcodeWebRTCSignaling signaling;
    private bool offerReceived = false;
    private SessionDescription remoteOffer;

    public void Init()
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
            };

            connection.OnConnectionStateChange = state =>
            {
                Debug.Log($"Connection state changed to: {state}");
            };

            connection.OnTrack = e =>
            {
                try
                {
                    if (e.Track is VideoStreamTrack video)
                    {
                        video.OnVideoReceived += tex =>
                        {
                            if (receiveImage != null)
                            {
                                receiveImage.texture = tex;
                            }
                        };
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error handling video track: {ex.Message}");
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

    void OnDestroy()
    {
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
        
        if (receiveImage != null)
        {
            receiveImage.texture = null;
        }
    }
}
