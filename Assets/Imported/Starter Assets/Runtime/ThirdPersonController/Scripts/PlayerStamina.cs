using UnityEngine;

namespace StarterAssets
{
    /// <summary>
    /// Self-contained stamina, thirst, and hunger system.
    /// Lives on the same GameObject as ThirdPersonController.
    /// The controller calls Tick(isSprinting) once per Update and reads back
    /// IsFatigued / CurrentStamina. Nothing else couples the two scripts,
    /// so this component can be swapped or disabled without touching the controller.
    ///
    /// LAYERED EFFECTIVE MAX STAMINA
    /// ----------------------------------
    ///   Base max:              always 100
    ///   _restMaxPenalty:       chips away first  (lack of rest)
    ///   _hungerMaxPenalty:     chips away second (starvation), capped at HungerMaxPenaltyCap
    ///   RestMaxStaHardFloor:   rest penalty cannot reduce max below this  (default 50)
    ///   HungerMaxStaHardFloor: hunger penalty cannot reduce max below this (default 25)
    ///   HungerMaxPenaltyCap:   maximum total hunger penalty (default 25)
    ///   EffectiveMax = Max(HungerMaxStaHardFloor, 100 - restPenalty - hungerPenalty)
    ///
    /// FATIGUE
    /// -------
    ///   Enter: CurrentStamina reaches 0.
    ///   Exit:  CurrentStamina recovers to HungerMaxStaHardFloor.
    ///          NormalizedFatigueFloor exposes this threshold for the HUD marker line.
    ///
    /// REST MAX PENALTY
    /// ----------------
    ///   Accumulates at RestMaxPenaltyRate per second once MaxStaDrainThreshold
    ///   is exhausted. Independent of StaminaDrainRate.
    ///
    /// THIRST-SCALED RECOVERY
    /// ----------------------
    ///   Stamina always regenerates when not sprinting.
    ///   Rate scales between StaminaRecoveryRate (full thirst) and
    ///   StaminaRecoveryRate * (FloorPct/100) (zero thirst).
    ///
    /// DEBUG
    /// -----
    ///   Public methods called by DebugSurvivalInputs.
    ///   Remove or disable that component to deactivate all debug behaviour.
    /// </summary>
    public class PlayerStamina : MonoBehaviour, IStaminaData
    {
        // -- Constant --
        private const float BaseMaxStamina = 100f;

        // -- Stamina settings --
        [Header("Stamina")]
        [Tooltip("Stamina drained per second while sprinting. ~20 /s")]
        public float StaminaDrainRate = 20f;

        [Tooltip("Base stamina recovered per second when not sprinting. " +
                 "Actual rate is scaled by thirst level and StaminaRecoveryRateFloorPct. ~10 /s")]
        public float StaminaRecoveryRate = 10f;

        [Tooltip("Minimum recovery rate as a percentage of StaminaRecoveryRate, reached at 0 thirst. " +
                 "Range 1-100. At 100, thirst has no effect on recovery rate.")]
        [Range(1, 100)]
        public float StaminaRecoveryRateFloorPct = 25f;

        [Tooltip("Consecutive stamina points drained before rest-max penalty starts. " +
                 "Grace period. Resets every time sprinting stops.")]
        public float MaxStaDrainThreshold = 10f;

        [Tooltip("Rate at which rest-max penalty accumulates per second once the grace " +
                 "period is exhausted. Independent of StaminaDrainRate. ~5 /s")]
        public float RestMaxPenaltyRate = 5f;

        [Tooltip("Rest penalty cannot reduce effective max below this value. Default: 50.")]
        public float RestMaxStaHardFloor = 50f;

        [Tooltip("Hunger penalty cannot reduce effective max below this value. " +
                 "Also the stamina threshold at which fatigue ends. Default: 25.")]
        public float HungerMaxStaHardFloor = 25f;

        // -- Thirst settings --
        [Header("Thirst")]
        [Tooltip("Thirst lost per second passively. ~1 /s")]
        public float ThirstDrainRate = 1f;

        [Tooltip("Thirst restored per second when debug drink is held. ~25 /s")]
        public float ThirstRestoreRate = 25f;

        // -- Hunger settings --
        [Header("Hunger")]
        [Tooltip("Hunger lost per second passively. ~0.5 /s")]
        public float HungerDrainRate = 0.5f;

