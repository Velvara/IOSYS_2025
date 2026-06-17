using System;
using UnityEngine;
using RootMotion.FinalIK;

namespace Game.Climbing
{
    /// <summary>The five climbing effectors. Order matters — used as array indices.</summary>
    public enum ClimbEffector
    {
        RootBody = 0,
        LeftHand = 1,
        RightHand = 2,
        LeftFoot = 3,
        RightFoot = 4
    }

    /// <summary>
    /// Drives the player's <see cref="FullBodyBipedIK"/> from five climbing effectors (root/body +
    /// both hands + both feet). Each effector interpolates its CURRENT pose from LAST toward NEXT with
    /// a per-effector delay/lag and an ease curve (article's effector model, Snippet 1). The climbing
    /// controller assigns a NEXT target — usually a hold Transform, so the effector tracks moving
    /// surfaces — and ticks this each frame; the rig writes position/rotation/weight into the FBBIK
    /// effectors, which solve the limbs.
    ///
    /// Plain class owned and driven by ClimbController (no MonoBehaviour) for explicit ordering.
    /// Refinements deferred: article's sin-based rotation weighting, jump foot-stretch, and the
    /// per-effector offset curve are stubbed via <see cref="OffsetProvider"/> until later phases.
    /// </summary>
    public class EffectorRig
    {
        private const int Count = 5;

        private readonly FullBodyBipedIK _ik;
        private readonly AnimationCurve _ease;

        private readonly Vector3[] _lastPos = new Vector3[Count];
        private readonly Vector3[] _currentPos = new Vector3[Count];
        private readonly Quaternion[] _lastRot = new Quaternion[Count];
        private readonly Quaternion[] _currentRot = new Quaternion[Count];

        private readonly Transform[] _nextHold = new Transform[Count];        // target hold (may move); null => fallback pose
        private readonly Vector3[] _nextPosFallback = new Vector3[Count];
        private readonly Quaternion[] _nextRotFallback = new Quaternion[Count];

        private readonly float[] _moveDuration = new float[Count];
        private readonly float[] _elapsed = new float[Count];
        private readonly float[] _delay = new float[Count];
        private readonly float[] _lag = new float[Count];
        private readonly bool[] _moving = new bool[Count];

        // Per-effector weight (0..1), multiplied by MasterWeight. Lets e.g. feet stay IK-free
        // (weight 0) during a free-hang while hands are fully driven.
        private readonly float[] _weight = new float[Count];

        // Per-effector rotation offset, applied at write time (e.g. live-tunable hand grip).
        private readonly Quaternion[] _rotOffset = new Quaternion[Count];

        /// <summary>Master weight, faded 0→1 on grab and 1→0 on release. Scales all effector weights.</summary>
        public float MasterWeight { get; private set; }

        /// <summary>
        /// Optional per-effector positional offset (e.g. a reach "bow" during a move). Argument is the
        /// signed normalized progress in [-0.5, 0.5] (0 at mid-move). Supplied by the controller; null = none.
        /// </summary>
        public Func<ClimbEffector, float, Vector3> OffsetProvider;

        public EffectorRig(FullBodyBipedIK ik, AnimationCurve ease)
        {
            _ik = ik;
            _ease = ease ?? AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

            // Quaternion default is all-zero (invalid for Slerp) — initialise to identity.
            for (int i = 0; i < Count; i++)
            {
                _lastRot[i] = Quaternion.identity;
                _currentRot[i] = Quaternion.identity;
                _nextRotFallback[i] = Quaternion.identity;
                _weight[i] = 1f;
                _rotOffset[i] = Quaternion.identity;
            }
        }

        public bool IsValid => _ik != null && _ik.solver != null;

        public Vector3 GetCurrentPosition(ClimbEffector e) => _currentPos[(int)e];
        public bool IsMoving(ClimbEffector e) => _moving[(int)e];

        /// <summary>True while any effector is mid-move (drives stamina "moving between holds").</summary>
        public bool AnyMoving
        {
            get
            {
                for (int i = 0; i < Count; i++) if (_moving[i]) return true;
                return false;
            }
        }

        /// <summary>Average of the two hand CURRENT positions — the root offset base / pendulum anchor.</summary>
        public Vector3 HandAverage =>
            (_currentPos[(int)ClimbEffector.LeftHand] + _currentPos[(int)ClimbEffector.RightHand]) * 0.5f;

        public void SetMasterWeight(float w) => MasterWeight = Mathf.Clamp01(w);

        /// <summary>Per-effector weight (multiplied by MasterWeight). Default 1; set feet to 0 for free-hang.</summary>
        public void SetEffectorWeight(ClimbEffector e, float w) => _weight[(int)e] = Mathf.Clamp01(w);

        /// <summary>Per-effector rotation offset applied at write time (e.g. live-tunable hand grip).</summary>
        public void SetRotationOffset(ClimbEffector e, Quaternion offset) => _rotOffset[(int)e] = offset;

