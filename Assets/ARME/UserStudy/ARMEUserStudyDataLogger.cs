using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using NaughtyAttributes;

/// <summary>
/// Data manager for the 2 (Modality) × 3 (Interactivity) SMS study, run as experimenter-picked
/// condition blocks of several repetitions (trials) each. Subscribes to the
/// <see cref="ARMEUserStudySession"/> (trial / ratings events) and the
/// <see cref="ARMEUserStudyController"/> (tap / musician-note / playback events), accumulates each
/// trial's taps and the ensemble's actually-played note onsets, then — once a take ends — computes
/// the behavioural measures (asynchrony = tap − nearest VIOLIN 1 note onset, since participants tap
/// one tap per Violin 1 note; its mean and SD, IOI variability, and an approximate phase-correction
/// index). Practice trials are discarded entirely — nothing from them is written to any file.
///
/// Output (Desktop/User Study Data/&lt;ParticipantID&gt;/), tidy for repeated-measures ANOVA:
///   • &lt;PID&gt;_taps.csv            — one row per tap (raw + asynchrony vs Violin 1, condition-tagged)
///   • &lt;PID&gt;_summary.csv         — one row per trial (derived metrics + the block's five ratings)
///   • &lt;PID&gt;_session.csv         — demographics + blocks run + engine-config snapshot
///   • &lt;PID&gt;_musician_onsets.csv — when each virtual musician actually played each note (the
///                                  ensemble's intended musical timing, for offline analysis)
///   • &lt;PID&gt;_onsets.csv          — (optional) Violin 1 (P0) played-note onsets plus every
///                                  follower model blink onset, kept for reference
/// </summary>
public class ARMEUserStudyDataLogger : MonoBehaviour
{
    [BoxGroup("Session Info")]
    [Tooltip("Fallback participant id, used only if no ARMEUserStudySession demographics are present.")]
    [SerializeField] private string participantID = "P01";

    [BoxGroup("Session Info")]
    [Tooltip("Optional free-text notes written into the session CSV.")]
    [ResizableTextArea]
    [SerializeField] private string sessionNotes = "";

    [BoxGroup("Analysis")]
    [Tooltip("A tap counts as a valid synchronisation tap if its asynchrony is within this fraction " +
             "of the trial's median inter-onset interval. Filters count-in taps and gross mis-taps " +
             "out of the summary statistics (raw values are still written to the taps file).")]
    [SerializeField, Range(0.1f, 1f)] private float asyncValidFraction = 0.5f;

    [BoxGroup("Save Settings")]
    [Tooltip("Automatically save when the session completes.")]
    [SerializeField] private bool saveOnSessionComplete = true;

    [BoxGroup("Save Settings")]
    [Tooltip("Also save when the application quits / scene unloads (safety net for partial sessions).")]
    [SerializeField] private bool saveOnApplicationQuit = true;

    [BoxGroup("Save Settings")]
    [Tooltip("Write the musician-onsets file: the game time at which each virtual musician actually " +
             "played each note (the ensemble's intended musical timing).")]
    [SerializeField] private bool writeMusicianOnsetFile = true;

