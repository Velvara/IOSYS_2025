using UnityEngine;

namespace Game.PlayerV2
{
    /// <summary>
    /// Minimal movement surface other systems need from the player controller:
    /// the transform/CharacterController to read or drive directly (e.g. hookshot drag),
    /// and RotateOnMove so aim modes can suppress face-movement rotation while aiming.
    /// </summary>
    public interface IPlayerMotor
    {
        Transform Transform { get; }
        CharacterController Controller { get; }
        bool RotateOnMove { get; set; }

        /// <summary>
        /// The motor's intended vertical velocity (gravity/jump), not the CharacterController's
        /// measured velocity. Read by external systems (e.g. climbing) for slide/exit handoff.
        /// </summary>
        float VerticalVelocity { get; }

        /// <summary>
        /// Sets the motor's vertical velocity directly — e.g. a climb jump-off impulse, or a
        /// zeroed mantle/exit. Takes effect when locomotion resumes after external control.
        /// </summary>
        void SetVerticalVelocity(float v);

        /// <summary>
        /// Adds a decaying horizontal launch velocity, applied additively on top of input-driven
        /// movement (e.g. a climb jump-off that arcs away from the wall before blending back to
        /// normal air control). Zero unless set, so ordinary locomotion is unaffected.
        /// </summary>
        void AddLaunchVelocity(Vector3 horizontalWorld, float decayRate);

        /// <summary>
        /// Ignore air-control input for the next <paramref name="seconds"/> so a launch arc (climb jump-off)
        /// flies clean — only the launch velocity + gravity, no input steering. Grounded movement unaffected.
        /// </summary>
        void SuppressAirControl(float seconds);
    }
}
