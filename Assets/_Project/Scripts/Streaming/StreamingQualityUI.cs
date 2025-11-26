using UnityEngine;
using TMPro;

/// <summary>
/// UI Bridge for streaming quality settings.
/// Connects a TMP_Dropdown to the resolution change system.
/// </summary>
public class StreamingQualityUI : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TMP_Dropdown resolutionDropdown;

    // Resolution presets: HD, Full HD, QHD
    private readonly (int width, int height)[] resolutions = new (int, int)[]
    {
        (640, 480),    // SD
        (1280, 720),   // HD
        (1920, 1080),  // Full HD
        (2560, 1440)   // QHD
    };

    private readonly string[] resolutionNames = new string[]
    {
        "SD",
        "HD",
        "Full HD",
        "QHD"
    };

    void Start()
    {
        // Auto-find dropdown if not assigned
        if (resolutionDropdown == null)
        {
            resolutionDropdown = GetComponent<TMP_Dropdown>();
        }

        // Setup dropdown if found
        if (resolutionDropdown != null)
        {
            SetupDropdown();
        }
        else
        {
            Debug.LogWarning("[StreamingQualityUI] TMP_Dropdown component not found. Please assign it in the inspector.");
        }
    }

    private void SetupDropdown()
    {
        // Clear existing options
        resolutionDropdown.ClearOptions();

        // Add resolution options
        var options = new System.Collections.Generic.List<string>(resolutionNames);
        resolutionDropdown.AddOptions(options);

        // Set default to SD (index 0)
        resolutionDropdown.value = 0;
        resolutionDropdown.RefreshShownValue();

        // Subscribe to dropdown value changes
        resolutionDropdown.onValueChanged.AddListener(OnResolutionChanged);
    }

    private void OnResolutionChanged(int index)
    {
        if (index < 0 || index >= resolutions.Length)
        {
            Debug.LogError($"[StreamingQualityUI] Invalid dropdown index: {index}");
            return;
        }

        var resolution = resolutions[index];
        Debug.Log($"[StreamingQualityUI] Resolution changed to: {resolutionNames[index]} ({resolution.width}x{resolution.height})");

        // Use the singleton Instance instead of FindObjectByType
        // The Instance is set in OnNetworkSpawn() when the NetworkObject is spawned
        var signaling = NetcodeWebRTCSignaling.Instance;
        
        if (signaling == null)
        {
            Debug.LogWarning("[StreamingQualityUI] NetcodeWebRTCSignaling.Instance is null. Is the signaling object spawned?");
            return;
        }

        if (!signaling.IsReady())
        {
            Debug.LogWarning("[StreamingQualityUI] NetcodeWebRTCSignaling is not ready yet. Wait for it to be spawned.");
            return;
        }

        signaling.RequestResolutionChange(resolution.width, resolution.height);
    }

    void OnDestroy()
    {
        if (resolutionDropdown != null)
        {
            resolutionDropdown.onValueChanged.RemoveListener(OnResolutionChanged);
        }
    }
}
