using System;
using System.Runtime.InteropServices;

/// <summary>
/// Unity C# wrapper for ARME Playback Controller - Perfect for real-time audio time-stretching
/// </summary>
namespace ARMEPlayback
{
    /// <summary>
    /// Error codes returned by the native library (must match the C header file)
    /// </summary>
    public enum ARMEPlaybackResult : int
    {
        Success = 0,
        ErrorInvalidHandle = -1,
        ErrorInvalidParameter = -2,
        ErrorNotInitialized = -3,
        ErrorBufferTooSmall = -4,
        ErrorThreadError = -5
    }

    /// <summary>
    /// Simple exception class for ARME Playback errors
    /// </summary>
    public class ARMEPlaybackException : Exception
    {
        public ARMEPlaybackException(string message) : base(message) { }
    }

    /// <summary>
    /// Native C API bindings for ARME Playback Controller
    /// 
    /// BEGINNER NOTE: These [DllImport] declarations connect C# methods to the 
    /// native library functions. The library name must match your .dll file name.
    /// </summary>
    internal static class NativeMethods
    {
        // IMPORTANT: This name must match your .dll file name (without .dll extension)
        // For Unity: Place ARMEPlaybackControllerLib.dll in Assets/Plugins/
        private const string LibName = "ARMEPlaybackControllerLib";

        // Handle type for the playback controller (represents a native pointer)
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr arme_playback_create(double sampleRate, int blockSize);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void arme_playback_destroy(IntPtr handle);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMEPlaybackResult arme_playback_set_audio_buffer(IntPtr handle, 
            float[] audioData, int numSamples, double originalSampleRate);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMEPlaybackResult arme_playback_start(IntPtr handle);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMEPlaybackResult arme_playback_stop(IntPtr handle);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMEPlaybackResult arme_playback_get_samples(IntPtr handle,
            float[] outputBuffer, int numSamples, out int samplesWritten);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMEPlaybackResult arme_playback_set_time_ratio(IntPtr handle, float ratio);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMEPlaybackResult arme_playback_set_ratio_from_onset_time(IntPtr handle, float playback_onset_time, float stretched_onset_time);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMEPlaybackResult arme_playback_get_time_ratio(IntPtr handle, out float ratio);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMEPlaybackResult arme_playback_is_running(IntPtr handle, out int isRunning);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMEPlaybackResult arme_playback_get_available_samples(IntPtr handle, out int numAvailable);
    }

    /// <summary>
    /// Simple C# wrapper for ARME Playback Controller - Great for Unity audio time-stretching!
    /// 
    /// BEGINNER NOTE: This class wraps the native .dll functions in a C#-friendly way.
    /// It handles memory management and error checking automatically.
    /// Perfect for real-time audio time-stretching in Unity games and applications.
    /// </summary>
    public class SimplePlaybackController : IDisposable
    {
        private IntPtr _handle = IntPtr.Zero;
        private bool _disposed = false;
        private double _sampleRate;
        private int _blockSize;

        /// <summary>
        /// Create a new playback controller for time-stretching audio
        /// </summary>
        /// <param name="sampleRate">Audio sample rate (usually 44100 or 48000)</param>
        /// <param name="blockSize">Processing block size (usually 512 or 1024)</param>
        public SimplePlaybackController(double sampleRate = 44100.0, int blockSize = 1024)
        {
            if (sampleRate <= 0 || blockSize <= 0)
                throw new ArgumentException("Sample rate and block size must be positive");

            _sampleRate = sampleRate;
            _blockSize = blockSize;

            _handle = NativeMethods.arme_playback_create(sampleRate, blockSize);
            
            if (_handle == IntPtr.Zero)
                throw new ARMEPlaybackException("Failed to create playback controller - check that ARMEPlaybackControllerLib.dll is in Assets/Plugins/");
        }

        /// <summary>
        /// Cleanup when object is destroyed
        /// </summary>
        ~SimplePlaybackController()
        {
            Dispose(false);
        }

