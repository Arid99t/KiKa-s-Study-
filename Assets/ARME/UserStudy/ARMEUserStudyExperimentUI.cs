using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

/// <summary>
/// Runtime experiment front-end for the 2 (Modality) × 3 (Interactivity) SMS study, run as
/// experimenter-picked condition blocks. Builds its whole UI in code (no scene/prefab setup) and
/// drives the session via <see cref="ARMEUserStudySession"/>:
///
///   Demographics screen (ID / age / gender / musical training)
///     → 3 practice trials (AudioVisual + Adaptive; shown as "Practice n of 3", data discarded)
///     → condition picker (six "modes", shown with neutral "Condition n" labels so participants
///       never see the interactivity names)
///         → the chosen block runs N reps; each rep:
///              ready screen (participants only ever see a running "Trial n" number)
///                → count-in at the standardised speed (tap/click along to set the tempo)
///                → one take (each tap fires Violin 1's next note; the other three musicians
///                  synchronise to Violin 1, with the timing model predicting their onsets)
///         → 5-item VAS questionnaire (0–100 sliders) for the block
///         → back to the picker (completed conditions are marked)
///     → Finish &amp; Save → thank-you screen (the data logger saves on session complete).
///
/// Drop the component on a GameObject in the User Study scene alongside an
/// <see cref="ARMEUserStudyController"/>; an <see cref="ARMEUserStudySession"/> is auto-found or created.
/// </summary>
public class ARMEUserStudyExperimentUI : MonoBehaviour
{
    [Tooltip("The study controller this UI drives. Auto-found in the scene if left empty.")]
    [SerializeField] private ARMEUserStudyController controller;

    [Tooltip("The session/condition runner. Auto-found (or created) in the scene if left empty.")]
    [SerializeField] private ARMEUserStudySession session;

    [Tooltip("How many counts the count-in shows (4 → 3 → 2 → 1).")]
    [SerializeField] private int countInBeats = 4;

    [Tooltip("Fallback seconds-per-count used only if no session provides the standard speed.")]
    [SerializeField] private float fallbackSecondsPerCount = 0.5f;

    // VAS questionnaire items rated 0–100 on sliders. Order must match OnQuestionnaireSubmit's
    // mapping into RatingsData.
    private static readonly string[] RatingItems =
    {
        "How in sync did you feel with the ensemble?",
        "How easy was it to stay in sync with the ensemble?",
        "How realistic did the interaction feel?",
        "How engaged did you feel during the take?",
        "How much did you feel the ensemble responded to you (sense of agency)?",
    };
    private const float VasMin = 0f, VasMax = 100f;
    private const float VasTrackWidth = 700f;

    // The six selectable "modes" (condition ids per ARMEUserStudySession.ConditionInfo.FromConditionId).
    // Labels are deliberately neutral so on-screen text never reveals the interactivity to the
    // participant. Experimenter mapping: 1/4 = Non-Agentic, 2/5 = Adaptive, 3/6 = Agentic
    // (1–3 audio-only, 4–6 audio-visual).
    private static readonly (string label, int id)[] PickerConditions =
    {
        ("Condition 4", 4), ("Condition 5", 5), ("Condition 6", 6),
        ("Condition 1", 1), ("Condition 2", 2), ("Condition 3", 3),
    };

    private Font _font;

    // Panels.
    private GameObject _demographicsPanel;
    private GameObject _pickerPanel;
    private GameObject _readyPanel;
    private GameObject _hudPanel;
    private GameObject _questionnairePanel;
    private GameObject _completePanel;

    // Demographics inputs.
    private InputField _idInput, _ageInput, _trainingInput;
    private string _selectedGender = "";
    private readonly List<Button> _genderButtons = new();
    private static readonly string[] Genders = { "Female", "Male", "Other", "Prefer not to say" };

    // Picker.
    private readonly List<Button> _pickerButtons = new();
    private Text _pickerProgress;

    // Ready screen.
    private Text _readyText;
    private int _trialNumber;      // running participant-facing trial count across the session
    private bool _isPracticeTrial; // current trial is practice (shows the "Violin 1" tag)

    // Questionnaire (VAS sliders).
    private readonly float[]  _ratingValues  = new float[RatingItems.Length];
    private readonly bool[]   _ratingTouched = new bool[RatingItems.Length];
    private readonly Slider[] _ratingSliders = new Slider[RatingItems.Length];
    private readonly Image[]  _ratingHandles = new Image[RatingItems.Length];

