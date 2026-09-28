using UnityEngine;
using UnityEngine.UI;

namespace EscapeRoom
{
    /// <summary>Visual feedback for "the crosshair is on this, and clicking it will do something".
    ///
    /// NOT driven by <c>IPointerEnterHandler</c>, which is the usual answer and would be wrong here.
    /// Unity's own documentation for that interface says the criteria are "implementation
    /// dependent" - it is the input module that decides to send it, and it sends it at the module's
    /// own pointer position. With the cursor locked that position is the frozen OS cursor, not the
    /// crosshair, so a hover built on it highlights whatever the cursor happened to be parked on
    /// rather than what a click would actually hit. A highlight that lies is worse than no
    /// highlight, because the player trusts it and clicks the wrong thing.
    ///
    /// So <see cref="EsCrosshairPointer"/> drives this from <c>FindTarget()</c> - the identical
    /// search the click uses, with the identical per-candidate radius. What lights up is therefore
    /// exactly what a click would reach, by construction rather than by agreement.
    ///
    /// Three cues, because they fail in different ways: a brighter plate reads at a distance, an
    /// outline reads when the plate is already near white, and a slight scale reads in peripheral
    /// vision. The base colours are captured in Awake and the outline is created once and toggled -
    /// rebuilding either per hover would allocate during aiming, which is the wrong place to
    /// allocate.
    /// </summary>
    [RequireComponent(typeof(Graphic))]
    public class EsHoverHighlight : MonoBehaviour
    {
        public Color hoverTint = new Color(1f, 1f, 1f, 1f);
        public float brighten = 0.35f;
        public float hoverScale = 1.04f;
        public Color outlineColor = new Color(1f, 0.92f, 0.45f, 0.95f);
        public float outlineWidth = 3f;

        Graphic _graphic;
        Outline _outline;
        RectTransform _rt;
        Color _baseColor;
        Vector3 _baseScale;
        bool _hovered;

        public bool Hovered => _hovered;

        void Awake()
        {
            _graphic = GetComponent<Graphic>();
            _rt = (RectTransform)transform;
            _baseColor = _graphic.color;
            _baseScale = _rt.localScale;

            _outline = gameObject.AddComponent<Outline>();
            _outline.effectColor = outlineColor;
            _outline.effectDistance = new Vector2(outlineWidth, -outlineWidth);
            _outline.useGraphicAlpha = true;
            _outline.enabled = false;      // off until hovered, so it costs nothing at rest
        }

        /// <summary>Called every frame by the crosshair pointer. Idempotent: the visual work only
        /// happens on an actual change of state, so a stationary crosshair costs nothing.</summary>
        public void SetHovered(bool on)
        {
            if (on == _hovered) return;
            _hovered = on;

            if (on)
            {
                // Additive brighten rather than a flat tint, so a plate that is already bright
                // (the amber ENTER) does not clip to white and lose its shape.
                _graphic.color = _baseColor + (Color.white - _baseColor) * brighten;
                if (_outline != null) _outline.enabled = true;
                if (_rt != null) _rt.localScale = _baseScale * hoverScale;
            }
            else
            {
                _graphic.color = _baseColor;
                if (_outline != null) _outline.enabled = false;
                if (_rt != null) _rt.localScale = _baseScale;
            }
        }
    }
}