        /// <summary>
        /// Clean up native resources - ALWAYS call this when done!
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && _handle != IntPtr.Zero)
            {
                NativeMethods.arme_playback_destroy(_handle);
                _handle = IntPtr.Zero;
                _disposed = true;
            }
        }

        /// <summary>
        /// Helper method to check if native function calls succeed
        /// </summary>
        private void CheckResult(ARMEPlaybackResult result)
        {
            if (result != ARMEPlaybackResult.Success)
            {
                throw new ARMEPlaybackException($"ARME Playback Error: {result}");
            }
        }

        /// <summary>
        /// Load audio data for time-stretching
        /// BEGINNER NOTE: audioData should be mono float samples between -1.0 and 1.0
        /// </summary>
        public void SetAudioBuffer(float[] audioData, double originalSampleRate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimplePlaybackController));
            if (audioData == null || audioData.Length == 0)
                throw new ArgumentException("Audio data cannot be null or empty");

            var result = NativeMethods.arme_playback_set_audio_buffer(_handle, audioData, audioData.Length, originalSampleRate);
            CheckResult(result);
        }

        /// <summary>
        /// Start the time-stretching process
        /// </summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimplePlaybackController));
            
            var result = NativeMethods.arme_playback_start(_handle);
            CheckResult(result);
        }

        /// <summary>
        /// Stop the time-stretching process
        /// </summary>
        public void Stop()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimplePlaybackController));
            
            var result = NativeMethods.arme_playback_stop(_handle);
            CheckResult(result);
        }

        /// <summary>
        /// Get time-stretched audio samples for playback
        /// Returns the number of samples actually written to the output buffer
        /// </summary>
        public int GetSamples(float[] outputBuffer)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimplePlaybackController));
            if (outputBuffer == null) throw new ArgumentNullException(nameof(outputBuffer));
            
            var result = NativeMethods.arme_playback_get_samples(_handle, outputBuffer, outputBuffer.Length, out int samplesWritten);
            CheckResult(result);
            return samplesWritten;
        }

        /// <summary>
        /// Set the native time-stretching ratio.
        /// 1.0 = normal duration, 0.5 = double playback speed, 2.0 = half playback speed.
        /// </summary>
        public float TimeRatio
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SimplePlaybackController));
                
                var result = NativeMethods.arme_playback_get_time_ratio(_handle, out float ratio);
                CheckResult(result);
                return ratio;
            }
            set
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SimplePlaybackController));
                if (value <= 0.0f) throw new ArgumentException("Time ratio must be positive");
                
                var result = NativeMethods.arme_playback_set_time_ratio(_handle, value);
                CheckResult(result);
            }
        }

        /// <summary>
        /// Check if the time-stretcher is currently running
        /// </summary>
        public bool IsRunning
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SimplePlaybackController));
                
                var result = NativeMethods.arme_playback_is_running(_handle, out int isRunning);
                CheckResult(result);
                return isRunning != 0;
            }
        }

        /// <summary>
        /// Get the number of processed samples available in the buffer
        /// </summary>
        public int AvailableSamples
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SimplePlaybackController));
                
                var result = NativeMethods.arme_playback_get_available_samples(_handle, out int numAvailable);
                CheckResult(result);
                return numAvailable;
            }
        }

        /// <summary>
        /// Properties for easy access to configuration
        /// </summary>
        public double SampleRate => _sampleRate;
        public int BlockSize => _blockSize;


        /// <summary>        
        /// Set the time-stretching ratio based on original/playback and desired/stretched onset times
        /// </summary>
        public void SetRatioFromOnsetTime(float playbackOnsetTime, float stretchedOnsetTime)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimplePlaybackController));
            if (playbackOnsetTime < 0.0f || stretchedOnsetTime < 0.0f)
                throw new ArgumentException("Onset times must be non-negative");

            var result = NativeMethods.arme_playback_set_ratio_from_onset_time(_handle, playbackOnsetTime, stretchedOnsetTime);
            CheckResult(result);
        }


    }
}