    // HUD / tempo meter.
    private GameObject _hudBlackout;   // full-screen black, shown only in audio-only takes
    private GameObject _violin1Label;  // "Violin 1 ▼" tag over the leader — practice takes only
    private Text _countdownText;
    private const float MinBPM = 40f, MaxBPM = 200f;
    private const float TempoTrackWidth = 560f;
    private const float TempoHandleLerpRate = 20f;   // higher = snappier handle, less lag
    private RectTransform _tempoHandle;

    private AudioSource _tickSource;
    private Coroutine _flowRoutine;

    // ── Lifecycle ────────────────────────────────────────────────────────

    void Awake()
    {
        if (controller == null) controller = FindFirstObjectByType<ARMEUserStudyController>();
        if (session == null)    session    = FindFirstObjectByType<ARMEUserStudySession>();
        // No session component in the scene → create one so the flow still runs (the logger
        // auto-finds it in its Start, which runs after this Awake).
        if (session == null)    session    = gameObject.AddComponent<ARMEUserStudySession>();

        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
             ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

        BuildTick();
        BuildUI();
    }

    void OnEnable()
    {
        if (controller != null)
        {
            controller.OnPlaybackEnded += HandlePlaybackEnded;
        }
        if (session != null)
        {
            session.OnTrialStarted     += HandleTrialStarted;
            session.OnBlockEnded       += HandleBlockEnded;
            session.OnPracticeEnded    += HandlePracticeEnded;
            session.OnRatingsSubmitted += HandleRatingsSubmitted;
            session.OnSessionComplete  += HandleSessionComplete;
        }
    }

    void OnDisable()
    {
        if (controller != null)
        {
            controller.OnPlaybackEnded -= HandlePlaybackEnded;
        }
        if (session != null)
        {
            session.OnTrialStarted     -= HandleTrialStarted;
            session.OnBlockEnded       -= HandleBlockEnded;
            session.OnPracticeEnded    -= HandlePracticeEnded;
            session.OnRatingsSubmitted -= HandleRatingsSubmitted;
            session.OnSessionComplete  -= HandleSessionComplete;
        }
        if (_flowRoutine != null) { StopCoroutine(_flowRoutine); _flowRoutine = null; }
    }

    void Start()
    {
        if (controller != null) controller.AcceptTaps = false;
        ShowOnly(_demographicsPanel);
    }

    void Update()
    {
        if (_hudPanel != null && _hudPanel.activeSelf && controller != null)
        {
            UpdateTempoMeter(controller.currentUserBPM);
            if (_violin1Label != null && _violin1Label.activeSelf)
                PositionViolin1Label();
        }
    }

    /// <summary>Keep the practice-only "Violin 1 ▼" tag hovering just above the Violin 1 plane.</summary>
    private void PositionViolin1Label()
    {
        var rend = controller.LeaderRenderer;
        var cam = Camera.main;
        if (cam == null) cam = FindFirstObjectByType<Camera>();
        if (rend == null || cam == null)
        {
            // Park off-screen (don't deactivate — the references may resolve a frame later).
            _violin1Label.transform.position = new Vector3(-2000f, -2000f, 0f);
            return;
        }

        // Top-centre of the musician's plane projected to the screen. On a screen-space
        // overlay canvas, UI positions ARE screen pixels, so it can be set directly.
        Vector3 top = rend.bounds.center + Vector3.up * rend.bounds.extents.y;
        Vector3 sp = cam.WorldToScreenPoint(top);
        if (sp.z <= 0f) return;   // behind the camera — keep the last position
        _violin1Label.transform.position = new Vector3(sp.x, sp.y + 8f, 0f);
    }

    // ── Flow ─────────────────────────────────────────────────────────────

    private void OnDemographicsSubmit()
    {
        if (session == null) return;

        int.TryParse(_ageInput != null ? _ageInput.text : "", out int age);
        float.TryParse(_trainingInput != null ? _trainingInput.text : "", out float training);
        string id = _idInput != null && !string.IsNullOrWhiteSpace(_idInput.text)
            ? _idInput.text.Trim() : "P" + session.ParticipantNumber.ToString("00");

        _trialNumber = 0;
        session.Begin(new ParticipantInfo
        {
            id = id, age = age, gender = _selectedGender, musicalTrainingYears = training
        });
        // Practice comes first (fires OnTrialStarted → ready screen); the picker is shown once
        // OnPracticeEnded fires (immediately if practice is disabled in the session inspector).
        session.StartPracticeBlock();
    }

