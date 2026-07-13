using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// Plays a set of video + audio "parts" (e.g. the four quartet players) locked to one
/// sample-accurate clock so they stay synchronized — WITHOUT the native time-stretch DLL.
///
/// Each part is a <see cref="VideoPlayer"/> (the _TB picture, which carries no audio track),
/// an <see cref="AudioClip"/> (the real .wav audio) and an optional onset file used only to
/// align the parts' first onsets. Audio is started sample-accurately on the DSP clock via
/// <see cref="AudioSource.PlayScheduled"/>; each video is started on the same clock and then
/// drift-corrected toward it, so picture and sound — and all the parts — stay together.
///
/// Setup: add this to one GameObject, use the context-menu "Auto-Fill Parts From Scene
/// Videos", then press Play. (No native plugins required.)
/// </summary>
[DisallowMultipleComponent]
public class ARMEEnsembleSyncPlayer : MonoBehaviour
{
    [System.Serializable]
    public class SyncPart
    {
        [Tooltip("Inspector label only.")]
        public string label;

        [Tooltip("VideoPlayer that shows this part's _TB clip.")]
        public VideoPlayer video;

        [Tooltip("This part's audio (.wav). The _TB video has no audio track, so the sound comes from here.")]
        public AudioClip audio;

        [Tooltip("Optional onset file (-CRNNManual). Used only to align the parts' first onsets.")]
        public TextAsset onsetFile;

        [Tooltip("Optional: the plane/renderer that shows this part. If set (and Manage Display is on) the player gives this part its OWN RenderTexture + material instance, so parts can never share a texture.")]
        public Renderer displayRenderer;

        [System.NonSerialized] public AudioSource source;
        [System.NonSerialized] public float firstOnset;
        [System.NonSerialized] public double startOffset;
        [System.NonSerialized] public bool videoStarted;
        [System.NonSerialized] public RenderTexture ownedRT;
        [System.NonSerialized] public Material matInstance;
    }

    [Header("Parts")]
    public List<SyncPart> parts = new List<SyncPart>();

    [Header("Sync")]
    [Tooltip("Shift each part so their first onsets coincide (uses the onset files). Small corrections; turn off to start every part at exactly the same instant.")]
    public bool alignFirstOnsets = true;

    [Tooltip("Loop the whole ensemble in lockstep.")]
    public bool loop = true;

    [Tooltip("Seconds to wait before playback begins, so audio and video can schedule cleanly.")]
    [Range(0.1f, 2f)] public float startDelay = 0.5f;

    [Tooltip("Only resync a video if it has drifted from the master clock by MORE than this many seconds. Keep this fairly loose — tighter values cause frequent seeks and stutter.")]
    [Range(0.05f, 0.5f)] public float videoDriftThreshold = 0.15f;

    [Tooltip("How often (seconds) to check video drift. Videos play at native speed between checks, so a longer interval is smoother. Sync is still anchored by the shared start + per-loop restart.")]
    [Range(0.25f, 5f)] public float videoResyncInterval = 1.0f;

    [Header("Display")]
    [Tooltip("Give each part its OWN RenderTexture + material instance at runtime (recommended). This avoids the 'all planes show one video' problem caused by shared/duplicated RenderTextures and materials. Requires each part's Display Renderer to be set (Auto-Fill does this by name).")]
    public bool manageDisplay = true;

    [Tooltip("Shader texture property that receives the video. The AlphaShaderTB / MixVidTB material uses '_TB'.")]
    public string videoTextureProperty = "_TB";

    [Header("Startup")]
    [Tooltip("Start automatically when the scene plays.")]
    public bool autoStartOnPlay = true;

    [Tooltip("Log start/loop events to the Console.")]
    public bool verboseLogging = true;

    private bool _playing;
    private bool _pendingStart;
    private float _armedRealTime;
    private double _cycleStartDsp;   // DSP time that maps to ensemble t = 0 for the current loop
    private double _loopPeriod;      // seconds per loop cycle
    private double _lastResyncDsp;   // last time we checked video drift

    void Start()
    {
        SetupParts();
        _pendingStart = autoStartOnPlay;
        _armedRealTime = Time.realtimeSinceStartup;
    }

    void OnDestroy()
    {
        foreach (var p in parts)
        {
            if (p == null || p.ownedRT == null)
                continue;
            p.ownedRT.Release();
            Destroy(p.ownedRT);
        }
    }

