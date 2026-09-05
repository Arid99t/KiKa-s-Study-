using System;
using System.Collections.Generic;
using UnityEngine;
using NaughtyAttributes;

/// <summary>Demographics captured for the participant at the start of a session.</summary>
public struct ParticipantInfo
{
    public string id;
    public int    age;
    public string gender;
    public float  musicalTrainingYears;
}

/// <summary>
/// One cell of the 2×3 design ("mode" in the experiment UI).
///   index          — 0-based order in which the experimenter started this block.
///   conditionId    — canonical 1..6 id (see <see cref="FromConditionId"/>).
///   modality       — AudioOnly / AudioVisual.
///   interactivity  — NonAdaptive / Adaptive / Agentic.
/// </summary>
public readonly struct ConditionInfo
{
    public readonly int index;
    public readonly int conditionId;
    public readonly Modality modality;
    public readonly EnsembleMode interactivity;

    public ConditionInfo(int index, int conditionId, Modality modality, EnsembleMode interactivity)
    {
        this.index = index; this.conditionId = conditionId;
        this.modality = modality; this.interactivity = interactivity;
    }

    /// <summary>
    /// Canonical id → (modality, interactivity):
    ///   1 = AudioOnly  × NonAdaptive   4 = AudioVisual × NonAdaptive
    ///   2 = AudioOnly  × Adaptive      5 = AudioVisual × Adaptive
    ///   3 = AudioOnly  × Agentic       6 = AudioVisual × Agentic
    /// </summary>
    public static ConditionInfo FromConditionId(int index, int conditionId)
    {
        Modality m = conditionId <= 3 ? Modality.AudioOnly : Modality.AudioVisual;
        EnsembleMode e = ((conditionId - 1) % 3) switch
        {
            0 => EnsembleMode.NonAdaptive,
            1 => EnsembleMode.Adaptive,
            _ => EnsembleMode.Agentic,
        };
        return new ConditionInfo(index, conditionId, m, e);
    }

    public override string ToString() => $"C{conditionId}({modality}/{interactivity})";
}

/// <summary>One repetition (trial) within a condition block.</summary>
public readonly struct TrialInfo
{
    public readonly ConditionInfo condition;
    public readonly int   repIndex;       // 1..repsPerCondition (or 1..practiceTrials)
    public readonly float countInSpeed;   // seconds per count for this trial
    public readonly bool  isPractice;     // familiarisation trial — data is NOT recorded

    public TrialInfo(ConditionInfo condition, int repIndex, float countInSpeed, bool isPractice = false)
    {
        this.condition = condition; this.repIndex = repIndex; this.countInSpeed = countInSpeed;
        this.isPractice = isPractice;
    }
}

/// <summary>The five subjective VAS ratings (0–100 visual analogue scale) collected after each
/// condition block. Negative = unanswered.</summary>
public struct RatingsData
{
    public float perceivedSynchrony;
    public float easeOfCoordination;   // "How easy was it to stay in sync with the ensemble?"
    public float realism;              // "How realistic did the interaction feel?"
    public float engagement;
    public float senseOfAgency;

    public static RatingsData Empty => new RatingsData
    {
        perceivedSynchrony = -1f, easeOfCoordination = -1f, realism = -1f,
        engagement = -1f, senseOfAgency = -1f
    };
}

/// <summary>
/// Source of truth for a user-study session. Holds the participant demographics and runs the study
/// as <b>experimenter-picked condition blocks</b>: the UI shows a 6-condition picker, the experimenter
/// selects one, and that block runs <see cref="RepsPerCondition"/> repetitions (trials). Before the
/// picker is first shown, <see cref="PracticeTrials"/> practice trials run (AudioVisual + Adaptive)
/// so participants learn the task; practice data is never saved. The count-in
/// speed is chosen per the inspector mode: <b>Fixed</b> (every trial uses
/// <see cref="countInSecondsPerCount"/>) or <b>Random Per Trial</b> (each trial randomly draws one of
/// <see cref="randomCountInSpeeds"/>). After the reps, the five ratings are collected and control
/// returns to the picker.
///
/// The UI (<see cref="ARMEUserStudyExperimentUI"/>) drives timing/input and the data logger
/// (<see cref="ARMEUserStudyDataLogger"/>) records, both via this component's events.
/// </summary>
public class ARMEUserStudySession : MonoBehaviour
{
    /// <summary>How each trial's count-in speed is chosen.</summary>
    public enum CountInSpeedMode
    {
        /// <summary>Every trial uses <see cref="countInSecondsPerCount"/>.</summary>
        Fixed,
        /// <summary>Each trial randomly draws one of <see cref="randomCountInSpeeds"/>.</summary>
        RandomPerTrial,
    }

