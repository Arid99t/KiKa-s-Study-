using System.Collections.Generic;
using System.Text;
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
/// Experimental condition for the ensemble.
///   Adaptive    — Wing-Kristofferson coupling: virtuals follow the user.
///   NonAdaptive — Fixed metronome at <c>fixedBPM</c>; user taps are ignored.
///   Agentic     — Semi-independent agents: each has its own slightly drifting
///                 tempo with per-blink jitter and only mild responsiveness to
///                 the user, so they behave like interacting partners rather
///                 than followers or a metronome.
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
    [Tooltip("Lower clamp on the video playback speed while following the user (fraction of native speed). No matter how slowly you tap, the video won't go below this.")]
    [SerializeField, Range(0.1f, 1f)] private float minPlaybackSpeed = 0.5f;

    [BoxGroup("Videos")]
    [Tooltip("Upper clamp on the video playback speed while following the user (multiple of native speed). No matter how fast you tap, the video won't go above this.")]
    [SerializeField, Range(1f, 4f)] private float maxPlaybackSpeed = 1.2f;

    [BoxGroup("Videos")]
    [Tooltip("How quickly the videos ease to a new tempo when your tapping speed changes (higher = snappier, lower = smoother). Ramping avoids jolting the decoder and audio on every tap.")]
    [SerializeField, Range(1f, 20f)] private float speedSmoothRate = 6f;

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

    // ARME expects ensemble onsets to share a beat index across players. We increment a
    // shared beat counter on each user tap and register the user + each virtual whose
    // most recent blink is "pending" under that same index. Without this the per-player
    // onset counts diverge as virtuals freewheel and ARME's predictions degenerate to
    // -900 sentinels or past-time values.
    private int _currentBeatIndex;
    private float[] _pendingBlinkTime;    // virtual's most recent blink time awaiting a beat slot (-1 = none)

    // ── Experiment flow (driven by ARMEUserStudyExperimentUI) ────────────
    private bool _externalControl;   // an experiment UI owns when playback starts/repeats
    private bool _playbackActive;    // a take is currently playing (for end detection)
    private int  _finishedParts;     // how many parts have reached their end this take

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

    // ── Data Logging Events ──────────────────────────────────────────────
    public event System.Action<UserStudyTapEvent>   OnUserTap;
    public event System.Action<UserStudyBlinkEvent> OnVirtualBlink;
    public event System.Action<UserStudyModeEvent>  OnModeChange;

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
        _pendingBlinkTime = new float[TotalPlayers];
        for (int i = 0; i < TotalPlayers; i++) _pendingBlinkTime[i] = -1f;
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
            if (part.audioElapsed >= part.audioClip.length)
                MarkPartFinished(part);
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
        _playbackActive = true;
        OnPlaybackStarted?.Invoke();

        foreach (var part in videoParts)
        {
            if (part == null)
                continue;

            // Per-player starting tempo offset drives the loose→converged feel. Audio and video
            // both start from the top so each part's sound and picture stay aligned (the native
            // stretcher has no per-sample start offset), so the spread is in tempo, not phase.
            part.tempoOffset = Random.Range(-startTempoSpread, startTempoSpread);
            float startSpeed = normalPlaybackRate * (1f + part.tempoOffset);

            part.finished = false;      // fresh take: nothing has ended yet
            part.audioElapsed = 0f;     // audio playhead back at the start

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
    /// True while the user is actively tapping and the ensemble should follow their tempo
    /// (Adaptive or Agentic). Driven directly by a recent tap + a measured tempo rather than
    /// the adaptiveMode sub-state, so the videos reliably speed up / slow down with the user.
    /// Non-Adaptive never engages (videos play at their native tempo).
    /// </summary>
    private bool VideosEngaged
    {
        get
        {
            if (mode == EnsembleMode.NonAdaptive) return false;
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

        // Register user onset to the model using an explicit, monotonically increasing
        // beat index. All players' onsets for the same beat share this index so ARME
        // can correlate them as ensemble partners.
        _currentBeatIndex++;
        if (_model != null)
        {
            try
            {
                _model.RegisterOnsetWithIndex(UserPlayerIndex, t, _currentBeatIndex);
                _onsetCounts[UserPlayerIndex]++;
            }
            catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] User onset register failed: {ex.Message}"); }
        }

        // Flush any pending virtual blinks into this beat slot. Virtuals fire on their
        // own clock between user taps; we record the blink time and only commit it to
        // ARME here, paired with the user's beat index.
        for (int i = 1; i < TotalPlayers; i++)
        {
            if (_pendingBlinkTime[i] < 0f) continue;
            if (_model != null)
            {
                try
                {
                    _model.RegisterOnsetWithIndex(i, _pendingBlinkTime[i], _currentBeatIndex);
                    _onsetCounts[i]++;
                }
                catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] P{i} flush register failed: {ex.Message}"); }
            }
            _pendingBlinkTime[i] = -1f;
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

        // Non-Adaptive mode ignores user taps for timing — they're recorded only for logging/model state.
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

        // Get ARME's predictions; these become the phase-correction targets. When the native
        // model is unavailable, predictions stay null and the per-player fallback below
        // (user tap + measured IOI) drives the blinks instead.
        float[] predictions = null;
        if (_model != null)
        {
            try { predictions = _model.PredictNextOnsets(); }
            catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] PredictNextOnsets failed: {ex.Message}"); }
        }

        if (predictions != null)
        {
            var sb = new StringBuilder();
            sb.Append("PREDICTIONS @ t=").Append(now.ToString("F3")).Append("s   ");
            for (int i = 0; i < predictions.Length; i++)
            {
                sb.Append('P').Append(i).Append('=').Append(predictions[i].ToString("F3"))
                  .Append("s (Δ=").Append((predictions[i] - now).ToString("+0.000;-0.000;0.000")).Append(") ");
            }
            Log(sb.ToString());
        }

        for (int i = 1; i < TotalPlayers; i++)
        {
            float offset = phaseOffsetPerPlayer * i;
            float idealNext;
            string targetSource;

            if (predictions != null && predictions[i] > now)
            {
                idealNext = predictions[i] + offset;
                targetSource = $"ARME predicted={predictions[i]:F3}";
            }
            else
            {
                // Fallback: derive ideal next from user's tap + measured IOI.
                idealNext = now + measuredUserIOI + offset;
                targetSource = "fallback (user tap + measured IOI)";
            }

            float oldNext = _nextBlinkTime[i];
            float oldIOI = _virtualIOI[i];

            // Phase correction: pull next-blink time toward ideal by alpha fraction of the error.
            float phaseError = _nextBlinkTime[i] - idealNext;
            _nextBlinkTime[i] -= effectiveAlpha * phaseError;

            // Period correction: blend internal IOI toward user's measured IOI by beta.
            _virtualIOI[i] = (1f - effectiveBeta) * _virtualIOI[i] + effectiveBeta * measuredUserIOI;

            Log($"  -> P{i} target={idealNext:F3}s ({targetSource})  phaseErr={phaseError:+0.000;-0.000}s  " +
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
            // Re-randomise virtual IOIs so future blinks scatter again, and drop any
            // pending blink registrations (they would belong to a stale beat).
            for (int i = 1; i < TotalPlayers; i++)
            {
                _virtualIOI[i] = Random.Range(minRandomInterval, maxRandomInterval);
                _pendingBlinkTime[i] = -1f;
            }
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
            // Defer ARME registration until the next user tap, where the shared beat
            // slot is known. Overwriting any earlier pending blink is intentional:
            // if the virtual fires faster than the user, only the most recent blink
            // counts for the upcoming beat.
            _pendingBlinkTime[i] = onsetTime;

            _nextBlinkTime[i] = onsetTime + _virtualIOI[i];
            Log($"P{i} BLINK (adaptive) @ t={onsetTime:F3}s  IOI={_virtualIOI[i]:F3}s  next={_nextBlinkTime[i]:F3}s  pendingForBeat={_currentBeatIndex + 1}");
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

        // Register to the model so it still informs predictions for any adapting partners.
        if (_model != null)
        {
            try
            {
                _model.RegisterOnset(i, onsetTime);
                _onsetCounts[i]++;
            }
            catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] P{i} register failed: {ex.Message}"); }
        }

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
    ///   • Each part targets the user's tempo scaled by its (shrinking) tempo offset, plus a
    ///     phase-cohesion nudge that pulls its playback position toward the ensemble centre so the
    ///     quartet tightens up. Audio pitch follows per part so sound and picture stay together.
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

        // Common tempo target from the user's tapping (native 1× until engaged).
        float userRatio = (engaged && currentUserBPM > 1e-3f)
            ? Mathf.Clamp(currentUserBPM / naturalBPM, minPlaybackSpeed, maxPlaybackSpeed)
            : 1f;

        // Phase reference = the first prepared part (the "lead" player). Everyone else aligns
        // their playback position onto the lead, which locks the quartet far more tightly than
        // chasing a moving average that never quite closes.
        float leadTime = -1f;
        foreach (var part in videoParts)
            if (part?.video != null && part.video.isPrepared) { leadTime = (float)part.video.time; break; }

        float sumSpeed = 0f;
        int speedCount = 0;

        foreach (var part in videoParts)
        {
            if (part?.video == null || !part.video.isPrepared)
                continue;

            // Smoothed tempo: the per-player offset shrinks toward 0 as the ensemble converges.
            float offset = part.tempoOffset * (1f - _convergence);
            float baseTarget = userRatio * (1f + offset) * normalPlaybackRate;
            part.baseSpeed = Mathf.Lerp(part.baseSpeed, baseTarget, Time.deltaTime * speedSmoothRate);

            // Phase alignment is applied ON TOP of the smoothed tempo and immediately (not smoothed),
            // so a part that's ahead/behind the lead actively catches up instead of asymptoting.
            float applied = part.baseSpeed;
            if (mode != EnsembleMode.NonAdaptive && leadTime >= 0f)
            {
                float phaseError = (float)part.video.time - leadTime;   // ahead of lead = positive
                applied -= phaseCohesionGain * _convergence * phaseError;
            }

            applied = Mathf.Clamp(applied, minPlaybackSpeed, maxPlaybackSpeed);
            part.speed = applied;

            // Track how much source audio the stretcher has consumed so the take can end with the
            // audio (see CheckAudioCompletion). Only while a take is actually running.
            if (_playbackActive && !part.finished)
                part.audioElapsed += applied * Time.deltaTime;

            part.video.playbackSpeed = applied;
            if (part.playback != null)
                part.playback.SetSpeed(applied);   // pitch-preserved time-stretch tracks the video

            sumSpeed += applied;
            speedCount++;
        }

        _ensembleSpeed = speedCount > 0 ? sumSpeed / speedCount : 1f;   // average, for the HUD
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

        for (int i = 0; i < TotalPlayers; i++)
        {
            _onsetCounts[i] = 0;
            _pendingBlinkTime[i] = -1f;
        }

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
        {
            _onsetCounts[i] = 0;
            _pendingBlinkTime[i] = -1f;
        }

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
