using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using NaughtyAttributes;
using ARMETiming;

/// <summary>Configuration snapshot passed to the data logger at session start.</summary>
public struct SessionConfig
{
    public int numVirtualPlayers;
    public EnsembleMode startMode;
    public float alpha, beta, userIoiSmoothing;
    public float fixedBPM;
    public float agenticIoiVariation, agenticTimingNoise, agenticBaselinePull;
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
/// User study controller for the ARME Timing Model. See <see cref="EnsembleMode"/>
/// for the three experimental conditions.
/// </summary>
public class ARMEUserStudyController : MonoBehaviour
{
    [BoxGroup("Mode")]
    [Tooltip("Experimental condition for this run.")]
    [SerializeField] private EnsembleMode mode = EnsembleMode.Adaptive;

    [BoxGroup("Virtual Players")]
    [Tooltip("How many virtual player objects to spawn.")]
    [SerializeField, Range(1, 15)] private int numVirtualPlayers = 3;

    [BoxGroup("Virtual Players")]
    [SerializeField] private float spacing = 2.5f;

    [BoxGroup("Virtual Players")]
    [SerializeField] private float scale = 1.0f;

    [BoxGroup("Virtual Players")]
    [Tooltip("Where the row of virtual players is centred (world space).")]
    [SerializeField] private Vector3 spawnCentre = new Vector3(0f, 1f, 0f);

    [BoxGroup("Blink Visuals")]
    [SerializeField] private Color baseColor = new Color(0.12f, 0.12f, 0.14f);

    [BoxGroup("Blink Visuals")]
    [SerializeField] private Color blinkColor = new Color(1f, 0.85f, 0.2f);

    [BoxGroup("Blink Visuals")]
    [SerializeField, Range(0.02f, 0.5f)] private float blinkDuration = 0.12f;

    [BoxGroup("Random Mode")]
    [Tooltip("Minimum random blink interval (seconds) when in Random mode (no recent user input).")]
    [SerializeField] private float minRandomInterval = 0.4f;

