using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
public class NPCController : MonoBehaviour
{
    #region A) Identity

    [Header("A) Identity")]
    public string npcName = "NPC";

    #endregion

    #region B) References

    [Header("B) References")]
    public Transform player;
    public string playerTag = "Player";

    public Animator animator;
    public AudioSource audioSource;

    public TextMeshProUGUI subtitleTMP;
    public string subtitleTag = "SubtitleTMP";

    #endregion

    #region C) Output

    [Header("C) Output")]
    public bool enableSubtitles = true;
    public bool enableVoice = true;

    [Tooltip("If player leaves proximity => stop audio + hide subtitle immediately.")]
    public bool stopOutputOnExitRange = true;

    #endregion

    #region D) Movement

    [Header("D) Movement")]
    public float walkSpeed = 2.2f;     // patrol uses WALK
    public float runSpeed = 4.8f;      // attack run away uses RUN

    public float stoppingDistance = 1.3f;
    public float motionSpeedDamp = 0.12f;

    #endregion

    #region E) Proximity

    [Header("E) Proximity")]
    public float proximityRadius = 6f;

    #endregion

    #region F) Patrol (FIXED)

    [Header("F) Patrol (Fixed)")]
    public List<Transform> patrolPoints = new List<Transform>();
    public float idleAtPointSeconds = 1.0f;

    [Tooltip("Uses agent.remainingDistance (stable).")]
    public float reachPointDistance = 0.6f;

    #endregion

    #region G) Talk / Dialogue

    [Serializable]
    public class DialogueLine
    {
        [TextArea(1, 3)] public string text;
        public AudioClip voice;
        public float duration = 2f; // fallback
    }

    public enum DialogueMode { Random, Sequential }

    [Header("G) Proximity Talk")]
    public bool talkOnEnterRange = true;
    public bool talkWhileInRange = false;

    public List<DialogueLine> proximityLines = new List<DialogueLine>();
    public DialogueMode proximityMode = DialogueMode.Random;
    public float proximityTalkInterval = 4f;

    [Header("G1) Talk Rules")]
    public bool mustFinishCurrentLine = true;

    // ✅ NEW: mutually exclusive talk movement mode
    public enum TalkMovementMode { StopToTalk, TalkWhileMoving }

    [Header("G2) Talk Movement (Choose ONE)")]
    [Tooltip("StopToTalk = NPC stops when speaking. TalkWhileMoving = NPC keeps moving while speaking.")]
    public TalkMovementMode talkMovementMode = TalkMovementMode.StopToTalk;

    // ✅ NEW: face player while talking
    [Header("G3) Face Player While Talking")]
    public bool facePlayerWhileTalking = true;
    public float faceTurnSpeed = 12f;

    #endregion

    #region H) In-Range Movement (Choose ONE)

    public enum InRangeMoveMode
    {
        None,           // do nothing special
        ApproachPlayer, // go near player (walk)
        FollowPlayer    // keep following player (run)
    }

    [Header("H) In-Range Move Mode (Choose ONE)")]
    [Tooltip("Choose ONE: None / ApproachPlayer / FollowPlayer")]
    public InRangeMoveMode inRangeMoveMode = InRangeMoveMode.None;

    #endregion

    #region I) On Attacked (Run Options)

    public enum AttackReactionMode
    {
        None,
        JustRun,
        SpeakThenRun,
        RunWhileSpeaking
    }

    [Header("I) On Attacked")]
    public AttackReactionMode attackReaction = AttackReactionMode.None;

    public List<DialogueLine> attackedLines = new List<DialogueLine>();
    public DialogueMode attackedMode = DialogueMode.Random;
    public float attackedTalkInterval = 2f;

    [Tooltip("Run destination (must be on NavMesh).")]
    public Transform safePoint;

    public float stayAtSafePointSeconds = 3f;

    #endregion

    #region TESTING

#if UNITY_EDITOR
    [Header("TESTING")]
    [Tooltip("Tick to simulate attacked ONCE (auto resets). Play mode only.")]
    public bool TEST_IsAttacked;

    private void OnValidate()
    {
        // keep values sane
        proximityRadius = Mathf.Max(0.1f, proximityRadius);
        stoppingDistance = Mathf.Max(0.05f, stoppingDistance);
        walkSpeed = Mathf.Max(0.1f, walkSpeed);
        runSpeed = Mathf.Max(walkSpeed, runSpeed);

        if (!Application.isPlaying) return;

        if (TEST_IsAttacked)
        {
            TEST_IsAttacked = false;
            OnAttacked();
        }
    }
#endif

