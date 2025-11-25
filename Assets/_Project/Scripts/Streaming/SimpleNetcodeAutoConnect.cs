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
    [Header("Relay Fallback")]
    [SerializeField] private bool enableRelayFallback = true;
    [SerializeField] private float lanDiscoveryTimeout = 5f;
    [SerializeField] private float relayHostFallbackDelay = 3f;
    [SerializeField] private string preferredRelayRegion = "";
    [SerializeField] [Tooltip("Optional manual relay join code for clients.")]
    private string manualRelayJoinCode = "";
    [SerializeField] private UnityEvent<string> onRelayJoinCodeGenerated;

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
    private enum TransportMode { Lan, Relay }
    private TransportMode currentTransportMode = TransportMode.Lan;
    private bool relayModeActive = false;
    private string activeRelayJoinCode = string.Empty;
    private bool relayClientFallbackTriggered = false;
    private bool hostRelayConfigured = false;
    private bool clientRelayConfigured = false;
    private RelayServerData hostRelayData;
    private RelayServerData clientRelayData;
    private CancellationTokenSource relayWatchdogCts;
    private Task unityServicesInitTask;
    private bool unityServicesReady = false;
    private float lanDiscoveryStartTime;

    void Awake()
    {
        if (enableRelayFallback)
        {
            InitializeUnityServicesIfNeeded();
        }
    }

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
            lanDiscoveryStartTime = Time.unscaledTime;
        }
    }

    private void OnServerStarted()
    {
        Debug.Log("[NetcodeAutoConnect] Server started successfully");
        if (enableLanDiscovery && !relayModeActive)
        {
            StartLanBroadcast();
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        Debug.Log($"[NetcodeAutoConnect] Client connected with ID: {clientId}");
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && clientId != NetworkManager.Singleton.LocalClientId)
        {
            CancelRelayFallbackTimer();
        }
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

        if (!startAsHost && enableRelayFallback && isWaitingForDiscovery)
        {
            if (Time.unscaledTime - lanDiscoveryStartTime >= lanDiscoveryTimeout)
            {
                Debug.Log("[NetcodeAutoConnect] LAN discovery timed out. Triggering Relay fallback.");
                isWaitingForDiscovery = false;
                TryRelayClientFallback();
            }
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

        if (!startAsHost)
        {
            if (enableLanDiscovery)
            {
                if (!HasDiscoveredEndpoint())
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
                        lanDiscoveryStartTime = Time.unscaledTime;
                    }
                    return;
                }

                serverAddress = lastDiscoveredAddress;
                serverPort = lastDiscoveredPort;
            }
            else if (enableRelayFallback)
            {
                TryRelayClientFallback();
                return;
            }
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

        relayModeActive = false;
        activeRelayJoinCode = string.Empty;
        hostRelayConfigured = false;

        bool success = StartHostInternal(TransportMode.Lan);
        if (!success)
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

        bool success = StartClientInternal(TransportMode.Lan);
        if (!success && NetworkManager.Singleton != null)
        {
            Debug.LogError($"[NetcodeAutoConnect] Failed to start as Client - NetworkManager state IsClient:{NetworkManager.Singleton.IsClient} IsServer:{NetworkManager.Singleton.IsServer} IsHost:{NetworkManager.Singleton.IsHost}");
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
        CancelRelayFallbackTimer();
    }

    void OnGUI()
    {
        if (!hasConnected && NetworkManager.Singleton != null && !NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsServer)
        {
            GUILayout.BeginArea(new Rect(10, 10, 320, 260));
            GUILayout.Label("Netcode Connection");
            if (enableLanDiscovery && !startAsHost && isWaitingForDiscovery)
            {
                GUILayout.Label("Waiting for host broadcast...");
            }
            if (enableRelayFallback)
            {
                if (startAsHost && !string.IsNullOrEmpty(activeRelayJoinCode))
                {
                    GUILayout.Label($"Relay code: {activeRelayJoinCode}");
                }
                else if (!startAsHost)
                {
                    GUILayout.Label("Relay code (fallback):");
                    string newCode = GUILayout.TextField(manualRelayJoinCode ?? string.Empty);
                    if (newCode != manualRelayJoinCode)
                    {
                        SetRelayJoinCode(newCode);
                    }
                }
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
        if (!startAsHost)
        {
            lanDiscoveryStartTime = Time.unscaledTime;
        }
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
            isWaitingForDiscovery = false;
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

    public void SetRelayJoinCode(string joinCode)
    {
        manualRelayJoinCode = string.IsNullOrWhiteSpace(joinCode) ? string.Empty : joinCode.Trim().ToUpperInvariant();
    }

    public string ActiveRelayJoinCode => activeRelayJoinCode;
    public bool IsUsingRelay => relayModeActive;

    private void InitializeUnityServicesIfNeeded()
    {
        if (!enableRelayFallback)
        {
            return;
        }

        if (unityServicesInitTask == null)
        {
            unityServicesInitTask = InitializeUnityServicesAsync();
        }
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

    private async Task<bool> EnsureUnityServicesReadyAsync()
    {
        InitializeUnityServicesIfNeeded();

        if (unityServicesInitTask != null)
        {
            await unityServicesInitTask;
        }

        return unityServicesReady;
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
            manualRelayJoinCode = activeRelayJoinCode;
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

    private bool ConfigureTransportForMode(TransportMode mode)
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[NetcodeAutoConnect] NetworkManager.Singleton is null!");
            return false;
        }

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport == null)
        {
            Debug.LogError("[NetcodeAutoConnect] UnityTransport component not found!");
            return false;
        }

        if (mode == TransportMode.Lan)
        {
            transport.SetConnectionData(serverAddress, serverPort);
            currentTransportMode = TransportMode.Lan;
            relayModeActive = false;
            return true;
        }

        RelayServerData relayData = startAsHost ? hostRelayData : clientRelayData;
        bool relayConfigured = startAsHost ? hostRelayConfigured : clientRelayConfigured;

        if (!relayConfigured)
        {
            Debug.LogError("[NetcodeAutoConnect] Relay data has not been configured for this role.");
            return false;
        }

        transport.SetRelayServerData(relayData);
        currentTransportMode = TransportMode.Relay;
        relayModeActive = true;
        return true;
    }

    private void StartHostRelayFallbackTimer()
    {
        if (!enableRelayFallback || !startAsHost || relayModeActive)
        {
            return;
        }

        CancelRelayFallbackTimer();
        relayWatchdogCts = new CancellationTokenSource();
        _ = HostRelayFallbackRoutine(relayWatchdogCts.Token);
    }

    private void CancelRelayFallbackTimer()
    {
        if (relayWatchdogCts != null)
        {
            relayWatchdogCts.Cancel();
            relayWatchdogCts.Dispose();
            relayWatchdogCts = null;
        }
    }

    private async Task HostRelayFallbackRoutine(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(relayHostFallbackDelay), token);
            if (token.IsCancellationRequested || relayModeActive || !this)
            {
                return;
            }

            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            {
                return;
            }

            if (NetworkManager.Singleton.ConnectedClientsIds.Count > 1)
            {
                return; // Already have a remote client over LAN.
            }

            await SwitchHostToRelayAsync();
        }
        catch (TaskCanceledException)
        {
            // expected when timer is cancelled
        }
    }

    private async Task SwitchHostToRelayAsync()
    {
        if (!enableRelayFallback || relayModeActive || !startAsHost)
        {
            return;
        }

        if (NetworkManager.Singleton == null)
        {
            return;
        }

        CancelRelayFallbackTimer();

        if (NetworkManager.Singleton.ConnectedClientsIds.Count > 1)
        {
            return;
        }

        Debug.Log("[NetcodeAutoConnect] No LAN peer detected in time. Switching host to Relay.");

        if (enableLanDiscovery)
        {
            StopLanBroadcast();
        }

        NetworkManager.Singleton.Shutdown();
        hasConnected = false;

        await Task.Delay(250);

        if (!await ConfigureRelayHostAsync())
        {
            Debug.LogError("[NetcodeAutoConnect] Relay host configuration failed. Staying offline.");
            return;
        }

        bool started = StartHostInternal(TransportMode.Relay);
        if (!started)
        {
            Debug.LogError("[NetcodeAutoConnect] Failed to relaunch host in Relay mode.");
        }
    }

    private async void TryRelayClientFallback()
    {
        if (!enableRelayFallback || relayClientFallbackTriggered || relayModeActive || startAsHost)
        {
            return;
        }

        relayClientFallbackTriggered = true;

        if (string.IsNullOrWhiteSpace(manualRelayJoinCode))
        {
            Debug.LogWarning("[NetcodeAutoConnect] Relay fallback requested but no join code is set. Call SetRelayJoinCode first.");
            relayClientFallbackTriggered = false;
            return;
        }

        Debug.Log("[NetcodeAutoConnect] No LAN host discovered. Attempting Relay client connection.");

        StopListeningForHosts();
        pendingManualConnectRequest = false;

        if (!await ConfigureRelayClientAsync(manualRelayJoinCode))
        {
            Debug.LogError("[NetcodeAutoConnect] Relay client configuration failed.");
            relayClientFallbackTriggered = false;
            return;
        }

        bool success = StartClientInternal(TransportMode.Relay);
        if (!success)
        {
            Debug.LogError("[NetcodeAutoConnect] Failed to start client in Relay mode.");
            relayClientFallbackTriggered = false;
        }
    }

    private bool StartHostInternal(TransportMode mode)
    {
        if (!ConfigureTransportForMode(mode))
        {
            return false;
        }

        bool success = NetworkManager.Singleton.StartHost();
        if (success)
        {
            hasConnected = true;
            Debug.Log($"[NetcodeAutoConnect] Started as Host ({mode}).");
            if (mode == TransportMode.Lan)
            {
                StartHostRelayFallbackTimer();
            }
        }
        return success;
    }

    private bool StartClientInternal(TransportMode mode)
    {
        if (!ConfigureTransportForMode(mode))
        {
            return false;
        }

        bool success = NetworkManager.Singleton.StartClient();
        if (success)
        {
            hasConnected = true;
            Debug.Log($"[NetcodeAutoConnect] Started as Client ({mode}).");
        }

        return success;
    }
}



