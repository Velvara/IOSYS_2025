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

        /// <summary>Fired just BEFORE the ragdoll takes the body — systems holding the character (climbing)
        /// tear down their holds/camera here; calling ReleaseExternalControl in the handler is safe.</summary>
        event Action RagdollStarting;

        /// <summary>Fired when the recovery blend completes and control returns to the player.</summary>
        event Action RagdollRecovered;
    }
}
