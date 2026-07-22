using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Video;
using NaughtyAttributes;
using ARMETiming;
using ARMEPlayback;

/// <summary>Configuration snapshot passed to the data logger at session start.</summary>
public struct SessionConfig
{
    public int numVirtualPlayers;
    public EnsembleMode startMode;
    public Modality modality;
    public float alpha, beta, userIoiSmoothing;
    public float fixedBPM;
    public float agenticIoiVariation, agenticTimingNoise, agenticBaselinePull;
}

/// <summary>
/// Sensory modality factor of the 2×3 study design.
///   AudioOnly   — the participant only hears the ensemble (musician videos hidden).
///   AudioVisual — the participant hears AND sees the ensemble (musician videos shown).
/// </summary>
public enum Modality
{
    AudioOnly,
    AudioVisual
}

/// <summary>Payload fired when the user taps.</summary>
public readonly struct UserStudyTapEvent
{
    public readonly float gameTime;
    public readonly int tapNumber;
    public readonly float interval;
    public readonly float measuredIOI;
    public readonly float bpm;
    public readonly EnsembleMode mode;
    public readonly bool adaptiveMode;
    public UserStudyTapEvent(float gameTime, int tapNumber, float interval,
        float measuredIOI, float bpm, EnsembleMode mode, bool adaptiveMode)
    {
        this.gameTime = gameTime; this.tapNumber = tapNumber; this.interval = interval;
        this.measuredIOI = measuredIOI; this.bpm = bpm;
        this.mode = mode; this.adaptiveMode = adaptiveMode;
    }
}

/// <summary>Payload fired each time a virtual player blinks.</summary>
public readonly struct UserStudyBlinkEvent
{
    public readonly float gameTime;
    public readonly int playerIndex;
    public readonly int onsetCount;
    public readonly float currentIOI;
    public readonly float nextBlinkTime;
    public readonly string blinkType;
    public UserStudyBlinkEvent(float gameTime, int playerIndex, int onsetCount,
        float currentIOI, float nextBlinkTime, string blinkType)
    {
        this.gameTime = gameTime; this.playerIndex = playerIndex;
        this.onsetCount = onsetCount; this.currentIOI = currentIOI;
        this.nextBlinkTime = nextBlinkTime; this.blinkType = blinkType;
    }
}

/// <summary>
/// Payload fired when a virtual musician actually plays a note: the moment the warped audio
/// playhead crosses one of the recording's onset timestamps. This is the ensemble's REAL
/// musical timing (unlike the abstract model blinks), so participant taps can be compared
/// against it directly in analysis.
/// </summary>
public readonly struct UserStudyNoteEvent
{
    public readonly float gameTime;      // when the note sounded (Time.time seconds)
    public readonly int partIndex;       // index into the controller's video parts
    public readonly string partLabel;    // e.g. "VN1_RC_Var00#02"
    public readonly int noteIndex;       // 1-based note number within the take
    public readonly float sourceTime;    // the onset's timestamp in the source recording
    public readonly float playbackSpeed; // speed the part was playing at when the note sounded
    public readonly bool isLeader;       // true for the Violin 1 (leader) part
    public UserStudyNoteEvent(float gameTime, int partIndex, string partLabel,
        int noteIndex, float sourceTime, float playbackSpeed, bool isLeader)
    {
        this.gameTime = gameTime; this.partIndex = partIndex; this.partLabel = partLabel;
        this.noteIndex = noteIndex; this.sourceTime = sourceTime;
        this.playbackSpeed = playbackSpeed; this.isLeader = isLeader;
    }
}

/// <summary>Payload fired when the ensemble mode or adaptive sub-state changes.</summary>
public readonly struct UserStudyModeEvent
{
    public readonly float gameTime;
    public readonly EnsembleMode ensembleMode;
    public readonly bool adaptiveMode;
    public readonly string description;
    public UserStudyModeEvent(float gameTime, EnsembleMode ensembleMode,
        bool adaptiveMode, string description)
    {
        this.gameTime = gameTime; this.ensembleMode = ensembleMode;
        this.adaptiveMode = adaptiveMode; this.description = description;
    }
}

/// <summary>
/// Experimental condition for the ensemble. IN EVERY CONDITION each user tap fires
/// Violin 1's next note: it glides toward each note in one continuous motion so the note
/// lands right on the expected tap; when a tap is overdue it slows down smoothly and truly
/// STOPS just before the next note, waiting — a late tap swells it back in, never a jump.
/// The conditions differ in what the OTHER three musicians do:
///   Adaptive    — the others synchronise to Violin 1: its smoothed tempo + the ARME timing
///                 model predicting their next onsets from the real played notes + a phase
///                 pull onto Violin 1's position.
///   NonAdaptive — the others play normally together at the fixed native tempo and make NO
///                 attempt to synchronise with Violin 1.
///   Agentic     — mutual corrections: the others follow Violin 1 loosely (capped
///                 convergence, tempo spread + jitter) AND Violin 1 is gently pulled toward
///                 the others, so everyone corrects toward everyone.
/// </summary>
public enum EnsembleMode
{
    Adaptive,
    NonAdaptive,
    Agentic
}

/// <summary>
/// How tightly virtuals lock onto the user's beat in Adaptive mode.
///   Custom        — Use inspector alpha/beta values verbatim (loose Wing-Kristofferson default).
///   MusicalLoose  — Same as Custom but guarantees the phase-reset on activation (fixes drift-in bug).
///   MusicalTight  — Stronger coupling (α≈0.6, β≈0.35); locks on within a few taps, still feels organic.
///   SnapToBeat    — α=1, β=1; spheres snap exactly to the predicted next beat each tap. Looks perfectly synced.
/// </summary>
public enum SyncTightness
{
    Custom,
    MusicalLoose,
    MusicalTight,
    SnapToBeat
}

/// <summary>
/// One musician video driven by the timing model: a <see cref="VideoPlayer"/> (picture) and
/// its audio (.wav) played on a plain <see cref="AudioSource"/>, both speed-matched to the
/// user's tapped tempo. The recorded onset times are parsed for reference/alignment.
/// </summary>
[System.Serializable]
public class VideoPart
{
    [Tooltip("Inspector label only.")]
    public string label;

    [Tooltip("VideoPlayer that shows this part's clip. Its playbackSpeed is warped to the model's onsets.")]
    public VideoPlayer video;

    [Tooltip("This part's audio (.wav). Routed through the native ARME playback controller for pitch-preserved time-stretch (tempo follows the user's taps; pitch stays natural).")]
    public AudioClip audioClip;

    [Tooltip("Onset file (-CRNNManual): one timestamp per line, used to warp the recording onto the user's beat.")]
    public TextAsset onsetFile;

    [Tooltip("Optional plane/renderer. If set (and Manage Display is on) the part gets its OWN RenderTexture + material instance so parts can never share a texture.")]
    public Renderer displayRenderer;

    // ── Runtime state (not serialized) ───────────────────────────────────
    [System.NonSerialized] public AudioSource audioSource;   // hosts Unity's DSP filter chain; the native controller fills it
    [System.NonSerialized] public ARMEOnsetBasedPlaybackController playback; // pitch-preserving time-stretch (ARME native lib)
    [System.NonSerialized] public List<float> onsets;
    [System.NonSerialized] public float speed;            // last playbackSpeed written to the video
    [System.NonSerialized] public float baseSpeed;        // smoothed tempo (before the immediate phase-alignment nudge)
    [System.NonSerialized] public float tempoOffset;      // per-player starting tempo delta (fraction), shrinks as the ensemble converges
    [System.NonSerialized] public bool  finished;         // audio content exhausted this take (take-end follows the audio, not the longer video)
    [System.NonSerialized] public float audioElapsed;     // source seconds the audio stretcher has consumed this take (≈ audio playhead)
    [System.NonSerialized] public int   nextOnsetIdx;     // next entry of `onsets` the playhead hasn't crossed yet this take
    [System.NonSerialized] public int   modelSlot = -1;   // ARME timing-model player slot (0 = Violin 1 leader, 1.. = followers, -1 = none)
    [System.NonSerialized] public float pendingNoteTime = -1f; // follower's latest played note awaiting the next VN1 beat slot (-1 = none)
    [System.NonSerialized] public bool  suspended;        // truly paused (video + native audio) while waiting for taps
    [System.NonSerialized] public RenderTexture ownedRT;
    [System.NonSerialized] public Material matInstance;
}

/// <summary>
/// User study controller for the ARME Timing Model. See <see cref="EnsembleMode"/>
/// for the three experimental conditions.
/// </summary>
public class ARMEUserStudyController : MonoBehaviour
{
    [BoxGroup("Mode")]
    [Tooltip("Experimental condition for this run.")]
    [SerializeField] private EnsembleMode mode = EnsembleMode.Adaptive;

    [BoxGroup("Mode")]
    [Tooltip("Sensory modality for this run. AudioOnly hides the musician videos (audio still plays); AudioVisual shows them.")]
    [SerializeField] private Modality modality = Modality.AudioVisual;

    [BoxGroup("Experiment Flow")]
    [Tooltip("When false, the controller waits for an ARMEUserStudyExperimentUI to call BeginPlayback() (welcome screen -> count-in -> start) instead of auto-playing the videos on scene load. Auto-set to false at runtime whenever an experiment UI is present in the scene.")]
    [SerializeField] private bool autoStart = true;

    [BoxGroup("Experiment Flow")]
    [Tooltip("If every musician is finished or standing in a wait and no tap has come for this many seconds, the take ends automatically — the ensemble has come to rest, so the trial is over instead of hanging open.")]
    [SerializeField, Range(2f, 15f)] private float stalledTakeTimeout = 5f;

    [BoxGroup("Input")]
    [Tooltip("Optional Teensy FSR tap sensor. When assigned (or auto-found), its hardware taps register as the user's beat taps, alongside mouse clicks. Leave empty to use mouse only.")]
    [SerializeField] private ARMETapDetection tapDetection;

    [BoxGroup("Virtual Players")]
    [Tooltip("How many virtual players the timing model runs. Each is mapped (cycling) to one of the scene's video parts. For independent per-musician warp, set this equal to the number of video parts (≤4).")]
    [SerializeField, Range(1, 15)] private int numVirtualPlayers = 3;

    [BoxGroup("Videos")]
    [Tooltip("The musician video parts driven by the model. Leave empty to import them from a scene ARMEEnsembleSyncPlayer (or discover scene VideoPlayers) at runtime, or use the context-menu auto-wire to populate by name.")]
    [SerializeField] private List<VideoPart> videoParts = new List<VideoPart>();

