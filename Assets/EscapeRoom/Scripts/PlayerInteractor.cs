using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace EscapeRoom
{
    /// <summary>Single-click interaction hub.
    ///
    /// Priority order on every click:
    ///   1. UI under the crosshair  -> ignored, the EventSystem/Canvas handles it
    ///   2. holding an item + device -> hand the item to that device (terminal, socket, door panel)
    ///   3. holding an item         -> drop it
    ///   4. free hand + item        -> pick it up
    ///   5. free hand + device      -> device.Interact(null)
    ///
    /// Uses only UnityEngine.InputSystem (this project is activeInputHandler: 1, so the
    /// legacy Input class does not exist).
    /// </summary>
    public class PlayerInteractor : MonoBehaviour
    {
        public float interactDistance = 3.4f;
        public Transform holdPoint;          // optional; falls back to the camera

        Camera _cam;
        GrabbableItem _held;
        Transform _hold;
        Renderer _promptTarget;
        string _prompt = "";

        public GrabbableItem Held => _held;
        public string Prompt => _prompt;
        public Transform HoldTransform => _hold;

        /// <summary>Drops the interactor's claim on anything it is not actually holding.
        ///
        /// Public and separate from <c>Update</c> so a test can reproduce the stuck state and prove
        /// it heals, without needing a synthesised mouse press. The state is the whole bug: a dropped
        /// item with the interactor still pointing at it, which reads as "I cannot pick anything up
        /// any more" and never recovers on its own.</summary>
        public void ReconcileHeld()
        {
            if (_held != null && !_held.IsHeld) _held = null;
        }

        /// <summary>Test hook: claims an item without a mouse press, so the drop path can be driven.
        /// Exists only for the self test; nothing in the game calls it.</summary>
        public void ForceClaim(GrabbableItem item) { _held = item; }

        /// <summary>True when t belongs to the held item's subtree (the item root itself or one
        /// of its visual parts). The key is 9 meshes under one GrabbableItem, so testing only
        /// the root transform would miss 9 out of 10 self-hits.</summary>
        public static bool IsHeldPart(GrabbableItem held, Transform t)
        {
            if (held == null || t == null) return false;
            return t == held.transform || t.IsChildOf(held.transform);
        }

        /// <summary>First ray hit that is NOT the held item itself.
        ///
        /// The key hangs 1.9 m in front of the camera, directly under the crosshair ray. A single
        /// Raycast returns whatever is nearest - and while the player turns, the exponentially
        /// followed item trails across the ray. Without this filter the click aimed at the socket
        /// lands on the key instead: no device, so the interactor takes the DROP branch and the
        /// insert becomes a drop. Skipping the held subtree makes that state unrepresentable.
        ///
        /// Shared with Self Test 10, so the test exercises the real routine instead of a copy.
        /// </summary>
        public static bool FirstSolidHit(RaycastHit[] hits, GrabbableItem held, out RaycastHit hit)
        {
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.collider == null) continue;
                if (IsHeldPart(held, h.collider.transform)) continue;
                hit = h;
                return true;
            }
            hit = default(RaycastHit);
            return false;
        }

        void Start()
        {
            _cam = Camera.main;
            if (_cam == null) _cam = GetComponentInChildren<Camera>();
            _hold = holdPoint != null ? holdPoint : (_cam != null ? _cam.transform : transform);
        }

        void Update()
        {
            if (_cam == null) return;
            // Self-heal before anything else. The interactor's notion of "what I am holding" and the
            // item's own can disagree, and the report was that after dropping a prop NOTHING could be
            // picked up again. Cause: the drop branch below called Drop() and returned without
            // clearing _held, so the interactor went on believing it held a dropped item. Every later
            // click therefore took the "I am holding something" path, which can only drop again -
            // never pick up. One missing line, and the whole game became unpickable after one drop.
            //
            // This check makes that state unrepresentable rather than merely fixed: whatever forgets
            // to clear the field, the next frame notices the item is not held and drops the claim.
            ReconcileHeld();
            if (!ReadPress()) { UpdatePrompt(); return; }

            // UI wins: never let a world raycast steal a click meant for the Canvas.
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

            Ray ray = _cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            // RaycastAll, not Raycast: the held item hangs under this very ray and must be
            // filtered by FirstSolidHit instead of winning it (see above).
            var hits = Physics.RaycastAll(ray, interactDistance, ~0, QueryTriggerInteraction.Collide);
            RaycastHit hit;
            bool hasHit = FirstSolidHit(hits, _held, out hit);

            if (_held != null)
            {
                var dev = hasHit ? hit.collider.GetComponentInParent<IInteractable>() : null;
                if (dev != null)
                {
                    // Only forget the item if the device actually consumed it. On a wrong
                    // item the device returns false and the player keeps holding it.
                    if (dev.Interact(_held)) _held = null;
                    return;
                }
                // Drop AND forget. Clearing _held here is the actual fix; ReconcileHeld() is the
                // belt to this pair of braces, so a future path that drops without clearing cannot
                // leave the player holding something they do not have.
                var dropped = _held;
                _held = null;
                dropped.Drop();
                return;
            }

            if (!hasHit) return;
            var grabbable = hit.collider.GetComponentInParent<GrabbableItem>();
            if (grabbable != null && !grabbable.IsHeld && !grabbable.pickupLocked)
            {
                grabbable.PickUp(_hold);
                _held = grabbable;
                return;
            }
            var device = hit.collider.GetComponentInParent<IInteractable>();
            if (device != null) device.Interact(null);
        }

        void UpdatePrompt()
        {
            if (_cam == null) { _prompt = ""; return; }
            Ray ray = _cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            var hits = Physics.RaycastAll(ray, interactDistance, ~0, QueryTriggerInteraction.Collide);
            RaycastHit hit;
            // The held filter is a no-op empty-handed; holding, the key hanging in front of
            // the camera must not answer for what the player is aiming at.
            bool hasHit = FirstSolidHit(hits, _held, out hit);
            if (_held != null)
            {
                // The teaching moment, before the click rather than after it: which slot this
                // is, and whether what is in hand belongs there. PromptForAim deliberately
                // hides the order guard (D-10) - the player works that out by trying.
                string next = "Clique para largar " + _held.displayName;
                if (hasHit)
                {
                    var d = hit.collider.GetComponentInParent<IInteractable>();
                    if (d != null)
                    {
                        string aim = (d is ItemSocket)
                            ? ((ItemSocket)d).PromptForAim(_held)
                            : d.PromptFor(_held);
                        if (!string.IsNullOrEmpty(aim)) next = aim;
                    }
                }
                _prompt = next;
                return;
            }
            string free = "";
            if (hasHit)
            {
                var g = hit.collider.GetComponentInParent<GrabbableItem>();
                if (g != null && !g.pickupLocked) free = "Clique para pegar " + g.displayName;
                else
                {
                    var d = hit.collider.GetComponentInParent<ItemSocket>();
                    if (d != null) free = d.PromptForAim(_held);
                    else
                    {
                        var other = hit.collider.GetComponentInParent<IInteractable>();
                        if (other != null) free = other.PromptFor(_held);
                    }
                }
            }
            _prompt = free;
        }

        static bool ReadPress()
        {
            var m = Mouse.current;
            if (m != null && m.leftButton.wasPressedThisFrame) return true;
            var k = Keyboard.current;
            if (k != null && k.eKey.wasPressedThisFrame) return true;
            var g = Gamepad.current;
            if (g != null && g.buttonWest.wasPressedThisFrame) return true;
            return false;
        }
    }
}
