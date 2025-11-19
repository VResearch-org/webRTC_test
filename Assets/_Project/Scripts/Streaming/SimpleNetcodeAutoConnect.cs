using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Simple auto-connect script for two-device setup.
/// First device becomes Host (Server), second device becomes Client.
/// </summary>
public class SimpleNetcodeAutoConnect : MonoBehaviour
{
    [SerializeField] private bool startAsHost = false;
    [SerializeField] private bool autoConnectOnStart = true;
    [SerializeField] private float connectionTimeout = 10f;

    private bool hasConnected = false;

    void Start()
    {
        if (autoConnectOnStart)
        {
            // Small delay to ensure NetworkManager is ready
            Invoke(nameof(AttemptConnection), 0.5f);
        }
    }

    private void AttemptConnection()
    {
        if (hasConnected || NetworkManager.Singleton == null)
            return;

        // Check if already connected
        if (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
        {
            hasConnected = true;
            return;
        }

        if (startAsHost)
        {
            StartHost();
        }
        else
        {
            StartClient();
        }
    }

    public void StartHost()
    {
        if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
        {
            bool success = NetworkManager.Singleton.StartHost();
            if (success)
            {
                hasConnected = true;
                Debug.Log("Started as Host (Server)");
            }
            else
            {
                Debug.LogError("Failed to start as Host");
            }
        }
    }

    public void StartClient()
    {
        if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsClient)
        {
            bool success = NetworkManager.Singleton.StartClient();
            if (success)
            {
                hasConnected = true;
                Debug.Log("Started as Client");
            }
            else
            {
                Debug.LogError("Failed to start as Client");
            }
        }
    }

    void OnGUI()
    {
        if (!hasConnected && NetworkManager.Singleton != null && !NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsServer)
        {
            GUILayout.BeginArea(new Rect(10, 10, 300, 200));
            GUILayout.Label("Netcode Connection");
            
            if (GUILayout.Button("Start Host (Receiver)"))
            {
                StartHost();
            }
            
            if (GUILayout.Button("Start Client (Sender)"))
            {
                StartClient();
            }
            
            GUILayout.EndArea();
        }
    }
}

