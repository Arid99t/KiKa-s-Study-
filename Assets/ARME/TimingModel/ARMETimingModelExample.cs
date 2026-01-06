/*
 * C# Example for ARME Timing Model - Beginner Friendly
 * 
 * This example shows how to use the ARME Timing Model library in C# and Unity
 * for musical ensemble timing prediction and synchronization.
 * 
 * UNITY SETUP INSTRUCTIONS:
 * =========================
 * 1. Build the ARME Timing Model project to create ARMETimingModelLib.dll
 * 2. In your Unity project, create folder: Assets/Plugins/x86_64/ (for 64-bit)
 * 3. Copy ARMETimingModelLib.dll to Assets/Plugins/x86_64/ or add containing folder to windows PATH
 * 4. Copy this C# script into your Assets/Scripts/ folder
 * 5. Copy SimpleTimingModel.cs into your Assets/Scripts/ folder
 * 6. Attach this script to a GameObject in your scene
 * 7. Hit Play and use the controls to test timing predictions!
 * 
 * TROUBLESHOOTING DLL NOT FOUND:
 * ==============================
 * If you get "DllNotFoundException", try these steps in order:
 * 
 * A) CHECK DLL ARCHITECTURE:
 *    - For 64-bit Unity: Put DLL in Assets/Plugins/x86_64/
 *    - For 32-bit Unity: Put DLL in Assets/Plugins/x86/
 *    - Check Unity architecture: Edit > Project Settings > Player > Configuration
 * 
 * B) VERIFY DLL EXISTS:
 *    - Make sure ARMETimingModelLib.dll is built and copied correctly
 *    - Copy from: Libraries/ARMETimingModel/build-vs2022-c-api/Debug/bin/
 * 
 * C) CHECK UNITY PLATFORM SETTINGS:
 *    - Select DLL in Unity Inspector
 *    - Set "Platform Settings" to match your target (Standalone, Editor, etc.)
 *    - Set "Architecture" to x86_64 for 64-bit builds
 * 
 * D) ALTERNATIVE: Try copying to main Plugins folder:
 *    - Some Unity versions work with Assets/Plugins/ (no subfolder)
 *    - Try this if the x86_64 subfolder doesn't work
 * 
 * IMPORTANT NOTES FOR BEGINNERS:
 * - This uses the SimpleTimingModel.cs wrapper for easy C# access
 * - Perfect for ensemble synchronization, rhythm games, and music applications
 * - Timing model learns from onset patterns and predicts future events
 * - Always clean up native resources to prevent memory leaks
 */

using System;
using UnityEngine;
using UnityEngine.UI;
using ARMETiming;

/*
 * UNITY COMPONENT EXAMPLE - Real-time Musical Timing Prediction!
 * 
 * UNITY SETUP CHECKLIST:
 * ✓ ARMETimingModelLib.dll copied to Assets/Plugins/
 * ✓ SimpleTimingModel.cs copied to Assets/Scripts/
 * ✓ This C# script copied to Assets/Scripts/
 * ✓ Script attached to a GameObject in your scene
 * ✓ Hit Play and test the timing model with the buttons!
 */
namespace ARMETiming
{
    /// <summary>
    /// Unity component for real-time musical timing prediction - Super easy to use!
    /// 
    /// BEGINNER GUIDE:
    /// 1. Attach this to a GameObject in your scene
    /// 2. Set numberOfPlayers in the Inspector (1-16)
    /// 3. Optionally connect UI elements for interactive control
    /// 4. Hit Play to test timing model functionality!
    /// 
    /// FEATURES:
    /// - Real-time onset registration and timing prediction
    /// - Support for multiple players/instruments
    /// - Unity UI integration for testing and visualization
    /// - Automatic ensemble synchronization analysis
    /// - Perfect for rhythm games and interactive music systems
    /// </summary>
    public class ARMETimingModelUnityExample : MonoBehaviour
    {
        [Header("Timing Model Setup")]
        [Tooltip("Number of players/instruments in the ensemble (1-16)")]
        [SerializeField] private int numberOfPlayers = 4;
        
        [Tooltip("Run automatic example on start")]
        [SerializeField] private bool runExampleOnStart = true;
        
