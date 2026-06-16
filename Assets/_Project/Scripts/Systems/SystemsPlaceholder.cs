using UnityEngine;

namespace Game.PlayerV2.Systems
{
    /// <summary>
    /// Adapter over the real <see cref="PlayerStamina"/> survival component. The controller
    /// ticks it once per frame with whether the player is sprinting; PlayerStamina owns all
    /// drain/recovery/thirst/hunger logic and the fatigue state. States read IsFatigued /
    /// IsDepleted through this adapter.
    ///
    /// NOTE: PlayerStamina currently lives in the Game.Player assembly; it moves into
    /// Game.PlayerV2 at the final prefab swap (see phase2-decisions memory).
    /// </summary>
    public class StaminaSystem
    {
        private readonly PlayerStamina _stamina;

        public StaminaSystem(PlayerStamina stamina)
        {
            _stamina = stamina;
        }

        /// <summary>
        /// Advances the survival system one frame. Pass true while the player is sprinting
        /// (drains stamina); otherwise it recovers. Mirrors ThirdPersonController's per-frame
        /// PlayerStamina.Tick call.
        /// </summary>
        public void Tick(bool isSprinting)
        {
            if (_stamina != null) _stamina.Tick(isSprinting);
        }

        /// <summary>True while fatigued (stamina hit 0, recovering to the fatigue floor).</summary>
        public bool IsFatigued => _stamina != null && _stamina.IsFatigued;

        public float CurrentStamina => _stamina != null ? _stamina.CurrentStamina : 0f;

        /// <summary>No stamina left to start or continue sprinting.</summary>
        public bool IsDepleted => _stamina == null || _stamina.CurrentStamina <= 0f;

        /// <summary>Reserved for future drained/drowned states (not used yet).</summary>
        public bool IsFullyDepleted => false;
    }

    /// <summary>
    /// Placeholder for HealthSystem - will be fully implemented in Phase 3
    /// </summary>
    public class HealthSystem
    {
        public float CurrentHealth => 100f;
        public float MaxHealth => 100f;
        public bool IsDead => false;

        public void TakeDamage(float amount)
        {
            // TODO: Implement in Phase 3
        }

        public void Heal(float amount)
        {
            // TODO: Implement in Phase 3
        }
    }

    /// <summary>
    /// Placeholder for InventorySystem - will be fully implemented later
    /// </summary>
    public class InventorySystem
    {
        public int CurrentSlot => 0;
        public int ItemCount => 0;

        public void CycleForward()
        {
            // TODO: Implement later
        }

        public void CycleBackward()
        {
            // TODO: Implement later
        }
    }
}
