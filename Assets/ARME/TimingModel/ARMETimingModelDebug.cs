using System;
using UnityEngine;
using UnityEngine.UI;
using NaughtyAttributes;


namespace ARMETiming
{
    /// <summary>
    /// Unity component for debugging the ARMETimingModel library in the Unity Editor.
    /// Provides buttons to initialize the model, register onsets, and predict future onsets.
    /// Uses NaughtyAttributes for easy button creation in the Inspector (Free asset from the Unity Asset Store).
    /// References the ARMETimingModelLib.dll native library.
    /// Does not require Play mode to function.
    /// </summary>
    public class ARMETimingModelUnityDebug : MonoBehaviour
    {
        public int _numberOfOnsetsRegistered = 0;

        [Button(enabledMode: EButtonEnableMode.Always)]
        private void InitTimingModel()
        {
            if (_timingModel != null)
            {
                _timingModel.Dispose();
                Debug.Log("✓ Timing Model cleaned up");
            }
            InitializeTimingModel();
            _numberOfOnsetsRegistered = 1;
        }

        [Button(enabledMode: EButtonEnableMode.Always)]
        private void RegisterFirstOnsets()
        {
            RegisterInitialOnsets();
            _numberOfOnsetsRegistered++;
        }

        [Button(enabledMode: EButtonEnableMode.Always)]
        private void RegisterAdditionalOnset()
        {
            float beatInterval = 60f / baseTempo; // Convert BPM to seconds per beat
            RegisterNewOnsetsWithVariation(beatInterval * _numberOfOnsetsRegistered);
            _numberOfOnsetsRegistered++;
        }

        [Button(enabledMode: EButtonEnableMode.Always)]
        private void CheckNumberOfOnsets()
        {
            if (_timingModel == null) return;

            for (int i = 0; i < numberOfPlayers; i++)
            {
                int onsetCount = _timingModel.GetNumberOfOnsets(i);
                float latestOnset = _timingModel.GetLatestOnset(i);
                Debug.Log($"  Player {i}: {onsetCount} onsets, latest at {latestOnset:F3}s");
            }
        }
        
        [Button(enabledMode: EButtonEnableMode.Always)]
        private void PredictNextOnsetForPlayerOne()
        {
            if (_timingModel == null) return;

            try
            {
                float prediction = _timingModel.PredictNextOnset(0);
                Debug.Log($"🔮 Player 0: Next onset predicted at {prediction:F3}s");
            }
            catch (ARMETimingException ex)
            {
                Debug.LogError($"🔥 Prediction for Player 0 failed: {ex.Message}");
            }
        }

        [Button(enabledMode: EButtonEnableMode.Always)]
        private void PredictNextOnsets()
        {
            float[] predictions = null;
                
            if (_timingModel != null)
            {
                try
                {
                    predictions = _timingModel.PredictNextOnsets();
                    Debug.Log("🔮 Predictions:");
                    for (int i = 0; i < predictions.Length; i++)
                    {
                        Debug.Log($"  Player {i}: Next onset predicted at {predictions[i]:F3}s");
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"🔥 Prediction failed with exception: {ex.Message}");
                    Debug.LogError($"Stack trace: {ex.StackTrace}");
                }
            }
            else
            {
                Debug.LogError("Timing model became null before prediction!");
            }
        }

        [Button(enabledMode: EButtonEnableMode.Always)]
        private void PredictNextOnsetsAndRegister()
        {
            float[] predictions = null;
                
            if (_timingModel != null)
            {
                try
                {
                    predictions = _timingModel.PredictNextOnsets();
                    Debug.Log("🔮 Predictions:");
                    for (int i = 0; i < predictions.Length; i++)
                    {
                        Debug.Log($"  Player {i}: Next onset predicted at {predictions[i]:F3}s");
                    }

                    // If predictions succeeded, register them as new onsets
                    for (int i = 0; i < predictions.Length; i++)
                    {
                        _timingModel.RegisterOnset(i, predictions[i]);
                        Debug.Log($"  Player {i}: Registered predicted onset at {predictions[i]:F3}s");
                    }

                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"🔥 Prediction failed with exception: {ex.Message}");
                    Debug.LogError($"Stack trace: {ex.StackTrace}");
                }
            }
            else
            {
                Debug.LogError("Timing model became null before prediction!");
            }
        }

        [Header("Timing Model Setup")]
        [Tooltip("Number of players/instruments in the ensemble (1-16)")]
        [SerializeField] private int numberOfPlayers = 4;
        
        [Header("Simulation Settings")]
        [Tooltip("Simulate realistic timing variations")]
        [SerializeField] private bool addTimingVariations = true;
        
        [Tooltip("Base tempo for simulation (beats per minute)")]
        [SerializeField] [Range(60f, 200f)] private float baseTempo = 120f;

        // Private members
        private SimpleTimingModel _timingModel;
        private bool _isInitialized = false;
        private float _simulationTime = 0f;
        private float _lastOnsetTime = 0f;

        /// <summary>
        /// Initialize the native timing model
        /// </summary>
        private void InitializeTimingModel()
        {
            try
            {
                // Create timing model
                _timingModel = new SimpleTimingModel(numberOfPlayers);
                
                Debug.Log($"✓ Timing Model created: {numberOfPlayers} players");
                Debug.Log($"✓ Library version: {SimpleTimingModel.GetVersion()}");

                // Initialize parameters with additional safety
                _timingModel.CreateNewParameters();
                
                // Wait a moment for parameters to fully initialize
                System.Threading.Thread.Sleep(50);
                
                _isInitialized = true;
                Debug.Log($"🎵 Timing Model ready!");
            }
            catch (ARMETimingException ex)
            {
                Debug.LogError($"Failed to initialize timing model: {ex.Message}");
                Debug.LogError("Make sure ARMETimingModelLib.dll is in Assets/Plugins/");
            }
        }

        private void RegisterNewOnsetsWithVariation(float baseTime)
        {
            for (int i = 0; i < numberOfPlayers; i++)
            {
                float variation = addTimingVariations ? UnityEngine.Random.Range(-0.02f, 0.02f) : 0f;
                float onsetTime = baseTime + variation;

                _timingModel.RegisterOnset(i, onsetTime);
                Debug.Log($"  Player {i}: Registered second onset at {onsetTime:F3}s");
            }
        }

        private void RegisterInitialOnsets()
        {
            for (int i = 0; i < numberOfPlayers; i++)
            {
                // Add some realistic timing variation
                float onsetTime = 0.0f;

                _timingModel.RegisterOnset(i, onsetTime);
                Debug.Log($"  Player {i}: Registered onset at {onsetTime:F3}s");
            }
        }

        /// <summary>
        /// Clean up when destroyed
        /// </summary>
        void OnDestroy()
        {
            if (_timingModel != null)
            {
                _timingModel.Dispose();
                Debug.Log("✓ Timing Model cleaned up");
            }
        }

        /// <summary>
        /// Show debug information in the Inspector
        /// </summary>
        void OnValidate()
        {
            numberOfPlayers = Mathf.Clamp(numberOfPlayers, 1, 16);
            baseTempo = Mathf.Clamp(baseTempo, 60f, 200f);
        }
    }

}