        [Tooltip("Maximum total hunger-based max-stamina penalty. " +
                 "Hunger penalty stops growing once this is reached. Default: 25.")]
        public float HungerMaxPenaltyCap = 25f;

        [Tooltip("Rate at which the hunger-based max-stamina penalty grows per second " +
                 "once the hunger bar is empty. ~2 /s")]
        public float HungerMaxDrainRate = 2f;

        [Tooltip("Hunger restored per second when debug eat is held. ~25 /s")]
        public float HungerRestoreRate = 25f;

        [Tooltip("Hunger-based max-stamina penalty removed per second when debug eat is held. ~10 /s")]
        public float HungerMaxRestoreRate = 10f;

        // -- Debug drain rates --
        [Header("Debug Drain Rates")]
        [Tooltip("Stamina drained per second by DEBUG_J. ~50 /s")]
        public float DebugStaminaDrainRate = 50f;

        [Tooltip("Hunger drained per second by DEBUG_K. ~50 /s")]
        public float DebugHungerDrainRate = 50f;

        [Tooltip("Thirst drained per second by DEBUG_L. ~50 /s")]
        public float DebugThirstDrainRate = 50f;

        // -- IStaminaData --
        public float CurrentStamina      => _currentStamina;
        public float CurrentThirst       => _currentThirst;
        public float CurrentHunger       => _currentHunger;
        public float RestMaxPenalty      => _restMaxPenalty;
        public float HungerMaxPenalty    => _hungerMaxPenalty;
        public float EffectiveMaxStamina => ComputeEffectiveMax();
        public bool  IsFatigued          => _isFatigued;

        public float NormalizedEffectiveMax     => ComputeEffectiveMax() / BaseMaxStamina;
        public float NormalizedRestPenaltyTop   => (BaseMaxStamina - _restMaxPenalty) / BaseMaxStamina;
        public float NormalizedHungerPenaltyTop => (BaseMaxStamina - _restMaxPenalty - _hungerMaxPenalty) / BaseMaxStamina;
        public float NormalizedStamina          => _currentStamina / BaseMaxStamina;
        public float NormalizedThirst           => _currentThirst  / BaseMaxStamina;
        public float NormalizedHunger           => _currentHunger  / BaseMaxStamina;

        public bool  IsAccumulatingRestPenalty       => _isAccumulatingRestPenalty;
        public float NormalizedCurrentSessionPenalty => _currentSessionPenalty / BaseMaxStamina;

        public float NormalizedHungerMaxPenalty =>
            HungerMaxPenaltyCap > 0f ? _hungerMaxPenalty / HungerMaxPenaltyCap : 0f;

        /// <summary>
        /// Normalized stamina threshold at which fatigue ends (HungerMaxStaHardFloor / 100).
        /// Drives the position of the marker line on the stamina HUD bar.
        /// </summary>
        public float NormalizedFatigueFloor => HungerMaxStaHardFloor / BaseMaxStamina;

        // -- Private state --
        private float _currentStamina;
        private float _currentThirst;
        private float _currentHunger;

        private float _restMaxPenalty;
        private float _hungerMaxPenalty;
        private float _staminaSpentAccumulator;
        private float _currentSessionPenalty;
        private float _currentStaminaRecoveryRate;

        private bool _isFatigued;
        private bool _isAccumulatingRestPenalty;

        // -- Lifecycle --

        private void Start()
        {
            _currentStamina             = BaseMaxStamina;
            _currentThirst              = BaseMaxStamina;
            _currentHunger              = BaseMaxStamina;
            _restMaxPenalty             = 0f;
            _hungerMaxPenalty           = 0f;
            _staminaSpentAccumulator    = 0f;
            _currentSessionPenalty      = 0f;
            _isFatigued                 = false;
            _isAccumulatingRestPenalty  = false;
            _currentStaminaRecoveryRate = StaminaRecoveryRate;
        }

        public void Tick(bool isSprinting)
        {
            float dt = Time.deltaTime;
            UpdateCurrentRecoveryRate();
            TickPassiveDrain(dt);
            TickStamina(isSprinting, dt);
            ClampAll();
        }