    private void OnPickerSelect(int conditionId)
    {
        if (session == null) return;
        session.StartBlock(conditionId);   // fires OnTrialStarted → HandleTrialStarted shows ready
    }

    private void OnFinishPressed()
    {
        if (session != null) session.Finish();   // fires OnSessionComplete
    }

    private void HandleTrialStarted(TrialInfo info)
    {
        if (controller != null) controller.AcceptTaps = false;
        _isPracticeTrial = info.isPractice;

        // Participants only ever see a running trial number (or "Practice n of N") — never the
        // condition name, so the labels can't shape their expectations of how the ensemble
        // will behave.
        string heading;
        if (info.isPractice)
        {
            int total = session != null ? session.PracticeTrials : 3;
            heading = $"Practice {info.repIndex} of {total}\n" +
                      "A practice round so you can get familiar with the task.\n";
        }
        else
        {
            _trialNumber++;
            heading = $"Trial {_trialNumber}\n";
        }

        if (_readyText != null)
        {
            _readyText.text =
                heading + "\n" +
                "Press Begin. A short countdown will play — tap along with it to establish your starting tempo.\n" +
                "Once the music begins, tap once for each note played by Violin 1 and continue tapping while " +
                "paying attention to the other musicians, trying to stay in time with the ensemble, " +
                "just as you would when performing with a real musical group.\n" +
                "The way the ensemble responds to your timing may vary across trials.";
        }
        ShowOnly(_readyPanel);
    }

    private void HandlePracticeEnded()
    {
        if (controller != null) { controller.AcceptTaps = false; controller.StopPlayback(); }
        ShowPicker();
    }

    private void OnReadyBeginPressed()
    {
        if (_flowRoutine != null) return;
        _flowRoutine = StartCoroutine(CountInThenPlay());
    }

    private IEnumerator CountInThenPlay()
    {
        ShowOnly(_hudPanel);

        // Audio-only → black screen (videos hidden); keep the metronome + texts visible on top.
        bool audioOnly = controller != null && controller.CurrentModality == Modality.AudioOnly;
        if (_hudBlackout != null) _hudBlackout.SetActive(audioOnly);

        // PRACTICE takes only: hang the "Violin 1 ▼" tag over the leader so participants learn
        // which musician to tap along with. Never shown in the real experiment trials.
        if (_violin1Label != null)
            _violin1Label.SetActive(_isPracticeTrial && !audioOnly);

        if (controller != null)
            controller.PrepareForTake();   // fresh model/counters before the count-in taps

        yield return null;                 // let the Begin click pass before counting taps
        if (controller != null) controller.AcceptTaps = true;

        float secondsPerCount = session != null ? session.CurrentCountInSpeed : fallbackSecondsPerCount;
        int n = Mathf.Max(1, countInBeats);
        for (int i = n; i >= 1; i--)
        {
            if (_countdownText != null) _countdownText.text = i.ToString();
            PlayTick();
            yield return new WaitForSeconds(Mathf.Max(0.1f, secondsPerCount));
        }
        if (_countdownText != null) _countdownText.text = "";

        if (controller != null) controller.BeginPlayback();
        _flowRoutine = null;
    }

    private void HandlePlaybackEnded()
    {
        // A rep's take finished. The session decides whether another rep follows (→ OnTrialStarted)
        // or the block is over (→ OnBlockEnded → questionnaire).
        if (controller != null) controller.AcceptTaps = false;
        if (session != null) session.EndTake();
    }

    private void HandleBlockEnded(int conditionId)
    {
        if (controller != null) { controller.AcceptTaps = false; controller.StopPlayback(); }
        ShowQuestionnaire();
    }

    private void OnQuestionnaireSubmit()
    {
        for (int i = 0; i < _ratingTouched.Length; i++)
            if (!_ratingTouched[i]) return;   // require every scale to be answered

        var ratings = new RatingsData
        {
            perceivedSynchrony = _ratingValues[0],
            easeOfCoordination = _ratingValues[1],
            realism            = _ratingValues[2],
            engagement         = _ratingValues[3],
            senseOfAgency      = _ratingValues[4],
        };
        if (session != null) session.SubmitRatings(ratings);
    }