        /// <summary>Instantly lock an effector to a fixed world pose (snap-grab with computed hold poses).</summary>
        public void SnapToPose(ClimbEffector e, Vector3 pos, Quaternion rot)
        {
            int i = (int)e;
            _nextHold[i] = null;
            _nextPosFallback[i] = pos;
            _nextRotFallback[i] = rot;
            _currentPos[i] = _lastPos[i] = pos;
            _currentRot[i] = _lastRot[i] = rot;
            _moving[i] = false;
        }

        // -- Target assignment (controller calls these) --

        /// <summary>Move an effector toward a hold Transform over <paramref name="duration"/> seconds.</summary>
        public void SetHoldTarget(ClimbEffector e, Transform hold, float duration, float delay = 0f, float lag = 0f)
        {
            int i = (int)e;
            _lastPos[i] = _currentPos[i];
            _lastRot[i] = _currentRot[i];
            _nextHold[i] = hold;
            _moveDuration[i] = Mathf.Max(0.0001f, duration);
            _delay[i] = Mathf.Max(0f, delay);
            _lag[i] = lag;
            _elapsed[i] = 0f;
            _moving[i] = true;
        }

        /// <summary>Move an effector toward a fixed world pose (e.g. a raycast foot point) over a duration.</summary>
        public void SetPoseTarget(ClimbEffector e, Vector3 pos, Quaternion rot, float duration, float delay = 0f, float lag = 0f)
        {
            int i = (int)e;
            _lastPos[i] = _currentPos[i];
            _lastRot[i] = _currentRot[i];
            _nextHold[i] = null;
            _nextPosFallback[i] = pos;
            _nextRotFallback[i] = rot;
            _moveDuration[i] = Mathf.Max(0.0001f, duration);
            _delay[i] = Mathf.Max(0f, delay);
            _lag[i] = lag;
            _elapsed[i] = 0f;
            _moving[i] = true;
        }

        /// <summary>Instantly lock an effector to a hold (snap-grab on entry, no interpolation).</summary>
        public void SnapToHold(ClimbEffector e, Transform hold)
        {
            int i = (int)e;
            _nextHold[i] = hold;
            _currentPos[i] = _lastPos[i] = hold.position;
            _currentRot[i] = _lastRot[i] = hold.rotation;
            _moving[i] = false;
        }

        /// <summary>Per-frame update: interpolate each effector and write the result into FBBIK.</summary>
        public void Tick(float dt)
        {
            if (!IsValid) return;

            for (int i = 0; i < Count; i++)
            {
                if (_moving[i])
                    StepMove(i, dt);
                else if (_nextHold[i] != null)
                {
                    // Reached + attached to a (possibly moving) hold: track it.
                    _currentPos[i] = _nextHold[i].position;
                    _currentRot[i] = _nextHold[i].rotation;
                }

                WriteEffector(i);
            }
        }

        private void StepMove(int i, float dt)
        {
            _elapsed[i] += dt;

            // Article timing: base duration shortened by delay, lengthened by lag; progress is
            // measured after the delay has elapsed.
            float time = Mathf.Max(0.0001f, _moveDuration[i] - _delay[i] + _lag[i]);
            float adjusted = Mathf.Clamp(_elapsed[i] - _delay[i], 0f, time);

            Vector3 nextPos = _nextHold[i] != null ? _nextHold[i].position : _nextPosFallback[i];
            Quaternion nextRot = _nextHold[i] != null ? _nextHold[i].rotation : _nextRotFallback[i];

            if (adjusted >= time)
            {
                _currentPos[i] = nextPos;
                _currentRot[i] = nextRot;
                _lastPos[i] = nextPos;
                _lastRot[i] = nextRot;
                _moving[i] = false;
                return;
            }

            float t = adjusted / time;
            float eased = _ease.Evaluate(t);
            _currentPos[i] = Vector3.LerpUnclamped(_lastPos[i], nextPos, eased);
            _currentRot[i] = Quaternion.SlerpUnclamped(_lastRot[i], nextRot, eased);

            if (OffsetProvider != null)
                _currentPos[i] += OffsetProvider((ClimbEffector)i, Mathf.Clamp(t - 0.5f, -0.5f, 0.5f));
        }

        private void WriteEffector(int i)
        {
            IKEffector eff = EffectorFor((ClimbEffector)i);
            if (eff == null) return;

            float w = MasterWeight * _weight[i];
            eff.position = _currentPos[i];
            eff.rotation = _currentRot[i] * _rotOffset[i];
            eff.positionWeight = w;
            eff.rotationWeight = w;
        }

        private IKEffector EffectorFor(ClimbEffector e)
        {
            switch (e)
            {
                case ClimbEffector.RootBody: return _ik.solver.bodyEffector;
                case ClimbEffector.LeftHand: return _ik.solver.leftHandEffector;
                case ClimbEffector.RightHand: return _ik.solver.rightHandEffector;
                case ClimbEffector.LeftFoot: return _ik.solver.leftFootEffector;
                case ClimbEffector.RightFoot: return _ik.solver.rightFootEffector;
                default: return null;
            }
        }
    }
}
