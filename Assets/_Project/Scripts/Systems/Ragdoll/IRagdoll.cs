using System;
using UnityEngine;

namespace Game.PlayerV2.Systems
{
    /// <summary>
    /// Body-level ragdoll surface on the player. External systems (climbing, powers, cutscenes)
    /// resolve it via GetComponentInParent&lt;IRagdoll&gt;() — never a serialized link — to force a
    /// ragdoll or react to one. Implemented by PlayerRagdoll.
    /// </summary>
    public interface IRagdoll
    {
        /// <summary>True from ragdoll start until control has returned (includes the recovery blend).</summary>
        bool IsRagdolled { get; }

        /// <summary>True once the downed body has stopped moving (a recovery input is meaningful).</summary>
        bool IsSettled { get; }

        /// <summary>True while a Use press would start the recovery.</summary>
        bool CanRecover { get; }

        /// <summary>Knock the character into ragdoll now (the current motor velocity carries into the bones).</summary>
        void TriggerRagdoll();

        /// <summary>Ragdoll with extra velocity on top of the carried momentum (explosion shove etc.).</summary>
        void TriggerRagdoll(Vector3 extraVelocity);

        /// <summary>
        /// Full form, for a ragdoll whose PELVIS is HELD by an external constraint — the rope tether's
        /// yank. Every bone gets the carried momentum plus <paramref name="extraVelocity"/>, but the
        /// pelvis keeps only <paramref name="pelvisVelocityScale"/> of it: that velocity differential
        /// across the joints is what throws the limbs, spine and head past the arrested hips.
        /// The caller owns the constraint itself (a joint to <see cref="PelvisBody"/>) and, through
        /// <see cref="SetRecoverySuppressed"/>, the recovery input.
        /// </summary>
        /// <param name="allowAirSteer">False disables the airborne move-input steering for this ragdoll
        /// (a body hanging on a rope never touches ground, so the usual latch-off would never fire).</param>
        void TriggerRagdoll(Vector3 extraVelocity, float pelvisVelocityScale, bool allowAirSteer);

        /// <summary>The pelvis rigidbody, so an external system can constrain the downed body (a rope
        /// joint). Null until the ragdoll rig has been discovered. Only live while ragdolled — the rig
        /// is kinematic with its colliders off the rest of the time.</summary>
        Rigidbody PelvisBody { get; }

        /// <summary>While suppressed, the built-in "Use to get up" recovery is disabled — an external
        /// system owns that press (the tether's Use-to-reattach). The suppressor must clear it.</summary>
        void SetRecoverySuppressed(bool suppressed);

        /// <summary>True while the ground probe under the pelvis is hitting something — a LIVE reading,
        /// not a latch, so a rope-held body can tell "come to rest on the ground" from "dangling in
        /// the air". Only meaningful while ragdolled.</summary>
        bool IsPelvisGrounded { get; }

        /// <summary>
        /// Ends the ragdoll INTO another system's hands: the body is placed where the caller says (no
        /// ground snap), the animator is left for the new owner to drive, and external control is NOT
        /// released. The pose still blends out of the ragdoll over <paramref name="blendTime"/> (≤ 0 =
        /// the component's default), so the crumpled shape morphs into the new pose instead of popping.
        /// The caller MUST take the body over in the same frame, immediately after this returns.
        /// </summary>
        void RecoverInto(Vector3 rootPosition, Quaternion rootRotation, float blendTime);

        /// <summary>Starts the recovery now, without waiting for the Use press — for systems that decide
        /// on the player's behalf that the fall is over (a tethered yank that ended on the ground).
        /// No-op unless the body is down and not already recovering.</summary>
        void ForceRecover();

        /// <summary>
        /// Sets linear + angular damping on EVERY ragdoll bone — air resistance for a body that has
        /// nothing else to lose energy to (hanging on a rope). It has to be the whole rig: damping only
        /// the pelvis leaves fourteen limbs feeding their momentum straight back into it through the
        /// joints. Automatically restored to the rig's authored values by the next ragdoll trigger.
        /// </summary>
        void SetBoneDamping(float linearDamping, float angularDamping);

        /// <summary>Restores the damping the ragdoll rig was authored with.</summary>
        void ResetBoneDamping();

        /// <summary>Turns the airborne move-input steering on/off mid-ragdoll. A rope catching a body
        /// that was already falling switches it off: a hanging body swings passively, and it never
        /// touches ground to latch the steering off by itself. Reset on the next ragdoll.</summary>
        void SetAirSteerEnabled(bool enabled);

        /// <summary>Fired just BEFORE the ragdoll takes the body — systems holding the character (climbing)
        /// tear down their holds/camera here; calling ReleaseExternalControl in the handler is safe.</summary>
        event Action RagdollStarting;

        /// <summary>Fired when the recovery blend completes and control returns to the player.</summary>
        event Action RagdollRecovered;
    }
}
