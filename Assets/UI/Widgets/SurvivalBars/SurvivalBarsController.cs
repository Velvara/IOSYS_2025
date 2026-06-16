using UnityEngine;
using UnityEngine.UIElements;
using Game.PlayerV2.Systems;

namespace StarterAssets
{
    /// <summary>
    /// Controls the SurvivalBars widget and acts as the SINGLE SOURCE OF TRUTH
    /// for all bar and icon colors. No colors should be set in UI Builder or USS
    /// for these elements -- all color configuration lives here.
    ///
    /// HUNGER BAR PENALTY PROGRESS DIRECTION
    /// ---------------------------------------
    /// When hunger is empty, the EffectiveMaxValue arc is repurposed as a progress
    /// indicator showing how much of the hunger penalty cap has been consumed.
    /// EffectiveMaxCounterClockwise is set to true so it fills counterclockwise,
    /// matching the visual language of the red and orange penalty arcs on the
    /// stamina bar (both of which reduce from the outer edge inward).
    /// When hunger is not empty, EffectiveMaxCounterClockwise is reset to false.
    ///
    /// STAMINA BAR MARKER LINE
    /// -----------------------
    /// White arc at NormalizedFatigueFloor showing where stamina must recover
    /// to before fatigue ends. Always visible on the stamina bar.
    ///
    /// BLINK TYPES
    /// -----------
    ///   Hunger warning: smooth pulse via PingPong + Color.Lerp
    ///   Red arc:        binary flash via IsBlinkOn (sharp, urgent)
    /// </summary>
    public class SurvivalBarsController : MonoBehaviour
    {
        [Tooltip("The UIDocument that owns the SurvivalBars widget.")]
        [SerializeField] private UIDocument _document;

        // -- Stamina bar --
        [Header("Stamina Bar")]
        [Tooltip("Current stamina fill color. Also used as the stamina icon's normal tint.")]
        public Color StaminaFillColor = new Color(0.2f, 0.8f, 0.2f, 1f);

        [Tooltip("Dark green arc showing the effective max (recovery ceiling).")]
        public Color StaminaEffectiveMaxColor = new Color(0.1f, 0.4f, 0.1f, 1f);

        [Tooltip("Background track color for the stamina bar.")]
        public Color StaminaTrackColor = new Color(0.15f, 0.15f, 0.15f, 0.8f);

        [Tooltip("Stamina icon tint when stamina reaches 0. Static, no blink.")]
        public Color StaminaIconEmptyTint = new Color(0.5f, 0.1f, 0.1f, 1f);

        [Tooltip("Normal color of the orange hunger penalty arc.")]
        public Color StaminaOrangeNormalColor = new Color(0.9f, 0.5f, 0.1f, 1f);

        [Tooltip("Normal color of the red session penalty arc.")]
        public Color StaminaRedNormalColor = new Color(0.9f, 0.1f, 0.1f, 1f);

        [Tooltip("Color the red arc blinks to while active.")]
        public Color StaminaRedBlinkColor = new Color(1f, 0.65f, 0f, 1f);

        [Tooltip("Seconds per full blink cycle for the red arc. Default 0.4.")]
        public float StaminaRedBlinkRate = 0.4f;

        [Header("Stamina Bar Marker")]
        [Tooltip("Color of the fatigue-floor marker line. Default white.")]
        public Color StaminaMarkerColor = Color.white;

        [Tooltip("Width of the fatigue-floor marker in stamina points (1 point = 1% of ring). " +
                 "Default 2.")]
        public float StaminaMarkerThicknessPoints = 2f;

        // -- Thirst bar --
        [Header("Thirst Bar")]
        [Tooltip("Current thirst fill color. Also used as the thirst icon's normal tint.")]
        public Color ThirstFillColor = new Color(0.2f, 0.7f, 0.9f, 1f);

        [Tooltip("Background track color for the thirst bar.")]
        public Color ThirstTrackNormalColor = new Color(0.15f, 0.15f, 0.15f, 0.8f);

        [Tooltip("Color applied to both the thirst track and icon when thirst reaches 0. Static.")]
        public Color ThirstEmptyColor = new Color(0.1f, 0.2f, 0.5f, 1f);