    [BoxGroup("Videos")]
    [Tooltip("Label prefix identifying the LEADER part (Violin 1). The participant's taps drive this part's tempo; the other musicians synchronise to it. Falls back to the first part if no label matches.")]
    [SerializeField] private string leaderLabelPrefix = "VN1";

    [BoxGroup("Videos")]
    [Tooltip("Give each part its own RenderTexture + material instance at runtime (binds the '_TB' texture). Only acts on parts whose Display Renderer is set; otherwise the VideoPlayer's existing render target is left untouched.")]
    [SerializeField] private bool manageDisplay = true;

    [BoxGroup("Videos")]
    [Tooltip("Shader texture property that receives the video (the AlphaShaderTB / MixVidTB material uses '_TB').")]
    [SerializeField] private string videoTextureProperty = "_TB";

    [BoxGroup("Videos")]
    [Tooltip("Tempo (BPM) at which the recordings play at their normal 1x speed. Tap at this rate and the video plays natively; tapping faster/slower scales the playback speed proportionally.")]
    [SerializeField, Range(40f, 220f)] private float naturalBPM = 120f;

    [BoxGroup("Videos")]
    [Tooltip("Baseline playback rate applied to the recordings' native speed (1 = original, 0.9 = 10% slower). Scales both the idle/native tempo and the user-followed tempo.")]
    [SerializeField, Range(0.25f, 1.5f)] private float normalPlaybackRate = 0.9f;

    [BoxGroup("Videos")]
    [Tooltip("Lower clamp on the video playback speed while following the user (fraction of native speed). Exception: while waiting for taps, Violin 1 (and an ensemble settling with it) can slow below this all the way to a true pause.")]
    [SerializeField, Range(0.1f, 1f)] private float minPlaybackSpeed = 0.5f;

    [BoxGroup("Videos")]
    [Tooltip("Upper clamp on the video playback speed while following the user (multiple of native speed). No matter how fast you tap, the video won't go above this.")]
    [SerializeField, Range(1f, 4f)] private float maxPlaybackSpeed = 1.2f;

    [BoxGroup("Videos")]
    [Tooltip("How quickly the videos ease to a new tempo when your tapping speed changes (higher = snappier, lower = smoother). Ramping avoids jolting the decoder and audio on every tap.")]
    [SerializeField, Range(1f, 20f)] private float speedSmoothRate = 6f;

    [BoxGroup("Videos")]
    [Tooltip("How far short (source seconds) of each note Violin 1's glide aims. The tap then carries the playhead across the note, so it sounds right on the tap. Smaller = tighter to the tap; larger = a safer buffer against notes slipping out on their own.")]
    [SerializeField, Range(0.01f, 0.2f)] private float armWindowSeconds = 0.03f;

    [BoxGroup("Videos")]
    [Tooltip("How firmly Violin 1 tracks its tap schedule (per second). Higher = notes land tighter on the taps but with more speed movement; lower = looser and even smoother.")]
    [SerializeField, Range(1f, 12f)] private float leaderCatchUpGain = 9f;


    [BoxGroup("Videos")]
    [Tooltip("Seconds over which the other musicians average Violin 1's tempo. They follow this SMOOTHED tempo — not Violin 1's instantaneous sprint/creep — so the tap-triggering stays inaudible in their playing. Higher = smoother/steadier, lower = more reactive.")]
    [SerializeField, Range(0.2f, 5f)] private float leaderTempoAveraging = 1.5f;

    [BoxGroup("Random Mode")]
    [Tooltip("Minimum random blink interval (seconds) when in Random mode (no recent user input).")]
    [SerializeField] private float minRandomInterval = 0.4f;