    void Update()
    {
        // Deferred start: wait until every video has finished preparing (or a timeout) so
        // audio and picture begin together.
        if (_pendingStart && !_playing)
        {
            bool ready = AllVideosPrepared();
            bool timedOut = Time.realtimeSinceStartup - _armedRealTime > 5f;
            if (ready || timedOut)
            {
                _pendingStart = false;
                Play();
            }
        }

        if (!_playing)
            return;

        double now = AudioSettings.dspTime;
        double elapsed = now - _cycleStartDsp;

        // Loop / end handling.
        if (elapsed >= _loopPeriod)
        {
            if (!loop)
            {
                Stop();
                return;
            }
            _cycleStartDsp += _loopPeriod;
            RescheduleAudioForCycle();
            foreach (var p in parts)
                if (p != null) p.videoStarted = false;
            if (verboseLogging)
                Debug.Log("[EnsembleSync] Loop restart");
            now = AudioSettings.dspTime;
        }

        // Start each video once, then let it play at native speed (smooth). Only resync
        // on a throttled interval — never seek every frame.
        bool doResync = (now - _lastResyncDsp) >= videoResyncInterval;
        if (doResync)
            _lastResyncDsp = now;

        foreach (var p in parts)
        {
            if (p == null || p.video == null)
                continue;

            double partStart = _cycleStartDsp + p.startOffset;

            if (!p.videoStarted)
            {
                if (now >= partStart && p.video.isPrepared)
                {
                    p.video.time = 0.0;
                    p.video.Play();
                    p.videoStarted = true;
                    if (verboseLogging)
                        Debug.Log($"[EnsembleSync] Video started: {p.label}");
                }
                continue;
            }

            if (!doResync)
                continue;

            // Gentle, infrequent drift correction. Skip while the clip has reached its end
            // (it just waits for the next loop restart) to avoid a freeze-on-last-frame seek.
            double expected = now - partStart;
            if (expected < 0.0)
                continue;
            if (p.video.length > 0.0 && expected >= p.video.length - 0.1)
                continue;
            if (System.Math.Abs(p.video.time - expected) > videoDriftThreshold)
                p.video.time = expected;
        }
    }

    /// <summary>Start (or restart) synchronized playback from the top.</summary>
    [ContextMenu("Play")]
    public void Play()
    {
        if (parts.Count == 0)
        {
            Debug.LogWarning("[EnsembleSync] No parts configured.");
            return;
        }

        // SetupParts may not have run yet if Play() is called before Start().
        if (_loopPeriod <= 0.0)
            SetupParts();

        _cycleStartDsp = AudioSettings.dspTime + startDelay;
        _lastResyncDsp = _cycleStartDsp;
        RescheduleAudioForCycle();
        foreach (var p in parts)
            if (p != null) p.videoStarted = false;
        _playing = true;

        if (verboseLogging)
            Debug.Log($"[EnsembleSync] Play: {parts.Count} part(s), loopPeriod={_loopPeriod:F3}s, startDelay={startDelay:F2}s");
    }

    /// <summary>Stop playback and rewind every part.</summary>
    [ContextMenu("Stop")]
    public void Stop()
    {
        _playing = false;
        foreach (var p in parts)
        {
            if (p == null)
                continue;
            if (p.source != null)
                p.source.Stop();
            if (p.video != null)
            {
                p.video.Pause();
                p.video.time = 0.0;
            }
        }
        if (verboseLogging)
            Debug.Log("[EnsembleSync] Stop");
    }

    private void RescheduleAudioForCycle()
    {
        foreach (var p in parts)
        {
            if (p == null || p.source == null)
                continue;
            p.source.Stop();
            p.source.time = 0f;
            p.source.PlayScheduled(_cycleStartDsp + p.startOffset);
        }
    }

    private bool AllVideosPrepared()
    {
        foreach (var p in parts)
            if (p != null && p.video != null && !p.video.isPrepared)
                return false;
        return true;
    }

