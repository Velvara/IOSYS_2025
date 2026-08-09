using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// Slip-risk class of a single hold, painted in the editor (Tools/Climbing/Risk Painter) and
    /// stored OUTSIDE the mesh, per SCENE INSTANCE, on <see cref="ClimbableSurface"/> — the shared
    /// baked <see cref="HoldDataSO"/> never carries paint, so the same prefab scattered around a
    /// level is painted per placement. Black = unpainted; at runtime it resolves through the GLOBAL
    /// Black Fallback class (default Green). The chance each class rolls is global too — both live
    /// on <see cref="ClimbRiskSettings"/>, not per surface.
    /// </summary>
    public enum ClimbRiskClass : byte
    {
        Black = 0,
        Green = 1,
        Blue = 2,
        Red = 3
    }

    public static class ClimbRiskClassUtil
    {
        /// <summary>Editor/gizmo display colour for a class (Black shows as dark grey).</summary>
        public static Color DisplayColor(ClimbRiskClass riskClass)
        {
            switch (riskClass)
            {
                case ClimbRiskClass.Green: return new Color(0.62f, 0.95f, 0.12f);   // lime — display only, the stored class is unchanged
                case ClimbRiskClass.Blue: return new Color(0.25f, 0.55f, 1f);
                case ClimbRiskClass.Red: return new Color(1f, 0.25f, 0.2f);
                default: return new Color(0.25f, 0.25f, 0.25f);
            }
        }
    }
}