    [BoxGroup("Random Mode")]
    [Tooltip("Maximum random blink interval (seconds) when in Random mode.")]
    [SerializeField] private float maxRandomInterval = 1.2f;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("Preset that overrides alpha/beta to control how tightly virtuals lock onto your beat. Set to Custom to use the alpha/beta sliders below.")]
    [SerializeField] private SyncTightness syncTightness = SyncTightness.MusicalTight;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("Phase-correction gain. Each user tap pulls each virtual's next-blink time toward the model's predicted onset by this fraction of the error. 0 = no phase coupling, 1 = snap to prediction immediately. Ignored unless syncTightness = Custom.")]
    [SerializeField, Range(0f, 1f)] private float alpha = 0.30f;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("Period-correction gain. Each user tap blends each virtual's internal IOI toward the user's measured IOI by this fraction. 0 = no tempo adaptation, 1 = match user immediately. Ignored unless syncTightness = Custom.")]
    [SerializeField, Range(0f, 1f)] private float beta = 0.15f;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("Weight given to each new tap when updating the measured tempo (IOI). HIGHER = more reactive (follows your latest tap faster); lower = more inertial/stable. ~0.6 feels responsive without jitter.")]
    [SerializeField, Range(0f, 1f)] private float userIoiSmoothing = 0.30f;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("Per-virtual visual phase offset (seconds). Virtual i targets predicted+offset*i so blinks pulse around the user's beat instead of on top of it. Set to 0 for in-phase sync.")]
    [SerializeField, Range(0f, 0.5f)] private float phaseOffsetPerPlayer = 0.0f;

    [BoxGroup("Idle Behaviour")]
    [Tooltip("If the user stops tapping for more than this many user-IOIs, virtuals revert to Random mode. (Adaptive mode only.)")]
    [SerializeField, Range(1f, 10f)] private float idleTimeoutFactor = 2.5f;

    [BoxGroup("Idle Behaviour")]
    [Tooltip("Hard fallback timeout (seconds) used before a user IOI has been measured.")]
    [SerializeField] private float fallbackIdleSeconds = 3.0f;

    [BoxGroup("Non-Adaptive Mode")]
    [Tooltip("Fixed ensemble tempo for Non-Adaptive and Agentic modes (BPM).")]
    [SerializeField, Range(40f, 200f)] private float fixedBPM = 90f;

    [BoxGroup("Agentic Mode")]
    [Tooltip("Per-virtual baseline IOI variation (seconds). Each virtual draws a fixed offset at start so the ensemble isn't homogeneous.")]
    [SerializeField, Range(0f, 0.3f)] private float agenticIoiVariation = 0.06f;

    [BoxGroup("Agentic Mode")]
    [Tooltip("Standard deviation of Gaussian noise added to each virtual's blink interval (seconds). Drives per-blink timing jitter.")]
    [SerializeField, Range(0f, 0.2f)] private float agenticTimingNoise = 0.04f;

    [BoxGroup("Agentic Mode")]
    [Tooltip("How strongly each virtual's IOI drifts back to its own baseline (vs. wandering freely). 0 = pure drift, 1 = locked to baseline.")]
    [SerializeField, Range(0f, 1f)] private float agenticBaselinePull = 0.25f;

    [BoxGroup("Agentic Mode")]
    [Tooltip("Reduced phase-correction gain in Agentic mode — agents only loosely follow the user.")]
    [SerializeField, Range(0f, 1f)] private float agenticAlpha = 0.10f;

    [BoxGroup("Agentic Mode")]
    [Tooltip("Reduced period-correction gain in Agentic mode.")]
    [SerializeField, Range(0f, 1f)] private float agenticBeta = 0.05f;

    [BoxGroup("Agentic Mode")]
    [Tooltip("Agentic only: how strongly Violin 1 is pulled toward the other musicians' playback position (mutual correction — everyone corrects toward everyone). Keep small so the tapping clearly stays in control; 0 = Violin 1 ignores the ensemble entirely.")]
    [SerializeField, Range(0f, 1f)] private float agenticLeaderCohesion = 0.3f;

    [BoxGroup("Sync Convergence (videos)")]
    [Tooltip("Each player starts at a slightly different tempo: a random ± fraction of native speed. 0 = all start identical.")]
    [SerializeField, Range(0f, 0.3f)] private float startTempoSpread = 0.04f;

    [BoxGroup("Sync Convergence (videos)")]
    [Tooltip("Seconds for the ensemble to pull fully into sync once you start tapping (lower = quicker). The tempo offsets shrink and the players align over this time.")]
    [SerializeField, Range(0.5f, 15f)] private float convergeSeconds = 4f;

    [BoxGroup("Sync Convergence (videos)")]
    [Tooltip("How strongly each player's speed is nudged to pull its playback position onto the lead player's (tightens the quartet). Higher = the players lock together faster/firmer. 0 = tempo only, no phase alignment.")]
    [SerializeField, Range(0f, 4f)] private float phaseCohesionGain = 1.6f;

    [BoxGroup("Sync Convergence (videos)")]
    [Tooltip("How tightly Agentic locks once converged (1 = as tight as Adaptive). Below 1 keeps a residual tempo/phase spread so Agentic stays a bit loose.")]
    [SerializeField, Range(0f, 1f)] private float agenticMaxConvergence = 0.55f;

    [BoxGroup("Logging")]
    [SerializeField] private bool verboseLogging = true;

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting] public int userTapCount;

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting] public float lastUserTapTime;

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting] public float currentUserBPM;

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting] public float measuredUserIOI;

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting] public bool adaptiveMode;

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting] public EnsembleMode currentMode;

    private const int UserPlayerIndex = 0;

    private SimpleTimingModel _model;
    private float[] _virtualIOI;          // each virtual's current internal IOI
    private float[] _virtualBaselineIOI;  // Agentic: per-virtual baseline tempo
    private float[] _nextBlinkTime;       // when each virtual fires next
    private int[] _onsetCounts;

    // Video startup is deferred until every VideoPlayer has finished preparing so the
    // pictures begin together with the audio.
    private bool _videosPending;
    private float _videoArmTime;
    private float _ensembleSpeed = 1f;   // average applied playback speed (for the HUD readout)
    private float _convergence = 0f;     // 0 = full starting offset (loose), 1 = fully pulled into sync
    private float _previousUserTapTime = -1f;
    private bool _haveSpawnGaussian;
    private float _spareGaussian;

    // ARME expects ensemble onsets to share a beat index across players. The timing model is
    // fed the musicians' REAL played note onsets: each Violin 1 note (fired by a user tap)
    // opens a new shared beat slot (VN1 = model player 0, the reference), and each follower's
    // most recent played note is registered under that same slot. The model then predicts the
    // other players' next onsets, which steer their playback in UpdateEnsembleTempo. Without
    // shared indices the per-player onset counts diverge and ARME's predictions degenerate to
    // -900 sentinels or past-time values.
    private int _currentBeatIndex;

    // ── Experiment flow (driven by ARMEUserStudyExperimentUI) ────────────
    private bool _externalControl;   // an experiment UI owns when playback starts/repeats
    private bool _playbackActive;    // a take is currently playing (for end detection)
    private int  _finishedParts;     // how many parts have reached their end this take
    private float _takeStartTime;    // when the current take's playback began (stall detection)
    private float _lastProgressPos;  // summed source position of unfinished parts at the last advance
    private float _lastProgressTime; // when that sum last moved (progress watchdog)
    private float _lastTakeDebugTime; // throttle for the per-take heartbeat log
    private VideoPart _leader;       // the Violin 1 part: driven by the taps, followed by the others
    private float _leaderSpeedAvg = 1f; // smoothed Violin 1 tempo the other musicians follow

    // Violin 1's tap schedule: each tap anchors a glide from the current playhead to just
    // short of the FOLLOWING note, timed to arrive at the next expected tap. A proportional
    // controller tracks this path (see LeaderTriggerSpeed), giving one continuous motion —
    // no sprint-then-freeze pumping.
    private float _schedAnchorPos;   // leader source position when the schedule was last anchored
    private float _schedAnchorTime;  // wall time of that anchor (the tap moment)
    private int   _schedTargetIdx;   // onset index the schedule glides toward

    /// <summary>While false, user taps are ignored (e.g. on the welcome screen).</summary>
    [System.NonSerialized] public bool AcceptTaps = true;

    /// <summary>Fired once when every video part has played to its end (for count-in + repeat).</summary>
    public event System.Action OnPlaybackEnded;

    /// <summary>Fired when a take's audio/video actually starts (after the count-in). Lets the data
    /// logger bound the synchronisation window to real playback and exclude count-in blinks.</summary>
    public event System.Action OnPlaybackStarted;

    // Read-only accessors for the experiment UI / HUD.
    public EnsembleMode Mode => mode;
    public Modality CurrentModality => modality;
    public int VirtualPlayerCount => numVirtualPlayers;
    public float EnsembleSpeed => _ensembleSpeed;

    /// <summary>The Violin 1 part's display plane — lets the experiment UI hang its
    /// practice-only "Violin 1" label over the right musician. The scene's sync-player parts
    /// often leave Display Renderer unwired, so fall back to resolving the paired plane by
    /// the project's naming convention (TopBottomVideoRender (n) → VideoPlayerPlane (n)).</summary>
    public Renderer LeaderRenderer
    {
        get
        {
            if (_leader == null) return null;
            if (_leader.displayRenderer == null && _leader.video != null)
            {
                string planeName = _leader.video.gameObject.name
                    .Replace("TopBottomVideoRender", "VideoPlayerPlane");
                var planeGO = GameObject.Find(planeName);
                if (planeGO != null)
                    _leader.displayRenderer = planeGO.GetComponent<Renderer>();
            }
            return _leader.displayRenderer;
        }
    }

    // ── Data Logging Events ──────────────────────────────────────────────
    public event System.Action<UserStudyTapEvent>   OnUserTap;
    public event System.Action<UserStudyBlinkEvent> OnVirtualBlink;
    public event System.Action<UserStudyModeEvent>  OnModeChange;

    /// <summary>Fired each time a musician's warped audio playhead crosses a note onset —
    /// i.e. whenever the ensemble actually plays a note. The data logger writes these to the
    /// musician-onsets CSV, and Violin 1's notes are the reference for tap asynchrony.</summary>
    public event System.Action<UserStudyNoteEvent> OnMusicianNote;

    /// <summary>Returns a configuration snapshot for CSV session metadata.</summary>
    public SessionConfig GetSessionConfig() => new SessionConfig
    {
        numVirtualPlayers   = numVirtualPlayers,
        startMode           = mode,
        modality            = modality,
        alpha               = alpha,
        beta                = beta,
        userIoiSmoothing    = userIoiSmoothing,
        fixedBPM            = fixedBPM,
        agenticIoiVariation = agenticIoiVariation,
        agenticTimingNoise  = agenticTimingNoise,
        agenticBaselinePull = agenticBaselinePull,
    };

    private int TotalPlayers => numVirtualPlayers + 1;

    void Start()
    {
        // Allocate state FIRST so a missing/incompatible native timing-model library can never
        // leave these arrays null (that caused a NullReferenceException every frame in
        // DriveVirtualBlinks on macOS, where the bundled Windows .dll's can't load).
        _virtualIOI = new float[TotalPlayers];
        _virtualBaselineIOI = new float[TotalPlayers];
        _nextBlinkTime = new float[TotalPlayers];
        _onsetCounts = new int[TotalPlayers];
        _currentBeatIndex = 0;

        // The ARME timing model needs a native plugin; the bundled libraries are Windows .dll's,
        // so on macOS/Linux model creation throws. Fall back to tap-tempo sync, which does NOT
        // need the native model: the video speed follows the user's measured BPM and the virtual
        // blinks use the IOI fallback in ApplyCorrections.
        try
        {
            _model = new SimpleTimingModel(TotalPlayers);
            _model.CreateNewParameters();
        }
        catch (System.Exception ex)
        {
            _model = null;
            Debug.LogWarning($"[UserStudy] Native timing model unavailable ({ex.Message}). " +
                             "Running in tap-tempo fallback — videos still follow your taps.");
        }

        BindVideoParts();
        InitVirtualsForMode();

        // If an experiment UI is present (welcome screen + count-in + repeat) it decides when
        // playback begins; otherwise auto-start once every clip has finished preparing.
        _externalControl = !autoStart || FindFirstObjectByType<ARMEUserStudyExperimentUI>() != null;
        if (!_externalControl)
        {
            _videosPending = true;
            _videoArmTime = Time.time;
        }

        // Hook up the Teensy FSR sensor (if present) so its presses count as user beat taps.
        if (tapDetection == null) tapDetection = FindFirstObjectByType<ARMETapDetection>();
        if (tapDetection != null)
        {
            tapDetection.OnHardwareTap += HandleHardwareTap;
            Log("FSR tap sensor connected — hardware presses will register as user taps.");
        }

        string libVersion;
        try { libVersion = SimpleTimingModel.GetVersion(); } catch { libVersion = "unavailable"; }
        Log($"Ready. Players: {TotalPlayers} (P0=user, P1..P{numVirtualPlayers}=virtual). " +
            $"Videos: {videoParts.Count}. Mode={mode}. alpha={alpha:F2}, beta={beta:F2}. Lib v{libVersion}");
    }

    /// <summary>
    /// Set up each virtual's IOI and first blink time according to the active mode.
    /// </summary>
    private void InitVirtualsForMode()
    {
        currentMode = mode;
        adaptiveMode = false;
        float fixedIOI = 60f / Mathf.Max(1f, fixedBPM);

        for (int i = 1; i < TotalPlayers; i++)
        {
            switch (mode)
            {
                case EnsembleMode.NonAdaptive:
                    _virtualBaselineIOI[i] = fixedIOI;
                    _virtualIOI[i] = fixedIOI;
                    // Stagger first blinks so they don't fire perfectly in unison.
                    _nextBlinkTime[i] = Time.time + (fixedIOI * (i / (float)TotalPlayers));
                    break;

                case EnsembleMode.Agentic:
                    float jitter = Random.Range(-agenticIoiVariation, agenticIoiVariation);
                    _virtualBaselineIOI[i] = Mathf.Max(0.1f, fixedIOI + jitter);
                    _virtualIOI[i] = _virtualBaselineIOI[i];
                    _nextBlinkTime[i] = Time.time + Random.Range(0.1f, _virtualIOI[i]);
                    break;

                case EnsembleMode.Adaptive:
                default:
                    _virtualBaselineIOI[i] = 0f;
                    _virtualIOI[i] = Random.Range(minRandomInterval, maxRandomInterval);
                    _nextBlinkTime[i] = Time.time + Random.Range(0.1f, _virtualIOI[i]);
                    break;
            }
        }
    }

    private const string AudioFolder = "Assets/ARME/Ensemble/AudioClipsMain";

    /// <summary>
    /// Bind the virtual players to the scene's musician videos (replaces sphere spawning).
    /// Each part gets a plain <see cref="AudioSource"/> playing its .wav (DLL-free), its onset
    /// file is parsed, and its display is bound so each plane shows its own clip.
    /// </summary>
    private void BindVideoParts()
    {
        if (videoParts == null || videoParts.Count == 0)
            ImportPartsFromScene();

        // This controller is the sole driver of the videos. Stop anything else that would
        // also play/seek them (the EnsembleSync fallback, the stray VideoControllerPrefab) —
        // two drivers fighting causes the decoder to stall and the picture to slow and freeze.
        DisableCompetingDrivers();

        foreach (var part in videoParts)
        {
            if (part == null || part.video == null)
                continue;

            // Audio path: route the .wav through the native ARME playback controller, which
            // time-stretches it with PITCH PRESERVED (Rubber Band, baked into the macOS dylib).
            // The AudioSource only hosts Unity's DSP filter chain; the controller fills it in
            // OnAudioFilterRead. Tempo is driven per frame from UpdateEnsembleTempo via SetSpeed,
            // so sound and picture stay locked to the tapped tempo without the chipmunk effect.
            part.onsets = ParseOnsets(part.onsetFile);
            if (part.audioClip != null)
            {
                var go = new GameObject($"Audio_{part.label}");
                go.transform.SetParent(transform, false);

                // A playing, clip-less AudioSource is what makes Unity call the controller's
                // OnAudioFilterRead each audio block.
                var src = go.AddComponent<AudioSource>();
                src.clip = null;
                src.playOnAwake = true;
                src.spatialBlend = 0f;     // 2D — no positional attenuation
                part.audioSource = src;

                part.playback = go.AddComponent<ARMEOnsetBasedPlaybackController>();
                part.playback.Configure(part.audioClip, part.onsetFile);
            }

            // Configure the VideoPlayer. Plays once at native speed; we only modulate
            // playbackSpeed to follow the tapped tempo (never seek -> the picture never snaps).
            // Sound comes from the WAV (the _TB clip has no audio track).
            part.video.audioOutputMode = VideoAudioOutputMode.None;
            part.video.playOnAwake = false;
            part.video.isLooping = false;   // play once, no repeat
            part.video.skipOnDrop = true;
            part.video.playbackSpeed = 1f;

            if (manageDisplay && part.displayRenderer != null)
                BindDisplay(part);

            if (!part.video.isPrepared)
                part.video.Prepare();

            // End-of-take detection for count-in + repeat (fires only when isLooping == false).
            part.video.loopPointReached -= OnPartReachedEnd;
            part.video.loopPointReached += OnPartReachedEnd;

            part.speed = 1f;
        }

        // Honour the configured modality from the start (hide videos in AudioOnly).
        ApplyModalityVisibility();

        FindLeaderPart();
    }

    /// <summary>
    /// Identify the leader part (Violin 1) by its label prefix. The participant's taps drive
    /// this part's tempo and the other musicians synchronise to it. Falls back to the first
    /// part so the study still runs if the labels don't match.
    /// </summary>
    private void FindLeaderPart()
    {
        _leader = null;
        foreach (var part in videoParts)
        {
            if (part == null || part.video == null) continue;
            if (!string.IsNullOrEmpty(part.label) && !string.IsNullOrEmpty(leaderLabelPrefix) &&
                part.label.StartsWith(leaderLabelPrefix, System.StringComparison.OrdinalIgnoreCase))
            {
                _leader = part;
                break;
            }
        }
        if (_leader == null)
        {
            foreach (var part in videoParts)
                if (part != null && part.video != null) { _leader = part; break; }
            if (_leader != null)
                Debug.LogWarning($"[UserStudy] No part label starts with '{leaderLabelPrefix}' — " +
                                 $"using '{_leader.label}' as the leader (Violin 1).");
        }
        // Map each part onto an ARME timing-model player slot: Violin 1 = player 0 (the
        // reference whose tap-fired notes define the shared beats), the others = players 1..N.
        int slot = 1;
        foreach (var part in videoParts)
        {
            if (part == null || part.video == null) continue;
            if (part == _leader)                  part.modelSlot = 0;
            else if (slot <= numVirtualPlayers)   part.modelSlot = slot++;
            else                                  part.modelSlot = -1;   // more parts than model players
        }

        if (_leader != null)
            Log($"Leader part (Violin 1): '{_leader.label}' (model player 0). Each tap fires its " +
                "next note; the others synchronise to Violin 1 via the timing model's predictions.");
    }

    /// <summary>Count parts that have a VideoPlayer (denominator for end-of-take detection).</summary>
    private int PartCount()
    {
        int n = 0;
        foreach (var p in videoParts)
            if (p != null && p.video != null) n++;
        return n;
    }

    /// <summary>The video reached its end (used when a video clip is shorter than its audio).</summary>
    private void OnPartReachedEnd(VideoPlayer vp)
    {
        if (!_playbackActive) return;
        MarkPartFinished(FindPart(vp));
        MaybeEndTake();
    }

    /// <summary>Find the part that owns a given VideoPlayer (for loopPointReached callbacks).</summary>
    private VideoPart FindPart(VideoPlayer vp)
    {
        foreach (var p in videoParts)
            if (p != null && p.video == vp) return p;
        return null;
    }

    /// <summary>
    /// A part is done the moment its AUDIO content is exhausted (or its video reaches the end,
    /// whichever comes first). Freeze the picture on its last frame and silence any residual
    /// audio so sound and picture stop together — the take follows the audio, not the (often
    /// slightly longer) video clip.
    /// </summary>
    private void MarkPartFinished(VideoPart part)
    {
        if (part == null || part.finished) return;
        part.finished = true;
        _finishedParts++;
        if (part.video != null)    part.video.Pause();          // freeze the picture where the sound stopped
        if (part.playback != null) part.playback.StopPlayback(); // silence any residual audio
    }

    /// <summary>End the take once every part has finished.</summary>
    private void MaybeEndTake()
    {
        if (_playbackActive && _finishedParts >= PartCount())
        {
            _playbackActive = false;
            Log("All parts finished (audio ended) — invoking OnPlaybackEnded.");
            OnPlaybackEnded?.Invoke();
        }
    }

    /// <summary>
    /// Each frame of an active take, end any part whose audio has played to the end of its clip.
    /// The stretcher consumes source audio at the same speed the video is warped, so once the
    /// consumed source (<see cref="VideoPart.audioElapsed"/>) reaches the clip length the sound
    /// has just finished — that's when the matching video is stopped, so videos end with the audio.
    /// </summary>
    private void CheckAudioCompletion()
    {
        if (!_playbackActive) return;
        foreach (var part in videoParts)
        {
            if (part == null || part.finished || part.video == null || part.audioClip == null)
                continue;

            // The video's own media position is an independent clock over the SAME content
            // (audio and video are the same take, same length, driven at the same speed from
            // the same start). Take the further of the two so the part can never linger after
            // its content is really over — this closes the "audio ends, videos play on" gap.
            float sourcePos = part.audioElapsed;
            if (part.video.isPrepared)
                sourcePos = Mathf.Max(sourcePos, (float)part.video.time);

            if (sourcePos >= part.audioClip.length - 0.05f)
                MarkPartFinished(part);
        }

        // If the rest of the ensemble's audio has finished and Violin 1 is just standing in a
        // wait (taps stopped), the music is over — close the take instead of holding the trial
        // open for taps that may never come.
        if (_playbackActive && _leader != null && !_leader.finished && _leader.suspended)
        {
            bool othersDone = true;
            foreach (var part in videoParts)
                if (part != null && part != _leader && part.video != null && !part.finished)
                { othersDone = false; break; }
            if (othersDone)
            {
                Log("Ensemble audio finished while Violin 1 waits for taps — ending the take.");
                MarkPartFinished(_leader);
            }
        }

        // Take-end watchdog — the trial must NEVER hang on a resting ensemble. Two triggers,
        // both requiring no tap for stalledTakeTimeout seconds:
        //  • at rest: every unfinished part is paused-waiting or barely moving (a part can
        //    legitimately hover just above the pause threshold without really going anywhere);
        //  • no progress: the unfinished parts' source position hasn't advanced at all —
        //    belt-and-braces for any state the flags don't capture (decoder stalls etc.).
        if (_playbackActive)
        {
            float now = Time.time;
            float idleSince = Mathf.Max(lastUserTapTime, _takeStartTime);

            float progress = 0f;
            bool anyRunning = false, anyUnfinished = false;
            foreach (var part in videoParts)
            {
                if (part == null || part.video == null || part.finished) continue;
                anyUnfinished = true;
                progress += Mathf.Max(part.audioElapsed, part.video.isPrepared ? (float)part.video.time : 0f);
                if (!part.suspended && part.speed > ResumeSpeed) anyRunning = true;
            }
            if (progress > _lastProgressPos + 0.05f)
            {
                _lastProgressPos = progress;
                _lastProgressTime = now;
            }

            bool tapsIdle   = now - idleSince > stalledTakeTimeout;
            bool atRest     = anyUnfinished && !anyRunning && tapsIdle;
            bool noProgress = anyUnfinished && now - Mathf.Max(_lastProgressTime, idleSince) > stalledTakeTimeout;

            if (atRest || noProgress)
            {
                Log($"Take-end watchdog: ensemble {(atRest ? "at rest" : "made no progress")} with no taps for {stalledTakeTimeout:F0}s — ending the take.");
                foreach (var part in videoParts)
                    if (part != null && part.video != null && !part.finished)
                        MarkPartFinished(part);
            }
            else if (verboseLogging && now - _lastTakeDebugTime > 2f)
            {
                // Heartbeat: one line every 2 s showing why the take is still open.
                _lastTakeDebugTime = now;
                var sb = new System.Text.StringBuilder("TAKE t=");
                sb.Append((now - _takeStartTime).ToString("F1")).Append("s  ");
                foreach (var part in videoParts)
                {
                    if (part == null || part.video == null) continue;
                    sb.Append('[').Append(part.label)
                      .Append(part.finished ? " FIN" : part.suspended ? " WAIT" : " RUN")
                      .Append(" v=").Append(part.speed.ToString("F2"))
                      .Append(" src=").Append(Mathf.Max(part.audioElapsed, part.video.isPrepared ? (float)part.video.time : 0f).ToString("F1"))
                      .Append('/').Append(part.audioClip != null ? part.audioClip.length.ToString("F1") : "?")
                      .Append("] ");
                }
                Log(sb.ToString());
            }
        }
        MaybeEndTake();
    }

    /// <summary>
    /// Populate <see cref="videoParts"/> when none were assigned in the inspector: prefer a
    /// scene <see cref="ARMEEnsembleSyncPlayer"/> (its parts already map video↔WAV↔onset),
    /// otherwise fall back to discovering VideoPlayers (video-only — use the editor auto-wire
    /// to attach audio/onset by name).
    /// </summary>
    private void ImportPartsFromScene()
    {
        videoParts = new List<VideoPart>();

        var syncs = FindObjectsByType<ARMEEnsembleSyncPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (syncs.Length > 0 && syncs[0].parts != null && syncs[0].parts.Count > 0)
        {
            foreach (var p in syncs[0].parts)
            {
                if (p == null || p.video == null)
                    continue;
                videoParts.Add(new VideoPart
                {
                    label = string.IsNullOrEmpty(p.label) ? p.video.name : p.label,
                    video = p.video,
                    audioClip = p.audio,
                    onsetFile = p.onsetFile,
                    displayRenderer = p.displayRenderer
                });
            }
            Log($"Imported {videoParts.Count} video part(s) from ARMEEnsembleSyncPlayer.");
            return;
        }

        var vps = FindObjectsByType<VideoPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var vp in vps)
        {
            if (vp.clip == null)
                continue;
            videoParts.Add(new VideoPart { label = vp.clip.name, video = vp });
        }
        Log($"Discovered {videoParts.Count} scene VideoPlayer(s) (no audio/onset wired — run the auto-wire context menu).");
    }

    /// <summary>
    /// Stop anything else in the scene that drives the same VideoPlayers. The
    /// <see cref="ARMEEnsembleSyncPlayer"/> ("EnsembleSync") and the stray VideoControllerPrefab
    /// both Play()/seek videos on their own clock; with this controller also warping them, the
    /// two fight and the decoder stalls ("videos slow down and stop"). We own the videos now.
    /// </summary>
    private void DisableCompetingDrivers()
    {
        var ours = new HashSet<VideoPlayer>();
        foreach (var p in videoParts)
            if (p != null && p.video != null)
                ours.Add(p.video);

        foreach (var sync in FindObjectsByType<ARMEEnsembleSyncPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            sync.Stop();          // pause its videos + stop its (native-tempo) WAV sources
            sync.enabled = false; // and stop its Update from re-driving them
            Log($"Disabled competing driver '{sync.name}' (ARMEEnsembleSyncPlayer).");
        }

        // A standalone ARMEOnsetBasedEnsembleController is a SECOND audio engine playing the
        // same parts (the overlap), and it auto-starts during the count-in. We now drive the
        // native pitch-preserving playback per part ourselves, so shut any such coordinator down.
        foreach (var ens in FindObjectsByType<ARMEOnsetBasedEnsembleController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            ens.StopEnsemblePlayback();
            ens.enabled = false;
            Log($"Disabled competing audio engine '{ens.name}' (ARMEOnsetBasedEnsembleController).");
        }

        foreach (var vp in FindObjectsByType<VideoPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (ours.Contains(vp))
                continue;
            vp.playOnAwake = false;
            if (vp.isPlaying)
                vp.Stop();
            Log($"Stopped stray VideoPlayer '{vp.name}' (not one of our parts).");
        }
    }

    /// <summary>
    /// Give a part its own RenderTexture + material instance and bind the video to the
    /// '_TB' texture, so parts can never share a texture (the "only one video plays" bug).
    /// Mirrors <see cref="ARMEEnsembleSyncPlayer"/>.
    /// </summary>
    private void BindDisplay(VideoPart part)
    {
        int w = (part.video.clip != null && part.video.clip.width > 0) ? (int)part.video.clip.width : 1920;
        int h = (part.video.clip != null && part.video.clip.height > 0) ? (int)part.video.clip.height : 1080;

        part.ownedRT = new RenderTexture(w, h, 0) { name = $"UserStudyRT_{part.label}" };
        part.ownedRT.Create();

        part.video.renderMode = VideoRenderMode.RenderTexture;
        part.video.targetTexture = part.ownedRT;

        part.matInstance = part.displayRenderer.material; // .material clones a unique instance
        if (part.matInstance != null && part.matInstance.HasProperty(videoTextureProperty))
            part.matInstance.SetTexture(videoTextureProperty, part.ownedRT);
        else
            Debug.LogWarning($"[UserStudy] Part '{part.label}': material has no '{videoTextureProperty}' property; video texture not bound.");
    }

    /// <summary>Parse an onset file (one timestamp per line) into a sorted list of seconds.</summary>
    private static List<float> ParseOnsets(TextAsset file) => ARMEOnsetUtil.ParseAll(file);

    /// <summary>True once every part's VideoPlayer has finished preparing.</summary>
    private bool AllVideosPrepared()
    {
        foreach (var part in videoParts)
            if (part != null && part.video != null && !part.video.isPrepared)
                return false;
        return true;
    }

    /// <summary>
    /// Start audio + video for every part. Each player gets a small random tempo offset and a
    /// small random phase (start position) so the ensemble begins slightly out of sync; once the
    /// user taps, UpdateEnsembleTempo gradually shrinks the offsets and pulls the players together.
    /// </summary>
    private void StartVideos()
    {
        _ensembleSpeed = 1f;
        _convergence = 0f;        // start loose; converge only once the user taps
        _finishedParts = 0;
        _leaderSpeedAvg = normalPlaybackRate;   // smoothed VN1 tempo restarts at native
        _schedAnchorPos = 0f;     // Violin 1's schedule: glide from the top of the recording…
        _schedAnchorTime = Time.time;
        _schedTargetIdx = 0;      // …to the first note, arriving at the first expected tap
        _takeStartTime = Time.time;
        _lastProgressPos = 0f;
        _lastProgressTime = Time.time;
        _lastTakeDebugTime = 0f;
        _playbackActive = true;
        OnPlaybackStarted?.Invoke();

        foreach (var part in videoParts)
        {
            if (part == null)
                continue;

            // Per-player starting tempo offset drives the loose→converged feel. Audio and video
            // both start from the top so each part's sound and picture stay aligned (the native
            // stretcher has no per-sample start offset), so the spread is in tempo, not phase.
            // Violin 1 is DISCONNECTED from the ensemble spread: it starts exactly at native
            // tempo and responds only to the user's tapping.
            part.tempoOffset = part == _leader ? 0f : Random.Range(-startTempoSpread, startTempoSpread);
            float startSpeed = normalPlaybackRate * (1f + part.tempoOffset);

            part.finished = false;      // fresh take: nothing has ended yet
            part.suspended = false;     // running (RestartFromBeginning + Play below)
            part.audioElapsed = 0f;     // audio playhead back at the start
            part.nextOnsetIdx = 0;      // note-onset tracking restarts from the first note
            part.pendingNoteTime = -1f; // no note awaiting a beat slot yet

            if (part.playback != null)
            {
                part.playback.RestartFromBeginning();   // rebuild the native stretcher → rewinds to sample 0
                part.playback.SetSpeed(startSpeed);
            }

            if (part.video != null)
            {
                part.video.playbackSpeed = startSpeed;
                part.video.time = 0.0;
                part.video.Play();
            }

            part.speed = startSpeed;
            part.baseSpeed = startSpeed;
        }
    }

    void Update()
    {
        // Deferred video start: wait until every clip has prepared (or a timeout) so the
        // pictures begin together.
        if (_videosPending)
        {
            if (AllVideosPrepared() || Time.time - _videoArmTime > 5f)
            {
                _videosPending = false;
                StartVideos();
            }
        }

        // If the user changed the mode in the inspector at runtime, re-initialise.
        if (mode != currentMode) InitVirtualsForMode();

        HandleUserTap();
        DetectIdleRevert();
        DriveVirtualBlinks();

        // Drive every part at one shared, smoothly-ramped tempo (follows the user while
        // engaged, eases back to native 1× otherwise).
        UpdateEnsembleTempo();

        // End each part (and the take) when its audio finishes, so videos stop with the sound.
        CheckAudioCompletion();
    }

    /// <summary>
    /// True while the user is actively tapping with a measured tempo. Drives the followers'
    /// convergence (which Non-Adaptive caps at 0 anyway) and Violin 1's fallback tempo once
    /// its notes are exhausted. Violin 1 itself is tap-controlled in EVERY condition.
    /// </summary>
    private bool VideosEngaged
    {
        get
        {
            if (currentUserBPM <= 1e-3f) return false;

            float idle = (lastUserTapTime > 0f) ? (Time.time - lastUserTapTime) : float.PositiveInfinity;
            float threshold = (measuredUserIOI > 0f) ? idleTimeoutFactor * measuredUserIOI : fallbackIdleSeconds;
            return idle <= threshold;
        }
    }

    private void HandleUserTap()
    {
        if (!AcceptTaps) return;   // ignore clicks while the welcome screen is up

        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;

        RegisterUserTap(Time.time);
    }

    /// <summary>
    /// Fired when the Teensy FSR sensor registers a tap. The event is raised on the main thread
    /// (dequeued in <see cref="ARMETapDetection.Update"/>), so it can drive the model directly.
    /// </summary>
    private void HandleHardwareTap(HardwareTapEvent e)
    {
        if (!AcceptTaps) return;
        RegisterUserTap(Time.time);
    }

    /// <summary>
    /// Core beat-tap handling shared by the mouse and the FSR sensor: registers the onset with the
    /// timing model, measures the user's tempo, and drives the ensemble adaptation.
    /// </summary>
    private void RegisterUserTap(float t)
    {
        userTapCount++;
        _onsetCounts[UserPlayerIndex]++;

        // NOTE: taps are NOT registered to the ARME timing model. The model is fed the
        // musicians' real played note onsets (see RegisterNoteWithModel): taps fire
        // Violin 1's notes, Violin 1's notes define the model's beats, and the model
        // predicts the other players' next onsets from those.

        // Each tap fires Violin 1's next note by re-anchoring its schedule: from wherever the
        // playhead is now (just short of the pending note), glide to the FOLLOWING note over
        // one expected tap interval. The pending note is crossed moments after the tap — a
        // smooth swell rather than a burst — and the glide continues seamlessly toward the
        // next note (see LeaderTriggerSpeed). Applies in EVERY condition.
        if (_playbackActive &&
            _leader != null && _leader.onsets != null && _leader.onsets.Count > 0)
        {
            _schedAnchorTime = t;
            _schedAnchorPos  = _leader.audioElapsed;
            _schedTargetIdx  = Mathf.Min(_leader.nextOnsetIdx + 1, _leader.onsets.Count - 1);
        }

        // Measure inter-tap interval and smooth it into the user's IOI estimate.
        float interval = (_previousUserTapTime > 0f) ? (t - _previousUserTapTime) : 0f;
        if (interval > 0.05f && interval < 5f)
        {
            if (measuredUserIOI <= 0f)
                measuredUserIOI = interval;
            else
                measuredUserIOI = (1f - userIoiSmoothing) * measuredUserIOI + userIoiSmoothing * interval;
            currentUserBPM = 60f / measuredUserIOI;
        }

        _previousUserTapTime = t;
        lastUserTapTime = t;

        OnUserTap?.Invoke(new UserStudyTapEvent(t, userTapCount, interval, measuredUserIOI, currentUserBPM, mode, adaptiveMode));

        Log($"USER TAP #{userTapCount} @ t={t:F3}s  Δ={interval:F3}s  measuredIOI={measuredUserIOI:F3}s  BPM={currentUserBPM:F1}  totalUserOnsets={_onsetCounts[UserPlayerIndex]} mode={mode}");

        // Non-Adaptive: Violin 1's schedule was already re-anchored above (it is tap-controlled
        // in every condition), but the OTHER musicians are a fixed ensemble — skip the blink
        // layer's adaptive transitions and corrections.
        if (mode == EnsembleMode.NonAdaptive) return;

        // Need at least one IOI measurement to drive adaptation.
        if (measuredUserIOI <= 0f) return;

        // Adaptive: transition Random -> follower on first measured IOI.
        if (mode == EnsembleMode.Adaptive && !adaptiveMode)
        {
            adaptiveMode = true;
            OnModeChange?.Invoke(new UserStudyModeEvent(t, mode, true,
                $"Adaptive activated: IOI={measuredUserIOI:F3}s BPM={currentUserBPM:F1}"));
            Log($">>> Switching to ADAPTIVE mode. Initial userIOI={measuredUserIOI:F3}s, BPM={currentUserBPM:F1}");
            for (int i = 1; i < TotalPlayers; i++)
            {
                _virtualIOI[i] = measuredUserIOI;
                // Reset phase so virtuals start aligned to the user's beat instead of
                // their random pre-activation schedule.
                _nextBlinkTime[i] = t + measuredUserIOI + phaseOffsetPerPlayer * i;
                Log($"  -> P{i} virtualIOI={measuredUserIOI:F3}s, nextBlink reset to {_nextBlinkTime[i]:F3}s");
            }
        }

        // Agentic mode is always "weakly adapting" once the user is tapping.
        if (mode == EnsembleMode.Agentic)
        {
            if (!adaptiveMode)
                OnModeChange?.Invoke(new UserStudyModeEvent(t, mode, true, "Agentic active: user tapping"));
            adaptiveMode = true;
        }

        ApplyCorrections(t);
    }

    /// <summary>
    /// Resolve the active coupling gains for the current mode and sync-tightness preset.
    /// Agentic always uses its own weak gains; Adaptive uses the preset (or Custom = inspector values).
    /// </summary>
    private void GetEffectiveGains(out float effAlpha, out float effBeta)
    {
        if (mode == EnsembleMode.Agentic)
        {
            effAlpha = agenticAlpha;
            effBeta  = agenticBeta;
            return;
        }

        switch (syncTightness)
        {
            case SyncTightness.MusicalLoose: effAlpha = 0.30f; effBeta = 0.15f; break;
            case SyncTightness.MusicalTight: effAlpha = 0.60f; effBeta = 0.35f; break;
            case SyncTightness.SnapToBeat:   effAlpha = 1.00f; effBeta = 1.00f; break;
            case SyncTightness.Custom:
            default:                         effAlpha = alpha; effBeta = beta;  break;
        }
    }

    private void ApplyCorrections(float now)
    {
        GetEffectiveGains(out float effectiveAlpha, out float effectiveBeta);

        // The blink layer is a beat-level abstraction kept for logging; it deliberately does
        // NOT consult the ARME model (the model tracks the musicians' real note onsets now,
        // see RegisterNoteWithModel). Blinks correct toward the user's tap + measured IOI.
        for (int i = 1; i < TotalPlayers; i++)
        {
            float idealNext = now + measuredUserIOI + phaseOffsetPerPlayer * i;

            float oldNext = _nextBlinkTime[i];
            float oldIOI = _virtualIOI[i];

            // Phase correction: pull next-blink time toward ideal by alpha fraction of the error.
            float phaseError = _nextBlinkTime[i] - idealNext;
            _nextBlinkTime[i] -= effectiveAlpha * phaseError;

            // Period correction: blend internal IOI toward user's measured IOI by beta.
            _virtualIOI[i] = (1f - effectiveBeta) * _virtualIOI[i] + effectiveBeta * measuredUserIOI;

            Log($"  -> P{i} target={idealNext:F3}s (user tap + measured IOI)  phaseErr={phaseError:+0.000;-0.000}s  " +
                $"nextBlink {oldNext:F3} -> {_nextBlinkTime[i]:F3} (Δ={_nextBlinkTime[i] - oldNext:+0.000;-0.000})  " +
                $"IOI {oldIOI:F3} -> {_virtualIOI[i]:F3} (target {measuredUserIOI:F3}, α={effectiveAlpha:F2}, β={effectiveBeta:F2})");
        }
    }

    private void DetectIdleRevert()
    {
        // Only Adaptive mode has a Random<->Adaptive sub-state to revert.
        if (mode != EnsembleMode.Adaptive || !adaptiveMode) return;

        float now = Time.time;
        float idleSec = (lastUserTapTime > 0f) ? (now - lastUserTapTime) : float.PositiveInfinity;
        float threshold = (measuredUserIOI > 0f) ? idleTimeoutFactor * measuredUserIOI : fallbackIdleSeconds;

        if (idleSec > threshold)
        {
            adaptiveMode = false;
            OnModeChange?.Invoke(new UserStudyModeEvent(now, mode, false,
                $"Reverted to Random: idle {idleSec:F2}s > threshold {threshold:F2}s"));
            Log($"<<< User idle for {idleSec:F2}s (> {threshold:F2}s) — reverting to RANDOM mode");
            // Re-randomise virtual IOIs so future blinks scatter again.
            for (int i = 1; i < TotalPlayers; i++)
                _virtualIOI[i] = Random.Range(minRandomInterval, maxRandomInterval);
        }
    }

    private void DriveVirtualBlinks()
    {
        float now = Time.time;
        for (int i = 1; i < TotalPlayers; i++)
        {
            if (now < _nextBlinkTime[i]) continue;

            float onsetTime = _nextBlinkTime[i];

            switch (mode)
            {
                case EnsembleMode.NonAdaptive:
                    ScheduleNonAdaptiveBlink(i, onsetTime);
                    break;

                case EnsembleMode.Agentic:
                    ScheduleAgenticBlink(i, onsetTime);
                    break;

                case EnsembleMode.Adaptive:
                default:
                    ScheduleAdaptiveBlink(i, onsetTime, now);
                    break;
            }
        }
    }

    private void ScheduleAdaptiveBlink(int i, float onsetTime, float now)
    {
        if (adaptiveMode)
        {
            _onsetCounts[i]++;
            _nextBlinkTime[i] = onsetTime + _virtualIOI[i];
            Log($"P{i} BLINK (adaptive) @ t={onsetTime:F3}s  IOI={_virtualIOI[i]:F3}s  next={_nextBlinkTime[i]:F3}s");
            OnVirtualBlink?.Invoke(new UserStudyBlinkEvent(onsetTime, i, _onsetCounts[i], _virtualIOI[i], _nextBlinkTime[i], "adaptive"));
        }
        else
        {
            // Pre-tap "Random" sub-state: don't pollute the model.
            float nextInterval = Random.Range(minRandomInterval, maxRandomInterval);
            _nextBlinkTime[i] = now + nextInterval;
            Log($"P{i} BLINK (random) @ t={onsetTime:F3}s  nextInterval={nextInterval:F3}s  next={_nextBlinkTime[i]:F3}s");
            OnVirtualBlink?.Invoke(new UserStudyBlinkEvent(onsetTime, i, _onsetCounts[i], nextInterval, _nextBlinkTime[i], "random"));
        }
    }

    private void ScheduleNonAdaptiveBlink(int i, float onsetTime)
    {
        // Strict metronome — fixed IOI, no noise, no model coupling.
        float ioi = _virtualBaselineIOI[i];
        _nextBlinkTime[i] = onsetTime + ioi;
        Log($"P{i} BLINK (non-adaptive) @ t={onsetTime:F3}s  IOI={ioi:F3}s  next={_nextBlinkTime[i]:F3}s");
        OnVirtualBlink?.Invoke(new UserStudyBlinkEvent(onsetTime, i, _onsetCounts[i], ioi, _nextBlinkTime[i], "non-adaptive"));
    }

    private void ScheduleAgenticBlink(int i, float onsetTime)
    {
        // Pull current IOI back toward the agent's own baseline so it doesn't drift forever.
        float baseline = _virtualBaselineIOI[i];
        _virtualIOI[i] = (1f - agenticBaselinePull) * _virtualIOI[i] + agenticBaselinePull * baseline;

        // Per-blink Gaussian timing jitter — the "noise" that makes agents feel alive.
        float jitter = NextGaussian() * agenticTimingNoise;
        float interval = Mathf.Max(0.05f, _virtualIOI[i] + jitter);

        // Blinks are logging-only; the ARME model is fed the real note onsets instead
        // (see RegisterNoteWithModel), so blinks must not pollute it.
        _onsetCounts[i]++;

        _nextBlinkTime[i] = onsetTime + interval;
        Log($"P{i} BLINK (agentic) @ t={onsetTime:F3}s  IOI={_virtualIOI[i]:F3}s  jitter={jitter:+0.000;-0.000}s  interval={interval:F3}s  next={_nextBlinkTime[i]:F3}s");
        OnVirtualBlink?.Invoke(new UserStudyBlinkEvent(onsetTime, i, _onsetCounts[i], interval, _nextBlinkTime[i], "agentic"));
    }

    /// <summary>Box-Muller standard-normal sample (mean 0, stddev 1).</summary>
    private float NextGaussian()
    {
        if (_haveSpawnGaussian)
        {
            _haveSpawnGaussian = false;
            return _spareGaussian;
        }
        float u1, u2;
        do { u1 = Random.value; } while (u1 <= float.Epsilon);
        u2 = Random.value;
        float mag = Mathf.Sqrt(-2f * Mathf.Log(u1));
        float z0 = mag * Mathf.Cos(2f * Mathf.PI * u2);
        _spareGaussian = mag * Mathf.Sin(2f * Mathf.PI * u2);
        _haveSpawnGaussian = true;
        return z0;
    }

    /// <summary>
    /// Drive each video part with its OWN speed so the ensemble can start slightly out of sync
    /// (per-player tempo + phase offset) and then converge gradually once the user taps:
    ///   • A shared "convergence" value ramps 0→1 while the user is tapping (over convergeSeconds),
    ///     and back to 0 when idle. Non-Adaptive never converges; Agentic caps below 1 so it stays
    ///     a bit loose; Adaptive reaches full lock.
    ///   • Violin 1 (the leader) sits OUTSIDE the ensemble mechanics: it is NOTE-TRIGGERED by
    ///     the taps (each tap fires its next note, see <see cref="LeaderTriggerSpeed"/>).
    ///   • Every other part synchronises to VIOLIN 1 on three layers: tempo (its SMOOTHED
    ///     average speed, so the trigger's stop-start motion is inaudible), timing (the ARME
    ///     model's predicted next onsets fine-tune each part, bounded so it stays musical) and
    ///     phase (a gentle cohesion nudge onto Violin 1's playback position). The chain is
    ///     taps → Violin 1 → the others. Audio pitch follows per part so sound and picture
    ///     stay together.
    /// </summary>
    private void UpdateEnsembleTempo()
    {
        if (_videosPending || videoParts.Count == 0)
            return;

        bool engaged = VideosEngaged;   // recent tap + measured BPM, and not Non-Adaptive

        // How far this mode is allowed to lock in.
        float maxConvergence = mode == EnsembleMode.NonAdaptive ? 0f
                             : mode == EnsembleMode.Agentic      ? agenticMaxConvergence
                                                                 : 1f;
        float convTarget = engaged ? maxConvergence : 0f;
        float convStep = convergeSeconds > 0.01f ? Time.deltaTime / convergeSeconds : 1f;
        _convergence = Mathf.MoveTowards(_convergence, convTarget, convStep);

        // ── Violin 1: controlled by the user's tapping (note-triggered) ──
        VideoPart leader = (_leader?.video != null && _leader.video.isPrepared) ? _leader : null;

        float sumSpeed = 0f;
        int speedCount = 0;
        float leadTime = -1f;

        if (leader != null)
        {
            // EACH USER TAP FIRES VIOLIN 1'S NEXT NOTE (see LeaderTriggerSpeed) — in EVERY
            // condition. Violin 1 sits outside the ensemble mechanics (no start offset, no
            // convergence); it eases twice as fast as the ensemble smoothing so taps register
            // promptly — the followers stay on the gentler rate.
            float target = LeaderTriggerSpeed(leader);
            leader.baseSpeed = Mathf.Lerp(leader.baseSpeed, target, Time.deltaTime * speedSmoothRate * 2f);

            float leaderSpeed = leader.baseSpeed;
            if (mode == EnsembleMode.Agentic)
            {
                // Mutual correction (Agentic only): Violin 1 is gently nudged toward the other
                // musicians' average playback position, so everyone corrects toward everyone.
                // Small — the taps remain clearly in charge.
                float sum = 0f; int n = 0;
                foreach (var p in videoParts)
                    if (p?.video != null && p.video.isPrepared && p != leader) { sum += (float)p.video.time; n++; }
                if (n > 0)
                {
                    float phaseError = (float)leader.video.time - sum / n;   // ahead of ensemble = +
                    // Capped below the resume threshold so the ensemble can never drag a
                    // WAITING Violin 1 back into playing without a tap.
                    leaderSpeed -= Mathf.Clamp(agenticLeaderCohesion * phaseError, -PauseSpeed, PauseSpeed);
                }
            }

            float appliedLeader = ApplyPartSpeed(leader, leaderSpeed, 0f);
            sumSpeed += appliedLeader;
            speedCount++;
            leadTime = (float)leader.video.time;

            // The others follow a SMOOTHED Violin 1 tempo — its average progress over the last
            // ~leaderTempoAveraging seconds — never its instantaneous sprint/creep, so the
            // trigger's stop-start motion is absorbed and their playing stays natural. If the
            // user stops tapping, this average glides down with the creeping Violin 1.
            _leaderSpeedAvg = Mathf.Lerp(_leaderSpeedAvg, appliedLeader,
                1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.2f, leaderTempoAveraging)));
        }

        // Where the ARME timing model expects each player's NEXT note onset, given the real
        // played onsets registered so far (see RegisterNoteWithModel). Needs a few Violin 1
        // notes before predictions are meaningful.
        float[] notePredictions = null;
        if (_model != null && _playbackActive && mode != EnsembleMode.NonAdaptive && _currentBeatIndex >= 2)
        {
            try { notePredictions = _model.PredictNextOnsets(); }
            catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] PredictNextOnsets failed: {ex.Message}"); }
        }

        // ── The others: per-condition behaviour ──
        // Adaptive/Agentic: synchronise to VIOLIN 1 — tempo from its smoothed speed (offsets
        // shrinking as they converge), timing from the model's predicted next onsets, phase
        // from a gentle cohesion nudge onto Violin 1's position.
        // Non-Adaptive: play normally TOGETHER at the fixed native tempo (no offsets, no
        // model, no cohesion) and make no attempt to synchronise with Violin 1.
        float followBase = mode == EnsembleMode.NonAdaptive ? normalPlaybackRate : _leaderSpeedAvg;

        foreach (var part in videoParts)
        {
            if (part?.video == null || !part.video.isPrepared || part == leader)
                continue;

            float offset = mode == EnsembleMode.NonAdaptive
                ? 0f   // fixed trio: exactly the native tempo, so they stay together
                : part.tempoOffset * (1f - _convergence);
            float baseTarget = followBase * (1f + offset);

            // Timing-model steering: aim this part's next unplayed note at the model's
            // predicted onset time for its player slot. Bounded around the ensemble tempo so
            // a degenerate prediction can never send a part rushing or crawling away — the
            // correction stays a gentle, musical adjustment.
            if (notePredictions != null && part.modelSlot > 0 && part.modelSlot < notePredictions.Length &&
                !part.finished && part.onsets != null && part.nextOnsetIdx < part.onsets.Count)
            {
                float wallGap = notePredictions[part.modelSlot] - Time.time;              // wall s until predicted note
                float srcGap  = part.onsets[part.nextOnsetIdx] - part.audioElapsed;       // source s until actual next note
                if (wallGap > 0.05f && wallGap < 4f && srcGap > 0.001f)
                {
                    float required = Mathf.Clamp(srcGap / wallGap, baseTarget * 0.7f, baseTarget * 1.3f);
                    baseTarget = Mathf.Lerp(baseTarget, required, _convergence);
                }
            }

            part.baseSpeed = Mathf.Lerp(part.baseSpeed, baseTarget, Time.deltaTime * speedSmoothRate);

            // Phase alignment is applied ON TOP of the smoothed tempo and immediately (not smoothed),
            // so a part that's ahead/behind Violin 1 actively catches up instead of asymptoting.
            float speed = part.baseSpeed;
            if (mode != EnsembleMode.NonAdaptive && leadTime >= 0f)
            {
                float phaseError = (float)part.video.time - leadTime;   // ahead of Violin 1 = positive
                speed -= phaseCohesionGain * _convergence * phaseError;
            }

            sumSpeed += ApplyPartSpeed(part, speed, LowSpeedFloor);
            speedCount++;
        }

        _ensembleSpeed = speedCount > 0 ? sumSpeed / speedCount : 1f;   // average, for the HUD
    }

    /// <summary>
    /// Lower speed clamp for the follower parts. In Adaptive/Agentic they may ease all the
    /// way to a stop so the ensemble settles together with a waiting Violin 1 instead of
    /// running ahead. Non-Adaptive keeps the plain clamp (the fixed trio never waits).
    /// </summary>
    private float LowSpeedFloor => mode == EnsembleMode.NonAdaptive
        ? minPlaybackSpeed
        : 0f;

    /// <summary>
    /// The tap-triggered speed for Violin 1 as ONE continuous motion (no sprint-then-freeze):
    /// each tap anchors a glide from the current playhead to just short of the following note,
    /// timed to arrive exactly at the next expected tap. A proportional controller (feed-forward
    /// slope + <see cref="leaderCatchUpGain"/> × position error) tracks that path, so:
    ///   • steady tapping → smooth playback at the tapped tempo, each note landing on its tap;
    ///   • an early tap → a gentle, bounded catch-up swell;
    ///   • an overdue tap → the feed-forward fades and the speed glides to a genuine STOP
    ///     just before the note (<see cref="ApplyPartSpeed"/> then pauses video + audio);
    ///     a very late tap swells back in over ~100 ms (fast leader easing + bounded gain),
    ///     so the resume never jumps.
    /// </summary>
    private float LeaderTriggerSpeed(VideoPart leader)
    {
        // No take running / notes exhausted → plain tap-tempo following as a fallback.
        if (!_playbackActive || leader.finished || leader.onsets == null ||
            leader.nextOnsetIdx >= leader.onsets.Count)
        {
            float ratio = (VideosEngaged && currentUserBPM > 1e-3f)
                ? Mathf.Clamp(currentUserBPM / naturalBPM, minPlaybackSpeed, maxPlaybackSpeed)
                : 1f;
            return ratio * normalPlaybackRate;
        }

        float ioi = measuredUserIOI > 0f ? measuredUserIOI : 60f / Mathf.Max(1f, naturalBPM);

        // The glide is timed to land slightly AFTER the expected tap (lag margin). During
        // steady tapping the next tap therefore re-anchors it mid-glide, while it is still
        // moving at full slope — the speed never dips into a wait around the taps. Only a
        // genuinely late or missing tap lets the glide run out.
        float glideTime = Mathf.Max(0.05f, ioi) * (1f + ScheduleLagMargin);

        // Where the schedule wants the playhead right now: gliding from the last anchor to
        // just short of the target note. If the stale target was already crossed (notes
        // self-fired while idle) the position error goes ≤ 0 and the speed settles at the
        // creep until the next tap re-anchors.
        int targetIdx = Mathf.Min(_schedTargetIdx, leader.onsets.Count - 1);
        float armPoint = Mathf.Max(_schedAnchorPos, leader.onsets[targetIdx] - armWindowSeconds);
        float frac = Mathf.Clamp01((Time.time - _schedAnchorTime) / glideTime);
        float targetPos = Mathf.Lerp(_schedAnchorPos, armPoint, frac);

        // Feed-forward = the glide's own slope, faded out smoothly over the glide's last
        // stretch (instead of cutting to zero) so a genuine wait is entered gradually. The
        // playhead still never pushes across a waiting note on its own — only the creep does.
        float slope = (armPoint - _schedAnchorPos) / glideTime;
        float feedForward = slope * Mathf.Clamp01((1f - frac) / FeedForwardFadeFrac);
        float posError = targetPos - leader.audioElapsed;   // + = behind schedule → speed up

        // Floor 0: with the feed-forward faded and the position error closed, the controller
        // glides to a genuine standstill at the hold point — ApplyPartSpeed then truly pauses
        // the part until the next tap swells it back in.
        float target = feedForward + leaderCatchUpGain * posError;

        // Overdue: approach the waiting point at a walking pace at most — no dash to the
        // doorstep the moment the user stops tapping.
        if (frac >= 1f) target = Mathf.Min(target, minPlaybackSpeed);

        return Mathf.Clamp(target, 0f, maxPlaybackSpeed);
    }

    // Glide tuning: the schedule lands 15% of a tap interval after the expected tap so steady
    // tapping re-anchors mid-glide (no pre-tap slowdown), and the feed-forward fades over the
    // glide's last 15% so a real wait is entered gradually rather than with a step.
    private const float ScheduleLagMargin = 0.15f;
    private const float FeedForwardFadeFrac = 0.15f;

    /// <summary>
    /// Clamp and apply a part's playback speed to its video + pitch-preserving audio, advance its
    /// audio playhead, and fire a note event for every source onset the playhead crossed.
    /// Returns the applied (clamped) speed.
    /// </summary>
    // Below PauseSpeed a part truly pauses (video + native audio); it must climb back above
    // ResumeSpeed to run again. The hysteresis gap prevents pause/resume chatter, and the
    // hard native Stop() during waits is what keeps the stretcher from silently consuming
    // audio at a clamped minimum rate — the cause of "the audio ends before the videos".
    private const float PauseSpeed  = 0.15f;
    private const float ResumeSpeed = 0.20f;

    private float ApplyPartSpeed(VideoPart part, float speed, float minAllowed)
    {
        float applied = Mathf.Clamp(speed, minAllowed, maxPlaybackSpeed);
        part.speed = applied;

        if (_playbackActive && !part.finished)
        {
            // Waiting = a TRUE pause. Commanding the stretcher near-zero speeds instead would
            // leave it consuming source audio at whatever floor it internally honours, letting
            // the sound race ahead of the picture. Stop() keeps the read position, so the next
            // tap resumes seamlessly right where the music paused.
            bool run = applied > (part.suspended ? ResumeSpeed : PauseSpeed);
            if (!run && !part.suspended)
            {
                part.suspended = true;
                if (part.video != null)    part.video.Pause();
                if (part.playback != null) part.playback.StopPlayback();
            }
            else if (run && part.suspended)
            {
                part.suspended = false;
                if (part.video != null)    part.video.Play();
                if (part.playback != null) part.playback.StartPlayback();
            }

            if (part.suspended)
                return applied;   // frozen: no progress, no speed pushes to a stopped engine

            // Track how much source audio the stretcher has consumed so the take can end with
            // the audio (see CheckAudioCompletion). unscaledDeltaTime: the audio thread runs
            // on WALL time and Time.deltaTime is clamped during frame hitches — the clamped
            // value undercounts and made takes end after the audio already had.
            part.audioElapsed += applied * Time.unscaledDeltaTime;
            EmitCrossedNotes(part, applied);
        }

        part.video.playbackSpeed = applied;
        if (part.playback != null)
            part.playback.SetSpeed(applied);   // pitch-preserved time-stretch tracks the video

        return applied;
    }

    /// <summary>
    /// Fire <see cref="OnMusicianNote"/> for every source onset the part's audio playhead crossed
    /// this frame. The note's game time is back-dated within the frame by how far past the onset
    /// the playhead landed, so logged note times don't quantise to frame boundaries.
    /// </summary>
    private void EmitCrossedNotes(VideoPart part, float speed)
    {
        if (part.onsets == null) return;
        while (part.nextOnsetIdx < part.onsets.Count && part.audioElapsed >= part.onsets[part.nextOnsetIdx])
        {
            float sourceTime = part.onsets[part.nextOnsetIdx];
            float overshoot = speed > 1e-4f ? (part.audioElapsed - sourceTime) / speed : 0f;
            float noteTime = Time.time - overshoot;
            part.nextOnsetIdx++;   // nextOnsetIdx is now this note's 1-based number

            OnMusicianNote?.Invoke(new UserStudyNoteEvent(
                noteTime, videoParts.IndexOf(part), part.label,
                part.nextOnsetIdx, sourceTime, speed, part == _leader));
            RegisterNoteWithModel(part, noteTime);
        }
    }

    /// <summary>
    /// Feed a REAL played note onset into the ARME timing model. Violin 1 (slot 0) is the
    /// reference: each of its tap-fired notes opens a new shared beat slot and flushes every
    /// follower's most recent note into that same slot (only the latest note counts for a
    /// beat). Followers just park their note until the next VN1 beat. The model's per-player
    /// predictions then steer the followers in UpdateEnsembleTempo.
    /// </summary>
    private void RegisterNoteWithModel(VideoPart part, float noteTime)
    {
        if (_model == null || part.modelSlot < 0) return;

        if (part.modelSlot == 0)
        {
            _currentBeatIndex++;
            try { _model.RegisterOnsetWithIndex(0, noteTime, _currentBeatIndex); }
            catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] VN1 note register failed: {ex.Message}"); }

            foreach (var p in videoParts)
            {
                if (p == null || p.modelSlot <= 0 || p.pendingNoteTime < 0f) continue;
                try { _model.RegisterOnsetWithIndex(p.modelSlot, p.pendingNoteTime, _currentBeatIndex); }
                catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] P{p.modelSlot} note register failed: {ex.Message}"); }
                p.pendingNoteTime = -1f;
            }
        }
        else
        {
            part.pendingNoteTime = noteTime;
        }
    }

    private void Log(string msg)
    {
        if (verboseLogging) Debug.Log("[UserStudy] " + msg);
    }

    // ── Experiment-UI driven API ─────────────────────────────────────────

    /// <summary>Switch the experimental condition and re-seed the virtuals for it.</summary>
    public void SetMode(EnsembleMode newMode)
    {
        mode = newMode;
        InitVirtualsForMode();
        Log($"Mode set to {mode}.");
    }

    /// <summary>
    /// Switch the sensory modality. In AudioOnly the musician videos are hidden (their
    /// display renderers are disabled) while the VideoPlayers keep running — they are still
    /// the tempo/phase reference in <see cref="UpdateEnsembleTempo"/> and the audio comes from
    /// the native pitch-preserving playback, not the video. AudioVisual re-shows them.
    /// </summary>
    public void SetModality(Modality newModality)
    {
        modality = newModality;
        ApplyModalityVisibility();
        Log($"Modality set to {modality}.");
    }

    /// <summary>Show/hide each part's display renderer according to the active modality.</summary>
    private void ApplyModalityVisibility()
    {
        bool visible = modality == Modality.AudioVisual;
        foreach (var part in videoParts)
        {
            if (part != null && part.displayRenderer != null)
                part.displayRenderer.enabled = visible;
        }
    }

    /// <summary>
    /// Reset the timing model + tap counters for a fresh take. The experiment UI calls this at
    /// the START of the count-in so the taps the user makes during the count-in accumulate and
    /// carry into playback (their tempo is ready when the videos begin).
    /// </summary>
    public void PrepareForTake() => ResetModelAndCounters();

    /// <summary>
    /// (Re)start every video part from the top, keeping the tempo already established during the
    /// count-in. Honours the deferred prepared-check so the pictures begin together.
    /// </summary>
    public void BeginPlayback()
    {
        _videosPending = true;
        _videoArmTime = Time.time;
        Log("BeginPlayback requested.");
    }

    /// <summary>
    /// Stop the current take and pause the videos (used by the experiment UI's End button when
    /// returning to the welcome screen). The next BeginPlayback restarts cleanly from the top.
    /// </summary>
    public void StopPlayback()
    {
        _playbackActive = false;
        _videosPending = false;
        AcceptTaps = false;

        foreach (var part in videoParts)
        {
            if (part == null) continue;
            if (part.video != null) part.video.Pause();
            if (part.playback != null) part.playback.StopPlayback();
        }

        Log("StopPlayback requested.");
    }

    /// <summary>Reset the timing model + user-tap state (without touching the videos).</summary>
    private void ResetModelAndCounters()
    {
        if (_model != null)
        {
            try { _model.Reset(); _model.CreateNewParameters(); }
            catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] Reset failed: {ex.Message}"); }
        }

        userTapCount = 0;
        currentUserBPM = 0f;
        measuredUserIOI = 0f;
        lastUserTapTime = 0f;
        _previousUserTapTime = -1f;
        _currentBeatIndex = 0;
        _ensembleSpeed = 1f;
        _schedAnchorPos = 0f;
        _schedAnchorTime = Time.time;
        _schedTargetIdx = 0;

        for (int i = 0; i < TotalPlayers; i++)
            _onsetCounts[i] = 0;

        foreach (var part in videoParts)
            if (part != null) part.pendingNoteTime = -1f;

        InitVirtualsForMode();
    }

    [Button("Reset Model & Restart")]
    private void ResetAll()
    {
        if (_model == null) return;

        try
        {
            _model.Reset();
            _model.CreateNewParameters();
        }
        catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] Reset failed: {ex.Message}"); }

        userTapCount = 0;
        currentUserBPM = 0f;
        measuredUserIOI = 0f;
        lastUserTapTime = 0f;
        _previousUserTapTime = -1f;
        _currentBeatIndex = 0;

        for (int i = 0; i < TotalPlayers; i++)
            _onsetCounts[i] = 0;

        // Rewind every video + audio part back to the top.
        _ensembleSpeed = 1f;
        foreach (var part in videoParts)
        {
            if (part == null)
                continue;
            if (part.playback != null)
            {
                part.playback.RestartFromBeginning();
                part.playback.SetSpeed(1f);
            }
            if (part.video != null)
            {
                part.video.playbackSpeed = 1f;
                part.video.time = 0.0;
                part.video.Play();
            }
            part.speed = 1f;
            part.suspended = false;
            part.audioElapsed = 0f;
            part.nextOnsetIdx = 0;
            part.pendingNoteTime = -1f;
        }

        InitVirtualsForMode();
        Log($"RESET complete. Mode={mode}.");
    }

    void OnDestroy()
    {
        if (tapDetection != null) tapDetection.OnHardwareTap -= HandleHardwareTap;

        foreach (var part in videoParts)
        {
            if (part == null)
                continue;
            if (part.video != null)
            {
                part.video.loopPointReached -= OnPartReachedEnd;
                part.video.Pause();
            }
            if (part.ownedRT != null)
            {
                part.ownedRT.Release();
                Destroy(part.ownedRT);
            }
            if (part.matInstance != null)
                Destroy(part.matInstance);
        }
        _model?.Dispose();
    }

