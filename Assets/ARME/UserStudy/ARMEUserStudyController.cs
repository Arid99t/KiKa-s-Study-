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

/// <summary>Payload fired each time a virtual player blinks (here: crosses a recorded onset).</summary>
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
/// Experimental condition for the ensemble. These mirror the AMUSER app's three modes:
///   Adaptive    — every virtual follows the user's tapped tempo (AMUSER "adaptive").
///   NonAdaptive — virtuals ignore the user and play at the recording's own tempo
///                 (AMUSER "non-agentic").
///   Agentic     — virtuals follow mostly the user but partly each other, so they also
///                 stay together (AMUSER "agentic": 0.7·user + 0.3·ensemble).
/// </summary>
public enum EnsembleMode
{
    Adaptive,
    NonAdaptive,
    Agentic
}

/// <summary>
/// Legacy tightness preset (kept for scene/inspector compatibility; no longer drives the
/// AMUSER-style per-onset warp, which follows the user's tempo directly).
/// </summary>
public enum SyncTightness
{
    Custom,
    MusicalLoose,
    MusicalTight,
    SnapToBeat
}

/// <summary>
/// One musician = one virtual player. A <see cref="VideoPlayer"/> (picture) plus its audio
/// (.wav) played through a per-part native time-stretch controller. Each part runs on its OWN
/// clock (its <see cref="VideoPlayer.time"/>) and warps its own recording onset-by-onset so the
/// recorded onsets land on the user's beat — exactly like AMUSER's per-player AVPlayer/AVAudioPlayer.
/// </summary>
[System.Serializable]
public class VideoPart
{
    [Tooltip("Inspector label only.")]
    public string label;

    [Tooltip("VideoPlayer that shows this part's clip. Its playbackSpeed is warped to the model's onsets.")]
    public VideoPlayer video;

    [Tooltip("This part's audio (.wav). Routed through the native pitch-preserving time-stretch controller.")]
    public AudioClip audioClip;

    [Tooltip("Onset file (-CRNNManual): one timestamp per line, used to warp the recording onto the user's beat.")]
    public TextAsset onsetFile;

    [Tooltip("Optional plane/renderer. If set (and Manage Display is on) the part gets its OWN RenderTexture + material instance so parts can never share a texture.")]
    public Renderer displayRenderer;

    // ── Runtime state (not serialized) ───────────────────────────────────
    [System.NonSerialized] public AudioSource audioSource;   // native: DSP keep-alive source; fallback: plays the real clip
    [System.NonSerialized] public ARMEOnsetBasedPlaybackController controller; // pitch-preserving native time-stretch (null in fallback)
    [System.NonSerialized] public bool useNativeAudio;       // true = native stretch; false = AudioSource.pitch fallback
    [System.NonSerialized] public List<float> onsets;
    [System.NonSerialized] public float speed;               // last playbackSpeed written to the video
    [System.NonSerialized] public int onsetIndex;            // current source-onset boundary reached (AMUSER currentOnset)
    [System.NonSerialized] public float currentRate;         // last applied rate (tempo ratio); seeds the ensemble mean
    [System.NonSerialized] public float effectiveInterval;   // this part's current target interval (s)
    [System.NonSerialized] public bool finished;             // reached the final onset
    [System.NonSerialized] public RenderTexture ownedRT;
    [System.NonSerialized] public Material matInstance;
}

/// <summary>
/// User study controller for the ARME Timing Model. See <see cref="EnsembleMode"/>
/// for the three experimental conditions.
///
/// Behaviour mirrors the AMUSER iOS app: each musician is an independent virtual player that
/// runs on its own clock and warps its own recording segment-by-segment (pitch-preserving)
/// so its recorded onsets land on the user's tapped beat. The per-onset rate is produced by
/// the ARME Timing Model via <see cref="ARMEPlaybackTimingBridge"/>; Agentic mode adds the
/// AMUSER ensemble coupling (0.7·user + 0.3·ensemble-mean) so the parts also stay together.
/// </summary>
public class ARMEUserStudyController : MonoBehaviour
{
    [BoxGroup("Mode")]
    [Tooltip("Experimental condition for this run.")]
    [SerializeField] private EnsembleMode mode = EnsembleMode.Adaptive;