    #endregion

    #region Internal

    private NavMeshAgent _agent;

    private Vector3 _homePos;
    private Quaternion _homeRot;

    private bool _inRange;
    private bool _wasInRange;

    private bool _isTalking;
    private bool _isRunning;

    private float _nextProxTalkTime = -999f;
    private float _nextAtkTalkTime = -999f;

    private int _seqProxIndex;
    private int _seqAtkIndex;

    private Coroutine _talkCo;

    private float _motionVelRef;
    private static readonly int MotionSpeedHash = Animator.StringToHash("MotionSpeed");

    // patrol state
    private int _patrolIndex;
    private bool _patrolWaiting;
    private bool _hasPatrolDest;
    private Vector3 _currentPatrolDest;

    #endregion

    #region Unity

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _homePos = transform.position;
        _homeRot = transform.rotation;

        if (!audioSource) audioSource = GetComponent<AudioSource>();
        if (!animator) animator = GetComponent<Animator>();

        if (!subtitleTMP && !string.IsNullOrEmpty(subtitleTag))
        {
            var go = GameObject.FindGameObjectWithTag(subtitleTag);
            if (go) subtitleTMP = go.GetComponent<TextMeshProUGUI>();
        }

        FindPlayerIfNeeded();

        _agent.stoppingDistance = stoppingDistance;
        _agent.speed = walkSpeed;
        _agent.isStopped = false;
        _agent.autoBraking = true;

