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
    }
}
