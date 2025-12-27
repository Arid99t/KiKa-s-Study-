/*
 * C# Example for ARME Playback Controller - Beginner Friendly
 * 
 * This example shows how to use the ARME Playback Controller library in C# and Unity
 * for real-time time-stretching of audio without changing pitch.
 * 
 * UNITY SETUP INSTRUCTIONS:
 * =========================
 * 1. Build the ARME Playback Controller project to create ARMEPlaybackControllerLib.dll
 * 2. In your Unity project, create folder: Assets/Plugins/x86_64/ (for 64-bit)
 * 3. Copy ALL these DLL files to Assets/Plugins/x86_64/ or add containing folder to windows PATH:
 *    - ARMEPlaybackControllerLib.dll (main library)
 *    - rubberband-3.dll (required dependency)
 *    - samplerate.dll (required dependency)
 *    - sleef.dll (required dependency)
 *    - sleefdft.dll (required dependency)
 * 4. Copy this C# script into your Assets/Scripts/ folder
 * 5. Attach this script to a GameObject in your scene
 * 6. Drag an AudioClip into the "Audio Clip" field in the Inspector
 * 7. Hit Play and use the controls to stretch the audio!
 * 
 * TROUBLESHOOTING DLL NOT FOUND:
 * ==============================
 * If you get "DllNotFoundException", try these steps in order:
 * 
 * A) CHECK DLL ARCHITECTURE:
 *    - For 64-bit Unity: Put DLLs in Assets/Plugins/x86_64/
 *    - For 32-bit Unity: Put DLLs in Assets/Plugins/x86/
 *    - Check Unity architecture: Edit > Project Settings > Player > Configuration
 * 
 * B) VERIFY ALL DEPENDENCIES:
 *    - You need ALL 5 DLL files listed above
 *    - Missing any one will cause the main DLL to fail loading
 *    - Copy from: Libraries/ARMEPlaybackController/build-vs2022-c-api/Debug/bin/
 * 
 * C) CHECK UNITY PLATFORM SETTINGS:
 *    - Select each DLL in Unity Inspector
 *    - Set "Platform Settings" to match your target (Standalone, Editor, etc.)
 *    - Set "Architecture" to x86_64 for 64-bit builds
 * 
 * D) ALTERNATIVE: Try copying to main Plugins folder:
 *    - Some Unity versions work with Assets/Plugins/ (no subfolder)
 *    - Try this if the x86_64 subfolder doesn't work
 * 
 * E) ENABLE UNSAFE CODE (if needed):
 *    - Edit > Project Settings > Player > Other Settings
 *    - Check "Allow 'unsafe' Code" if you get compilation errors
 * 
 * IMPORTANT NOTES FOR BEGINNERS:
 * - This uses Unity's OnAudioFilterRead for real-time audio output
 * - AudioClip data is converted to mono float buffer for processing
 * - Time stretching preserves pitch while changing playback speed
 * - Always clean up native resources to prevent memory leaks
 */

using System;
using UnityEngine;
using UnityEngine.UI;

/*
 * UNITY COMPONENT EXAMPLE - Real-time Audio Time-Stretching!
 * 
 * UNITY SETUP CHECKLIST:
 * ✓ ARMEPlaybackControllerLib.dll copied to Assets/Plugins/
 * ✓ This C# script copied to Assets/Scripts/
 * ✓ Script attached to a GameObject with an AudioSource component
 * ✓ Drag an AudioClip into the "Audio Clip" field
 * ✓ Hit Play and adjust the Time Ratio slider in real-time!
 */
namespace ARMEPlayback
{
    /// <summary>
    /// Unity component for real-time audio time-stretching - Super easy to use!
    /// 
    /// BEGINNER GUIDE:
    /// 1. Attach this to a GameObject with an AudioSource component
    /// 2. Drag an AudioClip into the "Audio Clip" field in Inspector
    /// 3. Optionally connect UI Slider for real-time time ratio control
    /// 4. Hit Play to hear time-stretched audio!
    /// 
    /// FEATURES:
    /// - Real-time time-stretching without pitch change
    /// - Unity AudioClip integration 
    /// - UI controls for interactive time ratio adjustment
    /// - Automatic stereo to mono conversion
    /// - Loop playback support
    /// </summary>
    public class UnityPlaybackControllerExample : MonoBehaviour
    {
        [Header("Audio Setup")]
        [Tooltip("The AudioClip to time-stretch (will be converted to mono)")]
        [SerializeField] private AudioClip audioClip;
        