    [BoxGroup("Save Settings")]
    [Tooltip("Also write the combined onset file: Violin 1's played-note onsets as Player 0, " +
             "plus the followers' abstract model blink onsets.")]
    [SerializeField] private bool writeOnsetFile = true;

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting] [SerializeField] private int trialsRecorded;

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting] [SerializeField] private int tapsLogged;

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting] [SerializeField] private string lastSavedFolder = "";

    // ── Per-tap / per-trial records ──────────────────────────────────────

    private class TapRecord
    {
        public int   indexInTrial;
        public float gameTime;
        public float ioi;          // seconds
        public float bpm;
        public bool  duringTake;
        public bool  isHardware;
        public int   force;
        // Filled in the end-of-trial post-pass.
        public float asyncMs;
        public int   nearestPlayer = -1;
        public int   nearestNote = -1;   // Violin 1 note number the tap was matched to (1-based)
        public bool  asyncValid;
    }

    private struct OnsetRecord
    {
        public int   player;
        public float gameTime;
        public bool  duringTake;
    }

    /// <summary>One actually-played musician note (warped audio playhead crossed a source onset).</summary>
    private struct NoteRecord
    {
        public int    partIndex;
        public string part;
        public int    noteIndex;    // 1-based within the take
        public float  sourceTime;   // onset timestamp in the source recording
        public float  gameTime;     // when the note actually sounded
        public float  speed;        // playback speed at that moment
        public bool   isLeader;     // Violin 1 — the participant's tapping target
    }

    private class TrialRecord
    {
        public TrialInfo info;
        public float startTime;
        public float endTime;
        public float playbackStart = -1f;
        public bool  finalized;
        public readonly List<TapRecord>   taps   = new List<TapRecord>(128);
        public readonly List<OnsetRecord> onsets = new List<OnsetRecord>(256);
        public readonly List<NoteRecord>  notes  = new List<NoteRecord>(512);

        // Summary metrics (computed in FinalizeTrial).
        public int   numValidTaps;
        public int   numLeaderNotes;
        public float meanAsyncMs, sdAsyncMs;
        public float meanIOI, sdIOI, cvIOI;
        public float lag1Slope, alphaEst;
    }

    private readonly List<TrialRecord> _trials = new List<TrialRecord>(32);
    private readonly Dictionary<int, RatingsData> _ratings = new Dictionary<int, RatingsData>();
    private TrialRecord _active;

    private ARMEUserStudyController _controller;
    private ARMEUserStudySession   _session;
    private ARMETapDetection       _tapDetection;

    private DateTime _sessionStartWall;
    private float    _lastHardwareTapTime = -10f;
    private int      _lastHardwareForce;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string OutputFolderName = "User Study Data";

    // ── Lifecycle ────────────────────────────────────────────────────────

    void Awake()
    {
        _sessionStartWall = DateTime.Now;
    }

    void Start()
    {
        _controller   = FindObjectOfType<ARMEUserStudyController>();
        _session      = FindObjectOfType<ARMEUserStudySession>();
        _tapDetection = FindObjectOfType<ARMETapDetection>();

        if (_controller != null)
        {
            _controller.OnUserTap          += HandleUserTap;
            _controller.OnVirtualBlink     += HandleVirtualBlink;
            _controller.OnMusicianNote     += HandleMusicianNote;
            _controller.OnPlaybackStarted  += HandlePlaybackStarted;
        }
        else Debug.LogWarning("[DataLogger] ARMEUserStudyController not found in scene.");

        if (_session != null)
        {
            _session.OnTrialStarted     += HandleTrialStarted;
            _session.OnTrialEnded       += HandleTrialEnded;
            _session.OnRatingsSubmitted += HandleRatingsSubmitted;
            _session.OnSessionComplete  += HandleSessionComplete;
        }
        else Debug.LogWarning("[DataLogger] ARMEUserStudySession not found — condition tagging/ratings will be unavailable.");

        if (_tapDetection != null)
            _tapDetection.OnHardwareTap += HandleHardwareTap;

        // Create the base output folder on the Desktop up-front so it exists before the first save.
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string baseFolder = Path.Combine(desktop, OutputFolderName);
            Directory.CreateDirectory(baseFolder);
            Debug.Log($"[DataLogger] Ready. Output folder: {baseFolder}/<ParticipantID>/");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[DataLogger] Could not create output folder: {ex.Message}");
        }
    }

    void OnDestroy()
    {
        if (_controller != null)
        {
            _controller.OnUserTap         -= HandleUserTap;
            _controller.OnVirtualBlink    -= HandleVirtualBlink;
            _controller.OnMusicianNote    -= HandleMusicianNote;
            _controller.OnPlaybackStarted -= HandlePlaybackStarted;
        }
        if (_session != null)
        {
            _session.OnTrialStarted     -= HandleTrialStarted;
            _session.OnTrialEnded       -= HandleTrialEnded;
            _session.OnRatingsSubmitted -= HandleRatingsSubmitted;
            _session.OnSessionComplete  -= HandleSessionComplete;
        }
        if (_tapDetection != null)
            _tapDetection.OnHardwareTap -= HandleHardwareTap;
    }

    void OnApplicationQuit()
    {
        if (saveOnApplicationQuit) SaveData();
    }

    // ── Event handlers ───────────────────────────────────────────────────

    private void HandleTrialStarted(TrialInfo info)
    {
        // Practice trials are familiarisation only — record nothing (every handler below
        // no-ops while _active is null, so no practice data can reach the CSVs).
        if (info.isPractice)
        {
            _active = null;
            return;
        }

        _active = new TrialRecord { info = info, startTime = Time.time };
        _trials.Add(_active);
        trialsRecorded = _trials.Count;
    }

    private void HandlePlaybackStarted()
    {
        if (_active != null && _active.playbackStart < 0f)
            _active.playbackStart = Time.time;
    }

    private void HandleUserTap(UserStudyTapEvent e)
    {
        if (_active == null) return;

        bool hw = Mathf.Abs(e.gameTime - _lastHardwareTapTime) < 0.05f;
        _active.taps.Add(new TapRecord
        {
            indexInTrial = _active.taps.Count + 1,
            gameTime   = e.gameTime,
            ioi        = e.interval,
            bpm        = e.bpm,
            duringTake = _active.playbackStart >= 0f && e.gameTime >= _active.playbackStart,
            isHardware = hw,
            force      = hw ? _lastHardwareForce : 0,
        });
        tapsLogged++;
    }

    private void HandleVirtualBlink(UserStudyBlinkEvent e)
    {
        if (_active == null) return;
        _active.onsets.Add(new OnsetRecord
        {
            player     = e.playerIndex,
            gameTime   = e.gameTime,
            duringTake = _active.playbackStart >= 0f && e.gameTime >= _active.playbackStart,
        });
    }

    private void HandleMusicianNote(UserStudyNoteEvent e)
    {
        if (_active == null) return;   // notes only fire during a take, so no duringTake flag needed
        _active.notes.Add(new NoteRecord
        {
            partIndex  = e.partIndex,
            part       = e.partLabel,
            noteIndex  = e.noteIndex,
            sourceTime = e.sourceTime,
            gameTime   = e.gameTime,
            speed      = e.playbackSpeed,
            isLeader   = e.isLeader,
        });
    }

    private void HandleTrialEnded(TrialInfo info)
    {
        if (_active != null) { _active.endTime = Time.time; FinalizeTrial(_active); }
    }

    private void HandleRatingsSubmitted(int conditionId, RatingsData ratings)
    {
        _ratings[conditionId] = ratings;   // one ratings set per condition block
    }

    private void HandleHardwareTap(HardwareTapEvent e)
    {
        _lastHardwareTapTime = e.gameTime;
        _lastHardwareForce   = e.force;
    }

    private void HandleSessionComplete()
    {
        if (saveOnSessionComplete) SaveData();
    }

    // ── Behavioural measures (end-of-trial post-pass) ────────────────────

    /// <summary>
    /// Compute each tap's asynchrony against its nearest VIOLIN 1 note onset (participants tap one
    /// tap per Violin 1 note, so the leader's actually-played notes are the reference), then the
    /// trial-level mean/SD asynchrony, IOI variability, and an approximate lag-1 phase-correction
    /// index. Falls back to the abstract model blinks if no leader notes were recorded (e.g. a
    /// missing onset file). Matching uses only the during-take window so count-in taps don't count.
    /// </summary>
    private void FinalizeTrial(TrialRecord c)
    {
        c.finalized = true;

        // Reference onsets = Violin 1's played notes.
        var onsetTimes = new List<float>();
        var onsetPlayers = new List<int>();
        var onsetNotes = new List<int>();
        foreach (var n in c.notes)
        {
            if (!n.isLeader) continue;
            onsetTimes.Add(n.gameTime);
            onsetPlayers.Add(n.partIndex);
            onsetNotes.Add(n.noteIndex);
        }
        c.numLeaderNotes = onsetTimes.Count;

        // Fallback: model blink onsets (prefer during-take; all if no take was detected).
        if (onsetTimes.Count == 0)
        {
            bool anyTakeOnsets = false;
            foreach (var o in c.onsets) if (o.duringTake) anyTakeOnsets = true;
            foreach (var o in c.onsets)
            {
                if (anyTakeOnsets && !o.duringTake) continue;
                onsetTimes.Add(o.gameTime);
                onsetPlayers.Add(o.player);
                onsetNotes.Add(-1);
            }
        }

        // Median IOI → validity window for asynchrony.
        var iois = new List<float>();
        foreach (var t in c.taps) if (t.duringTake && t.ioi > 0f) iois.Add(t.ioi);
        float medianIOI = Median(iois);
        float validWindowMs = (medianIOI > 0f ? medianIOI : 0.5f) * asyncValidFraction * 1000f;

        // Per-tap nearest-onset asynchrony.
        var validAsync = new List<float>();
        foreach (var t in c.taps)
        {
            int nearest = NearestOnset(onsetTimes, t.gameTime, out float bestDt);
            if (nearest >= 0)
            {
                t.asyncMs = bestDt * 1000f;            // signed: tap after onset = positive
                t.nearestPlayer = onsetPlayers[nearest];
                t.nearestNote = onsetNotes[nearest];
                t.asyncValid = t.duringTake && Mathf.Abs(t.asyncMs) <= validWindowMs;
                if (t.asyncValid) validAsync.Add(t.asyncMs);
            }
            else
            {
                t.asyncMs = float.NaN;
                t.nearestPlayer = -1;
                t.nearestNote = -1;
                t.asyncValid = false;
            }
        }

        c.numValidTaps = validAsync.Count;
        c.meanAsyncMs  = Mean(validAsync);
        c.sdAsyncMs    = StdDev(validAsync, c.meanAsyncMs);

        c.meanIOI = Mean(iois);
        c.sdIOI   = StdDev(iois, c.meanIOI);
        c.cvIOI   = c.meanIOI > 1e-6f ? c.sdIOI / c.meanIOI : 0f;

        // Approximate phase correction: lag-1 regression of async[n+1] on async[n].
        // Under async[n+1] = (1-α)·async[n] + noise, slope ≈ 1-α, so α ≈ 1 - slope.
        c.lag1Slope = Lag1Slope(validAsync);
        c.alphaEst  = float.IsNaN(c.lag1Slope) ? float.NaN : 1f - c.lag1Slope;
    }

    private static int NearestOnset(List<float> onsets, float t, out float signedDt)
    {
        signedDt = 0f;
        int best = -1;
        float bestAbs = float.PositiveInfinity;
        for (int i = 0; i < onsets.Count; i++)
        {
            float dt = t - onsets[i];
            float a = Mathf.Abs(dt);
            if (a < bestAbs) { bestAbs = a; best = i; signedDt = dt; }
        }
        return best;
    }

    // ── Save ─────────────────────────────────────────────────────────────

    [Button("Save Data Now")]
    public void SaveData()
    {
        if (_trials.Count == 0)
        {
            Debug.LogWarning("[DataLogger] Nothing to save yet (no trials recorded).");
            return;
        }

        // Finalize any trial that wasn't closed by an OnTrialEnded (partial session).
        foreach (var c in _trials)
            if (!c.finalized) FinalizeTrial(c);

        string pid = ResolveParticipantId();
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string folder  = Path.Combine(desktop, OutputFolderName, Sanitize(pid));
        Directory.CreateDirectory(folder);

        WriteTapsFile(Path.Combine(folder, $"{Sanitize(pid)}_taps.csv"), pid);
        WriteSummaryFile(Path.Combine(folder, $"{Sanitize(pid)}_summary.csv"), pid);
        WriteSessionFile(Path.Combine(folder, $"{Sanitize(pid)}_session.csv"), pid);
        if (writeMusicianOnsetFile)
            WriteMusicianOnsetFile(Path.Combine(folder, $"{Sanitize(pid)}_musician_onsets.csv"), pid);
        if (writeOnsetFile)
            WriteOnsetFile(Path.Combine(folder, $"{Sanitize(pid)}_onsets.csv"), pid);

        lastSavedFolder = folder;
        Debug.Log($"[DataLogger] Saved {_trials.Count} trial(s), {tapsLogged} tap(s) → {folder}");
    }

    private RatingsData RatingsFor(int conditionId)
        => _ratings.TryGetValue(conditionId, out var r) ? r : RatingsData.Empty;

    private void WriteTapsFile(string path, string pid)
    {
        var sb = new StringBuilder(16384);
        sb.AppendLine("ParticipantID,ConditionId,BlockOrder,Modality,Interactivity,Rep,CountInSpeed_s," +
                      "TapIndexInTrial,GameTime_s,TimeInTake_s,DuringTake,IOI_s,BPM," +
                      "Asynchrony_ms,NearestPlayer,NearestVN1Note,AsyncValid,TapSource,Force");

        foreach (var c in _trials)
        {
            var info = c.info; var cond = info.condition;
            foreach (var t in c.taps)
            {
                float tInTake = c.playbackStart >= 0f ? t.gameTime - c.playbackStart : float.NaN;
                sb.Append(CsvEscape(pid)).Append(',')
                  .Append(cond.conditionId).Append(',')
                  .Append(cond.index + 1).Append(',')
                  .Append(cond.modality).Append(',')
                  .Append(cond.interactivity).Append(',')
                  .Append(info.repIndex).Append(',')
                  .Append(F(info.countInSpeed, 2)).Append(',')
                  .Append(t.indexInTrial).Append(',')
                  .Append(F(t.gameTime, 4)).Append(',')
                  .Append(F(tInTake, 4)).Append(',')
                  .Append(t.duringTake ? 1 : 0).Append(',')
                  .Append(t.ioi > 0f ? F(t.ioi, 4) : "").Append(',')
                  .Append(t.bpm > 0f ? F(t.bpm, 2) : "").Append(',')
                  .Append(F(t.asyncMs, 2)).Append(',')
                  .Append(t.nearestPlayer).Append(',')
                  .Append(t.nearestNote >= 0 ? t.nearestNote.ToString(Inv) : "").Append(',')
                  .Append(t.asyncValid ? 1 : 0).Append(',')
                  .Append(t.isHardware ? "FSR" : "Mouse").Append(',')
                  .Append(t.force)
                  .Append('\n');
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private void WriteSummaryFile(string path, string pid)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("ParticipantID,ConditionId,BlockOrder,Modality,Interactivity,Rep,CountInSpeed_s," +
                      "TakeDuration_s,NumTapsTotal,NumValidTaps,NumVN1Notes," +
                      "MeanAsync_ms,SDAsync_ms,MeanIOI_s,SDIOI_s,CV_IOI," +
                      "PhaseCorr_Lag1Slope,PhaseCorr_AlphaEst," +
                      "VAS_PerceivedSync,VAS_Ease,VAS_Realism,VAS_Engagement,VAS_Agency");

        foreach (var c in _trials)
        {
            var info = c.info; var cond = info.condition;
            var r = RatingsFor(cond.conditionId);
            float dur = c.playbackStart >= 0f && c.endTime > c.playbackStart
                ? c.endTime - c.playbackStart : float.NaN;
            sb.Append(CsvEscape(pid)).Append(',')
              .Append(cond.conditionId).Append(',')
              .Append(cond.index + 1).Append(',')
              .Append(cond.modality).Append(',')
              .Append(cond.interactivity).Append(',')
              .Append(info.repIndex).Append(',')
              .Append(F(info.countInSpeed, 2)).Append(',')
              .Append(F(dur, 2)).Append(',')
              .Append(c.taps.Count).Append(',')
              .Append(c.numValidTaps).Append(',')
              .Append(c.numLeaderNotes).Append(',')
              .Append(F(c.meanAsyncMs, 2)).Append(',')
              .Append(F(c.sdAsyncMs, 2)).Append(',')
              .Append(F(c.meanIOI, 4)).Append(',')
              .Append(F(c.sdIOI, 4)).Append(',')
              .Append(F(c.cvIOI, 4)).Append(',')
              .Append(F(c.lag1Slope, 4)).Append(',')
              .Append(F(c.alphaEst, 4)).Append(',')
              .Append(R(r.perceivedSynchrony)).Append(',')
              .Append(R(r.easeOfCoordination)).Append(',')
              .Append(R(r.realism)).Append(',')
              .Append(R(r.engagement)).Append(',')
              .Append(R(r.senseOfAgency))
              .Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private void WriteSessionFile(string path, string pid)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("Parameter,Value");
        sb.AppendLine($"ParticipantID,{CsvEscape(pid)}");

        if (_session != null)
        {
            var p = _session.Participant;
            sb.AppendLine($"ParticipantNumber,{_session.ParticipantNumber}");
            sb.AppendLine($"Age,{p.age}");
            sb.AppendLine($"Gender,{CsvEscape(p.gender)}");
            sb.AppendLine($"MusicalTrainingYears,{F(p.musicalTrainingYears, 1)}");
            sb.AppendLine($"RepsPerCondition,{_session.RepsPerCondition}");
            sb.AppendLine($"PracticeTrials,{_session.PracticeTrials} (not recorded)");
        }

        sb.AppendLine($"BlocksRun,{CsvEscape(BlocksRunString())}");
        sb.AppendLine($"CountInSpeedsUsed,{CsvEscape(SpeedsUsedString())}");
        sb.AppendLine($"SessionDate,{_sessionStartWall:yyyy-MM-dd}");
        sb.AppendLine($"SessionStartTime,{_sessionStartWall:HH:mm:ss}");
        sb.AppendLine($"SessionDuration_s,{F(Time.time, 1)}");
        sb.AppendLine($"TrialsRecorded,{_trials.Count}");
        sb.AppendLine($"AsyncValidFraction,{F(asyncValidFraction, 2)}");

        if (_controller != null)
        {
            var cfg = _controller.GetSessionConfig();
            sb.AppendLine($"NumVirtualPlayers,{cfg.numVirtualPlayers}");
            sb.AppendLine($"Alpha,{F(cfg.alpha, 2)}");
            sb.AppendLine($"Beta,{F(cfg.beta, 2)}");
            sb.AppendLine($"UserIoiSmoothing,{F(cfg.userIoiSmoothing, 2)}");
            sb.AppendLine($"FixedBPM,{F(cfg.fixedBPM, 1)}");
            sb.AppendLine($"AgenticIoiVariation,{F(cfg.agenticIoiVariation, 3)}");
            sb.AppendLine($"AgenticTimingNoise,{F(cfg.agenticTimingNoise, 3)}");
            sb.AppendLine($"AgenticBaselinePull,{F(cfg.agenticBaselinePull, 2)}");
        }

        sb.AppendLine($"SessionNotes,{CsvEscape(sessionNotes)}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// One row per note a virtual musician actually played: the moment its (tempo-warped) audio
    /// crossed one of the recording's onset timestamps. IsLeader marks Violin 1 — the melody the
    /// participant taps along with — so taps can be compared with the ensemble's intended musical
    /// timing offline.
    /// </summary>
    private void WriteMusicianOnsetFile(string path, string pid)
    {
        var sb = new StringBuilder(32768);
        sb.AppendLine("ParticipantID,ConditionId,BlockOrder,Modality,Interactivity,Rep," +
                      "Part,PartIndex,IsLeader,NoteIndex,SourceTime_s,GameTime_s,TimeInTake_s,PlaybackSpeed");
        foreach (var c in _trials)
        {
            var info = c.info; var cond = info.condition;
            foreach (var n in c.notes)
            {
                float tInTake = c.playbackStart >= 0f ? n.gameTime - c.playbackStart : float.NaN;
                sb.Append(CsvEscape(pid)).Append(',')
                  .Append(cond.conditionId).Append(',')
                  .Append(cond.index + 1).Append(',')
                  .Append(cond.modality).Append(',')
                  .Append(cond.interactivity).Append(',')
                  .Append(info.repIndex).Append(',')
                  .Append(CsvEscape(n.part)).Append(',')
                  .Append(n.partIndex).Append(',')
                  .Append(n.isLeader ? 1 : 0).Append(',')
                  .Append(n.noteIndex).Append(',')
                  .Append(F(n.sourceTime, 4)).Append(',')
                  .Append(F(n.gameTime, 4)).Append(',')
                  .Append(F(tInTake, 4)).Append(',')
                  .Append(F(n.speed, 3))
                  .Append('\n');
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private void WriteOnsetFile(string path, string pid)
    {
        var sb = new StringBuilder(16384);
        sb.AppendLine("ParticipantID,ConditionId,BlockOrder,Modality,Interactivity,Rep," +
                      "Player,GameTime_s,TimeInTake_s,DuringTake");
        foreach (var c in _trials)
        {
            var info = c.info; var cond = info.condition;

            // Player 0 is the Violin 1 leader/reference. It does not generate the abstract
            // follower blinks stored in c.onsets, so merge its actually-played note onsets
            // into this legacy file to provide a complete P0..PN timing table.
            var rows = new List<OnsetRecord>(c.onsets.Count + c.notes.Count);
            rows.AddRange(c.onsets);
            foreach (var n in c.notes)
            {
                if (!n.isLeader) continue;
                rows.Add(new OnsetRecord
                {
                    player = 0,
                    gameTime = n.gameTime,
                    duringTake = c.playbackStart >= 0f && n.gameTime >= c.playbackStart,
                });
            }
            rows.Sort((a, b) => a.gameTime.CompareTo(b.gameTime));

            foreach (var o in rows)
            {
                float tInTake = c.playbackStart >= 0f ? o.gameTime - c.playbackStart : float.NaN;
                sb.Append(CsvEscape(pid)).Append(',')
                  .Append(cond.conditionId).Append(',')
                  .Append(cond.index + 1).Append(',')
                  .Append(cond.modality).Append(',')
                  .Append(cond.interactivity).Append(',')
                  .Append(info.repIndex).Append(',')
                  .Append(o.player).Append(',')
                  .Append(F(o.gameTime, 4)).Append(',')
                  .Append(F(tInTake, 4)).Append(',')
                  .Append(o.duringTake ? 1 : 0)
                  .Append('\n');
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Condition ids in the order the experimenter started their blocks (each once).</summary>
    private string BlocksRunString()
    {
        var seen = new HashSet<int>();
        var ids = new List<string>();
        foreach (var c in _trials)
            if (seen.Add(c.info.condition.conditionId))
                ids.Add(c.info.condition.conditionId.ToString(Inv));
        return string.Join("-", ids);
    }

    /// <summary>Distinct count-in speeds that actually appeared, ascending.</summary>
    private string SpeedsUsedString()
    {
        var set = new SortedSet<float>();
        foreach (var c in _trials) set.Add(c.info.countInSpeed);
        var parts = new List<string>();
        foreach (var s in set) parts.Add(F(s, 2));
        return string.Join("-", parts);
    }

    private string ResolveParticipantId()
    {
        if (_session != null && !string.IsNullOrEmpty(_session.Participant.id))
            return _session.Participant.id;
        return participantID;
    }

    // ── Math helpers ─────────────────────────────────────────────────────

    private static float Mean(List<float> xs)
    {
        if (xs.Count == 0) return float.NaN;
        float s = 0f; foreach (var x in xs) s += x;
        return s / xs.Count;
    }

    private static float StdDev(List<float> xs, float mean)
    {
        if (xs.Count < 2 || float.IsNaN(mean)) return float.NaN;
        float ss = 0f; foreach (var x in xs) { float d = x - mean; ss += d * d; }
        return Mathf.Sqrt(ss / (xs.Count - 1));   // sample SD
    }

    private static float Median(List<float> xs)
    {
        if (xs.Count == 0) return float.NaN;
        var s = new List<float>(xs); s.Sort();
        int n = s.Count;
        return (n % 2 == 1) ? s[n / 2] : 0.5f * (s[n / 2 - 1] + s[n / 2]);
    }

    /// <summary>Slope of the OLS regression of x[n+1] on x[n] (lag-1 autoregression).</summary>
    private static float Lag1Slope(List<float> x)
    {
        int n = x.Count - 1;
        if (n < 2) return float.NaN;
        float sx = 0f, sy = 0f;
        for (int i = 0; i < n; i++) { sx += x[i]; sy += x[i + 1]; }
        float mx = sx / n, my = sy / n;
        float cov = 0f, varx = 0f;
        for (int i = 0; i < n; i++)
        {
            float dx = x[i] - mx;
            cov += dx * (x[i + 1] - my);
            varx += dx * dx;
        }
        return varx > 1e-9f ? cov / varx : float.NaN;
    }

    // ── CSV helpers ──────────────────────────────────────────────────────

    /// <summary>Invariant-culture fixed-point; empty string for NaN so cells stay blank.</summary>
    private static string F(float v, int decimals)
        => float.IsNaN(v) || float.IsInfinity(v) ? "" : v.ToString("F" + decimals, Inv);

    /// <summary>VAS rating cell (0–100): blank when unrated (negative).</summary>
    private static string R(float rating) => rating < 0f ? "" : rating.ToString("F1", Inv);

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "P00";
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }

    private static string CsvEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