        [Header("UI Controls (Optional)")]
        [Tooltip("Button to register onset for Player 0")]
        [SerializeField] private Button registerOnsetButton0;
        
        [Tooltip("Button to register onset for Player 1")]
        [SerializeField] private Button registerOnsetButton1;
        
        [Tooltip("Button to register onset for Player 2")]
        [SerializeField] private Button registerOnsetButton2;
        
        [Tooltip("Button to register onset for Player 3")]
        [SerializeField] private Button registerOnsetButton3;
        
        [Tooltip("Button to predict next onsets for all players")]
        [SerializeField] private Button predictOnsetsButton;
        
        [Tooltip("Button to reset the timing model")]
        [SerializeField] private Button resetModelButton;
        
        [Tooltip("Text area to display timing information")]
        [SerializeField] private Text infoText;
        
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
        /// Initialize the timing model and UI
        /// </summary>
        void Start()
        {
            // Initialize the timing model
            InitializeTimingModel();
            
            // Setup UI controls
            InitializeUI();
            
            // Run example if requested
            if (runExampleOnStart && _isInitialized)
            {
                StartCoroutine(RunAutomaticExampleCoroutine());
            }
        }

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
                Debug.Log($"🎵 Timing Model ready! Auto-start: {runExampleOnStart}");

