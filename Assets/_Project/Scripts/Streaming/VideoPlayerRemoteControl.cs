using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;
using System;
using System.Collections;

/// <summary>
/// Controls VideoPlayer remotely from receiver to sender.
/// Set isSender to true on the sender scene, false on the receiver scene.
/// </summary>
public class VideoPlayerRemoteControl : MonoBehaviour
{
    [Header("Scene Configuration")]
    [SerializeField]
    [Tooltip("Set to true on sender scene (headset), false on receiver scene")]
    private bool isSender = false;

    [Header("Sender Settings (only used when isSender = true)")]
    [SerializeField]
    [Tooltip("The VideoPlayer component to control (on sender side)")]
    private VideoPlayer videoPlayer;

    [Header("Receiver Settings (only used when isSender = false)")]
    [SerializeField]
    [Tooltip("The play/pause button (on receiver side)")]
    private Button playPauseButton;

    [SerializeField]
    [Tooltip("The image component that displays the play/pause icon (on receiver side)")]
    private Image playPauseIcon;

    [SerializeField]
    [Tooltip("Play icon sprite (shown when video is paused)")]
    private Sprite playIcon;

    [SerializeField]
    [Tooltip("Pause icon sprite (shown when video is playing)")]
    private Sprite pauseIcon;

    [SerializeField]
    [Tooltip("The slider that shows video progress (0-1) (on receiver side)")]
    private Slider progressSlider;

    [SerializeField]
    [Tooltip("The TextMeshPro text that displays video time in format 00:00/00:00 (on receiver side)")]
    private TextMeshProUGUI timeText;

    [SerializeField]
    [Tooltip("The skip back button (on receiver side)")]
    private Button skipBackButton;

    [Header("Progress Update Settings")]
    [SerializeField]
    [Tooltip("How often to send progress updates (in seconds)")]
    private float progressUpdateInterval = 0.1f;

    private NetcodeWebRTCSignaling signaling;
    private bool isVideoPlaying = false;
    private Coroutine progressUpdateCoroutine;

    void Start()
    {
        if (isSender)
        {
            InitializeSender();
        }
        else
        {
            InitializeReceiver();
        }
    }

    private void InitializeSender()
    {
        // Find VideoPlayer if not assigned
        if (videoPlayer == null)
        {
            videoPlayer = FindObjectOfType<VideoPlayer>();
            if (videoPlayer == null)
            {
                Debug.LogError("[VideoPlayerRemoteControl] VideoPlayer not found and not assigned!");
                return;
            }
        }

        // Ensure video starts paused
        videoPlayer.playOnAwake = false;
        if (videoPlayer.isPlaying)
        {
            videoPlayer.Pause();
        }

        // Wait for signaling to be ready
        StartCoroutine(WaitForSignalingAndSetup());
    }

    private void InitializeReceiver()
    {
        // Validate UI components
        if (playPauseButton == null)
        {
            Debug.LogError("[VideoPlayerRemoteControl] Play/Pause button not assigned!");
            return;
        }

        if (playPauseIcon == null)
        {
            Debug.LogError("[VideoPlayerRemoteControl] Play/Pause icon Image not assigned!");
            return;
        }

        if (playIcon == null || pauseIcon == null)
        {
            Debug.LogError("[VideoPlayerRemoteControl] Play or Pause icon sprites not assigned!");
            return;
        }

        // Set initial state: paused (shows play icon)
        isVideoPlaying = false;
        UpdateButtonIcon();

        // Setup button click handlers
        playPauseButton.onClick.AddListener(OnPlayPauseButtonClicked);
        
        if (skipBackButton != null)
        {
            skipBackButton.onClick.AddListener(OnSkipBackButtonClicked);
        }

        // Wait for signaling to be ready
        StartCoroutine(WaitForSignalingAndSetup());
    }

    private IEnumerator WaitForSignalingAndSetup()
    {
        // Wait for NetworkManager to be available
        while (Unity.Netcode.NetworkManager.Singleton == null)
        {
            yield return null;
        }

        // Wait for signaling component
        while (NetcodeWebRTCSignaling.Instance == null || !NetcodeWebRTCSignaling.Instance.IsReady())
        {
            yield return null;
        }

        signaling = NetcodeWebRTCSignaling.Instance;

        if (isSender)
        {
            // Subscribe to play/pause events
            signaling.OnPlayPauseRequested += HandlePlayPauseRequest;
            // Subscribe to skip back events
            signaling.OnSkipBackRequested += HandleSkipBackRequest;
            
            // Start sending progress updates
            if (progressUpdateCoroutine != null)
            {
                StopCoroutine(progressUpdateCoroutine);
            }
            progressUpdateCoroutine = StartCoroutine(UpdateVideoProgress());
        }
        else
        {
            // Subscribe to progress updates
            signaling.OnVideoProgressUpdated += HandleVideoProgressUpdate;
            // Subscribe to time updates
            signaling.OnVideoTimeUpdated += HandleVideoTimeUpdate;
        }
    }

