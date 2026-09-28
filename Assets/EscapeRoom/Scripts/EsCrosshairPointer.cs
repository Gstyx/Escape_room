using System.Collections.Generic;   // HashSet, for the one-vote-per-handler dedupe
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace EscapeRoom
{
    /// <summary>Routes a click to whatever world-space canvas element the player is pointing at.
    ///
    /// FORGIVENESS IS PER CANDIDATE, NOT GLOBAL - the compromise that satisfies both complaints.
    ///
    /// Two things were reported, and they pull in opposite directions. Canvas buttons are hard to
    /// click from across the room, because a world-space canvas button shrinks with distance and
    /// Unity's manual is explicit that the canvas scale fixes the world size of one canvas pixel, so
    /// that is not a setting to be tuned. But a wide aiming aid that reaches the keypad types the
    /// code for the player, and that is the puzzle answered rather than made easier.
    ///
    /// The resolution is that the keypad is not "a canvas button", it is puzzle input - it is the
    /// player typing an answer. So a candidate marked <see cref="EsPuzzleInput"/> only matches
    /// within <see cref="snapRadiusPixels"/>, and everything else matches within
    /// <see cref="looseRadiusPixels"/>. The reboot button becomes comfortable from the spawn; the
    /// digits do not get easier. Neither the interact key nor the prompt is back, because the wide
    /// tolerance is now reachable by an ordinary click and needs no key to abuse.
    ///
    /// The click is still forgiving in the way that costs no information: the search is the exact
    /// distance from the crosshair to the element's real screen rect, zero when inside. An earlier
    /// version probed 8 points on two rings, which sounds like a tolerance and is not one - with a
    /// 29 px tall button the crosshair can fall between two samples and the "55 px of slack" does
    /// nothing. A radius is not coverage; the distance to the rectangle is.
    /// </summary>
    [RequireComponent(typeof(EventSystem))]
    public class EsCrosshairPointer : MonoBehaviour
    {
        /// <summary>Screen radius that applies to EVERY candidate, including puzzle input. Roughly
        /// a fingertip at 1080p. On the keypad this is the difference between aiming near a key and
        /// aiming at the neighbouring one, and a mis-typed digit is a real cost.</summary>
        public float snapRadiusPixels = 55f;

        /// <summary>Screen radius for candidates that are NOT puzzle input. Wide on purpose, and it
        /// is what makes the terminal reachable from the spawn without touching the answer.</summary>
        public float looseRadiusPixels = 260f;

        /// <summary>Logs every click decision. Off by default because the player is trying to play,
        /// not read a log - but "clicking the digits does nothing" is unsolvable by reasoning,
        /// because the deferral below depends on where the OS cursor happened to freeze and that
        /// differs between the editor and a real session. Turn this on and the next report contains
        /// the answer instead of another guess.</summary>
        public bool verboseClicks = false;

        /// <summary>What the crosshair is currently on, or null. Exposed so the crosshair itself can
        /// react - "you can click now" is a different piece of information from "here is the
        /// target", and the player asked for both.</summary>
        public GameObject Hovered { get; private set; }

        GameObject _armed;
        EsCrosshair _crosshair;

        void Update()
        {
            var module = GetComponent<BaseInputModule>();

            // ONE POINTER, NOT TWO. While the cursor is locked, the crosshair IS the pointer, so the
            // normal input path is switched off rather than consulted.
            //
            // It used to be consulted: the click deferred whenever the normal path found any
            // handler, on the reasoning that the normal path must then be handling it. With a locked
            // cursor that reasoning is wrong, because the normal path raycasts the FROZEN OS cursor -
            // a stale point the player never aimed at. So the click aimed at one key could be handed
            // to a different key, or to a stale point over nothing at all. That is exactly the
            // reported symptom: the highlight is on the right key, the click does nothing.
            //
            // Switching the module off makes that unrepresentable. The crosshair becomes the only
            // pointer, so the highlight, the target search and the dispatched click cannot disagree,
            // because there is nothing left to disagree with them. The cost is that the Screen Space
            // Overlay end panel is not mouse-clickable while locked - but it never reliably was,
            // since it also depends on where the cursor froze. Esc releases the cursor and the module
            // comes straight back, which is the path the end panel is used on anyway.
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                if (module != null && module.enabled) module.enabled = false;
            }
            else if (module != null && !module.enabled) module.enabled = true;

            if (Cursor.lockState != CursorLockMode.Locked) return;

            var mouse = Mouse.current;

            // Every frame, not on click: the highlight is aim feedback, and it has to be there
            // BEFORE the player commits to the click.
            var over = mouse != null ? FindTarget() : null;
            if (over != Hovered)
            {
                if (Hovered != null && Hovered.TryGetComponent<EsHoverHighlight>(out var oldHl))
                    oldHl.SetHovered(false);
                Hovered = over;
                if (Hovered != null && Hovered.TryGetComponent<EsHoverHighlight>(out var newHl))
                    newHl.SetHovered(true);
            }

            if (_crosshair == null) _crosshair = Object.FindFirstObjectByType<EsCrosshair>();
            if (_crosshair != null && (over != null) != (_armed != null))
            {
                _armed = over;
                _crosshair.SetArmed(over != null);
            }

            if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;

            var es = EventSystem.current;
            if (es == null) return;

            // Same target the highlight is already showing, resolved again rather than reused, so
            // the click can never disagree with the picture. With the module off there is no second
            // path, so there is no deferral to make and no double-fire to guard against.
            var target = FindTarget();
            if (target == null) return;

            if (verboseClicks)
                Debug.Log("[Click] crosshair -> " + target.name
                          + "  (frozen OS cursor at " + mouse.position.ReadValue().ToString("F0")
                          + " is IGNORED; the input module is off while locked)");

            var ped = new PointerEventData(es)
            {
                position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f),
                button = PointerEventData.InputButton.Left,
                clickTime = Time.unscaledTime,
                clickCount = 1,
                eligibleForClick = true,
                pointerPress = target,
                pointerClick = target
            };
            ExecuteEvents.Execute(target, ped, ExecuteEvents.pointerClickHandler);
        }

        /// <summary>True when the object sits under something marked as puzzle input. Walks up from
        /// the handler, because the marker is on the keypad ROOT and the hit is on a key.</summary>
        public static bool IsPuzzleInput(GameObject handler)
        {
            for (var t = handler.transform; t != null; t = t.parent)
                if (t.GetComponent<EsPuzzleInput>() != null) return true;
            return false;
        }

        /// <summary>Nearest clickable world-space canvas element to the screen centre, each judged
        /// against the radius its own category allows. Null if nothing qualifies.</summary>
        public GameObject FindTarget()
        {
            var es = EventSystem.current;
            if (es == null) return null;
            var crosshair = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            GameObject best = null;
            float bestDist = float.MaxValue;
            // Deduped by reference rather than by instance id: GetInstanceID is obsolete in Unity
            // 6.6, and a Button with a background plus a label is two Graphics that must not
            // out-vote each other.
            var seen = new HashSet<GameObject>();

            // The no-argument overload, not FindObjectsSortMode: that parameter is deprecated in
            // Unity 6.6 and the default already means "active, loaded", which is what is wanted.
            foreach (var canvas in Object.FindObjectsByType<Canvas>())
            {
                if (canvas == null || !canvas.isActiveAndEnabled) continue;
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) continue;   // HUD, not the world
                var cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
                if (cam == null) continue;

                foreach (var g in canvas.GetComponentsInChildren<Graphic>(false))
                {
                    if (!g.raycastTarget || !g.gameObject.activeInHierarchy) continue;
                    var sel = g.GetComponent<Selectable>();
                    if (sel != null && (!sel.IsActive() || !sel.IsInteractable())) continue;

                    var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(g.gameObject);
                    if (handler == null) continue;
                    if (!seen.Add(handler)) continue;   // one vote per handler

                    var corners = new Vector3[4];
                    g.rectTransform.GetWorldCorners(corners);
                    Vector2 s0 = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                    Vector2 s1 = RectTransformUtility.WorldToScreenPoint(cam, corners[1]);
                    Vector2 s2 = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                    Vector2 s3 = RectTransformUtility.WorldToScreenPoint(cam, corners[3]);

                    var min = new Vector2(Mathf.Min(Mathf.Min(s0.x, s1.x), Mathf.Min(s2.x, s3.x)),
                                         Mathf.Min(Mathf.Min(s0.y, s1.y), Mathf.Min(s2.y, s3.y)));
                    var max = new Vector2(Mathf.Max(Mathf.Max(s0.x, s1.x), Mathf.Max(s2.x, s3.x)),
                                         Mathf.Max(Mathf.Max(s0.y, s1.y), Mathf.Max(s2.y, s3.y)));
                    float d = RectDistance(min, max, crosshair);

                    // Each candidate is judged against the radius ITS OWN category allows. The
                    // keypad root carries EsPuzzleInput, so its keys can only ever be hit within
                    // snapRadiusPixels no matter how far the loose radius reaches.
                    float limit = IsPuzzleInput(handler) ? snapRadiusPixels : looseRadiusPixels;
                    if (d >= limit) continue;
                    if (d >= bestDist) continue;
                    best = handler; bestDist = d;
                }
            }
            return best;
        }

        /// <summary>True distance from a point to a rectangle; zero when the point is inside it.
        /// This is what makes the tolerance real coverage instead of a ring of samples.</summary>
        public static float RectDistance(Vector2 min, Vector2 max, Vector2 p)
        {
            float dx = Mathf.Max(min.x - p.x, 0f, p.x - max.x);
            float dy = Mathf.Max(min.y - p.y, 0f, p.y - max.y);
            return Mathf.Sqrt(dx * dx + dy * dy);
        }
    }
}