    [BoxGroup("Virtual Players")]
    [Tooltip("Reported in the session log. The actual virtual players are the video parts below (one musician = one player).")]
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
    [Tooltip("Tempo (BPM) at which the recordings play at their normal 1x speed. Tap at this rate and the videos play natively; tapping faster/slower scales each part's playback speed proportionally.")]
    [SerializeField, Range(40f, 220f)] private float naturalBPM = 120f;

    [BoxGroup("Videos")]
    [Tooltip("Lower clamp on a part's playback speed while following the user (fraction of native speed). No matter how slowly you tap, a part won't go below this.")]
    [SerializeField, Range(0.1f, 1f)] private float minPlaybackSpeed = 0.5f;

    [BoxGroup("Videos")]
    [Tooltip("Upper clamp on a part's playback speed while following the user (multiple of native speed). No matter how fast you tap, a part won't go above this.")]
    [SerializeField, Range(1f, 4f)] private float maxPlaybackSpeed = 2.0f;

    [BoxGroup("Random Mode")]
    [Tooltip("(Legacy — unused by the AMUSER-style warp.) Minimum random blink interval.")]
    [SerializeField] private float minRandomInterval = 0.4f;

    [BoxGroup("Random Mode")]
    [Tooltip("(Legacy — unused by the AMUSER-style warp.) Maximum random blink interval.")]
    [SerializeField] private float maxRandomInterval = 1.2f;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("(Legacy preset — unused by the AMUSER-style warp, which follows the user's tempo directly.)")]
    [SerializeField] private SyncTightness syncTightness = SyncTightness.MusicalTight;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("Logged for the session record. (Coupling now follows AMUSER: Agentic = 0.7·user + 0.3·ensemble.)")]
    [SerializeField, Range(0f, 1f)] private float alpha = 0.30f;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("Logged for the session record. (Coupling now follows AMUSER: Agentic = 0.7·user + 0.3·ensemble.)")]
    [SerializeField, Range(0f, 1f)] private float beta = 0.15f;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("Smoothing on the user's measured IOI itself. Lower = more reactive, higher = more inertial.")]
    [SerializeField, Range(0f, 1f)] private float userIoiSmoothing = 0.30f;

    [BoxGroup("Adaptation (Wing-Kristofferson)")]
    [Tooltip("(Legacy — unused by the AMUSER-style warp.) Per-virtual visual phase offset.")]
    [SerializeField, Range(0f, 0.5f)] private float phaseOffsetPerPlayer = 0.0f;

    [BoxGroup("Idle Behaviour")]
    [Tooltip("If the user stops tapping for more than this many user-IOIs, the parts ease back to the recorded tempo. (Adaptive / Agentic only.)")]
    [SerializeField, Range(1f, 10f)] private float idleTimeoutFactor = 2.5f;

    [BoxGroup("Idle Behaviour")]
    [Tooltip("Hard fallback timeout (seconds) used before a user IOI has been measured.")]
    [SerializeField] private float fallbackIdleSeconds = 3.0f;

    [BoxGroup("Non-Adaptive Mode")]
    [Tooltip("Logged for the session record. (Non-Adaptive now plays at the recording's own tempo, matching AMUSER's non-agentic mode.)")]
    [SerializeField, Range(40f, 200f)] private float fixedBPM = 90f;

    [BoxGroup("Agentic Mode")]
    [Tooltip("Logged for the session record.")]
    [SerializeField, Range(0f, 0.3f)] private float agenticIoiVariation = 0.06f;

    [BoxGroup("Agentic Mode")]
    [Tooltip("Logged for the session record.")]
    [SerializeField, Range(0f, 0.2f)] private float agenticTimingNoise = 0.04f;

    [BoxGroup("Agentic Mode")]
    [Tooltip("Logged for the session record.")]
    [SerializeField, Range(0f, 1f)] private float agenticBaselinePull = 0.25f;

    [BoxGroup("Agentic Mode")]
    [Tooltip("How strongly each virtual follows the user vs. the ensemble mean in Agentic mode (AMUSER agenticUserWeight). 1 = pure user, 0 = pure ensemble.")]
    [SerializeField, Range(0f, 1f)] private float agenticUserWeight = 0.7f;

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

    // ── Runtime ──────────────────────────────────────────────────────────
    private ARMEPlaybackTimingBridge _timingBridge;   // AMUSER-style per-onset rate via the Timing Model
    private float _ensembleMeanRate = 1f;             // mean of parts' currentRate (Agentic coupling)
    private AudioClip _dspKeepAlive;                  // tiny looping silent clip so each part's OnAudioFilterRead fires

    // Video/audio startup is deferred until every part has finished preparing so the pictures
    // and the native stretchers begin together.
    private bool _videosPending;
    private float _videoArmTime;
    private float _previousUserTapTime = -1f;

    // ── Data Logging Events ──────────────────────────────────────────────
    public event System.Action<UserStudyTapEvent>   OnUserTap;
    public event System.Action<UserStudyBlinkEvent> OnVirtualBlink;
    public event System.Action<UserStudyModeEvent>  OnModeChange;

    /// <summary>Returns a configuration snapshot for CSV session metadata.</summary>
    public SessionConfig GetSessionConfig() => new SessionConfig
    {
        numVirtualPlayers   = videoParts != null ? videoParts.Count : numVirtualPlayers,
        startMode           = mode,
        alpha               = alpha,
        beta                = beta,
        userIoiSmoothing    = userIoiSmoothing,
        fixedBPM            = fixedBPM,
        agenticIoiVariation = agenticIoiVariation,
        agenticTimingNoise  = agenticTimingNoise,
        agenticBaselinePull = agenticBaselinePull,
    };

    void Start()
    {
        _timingBridge = new ARMEPlaybackTimingBridge();
        _dspKeepAlive = AudioClip.Create("ARME_DSPKeepAlive", 1024, 1, AudioSettings.outputSampleRate, false);

        BindVideoParts();
        ApplyModeReset();

        // Defer the actual start until every clip has prepared and every native controller
        // has initialised, so audio and picture begin together.
        _videosPending = true;
        _videoArmTime = Time.time;

        Log($"Ready. Parts: {videoParts.Count}. Mode={mode}. naturalBPM={naturalBPM:F0}. " +
            $"AMUSER-style per-player pitch-preserving warp.");
    }

    /// <summary>Reset mode bookkeeping (no per-virtual blink arrays in the AMUSER-style model).</summary>
    private void ApplyModeReset()
    {
        currentMode = mode;
        adaptiveMode = false;
    }

    private const string AudioFolder = "Assets/ARME/Ensemble/AudioClipsMain";

    /// <summary>
    /// Bind the virtual players to the scene's musician videos. Each part gets its own native
    /// pitch-preserving playback controller (fed its .wav) plus a silent keep-alive AudioSource
    /// so Unity keeps calling the controller's OnAudioFilterRead, its onset file is parsed, and
    /// its display is bound so each plane shows its own clip.
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

            part.onsets = ParseOnsets(part.onsetFile);
            part.currentRate = 1f;

            if (part.audioClip != null)
            {
                var go = new GameObject($"Audio_{part.label}");
                go.transform.SetParent(transform, false);

                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 0f;     // 2D — no positional attenuation
                src.volume = 1f;
                part.audioSource = src;

                // Attempt the native pitch-preserving controller. If its native library loads,
                // it is used (AudioSource plays a silent keep-alive clip while the controller
                // fills OnAudioFilterRead with time-stretched samples). If it does NOT load,
                // StartVideos() removes it and falls back to driving AudioSource.pitch on the
                // real clip (rate-based — pitch shifts with tempo, like AMUSER's AVAudioPlayer).
                var ctrl = go.AddComponent<ARMEOnsetBasedPlaybackController>();
                ctrl.EnableDebugLogging = verboseLogging;
                ctrl.Configure(part.audioClip, part.onsetFile);
                part.controller = ctrl;
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

            part.speed = 1f;
        }
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
    private static List<float> ParseOnsets(TextAsset file)
    {
        var list = new List<float>();
        if (file == null)
            return list;

        foreach (var line in file.text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#"))
                continue;
            if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) && v >= 0f)
                list.Add(v);
        }
        list.Sort();
        return list;
    }

    /// <summary>True once every part's VideoPlayer has finished preparing.</summary>
    private bool AllVideosPrepared()
    {
        foreach (var part in videoParts)
            if (part != null && part.video != null && !part.video.isPrepared)
                return false;
        return true;
    }

    /// <summary>
    /// Start audio + video for every part from the top. Per part, decide the audio path now
    /// that the native controller (if any) has had a chance to initialise: use the native
    /// pitch-preserving stretcher if it is ready, otherwise fall back to AudioSource.pitch on
    /// the real clip so the part still sounds even when the native library can't load.
    /// </summary>
    private void StartVideos()
    {
        foreach (var part in videoParts)
        {
            if (part == null)
                continue;

            part.onsetIndex = 0;
            part.currentRate = 1f;
            part.effectiveInterval = 0f;
            part.finished = false;
            part.speed = 1f;

            part.useNativeAudio = part.controller != null && part.controller.IsReady;

            if (part.audioSource != null)
            {
                part.audioSource.pitch = 1f;

                if (part.useNativeAudio)
                {
                    // Native path: AudioSource plays a silent keep-alive clip so Unity keeps
                    // calling the controller's OnAudioFilterRead, which fills it with the
                    // time-stretched (pitch-preserved) audio.
                    part.audioSource.clip = _dspKeepAlive;
                    part.audioSource.loop = true;
                    part.audioSource.Play();
                    part.controller.StartPlayback();
                }
                else
                {
                    // Fallback: remove the (non-working) controller so its OnAudioFilterRead
                    // can't silence the output, then play the real clip directly; tempo will
                    // be driven via AudioSource.pitch in ApplyPartRate.
                    if (part.controller != null)
                    {
                        Destroy(part.controller);
                        part.controller = null;
                    }
                    part.audioSource.clip = part.audioClip;
                    part.audioSource.loop = false;
                    part.audioSource.time = 0f;
                    part.audioSource.Play();
                }
            }

            if (part.video != null)
            {
                part.video.playbackSpeed = 1f;
                part.video.time = 0.0;
                part.video.Play();
            }
        }
        _ensembleMeanRate = 1f;

        if (verboseLogging)
        {
            int nativeCount = 0;
            foreach (var p in videoParts) if (p != null && p.useNativeAudio) nativeCount++;
            Log($"Playback started. Audio path: {nativeCount}/{videoParts.Count} parts native (pitch-preserving), " +
                $"rest using AudioSource.pitch fallback.");
        }
    }

    void Update()
    {
        // Deferred start: wait until every clip has prepared and every native controller is
        // ready (or a timeout) so audio and picture begin together.
        if (_videosPending)
        {
            // Wait for the videos to prepare (by which point each part's native controller has
            // had its Start() run and settled to ready-or-failed), then begin. Timeout guards
            // against a clip that never prepares.
            if (AllVideosPrepared() || Time.time - _videoArmTime > 5f)
            {
                _videosPending = false;
                StartVideos();
            }
        }

        // If the user changed the mode in the inspector at runtime, re-initialise.
        if (mode != currentMode) ApplyModeReset();

        HandleUserTap();
        DetectIdleRevert();

        if (_videosPending)
            return;

        UpdateEnsembleMeanRate();
        for (int i = 0; i < videoParts.Count; i++)
            DrivePartWarp(videoParts[i], i);
    }

    /// <summary>
    /// True while the user is actively tapping and the ensemble is following them (Adaptive
    /// or Agentic). Until then — and after an idle revert — the parts play at recorded tempo.
    /// </summary>
    private bool VideosEngaged => adaptiveMode;

    private void HandleUserTap()
    {
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;

        float t = Time.time;
        userTapCount++;

        // Measure inter-tap interval and smooth it into the user's IOI estimate (the tapped beat).
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
        Log($"USER TAP #{userTapCount} @ t={t:F3}s  Δ={interval:F3}s  measuredIOI={measuredUserIOI:F3}s  BPM={currentUserBPM:F1}  mode={mode}");

        // Non-Adaptive ignores the user for timing (recorded tempo). [AMUSER non-agentic]
        if (mode == EnsembleMode.NonAdaptive) return;

        // Need at least one IOI measurement to drive adaptation.
        if (measuredUserIOI <= 0f) return;

        // Adaptive / Agentic engage on the first usable tap, then follow the user.
        if (!adaptiveMode)
        {
            adaptiveMode = true;
            OnModeChange?.Invoke(new UserStudyModeEvent(t, mode, true,
                $"{mode} engaged: IOI={measuredUserIOI:F3}s BPM={currentUserBPM:F1}"));
            Log($">>> {mode} engaged. userIOI={measuredUserIOI:F3}s, BPM={currentUserBPM:F1}");
        }
    }

    /// <summary>
    /// If the user stops tapping past the idle threshold, disengage so the parts ease back to
    /// the recording's own tempo. (Adaptive / Agentic only.)
    /// </summary>
    private void DetectIdleRevert()
    {
        if (!adaptiveMode || mode == EnsembleMode.NonAdaptive) return;

        float now = Time.time;
        float idleSec = (lastUserTapTime > 0f) ? (now - lastUserTapTime) : float.PositiveInfinity;
        float threshold = (measuredUserIOI > 0f) ? idleTimeoutFactor * measuredUserIOI : fallbackIdleSeconds;

        if (idleSec > threshold)
        {
            adaptiveMode = false;
            OnModeChange?.Invoke(new UserStudyModeEvent(now, mode, false,
                $"Disengaged (idle {idleSec:F2}s > {threshold:F2}s) — easing back to recorded tempo"));
            Log($"<<< Idle {idleSec:F2}s (> {threshold:F2}s) — disengaging, parts return to recorded tempo");
        }
    }

    /// <summary>Mean of all parts' current playback-rate; used for Agentic ensemble coupling.</summary>
    private void UpdateEnsembleMeanRate()
    {
        float sum = 0f; int n = 0;
        foreach (var part in videoParts)
        {
            if (part == null) continue;
            sum += part.currentRate; n++;
        }
        _ensembleMeanRate = (n > 0) ? sum / n : 1f;
    }

    /// <summary>
    /// The tempo ratio (× native speed) a part should target right now, by mode — the AMUSER
    /// desired-interval logic expressed as a rate so it works with note-level onset files:
    ///   NonAdaptive → 1 (recorded tempo);
    ///   Adaptive    → user's tempo (currentUserBPM / naturalBPM);
    ///   Agentic     → 0.7·user + 0.3·ensemble-mean.
    /// </summary>
    private float TargetRate()
    {
        float userRate = (VideosEngaged && currentUserBPM > 1e-3f)
            ? Mathf.Clamp(currentUserBPM / Mathf.Max(naturalBPM, 1f), minPlaybackSpeed, maxPlaybackSpeed)
            : 1f;

        switch (currentMode)
        {
            case EnsembleMode.NonAdaptive:
                return 1f;
            case EnsembleMode.Agentic:
                float ens = _ensembleMeanRate > 0f ? _ensembleMeanRate : userRate;
                return agenticUserWeight * userRate + (1f - agenticUserWeight) * ens;
            case EnsembleMode.Adaptive:
            default:
                return userRate;
        }
    }

    /// <summary>
    /// Per-part, per-onset warp on the part's OWN clock (its VideoPlayer.time). When the part
    /// reaches recorded onset i, set the playback rate for the upcoming segment [i, i+1] so that
    /// onset's spacing matches the target tempo — exactly AMUSER's calculateNextPlaybackRate.
    /// The rate is produced by the Timing Model bridge and applied to both the picture
    /// (VideoPlayer.playbackSpeed) and the audio (native pitch-preserving time-stretch).
    /// </summary>
    private void DrivePartWarp(VideoPart part, int index)
    {
        if (part == null || part.finished) return;
        var onsets = part.onsets;
        if (onsets == null || onsets.Count < 2) return;
        if (part.video == null || !part.video.isPrepared) return;

        int i = part.onsetIndex;
        if (i >= onsets.Count - 1) { part.finished = true; return; }

        // Hold the current segment's rate until this part's clock reaches the next onset.
        if (part.video.time < onsets[i]) return;

        // Reached onset i — choose the rate for the segment [onset i, onset i+1].
        float scoreInterval = onsets[i + 1] - onsets[i];
        float targetRate = TargetRate();
        float desiredInterval = scoreInterval / Mathf.Max(targetRate, 1e-3f);

        float rate = _timingBridge.CalculateAVPlaybackRate(onsets[i], scoreInterval, desiredInterval);
        rate = Mathf.Clamp(rate, minPlaybackSpeed, maxPlaybackSpeed);

        ApplyPartRate(part, rate);
        part.effectiveInterval = desiredInterval;
        part.onsetIndex = i + 1;

        OnVirtualBlink?.Invoke(new UserStudyBlinkEvent(
            Time.time, index + 1, part.onsetIndex, desiredInterval, 0f, currentMode.ToString()));
    }

    /// <summary>Apply a part's tempo ratio to its picture and its audio (native or fallback).</summary>
    private void ApplyPartRate(VideoPart part, float rate)
    {
        part.currentRate = rate;
        part.speed = rate;

        if (part.video != null && part.video.isPrepared && part.video.canSetPlaybackSpeed)
            part.video.playbackSpeed = rate;

        if (part.useNativeAudio && part.controller != null)
        {
            // Native time-stretch ratio is output/input duration = 1/rate, so the audio plays
            // at 'rate' while pitch is preserved.
            part.controller.SetSpeed(1f / Mathf.Max(rate, 1e-3f));
        }
        else if (part.audioSource != null)
        {
            // Fallback: rate-based — pitch shifts with tempo (like AMUSER's AVAudioPlayer.rate).
            part.audioSource.pitch = rate;
        }
    }

    private void Log(string msg)
    {
        if (verboseLogging) Debug.Log("[UserStudy] " + msg);
    }

    [Button("Reset & Restart")]
    private void ResetAll()
    {
        userTapCount = 0;
        currentUserBPM = 0f;
        measuredUserIOI = 0f;
        lastUserTapTime = 0f;
        _previousUserTapTime = -1f;
        _ensembleMeanRate = 1f;

        foreach (var part in videoParts)
        {
            if (part == null)
                continue;

            part.onsetIndex = 0;
            part.currentRate = 1f;
            part.effectiveInterval = 0f;
            part.finished = false;
            part.speed = 1f;

            if (part.useNativeAudio && part.controller != null)
            {
                // Native path: rewind the stretcher and restart; keep-alive source stays running.
                part.controller.ResetPlayback();
                part.controller.StartPlayback();
                if (part.audioSource != null)
                {
                    part.audioSource.pitch = 1f;
                    if (!part.audioSource.isPlaying) part.audioSource.Play();
                }
            }
            else if (part.audioSource != null)
            {
                // Fallback path: rewind and replay the real clip at normal pitch.
                part.audioSource.pitch = 1f;
                part.audioSource.time = 0f;
                part.audioSource.Play();
            }

            if (part.video != null)
            {
                part.video.playbackSpeed = 1f;
                part.video.time = 0.0;
                part.video.Play();
            }
        }

        ApplyModeReset();
        Log($"RESET complete. Mode={mode}.");
    }

    void OnDestroy()
    {
        foreach (var part in videoParts)
        {
            if (part == null)
                continue;
            if (part.video != null) part.video.Pause();
            if (part.ownedRT != null)
            {
                part.ownedRT.Release();
                Destroy(part.ownedRT);
            }
            if (part.matInstance != null)
                Destroy(part.matInstance);
        }
        // Per-part controllers live on child GameObjects; they dispose their own native
        // resources in their OnDestroy when this object's children are destroyed.
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

            string baseName = vp.clip.name;
            if (baseName.EndsWith("_TB"))
                baseName = baseName.Substring(0, baseName.Length - 3);

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
    {
        foreach (string guid in UnityEditor.AssetDatabase.FindAssets($"t:{typeof(T).Name}", new[] { folder }))
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path) == assetName)
                return UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
        }
        return null;
    }
#endif
}
