/*
 * ARME Onset-Based Ensemble Controller
 * 
 * Central coordinator for onset-based time stretching across multiple playback controllers
 * 
 * ARCHITECTURE:
 * This controller is the "conductor" that manages timing, coordination, and onset scheduling
 * across multiple ARMEOnsetBasedPlaybackController instances.
 * 
 * SETUP INSTRUCTIONS:
 * ==================
 * 1. Create a GameObject and attach this script
 * 2. Create multiple ARMEOnsetBasedPlaybackController instances in the scene
 * 3. Add them to the "Controllers" list in the Inspector
 * 4. Configure desired onset timing for each controller
 * 5. Use ensemble controls to coordinate all playback controllers
 * 
 * FEATURES:
 * ========
 * - Global playback time tracking for precise onset scheduling
 * - Individual desired onset timing per controller
 * - Automatic time ratio calculation and application
 * - Synchronized ensemble control (start/stop/reset)
 * - Real-time onset progression monitoring
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace ARMEPlayback
{
    /// <summary>
    /// Configuration for individual controller desired onset timing
    /// </summary>
    [System.Serializable]
    public class DesiredOnsetConfig 
    {
        [Header("Controller Reference")]
        [Tooltip("The playback controller this configuration applies to")]
        public ARMEOnsetBasedPlaybackController controller;

        [Tooltip("Optional video for this part. Its playback speed is warped in lockstep with the audio so the picture stays locked to the onset grid (and to the other parts).")]
        public VideoPlayer videoPlayer;

        [Header("Desired Timing Configuration")]
        [Tooltip("Generate desired onset times automatically from constant interval")]
        public bool autoGenerate = true;
        
        [Tooltip("Constant interval between desired onsets (e.g., 0.3 seconds)")]
        [Range(0.1f, 2.0f)]
        public float constantInterval = 0.3f;
        
        [Tooltip("Manual override for desired onset times (used when autoGenerate is false)")]
        public List<float> customDesiredOnsetTimes = new List<float>();
        
        [Header("Future Enhancement")]
        [Tooltip("Desired real-time position for the first onset (e.g., 1.0s)")]
        public float desiredFirstOnsetTime = 0.0f;
        
        [Tooltip("Enable padding to align first onset at specific time")]
        public bool enablePadding = false;
        
        [Tooltip("Calculated padding amount (read-only)")]
        [SerializeField] private float _calculatedPadding = 0.0f;
        
        [Header("Status (Read-Only)")]
        [Tooltip("Generated or configured desired onset times")]
        [SerializeField] private List<float> _desiredOnsetTimes = new List<float>();
        
        [Tooltip("Current onset index for this controller")]
        [SerializeField] private int _currentOnsetIndex = 0;
        
        [Tooltip("Last applied time ratio for this controller")]
        [SerializeField] private float _lastAppliedRatio = 1.0f;

        /// <summary>
        /// Calculate padding needed to align first onset at desired time
        /// </summary>
        public float CalculatePadding(List<float> originalOnsets)
        {
            if (!enablePadding || originalOnsets.Count == 0)
            {
                _calculatedPadding = 0.0f;
                return _calculatedPadding;
            }
            
            float firstOriginalOnset = originalOnsets[0];
            _calculatedPadding = desiredFirstOnsetTime - firstOriginalOnset;
            
            // Ensure padding is non-negative
            _calculatedPadding = Mathf.Max(0.0f, _calculatedPadding);
            
            return _calculatedPadding;
        }
        
        /// <summary>
        /// Get padded (adjusted) original onset times
        /// </summary>
        public List<float> GetPaddedOriginalOnsets(List<float> originalOnsets)
        {
            List<float> paddedOnsets = new List<float>();
            float padding = CalculatePadding(originalOnsets);
            
            foreach (float onset in originalOnsets)
            {
                paddedOnsets.Add(onset + padding);
            }
            
            return paddedOnsets;
        }
        public List<float> GetDesiredOnsetTimes()
        {
            return new List<float>(_desiredOnsetTimes);
        }
        
        /// <summary>
        /// Update desired onset times based on configuration.
        /// </summary>
        /// <param name="commonFirstOnset">
        /// When &gt;= 0, every part's grid starts at this same time instead of the part's own
        /// first onset, so the parts lock to one shared beat grid. Pass -1 for the legacy
        /// per-part behaviour.
        /// </param>
        public void UpdateDesiredOnsetTimes(float commonFirstOnset = -1f)
        {
            _desiredOnsetTimes.Clear();

            if (controller == null)
                return;

            List<float> originalOnsets = controller.GetOriginalOnsetTimes();

            if (originalOnsets.Count == 0)
                return;

            if (autoGenerate)
            {
                // Calculate padding and get padded original onsets
                float padding = CalculatePadding(originalOnsets);
                List<float> paddedOriginalOnsets = GetPaddedOriginalOnsets(originalOnsets);

                // Generate desired onset times: first matches the (optionally shared) grid start,
                // then evenly spaced intervals.
                float firstPaddedOnset = commonFirstOnset >= 0f
                    ? commonFirstOnset
                    : (paddedOriginalOnsets.Count > 0 ? paddedOriginalOnsets[0] : padding);

                for (int i = 0; i < originalOnsets.Count; i++)
                {
                    // First onset matches padded timing, subsequent onsets use constant interval
                    _desiredOnsetTimes.Add(firstPaddedOnset + i * constantInterval);
                }
            }
            else
            {
                // Use custom desired onset times
                _desiredOnsetTimes.AddRange(customDesiredOnsetTimes);
                
                // Ensure we have enough desired times for all original onsets
                while (_desiredOnsetTimes.Count < originalOnsets.Count)
                {
                    float padding = CalculatePadding(originalOnsets);
                    float lastTime = _desiredOnsetTimes.Count > 0 ? _desiredOnsetTimes[_desiredOnsetTimes.Count - 1] : 
                                    (enablePadding ? desiredFirstOnsetTime : padding);
                    _desiredOnsetTimes.Add(lastTime + constantInterval);
                }
            }
        }
        
        // Accessors for internal state management
        public int CurrentOnsetIndex 
        { 
            get => _currentOnsetIndex; 
            set => _currentOnsetIndex = value; 
        }
        
        public float LastAppliedRatio 
        { 
            get => _lastAppliedRatio; 
            set => _lastAppliedRatio = value; 
        }
        
        public List<float> DesiredOnsetTimes => _desiredOnsetTimes;
        
        /// <summary>
        /// Get the calculated padding amount
        /// </summary>
        public float CalculatedPadding => _calculatedPadding;
    }

    /// <summary>
    /// Central ensemble controller for coordinating onset-based time stretching
    /// 
    /// RESPONSIBILITIES:
    /// - Track global playback time with high precision
    /// - Schedule time ratio adjustments for each controller at precise moments
    /// - Manage individual desired onset timing configurations
    /// - Coordinate ensemble start/stop/reset operations
    /// - Monitor onset progression across all controllers
    /// </summary>
    public class ARMEOnsetBasedEnsembleController : MonoBehaviour
    {
        [Header("Ensemble Setup")]
        [Tooltip("List of onset-based playback controllers to coordinate")]
        [SerializeField] public List<DesiredOnsetConfig> controllerConfigs = new List<DesiredOnsetConfig>();
        
        [Header("Global Timing")]
        [Tooltip("Use high-precision DSP time for timing (recommended)")]
        [SerializeField] private bool useHighPrecisionTiming = true;

        [Tooltip("Common beat-grid spacing (seconds) given to auto-wired parts. ~0.226 matches the average onset spacing of the quartet recordings, so the time-warp is gentle and tempo stays close to the originals. Increase to slow the ensemble down, decrease to speed it up.")]
        [SerializeField] [Range(0.1f, 2.0f)] private float defaultDesiredInterval = 0.226f;

        [Tooltip("Automatically start the synchronized ensemble (audio + video) when the scene plays, without needing a Start button.")]
        [SerializeField] private bool autoStartOnPlay = true;
        
        [Header("Playback Status")]
        [Tooltip("Current global playback time")]
        [SerializeField] private double globalPlaybackTime = 0.0;
        
        [Tooltip("Is the ensemble currently playing")]
        [SerializeField] private bool isEnsemblePlaying = false;
        
        [Tooltip("Playback start time reference")]
        [SerializeField] private double playbackStartTime = 0.0;
        
        [Header("UI Controls (Optional)")]
        [Tooltip("Button to start ensemble playback")]
        [SerializeField] private Button startButton;
        
        [Tooltip("Button to stop ensemble playback")]
        [SerializeField] private Button stopButton;
        
        [Tooltip("Button to reset ensemble")]
        [SerializeField] private Button resetButton;
        
        [Tooltip("Text display for ensemble status")]
        [SerializeField] private Text statusText;
        
        [Tooltip("Text display for timing information")]
        [SerializeField] private Text timingText;
        
        [Header("Debug Settings")]
        [Tooltip("Show detailed debug logging")]
        [SerializeField] private bool enableDebugLogging = true;
        
        [Tooltip("Update status display in real-time")]
        [SerializeField] private bool showStatusUpdates = true;
        
        [Tooltip("Status update interval in seconds")]
        [SerializeField] [Range(0.1f, 1.0f)] private float statusUpdateInterval = 0.2f;

        [Header("Video Sync")]
        [Tooltip("Drive each config's VideoPlayer in lockstep with its time-warped audio.")]
        [SerializeField] private bool driveVideo = true;

        [Tooltip("Force every part onto one shared beat grid (same first-onset time + interval) so the parts lock to each other. Turn off to keep each part's own first-onset offset.")]
        [SerializeField] private bool alignPartsToCommonGrid = true;

        [Tooltip("Real time (seconds) at which the shared beat grid's first onset lands, when 'Align Parts To Common Grid' is on.")]
        [SerializeField] [Range(0f, 2f)] private float commonGridStartTime = 0.1f;

        [Tooltip("If a video drifts from its expected (warped) source position by more than this many seconds, snap-correct it. Larger = smoother but looser sync.")]
        [SerializeField] [Range(0.02f, 0.5f)] private float videoDriftThreshold = 0.06f;

        // Private members
        private float _statusUpdateTimer = 0f;
        private int _totalOnsetsProcessed = 0;
        private bool _autoStartPending = false;
        private float _autoStartArmedTime = 0f;

        #region Unity Lifecycle

        /// <summary>
        /// Initialize the ensemble controller
        /// </summary>
        void Start()
        {
            InitializeEnsemble();
        }

        /// <summary>
        /// Update timing and onset scheduling
        /// </summary>
        void FixedUpdate()
        {
            // Deferred auto-start: wait until every per-part controller has initialized
            // (Unity Start order is undefined) and the videos have finished preparing, so
            // audio and picture begin together. A timeout guards against a video that never
            // prepares.
            if (_autoStartPending && !isEnsemblePlaying)
            {
                bool videosReady = !driveVideo || AllAssignedVideosPrepared();
                bool timedOut = Time.time - _autoStartArmedTime > 5f;
                if (videosReady || timedOut)
                {
                    _autoStartPending = false;
                    StartEnsemblePlayback();
                }
            }

            if (isEnsemblePlaying)
            {
                UpdateGlobalTiming();
                ProcessOnsetScheduling();
                DriveVideoDrift();
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
        /// Update configuration when inspector values change
        /// </summary>
        void OnValidate()
        {
            // Update desired onset times when configuration changes
            float commonFirst = GetCommonFirstOnset();
            foreach (var config in controllerConfigs)
            {
                config.UpdateDesiredOnsetTimes(commonFirst);

                // Recalculate padded onset times in the controller for preview only
                // Actual audio padding will be applied during runtime initialization
                if (config.controller != null && config.enablePadding)
                {
                    List<float> originalOnsets = config.controller.GetOriginalOnsetTimes();
                    if (originalOnsets.Count > 0)
                    {
                        float paddingAmount = config.CalculatePadding(originalOnsets);
                        config.controller.RecalculatePaddedOnsets(paddingAmount);
                    }
                }
                else if (config.controller != null)
                {
                    // No padding - recalculate with zero padding for preview
                    config.controller.RecalculatePaddedOnsets(0.0f);
                }
            }
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the ensemble controller and UI
        /// </summary>
        private void InitializeEnsemble()
        {
            // Remove null configurations
            controllerConfigs.RemoveAll(config => config.controller == null);
            
            // Initialize UI controls
            InitializeUI();

            // Update desired onset times for all configurations
            float commonFirst = GetCommonFirstOnset();
            foreach (var config in controllerConfigs)
            {
                config.UpdateDesiredOnsetTimes(commonFirst);

                // Request audio padding if enabled
                // Padding will be applied during controller initialization (Start method)
                ApplyPaddingToController(config);
            }

            // Validate all controllers
            ValidateControllers();

            // Prepare any assigned videos so they can start in sync with the audio.
            PrepareVideos();
            
            if (enableDebugLogging)
                Debug.Log($"🎼 [{gameObject.name}] Ensemble Controller initialized with {controllerConfigs.Count} controllers - padding requested");

            // Begin synchronized playback once controllers + videos are ready, if requested.
            _autoStartPending = autoStartOnPlay;
            _autoStartArmedTime = Time.time;

            UpdateStatusDisplay();
        }

        /// <summary>
        /// Initialize UI button handlers
        /// </summary>
        private void InitializeUI()
        {
            if (startButton != null)
                startButton.onClick.AddListener(StartEnsemblePlayback);
            
            if (stopButton != null)
                stopButton.onClick.AddListener(StopEnsemblePlayback);
            
            if (resetButton != null)
                resetButton.onClick.AddListener(ResetEnsemblePlayback);
        }

        /// <summary>
        /// Apply audio padding to a specific controller if configured
        /// Called during initialization when controllers are actually loaded
        /// </summary>
        private void ApplyPaddingToController(DesiredOnsetConfig config)
        {
            if (config.controller == null || !config.enablePadding)
                return;
                
            List<float> originalOnsets = config.controller.GetOriginalOnsetTimes();
            if (originalOnsets.Count == 0)
                return;
            
            float paddingAmount = config.CalculatePadding(originalOnsets);
            
            if (paddingAmount > 0.0f)
            {
                if (enableDebugLogging)
                {
                    Debug.Log($"[{gameObject.name}] Applying {paddingAmount:F3}s padding to controller '{config.controller.name}'");
                    Debug.Log($"  First onset: {originalOnsets[0]:F3}s → {config.desiredFirstOnsetTime:F3}s");
                }
                
                // Apply actual audio buffer padding during runtime initialization
                config.controller.InitializeWithPadding(paddingAmount);
                
                if (enableDebugLogging)
                {
                    Debug.Log($"[{gameObject.name}] Audio buffer padding applied to '{config.controller.name}'");
                }
            }
        }
        private void ValidateControllers()
        {
            foreach (var config in controllerConfigs)
            {
                if (config.controller != null)
                {
                    string errorMessage;
                    if (!config.controller.ValidateSetup(out errorMessage))
                    {
                        Debug.LogWarning($"[{gameObject.name}] Controller '{config.controller.name}' validation failed: {errorMessage}");
                    }
                }
            }
        }

        #endregion

        #region Global Timing Management

        /// <summary>
        /// Update the global playback time with high precision
        /// </summary>
        private void UpdateGlobalTiming()
        {
            if (useHighPrecisionTiming)
            {
                // Use DSP time for sample-accurate timing
                globalPlaybackTime = AudioSettings.dspTime - playbackStartTime;
            }
            else
            {
                // Use Unity time (less precise but simpler)
                globalPlaybackTime = Time.time - playbackStartTime;
            }
        }

        #endregion

        #region Onset Scheduling

        /// <summary>
        /// Process onset scheduling for all controllers
        /// This is where the magic happens - checking if we need to apply new time ratios
        /// </summary>
        private void ProcessOnsetScheduling()
        {
            foreach (var config in controllerConfigs)
            {
                ProcessControllerOnsetScheduling(config);
            }
        }

        /// <summary>
        /// Process onset scheduling for a specific controller
        /// </summary>
        private void ProcessControllerOnsetScheduling(DesiredOnsetConfig config)
        {
            if (config.controller == null || config.DesiredOnsetTimes.Count == 0)
                return;

            // Use padded onset times if padding is applied, otherwise use original.
            // Read-only views avoid a per-FixedUpdate List copy (behaviour unchanged).
            IReadOnlyList<float> onsetTimes = config.controller.HasPadding ?
                config.controller.PaddedOnsetTimes :
                config.controller.OriginalOnsetTimes;

            if (onsetTimes.Count == 0)
                return;

            // Check if we've reached the time for the next onset adjustment
            int nextOnsetIndex = config.CurrentOnsetIndex + 1;
            
            // Process all onsets consistently: when we reach desired time for onset N, apply ratio for onset N + 1
            if (nextOnsetIndex < config.DesiredOnsetTimes.Count && nextOnsetIndex < onsetTimes.Count)
            {
                double currentDesiredTime = config.DesiredOnsetTimes[config.CurrentOnsetIndex];
                
                // Check if we've reached the desired time for the current onset
                if (globalPlaybackTime >= currentDesiredTime)
                {
                    // Apply timing adjustment for the current onset we've reached
                    float nextOriginalTime = onsetTimes[nextOnsetIndex];
                    float nextDesiredTime = config.DesiredOnsetTimes[nextOnsetIndex];
                    
                    ApplyOnsetTimeRatio(config, nextOriginalTime, nextDesiredTime, nextOnsetIndex);
                    
                    // Move to the next onset
                    config.CurrentOnsetIndex = nextOnsetIndex;
                    config.controller.SetOnsetIndex(nextOnsetIndex);
                }
            }
        }

        /// <summary>
        /// Apply time ratio adjustment to a specific controller
        /// </summary>
        private void ApplyOnsetTimeRatio(DesiredOnsetConfig config, float originalTime, float desiredTime, int onsetIndex)
        {
            config.controller.ApplyOnsetTimeRatio(originalTime, desiredTime);
            config.LastAppliedRatio = config.controller.GetCurrentTimeRatio();

            // The native ratio is a duration/stretch factor; VideoPlayer expects
            // playback speed, so use the inverse to keep picture and sound aligned.
            if (driveVideo && config.videoPlayer != null && config.videoPlayer.canSetPlaybackSpeed)
            {
                float videoSpeed = 1f / Mathf.Max(0.01f, config.LastAppliedRatio);
                config.videoPlayer.playbackSpeed = Mathf.Clamp(videoSpeed, 0.25f, 3f);
            }

            _totalOnsetsProcessed++;
            
            if (enableDebugLogging)
            {
                Debug.Log($"🎯 [{config.controller.name}] Onset {onsetIndex}: {originalTime:F3}s → {desiredTime:F3}s " +
                         $"(ratio: {config.LastAppliedRatio:F3}) at time {globalPlaybackTime:F3}s");
            }
        }

        #endregion

        #region Ensemble Control

        /// <summary>
        /// Start coordinated playback across all controllers
        /// </summary>
        public void StartEnsemblePlayback()
        {
            if (controllerConfigs.Count == 0)
            {
                Debug.LogWarning($"[{gameObject.name}] No controllers configured!");
                return;
            }

            if (enableDebugLogging)
                Debug.Log($"▶️ [{gameObject.name}] Starting ensemble with {controllerConfigs.Count} controllers...");

            // Record the start time
            if (useHighPrecisionTiming) {
                playbackStartTime = AudioSettings.dspTime;
                Debug.Log($"▶️ [{gameObject.name}] Using DSP time for high-precision timing: {playbackStartTime:F3}s");
            }
            else
            {
                playbackStartTime = Time.time;
                Debug.Log($"▶️ [{gameObject.name}] Using Unity time for high-precision timing: {playbackStartTime:F3}s");
            }

            globalPlaybackTime = 0.0;
            isEnsemblePlaying = true;

            // Start all controllers
            foreach (var config in controllerConfigs)
            {
                if (config.controller != null)
                {
                    config.controller.StartPlayback();
                }
            }

            // Start all videos from the top, in lockstep with the audio.
            StartVideos();

            _totalOnsetsProcessed = 0;

            if (enableDebugLogging)
                Debug.Log($"✅ [{gameObject.name}] Ensemble playback started!");

            UpdateStatusDisplay();
        }

        /// <summary>
        /// Stop coordinated playback across all controllers
        /// </summary>
        public void StopEnsemblePlayback()
        {
            if (!isEnsemblePlaying)
                return;

            if (enableDebugLogging)
                Debug.Log($"⏸️ [{gameObject.name}] Stopping ensemble playback...");

            isEnsemblePlaying = false;

            // Stop all controllers
            foreach (var config in controllerConfigs)
            {
                if (config.controller != null)
                {
                    config.controller.StopPlayback();
                }
            }

            // Pause videos in place (rewind happens on Reset, not Stop).
            StopVideos(false);

            if (enableDebugLogging)
                Debug.Log($"✅ [{gameObject.name}] Ensemble playback stopped!");

            UpdateStatusDisplay();
        }

        /// <summary>
        /// Reset all controllers to beginning
        /// </summary>
        public void ResetEnsemblePlayback()
        {
            if (enableDebugLogging)
                Debug.Log($"🔄 [{gameObject.name}] Resetting ensemble...");

            // Stop playback if running
            if (isEnsemblePlaying)
            {
                StopEnsemblePlayback();
            }

            // Reset all controllers and configurations
            foreach (var config in controllerConfigs)
            {
                if (config.controller != null)
                {
                    config.controller.ResetPlayback();
                    config.CurrentOnsetIndex = 0;
                    config.LastAppliedRatio = 1.0f;
                }
            }

            // Rewind videos to the top.
            StopVideos(true);

            // Reset timing
            globalPlaybackTime = 0.0;
            playbackStartTime = 0.0;
            _totalOnsetsProcessed = 0;

            if (enableDebugLogging)
                Debug.Log($"✅ [{gameObject.name}] Ensemble reset complete!");

            UpdateStatusDisplay();
        }

        #endregion

        #region Configuration Management

        /// <summary>
        /// Add a controller to the ensemble with default configuration
        /// </summary>
        public void AddController(ARMEOnsetBasedPlaybackController controller)
        {
            if (controller == null)
                return;

            // Check if already added
            foreach (var config in controllerConfigs)
            {
                if (config.controller == controller)
                    return;
            }

            // Create new configuration
            var newConfig = new DesiredOnsetConfig
            {
                controller = controller,
                autoGenerate = true,
                constantInterval = defaultDesiredInterval
            };

            newConfig.UpdateDesiredOnsetTimes(GetCommonFirstOnset());
            controllerConfigs.Add(newConfig);

            if (enableDebugLogging)
                Debug.Log($"➕ [{gameObject.name}] Added controller: {controller.name}");

            UpdateStatusDisplay();
        }

        /// <summary>
        /// Remove a controller from the ensemble
        /// </summary>
        public void RemoveController(ARMEOnsetBasedPlaybackController controller)
        {
            controllerConfigs.RemoveAll(config => config.controller == controller);

            if (enableDebugLogging)
                Debug.Log($"➖ [{gameObject.name}] Removed controller: {controller?.name}");

            UpdateStatusDisplay();
        }

        /// <summary>
        /// Update desired onset times for all controllers
        /// </summary>
        public void RefreshAllDesiredOnsetTimes()
        {
            float commonFirst = GetCommonFirstOnset();
            foreach (var config in controllerConfigs)
            {
                config.UpdateDesiredOnsetTimes(commonFirst);
            }

            if (enableDebugLogging)
                Debug.Log($"🔄 [{gameObject.name}] Refreshed all desired onset times");
        }

        /// <summary>
        /// Shared first-onset time for the common beat grid, or -1 to keep each part's own.
        /// </summary>
        private float GetCommonFirstOnset()
        {
            return alignPartsToCommonGrid ? commonGridStartTime : -1f;
        }

        #endregion

        #region Video Sync

        /// <summary>
        /// Prepare every assigned video and configure it for ensemble-driven playback.
        /// The _TB videos carry no audio track, so audio is routed through the
        /// time-stretched WAV (the playback controller) and the video's own audio is muted.
        /// </summary>
        private void PrepareVideos()
        {
            if (!driveVideo)
                return;

            foreach (var config in controllerConfigs)
            {
                var vp = config.videoPlayer;
                if (vp == null)
                    continue;

                vp.audioOutputMode = VideoAudioOutputMode.None; // sound comes from the warped WAV, not the clip
                vp.playOnAwake = false;
                vp.isLooping = false;                           // looping independently would desync; the ensemble drives timing
                vp.skipOnDrop = true;
                vp.playbackSpeed = 1f;

                if (!vp.isPrepared)
                    vp.Prepare();
            }
        }

        /// <summary>
        /// True when every config that has a VideoPlayer has finished preparing.
        /// </summary>
        private bool AllAssignedVideosPrepared()
        {
            foreach (var config in controllerConfigs)
            {
                if (config.videoPlayer != null && !config.videoPlayer.isPrepared)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Start all videos from the top, aligned with the audio start.
        /// </summary>
        private void StartVideos()
        {
            if (!driveVideo)
                return;

            foreach (var config in controllerConfigs)
            {
                var vp = config.videoPlayer;
                if (vp == null)
                    continue;

                vp.playbackSpeed = 1f;
                vp.time = 0.0;
                vp.Play();
            }
        }

        /// <summary>
        /// Pause every video, optionally rewinding to the start.
        /// </summary>
        private void StopVideos(bool rewind)
        {
            foreach (var config in controllerConfigs)
            {
                var vp = config.videoPlayer;
                if (vp == null)
                    continue;

                vp.Pause();
                if (rewind)
                {
                    vp.time = 0.0;
                    vp.playbackSpeed = 1f;
                }
            }
        }

        /// <summary>
        /// Safety net: if a video has drifted from where the onset warp says its source
        /// should be at the current global time, snap it back. Playback speed (set per
        /// onset) does the smooth tracking; this only catches accumulated error.
        /// </summary>
        private void DriveVideoDrift()
        {
            if (!driveVideo)
                return;

            foreach (var config in controllerConfigs)
            {
                var vp = config.videoPlayer;
                if (vp == null || !vp.isPrepared || config.controller == null)
                    continue;

                float expected = ExpectedSourceTime(config, (float)globalPlaybackTime);
                if (expected < 0f)
                    continue;

                // Clamp to the clip length so we never seek past the end.
                if (vp.length > 0.0)
                    expected = Mathf.Clamp(expected, 0f, (float)vp.length);

                if (Mathf.Abs((float)vp.time - expected) > videoDriftThreshold)
                    vp.time = expected;
            }
        }

        /// <summary>
        /// Where the part's original recording should be playing (source time, seconds) at
        /// global real time <paramref name="gt"/>, given the piecewise-linear warp from the
        /// part's onset times onto its desired grid times. Returns -1 if no onset data.
        /// </summary>
        private float ExpectedSourceTime(DesiredOnsetConfig config, float gt)
        {
            IReadOnlyList<float> original = config.controller.HasPadding
                ? config.controller.PaddedOnsetTimes
                : config.controller.OriginalOnsetTimes;
            List<float> desired = config.DesiredOnsetTimes;

            int n = Mathf.Min(original.Count, desired.Count);
            if (n == 0)
                return -1f;

            // Before the first grid onset the audio runs at ratio 1.0 from the top.
            if (gt <= desired[0])
                return gt;

            for (int i = 0; i < n - 1; i++)
            {
                if (gt >= desired[i] && gt < desired[i + 1])
                {
                    float realSpan = desired[i + 1] - desired[i];
                    if (realSpan <= 1e-6f)
                        return original[i];
                    float frac = (gt - desired[i]) / realSpan;
                    // The first segment actually starts from desired[0]: before the first
                    // onset the audio ran at ratio 1.0, so source == real time there. Every
                    // later segment is anchored on the part's own onset time.
                    float segStart = (i == 0) ? desired[0] : original[i];
                    return segStart + (original[i + 1] - segStart) * frac;
                }
            }

            // Past the last onset: extrapolate at the final segment's ratio.
            if (n >= 2)
            {
                float realSpan = desired[n - 1] - desired[n - 2];
                float ratio = realSpan > 1e-6f ? (original[n - 1] - original[n - 2]) / realSpan : 1f;
                return original[n - 1] + (gt - desired[n - 1]) * ratio;
            }

            return original[n - 1] + (gt - desired[n - 1]);
        }

        #endregion

        #region Status and UI

        /// <summary>
        /// Update status display
        /// </summary>
        private void UpdateStatusDisplay()
        {
            UpdateMainStatus();
            UpdateTimingDisplay();
        }

        /// <summary>
        /// Update main status text
        /// </summary>
        private void UpdateMainStatus()
        {
            if (statusText == null) return;

            string status = $"🎼 Ensemble Status:\n";
            status += $"Controllers: {controllerConfigs.Count}\n";
            status += $"Playing: {(isEnsemblePlaying ? "Yes" : "No")}\n";
            status += $"Onsets Processed: {_totalOnsetsProcessed}\n";

            // Count active controllers
            int activeCount = 0;
            int validCount = 0;

            foreach (var config in controllerConfigs)
            {
                if (config.controller != null)
                {
                    validCount++;
                    if (config.controller.IsPlaying)
                        activeCount++;
                }
            }

            status += $"Valid: {validCount}, Active: {activeCount}\n";

            statusText.text = status;
        }

        /// <summary>
        /// Update timing display
        /// </summary>
        private void UpdateTimingDisplay()
        {
            if (timingText == null) return;

            string timing = $"⏰ Timing:\n";
            timing += $"Global Time: {globalPlaybackTime:F3}s\n";
            timing += $"Precision: {(useHighPrecisionTiming ? "DSP" : "Unity")}\n";

            // Show next onset timing for first controller (if any)
            if (controllerConfigs.Count > 0 && controllerConfigs[0].controller != null)
            {
                var config = controllerConfigs[0];
                int nextIndex = config.CurrentOnsetIndex + 1;
                if (nextIndex < config.DesiredOnsetTimes.Count)
                {
                    float nextDesiredTime = config.DesiredOnsetTimes[nextIndex];
                    timing += $"Next Onset: {nextDesiredTime:F3}s\n";
                    timing += $"Time Until: {nextDesiredTime - globalPlaybackTime:F3}s\n";
                }
            }

            timingText.text = timing;
        }

        #endregion

        #region Public Properties

        /// <summary>
        /// Is the ensemble currently playing
        /// </summary>
        public bool IsPlaying => isEnsemblePlaying;

        /// <summary>
        /// Current global playback time
        /// </summary>
        public double GlobalPlaybackTime => globalPlaybackTime;

        /// <summary>
        /// Number of configured controllers
        /// </summary>
        public int ControllerCount => controllerConfigs.Count;

        /// <summary>
        /// Total number of onsets processed across all controllers
        /// </summary>
        public int TotalOnsetsProcessed => _totalOnsetsProcessed;

        #endregion

#if UNITY_EDITOR
        #region Editor Auto-Wire

        /// <summary>
        /// One-click setup: for every VideoPlayer in the scene, find the matching WAV and
        /// onset (-CRNNManual) asset by name, create an audio part (AudioSource +
        /// playback controller), and link it to the video in a new config. Run from the
        /// component's context menu (gear icon) in the Inspector while in Edit mode.
        /// </summary>
        [ContextMenu("Auto-Wire Parts From Scene Videos")]
        private void AutoWirePartsFromSceneVideos()
        {
            const string audioFolder = "Assets/ARME/Ensemble/AudioClipsMain";

            var videoPlayers = FindObjectsByType<VideoPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (videoPlayers.Length == 0)
            {
                Debug.LogWarning($"[{gameObject.name}] Auto-wire: no VideoPlayer found in the scene.");
                return;
            }

            // Base names already represented by an existing config, so re-running the
            // command (or a duplicate VideoPlayer pointing at the same clip) doesn't
            // create a second audio part for the same piece.
            var wiredBaseNames = new HashSet<string>();
            foreach (var c in controllerConfigs)
                if (c.videoPlayer != null && c.videoPlayer.clip != null)
                    wiredBaseNames.Add(StripTopBottomSuffix(c.videoPlayer.clip.name));

            int wired = 0;
            foreach (var vp in videoPlayers)
            {
                if (vp.clip == null)
                {
                    Debug.LogWarning($"[{gameObject.name}] Auto-wire: '{vp.name}' has no Video Clip; skipped.");
                    continue;
                }

                // Skip videos that are already wired to a config.
                bool already = false;
                foreach (var c in controllerConfigs)
                    if (c.videoPlayer == vp) { already = true; break; }
                if (already)
                    continue;

                string baseName = StripTopBottomSuffix(vp.clip.name);

                // Skip a different VideoPlayer that points at the same piece.
                if (wiredBaseNames.Contains(baseName))
                {
                    Debug.LogWarning($"[{gameObject.name}] Auto-wire: '{baseName}' already wired; skipped duplicate '{vp.name}'.");
                    continue;
                }

                AudioClip clip = FindAssetByName<AudioClip>(baseName, audioFolder);
                TextAsset onsets = FindAssetByName<TextAsset>(baseName + "-CRNNManual", audioFolder);

                if (clip == null || onsets == null)
                {
                    Debug.LogWarning($"[{gameObject.name}] Auto-wire: missing " +
                        $"{(clip == null ? "audio clip" : "")}{(clip == null && onsets == null ? " and " : "")}{(onsets == null ? "onset file" : "")} " +
                        $"for '{baseName}' in {audioFolder}; skipped '{vp.name}'.");
                    continue;
                }

                // Audio part GameObject. The AudioSource is what makes Unity call the
                // playback controller's OnAudioFilterRead; the clip is fed via the filter.
                var go = new GameObject($"Audio_{baseName}");
                UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Auto-Wire Ensemble Part");
                go.transform.SetParent(transform, false);

                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = true;
                src.spatialBlend = 0f;
                src.clip = null;

                var pbController = go.AddComponent<ARMEOnsetBasedPlaybackController>();
                pbController.Configure(clip, onsets);

                controllerConfigs.Add(new DesiredOnsetConfig
                {
                    controller = pbController,
                    videoPlayer = vp,
                    autoGenerate = true,
                    constantInterval = defaultDesiredInterval
                });

                wiredBaseNames.Add(baseName);
                wired++;
            }

            RefreshAllDesiredOnsetTimes();
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);

            Debug.Log($"[{gameObject.name}] Auto-wire complete: {wired} new part(s) linked. Total configs: {controllerConfigs.Count}.");
        }

        private static string StripTopBottomSuffix(string clipName)
            => ARMEEditorAssetUtil.StripTopBottomSuffix(clipName);

        private static T FindAssetByName<T>(string assetName, string folder) where T : UnityEngine.Object
            => ARMEEditorAssetUtil.FindAsset<T>(assetName, folder);

        #endregion
#endif
    }
}
