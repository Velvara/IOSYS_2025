using UnityEngine;
using UnityEngine.UIElements;

namespace StarterAssets
{
    /// <summary>
    /// A custom UI Toolkit control that draws a partially filled ring using Painter2D.
    ///
    /// SIX LAYERS drawn back to front (outermost to innermost):
    ///
    ///   1. Track (gray)               full 360 deg ring
    ///   2. Penalty arc (red)          RestPenaltyTopValue to PenaltyCapValue
    ///   3. HungerPenalty (orange)     EffectiveMaxValue to RestPenaltyTopValue
    ///   4. EffectiveMax (dark green)  0 to EffectiveMaxValue (clockwise)
    ///                                 OR (1 - EffectiveMaxValue) to 1 (counterclockwise)
    ///                                 controlled by EffectiveMaxCounterClockwise
    ///   5. Fill (bright color)        0 to Value
    ///   6. Marker line                MarkerValue to MarkerValue + (MarkerThicknessPoints / 100)
    ///
    /// EFFECTIVEMAX COUNTERCLOCKWISE
    /// ------------------------------
    /// When EffectiveMaxCounterClockwise is false (default):
    ///   Arc draws from 12 o'clock clockwise by EffectiveMaxValue. Fills rightward.
    ///   Used for: stamina bar dark green zone (recovery ceiling).
    ///
    /// When EffectiveMaxCounterClockwise is true:
    ///   Arc draws from (1 - EffectiveMaxValue) counterclockwise to 12 o'clock.
    ///   As value grows 0→1, the arc grows leftward into 12 o'clock.
    ///   Used for: hunger bar penalty progress indicator (fills counterclockwise
    ///   to match the visual language of the red/orange penalty arcs on stamina).
    ///
    /// PAINTER2D COORDINATE SYSTEM
    /// ----------------------------
    /// Angles clockwise from 3 o'clock. 12 o'clock = -90 degrees.
    /// Full ring: -90 to 270 degrees.
    /// </summary>
    [UxmlElement]
    public partial class RadialBar : VisualElement
    {
        // -- Fill --
        [UxmlAttribute]
        public float Value
        {
            get => _value;
            set { _value = Mathf.Clamp01(value); MarkDirtyRepaint(); }
        }

        [UxmlAttribute]
        public Color FillColor
        {
            get => _fillColor;
            set { _fillColor = value; MarkDirtyRepaint(); }
        }

        // -- Effective max --
        [UxmlAttribute]
        public float EffectiveMaxValue
        {
            get => _effectiveMaxValue;
            set { _effectiveMaxValue = Mathf.Clamp01(value); MarkDirtyRepaint(); }
        }

        [UxmlAttribute]
        public Color EffectiveMaxColor
        {
            get => _effectiveMaxColor;
            set { _effectiveMaxColor = value; MarkDirtyRepaint(); }
        }

        /// <summary>
        /// When false (default): EffectiveMax arc fills clockwise from 12 o'clock.
        /// When true: EffectiveMax arc fills counterclockwise into 12 o'clock.
        /// Set to true on the hunger bar when empty to match the visual language
        /// of the penalty arcs on the stamina bar.
        /// </summary>
        [UxmlAttribute]
        public bool EffectiveMaxCounterClockwise
        {
            get => _effectiveMaxCounterClockwise;
            set { _effectiveMaxCounterClockwise = value; MarkDirtyRepaint(); }
        }

        // -- Hunger penalty (orange) --
        [UxmlAttribute]
        public float RestPenaltyTopValue
        {
            get => _restPenaltyTopValue;
            set { _restPenaltyTopValue = Mathf.Clamp01(value); MarkDirtyRepaint(); }
        }

        [UxmlAttribute]
        public Color HungerPenaltyColor
        {
            get => _hungerPenaltyColor;
            set { _hungerPenaltyColor = value; MarkDirtyRepaint(); }
        }

        // -- Track (gray) --
        [UxmlAttribute]
        public Color TrackColor
        {
            get => _trackColor;
            set { _trackColor = value; MarkDirtyRepaint(); }
        }

        // -- Session penalty arc (red) --
        [UxmlAttribute]
        public float PenaltyCapValue
        {
            get => _penaltyCapValue;
            set { _penaltyCapValue = Mathf.Clamp01(value); MarkDirtyRepaint(); }
        }

        [UxmlAttribute]
        public Color PenaltyColor
        {
            get => _penaltyColor;
            set { _penaltyColor = value; MarkDirtyRepaint(); }
        }

        // -- Shared ring thickness --
        [UxmlAttribute]
        public float RingThickness
        {
            get => _ringThickness;
            set { _ringThickness = Mathf.Max(1f, value); MarkDirtyRepaint(); }
        }

        // -- Marker line --
        [UxmlAttribute]
        public float MarkerValue
        {
            get => _markerValue;
            set { _markerValue = value; MarkDirtyRepaint(); }
        }

