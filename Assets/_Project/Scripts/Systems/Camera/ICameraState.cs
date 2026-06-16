namespace Game.PlayerV2
{
    /// <summary>
    /// Read-only camera state other systems query. Maps the old controller's
    /// <c>cameraFrozen</c> flag: aim modes use it to skip aim exaggeration / rotation
    /// while camera look is frozen (e.g. during hookshot/external control).
    /// </summary>
    public interface ICameraState
    {
        bool IsCameraFrozen { get; }
    }
}