    private void HandleRatingsSubmitted(int conditionId, RatingsData ratings)
    {
        // The session finishes after all ratings subscribers have received the answers.
        if (session == null || !session.AllConditionsDone) ShowPicker();
    }

    private void HandleSessionComplete()
    {
        if (controller != null) { controller.AcceptTaps = false; controller.StopPlayback(); }
        ShowOnly(_completePanel);
    }

    // ── HUD helpers ──────────────────────────────────────────────────────

    private void UpdateTempoMeter(float bpm)
    {
        if (_tempoHandle == null) return;
        float frac = Mathf.InverseLerp(MinBPM, MaxBPM, Mathf.Clamp(bpm, MinBPM, MaxBPM));
        float targetX = (frac - 0.5f) * TempoTrackWidth;
        // Glide the handle toward its target instead of snapping between stepwise BPM values.
        // Frame-rate-independent exponential smoothing with a short time constant → smooth but
        // near-instant (≈95% of the way there in ~150 ms), so there's no perceptible lag.
        float x = Mathf.Lerp(_tempoHandle.anchoredPosition.x, targetX,
                             1f - Mathf.Exp(-TempoHandleLerpRate * Time.deltaTime));
        _tempoHandle.anchoredPosition = new Vector2(x, _tempoHandle.anchoredPosition.y);
    }

    // ── UI construction ──────────────────────────────────────────────────

    private void ShowOnly(GameObject panel)
    {
        foreach (var p in new[] { _demographicsPanel, _pickerPanel, _readyPanel, _hudPanel, _questionnairePanel, _completePanel })
            if (p != null) p.SetActive(p == panel);
    }

    private void BuildUI()
    {
        EnsureEventSystem();

        var canvasGO = new GameObject("ExperimentCanvas",
            typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGO.transform.SetParent(transform, false);

        var canvas = canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;

        var scaler = canvasGO.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        BuildDemographics(canvasGO.transform);
        BuildPicker(canvasGO.transform);
        BuildReady(canvasGO.transform);
        BuildHud(canvasGO.transform);
        BuildQuestionnaire(canvasGO.transform);
        BuildComplete(canvasGO.transform);

        ShowOnly(_demographicsPanel);
    }

    private void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        es.transform.SetParent(transform, false);
    }

    private void BuildDemographics(Transform parent)
    {
        _demographicsPanel = NewUI("DemographicsPanel", parent);
        Stretch(_demographicsPanel);
        AddImage(_demographicsPanel, new Color(0.05f, 0.06f, 0.08f, 1f));

        AddText(_demographicsPanel.transform, "Welcome to the Experiment", 60, FontStyle.Bold,
            TextAnchor.MiddleCenter, 1200, 90, 0, 360);
        AddText(_demographicsPanel.transform, "Please enter your details, then press Start.",
            28, FontStyle.Normal, TextAnchor.MiddleCenter, 1200, 50, 0, 290);

        AddText(_demographicsPanel.transform, "Participant ID", 26, FontStyle.Normal, TextAnchor.MiddleRight, 280, 50, -180, 190);
        _idInput = AddInputField(_demographicsPanel.transform, "e.g. P01", 360, 56, 160, 190, InputField.ContentType.Standard);

        AddText(_demographicsPanel.transform, "Age", 26, FontStyle.Normal, TextAnchor.MiddleRight, 280, 50, -180, 115);
        _ageInput = AddInputField(_demographicsPanel.transform, "18–35", 360, 56, 160, 115, InputField.ContentType.IntegerNumber);

        AddText(_demographicsPanel.transform, "Years of musical training", 26, FontStyle.Normal, TextAnchor.MiddleRight, 280, 50, -180, 40);
        _trainingInput = AddInputField(_demographicsPanel.transform, "e.g. 0, 2, 5", 360, 56, 160, 40, InputField.ContentType.DecimalNumber);

        AddText(_demographicsPanel.transform, "Gender", 26, FontStyle.Normal, TextAnchor.MiddleCenter, 600, 50, 0, -50);
        _genderButtons.Clear();
        float gw = 260, gap = 20;
        float totalW = Genders.Length * gw + (Genders.Length - 1) * gap;
        float startX = -totalW / 2f + gw / 2f;
        for (int i = 0; i < Genders.Length; i++)
        {
            string g = Genders[i];
            float x = startX + i * (gw + gap);
            var btn = AddButton(_demographicsPanel.transform, g, gw, 60, x, -120, () => SelectGender(g));
            _genderButtons.Add(btn);
        }

        var start = AddButton(_demographicsPanel.transform, "Start", 360, 80, 0, -260, OnDemographicsSubmit);
        (start.targetGraphic as Image).color = new Color(0.20f, 0.55f, 0.50f, 1f);
    }

