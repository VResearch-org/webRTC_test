using Unity.Netcode;
using UnityEngine;
using System;

/// <summary>
/// Handles WebRTC signaling over Netcode for GameObjects.
/// One device acts as Host (Server), the other as Client.
/// </summary>
public class NetcodeWebRTCSignaling : NetworkBehaviour
{
    public static NetcodeWebRTCSignaling Instance { get; private set; }

    // NetworkVariables for signaling data
    // Initialize with empty string to avoid null reference exceptions during serialization
    private NetworkVariable<NetworkString> offerData = new NetworkVariable<NetworkString>(
        new NetworkString { Value = string.Empty }, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    
    private NetworkVariable<NetworkString> answerData = new NetworkVariable<NetworkString>(
        new NetworkString { Value = string.Empty }, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Events for signaling
    public event Action<string> OnOfferReceived;
    public event Action<string> OnAnswerReceived;
    public event Action<string, string, int> OnIceCandidateReceived;
    public event Action<int, int> OnResolutionChangeRequested;

    private bool isInitialized = false;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        if (Instance == null)
        {
            Instance = this;
        }

        // Listen for offer changes
        // Offers are sent by server (sender) to client (receiver)
        // So client (receiver) needs to receive offers
        offerData.OnValueChanged += (NetworkString previous, NetworkString current) =>
        {
            if (!string.IsNullOrEmpty(current.Value) && IsClient)
            {
                OnOfferReceived?.Invoke(current.Value);
            }
        };

        // Listen for answer changes
        // Answers are sent by client (receiver) to server (sender)
        // So server (sender) needs to receive answers
        answerData.OnValueChanged += (NetworkString previous, NetworkString current) =>
        {
            if (!string.IsNullOrEmpty(current.Value) && IsServer)
            {
                OnAnswerReceived?.Invoke(current.Value);
            }
        };

        isInitialized = true;
    }

    public override void OnNetworkDespawn()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        base.OnNetworkDespawn();
    }

    /// <summary>
    /// Send offer (called by sender/server)
    /// </summary>
    public void SendOffer(string offerJson)
    {
        if (IsServer)
        {
            offerData.Value = new NetworkString { Value = offerJson };
        }
        else if (IsClient)
        {
            SendOfferServerRpc(offerJson);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void SendOfferServerRpc(string offerJson)
    {
        offerData.Value = new NetworkString { Value = offerJson };
    }

    /// <summary>
    /// Send answer (called by receiver/client)
    /// </summary>
    public void SendAnswer(string answerJson)
    {
        if (IsServer)
        {
            answerData.Value = new NetworkString { Value = answerJson };
        }
        else if (IsClient)
        {
            SendAnswerServerRpc(answerJson);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void SendAnswerServerRpc(string answerJson)
    {
        answerData.Value = new NetworkString { Value = answerJson };
    }

    /// <summary>
    /// Send ICE candidate
    /// </summary>
    public void SendIceCandidate(string candidate, string sdpMid, int sdpMLineIndex, bool isSender)
    {
        if (IsServer)
        {
            ReceiveIceCandidateClientRpc(candidate, sdpMid, sdpMLineIndex, isSender);
        }
        else if (IsClient)
        {
            SendIceCandidateServerRpc(candidate, sdpMid, sdpMLineIndex, isSender);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void SendIceCandidateServerRpc(string candidate, string sdpMid, int sdpMLineIndex, bool isSender)
    {
        ReceiveIceCandidateClientRpc(candidate, sdpMid, sdpMLineIndex, isSender);
    }

    [ClientRpc]
    private void ReceiveIceCandidateClientRpc(string candidate, string sdpMid, int sdpMLineIndex, bool isSender)
    {
        // Server (sender) receives candidates from receiver (isSender=false), Client (receiver) receives from sender (isSender=true)
        // So: if isSender=true, we want receiver (client) to process it
        //     if isSender=false, we want sender (server) to process it
        bool shouldProcess = (IsServer && !isSender) || (IsClient && isSender);
        
        if (shouldProcess)
        {
            OnIceCandidateReceived?.Invoke(candidate, sdpMid, sdpMLineIndex);
        }
    }

    /// <summary>
    /// Check if offer exists (for receiver to check initial state)
    /// </summary>
    public bool HasOffer()
    {
        return offerData.Value.Value != null && !string.IsNullOrEmpty(offerData.Value.Value);
    }

    /// <summary>
    /// Get current offer (for receiver initial check)
    /// </summary>
    public string GetCurrentOffer()
    {
        return offerData.Value.Value;
    }

    public bool IsReady()
    {
        return isInitialized && IsSpawned;
    }

    /// <summary>
    /// Request resolution change from client (receiver) to server (sender)
    /// </summary>
    public void RequestResolutionChange(int width, int height)
    {
        if (IsClient)
        {
            RequestResolutionChangeServerRpc(width, height);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestResolutionChangeServerRpc(int width, int height)
    {
        // Server receives the request and triggers the event
        OnResolutionChangeRequested?.Invoke(width, height);
    }
}

/// <summary>
/// Wrapper for NetworkVariable string support
/// </summary>
[Serializable]
public struct NetworkString : INetworkSerializable
{
    public string Value;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        // Ensure Value is never null during serialization
        if (Value == null)
        {
            Value = string.Empty;
        }
        serializer.SerializeValue(ref Value);
    }
}
