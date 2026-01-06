/*
 * ARME DLL Test Utility for Unity (Editor Tool)
 * 
 * Simplified development tool for testing ARME native libraries in Unity Editor.
 * Tests both ARMEPlaybackController and ARMETimingModel libraries for compatibility,
 * architecture matching, and basic functionality.
 * 
 * SETUP:
 * - Attach to any GameObject
 * - Toggle "Run Tests" to execute tests in editor
 * - View results in the Inspector
 * 
 * FEATURES:
 * - Editor-mode testing only
 * - Automatic platform detection
 * - Inspector-based results display
 * - Troubleshooting guidance
 */

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using System.IO;

namespace ARMEUtilities
{
    /// <summary>
    /// Simplified ARME DLL Test Utility for Editor Development
    /// </summary>
    public class ARMEDLLTestUtility : MonoBehaviour
    {
        [Header("Test Control")]
        [Tooltip("Toggle ON to run tests in editor (automatically toggles back OFF)")]
        [SerializeField] private bool runTests = false;
        
        [Header("Test Results")]
        [SerializeField, TextArea(3, 10)] private string playbackControllerResults = "Not tested yet";
        [SerializeField, TextArea(3, 10)] private string timingModelResults = "Not tested yet";
        [SerializeField, TextArea(2, 5)] private string platformInfo = "";
        [SerializeField, TextArea(2, 5)] private string overallStatus = "Ready to test";

        // Test results storage
        private LibraryTestInfo _playbackControllerTest;
        private LibraryTestInfo _timingModelTest;
        
        // Platform information
        private string _currentPlatform;
        private string _currentArchitecture;
        private string _expectedLibraryExtension;
        private string _pluginPath;

        /// <summary>
        /// OnValidate is called in editor when script is loaded or inspector values change
        /// </summary>
        void OnValidate()
        {
            // Only run in editor mode, and only if the toggle is on
            if (!Application.isPlaying && runTests)
            {
                // Reset the toggle to prevent continuous execution
                runTests = false;
                
                // Initialize and run tests
                InitializePlatformInfo();
                RunAllTests();
            }
        }

        /// <summary>
        /// Initialize platform detection (called from OnValidate)
        /// </summary>
        private void InitializePlatformInfo()
        {
            // Initialize test info
            _playbackControllerTest = new LibraryTestInfo("ARME Playback Controller");
            _timingModelTest = new LibraryTestInfo("ARME Timing Model");
            
            // Detect platform information
            DetectPlatformInfo();
            
            // Update platform info display
            platformInfo = $"Platform: {_currentPlatform}\n" +
                          $"Architecture: {_currentArchitecture}\n" +
                          $"Library Extension: {_expectedLibraryExtension}\n" +
                          $"Plugin Path: {_pluginPath}";
        }

        /// <summary>
        /// Detect current platform and architecture information
        /// </summary>
        private void DetectPlatformInfo()
        {
            // Platform detection
#if UNITY_STANDALONE_WIN
            _currentPlatform = "Windows";
            _expectedLibraryExtension = ".dll";
#elif UNITY_STANDALONE_OSX
            _currentPlatform = "macOS";
            _expectedLibraryExtension = ".dylib";
#elif UNITY_STANDALONE_LINUX
            _currentPlatform = "Linux";
            _expectedLibraryExtension = ".so";
#else
            _currentPlatform = "Unknown";
            _expectedLibraryExtension = ".dll"; // Default to Windows
#endif

            // Architecture detection
            _currentArchitecture = Environment.Is64BitProcess ? "x86_64" : "x86";
            
            // Plugin path detection
            _pluginPath = Path.Combine(Application.dataPath, "Plugins");
            if (_currentArchitecture == "x86_64")
            {
                _pluginPath = Path.Combine(_pluginPath, "x86_64");
            }
            else if (_currentArchitecture == "x86")
            {
                _pluginPath = Path.Combine(_pluginPath, "x86");
            }

            Debug.Log($"[ARME DLL Test] Platform: {_currentPlatform} {_currentArchitecture}, Plugin Path: {_pluginPath}");
        }

        /// <summary>
        /// Run all available tests and update inspector display
        /// </summary>
        public void RunAllTests()
        {
            Debug.Log("[ARME DLL Test] Starting DLL tests in editor mode...");

            TestPlaybackController();
            TestTimingModel();
            
            UpdateInspectorResults();
            
            Debug.Log("[ARME DLL Test] All tests completed!");
        }