                // Update UI
                UpdateInfoDisplay($"✓ ARME Timing Model Ready!\nPlayers: {numberOfPlayers}\nVersion: {SimpleTimingModel.GetVersion()}");
            }
            catch (ARMETimingException ex)
            {
                Debug.LogError($"Failed to initialize timing model: {ex.Message}");
                Debug.LogError("Make sure ARMETimingModelLib.dll is in Assets/Plugins/");
                UpdateInfoDisplay($"❌ Initialization Failed:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Initialize UI button handlers
        /// </summary>
        private void InitializeUI()
        {
            // Setup onset registration buttons
            if (registerOnsetButton0 != null)
                registerOnsetButton0.onClick.AddListener(() => RegisterOnsetForPlayer(0));
            
            if (registerOnsetButton1 != null)
                registerOnsetButton1.onClick.AddListener(() => RegisterOnsetForPlayer(1));
            
            if (registerOnsetButton2 != null)
                registerOnsetButton2.onClick.AddListener(() => RegisterOnsetForPlayer(2));
            
            if (registerOnsetButton3 != null)
                registerOnsetButton3.onClick.AddListener(() => RegisterOnsetForPlayer(3));
            
            // Setup prediction button
            if (predictOnsetsButton != null)
                predictOnsetsButton.onClick.AddListener(PredictAllOnsets);
            
            // Setup reset button
            if (resetModelButton != null)
                resetModelButton.onClick.AddListener(ResetTimingModel);
        }

        /// <summary>
        /// Update simulation time
        /// </summary>
        void Update()
        {
            _simulationTime = Time.time;
        }

        /// <summary>
        /// Run automatic example coroutine
        /// </summary>
        private System.Collections.IEnumerator RunAutomaticExampleCoroutine()
        {
            yield return new WaitForSeconds(1f);
            yield return StartCoroutine(RunBasicExampleCoroutine());
        }

        /// <summary>
        /// Run a comprehensive example showing all timing model features
        /// </summary>
        public System.Collections.IEnumerator RunBasicExampleCoroutine()
        {
            if (!_isInitialized || _timingModel == null) yield break;

                Debug.Log("🎵 Running Comprehensive Timing Model Example:");
                UpdateInfoDisplay("🎵 Running Example...\n");

                // Step 1: Register initial onsets with realistic timing
                Debug.Log("📝 Step 1: Registering initial onsets");
                float baseTime = _simulationTime;
                float beatInterval = 60f / baseTempo; // Convert BPM to seconds per beat

                for (int i = 0; i < numberOfPlayers; i++)
                {
                    // Add some realistic timing variation
                    float variation = addTimingVariations ? UnityEngine.Random.Range(-0.02f, 0.02f) : 0f;
                    float onsetTime = baseTime + (i * 0.01f) + variation; // Slight staggering + variation
                    
                    _timingModel.RegisterOnset(i, onsetTime);
                    Debug.Log($"  Player {i}: Registered onset at {onsetTime:F3}s");
                }

                yield return new WaitForSeconds(0.5f);

                // Step 2: Register second round of onsets
                Debug.Log("📝 Step 2: Registering second round");
                baseTime += beatInterval;
                
                for (int i = 0; i < numberOfPlayers; i++)
                {
                    float variation = addTimingVariations ? UnityEngine.Random.Range(-0.015f, 0.015f) : 0f;
                    float onsetTime = baseTime + (i * 0.008f) + variation;
                    
                    _timingModel.RegisterOnset(i, onsetTime);
                    Debug.Log($"  Player {i}: Registered second onset at {onsetTime:F3}s");
                }

                yield return new WaitForSeconds(0.5f);

                // Step 3: Make predictions with error checking
                Debug.Log("🔮 Step 3: Predicting next onsets");
                
                // Add a small delay to ensure parameters are fully initialized
                yield return new WaitForSeconds(0.1f);
                
                float[] predictions = null;
                bool predictionSuccessful = false;
                
                // First prediction attempt
                if (_timingModel != null)
                {
                    try
                    {
                        predictions = _timingModel.PredictNextOnsets();
                        predictionSuccessful = true;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"🔥 Prediction failed with exception: {ex.Message}");
                        Debug.LogError($"Stack trace: {ex.StackTrace}");
                        predictionSuccessful = false;
                    }
                }
                else
                {
                    Debug.LogError("Timing model became null before prediction!");
                    yield break;
                }
                
                // Recovery attempt if first prediction failed
                if (!predictionSuccessful)
                {
                    Debug.Log("🔧 Attempting to recover by reinitializing parameters...");
                    try
                    {
                        _timingModel.CreateNewParameters();
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"❌ Recovery failed during parameter initialization: {ex.Message}");
                        yield break;
                    }
                    
                    yield return new WaitForSeconds(0.2f);
                    
                    try
                    {
                        predictions = _timingModel.PredictNextOnsets();
                        predictionSuccessful = true;
                        Debug.Log("✅ Recovery successful!");
                    }
                    catch (System.Exception recoveryEx)
                    {
                        Debug.LogError($"❌ Recovery failed: {recoveryEx.Message}");
                        yield break;
                    }
                }
                
                if (!predictionSuccessful || predictions == null)
                {
                    Debug.LogError("❌ Predictions failed and could not be recovered!");
                    yield break;
                }
                
                string predictionInfo = "🔮 Predictions:\n";
                for (int i = 0; i < predictions.Length; i++)
                {
                    Debug.Log($"  Player {i}: Next onset predicted at {predictions[i]:F3}s");
                    predictionInfo += $"Player {i}: {predictions[i]:F3}s\n";
                }

                // Step 4: Show model statistics
                Debug.Log("📊 Step 4: Model Statistics");
                string statsInfo = "\n📊 Statistics:\n";
                
                for (int i = 0; i < numberOfPlayers; i++)
                {
                    int onsetCount = _timingModel.GetNumberOfOnsets(i);
                    float latestOnset = _timingModel.GetLatestOnset(i);
                    Debug.Log($"  Player {i}: {onsetCount} onsets, latest at {latestOnset:F3}s");
                    statsInfo += $"Player {i}: {onsetCount} onsets, latest {latestOnset:F3}s\n";
                }

                int totalNotes = _timingModel.GetTotalNumberOfNotes();
                int predictions_count = _timingModel.GetNumberOfCalculatedPredictions();
                Debug.Log($"Total notes: {totalNotes}, Predictions calculated: {predictions_count}");
                statsInfo += $"Total notes: {totalNotes}\nPredictions: {predictions_count}";

                // Update display
                UpdateInfoDisplay(predictionInfo + statsInfo);

                Debug.Log("✅ Example completed successfully!");

                // Step 5: Optional - demonstrate continuous prediction
                if (addTimingVariations)
                {
                    yield return new WaitForSeconds(1f);
                    StartCoroutine(ContinuousSimulation());
                }
        }