    private void OnPlayPauseButtonClicked()
    {
        if (signaling == null || !signaling.IsReady())
        {
            Debug.LogWarning("[VideoPlayerRemoteControl] Signaling not ready yet. Cannot send play/pause command.");
            return;
        }

        // Toggle state
        isVideoPlaying = !isVideoPlaying;

        // Send command to sender
        signaling.RequestPlayPause(isVideoPlaying);

        // Update button icon
        UpdateButtonIcon();
    }

    private void HandlePlayPauseRequest(bool shouldPlay)
    {
        if (videoPlayer == null)
        {
            Debug.LogError("[VideoPlayerRemoteControl] VideoPlayer is null!");
            return;
        }

        if (shouldPlay)
        {
            videoPlayer.Play();
            Debug.Log("[VideoPlayerRemoteControl] Video playback started");
        }
        else
        {
            videoPlayer.Pause();
            Debug.Log("[VideoPlayerRemoteControl] Video playback paused");
        }
    }

    private void OnSkipBackButtonClicked()
    {
        if (signaling == null || !signaling.IsReady())
        {
            Debug.LogWarning("[VideoPlayerRemoteControl] Signaling not ready yet. Cannot send skip back command.");
            return;
        }

        // Send skip back command to sender
        signaling.RequestSkipBack();
    }

    private void HandleSkipBackRequest()
    {
        if (videoPlayer == null)
        {
            Debug.LogError("[VideoPlayerRemoteControl] VideoPlayer is null!");
            return;
        }

        // Remember if video was playing
        bool wasPlaying = videoPlayer.isPlaying;

        // Reset video to first frame (time = 0)
        videoPlayer.time = 0;
        videoPlayer.frame = 0;

        // If it was playing, continue playing; if paused, stay paused (just show first frame)
        if (wasPlaying)
        {
            videoPlayer.Play();
            Debug.Log("[VideoPlayerRemoteControl] Video reset to first frame and continued playing");
        }
        else
        {
            videoPlayer.Pause();
            Debug.Log("[VideoPlayerRemoteControl] Video reset to first frame (paused)");
        }
    }

    private void UpdateButtonIcon()
    {
        if (playPauseIcon != null)
        {
            playPauseIcon.sprite = isVideoPlaying ? pauseIcon : playIcon;
        }
    }

    private IEnumerator UpdateVideoProgress()
    {
        while (true)
        {
            yield return new WaitForSeconds(progressUpdateInterval);

            if (videoPlayer != null && signaling != null && signaling.IsReady())
            {
                // Calculate progress (0-1)
                float progress = 0f;
                if (videoPlayer.length > 0)
                {
                    progress = (float)(videoPlayer.time / videoPlayer.length);
                    progress = Mathf.Clamp01(progress);
                }

                // Send progress to receiver
                signaling.SendVideoProgress(progress);
                
                // Send time information to receiver
                signaling.SendVideoTime((float)videoPlayer.time, (float)videoPlayer.length);
            }
        }
    }

    private void HandleVideoProgressUpdate(float progress)
    {
        if (progressSlider != null)
        {
            progressSlider.value = progress;
        }
    }

    private void HandleVideoTimeUpdate(float currentTime, float totalLength)
    {
        if (timeText != null)
        {
            // Format time as MM:SS / MM:SS
            var currentTimeSpan = TimeSpan.FromSeconds(currentTime);
            var totalTimeSpan = TimeSpan.FromSeconds(totalLength);
            
            string currentTimeString = string.Format("{0:D2}:{1:D2}",
                currentTimeSpan.Minutes,
                currentTimeSpan.Seconds);
            
            string totalTimeString = string.Format("{0:D2}:{1:D2}",
                totalTimeSpan.Minutes,
                totalTimeSpan.Seconds);
            
            timeText.text = currentTimeString + "/" + totalTimeString;
        }
    }

    void OnDestroy()
    {
        if (progressUpdateCoroutine != null)
        {
            StopCoroutine(progressUpdateCoroutine);
            progressUpdateCoroutine = null;
        }

        if (signaling != null)
        {
            if (isSender)
            {
                signaling.OnPlayPauseRequested -= HandlePlayPauseRequest;
                signaling.OnSkipBackRequested -= HandleSkipBackRequest;
            }
            else
            {
                signaling.OnVideoProgressUpdated -= HandleVideoProgressUpdate;
                signaling.OnVideoTimeUpdated -= HandleVideoTimeUpdate;
            }
        }

        if (playPauseButton != null)
        {
            playPauseButton.onClick.RemoveListener(OnPlayPauseButtonClicked);
        }

        if (skipBackButton != null)
        {
            skipBackButton.onClick.RemoveListener(OnSkipBackButtonClicked);
        }
    }
}

