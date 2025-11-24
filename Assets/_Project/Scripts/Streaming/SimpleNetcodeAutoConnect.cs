using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    [Header("LAN Discovery")]
    [SerializeField] private bool enableLanDiscovery = true;
    [SerializeField] private int discoveryPort = 47777;
    [SerializeField] private float discoveryBroadcastInterval = 1.5f;
    [SerializeField] private string discoveryAppId = "VRX-WebRTC";

    private bool hasConnected = false;
    private bool isWaitingForDiscovery = false;
    private bool isListeningForDiscovery = false;
    private string lastDiscoveredAddress = null;
    private ushort lastDiscoveredPort = 0;
    private CancellationTokenSource broadcastCts;
    private CancellationTokenSource listenCts;
    private Task broadcastTask;
    private Task listenTask;
    private UdpClient activeListener;
    private readonly object discoveryLock = new object();
    private string pendingDiscoveryAddress;
    private ushort pendingDiscoveryPort;
    private bool hasPendingDiscoveryUpdate = false;
    private bool pendingManualConnectRequest = false;

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

        if (enableLanDiscovery && !startAsHost)
        {
            StartListeningForHosts();
            isWaitingForDiscovery = true;
        }
    }

    private void OnServerStarted()
    {
        Debug.Log("[NetcodeAutoConnect] Server started successfully");
        if (enableLanDiscovery)
        {
            StartLanBroadcast();
        }
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
        if (enableLanDiscovery && wasHost)
        {
            StopLanBroadcast();
        }
    }

    void Update()
    {
        if (hasPendingDiscoveryUpdate)
        {
            string address;
            ushort port;
            lock (discoveryLock)
            {
                address = pendingDiscoveryAddress;
                port = pendingDiscoveryPort;
                hasPendingDiscoveryUpdate = false;
            }

            ApplyDiscoveredEndpoint(address, port);
        }
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

        if (enableLanDiscovery && !startAsHost && !HasDiscoveredEndpoint())
        {
            if (!isListeningForDiscovery)
            {
                StartListeningForHosts();
            }
            pendingManualConnectRequest = true;
            if (!isWaitingForDiscovery)
            {
                Debug.Log("[NetcodeAutoConnect] Waiting for LAN host discovery before attempting client connection...");
                isWaitingForDiscovery = true;
            }
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

        if (enableLanDiscovery)
        {
            StopLanBroadcast();
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

        if (enableLanDiscovery && !startAsHost && HasDiscoveredEndpoint())
        {
            serverAddress = lastDiscoveredAddress;
            serverPort = lastDiscoveredPort;
        }

        pendingManualConnectRequest = false;

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

        StopLanBroadcast();
        StopListeningForHosts();
    }

    void OnGUI()
    {
        if (!hasConnected && NetworkManager.Singleton != null && !NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsServer)
        {
            GUILayout.BeginArea(new Rect(10, 10, 300, 200));
            GUILayout.Label("Netcode Connection");
            if (enableLanDiscovery && !startAsHost && isWaitingForDiscovery)
            {
                GUILayout.Label("Waiting for host broadcast...");
            }
            
            if (GUILayout.Button("Start Host (Receiver)"))
            {
                StartHost();
            }
            
            if (GUILayout.Button("Start Client (Sender)"))
            {
                AttemptConnection();
            }
            
            GUILayout.EndArea();
        }
    }

    private void StartLanBroadcast()
    {
        if (broadcastTask != null && !broadcastTask.IsCompleted)
        {
            return;
        }

        broadcastCts = new CancellationTokenSource();
        broadcastTask = BroadcastLoopAsync(broadcastCts.Token);
    }

    private void StopLanBroadcast()
    {
        if (broadcastCts == null)
        {
            return;
        }

        try
        {
            broadcastCts.Cancel();
        }
        catch { }
        finally
        {
            broadcastCts.Dispose();
            broadcastCts = null;
            broadcastTask = null;
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        UdpClient udpClient = null;
        try
        {
            udpClient = new UdpClient();
            udpClient.EnableBroadcast = true;
            var endPoint = new IPEndPoint(IPAddress.Broadcast, discoveryPort);

            while (!token.IsCancellationRequested)
            {
                string ipToAdvertise = GetLocalIPv4Address() ?? serverAddress;
                ushort portToAdvertise = GetTransportPort();
                var payload = $"{discoveryAppId}|{ipToAdvertise}|{portToAdvertise}";
                byte[] data = Encoding.UTF8.GetBytes(payload);
                await udpClient.SendAsync(data, data.Length, endPoint);

                await Task.Delay(TimeSpan.FromSeconds(discoveryBroadcastInterval), token);
            }
        }
        catch (OperationCanceledException)
        {
            // expected when closing
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[NetcodeAutoConnect] LAN broadcast error: {ex.Message}");
        }
        finally
        {
            udpClient?.Close();
        }
    }

    private void StartListeningForHosts()
    {
        if (listenTask != null && !listenTask.IsCompleted)
        {
            isListeningForDiscovery = true;
            return;
        }

        listenCts = new CancellationTokenSource();
        listenTask = ListenForHostsAsync(listenCts.Token);
        isListeningForDiscovery = true;
    }

    private void StopListeningForHosts()
    {
        if (listenCts == null)
        {
            return;
        }

        try
        {
            listenCts.Cancel();
        }
        catch { }
        finally
        {
            listenCts.Dispose();
            listenCts = null;
            listenTask = null;
            isListeningForDiscovery = false;
            if (activeListener != null)
            {
                try
                {
                    activeListener.Close();
                }
                catch { }
                finally
                {
                    activeListener = null;
                }
            }
        }
    }

    private async Task ListenForHostsAsync(CancellationToken token)
    {
        UdpClient listener = null;
        try
        {
            listener = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
            listener.EnableBroadcast = true;
            activeListener = listener;

            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result = await listener.ReceiveAsync();
                ProcessDiscoveryMessage(result.Buffer);
            }
        }
        catch (ObjectDisposedException)
        {
            // socket closed
        }
        catch (SocketException ex)
        {
            Debug.LogWarning($"[NetcodeAutoConnect] LAN listener socket error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[NetcodeAutoConnect] LAN listener error: {ex.Message}");
        }
        finally
        {
            listener?.Close();
            if (activeListener == listener)
            {
                activeListener = null;
            }
        }
    }

    private void ProcessDiscoveryMessage(byte[] buffer)
    {
        if (buffer == null || buffer.Length == 0)
        {
            return;
        }

        string message = Encoding.UTF8.GetString(buffer);
        var parts = message.Split('|');
        if (parts.Length < 3)
        {
            return;
        }

        if (!string.Equals(parts[0], discoveryAppId, StringComparison.Ordinal))
        {
            return;
        }

        if (!ushort.TryParse(parts[2], out ushort advertisedPort))
        {
            return;
        }

        string advertisedAddress = parts[1];
        lock (discoveryLock)
        {
            pendingDiscoveryAddress = advertisedAddress;
            pendingDiscoveryPort = advertisedPort;
            hasPendingDiscoveryUpdate = true;
        }
    }

    private void ApplyDiscoveredEndpoint(string address, ushort port)
    {
        if (string.IsNullOrEmpty(address) || port == 0)
        {
            return;
        }

        lastDiscoveredAddress = address;
        lastDiscoveredPort = port;
        serverAddress = address;
        serverPort = port;

        Debug.Log($"[NetcodeAutoConnect] Discovered host at {address}:{port}");

        if (isWaitingForDiscovery)
        {
            isWaitingForDiscovery = false;
            if (!startAsHost && (autoConnectOnStart || pendingManualConnectRequest))
            {
                pendingManualConnectRequest = false;
                AttemptConnection();
            }
        }
    }

    private bool HasDiscoveredEndpoint()
    {
        return !string.IsNullOrEmpty(lastDiscoveredAddress) && lastDiscoveredPort != 0;
    }

    private ushort GetTransportPort()
    {
        var transport = NetworkManager.Singleton != null ? NetworkManager.Singleton.GetComponent<UnityTransport>() : null;
        if (transport != null)
        {
            return transport.ConnectionData.Port;
        }

        return serverPort;
    }

    private string GetLocalIPv4Address()
    {
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                    {
                        return ip.Address.ToString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[NetcodeAutoConnect] Unable to resolve local IP: {ex.Message}");
        }

        return null;
    }
}