        [Header("Time-Stretching Controls")]
        [Tooltip("Time stretching ratio: 1.0 = normal, 0.5 = double speed, 2.0 = half speed")]
        [SerializeField] [Range(0.1f, 4.0f)] private float timeRatio = 1.0f;
        
        [Tooltip("Enable looping playback")]
        [SerializeField] private bool loopPlayback = true;
        
        [Header("UI Controls (Optional)")]
        [Tooltip("Optional slider to control time ratio in real-time")]
        [SerializeField] private Slider timeRatioSlider;
        
        [Tooltip("Optional text to display current time ratio")]
        [SerializeField] private Text timeRatioText;
        
        [Header("Advanced Settings")]
        [Tooltip("Processing block size - smaller = lower latency, larger = more efficient")]
        [SerializeField] private int blockSize = 512;

        // Private members
        private SimplePlaybackController _playbackController;
        private float[] _audioBuffer;
        private float[] _outputBuffer;
        private bool _isInitialized = false;
        private float _lastTimeRatio;
        private float _originalSampleRate; // Cache the sample rate to avoid main thread issues

        /// <summary>
        /// Initialize the playback controller and load audio data
        /// </summary>
        void Start()
        {
            // Validate audio clip
            if (audioClip == null)
            {
                Debug.LogError("No AudioClip assigned!");
                return;
            }

            // Initialize UI controls
            if (timeRatioSlider != null)
            {
                timeRatioSlider.value = timeRatio;
                timeRatioSlider.onValueChanged.AddListener(OnTimeRatioSliderChanged);
            }

            // Load and initialize
            InitializePlaybackController();
        }

        /// <summary>
        /// Initialize the native playback controller with audio data
        /// </summary>
        private void InitializePlaybackController()
        {
            // First, test DLL loading
            Debug.Log("🔍 Testing DLL loading...");
            if (!SimplePlaybackController.TestDLLLoading(out string dllTestMessage))
            {
                Debug.LogError($"❌ DLL Test Failed: {dllTestMessage}");
                return;
            }
            Debug.Log($"✓ DLL Test Passed: {dllTestMessage}");

            try
            {
                // Create playback controller
                double sampleRate = AudioSettings.outputSampleRate;
                _playbackController = new SimplePlaybackController(sampleRate, blockSize);
                
                Debug.Log($"✓ Playback Controller created: {sampleRate}Hz, block size {blockSize}");

                // Convert AudioClip to float buffer (mono)
                _audioBuffer = AudioClipToMonoFloatArray(audioClip);
                Debug.Log($"✓ Loaded audio: {_audioBuffer.Length} samples from '{audioClip.name}'");

                // Cache the sample rate to avoid main thread issues in OnAudioFilterRead
                _originalSampleRate = audioClip.frequency;

                // Set up the audio buffer in the controller
                _playbackController.SetAudioBuffer(_audioBuffer, _originalSampleRate);
                
                // Set initial time ratio
                _playbackController.TimeRatio = timeRatio;
                _lastTimeRatio = timeRatio;

                // Start the stretcher
                _playbackController.Start();

                // Create output buffer
                _outputBuffer = new float[blockSize];

                _isInitialized = true;
                Debug.Log("🎵 Playback Controller ready for real-time time-stretching!");

                // Update UI
                UpdateTimeRatioDisplay();
            }
            catch (ARMEPlaybackException ex)
            {
                Debug.LogError($"Failed to initialize playback controller: {ex.Message}");
                Debug.LogError("Make sure ARMEPlaybackControllerLib.dll is in Assets/Plugins/");
            }
        }