    private void SelectGender(string g)
    {
        _selectedGender = g;
        for (int i = 0; i < _genderButtons.Count; i++)
        {
            var img = _genderButtons[i].targetGraphic as Image;
            if (img != null)
                img.color = Genders[i] == g ? new Color(0.20f, 0.55f, 0.50f, 0.98f)
                                            : new Color(0.18f, 0.18f, 0.22f, 0.95f);
        }
    }

    private void BuildPicker(Transform parent)
    {
        _pickerPanel = NewUI("PickerPanel", parent);
        Stretch(_pickerPanel);
        AddImage(_pickerPanel, new Color(0.05f, 0.06f, 0.08f, 1f));

        AddText(_pickerPanel.transform, "Select the next condition", 52, FontStyle.Bold,
            TextAnchor.MiddleCenter, 1400, 80, 0, 400);
        _pickerProgress = AddText(_pickerPanel.transform, "", 26, FontStyle.Normal,
            TextAnchor.MiddleCenter, 1200, 44, 0, 330);

        _pickerButtons.Clear();
        float bw = 460, bh = 130, gx = 40, gy = 40;
        float startX = -(bw + gx);              // 3 columns centred
        float topY = 150;                       // two rows
        for (int k = 0; k < PickerConditions.Length; k++)
        {
            int col = k % 3, rowI = k / 3;
            float x = startX + col * (bw + gx);
            float y = topY - rowI * (bh + gy);
            int id = PickerConditions[k].id;
            var btn = AddButton(_pickerPanel.transform, PickerConditions[k].label, bw, bh, x, y,
                () => OnPickerSelect(id));
            _pickerButtons.Add(btn);
        }

        var finish = AddButton(_pickerPanel.transform, "Finish & Save", 400, 80, 0, -320, OnFinishPressed);
        (finish.targetGraphic as Image).color = new Color(0.20f, 0.55f, 0.50f, 1f);
    }

    private void ShowPicker()
    {
        if (session != null)
        {
            _pickerProgress.text = $"{session.ConditionsDone} of {ARMEUserStudySession.TotalConditions} conditions completed";
            for (int k = 0; k < _pickerButtons.Count; k++)
            {
                bool done = session.IsConditionDone(PickerConditions[k].id);
                var img = _pickerButtons[k].targetGraphic as Image;
                if (img != null)
                    img.color = done ? new Color(0.16f, 0.30f, 0.27f, 0.95f)   // dim teal = done
                                     : new Color(0.18f, 0.18f, 0.22f, 0.95f);
                var label = _pickerButtons[k].GetComponentInChildren<Text>();
                if (label != null)
                    label.text = (done ? "✓ " : "") + PickerConditions[k].label;
            }
        }
        ShowOnly(_pickerPanel);
    }

    private void BuildReady(Transform parent)
    {
        _readyPanel = NewUI("ReadyPanel", parent);
        Stretch(_readyPanel);
        AddImage(_readyPanel, new Color(0.05f, 0.06f, 0.08f, 1f));

        _readyText = AddText(_readyPanel.transform, "", 34, FontStyle.Normal,
            TextAnchor.MiddleCenter, 1300, 340, 0, 80);

        var begin = AddButton(_readyPanel.transform, "Begin", 360, 90, 0, -220, OnReadyBeginPressed);
        (begin.targetGraphic as Image).color = new Color(0.20f, 0.55f, 0.50f, 1f);
    }

