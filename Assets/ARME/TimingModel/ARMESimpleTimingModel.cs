using System;
using System.Runtime.InteropServices;

/// <summary>
/// Unity C# wrapper for ARME Timing Model - Perfect for musical timing prediction
/// </summary>
namespace ARMETiming
{
    /// <summary>
    /// Error codes returned by the native library (must match the C header file)
    /// </summary>
    public enum ARMETimingResult : int
    {
        Success = 0,
        ErrorInvalidHandle = -1,
        ErrorInvalidParameter = -2,
        ErrorInvalidPlayerIndex = -3,
        ErrorOutOfMemory = -4
    }

    /// <summary>
    /// Simple exception class for ARME Timing errors
    /// </summary>
    public class ARMETimingException : Exception
    {
        public ARMETimingException(string message) : base(message) { }
    }

    /// <summary>
    /// Native C API bindings for ARME Timing Model
    /// 
    /// BEGINNER NOTE: These [DllImport] declarations connect C# methods to the 
    /// native library functions. The library name must match your .dll file name.
    /// </summary>
    internal static class NativeMethods
    {
        // IMPORTANT: This name must match your .dll file name (without .dll extension)
        // For Unity: Place ARMETimingModelLib.dll in Assets/Plugins/
        private const string LibName = "ARMETimingModelLib";

        // Handle type for the timing model (represents a native pointer)
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ARME_CreateTimingModel(int numberOfPlayers);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_DestroyTimingModel(IntPtr model);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_ResetModel(IntPtr model);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_ResetOnsets(IntPtr model, int maxNumberOfOnsets);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_CreateNewParameters(IntPtr model, int numberOfPlayers);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_RegisterOnset(IntPtr model, int playerIndex, float onsetTime);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_RegisterOnsetWithIndex(IntPtr model, int playerIndex, float onsetTime, int onsetNumber);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_PredictNextOnset(IntPtr model, int playerIndex, out float nextOnset);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_PredictNextOnsets(IntPtr model, float[] nextOnsets, int arraySize);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_GetLatestOnset(IntPtr model, int playerIndex, out float onsetTime);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_GetOnsetForNoteIndex(IntPtr model, int playerIndex, int noteNumber, out float onsetTime);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_GetNumberOfOnsetsRegisteredForPlayer(IntPtr model, int playerIndex, out int numberOfOnsets);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_GetNumberOfPlayers(IntPtr model, out int numberOfPlayers);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_GetNumberOfNotesRegisteredByAllPlayers(IntPtr model, out int numberOfNotes);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ARMETimingResult ARME_GetNumberOfNextOnsetsCalculated(IntPtr model, out int numberOfCalculated);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ARME_GetVersion();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ARME_GetErrorString(ARMETimingResult result);
    }

    /// <summary>
    /// Simple C# wrapper for ARME Timing Model - Great for Unity musical timing prediction!
    /// 
    /// BEGINNER NOTE: This class wraps the native .dll functions in a C#-friendly way.
    /// It handles memory management and error checking automatically.
    /// Perfect for musical timing applications, ensemble synchronization, and rhythm analysis.
    /// </summary>
    public class SimpleTimingModel : IDisposable
    {
        private IntPtr _handle = IntPtr.Zero;
        private bool _disposed = false;
        private int _numberOfPlayers;

        /// <summary>
        /// Create a new timing model for musical ensemble timing prediction
        /// </summary>
        /// <param name="numberOfPlayers">Number of players/instruments in the ensemble (1-16)</param>
        public SimpleTimingModel(int numberOfPlayers = 4)
        {
            if (numberOfPlayers < 1 || numberOfPlayers > 16)
                throw new ArgumentException("Number of players must be between 1 and 16");

            _numberOfPlayers = numberOfPlayers;

            _handle = NativeMethods.ARME_CreateTimingModel(numberOfPlayers);
            
            if (_handle == IntPtr.Zero)
                throw new ARMETimingException("Failed to create timing model - check that ARMETimingModelLib.dll is in Assets/Plugins/");
        }

