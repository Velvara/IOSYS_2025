namespace Game.PlayerV2
{
    /// <summary>
    /// Camera state/coordination seam other systems query. <see cref="IsCameraFrozen"/> maps the old
    /// controller's <c>cameraFrozen</c> flag (aim modes skip exaggeration/rotation while look is frozen,
    /// e.g. hookshot/external control). <see cref="SetPitchLock"/> lets an aim mode force a look angle
    /// (e.g. rope aim tilting the camera down at the ground) without owning the camera rig.
    /// </summary>
    public interface ICameraState
    {
        bool IsCameraFrozen { get; }

        /// <summary>Force the camera pitch to a fixed angle (eased), freezing vertical look while yaw
        /// stays live. Pass locked=false to release. See <c>PlayerCameraRig.SetPitchLock</c>.</summary>
        void SetPitchLock(bool locked, float pitchDegrees);
    }
}