        /// <summary>
        /// Convert Unity AudioClip to mono float array
        /// BEGINNER NOTE: This handles stereo to mono conversion automatically
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
                Debug.Log($"Converted {clip.channels}-channel audio to mono");
                return monoData;
            }
        }

        /// <summary>
        /// Unity's audio filter callback - this runs in real-time for audio output
        /// BEGINNER NOTE: This is where the magic happens - time-stretched audio comes out here!
        /// </summary>
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_isInitialized || _playbackController == null)
            {
                // Fill with silence if not ready
                for (int i = 0; i < data.Length; i++)
                    data[i] = 0f;
                return;
            }

            // Update time ratio if it changed
            if (Mathf.Abs(timeRatio - _lastTimeRatio) > 0.001f)
            {
                try
                {
                    _playbackController.TimeRatio = timeRatio;
                    _lastTimeRatio = timeRatio;
                    // Note: UI updates are handled in Update() method to avoid threading issues
                }
                catch (ARMEPlaybackException ex)
                {
                    Debug.LogWarning($"Failed to update time ratio: {ex.Message}");
                }
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

                // Fill remaining with silence if we didn't get enough samples
                for (int i = samplesGot * channels; i < data.Length; i++)
                {
                    data[i] = 0f;
                }
            }
            catch (ARMEPlaybackException ex)
            {
                Debug.LogWarning($"Audio processing error: {ex.Message}");
                // Fill with silence on error
                for (int i = 0; i < data.Length; i++)
                    data[i] = 0f;
            }
        }

        /// <summary>
        /// Handle time ratio slider changes
        /// </summary>
        private void OnTimeRatioSliderChanged(float value)
        {
            timeRatio = value;
        }

        /// <summary>
        /// Update the time ratio display text
        /// </summary>
        private void UpdateTimeRatioDisplay()
        {
            if (timeRatioText != null)
            {
                timeRatioText.text = $"Time Ratio: {timeRatio:F2}x";
            }
        }

        /// <summary>
        /// Public methods for external control
        /// </summary>
        
        public void SetTimeRatio(float ratio)
        {
            timeRatio = Mathf.Clamp(ratio, 0.1f, 4.0f);
            if (timeRatioSlider != null)
                timeRatioSlider.value = timeRatio;
        }

        public void ResetTimeRatio()
        {
            SetTimeRatio(1.0f);
        }

        public void RestartPlayback()
        {
            if (_isInitialized && _playbackController != null)
            {
                try
                {
                    _playbackController.Stop();
                    _playbackController.SetAudioBuffer(_audioBuffer, _originalSampleRate);
                    _playbackController.Start();
                    Debug.Log("🔄 Playback restarted");
                }
                catch (ARMEPlaybackException ex)
                {
                    Debug.LogWarning($"Failed to restart playback: {ex.Message}");
                }
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
                Debug.Log("✓ Playback Controller cleaned up");
            }
        }

        /// <summary>
        /// Display debug info in Inspector
        /// </summary>
        void Update()
        {
            // Update time ratio display in real-time
            if (_isInitialized && timeRatioText != null && Mathf.Abs(timeRatio - _lastTimeRatio) > 0.001f)
            {
                UpdateTimeRatioDisplay();
            }
        }

        /// <summary>
        /// Show debug information in the Inspector
        /// </summary>
        void OnValidate()
        {
            timeRatio = Mathf.Clamp(timeRatio, 0.5f, 2.0f);
        }
    }

    /// <summary>
    /// Simple UI controller for the playback example - attach to a Canvas
    /// </summary>
    public class PlaybackControllerUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private UnityPlaybackControllerExample playbackController;
        [SerializeField] private Slider timeRatioSlider;
        [SerializeField] private Text timeRatioText;
        [SerializeField] private Button resetButton;
        [SerializeField] private Button restartButton;
        [SerializeField] private Toggle loopToggle;

        void Start()
        {
            // Setup UI event handlers
            if (resetButton != null)
                resetButton.onClick.AddListener(() => playbackController?.ResetTimeRatio());
            
            if (restartButton != null)
                restartButton.onClick.AddListener(() => playbackController?.RestartPlayback());
            
            if (timeRatioSlider != null)
                timeRatioSlider.onValueChanged.AddListener(ratio => playbackController?.SetTimeRatio(ratio));
        }
    }
}


