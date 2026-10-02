using UnityEngine;
using UnityEngine.UI;

namespace EscapeRoom
{
    /// <summary>Blinks a denied-access hologram label (A.E.G.I.S. Fase 0).
    /// Alpha-only: never touches raycastTarget or active state, so it cannot
    /// eat crosshair clicks or trip the canvas-overlap checks.</summary>
    public class EsHologramFlicker : MonoBehaviour
    {
        public float speed = 3.1f;
        public float phase;
        public float dimAlpha = 0.12f;

        Text _t;
        Color _base;

        void Awake()
        {
            _t = GetComponent<Text>();
            if (_t != null) _base = _t.color;
        }

        void Update()
        {
            if (_t == null) return;
            float s = Mathf.Sin(Time.time * speed + phase);
            float a = s > -0.35f ? 1f : dimAlpha;
            _t.color = new Color(_base.r, _base.g, _base.b, _base.a * a);
        }
    }
}
