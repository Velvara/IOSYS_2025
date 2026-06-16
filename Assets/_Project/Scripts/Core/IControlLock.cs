namespace Game.PlayerV2
{
    /// <summary>
    /// Lets external systems (hookshot drag, cutscenes, scripted moves) take movement
    /// control away from the player controller. While active, the controller relinquishes
    /// locomotion and camera look (it enters the ExternalControl state); the external system
    /// is free to drive the CharacterController directly. Replaces the old
    /// FreezeCharacter / IsExternalControlActive / component-disable patches.
    /// </summary>
    public interface IControlLock
    {
        /// <summary>True while an external system holds control.</summary>
        bool IsExternalControlActive { get; }

        /// <summary>Take control away from the player controller.</summary>
        void RequestExternalControl();

        /// <summary>Return control to the player controller.</summary>
        void ReleaseExternalControl();
    }
}
