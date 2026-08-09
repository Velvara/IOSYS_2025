namespace Game.Climbing
{
    /// <summary>
    /// How a climb ended. Systems attached to the climber react differently to each — a carbine tether
    /// must survive a FALL (catching it is the entire point of the rope) but let go when the climber has
    /// simply arrived somewhere.
    /// </summary>
    public enum ClimbEndKind
    {
        /// <summary>Let go under the player's own control, or stepped off near the bottom — the climb
        /// faded out deliberately.</summary>
        Released,

        /// <summary>Came off the wall involuntarily: a failed slip QTE, a slide that ran out of wall,
        /// grip lost to empty stamina, or a hard fall taking the body into ragdoll.</summary>
        Fell,

        /// <summary>Ended standing on solid ground — mantled over the top, or slid down onto the floor.</summary>
        Landed,

        /// <summary>Another system took the body over (the climb → rope rappel handoff).</summary>
        HandedOff
    }
}