    private void BuildHud(Transform parent)
    {
        _hudPanel = NewUI("HudPanel", parent);
        Stretch(_hudPanel);
        _hudPanel.SetActive(false);

        // Full-screen black backdrop for audio-only takes (added first → renders behind the HUD
        // text/meter, but on the screen-space overlay canvas it covers the 3D musician videos).
        _hudBlackout = NewUI("AudioOnlyBlackout", _hudPanel.transform);
        Stretch(_hudBlackout);
        var blackout = _hudBlackout.AddComponent<Image>();
        blackout.color = Color.black;
        blackout.raycastTarget = false;
        _hudBlackout.SetActive(false);

        _countdownText = AddText(_hudPanel.transform, "", 220, FontStyle.Bold,
            TextAnchor.MiddleCenter, 600, 320, 0, 0);

        BuildTempoMeter(_hudPanel.transform);
        BuildViolin1Label(_hudPanel.transform);
    }

    /// <summary>
    /// "Violin 1 ▼" tag hung over the Violin 1 musician during PRACTICE takes only, so
    /// participants learn which player to tap along with. Repositioned every frame in
    /// <see cref="PositionViolin1Label"/>; hidden in the real experiment trials.
    /// </summary>
    private void BuildViolin1Label(Transform parent)
    {
        _violin1Label = NewUI("Violin1Label", parent);
        var rt = _violin1Label.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = Vector2.zero;   // placed in screen pixels each frame
        rt.pivot = new Vector2(0.5f, 0f);             // bottom-centre sits just above the plane
        rt.sizeDelta = new Vector2(360, 96);

        var labelColor = Color.black;   // reads clearly on the study's light backdrop

        var title = AddText(_violin1Label.transform, "Violin 1", 44, FontStyle.Bold,
            TextAnchor.MiddleCenter, 360, 54, 0, -6);
        title.color = labelColor;

        var arrow = AddText(_violin1Label.transform, "▼", 40, FontStyle.Bold,
            TextAnchor.MiddleCenter, 120, 44, 0, -42);
        arrow.color = labelColor;

        _violin1Label.SetActive(false);
    }

    private void BuildQuestionnaire(Transform parent)
    {
        _questionnairePanel = NewUI("QuestionnairePanel", parent);
        Stretch(_questionnairePanel);
        AddImage(_questionnairePanel, new Color(0.05f, 0.06f, 0.08f, 1f));

        AddText(_questionnairePanel.transform, "A few questions about the trials you just completed",
            44, FontStyle.Bold, TextAnchor.MiddleCenter, 1500, 80, 0, 420);
        AddText(_questionnairePanel.transform, "Click or drag anywhere along each line to answer.",
            24, FontStyle.Normal, TextAnchor.MiddleCenter, 1200, 40, 0, 360);

        float rowTop = 260, rowGap = 130;   // 5 rows fit between the header and the Submit button
        for (int i = 0; i < RatingItems.Length; i++)
        {
            float y = rowTop - i * rowGap;
            AddText(_questionnairePanel.transform, RatingItems[i], 24, FontStyle.Normal,
                TextAnchor.MiddleCenter, 1300, 50, 0, y + 52);
            BuildVasRow(_questionnairePanel.transform, i, y);
        }

        var submit = AddButton(_questionnairePanel.transform, "Submit", 360, 80, 0, -440, OnQuestionnaireSubmit);
        (submit.targetGraphic as Image).color = new Color(0.20f, 0.55f, 0.50f, 1f);
    }

