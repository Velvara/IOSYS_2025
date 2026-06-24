using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// Code-driven two-mass pendulum (no rigidbodies/joints — see climbing design decision). Models
    /// two spring-connected point masses hanging from a moving anchor (the hand-average while
    /// free-hanging). Deterministic and stepped at a fixed dt; the body swing emerges naturally when
    /// the anchor moves (secondary motion) and from gravity. <see cref="CharacterPendulum"/> reads
    /// the mass positions to drive the hips/chest bones.
    ///
    /// Chain, from the grab point downward:  Anchor (hands) → UpperMass (≈ chest) → LowerMass (≈ hips).
    /// Plain class, stepped from the controller's FixedUpdate (like PlayerMotor / OscillatorBank).
    /// </summary>
    public class TwoMassPendulum
    {
        // -- Tuning --
        /// <summary>Rest length from the anchor (hands) to the upper mass (≈ chest).</summary>
        public float AnchorToUpper = 0.55f;
        /// <summary>Rest length from the upper mass (≈ chest) to the lower mass (≈ hips).</summary>
        public float UpperToLower = 0.45f;
        public float UpperMass = 1f;
        public float LowerMass = 1.2f;
        /// <summary>Spring constant pulling each segment back to its rest length.</summary>
        public float Stiffness = 160f;
        /// <summary>Velocity damping (higher = settles faster, less swing).</summary>
        public float Damping = 4f;
        public float Gravity = 9.81f;
        /// <summary>Unit "down" the masses hang toward. World <see cref="Vector3.down"/> normally; the
        /// climber points it along the trunk axis (−TrunkUp) when braced on a bent trunk so the body
        /// hangs along the limb instead of straight down through it.</summary>
        public Vector3 GravityDir = Vector3.down;

        // -- State --
        public Vector3 Anchor { get; private set; }
        public Vector3 UpperPos { get; private set; }   // ≈ chest
        public Vector3 LowerPos { get; private set; }   // ≈ hips

        private Vector3 _vUpper, _vLower;
        private bool _initialized;

        /// <summary>Places the masses hanging straight down from the anchor and clears velocity.</summary>
        public void Reset(Vector3 anchor)
        {
            Anchor = anchor;
            Vector3 down = GravityDir.sqrMagnitude > 1e-6f ? GravityDir.normalized : Vector3.down;
            UpperPos = anchor + down * AnchorToUpper;
            LowerPos = UpperPos + down * UpperToLower;
            _vUpper = _vLower = Vector3.zero;
            _initialized = true;
        }

        public void SetAnchor(Vector3 anchor) => Anchor = anchor;

        /// <summary>Advances the simulation one fixed step (semi-implicit Euler).</summary>
        public void Step(float dt)
        {
            if (!_initialized) { Reset(Anchor); return; }
            if (dt <= 0f) return;

            Vector3 down = GravityDir.sqrMagnitude > 1e-6f ? GravityDir.normalized : Vector3.down;
            Vector3 g = down * Gravity;

            // Segment 1: anchor -> upper mass.
            Vector3 d1 = UpperPos - Anchor;
            float len1 = d1.magnitude;
            Vector3 n1 = len1 > 1e-5f ? d1 / len1 : down;
            Vector3 spring1 = -Stiffness * (len1 - AnchorToUpper) * n1;

            // Segment 2: upper mass -> lower mass.
            Vector3 d2 = LowerPos - UpperPos;
            float len2 = d2.magnitude;
            Vector3 n2 = len2 > 1e-5f ? d2 / len2 : down;
            Vector3 spring2 = -Stiffness * (len2 - UpperToLower) * n2;

            // Upper mass feels spring1 (toward anchor), the reaction of spring2, gravity, damping.
            Vector3 fUpper = spring1 - spring2 + g * UpperMass - Damping * _vUpper;
            Vector3 fLower = spring2 + g * LowerMass - Damping * _vLower;

            _vUpper += (fUpper / Mathf.Max(0.0001f, UpperMass)) * dt;
            _vLower += (fLower / Mathf.Max(0.0001f, LowerMass)) * dt;
            UpperPos += _vUpper * dt;
            LowerPos += _vLower * dt;
        }

        /// <summary>Anchor → upper-mass direction (upper segment swing), for deriving body lean.</summary>
        public Vector3 UpperDir =>
            (UpperPos - Anchor).sqrMagnitude > 1e-6f ? (UpperPos - Anchor).normalized : Vector3.down;

        /// <summary>Upper-mass → lower-mass direction (lower segment swing).</summary>
        public Vector3 LowerDir =>
            (LowerPos - UpperPos).sqrMagnitude > 1e-6f ? (LowerPos - UpperPos).normalized : Vector3.down;
    }
}
