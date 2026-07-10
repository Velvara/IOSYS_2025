using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// A muscle-fatigue tremble for IK bend directions. As stamina drains past <see cref="startFraction"/>
    /// while the character is moving, the elbows/knees pick up a small, growing horizontal jitter.
    /// Strength ramps 0 → 1 from startFraction stamina down to empty; both amplitude and frequency scale
    /// with strength, so it begins almost imperceptible and intensifies. Shared by the climb and rappel
    /// controllers (each owns its own instance so the two can be tuned independently).
    /// </summary>
    [System.Serializable]
    public class FatigueJitter
    {
        [Tooltip("Normalized stamina (0-1) at which the tremble begins. At/above this, no jitter.")]
        [Range(0f, 1f)] public float startFraction = 0.25f;
        [Tooltip("Tremble frequency at EMPTY stamina (oscillations/sec). Scales down toward 0 as stamina rises to startFraction.")]
        public float endFrequency = 16f;
        [Tooltip("Tremble amplitude — magnitude ADDED to the (normalized) bend direction — at EMPTY stamina. Keep small; scales to 0 at startFraction.")]
        public float endAmplitude = 0.12f;

        private float _phase;

        /// <summary>0 at startFraction stamina → 1 at empty; 0 above startFraction.</summary>
        public float Strength(float staminaFraction) =>
            startFraction <= 0f ? 0f : Mathf.Clamp01((startFraction - staminaFraction) / startFraction);

        /// <summary>Advance the oscillator once per frame. Frequency scales with strength (slower + tinier
        /// when barely fatigued), so the shared phase never jumps when strength changes.</summary>
        public void Advance(float strength, float dt)
        {
            _phase += endFrequency * strength * dt * (Mathf.PI * 2f);
            if (_phase > Mathf.PI * 2f) _phase -= Mathf.PI * 2f;
        }

        /// <summary>Perturb a normalized bend <paramref name="direction"/> with the tremble along
        /// <paramref name="horizontalAxis"/>. <paramref name="limbPhase"/> decorrelates the four limbs so
        /// they don't shake in unison. Returns the (still-unnormalized) perturbed direction.</summary>
        public Vector3 Perturb(Vector3 direction, Vector3 horizontalAxis, float strength, float limbPhase)
        {
            if (strength <= 0f) return direction;
            float a = endAmplitude * strength * Mathf.Sin(_phase + limbPhase);
            return direction + horizontalAxis * a;
        }
    }
}
