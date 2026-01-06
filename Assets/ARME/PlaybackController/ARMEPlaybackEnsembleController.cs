/*
 * ARME Playback Ensemble Controller
 * 
 * Global controller for managing multiple ARME Playback Controller instances.
 * Provides synchronized Start, Stop, and Reset controls for ensemble playback.
 * 
 * SETUP INSTRUCTIONS:
 * ==================
 * 1. Create a GameObject and attach this script
 * 2. Add multiple ARMEPlaybackControllerExample instances to the scene
 * 3. Drag them into the "Playback Controllers" list in the Inspector
 * 4. Optionally connect UI elements for global control
 * 5. Use the global controls to synchronize all playback controllers
 * 
 * FEATURES:
 * ========
 * - Simultaneous start/stop for all registered controllers
 * - Global reset functionality
 * - UI integration with buttons and status display
 * - Individual controller management
 * - Status monitoring for all controllers
 */

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ARMEPlayback;

namespace ARMEPlayback
{
    /// <summary>
    /// Ensemble controller for managing multiple ARME Playback Controllers
    /// 
    /// BEGINNER GUIDE:
    /// 1. Attach this script to a GameObject in your scene
    /// 2. Create multiple UnityPlaybackControllerExample instances
    /// 3. Add them to the "Playback Controllers" list
    /// 4. Connect UI elements for global control
    /// 5. Use Global Start/Stop/Reset to control all instances simultaneously
    /// 
    /// FEATURES:
    /// - Synchronized ensemble playback
    /// - Global time ratio control
    /// - Individual controller status monitoring
    /// - UI integration for easy control
    /// </summary>
    public class ARMEPlaybackEnsembleController : MonoBehaviour
    {
        [Header("Ensemble Setup")]
        [Tooltip("List of playback controllers to manage")]
        [SerializeField] public List<UnityPlaybackControllerExample> playbackControllers = new List<UnityPlaybackControllerExample>();
        
        [Header("Global Controls")]
        [Tooltip("Global time ratio applied to all controllers")]
        [SerializeField] [Range(0.1f, 4.0f)] private float globalTimeRatio = 1.0f;
        
        [Tooltip("Apply global time ratio to all controllers when changed")]
        [SerializeField] private bool syncTimeRatios = true;
        
        [Header("UI Controls (Optional)")]
        [Tooltip("Button to start all controllers")]
        [SerializeField] private Button globalStartButton;
        
        [Tooltip("Button to stop all controllers")]
        [SerializeField] private Button globalStopButton;
        
        [Tooltip("Button to reset all controllers")]
        [SerializeField] private Button globalResetButton;
        
        [Tooltip("Button to restart all controllers")]
        [SerializeField] private Button globalRestartButton;
        
        [Tooltip("Slider for global time ratio control")]
        [SerializeField] private Slider globalTimeRatioSlider;
        
        [Tooltip("Text display for global time ratio")]
        [SerializeField] private Text globalTimeRatioText;
        
        [Tooltip("Text display for ensemble status")]
        [SerializeField] private Text statusText;
        
        [Header("Status Display")]
        [Tooltip("Update status display in real-time")]
        [SerializeField] private bool showStatusUpdates = true;
        
        [Tooltip("Status update interval in seconds")]
        [SerializeField] [Range(0.1f, 2.0f)] private float statusUpdateInterval = 0.5f;

        // Private members
        private float _lastGlobalTimeRatio;
        private float _statusUpdateTimer = 0f;
        private bool _isPlaying = false;

        /// <summary>
        /// Initialize the ensemble controller and UI
        /// </summary>
        void Start()
        {
            // Remove null references
            playbackControllers.RemoveAll(controller => controller == null);
            
            // Initialize UI controls
            InitializeUI();
            
            // Set initial values
            _lastGlobalTimeRatio = globalTimeRatio;
            
            // Update initial status
            UpdateStatusDisplay();
            
            Debug.Log($"🎼 Ensemble Controller initialized with {playbackControllers.Count} controllers");
        }

        /// <summary>
        /// Initialize UI button handlers and sliders
        /// </summary>
        private void InitializeUI()
        {
            // Setup control buttons
            if (globalStartButton != null)
                globalStartButton.onClick.AddListener(StartAllControllers);
            
            if (globalStopButton != null)
                globalStopButton.onClick.AddListener(StopAllControllers);
            
            if (globalResetButton != null)
                globalResetButton.onClick.AddListener(ResetAllControllers);
            
            if (globalRestartButton != null)
                globalRestartButton.onClick.AddListener(RestartAllControllers);
            
            // Setup time ratio slider
            if (globalTimeRatioSlider != null)
            {
                globalTimeRatioSlider.value = globalTimeRatio;
                globalTimeRatioSlider.onValueChanged.AddListener(OnGlobalTimeRatioChanged);
            }
            
            // Update initial display
            UpdateTimeRatioDisplay();
        }