    [Tooltip("The study controller this session configures each condition. Auto-found if empty.")]
    [SerializeField] private ARMEUserStudyController controller;

    [BoxGroup("Trials")]
    [Tooltip("Participant number used for the default Participant ID. Parsed from the entered " +
             "Participant ID when possible; otherwise this value is used.")]
    [SerializeField] private int participantNumber = 1;

    [BoxGroup("Trials")]
    [Tooltip("Repetitions (takes) per condition block.")]
    [SerializeField] private int repsPerCondition = 5;

    [BoxGroup("Trials")]
    [Tooltip("Practice trials run automatically after the demographics screen, before the first " +
             "condition block. Practice always uses AudioVisual + Adaptive and its data is NOT saved. " +
             "Set to 0 to skip practice.")]
    [SerializeField] private int practiceTrials = 3;

    [BoxGroup("Trials")]
    [Tooltip("Fixed → every trial uses Count In Seconds Per Count. Random Per Trial → each trial " +
             "randomly draws its speed from Random Count In Speeds.")]
    [SerializeField] private CountInSpeedMode countInSpeedMode = CountInSpeedMode.Fixed;

    [BoxGroup("Trials")]
    [ShowIf(nameof(UsesFixedCountIn)), AllowNesting]
    [Tooltip("Count-in speed (seconds per count) used for every trial when the mode is Fixed.")]
    [SerializeField] private float countInSecondsPerCount = 0.5f;

