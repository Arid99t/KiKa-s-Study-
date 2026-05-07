using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using NaughtyAttributes;

/// <summary>
/// Attach to any GameObject. Subscribes to ARMEUserStudyController and
/// ARMETapDetection events, accumulates every timed event in memory, then
/// writes a two-section CSV (metadata + event log) to
/// Desktop/User Study Data/ on quit or via the inspector button.
/// </summary>
public class ARMEUserStudyDataLogger : MonoBehaviour
{
    [BoxGroup("Session Info")]
    [Tooltip("Participant identifier used in the file name and CSV header.")]
    [SerializeField] private string participantID = "P01";

    [BoxGroup("Session Info")]
    [Tooltip("Optional free-text notes written into the CSV header.")]
    [ResizableTextArea]
    [SerializeField] private string sessionNotes = "";

    [BoxGroup("Save Settings")]
    [Tooltip("Automatically save when the application quits or the scene unloads.")]
    [SerializeField] private bool saveOnApplicationQuit = true;

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting]
    [SerializeField] private int eventsLogged;

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting]
    [SerializeField] private string lastSavedPath = "";

    // ── Internal state ───────────────────────────────────────────────────

    private ARMEUserStudyController _controller;
    private ARMETapDetection        _tapDetection;

    private readonly List<string[]> _rows = new List<string[]>(512);
    private DateTime _sessionStartWall;
    private float    _sessionStartGame;

    private static readonly string[] Columns =
    {
        "EventIndex", "GameTime_s", "WallClockTime",
        "EventType", "PlayerID", "Count",
        "IOI_s", "BPM", "TapForce",
        "AdaptiveMode", "EnsembleMode", "NextBlink_s", "BlinkType", "Notes"
    };

    // ── Lifecycle ────────────────────────────────────────────────────────

    void Awake()
    {
        _sessionStartWall = DateTime.Now;
        _sessionStartGame = Time.time;
    }

    void Start()
    {
        _controller   = FindObjectOfType<ARMEUserStudyController>();
        _tapDetection = FindObjectOfType<ARMETapDetection>();

        if (_controller == null)
            Debug.LogWarning("[DataLogger] ARMEUserStudyController not found in scene.");
        else
        {
            _controller.OnUserTap      += HandleUserTap;
            _controller.OnVirtualBlink += HandleVirtualBlink;
            _controller.OnModeChange   += HandleModeChange;
        }

        if (_tapDetection != null)
            _tapDetection.OnHardwareTap += HandleHardwareTap;
        else
            Debug.Log("[DataLogger] No ARMETapDetection found — hardware taps will not be logged.");

        AppendRow(Time.time, "SESSION_START", -1, 0, 0f, 0f, 0,
            false, _controller != null ? _controller.currentMode : EnsembleMode.Adaptive,
            -1f, "", "Logging started");

        Debug.Log($"[DataLogger] Session begun. ParticipantID={participantID}  File will go to: Desktop/User Study Data/");
    }

    void OnDestroy()
    {
        if (_controller != null)
        {
            _controller.OnUserTap      -= HandleUserTap;
            _controller.OnVirtualBlink -= HandleVirtualBlink;
            _controller.OnModeChange   -= HandleModeChange;
        }
        if (_tapDetection != null)
            _tapDetection.OnHardwareTap -= HandleHardwareTap;
    }

    void OnApplicationQuit()
    {
        if (saveOnApplicationQuit)
            SaveData();
    }

    // ── Event handlers ───────────────────────────────────────────────────

    private void HandleUserTap(UserStudyTapEvent e)
    {
        AppendRow(e.gameTime, "USER_TAP", 0, e.tapNumber,
            e.interval, e.bpm, 0,
            e.adaptiveMode, e.mode,
            -1f, "", $"measuredIOI={e.measuredIOI:F4}s");
    }

    private void HandleVirtualBlink(UserStudyBlinkEvent e)
    {
        float bpm = e.currentIOI > 0f ? 60f / e.currentIOI : 0f;
        AppendRow(e.gameTime, "VIRTUAL_BLINK", e.playerIndex, e.onsetCount,
            e.currentIOI, bpm, 0,
            true, _controller != null ? _controller.currentMode : EnsembleMode.Adaptive,
            e.nextBlinkTime, e.blinkType, "");
    }

    private void HandleModeChange(UserStudyModeEvent e)
    {
        AppendRow(e.gameTime, "MODE_CHANGE", -1, 0,
            0f, 0f, 0,
            e.adaptiveMode, e.ensembleMode,
            -1f, "", e.description);
    }

    private void HandleHardwareTap(HardwareTapEvent e)
    {
        AppendRow(e.gameTime, "HARDWARE_TAP", 0, e.tapCount,
            0f, 0f, e.force,
            false, _controller != null ? _controller.currentMode : EnsembleMode.Adaptive,
            -1f, "", $"Force={e.force}");
    }

    // ── Row builder ──────────────────────────────────────────────────────

    private void AppendRow(float eventGameTime, string eventType, int playerID, int count,
        float ioi, float bpm, int force, bool adaptive,
        EnsembleMode ensMode, float nextBlink, string blinkType, string notes)
    {
        float relTime = eventGameTime - _sessionStartGame;

        _rows.Add(new[]
        {
            (_rows.Count + 1).ToString(),
            relTime.ToString("F3"),
            DateTime.Now.ToString("HH:mm:ss.fff"),
            eventType,
            playerID.ToString(),
            count.ToString(),
            ioi  > 0f  ? ioi.ToString("F4")  : "",
            bpm  > 0f  ? bpm.ToString("F2")  : "",
            force > 0  ? force.ToString()     : "",
            adaptive.ToString(),
            ensMode.ToString(),
            nextBlink >= 0f ? nextBlink.ToString("F3") : "",
            blinkType,
            CsvEscape(notes)
        });

        eventsLogged = _rows.Count;
    }

    // ── Save ─────────────────────────────────────────────────────────────

    [Button("Save Data Now")]
    public void SaveData()
    {
        // Add a save-marker row (not persistent — written fresh each call so
        // multiple mid-session saves each get an accurate timestamp).
        float relEnd = Time.time - _sessionStartGame;
        string endRow = string.Join(",", new[]
        {
            (_rows.Count + 1).ToString(),
            relEnd.ToString("F3"),
            DateTime.Now.ToString("HH:mm:ss.fff"),
            "SESSION_SAVE",
            "-1", "0", "", "", "", "False",
            (_controller != null ? _controller.currentMode : EnsembleMode.Adaptive).ToString(),
            "", "",
            CsvEscape($"TotalUserTaps={(_controller != null ? _controller.userTapCount : 0)}" +
                      $"  Duration={relEnd:F1}s  TotalEvents={_rows.Count + 1}")
        });

        // Resolve output path.
        string desktop  = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string folder   = Path.Combine(desktop, "User Study Data");
        Directory.CreateDirectory(folder);

        EnsembleMode startMode = _controller != null
            ? _controller.GetSessionConfig().startMode
            : EnsembleMode.Adaptive;
        string fileName = $"{participantID}_{startMode}_{_sessionStartWall:yyyyMMdd_HHmmss}.csv";
        string filePath = Path.Combine(folder, fileName);

        var sb = new StringBuilder(8192);

        // ── Section 1: Session Metadata ───────────────────────────────
        sb.AppendLine("## SESSION INFO");
        sb.AppendLine("Parameter,Value");
        sb.AppendLine($"ParticipantID,{participantID}");
        sb.AppendLine($"SessionDate,{_sessionStartWall:yyyy-MM-dd}");
        sb.AppendLine($"SessionStartTime,{_sessionStartWall:HH:mm:ss}");
        sb.AppendLine($"SessionDuration_s,{relEnd:F1}");

        if (_controller != null)
        {
            var cfg = _controller.GetSessionConfig();
            sb.AppendLine($"EnsembleMode,{cfg.startMode}");
            sb.AppendLine($"NumVirtualPlayers,{cfg.numVirtualPlayers}");
            sb.AppendLine($"Alpha,{cfg.alpha:F2}");
            sb.AppendLine($"Beta,{cfg.beta:F2}");
            sb.AppendLine($"UserIoiSmoothing,{cfg.userIoiSmoothing:F2}");
            sb.AppendLine($"FixedBPM,{cfg.fixedBPM:F1}");
            sb.AppendLine($"AgenticIoiVariation,{cfg.agenticIoiVariation:F3}");
            sb.AppendLine($"AgenticTimingNoise,{cfg.agenticTimingNoise:F3}");
            sb.AppendLine($"AgenticBaselinePull,{cfg.agenticBaselinePull:F2}");
            sb.AppendLine($"TotalUserTaps,{_controller.userTapCount}");
            sb.AppendLine($"FinalUserBPM,{_controller.currentUserBPM:F1}");
            sb.AppendLine($"FinalMeasuredIOI_s,{_controller.measuredUserIOI:F4}");
            sb.AppendLine($"FinalAdaptiveMode,{_controller.adaptiveMode}");
            sb.AppendLine($"FinalEnsembleMode,{_controller.currentMode}");
        }

        sb.AppendLine($"TotalEventsLogged,{_rows.Count + 1}");
        sb.AppendLine($"SessionNotes,{CsvEscape(sessionNotes)}");

        sb.AppendLine();

        // ── Section 2: Event Log ──────────────────────────────────────
        sb.AppendLine("## EVENT LOG");
        sb.AppendLine(string.Join(",", Columns));

        foreach (var row in _rows)
            sb.AppendLine(string.Join(",", row));

        sb.AppendLine(endRow);   // session-save marker as final row

        // Write UTF-8 without BOM so Excel and Python both read it cleanly.
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));

        lastSavedPath = filePath;
        Debug.Log($"[DataLogger] Saved {_rows.Count + 1} events → {filePath}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string CsvEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