        /// <summary>
        /// Continuous simulation for demonstration
        /// </summary>
        private System.Collections.IEnumerator ContinuousSimulation()
        {
            Debug.Log("🔄 Starting continuous simulation...");
            
            for (int cycle = 0; cycle < 3; cycle++)
            {
                yield return new WaitForSeconds(60f / baseTempo); // Wait one beat
                
                // Register onsets based on predictions
                var predictions = _timingModel.PredictNextOnsets();
                
                for (int i = 0; i < numberOfPlayers; i++)
                {
                    float variation = UnityEngine.Random.Range(-0.01f, 0.01f);
                    float actualOnset = predictions[i] + variation;
                    _timingModel.RegisterOnset(i, actualOnset);
                    Debug.Log($"Cycle {cycle + 1} - Player {i}: Predicted {predictions[i]:F3}s, Actual {actualOnset:F3}s");
                }
                
                // Update display with latest predictions
                var newPredictions = _timingModel.PredictNextOnsets();
                string info = $"Cycle {cycle + 1} Predictions:\n";
                for (int i = 0; i < newPredictions.Length; i++)
                {
                    info += $"Player {i}: {newPredictions[i]:F3}s\n";
                }
                UpdateInfoDisplay(info);
            }
            
            Debug.Log("🏁 Continuous simulation completed!");
        }