    /// <summary>
    /// One VAS item: a horizontal line with end ticks and "Not at all" / "Completely" anchors,
    /// answered by clicking or dragging anywhere along it (continuous 0–100, no numbers shown).
    /// The handle stays grey until the participant answers, then turns teal.
    /// </summary>
    private void BuildVasRow(Transform parent, int item, float y)
    {
        var go = NewUI($"VAS_{item}", parent);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(VasTrackWidth, 56);
        rt.anchoredPosition = new Vector2(0, y);

        // Invisible full-height hit area so clicks anywhere along the line register.
        var hit = go.AddComponent<Image>();
        hit.color = Color.clear;

        // Track line + end ticks.
        var track = NewUI("Track", go.transform);
        var trackRt = track.GetComponent<RectTransform>();
        trackRt.anchorMin = new Vector2(0f, 0.5f);
        trackRt.anchorMax = new Vector2(1f, 0.5f);
        trackRt.offsetMin = new Vector2(0f, -3f);
        trackRt.offsetMax = new Vector2(0f, 3f);
        var trackImg = track.AddComponent<Image>();
        trackImg.color = new Color(1f, 1f, 1f, 0.35f);
        trackImg.raycastTarget = false;
        AddImageChild(go.transform, new Color(1f, 1f, 1f, 0.5f), 4, 26, -VasTrackWidth / 2f, 0);
        AddImageChild(go.transform, new Color(1f, 1f, 1f, 0.5f), 4, 26, VasTrackWidth / 2f, 0);

        // Anchor labels under the line ends.
        AddText(go.transform, "Not at all", 20, FontStyle.Normal,
            TextAnchor.MiddleCenter, 220, 30, -VasTrackWidth / 2f, -36);
        AddText(go.transform, "Completely", 20, FontStyle.Normal,
            TextAnchor.MiddleCenter, 220, 30, VasTrackWidth / 2f, -36);

        // Handle (inset half its width so it can't overhang the line ends).
        var area = NewUI("HandleArea", go.transform);
        var areaRt = area.GetComponent<RectTransform>();
        areaRt.anchorMin = Vector2.zero;
        areaRt.anchorMax = Vector2.one;
        areaRt.offsetMin = new Vector2(7f, 0f);
        areaRt.offsetMax = new Vector2(-7f, 0f);
        var handleGO = NewUI("Handle", area.transform);
        var handleRt = handleGO.GetComponent<RectTransform>();
        handleRt.sizeDelta = new Vector2(14f, 38f);
        var handleImg = handleGO.AddComponent<Image>();
        handleImg.color = new Color(0.45f, 0.45f, 0.50f, 1f);
        handleImg.raycastTarget = false;

        var slider = go.AddComponent<Slider>();
        slider.transition = Selectable.Transition.None;
        slider.targetGraphic = hit;
        slider.handleRect = handleRt;
        slider.minValue = VasMin;
        slider.maxValue = VasMax;
        slider.wholeNumbers = false;
        slider.SetValueWithoutNotify((VasMin + VasMax) / 2f);
        int idx = item;
        slider.onValueChanged.AddListener(_ => MarkVasAnswered(idx));

        // A click exactly on the handle's current position doesn't change the value, so also
        // count any pointer-down on the scale as an answer.
        var trigger = go.AddComponent<EventTrigger>();
        var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
        entry.callback.AddListener(_ => MarkVasAnswered(idx));
        trigger.triggers.Add(entry);

        _ratingSliders[item] = slider;
        _ratingHandles[item] = handleImg;
        _ratingValues[item] = -1f;
        _ratingTouched[item] = false;
    }

    private void MarkVasAnswered(int item)
    {
        if (_ratingSliders[item] != null) _ratingValues[item] = _ratingSliders[item].value;
        if (!_ratingTouched[item] && _ratingHandles[item] != null)
            _ratingHandles[item].color = new Color(0.30f, 0.85f, 0.55f, 1f);
        _ratingTouched[item] = true;
    }

    private void ResetQuestionnaire()
    {
        for (int i = 0; i < RatingItems.Length; i++)
        {
            _ratingValues[i] = -1f;
            _ratingTouched[i] = false;
            if (_ratingSliders[i] != null) _ratingSliders[i].SetValueWithoutNotify((VasMin + VasMax) / 2f);
            if (_ratingHandles[i] != null) _ratingHandles[i].color = new Color(0.45f, 0.45f, 0.50f, 1f);
        }
    }

    private void ShowQuestionnaire()
    {
        ResetQuestionnaire();
        ShowOnly(_questionnairePanel);
    }

    private void BuildComplete(Transform parent)
    {
        _completePanel = NewUI("CompletePanel", parent);
        Stretch(_completePanel);
        AddImage(_completePanel, new Color(0.05f, 0.06f, 0.08f, 1f));

        AddText(_completePanel.transform,
            "All done — thank you!\n\nYour responses have been saved.",
            44, FontStyle.Bold, TextAnchor.MiddleCenter, 1300, 300, 0, 0);
    }

    private void BuildTempoMeter(Transform parent)
    {
        var meter = NewUI("TempoMeter", parent);
        var crt = meter.GetComponent<RectTransform>();
        crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 1f);
        crt.sizeDelta = new Vector2(TempoTrackWidth + 160, 110);
        crt.anchoredPosition = new Vector2(0f, -28f);

