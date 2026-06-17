using System.Collections.Generic;
using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// One under-damped harmonic oscillator that springs back to rest at 0. "Kick" it with an
    /// impulse (<see cref="Fire"/>) on an event — grab, wall impact, jump wind-up/stretch, landing —
    /// and it oscillates back to zero with damping, producing the body sway and impact cushioning.
    /// The scalar <see cref="Value"/> is applied along <see cref="Direction"/> to offset the root.
    ///
    /// Plain class (no MonoBehaviour): stepped explicitly from the climbing controller's FixedUpdate
    /// so the integration runs at a fixed timestep with deterministic ordering, like PlayerMotor.
    /// </summary>
    public class Oscillator
    {
        /// <summary>Natural angular frequency (rad/s). Higher = faster oscillation / stiffer spring.</summary>
        public float AngularFrequency;
        /// <summary>Damping ratio. 0 = undamped, &lt;1 = under-damped (oscillates), 1 = critical.</summary>
        public float DampingRatio;
        /// <summary>Direction the scalar value is applied along (normalized).</summary>
        public Vector3 Direction;

        public float Value { get; private set; }
        public float Velocity { get; private set; }

        public Oscillator(float angularFrequency, float dampingRatio, Vector3 direction)
        {
            AngularFrequency = Mathf.Max(0f, angularFrequency);
            DampingRatio = Mathf.Max(0f, dampingRatio);
            Direction = direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.zero;
        }

        /// <summary>Impulse the oscillator (adds to velocity), e.g. on grab / land / jump.</summary>
        public void Fire(float impulse) => Velocity += impulse;

        /// <summary>Displace the value directly (e.g. a sustained offset that then springs back).</summary>
        public void Displace(float offset) => Value += offset;

        /// <summary>Advance one fixed step (semi-implicit Euler; stable for FixedUpdate dt).</summary>
        public void Step(float dt)
        {
            // Damped spring toward 0:  a = -ω²·x − 2ζω·v
            float accel = -(AngularFrequency * AngularFrequency) * Value
                          - 2f * DampingRatio * AngularFrequency * Velocity;
            Velocity += accel * dt;
            Value += Velocity * dt;
        }

        public void Reset()
        {
            Value = 0f;
            Velocity = 0f;
        }

        /// <summary>The current offset vector (Value · Direction).</summary>
        public Vector3 Offset => Direction * Value;
    }

    /// <summary>
    /// Named collection of <see cref="Oscillator"/>s, accessed by name (article: oscillators stored
    /// in a dictionary, modified as needed). The climbing controller registers the springs it needs
    /// (e.g. "wallImpact", "sideSway", "jumpWindUp", "jumpStretch", "landingCompression"), fires them
    /// on events, steps them all each FixedUpdate, and reads back their offsets to apply to the root.
    /// </summary>
    public class OscillatorBank
    {
        private readonly Dictionary<string, Oscillator> _oscillators = new Dictionary<string, Oscillator>();

        /// <summary>Registers (or overwrites) a named oscillator and returns it.</summary>
        public Oscillator Add(string name, float angularFrequency, float dampingRatio, Vector3 direction)
        {
            var osc = new Oscillator(angularFrequency, dampingRatio, direction);
            _oscillators[name] = osc;
            return osc;
        }

        public bool TryGet(string name, out Oscillator osc) => _oscillators.TryGetValue(name, out osc);

        public Oscillator Get(string name) => _oscillators.TryGetValue(name, out var osc) ? osc : null;

        public bool Has(string name) => _oscillators.ContainsKey(name);

        /// <summary>Impulses a named oscillator if present.</summary>
        public void Fire(string name, float impulse)
        {
            if (_oscillators.TryGetValue(name, out var osc)) osc.Fire(impulse);
        }

        /// <summary>Updates the direction a named oscillator pushes along (e.g. sway follows traversal).</summary>
        public void SetDirection(string name, Vector3 direction)
        {
            if (_oscillators.TryGetValue(name, out var osc))
                osc.Direction = direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.zero;
        }

        public float GetValue(string name) => _oscillators.TryGetValue(name, out var osc) ? osc.Value : 0f;

        public Vector3 GetDirection(string name) => _oscillators.TryGetValue(name, out var osc) ? osc.Direction : Vector3.zero;

        public Vector3 GetOffset(string name) => _oscillators.TryGetValue(name, out var osc) ? osc.Offset : Vector3.zero;

        /// <summary>Steps every registered oscillator one fixed timestep.</summary>
        public void StepAll(float dt)
        {
            foreach (var osc in _oscillators.Values)
                osc.Step(dt);
        }

        /// <summary>
        /// Sums the linear offsets of the named oscillators (iterates by index — no per-frame alloc).
        /// Used to build the total root offset from the active climbing springs each frame.
        /// </summary>
        public Vector3 SumOffsets(List<string> names)
        {
            Vector3 sum = Vector3.zero;
            if (names == null) return sum;
            for (int i = 0; i < names.Count; i++)
            {
                if (_oscillators.TryGetValue(names[i], out var osc))
                    sum += osc.Offset;
            }
            return sum;
        }

        public void ResetAll()
        {
            foreach (var osc in _oscillators.Values)
                osc.Reset();
        }
    }
}