#if UNITY_EDITOR
    /// <summary>
    /// Populate <see cref="videoParts"/> from the VideoPlayers in the open scene, matching
    /// each clip to its WAV and -CRNNManual onset file by name (strip the _TB suffix) plus
    /// its VideoPlayerPlane display renderer. Run from the component's context menu (gear
    /// icon) in Edit mode. Lets the study run without an ARMEEnsembleSyncPlayer in the scene.
    /// </summary>
    [ContextMenu("Auto-Wire Video Parts From Scene Videos")]
    private void AutoWireVideoPartsFromSceneVideos()
    {
        var videoPlayers = FindObjectsByType<VideoPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (videoPlayers.Length == 0)
        {
            Debug.LogWarning("[UserStudy] Auto-wire: no VideoPlayer found in the scene.");
            return;
        }

        videoParts.Clear();
        var seen = new HashSet<string>();

        foreach (var vp in videoPlayers)
        {
            if (vp.clip == null)
                continue;

            string baseName = ARMEEditorAssetUtil.StripTopBottomSuffix(vp.clip.name);

            if (!seen.Add(baseName))
                continue; // skip a duplicate VideoPlayer pointing at the same piece

            AudioClip clip = FindAsset<AudioClip>(baseName, AudioFolder);
            TextAsset onset = FindAsset<TextAsset>(baseName + "-CRNNManual", AudioFolder);

            if (clip == null)
                Debug.LogWarning($"[UserStudy] Auto-wire: no audio clip named '{baseName}' in {AudioFolder}.");

            // Match the display plane by name: TopBottomVideoRender (N) -> VideoPlayerPlane (N).
            Renderer planeRenderer = null;
            string planeName = vp.gameObject.name.Replace("TopBottomVideoRender", "VideoPlayerPlane");
            var planeGO = GameObject.Find(planeName);
            if (planeGO != null)
                planeRenderer = planeGO.GetComponent<Renderer>();

            videoParts.Add(new VideoPart
            {
                label = baseName,
                video = vp,
                audioClip = clip,
                onsetFile = onset,
                displayRenderer = planeRenderer
            });
        }

        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log($"[UserStudy] Auto-wire complete: {videoParts.Count} video part(s).");
    }

    private static T FindAsset<T>(string assetName, string folder) where T : UnityEngine.Object
        => ARMEEditorAssetUtil.FindAsset<T>(assetName, folder);
#endif
}
