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
using UnityEngine.Events;
using Unity.Networking.Transport.Relay;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;

/// <summary>
/// Simple connection script for two-device setup.
/// Provides two public methods: StartWithLAN() and StartWithRelay()
/// </summary>
public class SimpleNetcodeAutoConnect : MonoBehaviour
{
    [Header("Role")]
    [SerializeField] [Tooltip("True for Sender (Host), False for Receiver (Client)")]
    private bool isHost = false;
    
    [SerializeField] private string serverAddress = "127.0.0.1";
    [SerializeField] private ushort serverPort = 7777;
    [Header("LAN Discovery")]
    [SerializeField] private int discoveryPort = 47777;
    [SerializeField] private float discoveryBroadcastInterval = 1.5f;
    [SerializeField] private string discoveryAppId = "VRX-WebRTC";
    [Header("Relay")]
    [SerializeField] private string preferredRelayRegion = "";
    [SerializeField] private UnityEvent<string> onRelayJoinCodeGenerated;
    [Header("UI")]
    [SerializeField] [Tooltip("UI GameObject to hide when connection starts")]
    private GameObject uiGameObject;

    private string manualRelayJoinCode = string.Empty;
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
    private bool relayModeActive = false;
    private string activeRelayJoinCode = string.Empty;
    private bool hostRelayConfigured = false;
    private bool clientRelayConfigured = false;
    private RelayServerData hostRelayData;
    private RelayServerData clientRelayData;
    private Task unityServicesInitTask;
    private bool unityServicesReady = false;

    void Start()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            NetworkManager.Singleton.OnServerStarted += OnServerStarted;
            NetworkManager.Singleton.OnClientStopped += OnClientStopped;
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

    /// <summary>
    /// Start connection using LAN.
    /// If isHost is true, starts as host. If false, starts listening for hosts and connects as client.
    /// </summary>
    public void StartWithLAN()
    {
        HideUI();
        
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[NetcodeAutoConnect] NetworkManager.Singleton is null!");
            return;
        }

        // Check if already connected
        if (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
        {
            Debug.LogWarning("[NetcodeAutoConnect] Already connected. Ignoring StartWithLAN call.");
            return;
        }

        if (isHost)
        {
            // Start as host
            StartHostLAN();
        }
        else
        {
            // Start listening for hosts and connect as client
            StartListeningForHosts();
        }
    }

    /// <summary>
    /// Set the relay join code from input field. Call this when the input field value changes.
    /// </summary>
    public void SetJoinCode(string joinCode)
    {
        manualRelayJoinCode = string.IsNullOrWhiteSpace(joinCode) ? string.Empty : joinCode.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Start connection using Relay.
    /// If isHost is true, starts as host and generates a join code.
    /// If isHost is false, starts as client using the join code set via SetJoinCode.
    /// </summary>
    public async void StartWithRelay()
    {
        HideUI();
        
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[NetcodeAutoConnect] NetworkManager.Singleton is null!");
            return;
        }

        // Check if already connected
        if (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
        {
            Debug.LogWarning("[NetcodeAutoConnect] Already connected. Ignoring StartWithRelay call.");
            return;
        }

        if (isHost)
        {
            // Start as host (generate new code)
            await StartHostRelay();
        }
        else
        {
            // Start as client (use provided code)
            if (string.IsNullOrWhiteSpace(manualRelayJoinCode))
            {
                Debug.LogError("[NetcodeAutoConnect] Cannot start as client: join code is empty. Set join code via SetJoinCode() first.");
                return;
            }
            await StartClientRelay(manualRelayJoinCode);
        }
    }

    private void HideUI()
    {
        if (uiGameObject != null)
        {
            uiGameObject.SetActive(false);
        }
    }

    private bool StartHostLAN()
    {
        if (NetworkManager.Singleton == null)
        {
            return false;
        }

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport == null)
        {
            Debug.LogError("[NetcodeAutoConnect] UnityTransport component not found!");
            return false;
        }

        transport.SetConnectionData(serverAddress, serverPort);
        relayModeActive = false;

        bool success = NetworkManager.Singleton.StartHost();
        if (success)
        {
            Debug.Log($"[NetcodeAutoConnect] Started as Host (LAN) on {serverAddress}:{serverPort}");
        }
        return success;
    }

