using System.Collections;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;
using Firebase.Database;
using System;
using System.Collections.Generic;

public class SimpleMediaStreamReceiver : MonoBehaviour
{
    [SerializeField] private RawImage receiveImage;

    private string RoomId => UserManager.userName;

    private RTCPeerConnection connection;
    private DatabaseReference dbRef;
    private bool offerReceived = false;
    private SessionDescription remoteOffer;
    private bool isRoomCreated = false;

    public void Init()
    {
        CreateRoom();
        InitializeConnection();
    }

    private void CreateRoom()
    {
        try
        {
            FirebaseDatabase.DefaultInstance.SetPersistenceEnabled(false);
            dbRef = FirebaseDatabase.DefaultInstance.GetReference("webrtc").Child(RoomId);
            
            // First, check and clean any existing room data
            dbRef.GetValueAsync().ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    Debug.LogError($"Error checking existing room: {task.Exception}");
                    return;
                }

                // If there's existing data, remove it first
                if (task.Result.Exists)
                {
                    Debug.Log($"Found existing room data for {RoomId}, cleaning up...");
                    dbRef.RemoveValueAsync().ContinueWith(cleanupTask =>
                    {
                        if (cleanupTask.IsFaulted)
                        {
                            Debug.LogError($"Error cleaning existing room: {cleanupTask.Exception}");
                            return;
                        }
                        CreateNewRoom();
                    });
                }
                else
                {
                    CreateNewRoom();
                }
            });
        }
        catch (Exception e)
        {
            Debug.LogError($"Error in CreateRoom: {e.Message}");
        }
    }

    private void CreateNewRoom()
    {
        try
        {
            // Create the room structure
            var roomData = new Dictionary<string, object>
            {
                { "created", DateTime.UtcNow.ToString("o") },
                { "status", "active" }
            };
            
            dbRef.SetValueAsync(roomData).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    Debug.LogError($"Error creating room: {task.Exception}");
                }
                else
                {
                    isRoomCreated = true;
                    Debug.Log($"Room {RoomId} created successfully");
                }
            });
        }
        catch (Exception e)
        {
            Debug.LogError($"Error in CreateNewRoom: {e.Message}");
        }
    }

    public void CleanupRoom()
    {
        if (dbRef != null)
        {
            try
            {
                // Remove all data in the room
                dbRef.RemoveValueAsync().ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        Debug.LogError($"Error cleaning up room: {task.Exception}");
                    }
                    else
                    {
                        Debug.Log($"Room {RoomId} cleaned up successfully");
                        isRoomCreated = false;
                    }
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"Error in CleanupRoom: {e.Message}");
            }
        }
    }

    private void InitializeConnection()
    {
        try
        {
            FirebaseDatabase.DefaultInstance.SetPersistenceEnabled(false);
            dbRef = FirebaseDatabase.DefaultInstance.GetReference("webrtc").Child(RoomId);

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
                    var candidateInit = new CandidateInit
                    {
                        SdpMid = candidate.SdpMid,
                        SdpMLineIndex = candidate.SdpMLineIndex ?? 0,
                        Candidate = candidate.Candidate
                    };

                    string json = candidateInit.ConvertToJSON();
                    dbRef.Child("candidates/receiver").Push().SetRawJsonValueAsync(json);
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
        // First, do a one-time check in case the offer already exists
        dbRef.Child("offer").GetValueAsync().ContinueWith(task =>
        {
            if (task.IsCompleted && task.Result.Exists && !offerReceived)
            {
                try
                {
                    var json = task.Result.GetRawJsonValue();
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
        });

        // Then set up listeners for future changes
        dbRef.Child("offer").ValueChanged += (s, args) =>
        {
            if (args.Snapshot.Exists && !offerReceived)
            {
                try
                {
                    var json = args.Snapshot.GetRawJsonValue();
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

        dbRef.Child("candidates/sender").ChildAdded += (s, args) =>
        {
            try
            {
                var json = args.Snapshot.GetRawJsonValue();
                var candidateInit = CandidateInit.FromJSON(json);
                var init = new RTCIceCandidateInit
                {
                    sdpMid = candidateInit.SdpMid,
                    sdpMLineIndex = candidateInit.SdpMLineIndex,
                    candidate = candidateInit.Candidate
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

            dbRef.Child("answer").SetRawJsonValueAsync(session.ConvertToJSON());
        }
        catch (Exception e)
        {
            Debug.LogError($"Error sending answer: {e.Message}");
        }
    }

    void OnDestroy()
    {
        if (connection != null)
        {
            connection.Close();
            connection = null;
        }
        
        if (receiveImage != null)
        {
            receiveImage.texture = null;
        }

        CleanupRoom();
    }

    void OnApplicationQuit()
    {
        CleanupRoom();
    }
}