        /// <summary>
        /// Update loop for status monitoring and sync
        /// </summary>
        void Update()
        {
            // Update global time ratio if changed
            if (syncTimeRatios && Mathf.Abs(globalTimeRatio - _lastGlobalTimeRatio) > 0.001f)
            {
                ApplyGlobalTimeRatio();
                _lastGlobalTimeRatio = globalTimeRatio;
            }
            
            // Update status display periodically
            if (showStatusUpdates)
            {
                _statusUpdateTimer += Time.deltaTime;
                if (_statusUpdateTimer >= statusUpdateInterval)
                {
                    UpdateStatusDisplay();
                    _statusUpdateTimer = 0f;
                }
            }
        }

        /// <summary>
        /// Start all registered playback controllers simultaneously
        /// </summary>
        public void StartAllControllers()
        {
            if (playbackControllers.Count == 0)
            {
                Debug.LogWarning("No playback controllers registered!");
                return;
            }

            Debug.Log($"▶️ Starting {playbackControllers.Count} controllers simultaneously...");
            
            foreach (var controller in playbackControllers)
            {
                if (controller != null)
                {
                    controller.StartPlayback();
                }
            }
            
            _isPlaying = true;
            UpdateStatusDisplay();
            Debug.Log("✅ All controllers started!");
        }

        /// <summary>
        /// Stop all registered playback controllers simultaneously
        /// </summary>
        public void StopAllControllers()
        {
            if (playbackControllers.Count == 0)
            {
                Debug.LogWarning("No playback controllers registered!");
                return;
            }

            Debug.Log($"⏸️ Stopping {playbackControllers.Count} controllers simultaneously...");
            
            foreach (var controller in playbackControllers)
            {
                if (controller != null)
                {
                    controller.StopPlayback();
                }
            }
            
            _isPlaying = false;
            UpdateStatusDisplay();
            Debug.Log("✅ All controllers stopped!");
        }

        /// <summary>
        /// Reset all registered playback controllers to the beginning
        /// </summary>
        public void ResetAllControllers()
        {
            if (playbackControllers.Count == 0)
            {
                Debug.LogWarning("No playback controllers registered!");
                return;
            }

            Debug.Log($"🔄 Resetting {playbackControllers.Count} controllers to beginning...");
            
            foreach (var controller in playbackControllers)
            {
                if (controller != null)
                {
                    controller.ResetPlayback();
                }
            }
            
            _isPlaying = false;
            UpdateStatusDisplay();
            Debug.Log("✅ All controllers reset!");
        }

        /// <summary>
        /// Restart all registered playback controllers simultaneously
        /// </summary>
        public void RestartAllControllers()
        {
            if (playbackControllers.Count == 0)
            {
                Debug.LogWarning("No playback controllers registered!");
                return;
            }

            Debug.Log($"🔄 Restarting {playbackControllers.Count} controllers simultaneously...");
            
            foreach (var controller in playbackControllers)
            {
                if (controller != null)
                {
                    controller.RestartPlayback();
                }
            }
            
            _isPlaying = true;
            UpdateStatusDisplay();
            Debug.Log("✅ All controllers restarted!");
        }

        /// <summary>
        /// Apply global time ratio to all controllers
        /// </summary>
        public void ApplyGlobalTimeRatio()
        {
            if (!syncTimeRatios) return;
            
            foreach (var controller in playbackControllers)
            {
                if (controller != null)
                {
                    controller.SetTimeRatio(globalTimeRatio);
                }
            }
            
            UpdateTimeRatioDisplay();
        }

        /// <summary>
        /// Handle global time ratio slider changes
        /// </summary>
        private void OnGlobalTimeRatioChanged(float value)
        {
            globalTimeRatio = value;
            UpdateTimeRatioDisplay();
        }

        /// <summary>
        /// Update the time ratio display
        /// </summary>
        private void UpdateTimeRatioDisplay()
        {
            if (globalTimeRatioText != null)
            {
                globalTimeRatioText.text = $"Global Time Ratio: {globalTimeRatio:F2}x";
            }
        }