        private void UpdateCurrentRecoveryRate()
        {
            float floorFraction  = StaminaRecoveryRateFloorPct / 100f;
            float thirstFraction = _currentThirst / BaseMaxStamina;
            _currentStaminaRecoveryRate = StaminaRecoveryRate
                                         * Mathf.Lerp(floorFraction, 1f, thirstFraction);
        }

        private void TickPassiveDrain(float dt)
        {
            _currentThirst -= ThirstDrainRate * dt;
            _currentHunger -= HungerDrainRate * dt;

            if (_currentHunger <= 0f)
                _hungerMaxPenalty += HungerMaxDrainRate * dt;

            // -- Future stamina-draining states go here --
            // Add a public float drain-rate field in the [Header("Stamina")] block
            // for each state, then add its drain below. Examples:
            //
            //   Swimming  (~15 /s):  _currentStamina -= SwimDrainRate  * dt;
            //   Climbing  (~25 /s):  _currentStamina -= ClimbDrainRate * dt;
            //   Holding breath (~30 /s): _currentStamina -= HoldBreathDrainRate * dt;
        }

        private void TickStamina(bool isSprinting, float dt)
        {
            if (isSprinting && !_isFatigued && _currentStamina > 0f)
            {
                float drain = StaminaDrainRate * dt;
                _currentStamina -= drain;
                _staminaSpentAccumulator += drain;

                if (_staminaSpentAccumulator >= MaxStaDrainThreshold)
                {
                    float penaltyThisFrame  = RestMaxPenaltyRate * dt;
                    _restMaxPenalty        += penaltyThisFrame;
                    _currentSessionPenalty += penaltyThisFrame;
                    _isAccumulatingRestPenalty = true;
                }
                else
                {
                    _isAccumulatingRestPenalty = false;
                }

                if (_currentStamina <= 0f)
                    _isFatigued = true;
            }
            else
            {
                _staminaSpentAccumulator   = 0f;
                _currentSessionPenalty     = 0f;
                _isAccumulatingRestPenalty = false;

                _currentStamina += _currentStaminaRecoveryRate * dt;

                if (_isFatigued && _currentStamina >= HungerMaxStaHardFloor)
                    _isFatigued = false;
            }
        }

        private void ClampAll()
        {
            _currentThirst = Mathf.Clamp(_currentThirst, 0f, BaseMaxStamina);
            _currentHunger = Mathf.Clamp(_currentHunger, 0f, BaseMaxStamina);

            float restPenaltyCap = BaseMaxStamina - RestMaxStaHardFloor;
            _restMaxPenalty = Mathf.Clamp(_restMaxPenalty, 0f, restPenaltyCap);

            float hungerFloorHeadroom = (BaseMaxStamina - _restMaxPenalty) - HungerMaxStaHardFloor;
            float hungerPenaltyCap    = Mathf.Min(HungerMaxPenaltyCap,
                                                   Mathf.Max(0f, hungerFloorHeadroom));
            _hungerMaxPenalty = Mathf.Clamp(_hungerMaxPenalty, 0f, hungerPenaltyCap);

            _currentStamina        = Mathf.Clamp(_currentStamina, 0f, ComputeEffectiveMax());
            _currentSessionPenalty = Mathf.Clamp(_currentSessionPenalty, 0f, BaseMaxStamina);
        }

        private float ComputeEffectiveMax()
        {
            return Mathf.Max(HungerMaxStaHardFloor,
                             BaseMaxStamina - _restMaxPenalty - _hungerMaxPenalty);
        }

        // -- Public debug API --

        public void Debug_RestoreRest()
        {
            _restMaxPenalty        = 0f;
            _currentSessionPenalty = 0f;
        }

        public void Debug_RestoreHunger(float dt)
        {
            _currentHunger    += HungerRestoreRate    * dt;
            _hungerMaxPenalty -= HungerMaxRestoreRate * dt;
        }

        public void Debug_RestoreThirst(float dt)
        {
            _currentThirst += ThirstRestoreRate * dt;
        }

        public void Debug_DrainStamina(float dt)
        {
            _currentStamina -= DebugStaminaDrainRate * dt;
            if (_currentStamina <= 0f)
                _isFatigued = true;
        }

        public void Debug_DrainHunger(float dt)
        {
            _currentHunger -= DebugHungerDrainRate * dt;
        }

        public void Debug_DrainThirst(float dt)
        {
            _currentThirst -= DebugThirstDrainRate * dt;
        }
    }
}
