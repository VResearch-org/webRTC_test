using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Spawns the NetcodeWebRTCSignaling object when network starts.
/// </summary>
public class NetcodeSignalingSpawner : MonoBehaviour
{
    [SerializeField] private GameObject signalingPrefab;
    [SerializeField] private bool spawnOnHost = true;
    [SerializeField] private bool spawnOnClient = true;

    [SerializeField] UnityEvent onSignallingSpawned;

    private GameObject spawnedSignaling;

    void Start()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted += OnServerStarted;
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        }
    }

    private void OnServerStarted()
    {
        if (spawnOnHost && signalingPrefab != null && spawnedSignaling == null)
        {
            SpawnSignaling();
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        // Spawn on server when client connects (if not already spawned)
        if (NetworkManager.Singleton.IsServer && spawnOnHost && signalingPrefab != null && spawnedSignaling == null)
        {
            SpawnSignaling();
        }
    }


    private void SpawnSignaling()
    {
        if (signalingPrefab == null)
        {
            Debug.LogError("Signaling prefab not assigned!");
            return;
        }

        spawnedSignaling = Instantiate(signalingPrefab);
        NetworkObject networkObject = spawnedSignaling.GetComponent<NetworkObject>();
        
        if (networkObject != null)
        {
            networkObject.Spawn();
            Debug.Log("NetcodeWebRTCSignaling spawned successfully");
            onSignallingSpawned?.Invoke();
        }
        else
        {
            Debug.LogError("Signaling prefab must have a NetworkObject component!");
            Destroy(spawnedSignaling);
            spawnedSignaling = null;
        }
    }

    void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        }
    }
}

