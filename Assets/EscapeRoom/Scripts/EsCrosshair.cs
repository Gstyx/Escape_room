using UnityEngine;
using UnityEngine.UI;

namespace EscapeRoom
{
    /// <summary>A screen-centre crosshair, drawn on its own overlay canvas.
    ///
    /// This started as a debug aid and turned out to be load-bearing for play. With
    /// <c>Cursor.lockState = Locked</c> the OS cursor is frozen wherever it happened to be, and
    /// every click is resolved at the centre of the screen (see <see cref="EsCrosshairPointer"/>).
    /// So the centre of the screen IS the cursor, and without something drawn there the player has
    /// no way to tell where they are aiming - which is most of why clicking a world-space button
    /// felt arbitrary.
    ///
    /// The canvas deliberately has NO <c>GraphicRaycaster</c>. That is the guarantee that matters:
    /// the crosshair is invisible to the UI event system, so it can never intercept a click that
    /// was meant for the terminal. Every graphic also sets <c>raycastTarget = false</c>, so even if
    /// a raycaster were added later the pieces would not be hit targets. Two independent guards,
    /// because an invisible full-screen element that eats clicks is exactly the class of bug this
    /// project has produced repeatedly.
    ///
    /// It hides itself when the cursor is unlocked: the crosshair represents the locked-cursor
    /// aiming point, and once the real cursor is visible and clickable, showing both is just two
    /// pointers disagreeing with each other.
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public class EsCrosshair : MonoBehaviour
    {
        public bool enabledByDefault = true;
        public Color tint = new Color(0.92f, 0.96f, 1f, 0.85f);
        public Color dotTint = new Color(1f, 0.86f, 0.35f, 0.95f);

        public float armLength = 11f;
        public float armThickness = 2f;
        public float centreGap = 5f;      // pixels of clear space around the middle
        public float dotSize = 3f;

        /// <summary>Colour of the dot when the crosshair is on something clickable. Separate from
        /// the arm tint because the dot is what the player actually looks at while aiming, and
        /// "you can click now" is a different message from "here is where you are pointing".</summary>
        public Color armedTint = new Color(0.35f, 1f, 0.55f, 1f);

        CanvasGroup _group;
        bool _userEnabled;
        bool _initialised;
        bool _armed;
        Graphic _dot;

        public bool Visible => _userEnabled;

        /// <summary>Called by <see cref="EsCrosshairPointer"/> when the crosshair is over a target
        /// that a click would reach. Drives the dot colour, which is the cheapest possible
        /// "now" signal - it needs no reading, just a glance at the middle of the screen.</summary>
        public void SetArmed(bool armed)
        {
            _armed = armed;
            if (_dot != null) _dot.color = armed ? armedTint : dotTint;
        }

        void Awake()
        {
            _userEnabled = enabledByDefault;
            _group = GetComponent<CanvasGroup>();
            if (_group == null) _group = gameObject.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;      // third guard, at the group level
            _group.interactable = false;
            Build();
        }

        void Update()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f1Key.wasPressedThisFrame) _userEnabled = !_userEnabled;
            if (!_initialised) Build();
            // Only meaningful while the OS cursor is frozen; otherwise the real cursor is the aim.
            _group.alpha = _userEnabled && Cursor.lockState == CursorLockMode.Locked ? 1f : 0f;
        }

        /// <summary>Built in code rather than authored as a prefab so the whole scene stays
        /// reproducible from the menu item, and so the pixel geometry is derived from the arm
        /// parameters instead of being typed into an inspector and drifting.</summary>
        void Build()
        {
            for (int i = transform.childCount - 1; i >= 0; i--) Destroy(transform.GetChild(i).gameObject);

            float a = armThickness * 0.5f;
            float inner = centreGap + a;
            float outer = centreGap + armLength;

            Bar(new Vector2(armThickness, armLength), new Vector2(0f, (inner + outer) * 0.5f), tint);   // up
            Bar(new Vector2(armThickness, armLength), new Vector2(0f, -(inner + outer) * 0.5f), tint);  // down
            Bar(new Vector2(armLength, armThickness), new Vector2((inner + outer) * 0.5f, 0f), tint);   // right
            Bar(new Vector2(armLength, armThickness), new Vector2(-(inner + outer) * 0.5f, 0f), tint);  // left
            _dot = Bar(new Vector2(dotSize, dotSize), Vector2.zero, dotTint);                          // centre dot

            _initialised = true;
        }

        Graphic Bar(Vector2 size, Vector2 anchoredPos, Color c)
        {
            var go = new GameObject("Bar", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            var rt = (RectTransform)go.transform;
            // Screen Space Overlay is not scaled by a CanvasScaler, so these are already pixels.
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = anchoredPos;

            var img = go.AddComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            return img;
        }
    }
}