        /// <summary>
        /// Test ARMEPlaybackController library
        /// </summary>
        public void TestPlaybackController()
        {
            Debug.Log("[ARME DLL Test] Testing Playback Controller...");
            
            _playbackControllerTest = new LibraryTestInfo("ARME Playback Controller");
            
            try
            {
                // Test 1: File existence
                string libraryName = "ARMEPlaybackControllerLib";
                if (!CheckLibraryFileExists(libraryName, out string filePath))
                {
                    _playbackControllerTest.result = TestResult.Failed;
                    _playbackControllerTest.message = "Library file not found";
                    _playbackControllerTest.details = GetFileNotFoundTroubleshooting(libraryName);
                    return;
                }
                
                // Test 2: Basic loading and function test
                var testResult = TestPlaybackControllerFunctions();
                _playbackControllerTest.result = testResult.success ? TestResult.Passed : TestResult.Failed;
                _playbackControllerTest.message = testResult.message;
                _playbackControllerTest.details = testResult.details;
                
                if (_playbackControllerTest.result == TestResult.Passed)
                {
                    // Test 3: Dependency check (Windows only)
                    if (_currentPlatform == "Windows")
                    {
                        var depResult = CheckPlaybackControllerDependencies();
                        if (!depResult.allFound)
                        {
                            _playbackControllerTest.result = TestResult.Warning;
                            _playbackControllerTest.message = "Core functions work, but some dependencies missing";
                            _playbackControllerTest.details += $"\n\nDependency Check:\n{depResult.message}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _playbackControllerTest.result = TestResult.Failed;
                _playbackControllerTest.message = "Unexpected error during testing";
                _playbackControllerTest.details = $"Exception: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}";
            }
            
            LogTestResult(_playbackControllerTest);
        }

        /// <summary>
        /// Test ARMETimingModel library
        /// </summary>
        public void TestTimingModel()
        {
            Debug.Log("[ARME DLL Test] Testing Timing Model...");
            
            _timingModelTest = new LibraryTestInfo("ARME Timing Model");
            
            try
            {
                // Test 1: File existence
                string libraryName = "ARMETimingModelLib";
                if (!CheckLibraryFileExists(libraryName, out string filePath))
                {
                    _timingModelTest.result = TestResult.Failed;
                    _timingModelTest.message = "Library file not found";
                    _timingModelTest.details = GetFileNotFoundTroubleshooting(libraryName);
                    return;
                }
                
                // Test 2: Basic loading and function test
                var testResult = TestTimingModelFunctions();
                _timingModelTest.result = testResult.success ? TestResult.Passed : TestResult.Failed;
                _timingModelTest.message = testResult.message;
                _timingModelTest.details = testResult.details;
                
            }
            catch (Exception ex)
            {
                _timingModelTest.result = TestResult.Failed;
                _timingModelTest.message = "Unexpected error during testing";
                _timingModelTest.details = $"Exception: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}";
            }
            
            LogTestResult(_timingModelTest);
        }

        /// <summary>
        /// Update inspector display fields with test results
        /// </summary>
        private void UpdateInspectorResults()
        {
            // Update individual test results
            playbackControllerResults = FormatTestResult(_playbackControllerTest);
            timingModelResults = FormatTestResult(_timingModelTest);
            
            // Update overall status
            bool allPassed = _playbackControllerTest.result == TestResult.Passed && 
                           _timingModelTest.result == TestResult.Passed;
            bool anyFailed = _playbackControllerTest.result == TestResult.Failed || 
                           _timingModelTest.result == TestResult.Failed;
            
            if (allPassed)
            {
                overallStatus = "✅ All tests PASSED!\nLibraries are ready for use.";
            }
            else if (anyFailed)
            {
                overallStatus = "❌ Some tests FAILED.\nCheck individual results for details.";
            }
            else
            {
                overallStatus = "⚠️ Tests completed with warnings.\nLibraries should work but may have minor issues.";
            }
        }

        /// <summary>
        /// Format individual test result for inspector display
        /// </summary>
        private string FormatTestResult(LibraryTestInfo test)
        {
            string icon = test.result switch
            {
                TestResult.Passed => "✅",
                TestResult.Failed => "❌",
                TestResult.Warning => "⚠️",
                _ => "⏸️"
            };
            
            string result = $"{icon} {test.result}: {test.message}";
            
            if (!string.IsNullOrEmpty(test.details))
            {
                result += $"\n\nDetails:\n{test.details}";
            }
            
            return result;
        }

        /// <summary>
        /// Check if a library file exists in the expected location
        /// </summary>
        private bool CheckLibraryFileExists(string libraryName, out string filePath)
        {
            filePath = "";
            
            // Try different possible paths
            string[] possiblePaths = {
                Path.Combine(_pluginPath, libraryName + _expectedLibraryExtension),
                Path.Combine(Application.dataPath, "Plugins", libraryName + _expectedLibraryExtension),
                Path.Combine(Application.streamingAssetsPath, libraryName + _expectedLibraryExtension)
            };
            
            foreach (string path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    filePath = path;
                    return true;
                }
            }
            
            return false;
        }

        /// <summary>
        /// Test Playback Controller native functions
        /// </summary>
        private (bool success, string message, string details) TestPlaybackControllerFunctions()
        {
            try
            {
                var testHandle = PlaybackControllerNativeMethods.arme_playback_create(44100, 512);
                if (testHandle != IntPtr.Zero)
                {
                    PlaybackControllerNativeMethods.arme_playback_destroy(testHandle);
                    return (true, "All functions working correctly", 
                           "✓ Library loaded successfully\n✓ Create/destroy functions working\n✓ Basic functionality verified");
                }
                else
                {
                    return (false, "Function returned null handle", 
                           "Library loaded but arme_playback_create returned null.\nThis may indicate:\n" +
                           "- Library build issues\n- Missing dependencies\n- Initialization problems");
                }
            }
            catch (DllNotFoundException ex)
            {
                return (false, "Library not found", 
                       $"DllNotFoundException: {ex.Message}\n\n" +
                       GetDllNotFoundTroubleshooting("ARMEPlaybackControllerLib"));
            }
            catch (BadImageFormatException ex)
            {
                return (false, "Architecture mismatch", 
                       $"BadImageFormatException: {ex.Message}\n\n" +
                       GetArchitectureMismatchTroubleshooting());
            }
            catch (Exception ex)
            {
                return (false, $"Unexpected error: {ex.GetType().Name}", 
                       $"Exception: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Test Timing Model native functions
        /// </summary>
        private (bool success, string message, string details) TestTimingModelFunctions()
        {
            try
            {
                var testHandle = TimingModelNativeMethods.ARME_CreateTimingModel(4);
                if (testHandle != IntPtr.Zero)
                {
                    var result = TimingModelNativeMethods.ARME_DestroyTimingModel(testHandle);
                    return (true, "All functions working correctly", 
                           "✓ Library loaded successfully\n✓ Create/destroy functions working\n✓ Basic functionality verified");
                }
                else
                {
                    return (false, "Function returned null handle", 
                           "Library loaded but ARME_CreateTimingModel returned null.\nThis may indicate:\n" +
                           "- Library build issues\n- Initialization problems\n- Parameter validation failures");
                }
            }
            catch (DllNotFoundException ex)
            {
                return (false, "Library not found", 
                       $"DllNotFoundException: {ex.Message}\n\n" +
                       GetDllNotFoundTroubleshooting("ARMETimingModelLib"));
            }
            catch (BadImageFormatException ex)
            {
                return (false, "Architecture mismatch", 
                       $"BadImageFormatException: {ex.Message}\n\n" +
                       GetArchitectureMismatchTroubleshooting());
            }
            catch (Exception ex)
            {
                return (false, $"Unexpected error: {ex.GetType().Name}", 
                       $"Exception: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Check Playback Controller dependencies (Windows-specific)
        /// </summary>
        private (bool allFound, string message) CheckPlaybackControllerDependencies()
        {
            if (_currentPlatform != "Windows")
                return (true, "Dependency checking not available on this platform");
            
            string[] dependencies = {
                "rubberband-3.dll",
                "samplerate.dll", 
                "sleef.dll",
                "sleefdft.dll"
            };
            
            var missing = new System.Collections.Generic.List<string>();
            var found = new System.Collections.Generic.List<string>();
            
            foreach (string dep in dependencies)
            {
                string depPath = Path.Combine(_pluginPath, dep);
                if (File.Exists(depPath))
                {
                    found.Add(dep);
                }
                else
                {
                    missing.Add(dep);
                }
            }
            
            string message = "";
            if (found.Count > 0)
            {
                message += $"Found dependencies ({found.Count}):\n";
                foreach (string f in found)
                    message += $"  ✓ {f}\n";
            }
            
            if (missing.Count > 0)
            {
                message += $"\nMissing dependencies ({missing.Count}):\n";
                foreach (string m in missing)
                    message += $"  ✗ {m}\n";
                message += "\nNote: Main library may still work if these are available in system PATH.";
            }
            
            return (missing.Count == 0, message);
        }

        /// <summary>
        /// Log individual test result to console
        /// </summary>
        private void LogTestResult(LibraryTestInfo test)
        {
            string message = $"[ARME DLL Test] {test.libraryName}: {test.result} - {test.message}";
            
            switch (test.result)
            {
                case TestResult.Failed:
                    Debug.LogError(message);
                    if (!string.IsNullOrEmpty(test.details))
                        Debug.LogError($"Details:\n{test.details}");
                    break;
                case TestResult.Warning:
                    Debug.LogWarning(message);
                    if (!string.IsNullOrEmpty(test.details))
                        Debug.LogWarning($"Details:\n{test.details}");
                    break;
                default:
                    Debug.Log(message);
                    if (!string.IsNullOrEmpty(test.details))
                        Debug.Log($"Details:\n{test.details}");
                    break;
            }
        }

        /// <summary>
        /// Generate troubleshooting information for file not found errors
        /// </summary>
        private string GetFileNotFoundTroubleshooting(string libraryName)
        {
            return $"Library '{libraryName}{_expectedLibraryExtension}' not found.\n\n" +
                   $"CHECK THESE LOCATIONS:\n" +
                   $"• {_pluginPath}/\n" +
                   $"• Assets/Plugins/\n" +
                   $"• Assets/StreamingAssets/\n\n" +
                   $"VERIFY:\n" +
                   $"• Library is built for {_currentPlatform} {_currentArchitecture}\n" +
                   $"• File naming: {libraryName}{_expectedLibraryExtension}\n" +
                   $"• Unity import settings match platform/architecture";
        }

        /// <summary>
        /// Generate troubleshooting information for DLL not found errors
        /// </summary>
        private string GetDllNotFoundTroubleshooting(string libraryName)
        {
            return $"TROUBLESHOOTING DLL NOT FOUND:\n\n" +
                   $"LOCATION:\n" +
                   $"• Assets/Plugins/{_currentArchitecture}/\n" +
                   $"• Assets/Plugins/\n" +
                   $"• System PATH\n\n" +
                   $"UNITY SETTINGS:\n" +
                   $"• Select {libraryName}{_expectedLibraryExtension}\n" +
                   $"• Set Platform: {_currentPlatform}\n" +
                   $"• Set Architecture: {_currentArchitecture}\n\n" +
                   $"DEPENDENCIES:\n" +
                   $"• All required DLLs must be present\n" +
                   $"• Dependencies must match architecture\n\n" +
                   GetPlatformSpecificTroubleshooting();
        }

        /// <summary>
        /// Generate troubleshooting information for architecture mismatch
        /// </summary>
        private string GetArchitectureMismatchTroubleshooting()
        {
            return $"ARCHITECTURE MISMATCH:\n\n" +
                   $"Unity Process: {_currentArchitecture}\n" +
                   $"Library was built for different architecture.\n\n" +
                   $"SOLUTIONS:\n" +
                   $"• Rebuild library for {_currentArchitecture}\n" +
                   $"• Use Unity matching library architecture\n" +
                   $"• Check Unity Player Settings > Configuration";
        }

        /// <summary>
        /// Get platform-specific troubleshooting tips
        /// </summary>
        private string GetPlatformSpecificTroubleshooting()
        {
            return _currentPlatform switch
            {
                "Windows" => "WINDOWS:\n• Use Dependency Walker for missing DLLs\n• Install Visual C++ Redistributable\n• Check Windows Event Viewer",
                "macOS" => "macOS:\n• Use 'otool -L library.dylib' for deps\n• Check code signing\n• Verify Xcode tools installed",
                "Linux" => "LINUX:\n• Use 'ldd library.so' for deps\n• Install system libraries\n• Check LD_LIBRARY_PATH",
                _ => "Platform-specific help not available."
            };
        }
    }

    /// <summary>
    /// Test results for individual library tests
    /// </summary>
    public enum TestResult
    {
        NotRun,
        Passed,
        Failed,
        Warning
    }

    /// <summary>
    /// Test information container
    /// </summary>
    [System.Serializable]
    public class LibraryTestInfo
    {
        public string libraryName;
        public TestResult result;
        public string message;
        public string details;
        
        public LibraryTestInfo(string name)
        {
            libraryName = name;
            result = TestResult.NotRun;
            message = "";
            details = "";
        }
    }

    // ==========================================
    // NATIVE METHOD DECLARATIONS FOR TESTING
    // ==========================================

    /// <summary>
    /// Minimal native methods for Playback Controller testing
    /// </summary>
    internal static class PlaybackControllerNativeMethods
    {
        private const string LibName = "ARMEPlaybackControllerLib";

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr arme_playback_create(double sampleRate, int blockSize);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void arme_playback_destroy(IntPtr handle);
    }

    /// <summary>
    /// Minimal native methods for Timing Model testing
    /// </summary>
    internal static class TimingModelNativeMethods
    {
        private const string LibName = "ARMETimingModelLib";

        public enum ARMEResult : int
        {
            Success = 0,
            ErrorInvalidHandle = -1,
            ErrorInvalidParameter = -2,
            ErrorInvalidPlayerIndex = -3,
            ErrorOutOfMemory = -4
        }

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ARME_CreateTimingModel(int numberOfPlayers);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMEResult ARME_DestroyTimingModel(IntPtr model);
    }
}