        /// <summary>
        /// Register onset for a specific player (called by UI buttons)
        /// </summary>
        public void RegisterOnsetForPlayer(int playerIndex)
        {
            if (!_isInitialized || _timingModel == null) return;

            try
            {
                float onsetTime = _simulationTime;
                _timingModel.RegisterOnset(playerIndex, onsetTime);
                Debug.Log($"🎵 Registered onset: Player {playerIndex} at {onsetTime:F3}s");
                
                // Update display with latest info
                UpdatePlayerInfo();
            }
            catch (ARMETimingException ex)
            {
                Debug.LogWarning($"Failed to register onset: {ex.Message}");
                UpdateInfoDisplay($"❌ Registration Failed:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Predict onsets for all players (called by UI button)
        /// </summary>
        public void PredictAllOnsets()
        {
            if (!_isInitialized || _timingModel == null) return;

            try
            {
                // Add null check before prediction
                if (_timingModel == null)
                {
                    Debug.LogWarning("❌ Timing model is null, cannot predict");
                    return;
                }
                
                var predictions = _timingModel.PredictNextOnsets();
                
                if (predictions == null)
                {
                    Debug.LogWarning("❌ Predictions returned null");
                    UpdateInfoDisplay("❌ Prediction Failed:\nReturned null result");
                    return;
                }
                
                Debug.Log("🔮 Current predictions:");
                string info = "🔮 Next Onset Predictions:\n";
                
                for (int i = 0; i < predictions.Length; i++)
                {
                    Debug.Log($"  Player {i}: {predictions[i]:F3}s");
                    info += $"Player {i}: {predictions[i]:F3}s\n";
                }
                
                UpdateInfoDisplay(info);
            }
            catch (ARMETimingException ex)
            {
                Debug.LogError($"❌ Timing prediction failed: {ex.Message}");
                UpdateInfoDisplay($"❌ Prediction Failed:\n{ex.Message}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"❌ Unexpected error during prediction: {ex.Message}");
                UpdateInfoDisplay($"❌ Unexpected Error:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Reset the timing model (called by UI button)
        /// </summary>
        public void ResetTimingModel()
        {
            if (!_isInitialized || _timingModel == null) return;

            try
            {
                _timingModel.Reset();
                _timingModel.CreateNewParameters();
                Debug.Log("🔄 Timing model reset");
                UpdateInfoDisplay("🔄 Model Reset\nReady for new onsets!");
            }
            catch (ARMETimingException ex)
            {
                Debug.LogWarning($"Failed to reset model: {ex.Message}");
                UpdateInfoDisplay($"❌ Reset Failed:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Update player information display
        /// </summary>
        private void UpdatePlayerInfo()
        {
            if (!_isInitialized || _timingModel == null) return;

            try
            {
                string info = "📊 Current Status:\n";
                
                for (int i = 0; i < numberOfPlayers; i++)
                {
                    int onsetCount = _timingModel.GetNumberOfOnsets(i);
                    if (onsetCount > 0)
                    {
                        float latestOnset = _timingModel.GetLatestOnset(i);
                        info += $"Player {i}: {onsetCount} onsets, latest {latestOnset:F3}s\n";
                    }
                    else
                    {
                        info += $"Player {i}: No onsets registered\n";
                    }
                }
                
                int totalNotes = _timingModel.GetTotalNumberOfNotes();
                info += $"\nTotal Notes: {totalNotes}";
                
                UpdateInfoDisplay(info);
            }
            catch (ARMETimingException ex)
            {
                UpdateInfoDisplay($"❌ Info Update Failed:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Update the info display text
        /// </summary>
        private void UpdateInfoDisplay(string message)
        {
            if (infoText != null)
            {
                infoText.text = message;
            }
        }

        /// <summary>
        /// Public wrapper method for UI button calls to run the basic example
        /// </summary>
        public void RunBasicExample()
        {
            StartCoroutine(RunBasicExampleCoroutine());
        }

        /// <summary>
        /// Public methods for external control
        /// </summary>
        
        /// <summary>
        /// Register an onset for any player (external API)
        /// </summary>
        public bool RegisterOnset(int playerIndex, float onsetTime)
        {
            if (!_isInitialized || _timingModel == null) return false;

            try
            {
                _timingModel.RegisterOnset(playerIndex, onsetTime);
                Debug.Log($"🎵 External onset registered: Player {playerIndex} at {onsetTime:F3}s");
                return true;
            }
            catch (ARMETimingException ex)
            {
                Debug.LogWarning($"Failed to register external onset: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get prediction for a specific player (external API)
        /// </summary>
        public float GetPredictionForPlayer(int playerIndex)
        {
            if (!_isInitialized || _timingModel == null) return 0f;

            try
            {
                return _timingModel.PredictNextOnset(playerIndex);
            }
            catch (ARMETimingException ex)
            {
                Debug.LogWarning($"Cannot predict for player {playerIndex}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// Get all current predictions (external API)
        /// </summary>
        public float[] GetAllPredictions()
        {
            if (!_isInitialized || _timingModel == null) return new float[numberOfPlayers];

            try
            {
                return _timingModel.PredictNextOnsets();
            }
            catch (ARMETimingException ex)
            {
                Debug.LogWarning($"Cannot get predictions: {ex.Message}");
                return new float[numberOfPlayers];
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

    /// <summary>
    /// Simple UI controller for the timing model example - attach to a Canvas
    /// </summary>
    public class TimingModelControllerUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private ARMETimingModelUnityExample timingController;
        [SerializeField] private Button[] playerOnsetButtons;
        [SerializeField] private Button predictButton;
        [SerializeField] private Button resetButton;
        [SerializeField] private Button runExampleButton;
        [SerializeField] private Text displayText;
        [SerializeField] private Slider tempoSlider;
        [SerializeField] private Text tempoText;

        void Start()
        {
            // Setup UI event handlers
            if (resetButton != null)
                resetButton.onClick.AddListener(() => timingController?.ResetTimingModel());
            
            if (predictButton != null)
                predictButton.onClick.AddListener(() => timingController?.PredictAllOnsets());
            
            if (runExampleButton != null)
                runExampleButton.onClick.AddListener(() => timingController?.RunBasicExample());
            
            // Setup player onset buttons
            for (int i = 0; i < playerOnsetButtons.Length; i++)
            {
                int playerIndex = i; // Capture for closure
                if (playerOnsetButtons[i] != null)
                    playerOnsetButtons[i].onClick.AddListener(() => timingController?.RegisterOnsetForPlayer(playerIndex));
            }
            
            // Setup tempo slider
            if (tempoSlider != null)
            {
                tempoSlider.onValueChanged.AddListener(OnTempoChanged);
                UpdateTempoDisplay();
            }
        }

        private void OnTempoChanged(float tempo)
        {
            UpdateTempoDisplay();
            // Here you could update the timing controller's tempo if needed
        }

        private void UpdateTempoDisplay()
        {
            if (tempoText != null && tempoSlider != null)
            {
                tempoText.text = $"Tempo: {tempoSlider.value:F0} BPM";
            }
        }
    }
}