        // -- Hunger bar --
        [Header("Hunger Bar")]
        [Tooltip("Current hunger fill color. Also used as the hunger icon's normal tint.")]
        public Color HungerFillColor = new Color(0.9f, 0.5f, 0.1f, 1f);

        [Tooltip("Normal track color when hunger is not empty.")]
        public Color HungerTrackNormalColor = new Color(0.15f, 0.15f, 0.15f, 0.8f);

        [Tooltip("Static track color when hunger is empty. Also the base icon tint when empty.")]
        public Color HungerEmptyColor = new Color(0.5f, 0.25f, 0.05f, 1f);

        [Tooltip("Color that the hunger progress layer and icon pulse to when hunger is empty. " +
                 "Also applied to the orange arc on the stamina bar.")]
        public Color HungerBlinkColor = new Color(1f, 0.4f, 0f, 1f);

        [Tooltip("Seconds per full smooth pulse cycle for all hunger warning elements. Default 1.")]
        public float HungerBlinkRate = 1f;

        // -- Private bar references --
        private RadialBar _staminaBar;
        private RadialBar _thirstBar;
        private RadialBar _hungerBar;

        private IStaminaData _data;
        private bool _isReady;

        private float _penaltyCap     = 1f;
        private bool _wasAccumulating = false;

        private void Awake()
        {
            if (_document == null)
            {
                Debug.LogError("SurvivalBarsController: no UIDocument assigned.", this);
                return;
            }

            var root = _document.rootVisualElement;

            _staminaBar = root.Q<RadialBar>("stamina-bar");
            _thirstBar  = root.Q<RadialBar>("thirst-bar");
            _hungerBar  = root.Q<RadialBar>("hunger-bar");

            if (_staminaBar == null) Debug.LogWarning("SurvivalBarsController: 'stamina-bar' not found in UXML.", this);
            if (_thirstBar  == null) Debug.LogWarning("SurvivalBarsController: 'thirst-bar' not found in UXML.", this);
            if (_hungerBar  == null) Debug.LogWarning("SurvivalBarsController: 'hunger-bar' not found in UXML.", this);

            InitializeColors();
        }

        private void InitializeColors()
        {
            if (_staminaBar != null)
            {
                _staminaBar.FillColor                    = StaminaFillColor;
                _staminaBar.EffectiveMaxColor             = StaminaEffectiveMaxColor;
                _staminaBar.EffectiveMaxCounterClockwise  = false;
                _staminaBar.TrackColor                   = StaminaTrackColor;
                _staminaBar.HungerPenaltyColor           = StaminaOrangeNormalColor;
                _staminaBar.PenaltyColor                 = StaminaRedNormalColor;
                _staminaBar.MarkerColor                  = StaminaMarkerColor;
                _staminaBar.MarkerThicknessPoints        = StaminaMarkerThicknessPoints;
                _staminaBar.style.unityBackgroundImageTintColor = StaminaFillColor;
            }

            if (_thirstBar != null)
            {
                _thirstBar.FillColor                   = ThirstFillColor;
                _thirstBar.TrackColor                  = ThirstTrackNormalColor;
                _thirstBar.EffectiveMaxCounterClockwise = false;
                _thirstBar.MarkerValue                 = -1f;
                _thirstBar.style.unityBackgroundImageTintColor = ThirstFillColor;
            }

            if (_hungerBar != null)
            {
                _hungerBar.FillColor   = HungerFillColor;
                _hungerBar.TrackColor  = HungerTrackNormalColor;
                _hungerBar.MarkerValue = -1f;
                _hungerBar.style.unityBackgroundImageTintColor = HungerFillColor;
            }
        }