    [BoxGroup("Random Mode")]
    [Tooltip("Maximum random blink interval (seconds) when in Random mode.")]
    [SerializeField] private float maxRandomInterval = 1.2f;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("Phase-correction gain. Each user tap pulls each virtual's next-blink time toward the model's predicted onset by this fraction of the error. 0 = no phase coupling, 1 = snap to prediction immediately.")]
    [SerializeField, Range(0f, 1f)] private float alpha = 0.30f;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("Period-correction gain. Each user tap blends each virtual's internal IOI toward the user's measured IOI by this fraction. 0 = no tempo adaptation, 1 = match user immediately.")]
    [SerializeField, Range(0f, 1f)] private float beta = 0.15f;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("Smoothing on the user's measured IOI itself. Lower = more reactive, higher = more inertial.")]
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
    private GameObject[] _virtualPlayers;
    private Material[] _materials;
    private float[] _virtualIOI;          // each virtual's current internal IOI
    private float[] _virtualBaselineIOI;  // Agentic: per-virtual baseline tempo
    private float[] _nextBlinkTime;       // when each virtual fires next
    private float[] _flashStartTime;      // most recent blink start time (for visual)
    private int[] _onsetCounts;
    private float _previousUserTapTime = -1f;
    private bool _haveSpawnGaussian;
    private float _spareGaussian;

    // ── Data Logging Events ──────────────────────────────────────────────
    public event System.Action<UserStudyTapEvent>   OnUserTap;
    public event System.Action<UserStudyBlinkEvent> OnVirtualBlink;
    public event System.Action<UserStudyModeEvent>  OnModeChange;

    /// <summary>Returns a configuration snapshot for CSV session metadata.</summary>
    public SessionConfig GetSessionConfig() => new SessionConfig
    {
        numVirtualPlayers   = numVirtualPlayers,
        startMode           = mode,
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
        _model = new SimpleTimingModel(TotalPlayers);
        _model.CreateNewParameters();

        _virtualIOI = new float[TotalPlayers];
        _virtualBaselineIOI = new float[TotalPlayers];
        _nextBlinkTime = new float[TotalPlayers];
        _flashStartTime = new float[TotalPlayers];
        _onsetCounts = new int[TotalPlayers];

        SpawnVirtualPlayers();
        InitVirtualsForMode();

        Log($"Ready. Players: {TotalPlayers} (P0=user, P1..P{numVirtualPlayers}=virtual). " +
            $"Mode={mode}. alpha={alpha:F2}, beta={beta:F2}. Lib v{SimpleTimingModel.GetVersion()}");
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
            _flashStartTime[i] = -1f;
        }
    }

    private void SpawnVirtualPlayers()
    {
        _virtualPlayers = new GameObject[numVirtualPlayers];
        _materials = new Material[numVirtualPlayers];

        float startX = -((numVirtualPlayers - 1) * spacing) * 0.5f;
        Shader shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");

        for (int i = 0; i < numVirtualPlayers; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"VirtualPlayer_{i + 1}";
            go.transform.SetParent(transform, false);
            go.transform.position = spawnCentre + new Vector3(startX + i * spacing, 0f, 0f);
            go.transform.localScale = Vector3.one * scale;

            var rend = go.GetComponent<Renderer>();
            var mat = new Material(shader);
            mat.color = baseColor;
            rend.material = mat;

            _virtualPlayers[i] = go;
            _materials[i] = mat;
        }
    }

    void Update()
    {
        // If the user changed the mode in the inspector at runtime, re-initialise.
        if (mode != currentMode) InitVirtualsForMode();

        HandleUserTap();
        DetectIdleRevert();
        DriveVirtualBlinks();
        UpdateVisuals();
    }

    private void HandleUserTap()
    {
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;

        float t = Time.time;
        userTapCount++;

        // Register user onset to the model.
        try
        {
            _model.RegisterOnset(UserPlayerIndex, t);
            _onsetCounts[UserPlayerIndex]++;
        }
        catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] User onset register failed: {ex.Message}"); }

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
                Log($"  -> P{i} virtualIOI initialised to {measuredUserIOI:F3}s");
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

    private void ApplyCorrections(float now)
    {
        // Pick coupling strength for the active mode.
        float effectiveAlpha = (mode == EnsembleMode.Agentic) ? agenticAlpha : alpha;
        float effectiveBeta  = (mode == EnsembleMode.Agentic) ? agenticBeta  : beta;

        // Get ARME's predictions; these become the phase-correction targets.
        float[] predictions = null;
        try { predictions = _model.PredictNextOnsets(); }
        catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] PredictNextOnsets failed: {ex.Message}"); }

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
            _flashStartTime[i] = onsetTime;

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
            // Register virtual blink to the model so it can refine its predictions.
            try
            {
                _model.RegisterOnset(i, onsetTime);
                _onsetCounts[i]++;
            }
            catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] P{i} register failed: {ex.Message}"); }

            _nextBlinkTime[i] = onsetTime + _virtualIOI[i];
            Log($"P{i} BLINK (adaptive) @ t={onsetTime:F3}s  IOI={_virtualIOI[i]:F3}s  next={_nextBlinkTime[i]:F3}s  totalOnsets={_onsetCounts[i]}");
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
        try
        {
            _model.RegisterOnset(i, onsetTime);
            _onsetCounts[i]++;
        }
        catch (System.Exception ex) { Debug.LogWarning($"[UserStudy] P{i} register failed: {ex.Message}"); }

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

    private void UpdateVisuals()
    {
        float now = Time.time;
        for (int i = 1; i < TotalPlayers; i++)
        {
            float start = _flashStartTime[i];
            bool blinking = start >= 0f && now >= start && now < start + blinkDuration;
            _materials[i - 1].color = blinking ? blinkColor : baseColor;
        }
    }

    private void Log(string msg)
    {
        if (verboseLogging) Debug.Log("[UserStudy] " + msg);
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

        for (int i = 0; i < TotalPlayers; i++) _onsetCounts[i] = 0;
        InitVirtualsForMode();
        Log($"RESET complete. Mode={mode}.");
    }

    void OnDestroy()
    {
        if (_materials != null)
        {
            foreach (var m in _materials)
                if (m != null) Destroy(m);
        }
        _model?.Dispose();
    }
}
