using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// PROJECT-WIDE slip-risk tuning: what each painted risk class costs, what an unpainted (Black)
    /// hold resolves to, and the weather dry override. One asset for the whole game — the numbers are
    /// a difficulty knob, not a property of an individual rock, so tuning them per surface instance
    /// meant hunting down every placement to change a value.
    ///
    /// Also owns every knob of the in-game hold overlay (<see cref="ClimbRiskIndicators"/>, which is
    /// a pure per-frame driver with no config of its own) — the visual and the numbers it visualises
    /// are one authoring concern, and there is only ever one player wearing it.
    ///
    /// What stays per instance on <see cref="ClimbableSurface"/>: hold data, streaming radius, bake
    /// mesh — and the PAINT itself (<see cref="ClimbRiskPaint"/>), which is inherently per placement.
    /// Authoring: Tools/Climbing/Risk Settings (creates the asset on first use).
    ///
    /// Stored in a <c>Resources</c> folder so runtime code can reach it without an inspector link
    /// (climb surfaces are scattered/streamed; a serialized reference on each one is exactly the
    /// per-instance state this removes). It is one tiny asset, so the Resources cost is negligible.
    /// </summary>
    public class ClimbRiskSettings : ScriptableObject
    {
        /// <summary>Filename (no extension) inside any <c>Resources</c> folder.</summary>
        public const string ResourceName = "ClimbRiskSettings";

        [Header("Slip chance per painted class")]
        [Tooltip("Slip chance rolled when a hand LEAVES a green-painted hold.")]
        [Range(0f, 1f)][SerializeField] private float greenRisk = 0.05f;
        [Tooltip("Slip chance rolled when a hand LEAVES a blue-painted hold.")]
        [Range(0f, 1f)][SerializeField] private float blueRisk = 0.25f;
        [Tooltip("Slip chance rolled when a hand LEAVES a red-painted hold.")]
        [Range(0f, 1f)][SerializeField] private float redRisk = 0.5f;

        [Header("Unpainted holds")]
        [Tooltip("Class an unpainted (black) hold resolves to at runtime. Leaving this on Black counts as Green.")]
        [SerializeField] private ClimbRiskClass blackFallback = ClimbRiskClass.Green;

        [Header("Weather")]
        [Tooltip("Overrides the global wet state so rain never raises climb risk (weather, later).")]
        [SerializeField] private bool alwaysDry = false;

        // ---- Runtime hold overlay (see ClimbRiskIndicators — the component is a pure driver; ALL of
        // its tuning lives here so the visual and the numbers it visualises are authored together). ----

        [Header("Indicators — toggle")]
        [Tooltip("Master switch for the in-game hold overlay. Indicators only ever show while actively climbing.")]
        [SerializeField] private bool showIndicators = true;

        [Header("Indicators — field")]
        [Tooltip("Outer ring radius (m) around the player: holds within this show an indicator, holds " +
                 "past it show nothing. Also the OverlapSphere radius used to find nearby surfaces.")]
        [SerializeField] private float indicatorRange = 4f;
        [Tooltip("World size (m) of each billboard image.")]
        [SerializeField] private float indicatorSize = 0.12f;
        [Tooltip("Push each billboard OUT along the hold's outward normal by this many metres, so it sits " +
                 "off the surface instead of clipping into the mesh (raise until the images clear the rock).")]
        [SerializeField] private float surfaceOffset = 0.05f;
        [Tooltip("Height above the player root used as the ring CENTRE (≈ chest), so the field centres on " +
                 "the body rather than the feet.")]
        [SerializeField] private float centerHeightOffset = 1f;
        [Tooltip("Opacity vs normalized distance from the player (x: 0 = at the player → 1 = at the ring; " +
                 "y: image opacity). Default fades linearly 1 → 0; the per-class tint alpha scales the max.")]
        [SerializeField] private AnimationCurve opacityFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);

        [Header("Indicators — images + tint")]
        [Tooltip("Image for GREEN-risk holds. Leave null to draw nothing for this class.")]
        [SerializeField] private Texture2D greenImage;
        [SerializeField] private Color greenTint = Color.white;
        [Tooltip("Image for BLUE-risk holds.")]
        [SerializeField] private Texture2D blueImage;
        [SerializeField] private Color blueTint = Color.white;
        [Tooltip("Image for RED-risk holds.")]
        [SerializeField] private Texture2D redImage;
        [SerializeField] private Color redTint = Color.white;
        [Tooltip("Draw UNPAINTED (Black) holds as the Black Fallback class above (matches the slip roll). " +
                 "Off = only explicitly-painted holds show an image.")]
        [SerializeField] private bool drawUnpainted = true;

        [Header("Indicators — gather / perf")]
        [Tooltip("Layers searched for nearby climbable surfaces.")]
        [SerializeField] private LayerMask climbableMask = ~0;
        [Tooltip("Seconds between OverlapSphere refreshes of the nearby-surface set (surfaces don't move; " +
                 "the per-hold gather + billboard build still run every frame).")]
        [SerializeField] private float surfaceRefreshInterval = 0.25f;
        [Tooltip("Hard cap on indicators drawn per frame (dense vertex bakes hold ~21k). Past this the " +
                 "farthest-in-iteration holds are dropped — those near the ring are near-invisible anyway. " +
                 "Warns once if hit so you can shrink the range.")]
        [SerializeField] private int maxIndicators = 4000;
        [Tooltip("Optional template material for the billboards (a transparent, unlit, vertex-colour × " +
                 "texture shader). Leave null to build from the built-in \"Sprites/Default\".")]
        [SerializeField] private Material billboardMaterialTemplate;

        public float GreenRisk => greenRisk;
        public float BlueRisk => blueRisk;
        public float RedRisk => redRisk;
        public ClimbRiskClass BlackFallback => blackFallback;
        public bool AlwaysDry => alwaysDry;

        public bool ShowIndicators => showIndicators;
        public float IndicatorRange => indicatorRange;
        public float IndicatorSize => indicatorSize;
        public float SurfaceOffset => surfaceOffset;
        public float CenterHeightOffset => centerHeightOffset;
        public AnimationCurve OpacityFalloff => opacityFalloff;
        public Texture2D GreenImage => greenImage;
        public Texture2D BlueImage => blueImage;
        public Texture2D RedImage => redImage;
        public Color GreenTint => greenTint;
        public Color BlueTint => blueTint;
        public Color RedTint => redTint;
        public bool DrawUnpainted => drawUnpainted;
        public LayerMask ClimbableMask => climbableMask;
        public float SurfaceRefreshInterval => surfaceRefreshInterval;
        public int MaxIndicators => maxIndicators;
        public Material BillboardMaterialTemplate => billboardMaterialTemplate;

        /// <summary>Black resolves through <see cref="BlackFallback"/> (Green when the fallback is
        /// itself Black); every other class passes through.</summary>
        public ClimbRiskClass Resolve(ClimbRiskClass riskClass)
        {
            if (riskClass != ClimbRiskClass.Black) return riskClass;
            return blackFallback == ClimbRiskClass.Black ? ClimbRiskClass.Green : blackFallback;
        }

        /// <summary>Slip chance (0..1) for a class; Black resolves through the fallback.</summary>
        public float Risk01(ClimbRiskClass riskClass)
        {
            switch (Resolve(riskClass))
            {
                case ClimbRiskClass.Blue: return blueRisk;
                case ClimbRiskClass.Red: return redRisk;
                default: return greenRisk;
            }
        }

        // ------------------------------------------------------------------ single instance

        private static ClimbRiskSettings _instance;

        /// <summary>The one settings asset. Loaded from Resources; auto-created in the editor the
        /// first time anything asks for it, and falls back to an in-memory default (with a warning)
        /// if a build ever ships without it.</summary>
        public static ClimbRiskSettings Instance
        {
            get
            {
                if (_instance != null) return _instance;

                _instance = Resources.Load<ClimbRiskSettings>(ResourceName);
#if UNITY_EDITOR
                if (_instance == null) _instance = EditorCreateAsset();
#endif
                if (_instance == null)
                {
                    _instance = CreateInstance<ClimbRiskSettings>();   // defaults, not persisted
                    Debug.LogWarning($"[ClimbRiskSettings] No '{ResourceName}' asset found in a Resources " +
                                     "folder — using built-in defaults. Create it via Tools/Climbing/Risk Settings.");
                }
                return _instance;
            }
        }

#if UNITY_EDITOR
        private const string AssetFolder = "Assets/_Project/Climbing/Resources";

        [UnityEditor.MenuItem("Tools/Climbing/Risk Settings")]
        private static void OpenSettings()
        {
            ClimbRiskSettings s = Instance;
            if (s != null) UnityEditor.Selection.activeObject = s;
        }

        /// <summary>EDITOR — creates the asset (and its Resources folder) on first access.</summary>
        private static ClimbRiskSettings EditorCreateAsset()
        {
            if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating)
                return null;   // asset ops during import/compile are unsafe; caller falls back to defaults

            if (!UnityEditor.AssetDatabase.IsValidFolder(AssetFolder))
                UnityEditor.AssetDatabase.CreateFolder("Assets/_Project/Climbing", "Resources");

            var created = CreateInstance<ClimbRiskSettings>();
            UnityEditor.AssetDatabase.CreateAsset(created, $"{AssetFolder}/{ResourceName}.asset");
            UnityEditor.AssetDatabase.SaveAssets();
            Debug.Log($"[ClimbRiskSettings] Created {AssetFolder}/{ResourceName}.asset (global climb risk tuning).", created);
            return created;
        }
#endif
    }
}