        [UxmlAttribute]
        public float MarkerThicknessPoints
        {
            get => _markerThicknessPoints;
            set { _markerThicknessPoints = Mathf.Max(0.1f, value); MarkDirtyRepaint(); }
        }

        [UxmlAttribute]
        public Color MarkerColor
        {
            get => _markerColor;
            set { _markerColor = value; MarkDirtyRepaint(); }
        }

        // -- Private backing fields --
        private float _value                       = 1f;
        private float _effectiveMaxValue           = 1f;
        private float _restPenaltyTopValue         = 1f;
        private float _penaltyCapValue             = 1f;
        private float _ringThickness               = 8f;
        private float _markerValue                 = -1f;
        private float _markerThicknessPoints       = 2f;
        private bool  _effectiveMaxCounterClockwise = false;

        private Color _fillColor          = new Color(0.2f, 0.8f, 0.2f, 1f);
        private Color _effectiveMaxColor  = new Color(0.1f, 0.4f, 0.1f, 1f);
        private Color _hungerPenaltyColor = new Color(0.9f, 0.5f, 0.1f, 1f);
        private Color _trackColor         = new Color(0.15f, 0.15f, 0.15f, 0.8f);
        private Color _penaltyColor       = new Color(0.9f, 0.1f, 0.1f, 1f);
        private Color _markerColor        = Color.white;

        // -- Constructor --
        public RadialBar()
        {
            generateVisualContent += GenerateVisualContent;
        }

        // -- Drawing --

        private void GenerateVisualContent(MeshGenerationContext context)
        {
            float width  = resolvedStyle.width;
            float height = resolvedStyle.height;

            if (width < 1f || height < 1f)
                return;

            var painter = context.painter2D;

            float cx     = width  * 0.5f;
            float cy     = height * 0.5f;
            float radius = Mathf.Min(cx, cy) - _ringThickness * 0.5f;

            const float startAngle = -90f;
            const float fullArc    = 360f;

            painter.lineWidth = _ringThickness;
            painter.lineCap   = LineCap.Butt;

            var center = new Vector2(cx, cy);

            float effectiveMaxAngle   = startAngle + fullArc * _effectiveMaxValue;
            float restPenaltyTopAngle = startAngle + fullArc * _restPenaltyTopValue;
            float penaltyCapAngle     = startAngle + fullArc * _penaltyCapValue;

            // -- Layer 1: Track --
            painter.strokeColor = _trackColor;
            painter.BeginPath();
            painter.Arc(center, radius, startAngle, startAngle + fullArc);
            painter.Stroke();

            // -- Layer 2: Red session penalty arc --
            if (_penaltyCapValue > _restPenaltyTopValue)
            {
                painter.strokeColor = _penaltyColor;
                painter.BeginPath();
                painter.Arc(center, radius, restPenaltyTopAngle, penaltyCapAngle);
                painter.Stroke();
            }

            // -- Layer 3: Orange hunger penalty band --
            if (_restPenaltyTopValue > _effectiveMaxValue)
            {
                painter.strokeColor = _hungerPenaltyColor;
                painter.BeginPath();
                painter.Arc(center, radius, effectiveMaxAngle, restPenaltyTopAngle);
                painter.Stroke();
            }

            // -- Layer 4: Effective max arc --
            // Clockwise (default): startAngle → startAngle + arc * value
            //   Grows rightward from 12 o'clock. Used for stamina dark green zone.
            // Counterclockwise: startAngle + arc * (1 - value) → startAngle + arc
            //   Grows leftward into 12 o'clock. Used for hunger penalty progress.
            if (_effectiveMaxValue > 0f)
            {
                painter.strokeColor = _effectiveMaxColor;
                painter.BeginPath();

                if (_effectiveMaxCounterClockwise)
                {
                    // Arc starts at (1 - value) and ends at the full ring (12 o'clock).
                    // As value grows 0→1, the start point moves left toward 12 o'clock,
                    // growing the arc counterclockwise.
                    float ccwStart = startAngle + fullArc * (1f - _effectiveMaxValue);
                    float ccwEnd   = startAngle + fullArc; // 270 degrees = back to 12 o'clock
                    painter.Arc(center, radius, ccwStart, ccwEnd);
                }
                else
                {
                    painter.Arc(center, radius, startAngle, effectiveMaxAngle);
                }

                painter.Stroke();
            }

            // -- Layer 5: Fill --
            if (_value > 0f)
            {
                float fillEnd = startAngle + fullArc * _value;
                painter.strokeColor = _fillColor;
                painter.BeginPath();
                painter.Arc(center, radius, startAngle, fillEnd);
                painter.Stroke();
            }

            // -- Layer 6: Marker line --
            if (_markerValue >= 0f)
            {
                float markerNormalizedWidth = _markerThicknessPoints / 100f;
                float markerStart = startAngle + fullArc * _markerValue;
                float markerEnd   = markerStart + fullArc * markerNormalizedWidth;

                painter.strokeColor = _markerColor;
                painter.BeginPath();
                painter.Arc(center, radius, markerStart, markerEnd);
                painter.Stroke();
            }
        }
    }
}