        HideSubtitle();
        SetNextPatrolDestination(force: true);
    }

    private void Update()
    {
        FindPlayerIfNeeded();

        _agent.stoppingDistance = stoppingDistance;
        UpdateMotionSpeed();

        // ✅ Face player while talking
        if (_isTalking && facePlayerWhileTalking)
            FacePlayerSmooth();

        _inRange = IsPlayerInRange();

        if (_wasInRange && !_inRange && stopOutputOnExitRange)
            ForceStopOutput();

        if (!_wasInRange && _inRange && talkOnEnterRange)
            TryTalkProximity();

        _wasInRange = _inRange;

        if (_isRunning) return;

        // Movement priority:
        // 1) In-range move mode
        // 2) Patrol (walk)
        if (_inRange)
        {
            if (inRangeMoveMode == InRangeMoveMode.FollowPlayer)
                FollowPlayer();
            else if (inRangeMoveMode == InRangeMoveMode.ApproachPlayer)
                ApproachPlayer();
            else
                PatrolTickFixed();
        }
        else
        {
            PatrolTickFixed();
        }

        if (_inRange && talkWhileInRange)
            TryTalkProximity();
    }

    private void FindPlayerIfNeeded()
    {
        if (!player && !string.IsNullOrEmpty(playerTag))
        {
            var p = GameObject.FindGameObjectWithTag(playerTag);
            if (p) player = p.transform;
        }
    }

    #endregion

    #region Patrol (Fixed)

    private void PatrolTickFixed()
    {
        if (_isTalking && talkMovementMode == TalkMovementMode.StopToTalk) return;
        if (patrolPoints == null || patrolPoints.Count == 0) return;
        if (_patrolWaiting) return;

        if (_agent.speed != walkSpeed) _agent.speed = walkSpeed;

        if (!_hasPatrolDest)
        {
            SetNextPatrolDestination(force: true);
            return;
        }

        if (_agent.pathPending) return;

        if (_agent.pathStatus == NavMeshPathStatus.PathInvalid)
        {
            SetNextPatrolDestination(force: true);
            return;
        }

        if (_agent.hasPath && _agent.remainingDistance <= Mathf.Max(reachPointDistance, _agent.stoppingDistance + 0.05f))
        {
            if (_agent.velocity.sqrMagnitude < 0.02f)
                StartCoroutine(PatrolWaitThenNext());
        }
    }

    private IEnumerator PatrolWaitThenNext()
    {
        _patrolWaiting = true;
        yield return new WaitForSeconds(Mathf.Max(0f, idleAtPointSeconds));
        _patrolWaiting = false;

        _patrolIndex = (_patrolIndex + 1) % patrolPoints.Count;
        SetNextPatrolDestination(force: true);
    }

    private void SetNextPatrolDestination(bool force)
    {
        if (patrolPoints == null || patrolPoints.Count == 0) return;

        var t = patrolPoints[_patrolIndex];
        if (!t)
        {
            _patrolIndex = (_patrolIndex + 1) % patrolPoints.Count;
            return;
        }

        Vector3 dest = t.position;

        if (!force && _hasPatrolDest && Vector3.SqrMagnitude(dest - _currentPatrolDest) < 0.01f)
            return;

        _currentPatrolDest = dest;
        _hasPatrolDest = true;

        _agent.isStopped = false;
        _agent.SetDestination(dest);
    }

    #endregion

    #region In-Range Movement

    private void FollowPlayer()
    {
        if (!player) return;
        if (_isTalking && talkMovementMode == TalkMovementMode.StopToTalk) return;

        _agent.speed = walkSpeed;
        _agent.isStopped = false;
        _agent.SetDestination(player.position);
    }

    private void ApproachPlayer()
    {
        if (!player) return;
        if (_isTalking && talkMovementMode == TalkMovementMode.StopToTalk) return;

        _agent.speed = walkSpeed;
        _agent.isStopped = false;
        _agent.SetDestination(player.position);
    }

    #endregion

    #region Talking

    private void TryTalkProximity()
    {
        if (proximityLines == null || proximityLines.Count == 0) return;
        if (mustFinishCurrentLine && _isTalking) return;
        if (Time.time < _nextProxTalkTime) return;

        // If ApproachPlayer is selected, wait until close enough
        if (inRangeMoveMode == InRangeMoveMode.ApproachPlayer && player)
        {
            if (Vector3.Distance(transform.position, player.position) > stoppingDistance + 0.2f)
                return;
        }

        var line = PickLine(proximityLines, proximityMode, ref _seqProxIndex);

        bool stopMovementWhileTalking = (talkMovementMode == TalkMovementMode.StopToTalk);
        StartTalk(line, stopMovementWhileTalking);

        _nextProxTalkTime = Time.time + Mathf.Max(0.2f, proximityTalkInterval);
    }

    private void StartTalk(DialogueLine line, bool stopMovementWhileTalking)
    {
        if (line == null) return;

        if (!mustFinishCurrentLine && _talkCo != null)
        {
            StopCoroutine(_talkCo);
            _talkCo = null;
            _isTalking = false;
        }
        else
        {
            if (_isTalking) return;
        }

        _talkCo = StartCoroutine(TalkRoutine(line, stopMovementWhileTalking));
    }

    private IEnumerator TalkRoutine(DialogueLine line, bool stopMovementWhileTalking)
    {
        _isTalking = true;

        if (stopMovementWhileTalking)
            _agent.isStopped = true;

        if (enableSubtitles && subtitleTMP)
        {
            subtitleTMP.gameObject.SetActive(true);
            subtitleTMP.text = $"{npcName}: {line.text}";
        }

        float waitTime = Mathf.Max(0.2f, line.duration);

        if (enableVoice && audioSource && line.voice)
        {
            audioSource.Stop();
            audioSource.clip = line.voice;
            audioSource.Play();
            waitTime = Mathf.Max(waitTime, line.voice.length);
        }

        yield return new WaitForSeconds(waitTime);

        HideSubtitle();

        if (stopMovementWhileTalking)
            _agent.isStopped = false;

        _isTalking = false;
    }

    private void ForceStopOutput()
    {
        HideSubtitle();
        if (audioSource) audioSource.Stop();

        if (!mustFinishCurrentLine)
        {
            if (_talkCo != null) StopCoroutine(_talkCo);
            _talkCo = null;
            _isTalking = false;
        }
    }

    private void HideSubtitle()
    {
        if (!subtitleTMP) return;
        subtitleTMP.text = "";
        subtitleTMP.gameObject.SetActive(false);
    }

    private DialogueLine PickLine(List<DialogueLine> list, DialogueMode mode, ref int seqIndex)
    {
        if (list == null || list.Count == 0) return null;

        if (mode == DialogueMode.Random)
            return list[UnityEngine.Random.Range(0, list.Count)];

        if (seqIndex < 0) seqIndex = 0;
        if (seqIndex >= list.Count) seqIndex = 0;

        var line = list[seqIndex];
        seqIndex = (seqIndex + 1) % list.Count;
        return line;
    }

    #endregion

    #region On Attacked

    public void OnAttacked()
    {
         if (attackInterruptsAnyTalk)
        ForceStopAllTalkingNow(); 

        if (attackReaction == AttackReactionMode.None) return;

        if (attackReaction == AttackReactionMode.JustRun)
        {
            ForceStopAllTalkingNow();
            StartCoroutine(RunAwayRoutine());
            return;
        }

        if (attackReaction == AttackReactionMode.SpeakThenRun)
        {
            StartCoroutine(SpeakThenRunRoutine());
            return;
        }

        if (attackReaction == AttackReactionMode.RunWhileSpeaking)
        {
            StartCoroutine(RunWhileSpeakingRoutine());
            return;
        }
    }

    private IEnumerator SpeakThenRunRoutine()
    {
        if (attackedLines != null && attackedLines.Count > 0 && Time.time >= _nextAtkTalkTime)
        {
            var line = PickLine(attackedLines, attackedMode, ref _seqAtkIndex);
            _nextAtkTalkTime = Time.time + Mathf.Max(0.2f, attackedTalkInterval);
            yield return TalkRoutine(line, stopMovementWhileTalking: true);
        }

        ForceStopAllTalkingNow();
        yield return RunAwayRoutine();
    }

    private IEnumerator RunWhileSpeakingRoutine()
    {
        var runCo = StartCoroutine(RunAwayRoutine());

        if (attackedLines != null && attackedLines.Count > 0 && Time.time >= _nextAtkTalkTime)
        {
            if (!mustFinishCurrentLine || !_isTalking)
            {
                var line = PickLine(attackedLines, attackedMode, ref _seqAtkIndex);
                _nextAtkTalkTime = Time.time + Mathf.Max(0.2f, attackedTalkInterval);

                // talk while moving
                StartTalk(line, stopMovementWhileTalking: false);
            }
        }

        yield return runCo;
    }

    private IEnumerator RunAwayRoutine()
    {
        _isRunning = true;

        _agent.speed = runSpeed;
        _agent.isStopped = false;

        if (!safePoint)
            _agent.SetDestination(_homePos);
        else
            _agent.SetDestination(safePoint.position);

        float t = 0f;
        while (t < 8f)
        {
            t += Time.deltaTime;
            if (!_agent.pathPending && _agent.hasPath &&
                _agent.remainingDistance <= Mathf.Max(reachPointDistance, _agent.stoppingDistance + 0.05f))
            {
                if (_agent.velocity.sqrMagnitude < 0.02f)
                    break;
            }
            yield return null;
        }

        yield return new WaitForSeconds(Mathf.Max(0f, stayAtSafePointSeconds));

        _agent.speed = walkSpeed;
        _agent.isStopped = false;
        _agent.SetDestination(_homePos);

        float t2 = 0f;
        while (t2 < 10f)
        {
            t2 += Time.deltaTime;
            if (!_agent.pathPending && _agent.hasPath &&
                _agent.remainingDistance <= Mathf.Max(reachPointDistance, _agent.stoppingDistance + 0.05f))
            {
                if (_agent.velocity.sqrMagnitude < 0.02f)
                    break;
            }
            yield return null;
        }

        transform.rotation = _homeRot;
        SetNextPatrolDestination(force: true);

        _isRunning = false;
    }

    private void ForceStopAllTalkingNow()
    {
        HideSubtitle();
        if (audioSource) audioSource.Stop();

        if (_talkCo != null)
        {
            StopCoroutine(_talkCo);
            _talkCo = null;
        }

        _isTalking = false;
        _agent.isStopped = false;
    }
    [Tooltip("If attacked, instantly cancel any current NPC talk and switch to attack reaction.")]
public bool attackInterruptsAnyTalk = true;


    #endregion

    #region Face Player While Talking

    private void FacePlayerSmooth()
    {
        if (!player) return;

        Vector3 dir = player.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;

        Quaternion targetRot = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * Mathf.Max(1f, faceTurnSpeed));
    }

    #endregion

    #region Helpers

    private bool IsPlayerInRange()
    {
        if (!player) return false;
        return Vector3.Distance(transform.position, player.position) <= proximityRadius;
    }

    private void UpdateMotionSpeed()
    {
        if (!animator || _agent == null) return;

        float max = Mathf.Max(0.01f, runSpeed);
        float normalized = Mathf.Clamp01(_agent.velocity.magnitude / max);

        float cur = animator.GetFloat(MotionSpeedHash);
        float vel = _motionVelRef;
        float smoothed = Mathf.SmoothDamp(cur, normalized, ref vel, motionSpeedDamp);
        _motionVelRef = vel;

        animator.SetFloat(MotionSpeedHash, smoothed);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, proximityRadius);

        if (safePoint)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(safePoint.position, 0.3f);
        }
    }
#endif

    #endregion
}