    private void SetupParts()
    {
        double maxFirst = 0.0;

        // First pass: configure audio sources + videos, and read each first onset.
        foreach (var p in parts)
        {
            if (p == null)
                continue;

            p.firstOnset = ParseFirstOnset(p.onsetFile);
            if (p.firstOnset > maxFirst)
                maxFirst = p.firstOnset;

            if (p.video != null)
            {
                p.video.audioOutputMode = VideoAudioOutputMode.None; // sound comes from the WAV, not the clip
                p.video.playOnAwake = false;
                p.video.isLooping = false;                           // looping is driven centrally for sync
                p.video.skipOnDrop = true;

                // Give this part its own RenderTexture + material instance so it can never
                // share a texture with another part (the usual cause of "only one video").
                if (manageDisplay && p.displayRenderer != null)
                {
                    int w = (p.video.clip != null && p.video.clip.width > 0) ? (int)p.video.clip.width : 1920;
                    int h = (p.video.clip != null && p.video.clip.height > 0) ? (int)p.video.clip.height : 1080;

                    p.ownedRT = new RenderTexture(w, h, 0) { name = $"EnsembleRT_{p.label}" };
                    p.ownedRT.Create();

                    p.video.renderMode = VideoRenderMode.RenderTexture;
                    p.video.targetTexture = p.ownedRT;

                    p.matInstance = p.displayRenderer.material; // .material clones into a unique instance
                    if (p.matInstance != null && p.matInstance.HasProperty(videoTextureProperty))
                        p.matInstance.SetTexture(videoTextureProperty, p.ownedRT);
                    else
                        Debug.LogWarning($"[EnsembleSync] Part '{p.label}': material has no '{videoTextureProperty}' property; video texture not bound. Check Video Texture Property.");
                }

                if (!p.video.isPrepared)
                    p.video.Prepare();
            }

            if (p.audio != null)
            {
                var host = p.video != null ? p.video.gameObject : gameObject;
                var src = host.GetComponent<AudioSource>();
                if (src == null)
                    src = host.AddComponent<AudioSource>();
                src.clip = p.audio;
                src.playOnAwake = false;
                src.loop = false;
                src.spatialBlend = 0f; // 2D — no positional attenuation
                p.source = src;
            }
            else
            {
                Debug.LogWarning($"[EnsembleSync] Part '{p.label}' has no AudioClip — it will be silent.");
            }
        }

        // Second pass: per-part start offsets (first-onset alignment) and the loop period.
        _loopPeriod = 0.0;
        foreach (var p in parts)
        {
            if (p == null)
                continue;

            p.startOffset = alignFirstOnsets ? (maxFirst - p.firstOnset) : 0.0;

            double end = p.startOffset + (p.audio != null ? p.audio.length : 0.0);
            if (end > _loopPeriod)
                _loopPeriod = end;
        }
        if (_loopPeriod <= 0.0)
            _loopPeriod = 1.0;
    }

    private static float ParseFirstOnset(TextAsset onsetFile) => ARMEOnsetUtil.ParseFirst(onsetFile);

#if UNITY_EDITOR
    /// <summary>
    /// Populate <see cref="parts"/> from the VideoPlayers in the open scene, matching each
    /// clip to its .wav and -CRNNManual onset file by name. Run from the component's context
    /// menu (gear icon) in Edit mode, then press Play.
    /// </summary>
    [ContextMenu("Auto-Fill Parts From Scene Videos")]
    private void AutoFillFromSceneVideos()
    {
        const string audioFolder = "Assets/ARME/Ensemble/AudioClipsMain";

        var videoPlayers = FindObjectsByType<VideoPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (videoPlayers.Length == 0)
        {
            Debug.LogWarning("[EnsembleSync] Auto-fill: no VideoPlayer found in the scene.");
            return;
        }

        parts.Clear();
        var seen = new HashSet<string>();

        foreach (var vp in videoPlayers)
        {
            if (vp.clip == null)
                continue;

            string baseName = ARMEEditorAssetUtil.StripTopBottomSuffix(vp.clip.name);

            if (!seen.Add(baseName))
                continue; // skip a duplicate VideoPlayer pointing at the same piece

            AudioClip clip = FindAsset<AudioClip>(baseName, audioFolder);
            TextAsset onset = FindAsset<TextAsset>(baseName + "-CRNNManual", audioFolder);

            if (clip == null)
            {
                Debug.LogWarning($"[EnsembleSync] Auto-fill: no audio clip named '{baseName}' in {audioFolder}; skipped '{vp.name}'.");
                continue;
            }

            // Match the display plane by name: TopBottomVideoRender (N) -> VideoPlayerPlane (N).
            Renderer planeRenderer = null;
            string planeName = vp.gameObject.name.Replace("TopBottomVideoRender", "VideoPlayerPlane");
            var planeGO = GameObject.Find(planeName);
            if (planeGO != null)
                planeRenderer = planeGO.GetComponent<Renderer>();
            if (planeRenderer == null)
                Debug.LogWarning($"[EnsembleSync] Auto-fill: no display plane '{planeName}' found for '{vp.name}'. Assign its Display Renderer manually, or it will use whatever its plane material is already wired to.");

            parts.Add(new SyncPart { label = baseName, video = vp, audio = clip, onsetFile = onset, displayRenderer = planeRenderer });
        }

        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log($"[EnsembleSync] Auto-fill complete: {parts.Count} part(s).");
    }

    private static T FindAsset<T>(string assetName, string folder) where T : UnityEngine.Object
        => ARMEEditorAssetUtil.FindAsset<T>(assetName, folder);
#endif
}
