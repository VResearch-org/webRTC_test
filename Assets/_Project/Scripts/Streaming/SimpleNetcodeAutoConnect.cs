using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
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
    [SerializeField] private string serverAddress = "127.0.0.1";
    [SerializeField] private ushort serverPort = 7777;

    private bool hasConnected = false;

    void Start()
    {
        if (NetworkManager.Singleton != null)
        {
            // Subscribe to connection events for better debugging
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            NetworkManager.Singleton.OnServerStarted += OnServerStarted;
            NetworkManager.Singleton.OnClientStopped += OnClientStopped;
        }

        if (autoConnectOnStart)
        {
            // Small delay to ensure NetworkManager is ready
            Invoke(nameof(AttemptConnection), 0.5f);
        }
    }

    private void OnServerStarted()
    {
        Debug.Log("[NetcodeAutoConnect] Server started successfully");
    }

    private void OnClientConnected(ulong clientId)
    {
        Debug.Log($"[NetcodeAutoConnect] Client connected with ID: {clientId}");
    }

    private void OnClientDisconnected(ulong clientId)
    {
        Debug.LogWarning($"[NetcodeAutoConnect] Client disconnected with ID: {clientId}");
    }

    private void OnClientStopped(bool wasHost)
    {
        Debug.LogWarning($"[NetcodeAutoConnect] Client stopped. Was host: {wasHost}");
    }

    private void AttemptConnection()
    {
        if (hasConnected || NetworkManager.Singleton == null)
        {
            Debug.LogWarning($"[NetcodeAutoConnect] Cannot attempt connection - hasConnected: {hasConnected}, NetworkManager: {NetworkManager.Singleton != null}");
            return;
        }

        // Check if already connected
        if (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
        {
            Debug.Log($"[NetcodeAutoConnect] Already connected - IsClient: {NetworkManager.Singleton.IsClient}, IsServer: {NetworkManager.Singleton.IsServer}");
            hasConnected = true;
            return;
        }

        Debug.Log($"[NetcodeAutoConnect] Attempting connection - startAsHost: {startAsHost}");

        if (startAsHost)
        {
            StartHost();
        }
        else
        {
            StartClient();
        }
    }

    private void ConfigureTransport()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[NetcodeAutoConnect] NetworkManager.Singleton is null!");
            return;
        }

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport == null)
        {
            Debug.LogError("[NetcodeAutoConnect] UnityTransport component not found!");
            return;
        }

        // Configure for direct IP connection (not Relay)
        transport.SetConnectionData(serverAddress, serverPort);
        Debug.Log($"[NetcodeAutoConnect] Configured transport for direct connection: {serverAddress}:{serverPort}");
    }

    public void StartHost()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[NetcodeAutoConnect] NetworkManager.Singleton is null!");
            return;
        }

        if (NetworkManager.Singleton.IsServer)
        {
            Debug.LogWarning("[NetcodeAutoConnect] Already a server, skipping StartHost");
            return;
        }

        // Configure transport before starting
        ConfigureTransport();

        bool success = NetworkManager.Singleton.StartHost();
        if (success)
        {
            hasConnected = true;
            Debug.Log("[NetcodeAutoConnect] Started as Host (Server)");
        }
        else
        {
            Debug.LogError("[NetcodeAutoConnect] Failed to start as Host");
        }
    }

    public void StartClient()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[NetcodeAutoConnect] NetworkManager.Singleton is null!");
            return;
        }

        if (NetworkManager.Singleton.IsClient)
        {
            Debug.LogWarning("[NetcodeAutoConnect] Already a client, skipping StartClient");
            return;
        }

        // Configure transport before starting
        ConfigureTransport();

        Debug.Log("[NetcodeAutoConnect] Attempting to start as Client...");
        bool success = NetworkManager.Singleton.StartClient();
        
        if (success)
        {
            hasConnected = true;
            Debug.Log("[NetcodeAutoConnect] Successfully started as Client");
        }
        else
        {
            Debug.LogError("[NetcodeAutoConnect] Failed to start as Client - StartClient returned false");
            // Try to get more info about why it failed
            if (NetworkManager.Singleton != null)
            {
                Debug.LogError($"[NetcodeAutoConnect] NetworkManager state - IsClient: {NetworkManager.Singleton.IsClient}, IsServer: {NetworkManager.Singleton.IsServer}, IsHost: {NetworkManager.Singleton.IsHost}");
            }
        }
    }

    void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
            NetworkManager.Singleton.OnClientStopped -= OnClientStopped;
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