        var track = AddImageChild(meter.transform, new Color(1f, 1f, 1f, 0.18f), TempoTrackWidth, 8, 0, -20);
        var handle = AddImageChild(track.transform, new Color(0.30f, 0.85f, 0.55f, 1f), 14, 34, 0, 0);
        _tempoHandle = handle.rectTransform;
    }

    // ── UI primitive helpers ─────────────────────────────────────────────

    private Image AddImageChild(Transform parent, Color color, float w, float h, float x, float y)
    {
        var go = NewUI("Img", parent);
        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, y);
        return img;
    }

    private static GameObject NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    private static void Stretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static Image AddImage(GameObject go, Color color)
    {
        var img = go.AddComponent<Image>();
        img.color = color;
        return img;
    }

    private Text AddText(Transform parent, string content, int size, FontStyle style,
        TextAnchor anchor, float w, float h, float x, float y)
    {
        var go = NewUI("Text", parent);
        var txt = go.AddComponent<Text>();
        txt.font = _font;
        txt.text = content;
        txt.fontSize = size;
        txt.fontStyle = style;
        txt.alignment = anchor;
        txt.color = Color.white;
        txt.horizontalOverflow = HorizontalWrapMode.Wrap;
        txt.verticalOverflow = VerticalWrapMode.Overflow;
        txt.raycastTarget = false;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, y);
        return txt;
    }

    private Text AddTextTopLeft(Transform parent, string content, int size, float w, float h, float x, float y)
    {
        var go = NewUI("Text", parent);
        var txt = go.AddComponent<Text>();
        txt.font = _font;
        txt.text = content;
        txt.fontSize = size;
        txt.alignment = TextAnchor.UpperLeft;
        txt.color = Color.white;
        txt.raycastTarget = false;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, y);
        return txt;
    }

    private Button AddButton(Transform parent, string label, float w, float h, float x, float y,
        UnityEngine.Events.UnityAction onClick)
    {
        var go = NewUI("Button", parent);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.18f, 0.18f, 0.22f, 0.95f);

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, y);

        var labelTxt = AddText(go.transform, label, 28, FontStyle.Bold, TextAnchor.MiddleCenter, w, h, 0, 0);
        labelTxt.raycastTarget = false;
        return btn;
    }

    /// <summary>Builds a legacy UI InputField (background + text + placeholder) at (x,y).</summary>
    private InputField AddInputField(Transform parent, string placeholder, float w, float h, float x, float y,
        InputField.ContentType contentType)
    {
        var go = NewUI("InputField", parent);
        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.14f, 0.14f, 0.17f, 1f);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, y);

        var input = go.AddComponent<InputField>();
        input.targetGraphic = bg;
        input.contentType = contentType;

        var phText = MakeInputText(go.transform, "Placeholder", placeholder);
        phText.fontStyle = FontStyle.Italic;
        phText.color = new Color(1f, 1f, 1f, 0.4f);

        var valText = MakeInputText(go.transform, "Text", "");
        valText.color = Color.white;
        valText.supportRichText = false;

        input.placeholder = phText;
        input.textComponent = valText;
        return input;
    }

    private Text MakeInputText(Transform parent, string name, string content)
    {
        var go = NewUI(name, parent);
        var txt = go.AddComponent<Text>();
        txt.font = _font;
        txt.text = content;
        txt.fontSize = 26;
        txt.alignment = TextAnchor.MiddleLeft;
        txt.horizontalOverflow = HorizontalWrapMode.Overflow;
        txt.verticalOverflow = VerticalWrapMode.Truncate;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(14, 4);
        rt.offsetMax = new Vector2(-14, -4);
        return txt;
    }

    // ── Count-in tick (synthesised, no audio asset needed) ───────────────

    private void BuildTick()
    {
        const int sampleRate = 44100;
        int len = sampleRate / 10;                 // 0.1 s
        var samples = new float[len];
        for (int i = 0; i < len; i++)
        {
            float t = i / (float)sampleRate;
            float env = Mathf.Exp(-t * 32f);
            samples[i] = Mathf.Sin(2f * Mathf.PI * 1000f * t) * env * 0.6f;
        }

        var clip = AudioClip.Create("CountInTick", len, 1, sampleRate, false);
        clip.SetData(samples, 0);

        var go = new GameObject("CountInTick");
        go.transform.SetParent(transform, false);
        _tickSource = go.AddComponent<AudioSource>();
        _tickSource.clip = clip;
        _tickSource.playOnAwake = false;
        _tickSource.spatialBlend = 0f;
    }

    private void PlayTick()
    {
        if (_tickSource != null) _tickSource.Play();
    }
}
