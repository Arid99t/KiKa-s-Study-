/*
 * ARME Onset-Based Playback Controller
 * 
 * Unity component for onset-driven audio time-stretching - handles audio playback and original onset data
 * 
 * ARCHITECTURE:
 * This controller focuses on audio playback and storing original onset timing data.
 * The ensemble controller handles all timing coordination and scheduling of time ratio adjustments.
 * 
 * SETUP INSTRUCTIONS:
 * ==================
 * 1. Attach this script to a GameObject with an AudioSource component
 * 2. Drag an AudioClip into the "Audio Clip" field in Inspector
 * 3. Drag a TextAsset (.txt file) with onset times into "Onset File" field
 * 4. Add this controller to an ARMEOnsetBasedEnsembleController for coordination
 * 
 * ONSET FILE FORMAT:
 * ================
 * Simple text file with one onset time per line (in seconds):
 * 0.00
 * 0.22
 * 0.43
 * 0.67
 * etc.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using ARMEPlayback;

namespace ARMEPlayback
{
    /// <summary>
    /// Onset-based playback controller for time-stretched audio
    /// 
    /// RESPONSIBILITIES:
    /// - Load and parse original onset times from text files
    /// - Handle audio playback via ARME infrastructure
    /// - Receive and apply time ratio adjustments from ensemble controller
    /// - Expose original onset data for ensemble coordination
    /// 
    /// NOT RESPONSIBLE FOR:
    /// - Tracking playback time (ensemble controller handles this)
    /// - Calculating desired onset times (ensemble controller handles this)
    /// - Scheduling time ratio changes (ensemble controller handles this)
    /// </summary>
    public class ARMEOnsetBasedPlaybackController : MonoBehaviour
    {
        [Header("Audio Setup")]
        [Tooltip("The AudioClip to time-stretch (will be converted to mono)")]
        [SerializeField] private AudioClip audioClip;
        
        [Tooltip("Text file containing original onset times (one per line in seconds)")]
        [SerializeField] private TextAsset onsetFile;
        
        [Header("Original Onset Data")]
        [Tooltip("Parsed original onset times from the text file (read-only)")]
        [SerializeField] private List<float> originalOnsetTimes = new List<float>();
        
        [Tooltip("Padded onset times (adjusted for audio padding, read-only)")]
        [SerializeField] private List<float> paddedOnsetTimes = new List<float>();
        
        [Tooltip("Amount of audio padding applied (in seconds, read-only)")]
        [SerializeField] private float appliedPadding = 0.0f;
        
        [Header("Status (Read-Only)")]
        [Tooltip("Current position in onset sequence (managed by ensemble controller)")]
        [SerializeField] private int currentOnsetIndex = 0;
        
        [Tooltip("Current applied time ratio (managed by ensemble controller)")]
        [SerializeField] private float currentTimeRatio = 1.0f;
        
        [Tooltip("Is the controller currently playing")]
        [SerializeField] private bool isPlaying = false;
        
        [Header("Advanced Settings")]
        [Tooltip("Processing block size - smaller = lower latency, larger = more efficient")]
        [SerializeField] private int blockSize = 512;
        
        [Tooltip("Show debug logging for onset parsing and playback")]
        [SerializeField] private bool enableDebugLogging = true;
        
        [Header("Latency Compensation")]
        [Tooltip("Playback offset in samples (negative = earlier, positive = later). Use to compensate for system latency.")]
        [SerializeField] [Range(-4410, 4410)] private int playbackOffsetSamples = -1024;
        
        [Tooltip("Applied playback offset in seconds (read-only)")]
        [SerializeField] private float appliedPlaybackOffset = 0f;
        
        [Header("Recording (Optional)")]
        [Tooltip("Record processed audio output to WAV file (for debugging/validation)")]
        [SerializeField] private bool enableRecording = false;
        
        [Tooltip("Current recording status (read-only)")]
        [SerializeField] private bool isRecording = false;
        
        [Tooltip("Recorded audio length in seconds (read-only)")]
        [SerializeField] private float recordedDuration = 0f;

        // Private members
        private SimplePlaybackController _playbackController;
        private float[] _audioBuffer;
        private float[] _outputBuffer;
        private bool _isInitialized = false;
        private float _originalSampleRate;
        private float _requestedPadding = 0.0f; // Padding requested by ensemble controller
        
        // Recording members
        private List<float> _recordedSamples = new List<float>();
        private int _recordingSampleRate = 44100;
        
        // Latency compensation members
        private int _offsetSamples = 0;

        #region Unity Lifecycle

        /// <summary>
        /// Initialize the playback controller and load audio data
        /// </summary>
        void Start()
        {
            InitializePlaybackController();
        }

        /// <summary>
        /// Assign the audio clip and onset file at runtime or from editor tooling,
        /// before Start() runs. Parses the onset file immediately so onset data is
        /// available to the ensemble controller. Used by the ensemble auto-wire helper.
        /// </summary>
        public void Configure(AudioClip clip, TextAsset onsets)
        {
            audioClip = clip;
            onsetFile = onsets;
            ParseOnsetFile();
        }

        /// <summary>
        /// Request padding to be applied during initialization
        /// Called by ensemble controller before Unity starts playing
        /// </summary>
        public void InitializeWithPadding(float paddingSeconds)
        {
            _requestedPadding = paddingSeconds;
            if (enableDebugLogging)
                Debug.Log($"[{gameObject.name}] Padding requested: {paddingSeconds:F3}s (will be applied during initialization)");
        }

        /// <summary>
        /// Parse onset file whenever inspector values change
        /// </summary>
        void OnValidate()
        {
            ParseOnsetFile();
            
            // Update latency compensation when changed in inspector
            if (_isInitialized)
            {
                _offsetSamples = playbackOffsetSamples;
                appliedPlaybackOffset = (float)_offsetSamples / _recordingSampleRate;
            }
        }

        /// <summary>
        /// Clean up when destroyed
        /// </summary>
        void OnDestroy()
        {
            if (_playbackController != null)
            {
                _playbackController.Dispose();
                if (enableDebugLogging)
                    Debug.Log($"✓ [{gameObject.name}] Playback Controller cleaned up");
            }
        }

        #endregion

        #region Audio Setup and Initialization

        /// <summary>
        /// Initialize the native playback controller with audio data
        /// </summary>
        private void InitializePlaybackController()
        {
            // Validate inputs
            if (audioClip == null)
            {
                Debug.LogError($"[{gameObject.name}] No AudioClip assigned!");
                return;
            }

            if (originalOnsetTimes.Count == 0)
            {
                Debug.LogWarning($"[{gameObject.name}] No onset times loaded - make sure onset file is assigned and valid");
            }

            try
            {
                // Create playback controller
                double sampleRate = AudioSettings.outputSampleRate;
                _playbackController = new SimplePlaybackController(sampleRate, blockSize);
                
                // Store sample rate for recording
                _recordingSampleRate = (int)sampleRate;
                
                if (enableDebugLogging)
                    Debug.Log($"✓ [{gameObject.name}] Playback Controller created: {sampleRate}Hz, block size {blockSize}");

                // Convert AudioClip to float buffer (mono)
                _audioBuffer = AudioClipToMonoFloatArray(audioClip);
                
                if (enableDebugLogging)
                    Debug.Log($"✓ [{gameObject.name}] Loaded audio: {_audioBuffer.Length} samples from '{audioClip.name}'");

                // Cache the sample rate to avoid main thread issues in OnAudioFilterRead
                _originalSampleRate = audioClip.frequency;

                // Apply padding if requested by ensemble controller
                if (_requestedPadding > 0.0f)
                {
                    // Apply padding to audio buffer
                    int paddingSamples = Mathf.RoundToInt((float)(_requestedPadding * _originalSampleRate));
                    float[] paddedBuffer = new float[_audioBuffer.Length + paddingSamples];
                    
                    // Fill with silence (zeros) then original audio
                    System.Array.Clear(paddedBuffer, 0, paddingSamples); // Silence padding
                    System.Array.Copy(_audioBuffer, 0, paddedBuffer, paddingSamples, _audioBuffer.Length); // Original audio
                    
                    _audioBuffer = paddedBuffer;
                    appliedPadding = _requestedPadding;
                    
                    if (enableDebugLogging)
                        Debug.Log($"✓ [{gameObject.name}] Applied {_requestedPadding:F3}s ({paddingSamples} samples) of audio padding");
                }
                else
                {
                    appliedPadding = 0.0f;
                }

                // Apply latency compensation by modifying the audio buffer
                _offsetSamples = playbackOffsetSamples;
                appliedPlaybackOffset = (float)_offsetSamples / _recordingSampleRate;
                
                if (_offsetSamples != 0)
                {
                    _audioBuffer = ApplyLatencyCompensationToBuffer(_audioBuffer);
                    
                    if (enableDebugLogging)
                        Debug.Log($"✓ [{gameObject.name}] Latency compensation: {_offsetSamples} samples ({appliedPlaybackOffset * 1000f:F1}ms) applied to audio buffer");
                }


                // Set up the audio buffer in the controller
                _playbackController.SetAudioBuffer(_audioBuffer, _originalSampleRate);
                
                // Initialize padded onset times (no padding initially)
                UpdatePaddedOnsetTimes();
                
                // Set initial time ratio
                _playbackController.TimeRatio = currentTimeRatio;

                // Create output buffer
                _outputBuffer = new float[blockSize];
                

                _isInitialized = true;
                
                if (enableDebugLogging)
                    Debug.Log($"🎵 [{gameObject.name}] Playback Controller ready! Onsets: {originalOnsetTimes.Count}");
            }
            catch (DllNotFoundException ex)
            {
                // The native time-stretch library (or one of its dependencies such as
                // rubberband-3.dll / samplerate.dll / sleef.dll) could not be loaded. Fail
                // softly: this part stays silent instead of throwing, so the rest of the
                // scene keeps working.
                Debug.LogWarning($"[{gameObject.name}] Native audio library 'ARMEPlaybackControllerLib' could not be loaded ({ex.Message}). " +
                    "Its native dependencies (rubberband-3.dll, samplerate.dll, sleef.dll, sleefdft.dll) are probably missing from Assets/Plugins/. " +
                    "This part will be SILENT. For DLL-free synchronized playback, use the ARMEEnsembleSyncPlayer component instead.");
            }
            catch (ARMEPlaybackException ex)
            {
                Debug.LogError($"[{gameObject.name}] Failed to initialize playback controller: {ex.Message}");
                Debug.LogError("Make sure ARMEPlaybackControllerLib.dll is in Assets/Plugins/");
            }
        }

        /// <summary>
        /// Convert Unity AudioClip to mono float array
        /// </summary>
        private float[] AudioClipToMonoFloatArray(AudioClip clip)
        {
            int totalSamples = clip.samples * clip.channels;
            float[] clipData = new float[totalSamples];
            clip.GetData(clipData, 0);

            if (clip.channels == 1)
            {
                // Already mono
                return clipData;
            }
            else
            {
                // Convert stereo (or multi-channel) to mono by averaging
                float[] monoData = new float[clip.samples];
                for (int i = 0; i < clip.samples; i++)
                {
                    float sum = 0f;
                    for (int ch = 0; ch < clip.channels; ch++)
                    {
                        sum += clipData[i * clip.channels + ch];
                    }
                    monoData[i] = sum / clip.channels;
                }
                
                if (enableDebugLogging)
                    Debug.Log($"[{gameObject.name}] Converted {clip.channels}-channel audio to mono");
                    
                return monoData;
            }
        }

        #endregion

        #region Onset File Parsing

        /// <summary>
        /// Parse onset times from the text file
        /// Called automatically in OnValidate() when inspector values change
        /// </summary>
        private void ParseOnsetFile()
        {
            originalOnsetTimes.Clear();
            
            if (onsetFile == null)
            {
                if (enableDebugLogging)
                    Debug.Log($"[{gameObject.name}] No onset file assigned");
                return;
            }

            try
            {
                string[] lines = onsetFile.text.Split('\n');
                int validCount = 0;
                
                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    
                    // Skip empty lines and comments
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
                        continue;
                    
                    if (float.TryParse(trimmedLine, out float onsetTime))
                    {
                        if (onsetTime >= 0.0f)
                        {
                            originalOnsetTimes.Add(onsetTime);
                            validCount++;
                        }
                        else
                        {
                            Debug.LogWarning($"[{gameObject.name}] Negative onset time ignored: {onsetTime}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[{gameObject.name}] Invalid onset time format: '{trimmedLine}'");
                    }
                }
                
                // Sort onset times to ensure they're in chronological order
                originalOnsetTimes.Sort();
                
                if (enableDebugLogging && validCount > 0)
                {
                    Debug.Log($"✓ [{gameObject.name}] Parsed {validCount} onset times from '{onsetFile.name}'");
                    Debug.Log($"  Range: {originalOnsetTimes[0]:F3}s to {originalOnsetTimes[originalOnsetTimes.Count-1]:F3}s");
                    
                    if (validCount > 1)
                    {
                        float avgInterval = (originalOnsetTimes[originalOnsetTimes.Count-1] - originalOnsetTimes[0]) / (validCount - 1);
                        Debug.Log($"  Average interval: {avgInterval:F3}s");
                    }
                }
                
                if (validCount == 0)
                {
                    Debug.LogWarning($"[{gameObject.name}] No valid onset times found in file '{onsetFile.name}'");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{gameObject.name}] Error parsing onset file '{onsetFile.name}': {ex.Message}");
            }
        }

        #endregion

        #region Audio Processing

        /// <summary>
        /// Unity's audio filter callback - handles real-time audio output
        /// </summary>
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_isInitialized || _playbackController == null || !isPlaying)
            {
                // Fill with silence if not ready or not playing
                for (int i = 0; i < data.Length; i++)
                    data[i] = 0f;
                return;
            }

            try
            {
                int samplesNeeded = data.Length / channels;
                
                // Get stretched samples from controller
                int samplesGot = _playbackController.GetSamples(_outputBuffer);
                
               
                // Copy to output buffer (duplicate mono to stereo if needed)
                for (int i = 0; i < samplesNeeded && i < samplesGot; i++)
                {
                    float sample = _outputBuffer[i];
                    for (int ch = 0; ch < channels; ch++)
                    {
                        data[i * channels + ch] = sample;
                    }
                }
                
                // Record processed audio if recording is enabled
                if (enableRecording && isRecording)
                {
                    for (int i = 0; i < samplesGot; i++)
                    {
                        _recordedSamples.Add(_outputBuffer[i]);
                    }
                    
                    // Update recorded duration
                    recordedDuration = (float)_recordedSamples.Count / _recordingSampleRate;
                }

                // Fill remaining with silence if we didn't get enough samples
                for (int i = samplesGot * channels; i < data.Length; i++)
                {
                    data[i] = 0f;
                }
            }
            catch (ARMEPlaybackException ex)
            {
                if (enableDebugLogging)
                    Debug.LogWarning($"[{gameObject.name}] Audio processing error: {ex.Message}");
                    
                // Fill with silence on error
                for (int i = 0; i < data.Length; i++)
                    data[i] = 0f;
            }
        }

        #endregion

        #region Public API for Ensemble Controller

        /// <summary>
        /// Update padded onset times based on applied padding
        /// </summary>
        private void UpdatePaddedOnsetTimes()
        {
            paddedOnsetTimes.Clear();
            
            foreach (float onsetTime in originalOnsetTimes)
            {
                paddedOnsetTimes.Add(onsetTime + appliedPadding);
            }
            
            if (enableDebugLogging && paddedOnsetTimes.Count > 0)
            {
                Debug.Log($"[{gameObject.name}] Updated padded onsets: first at {paddedOnsetTimes[0]:F3}s (was {originalOnsetTimes[0]:F3}s)");
            }
        }
        
        /// <summary>
        /// Recalculate padded onset times with a specific padding amount (for preview/validation)
        /// This doesn't change appliedPadding or reload audio - just updates the padded onset times
        /// </summary>
        public void RecalculatePaddedOnsets(float paddingAmount)
        {
            paddedOnsetTimes.Clear();
            
            foreach (float onsetTime in originalOnsetTimes)
            {
                paddedOnsetTimes.Add(onsetTime + paddingAmount);
            }
            
            if (enableDebugLogging && paddedOnsetTimes.Count > 0 && originalOnsetTimes.Count > 0)
            {
                Debug.Log($"[{gameObject.name}] Recalculated padded onsets with {paddingAmount:F3}s padding: first at {paddedOnsetTimes[0]:F3}s (was {originalOnsetTimes[0]:F3}s)");
            }
        }
        
        /// <summary>
        /// Set audio buffer with optional padding
        /// </summary>
        /// <param name="audioData">Raw audio samples</param>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="paddingSeconds">Seconds of silence to add before audio</param>
        public List<float> GetOriginalOnsetTimes()
        {
            return new List<float>(originalOnsetTimes); // Return a copy to prevent external modification
        }
        
        /// <summary>
        /// Get the list of padded onset times (original times + padding)
        /// Used by ensemble controller for timing coordination when padding is applied
        /// </summary>
        public List<float> GetPaddedOnsetTimes()
        {
            return new List<float>(paddedOnsetTimes); // Return a copy to prevent external modification
        }

        /// <summary>
        /// Apply time ratio adjustment based on onset timing
        /// Called by ensemble controller when onset timing needs adjustment
        /// </summary>
        public void ApplyOnsetTimeRatio(float originalOnsetTime, float desiredOnsetTime)
        {
            if (!_isInitialized || _playbackController == null)
            {
                Debug.LogWarning($"[{gameObject.name}] Cannot apply onset time ratio - controller not initialized");
                return;
            }

            try
            {
                _playbackController.SetRatioFromOnsetTime(originalOnsetTime, desiredOnsetTime);
                
                // Update our cached time ratio for display
                currentTimeRatio = _playbackController.TimeRatio;
                
                if (enableDebugLogging)
                {
                    Debug.Log($"[{gameObject.name}] Applied onset ratio: {originalOnsetTime:F3}s → {desiredOnsetTime:F3}s (ratio: {currentTimeRatio:F3})");
                }
            }
            catch (ARMEPlaybackException ex)
            {
                Debug.LogWarning($"[{gameObject.name}] Failed to apply onset time ratio: {ex.Message}");
            }
        }

        /// <summary>
        /// Update the current onset index
        /// Used by ensemble controller to track progress through onset sequence
        /// </summary>
        public void SetOnsetIndex(int index)
        {
            currentOnsetIndex = Mathf.Clamp(index, 0, originalOnsetTimes.Count);
        }

        /// <summary>
        /// Get the current onset index
        /// </summary>
        public int GetOnsetIndex()
        {
            return currentOnsetIndex;
        }

        /// <summary>
        /// Get the total number of onsets
        /// </summary>
        public int GetOnsetCount()
        {
            return originalOnsetTimes.Count;
        }

        /// <summary>
        /// Start playback - called by ensemble controller
        /// </summary>
        public void StartPlayback()
        {
            if (_isInitialized && _playbackController != null)
            {
                try
                {
                    _playbackController.Start();
                    isPlaying = true;
                    
                    // Start recording if enabled
                    if (enableRecording)
                    {
                        StartRecording();
                    }
                    
                    if (enableDebugLogging)
                        Debug.Log($"▶️ [{gameObject.name}] Playback started{(enableRecording ? " (recording)" : "")}");
                }
                catch (ARMEPlaybackException ex)
                {
                    Debug.LogWarning($"[{gameObject.name}] Failed to start playback: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Stop playback - called by ensemble controller
        /// </summary>
        public void StopPlayback()
        {
            if (_isInitialized && _playbackController != null)
            {
                try
                {
                    _playbackController.Stop();
                    isPlaying = false;
                    
                    // Stop recording and save file if was recording
                    if (isRecording)
                    {
                        StopRecordingAndSave();
                    }
                    
                    if (enableDebugLogging)
                        Debug.Log($"⏸️ [{gameObject.name}] Playback stopped");
                }
                catch (ARMEPlaybackException ex)
                {
                    Debug.LogWarning($"[{gameObject.name}] Failed to stop playback: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Reset playback to beginning - called by ensemble controller
        /// </summary>
        public void ResetPlayback()
        {
            if (_isInitialized && _playbackController != null)
            {
                try
                {
                    bool wasPlaying = isPlaying;
                    
                    // Stop recording if active
                    if (isRecording)
                    {
                        StopRecordingAndSave();
                    }
                    
                    _playbackController.Stop();
                    _playbackController.SetAudioBuffer(_audioBuffer, _originalSampleRate);
                    
                    // Reset time ratio to 1.0 for fresh start
                    _playbackController.TimeRatio = 1.0f;
                    currentTimeRatio = 1.0f;
                    
                    // Reset onset tracking
                    currentOnsetIndex = 0;
                    
                    isPlaying = false;
                    
                    if (enableDebugLogging)
                        Debug.Log($"🔄 [{gameObject.name}] Playback reset to beginning");
                }
                catch (ARMEPlaybackException ex)
                {
                    Debug.LogWarning($"[{gameObject.name}] Failed to reset playback: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Check if playback is currently active
        /// </summary>
        public bool IsPlaying => isPlaying;

        /// <summary>
        /// Get the amount of padding applied to the audio buffer
        /// </summary>
        public float GetAppliedPadding()
        {
            return appliedPadding;
        }
        
        /// <summary>
        /// Check if audio padding has been applied or requested
        /// </summary>
        public bool HasPadding => appliedPadding > 0.0f || _requestedPadding > 0.0f;
        public float GetCurrentTimeRatio()
        {
            return currentTimeRatio;
        }

        /// <summary>
        /// Get a formatted status string for debugging/display
        /// </summary>
        public string GetStatusString()
        {
            if (!_isInitialized)
                return "Not Initialized";
                
            string status = $"Playing: {isPlaying}, Onset: {currentOnsetIndex}/{originalOnsetTimes.Count}, Ratio: {currentTimeRatio:F2}x";
            
            if (HasPadding)
            {
                status += $", Padding: {appliedPadding:F3}s";
            }
            
            if (appliedPlaybackOffset != 0f)
            {
                status += $", Offset: {playbackOffsetSamples} samples";
            }
            
            return status;
        }

        #endregion

        #region Validation and Debug

        /// <summary>
        /// Validate that the controller is properly set up
        /// </summary>
        public bool ValidateSetup(out string errorMessage)
        {
            errorMessage = "";
            
            if (audioClip == null)
            {
                errorMessage = "No AudioClip assigned";
                return false;
            }
            
            if (onsetFile == null)
            {
                errorMessage = "No onset file assigned";
                return false;
            }
            
            if (originalOnsetTimes.Count == 0)
            {
                errorMessage = "No valid onset times parsed from file";
                return false;
            }
            
            if (!_isInitialized)
            {
                errorMessage = "Playback controller not initialized";
                return false;
            }
            
            return true;
        }

        /// <summary>
        /// Force re-parsing of onset file (useful for debugging)
        /// </summary>
        [ContextMenu("Reparse Onset File")]
        public void ForceReparseOnsetFile()
        {
            ParseOnsetFile();
        }

        /// <summary>
        /// Log current status (useful for debugging)
        /// </summary>
        [ContextMenu("Log Status")]
        public void LogCurrentStatus()
        {
            Debug.Log($"[{gameObject.name}] Status: {GetStatusString()}");
            
            if (originalOnsetTimes.Count > 0)
            {
                Debug.Log($"[{gameObject.name}] Onset times: [{string.Join(", ", originalOnsetTimes)}]");
            }
        }

        #endregion

        #region Latency Compensation

        /// <summary>
        /// Apply latency compensation by modifying the audio buffer
        /// </summary>
        private float[] ApplyLatencyCompensationToBuffer(float[] originalBuffer)
        {
            if (_offsetSamples == 0)
                return originalBuffer;

            if (_offsetSamples < 0)
            {
                // Negative offset: trim samples from the beginning (play earlier)
                int trimSamples = -_offsetSamples;
                if (trimSamples >= originalBuffer.Length)
                {
                    // If trying to trim more than the buffer length, return silence
                    Debug.LogWarning($"[{gameObject.name}] Latency compensation trim ({trimSamples}) exceeds buffer length ({originalBuffer.Length}), using silence");
                    return new float[512]; // Return minimal buffer
                }
                
                float[] trimmedBuffer = new float[originalBuffer.Length - trimSamples];
                System.Array.Copy(originalBuffer, trimSamples, trimmedBuffer, 0, trimmedBuffer.Length);
                
                if (enableDebugLogging)
                    Debug.Log($"[{gameObject.name}] Trimmed {trimSamples} samples from beginning for latency compensation");
                    
                return trimmedBuffer;
            }
            else
            {
                // Positive offset: pad silence at the beginning (play later)
                int padSamples = _offsetSamples;
                float[] paddedBuffer = new float[originalBuffer.Length + padSamples];
                
                // Fill beginning with silence, then copy original audio
                System.Array.Clear(paddedBuffer, 0, padSamples);
                System.Array.Copy(originalBuffer, 0, paddedBuffer, padSamples, originalBuffer.Length);
                
                if (enableDebugLogging)
                    Debug.Log($"[{gameObject.name}] Added {padSamples} samples of silence at beginning for latency compensation");
                    
                return paddedBuffer;
            }
        }

        #endregion

        #region Recording Functionality

        /// <summary>
        /// Start recording processed audio output
        /// </summary>
        private void StartRecording()
        {
            if (!enableRecording)
                return;
                
            _recordedSamples.Clear();
            isRecording = true;
            recordedDuration = 0f;
            
            if (enableDebugLogging)
                Debug.Log($"🔴 [{gameObject.name}] Recording started");
        }
        
        /// <summary>
        /// Stop recording and save to WAV file
        /// </summary>
        private void StopRecordingAndSave()
        {
            if (!isRecording)
                return;
                
            isRecording = false;
            
            if (_recordedSamples.Count == 0)
            {
                if (enableDebugLogging)
                    Debug.Log($"[{gameObject.name}] Recording stopped - no audio recorded");
                return;
            }
            
            try
            {
                string outputPath = GetRecordingOutputPath();
                WriteWAVFile(outputPath, _recordedSamples.ToArray(), _recordingSampleRate);
                
                if (enableDebugLogging)
                    Debug.Log($"💾 [{gameObject.name}] Recorded audio saved: {outputPath} ({recordedDuration:F2}s, {_recordedSamples.Count} samples)");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[{gameObject.name}] Failed to save recorded audio: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get the output path for recorded WAV file
        /// </summary>
        private string GetRecordingOutputPath()
        {
            if (audioClip == null)
            {
                return System.IO.Path.Combine(Application.streamingAssetsPath, $"{gameObject.name}_recorded.wav");
            }
            
            // Get the original audio file path (this is tricky in Unity, so we'll use a fallback)
            string originalName = audioClip.name;
            string fileName = $"{originalName}_recorded.wav";
            
            // Try to save in the same directory as the onset file if available
            if (onsetFile != null)
            {
#if UNITY_EDITOR
                string onsetFilePath = UnityEditor.AssetDatabase.GetAssetPath(onsetFile);
                if (!string.IsNullOrEmpty(onsetFilePath))
                {
                    string directory = System.IO.Path.GetDirectoryName(onsetFilePath);
                    return System.IO.Path.Combine(directory, fileName);
                }
#endif
            }
            
            // Fallback to StreamingAssets
            string streamingAssetsPath = Application.streamingAssetsPath;
            if (!System.IO.Directory.Exists(streamingAssetsPath))
            {
                System.IO.Directory.CreateDirectory(streamingAssetsPath);
            }
            
            return System.IO.Path.Combine(streamingAssetsPath, fileName);
        }
        
        /// <summary>
        /// Write audio samples to WAV file
        /// </summary>
        private void WriteWAVFile(string path, float[] samples, int sampleRate)
        {
            using (var fileStream = new System.IO.FileStream(path, System.IO.FileMode.Create))
            using (var writer = new System.IO.BinaryWriter(fileStream))
            {
                // WAV file header
                int channels = 1; // Mono
                int bitsPerSample = 16;
                int bytesPerSample = bitsPerSample / 8;
                int byteRate = sampleRate * channels * bytesPerSample;
                int blockAlign = channels * bytesPerSample;
                int dataSize = samples.Length * bytesPerSample;
                int fileSize = 36 + dataSize;
                
                // RIFF header
                writer.Write("RIFF".ToCharArray());
                writer.Write(fileSize);
                writer.Write("WAVE".ToCharArray());
                
                // fmt chunk
                writer.Write("fmt ".ToCharArray());
                writer.Write(16); // fmt chunk size
                writer.Write((short)1); // PCM format
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write((short)blockAlign);
                writer.Write((short)bitsPerSample);
                
                // data chunk
                writer.Write("data".ToCharArray());
                writer.Write(dataSize);
                
                // Convert float samples to 16-bit PCM
                foreach (float sample in samples)
                {
                    short pcmSample = (short)(Mathf.Clamp(sample, -1f, 1f) * 32767f);
                    writer.Write(pcmSample);
                }
            }
        }
        
        /// <summary>
        /// Force stop recording and save (useful for debugging)
        /// </summary>
        [ContextMenu("Save Recording")]
        public void ForceSaveRecording()
        {
            if (isRecording)
            {
                StopRecordingAndSave();
            }
            else
            {
                Debug.Log($"[{gameObject.name}] No active recording to save");
            }
        }

        #endregion
    }
}