    private async Task StartHostRelay()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[NetcodeAutoConnect] NetworkManager.Singleton is null!");
            return;
        }

        // Initialize Unity Services if needed
        if (!await EnsureUnityServicesReadyAsync())
        {
            Debug.LogError("[NetcodeAutoConnect] Unity Services are not ready. Cannot start relay host.");
            return;
        }

        // Configure relay host
        if (!await ConfigureRelayHostAsync())
        {
            Debug.LogError("[NetcodeAutoConnect] Failed to configure relay host. Cannot start.");
            return;
        }

        // Configure transport
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport == null)
        {
            Debug.LogError("[NetcodeAutoConnect] UnityTransport component not found!");
            return;
        }

        transport.SetRelayServerData(hostRelayData);
        relayModeActive = true;

        // Start host
        bool success = NetworkManager.Singleton.StartHost();
        if (success)
        {
            Debug.Log($"[NetcodeAutoConnect] Started as Host (Relay) with join code: {activeRelayJoinCode}");
        }
        else
        {
            Debug.LogError("[NetcodeAutoConnect] Failed to start as Host (Relay)");
        }
    }

    private async Task StartClientRelay(string joinCode)
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[NetcodeAutoConnect] NetworkManager.Singleton is null!");
            return;
        }

        // Initialize Unity Services if needed
        if (!await EnsureUnityServicesReadyAsync())
        {
            Debug.LogError("[NetcodeAutoConnect] Unity Services are not ready. Cannot start relay client.");
            return;
        }

        // Configure relay client
        if (!await ConfigureRelayClientAsync(joinCode))
        {
            Debug.LogError("[NetcodeAutoConnect] Failed to configure relay client. Cannot start.");
            return;
        }

        // Configure transport
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport == null)
        {
            Debug.LogError("[NetcodeAutoConnect] UnityTransport component not found!");
            return;
        }

        transport.SetRelayServerData(clientRelayData);
        relayModeActive = true;

        // Start client
        bool success = NetworkManager.Singleton.StartClient();
        if (success)
        {
            Debug.Log($"[NetcodeAutoConnect] Started as Client (Relay) with join code: {joinCode}");
        }
        else
        {
            Debug.LogError("[NetcodeAutoConnect] Failed to start as Client (Relay)");
        }
    }

    private void OnServerStarted()
    {
        Debug.Log("[NetcodeAutoConnect] Server started successfully");
        if (!relayModeActive)
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
        if (wasHost && !relayModeActive)
        {
            StopLanBroadcast();
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

        Debug.Log($"[NetcodeAutoConnect] Discovered host at {address}:{port}. Connecting as client...");

        // Connect as client
        if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsServer)
        {
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport != null)
            {
                transport.SetConnectionData(address, port);
                relayModeActive = false;

                bool success = NetworkManager.Singleton.StartClient();
                if (success)
                {
                    Debug.Log($"[NetcodeAutoConnect] Connected as Client (LAN) to {address}:{port}");
                    StopListeningForHosts();
                }
                else
                {
                    Debug.LogError("[NetcodeAutoConnect] Failed to connect as Client (LAN)");
                }
            }
        }
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

    private async Task<bool> EnsureUnityServicesReadyAsync()
    {
        if (unityServicesInitTask == null)
        {
            unityServicesInitTask = InitializeUnityServicesAsync();
        }

        if (unityServicesInitTask != null)
        {
            await unityServicesInitTask;
        }

        return unityServicesReady;
    }

    private async Task InitializeUnityServicesAsync()
    {
        if (unityServicesReady)
        {
            return;
        }

        try
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                await UnityServices.InitializeAsync();
            }
            else if (UnityServices.State == ServicesInitializationState.Initializing)
            {
                while (UnityServices.State == ServicesInitializationState.Initializing)
                {
                    await Task.Yield();
                }
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }

            unityServicesReady = true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[NetcodeAutoConnect] Unity Services initialization failed: {ex.Message}");
            unityServicesReady = false;
        }
    }

    private async Task<bool> ConfigureRelayHostAsync()
    {
        if (!await EnsureUnityServicesReadyAsync())
        {
            Debug.LogError("[NetcodeAutoConnect] Unity Services are not ready. Cannot configure relay host.");
            return false;
        }

        try
        {
            var allocation = await RelayService.Instance.CreateAllocationAsync(1, string.IsNullOrWhiteSpace(preferredRelayRegion) ? null : preferredRelayRegion);
            activeRelayJoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            hostRelayData = allocation.ToRelayServerData("dtls");
            hostRelayConfigured = true;
            Debug.Log($"[NetcodeAutoConnect] Relay host ready. Join code: {activeRelayJoinCode}");
            onRelayJoinCodeGenerated?.Invoke(activeRelayJoinCode);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[NetcodeAutoConnect] Failed to create Relay allocation: {ex.Message}");
            hostRelayConfigured = false;
            activeRelayJoinCode = string.Empty;
            return false;
        }
    }

    private async Task<bool> ConfigureRelayClientAsync(string joinCode)
    {
        if (string.IsNullOrWhiteSpace(joinCode))
        {
            Debug.LogWarning("[NetcodeAutoConnect] Relay join code is empty. Cannot configure client.");
            return false;
        }

        if (!await EnsureUnityServicesReadyAsync())
        {
            Debug.LogError("[NetcodeAutoConnect] Unity Services are not ready. Cannot configure relay client.");
            return false;
        }

        try
        {
            var allocation = await RelayService.Instance.JoinAllocationAsync(joinCode.Trim().ToUpperInvariant());
            clientRelayData = allocation.ToRelayServerData("dtls");
            clientRelayConfigured = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[NetcodeAutoConnect] Failed to join Relay allocation with code {joinCode}: {ex.Message}");
            clientRelayConfigured = false;
            return false;
        }
    }

    // Public properties for external access
    public string ActiveRelayJoinCode => activeRelayJoinCode;
    public bool IsUsingRelay => relayModeActive;
}