    [BoxGroup("Trials")]
    [ShowIf(nameof(UsesRandomCountIn)), AllowNesting]
    [Tooltip("The speeds (seconds per count) a trial can randomly get when the mode is Random Per Trial.")]
    [SerializeField] private float[] randomCountInSpeeds = { 0.4f, 0.5f, 0.6f };

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting] [SerializeField] private string currentBlock = "(none)";

    [BoxGroup("Status")]
    [ReadOnly, AllowNesting] [SerializeField] private int conditionsCompleted;

    public const int TotalConditions = 6;

    /// <summary>Condition the practice trials always run in: AudioVisual × Adaptive.</summary>
    public const int PracticeConditionId = 5;

    private ConditionInfo _condition;
    private int     _currentRep;          // 1-based within the active block
    private float   _currentTrialCountInSpeed = 0.5f;   // drawn once per trial
    private int     _blocksStarted;
    private bool    _blockActive;
    private bool    _isPracticeBlock;     // the active block is the (unrecorded) practice block
    private readonly HashSet<int> _doneConditions = new HashSet<int>();

    private bool UsesFixedCountIn  => countInSpeedMode == CountInSpeedMode.Fixed;
    private bool UsesRandomCountIn => countInSpeedMode == CountInSpeedMode.RandomPerTrial;

    public ParticipantInfo Participant { get; private set; }
    public int    ParticipantNumber => participantNumber;
    public int    RepsPerCondition  => Mathf.Max(1, repsPerCondition);
    public int    PracticeTrials    => Mathf.Max(0, practiceTrials);
    public bool   InPractice        => _isPracticeBlock;
    public int    ConditionsDone    => _doneConditions.Count;
    public bool   AllConditionsDone => _doneConditions.Count >= TotalConditions;
    public TrialInfo CurrentTrial   => new TrialInfo(_condition, _currentRep, CurrentCountInSpeed, _isPracticeBlock);
    public float  CurrentCountInSpeed => _currentTrialCountInSpeed;

    public bool IsConditionDone(int conditionId) => _doneConditions.Contains(conditionId);

    // ── Events the UI and logger subscribe to ────────────────────────────
    public event Action<TrialInfo>            OnTrialStarted;    // a rep is ready / starting
    public event Action<TrialInfo>            OnTrialEnded;      // a rep's take finished
    public event Action<int>                  OnBlockEnded;      // all reps done → collect ratings
    public event Action                        OnPracticeEnded;   // all practice trials done → show picker
    public event Action<int, RatingsData>     OnRatingsSubmitted;
    public event Action                        OnSessionComplete;

    void Awake()
    {
        if (controller == null) controller = FindFirstObjectByType<ARMEUserStudyController>();
    }

    /// <summary>Begin the session: store demographics (no condition runs until one is picked).</summary>
    public void Begin(ParticipantInfo info)
    {
        Participant = info;
        if (TryParseLeadingInt(info.id, out int n) && n > 0) participantNumber = n;
        _blocksStarted = 0;
        _blockActive = false;
        _isPracticeBlock = false;
        _doneConditions.Clear();
        conditionsCompleted = 0;
    }

    /// <summary>
    /// Start the practice block: <see cref="PracticeTrials"/> familiarisation trials in the fixed
    /// AudioVisual + Adaptive condition. Practice trials look identical to real ones from the
    /// participant's side (count-in → take) but no questionnaire follows and the data logger
    /// discards them. Fires <see cref="OnPracticeEnded"/> immediately if practice is disabled.
    /// </summary>
    public void StartPracticeBlock()
    {
        if (PracticeTrials <= 0)
        {
            OnPracticeEnded?.Invoke();
            return;
        }

        _condition = ConditionInfo.FromConditionId(-1, PracticeConditionId);
        _isPracticeBlock = true;
        _currentRep = 1;
        _currentTrialCountInSpeed = PickCountInSpeed();
        _blockActive = true;
        currentBlock = $"PRACTICE {_condition} reps={PracticeTrials}";

        if (controller != null)
        {
            controller.SetModality(_condition.modality);
            controller.SetMode(_condition.interactivity);
        }

        OnTrialStarted?.Invoke(CurrentTrial);
    }

    /// <summary>Start a condition block (experimenter picked it from the UI). Configures the
    /// controller and starts the first rep.</summary>
    public void StartBlock(int conditionId)
    {
        int blockOrder = _blocksStarted++;
        _condition = ConditionInfo.FromConditionId(blockOrder, conditionId);
        _isPracticeBlock = false;
        _currentRep = 1;
        _currentTrialCountInSpeed = PickCountInSpeed();
        _blockActive = true;
        currentBlock = $"{_condition} reps={RepsPerCondition}";

        if (controller != null)
        {
            controller.SetModality(_condition.modality);
            controller.SetMode(_condition.interactivity);
        }

        OnTrialStarted?.Invoke(CurrentTrial);
    }

    /// <summary>Called by the UI when a rep's take finishes. Ends the trial and either starts the
    /// next rep or, after the last rep, ends the block — a real block moves on to ratings, the
    /// practice block hands control back to the picker.</summary>
    public void EndTake()
    {
        if (!_blockActive) return;
        OnTrialEnded?.Invoke(CurrentTrial);

        int reps = _isPracticeBlock ? PracticeTrials : RepsPerCondition;
        if (_currentRep < reps)
        {
            _currentRep++;
            _currentTrialCountInSpeed = PickCountInSpeed();
            OnTrialStarted?.Invoke(CurrentTrial);
        }
        else if (_isPracticeBlock)
        {
            _blockActive = false;
            _isPracticeBlock = false;
            currentBlock = "(practice done)";
            OnPracticeEnded?.Invoke();
        }
        else
        {
            _blockActive = false;
            OnBlockEnded?.Invoke(_condition.conditionId);
        }
    }

    /// <summary>Called by the UI when the participant submits the block's five ratings.</summary>
    public void SubmitRatings(RatingsData ratings)
    {
        _doneConditions.Add(_condition.conditionId);
        conditionsCompleted = _doneConditions.Count;
        OnRatingsSubmitted?.Invoke(_condition.conditionId, ratings);

        // Let every ratings subscriber store the final answers before completion saves them.
        if (AllConditionsDone) Finish();
    }

    /// <summary>End the whole session (experimenter pressed Finish, or all six conditions done).</summary>
    public void Finish() => OnSessionComplete?.Invoke();

    /// <summary>Count-in speed for the trial about to run, per the inspector mode.</summary>
    private float PickCountInSpeed()
    {
        if (UsesRandomCountIn && randomCountInSpeeds != null && randomCountInSpeeds.Length > 0)
        {
            float s = randomCountInSpeeds[UnityEngine.Random.Range(0, randomCountInSpeeds.Length)];
            if (s > 0f) return s;
        }
        return countInSecondsPerCount > 0f ? countInSecondsPerCount : 0.5f;
    }

    private static bool TryParseLeadingInt(string s, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;
        int i = 0; while (i < s.Length && !char.IsDigit(s[i])) i++;
        int start = i; while (i < s.Length && char.IsDigit(s[i])) i++;
        return start < i && int.TryParse(s.Substring(start, i - start), out value);
    }
}