        /// <summary>
        /// Cleanup when object is destroyed
        /// </summary>
        ~SimpleTimingModel()
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
                NativeMethods.ARME_DestroyTimingModel(_handle);
                _handle = IntPtr.Zero;
                _disposed = true;
            }
        }

        /// <summary>
        /// Helper method to check if native function calls succeed
        /// </summary>
        private void CheckResult(ARMETimingResult result)
        {
            if (result != ARMETimingResult.Success)
            {
                IntPtr errorPtr = NativeMethods.ARME_GetErrorString(result);
                string errorMessage = Marshal.PtrToStringAnsi(errorPtr) ?? "Unknown error";
                throw new ARMETimingException($"ARME Timing Error: {errorMessage}");
            }
        }

        /// <summary>
        /// Reset the complete timing model (clears all onsets and parameters)
        /// </summary>
        public void Reset()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            
            var result = NativeMethods.ARME_ResetModel(_handle);
            CheckResult(result);
        }

        /// <summary>
        /// Reset only the onsets (keeps parameters)
        /// </summary>
        /// <param name="maxNumberOfOnsets">Maximum number of onsets to allow</param>
        public void ResetOnsets(int maxNumberOfOnsets = 1000)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            
            var result = NativeMethods.ARME_ResetOnsets(_handle, maxNumberOfOnsets);
            CheckResult(result);
        }

        /// <summary>
        /// Create new parameters for the timing model
        /// </summary>
        public void CreateNewParameters()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            
            var result = NativeMethods.ARME_CreateNewParameters(_handle, _numberOfPlayers);
            CheckResult(result);
        }

        /// <summary>
        /// Register a new onset for a player
        /// BEGINNER NOTE: An "onset" is when a musical note or event starts
        /// </summary>
        /// <param name="playerIndex">Player index (0-based)</param>
        /// <param name="onsetTime">Time when the onset occurred</param>
        public void RegisterOnset(int playerIndex, float onsetTime)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            if (playerIndex < 0 || playerIndex >= _numberOfPlayers)
                throw new ArgumentOutOfRangeException(nameof(playerIndex));
            
            var result = NativeMethods.ARME_RegisterOnset(_handle, playerIndex, onsetTime);
            CheckResult(result);
        }

        /// <summary>
        /// Register a new onset with specific onset number
        /// </summary>
        /// <param name="playerIndex">Player index (0-based)</param>
        /// <param name="onsetTime">Time when the onset occurred</param>
        /// <param name="onsetNumber">Specific onset number in the score</param>
        public void RegisterOnsetWithIndex(int playerIndex, float onsetTime, int onsetNumber)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            if (playerIndex < 0 || playerIndex >= _numberOfPlayers)
                throw new ArgumentOutOfRangeException(nameof(playerIndex));
            
            var result = NativeMethods.ARME_RegisterOnsetWithIndex(_handle, playerIndex, onsetTime, onsetNumber);
            CheckResult(result);
        }

        /// <summary>
        /// Predict when the next onset will happen for a specific player
        /// </summary>
        /// <param name="playerIndex">Player index (0-based)</param>
        /// <returns>Predicted next onset time</returns>
        public float PredictNextOnset(int playerIndex)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            if (playerIndex < 0 || playerIndex >= _numberOfPlayers)
                throw new ArgumentOutOfRangeException(nameof(playerIndex));
            
            var result = NativeMethods.ARME_PredictNextOnset(_handle, playerIndex, out float nextOnset);
            CheckResult(result);
            return nextOnset;
        }

        /// <summary>
        /// Predict next onsets for all players
        /// </summary>
        /// <returns>Array of predicted onset times for all players</returns>
        public float[] PredictNextOnsets()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            
            float[] nextOnsets = new float[_numberOfPlayers];
            var result = NativeMethods.ARME_PredictNextOnsets(_handle, nextOnsets, _numberOfPlayers);
            CheckResult(result);
            return nextOnsets;
        }

        /// <summary>
        /// Get the most recent onset time for a player
        /// </summary>
        /// <param name="playerIndex">Player index (0-based)</param>
        /// <returns>Latest onset time</returns>
        public float GetLatestOnset(int playerIndex)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            if (playerIndex < 0 || playerIndex >= _numberOfPlayers)
                throw new ArgumentOutOfRangeException(nameof(playerIndex));
            
            var result = NativeMethods.ARME_GetLatestOnset(_handle, playerIndex, out float onsetTime);
            CheckResult(result);
            return onsetTime;
        }

        /// <summary>
        /// Get onset time for a specific note index
        /// </summary>
        /// <param name="playerIndex">Player index (0-based)</param>
        /// <param name="noteNumber">Note number in the sequence</param>
        /// <returns>Onset time for the specified note</returns>
        public float GetOnsetForNoteIndex(int playerIndex, int noteNumber)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            if (playerIndex < 0 || playerIndex >= _numberOfPlayers)
                throw new ArgumentOutOfRangeException(nameof(playerIndex));
            
            var result = NativeMethods.ARME_GetOnsetForNoteIndex(_handle, playerIndex, noteNumber, out float onsetTime);
            CheckResult(result);
            return onsetTime;
        }

        /// <summary>
        /// Get how many onsets have been registered for a specific player
        /// </summary>
        /// <param name="playerIndex">Player index (0-based)</param>
        /// <returns>Number of registered onsets</returns>
        public int GetNumberOfOnsets(int playerIndex)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            if (playerIndex < 0 || playerIndex >= _numberOfPlayers)
                throw new ArgumentOutOfRangeException(nameof(playerIndex));
            
            var result = NativeMethods.ARME_GetNumberOfOnsetsRegisteredForPlayer(_handle, playerIndex, out int numberOfOnsets);
            CheckResult(result);
            return numberOfOnsets;
        }

        /// <summary>
        /// Get the total number of notes registered by all players
        /// </summary>
        /// <returns>Total number of registered notes</returns>
        public int GetTotalNumberOfNotes()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            
            var result = NativeMethods.ARME_GetNumberOfNotesRegisteredByAllPlayers(_handle, out int numberOfNotes);
            CheckResult(result);
            return numberOfNotes;
        }

        /// <summary>
        /// Get the number of next onsets calculated
        /// </summary>
        /// <returns>Number of calculated predictions</returns>
        public int GetNumberOfCalculatedPredictions()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimpleTimingModel));
            
            var result = NativeMethods.ARME_GetNumberOfNextOnsetsCalculated(_handle, out int numberOfCalculated);
            CheckResult(result);
            return numberOfCalculated;
        }

        /// <summary>
        /// Properties for easy access to configuration
        /// </summary>
        public int NumberOfPlayers => _numberOfPlayers;

        /// <summary>
        /// Get the library version (useful for debugging)
        /// </summary>
        public static string GetVersion()
        {
            IntPtr versionPtr = NativeMethods.ARME_GetVersion();
            return Marshal.PtrToStringAnsi(versionPtr) ?? "Unknown version";
        }


    }
}