        private void Update()
        {
            if (!_isReady) return;

            float thirst  = _data.NormalizedThirst;
            float hunger  = _data.NormalizedHunger;
            float stamina = _data.NormalizedStamina;

            bool thirstEmpty    = thirst  <= 0f;
            bool hungerEmpty    = hunger  <= 0f;
            bool staminaEmpty   = stamina <= 0f;
            bool isAccumulating = _data.IsAccumulatingRestPenalty;

            float hungerPulse      = hungerEmpty ? HungerBlinkLerp() : 0f;
            Color hungerPulseColor = Color.Lerp(HungerEmptyColor, HungerBlinkColor, hungerPulse);

            bool redBlinkOn = isAccumulating && IsBlinkOn(StaminaRedBlinkRate);

            // -- Thirst bar --
            _thirstBar.Value               = thirst;
            _thirstBar.EffectiveMaxValue   = thirst;
            _thirstBar.RestPenaltyTopValue = thirst;
            _thirstBar.PenaltyCapValue     = thirst;

            _thirstBar.TrackColor = thirstEmpty ? ThirstEmptyColor : ThirstTrackNormalColor;
            _thirstBar.style.unityBackgroundImageTintColor = thirstEmpty
                ? ThirstEmptyColor
                : ThirstFillColor;

            // -- Hunger bar --
            _hungerBar.Value               = hunger;
            _hungerBar.RestPenaltyTopValue = hunger;
            _hungerBar.PenaltyCapValue     = hunger;

            if (hungerEmpty)
            {
                // Static full-ring track in HungerEmptyColor.
                _hungerBar.TrackColor = HungerEmptyColor;

                // Progress layer fills counterclockwise to match penalty arc visual language.
                // EffectiveMaxCounterClockwise = true: arc grows leftward into 12 o'clock
                // as NormalizedHungerMaxPenalty rises from 0 to 1.
                _hungerBar.EffectiveMaxCounterClockwise = true;
                _hungerBar.EffectiveMaxValue            = _data.NormalizedHungerMaxPenalty;
                _hungerBar.EffectiveMaxColor            = hungerPulseColor;
                _hungerBar.style.unityBackgroundImageTintColor = hungerPulseColor;
            }
            else
            {
                // Normal state: clockwise suppression layer hidden under fill.
                _hungerBar.TrackColor                   = HungerTrackNormalColor;
                _hungerBar.EffectiveMaxCounterClockwise = false;
                _hungerBar.EffectiveMaxValue            = hunger;
                _hungerBar.EffectiveMaxColor            = HungerFillColor;
                _hungerBar.style.unityBackgroundImageTintColor = HungerFillColor;
            }

            // -- Stamina bar: icon --
            _staminaBar.style.unityBackgroundImageTintColor = staminaEmpty
                ? StaminaIconEmptyTint
                : StaminaFillColor;

            // -- Stamina bar: red arc cap freeze --
            if (isAccumulating && !_wasAccumulating)
                _penaltyCap = _data.NormalizedRestPenaltyTop;

            float penaltyCapDisplay = isAccumulating
                ? _penaltyCap
                : _data.NormalizedRestPenaltyTop;

            _wasAccumulating = isAccumulating;

            // -- Stamina bar: all layers --
            _staminaBar.PenaltyCapValue     = penaltyCapDisplay;
            _staminaBar.RestPenaltyTopValue = _data.NormalizedRestPenaltyTop;
            _staminaBar.EffectiveMaxValue   = _data.NormalizedEffectiveMax;
            _staminaBar.Value               = _data.NormalizedStamina;
            _staminaBar.MarkerValue         = _data.NormalizedFatigueFloor;

            _staminaBar.HungerPenaltyColor = hungerEmpty
                ? hungerPulseColor
                : StaminaOrangeNormalColor;

            _staminaBar.PenaltyColor = redBlinkOn
                ? StaminaRedBlinkColor
                : StaminaRedNormalColor;
        }

        private float HungerBlinkLerp()
        {
            return Mathf.PingPong(Time.time / HungerBlinkRate, 1f);
        }

        private static bool IsBlinkOn(float rate)
        {
            return (Time.time % rate) < rate * 0.5f;
        }

        public void Bind(IStaminaData data)
        {
            _data            = data;
            _isReady         = data != null;
            _wasAccumulating = false;
            _penaltyCap      = 1f;

            InitializeColors();

            if (!_isReady)
                Debug.LogWarning("SurvivalBarsController: Bind() called with null data.", this);
        }
    }
}