        /// <summary>
        /// Update the status display with ensemble information
        /// </summary>
        private void UpdateStatusDisplay()
        {
            if (statusText == null) return;

            string status = $"🎼 Ensemble Status:\n";
            status += $"Controllers: {playbackControllers.Count}\n";
            status += $"Playing: {(_isPlaying ? "Yes" : "No")}\n";
            status += $"Global Time Ratio: {globalTimeRatio:F2}x\n";
            
            // Count active controllers
            int activeCount = 0;
            int nullCount = 0;
            
            foreach (var controller in playbackControllers)
            {
                if (controller == null)
                {
                    nullCount++;
                }
                else if (controller.IsPlaying)
                {
                    activeCount++;
                }
            }
            
            status += $"Active: {activeCount}\n";
            
            if (nullCount > 0)
            {
                status += $"⚠️ Null references: {nullCount}\n";
            }
            
            statusText.text = status;
        }

        /// <summary>
        /// Add a playback controller to the ensemble
        /// </summary>
        public void AddController(UnityPlaybackControllerExample controller)
        {
            if (controller != null && !playbackControllers.Contains(controller))
            {
                playbackControllers.Add(controller);
                
                // Apply global time ratio if sync is enabled
                if (syncTimeRatios)
                {
                    controller.SetTimeRatio(globalTimeRatio);
                }
                
                Debug.Log($"➕ Added controller to ensemble: {controller.name}");
                UpdateStatusDisplay();
            }
        }

        /// <summary>
        /// Remove a playback controller from the ensemble
        /// </summary>
        public void RemoveController(UnityPlaybackControllerExample controller)
        {
            if (playbackControllers.Remove(controller))
            {
                Debug.Log($"➖ Removed controller from ensemble: {controller?.name}");
                UpdateStatusDisplay();
            }
        }

        /// <summary>
        /// Clear all controllers from the ensemble
        /// </summary>
        public void ClearControllers()
        {
            int count = playbackControllers.Count;
            playbackControllers.Clear();
            Debug.Log($"🧹 Cleared {count} controllers from ensemble");
            UpdateStatusDisplay();
        }

        /// <summary>
        /// Public API for external control
        /// </summary>
        
        /// <summary>
        /// Set global time ratio for all controllers
        /// </summary>
        public void SetGlobalTimeRatio(float ratio)
        {
            globalTimeRatio = Mathf.Clamp(ratio, 0.1f, 4.0f);
            
            if (globalTimeRatioSlider != null)
                globalTimeRatioSlider.value = globalTimeRatio;
            
            if (syncTimeRatios)
                ApplyGlobalTimeRatio();
        }

        /// <summary>
        /// Get the current ensemble playing state
        /// </summary>
        public bool IsEnsemblePlaying => _isPlaying;

        /// <summary>
        /// Get the number of registered controllers
        /// </summary>
        public int ControllerCount => playbackControllers.Count;

        /// <summary>
        /// Validate controller references and settings
        /// </summary>
        void OnValidate()
        {
            globalTimeRatio = Mathf.Clamp(globalTimeRatio, 0.1f, 4.0f);
            statusUpdateInterval = Mathf.Clamp(statusUpdateInterval, 0.1f, 2.0f);
            

        }
    }

    /// <summary>
    /// Simple UI panel controller for the ensemble - attach to a Canvas
    /// </summary>
    public class EnsembleControllerUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private ARMEPlaybackEnsembleController ensembleController;
        [SerializeField] private Button startAllButton;
        [SerializeField] private Button stopAllButton;
        [SerializeField] private Button resetAllButton;
        [SerializeField] private Button restartAllButton;
        [SerializeField] private Slider timeRatioSlider;
        [SerializeField] private Text timeRatioText;
        [SerializeField] private Text statusText;
        [SerializeField] private Toggle syncToggle;

        void Start()
        {
            // Setup UI event handlers
            if (startAllButton != null)
                startAllButton.onClick.AddListener(() => ensembleController?.StartAllControllers());
            
            if (stopAllButton != null)
                stopAllButton.onClick.AddListener(() => ensembleController?.StopAllControllers());
            
            if (resetAllButton != null)
                resetAllButton.onClick.AddListener(() => ensembleController?.ResetAllControllers());
            
            if (restartAllButton != null)
                restartAllButton.onClick.AddListener(() => ensembleController?.RestartAllControllers());
            
            if (timeRatioSlider != null)
                timeRatioSlider.onValueChanged.AddListener(ratio => ensembleController?.SetGlobalTimeRatio(ratio));
        }
    }
}