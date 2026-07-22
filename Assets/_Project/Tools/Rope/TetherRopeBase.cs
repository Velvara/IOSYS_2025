using UnityEngine;

/// <summary>
/// Common face of the two carbine-tether simulations (verlet+collision / PhysX joint chain), so
/// CarbineController can create either behind its toggle and the later fall/yank increments read
/// one API. A tether runs carbine → hip slot with both ends following their transforms; detaching
/// releases the hip end (the rope then dangles from the carbine for the dissolve).
/// </summary>
public abstract class TetherRopeBase : MonoBehaviour
{
    /// <summary>MAX rope length (m), set at Init — the hard range cap.</summary>
    public float RopeLength { get; protected set; }

    /// <summary>Rope currently PAID OUT (m) — the length actually simulated/rendered. The controller
    /// grows it as the player moves away from the carbine (distance + slack) and reels it back in
    /// approaching, never past <see cref="RopeLength"/>. Remaining spare = RopeLength − ActiveLength.</summary>
    public float ActiveLength { get; protected set; }

    /// <summary>Sets the paid-out length (clamped to [0.5, RopeLength]). Called every frame while
    /// tethered — implementations must take a live-changing rest length cheaply.</summary>
    public abstract void SetActiveLength(float length);

    /// <summary>False after <see cref="ReleaseHipEnd"/> — the rope dangles from the carbine only.</summary>
    public bool HipAttached { get; protected set; } = true;

    /// <summary>Straight-line distance between the two ends divided by the rope length —
    /// ~0 slack, ≥1 taut/stretched. The yank/fall increment reads this.</summary>
    public abstract float TautAmount { get; }

    /// <summary>Measured length of the simulated rope path (sum of the segment distances). While the
    /// rope wraps a corner this is the TRUE rope requirement — straight anchor→hip distance
    /// under-measures there. Exceeding ActiveLength means the chain is stretched (taut past its
    /// paid length): the pay-out and the range checks key off that.</summary>
    public abstract float CurrentArcLength { get; }

    /// <summary>The rope's last geometry contact nearest the HIP end (a wrap pivot — e.g. the corner
    /// edge) and the rope arc from the anchor to it. False when the sim doesn't track contacts (the
    /// PhysX variant) or nothing is touched — callers fall back to anchor-straight measurements.</summary>
    public virtual bool GetLastContact(out Vector3 point, out float arcFromAnchor)
    {
        point = default;
        arcFromAnchor = 0f;
        return false;
    }

    /// <summary>Current world position of the rope's hip-side end.</summary>
    public abstract Vector3 HipEndPoint { get; }

    /// <param name="anchorEnd">Rope start — the carbine's rope point (static once placed).</param>
    /// <param name="hipEnd">Rope end — the player's hip slot (followed every frame).</param>
    /// <param name="ropeLength">Total rope length (m).</param>
    /// <param name="material">Tube material.</param>
    /// <param name="collisionMask">What the rope collides with / drapes over.</param>
    /// <param name="ignoreRootA">Hierarchy ignored by rope collision (the carbine).</param>
    /// <param name="ignoreRootB">Hierarchy ignored by rope collision (the player).</param>
    public abstract void Init(Transform anchorEnd, Transform hipEnd, float ropeLength, Material material,
                              LayerMask collisionMask, Transform ignoreRootA, Transform ignoreRootB);

    /// <summary>Detach: the hip end lets go and the rope dangles from the carbine.</summary>
    public abstract void ReleaseHipEnd();
}
