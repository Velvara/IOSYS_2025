namespace Game.Climbing
{
    /// <summary>
    /// Classifies how a climbable surface is climbed. Per-type rules (traversal, slide, ledge shimmy)
    /// hang off this identity:
    ///   Free  — vertex-per-hold bake (Every Vertex mode / purpose-built climb proxies); the whole
    ///           face is climbable. The ENTRY SLIDE only happens here.
    ///   Ledge — edge/protrusion bake (Ledge Edges mode): discrete edges, ledges and indented holds.
    ///   Trunk — procedural Flora trunk (holds pushed at generation time, trunk-axis "up").
    ///
    /// Trunks classify automatically by hold source; authored surfaces carry the type stamped into
    /// their baked <see cref="HoldDataSO"/> by the bake window.
    /// </summary>
    public enum ClimbType
    {
        Free = 0,
        Ledge = 1,
        Trunk = 2
    }
}
