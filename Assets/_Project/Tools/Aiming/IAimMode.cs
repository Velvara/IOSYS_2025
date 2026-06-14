// IAimMode.cs
public interface IAimMode
{
    /// Called when this aim mode becomes active (right mouse pressed and this mode selected)
    void EnterMode();

    /// Called every frame while this mode is active
    void UpdateMode();

    /// Called when leaving the mode (right mouse released or mode switched)
    void ExitMode();

    /// Optional: called when the held item changes while this mode is active
    void OnItemChanged(UnityEngine.GameObject newItem);
}
