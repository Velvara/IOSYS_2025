namespace StarterAssets
{
    /// <summary>
    /// Exposes normalized survival bar values for any character that has a stamina system.
    /// Consumed by SurvivalBarsController. Characters implement this on their stamina component.
    /// All normalized values are 0-1, ready for radial bar rendering.
    /// </summary>
    public interface IStaminaData
    {
        // -- Current bar values --
        float NormalizedStamina { get; }
        float NormalizedThirst  { get; }
        float NormalizedHunger  { get; }

        // -- Layered max stamina --
        float NormalizedEffectiveMax     { get; }
        float NormalizedRestPenaltyTop   { get; }
        float NormalizedHungerPenaltyTop { get; }

        // -- Rest penalty accumulation --
        bool  IsAccumulatingRestPenalty       { get; }
        float NormalizedCurrentSessionPenalty { get; }

        // -- Hunger max penalty progress --
        /// <summary>
        /// How much of the hunger max penalty cap has been consumed, 0-1.
        /// 1 = hunger penalty has reached HungerMaxPenaltyCap.
        /// </summary>
        float NormalizedHungerMaxPenalty { get; }

        // -- Fatigue floor --
        /// <summary>
        /// The normalized stamina value at which fatigue ends (HungerMaxStaHardFloor / 100).
        /// Used to position the marker line on the stamina bar so the player can see
        /// exactly where recovery will exit the fatigued state.
        /// </summary>
        float NormalizedFatigueFloor { get; }
    }
}
