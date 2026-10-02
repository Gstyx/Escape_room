using UnityEngine;

namespace EscapeRoom
{
    /// <summary>An object the player can pick up, carry and drop. Physics-driven, no parenting
    /// tricks: while held the rigidbody goes kinematic and is moved with MovePosition.</summary>
    [RequireComponent(typeof(Rigidbody))]
    public class GrabbableItem : MonoBehaviour
    {
        [Tooltip("Logical id: 'chave', 'celula' or 'cartao'.")]
        public string itemKey = "";
        public string displayName = "Item";
        public float holdDistance = 1.9f;
        public float holdHeight = -0.22f;
        public float followSpeed = 16f;

        /// <summary>How hard a dropped item is pushed away. Was 4.5, which launched the item across
        /// the room: a 4.5 N.s impulse on a 1 kg body is 4.5 m/s, enough to clear a crate, land in a
        /// wall, or roll behind something. The items gate the puzzle - losing one is an unwinnable
        /// game, not an annoyance - so the drop has to be gentle enough that the item lands where
        /// the player dropped it and is trivially re-grabbable.</summary>
        public float throwSpeed = 1.1f;

        /// <summary>Beyond this distance from where the item started, it is assumed lost and
        /// returned. A safety net for the soft-lock: anything the player can no longer reach comes
        /// back by itself rather than ending the run.</summary>
        public float leashRadius = 6.0f;

        /// <summary>Below this height the item has left the playable volume (fallen through the
        /// floor, or thrown off the rooftop) and is returned.</summary>
        public float killBelowY = -3.0f;

        /// <summary>Grace period after a drop, so the leash does not fire while the item is still
        /// flying through a legitimately far arc.</summary>
        public float leashDelay = 1.2f;

        /// <summary>When true the interactor will not pick this up again (e.g. a plate
        /// seated in the optical reader: rotation happens through the reader's own
        /// GIRAR buttons, and a free-hand click must not steal the plate back).</summary>
        public bool pickupLocked;

        Rigidbody _rb;
        Vector3 _homePos;
        Quaternion _homeRot;
        Transform _holder;
        Vector3 _throwDir;
        float _droppedAt;

        public bool IsHeld => _holder != null;

        /// <summary>Who is holding it, or null. Public so the interactor's reconcile can be
        /// exercised from a test - the bug it guards against lives in a field nobody can read.</summary>
        public Transform Holder => _holder;

        public string ItemKey => itemKey;
        public float SecondsSinceDrop => IsHeld ? 0f : Time.time - _droppedAt;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _homePos = transform.position;
            _homeRot = transform.rotation;
        }

        /// <summary>Called from FixedUpdate. Returns the item home if the player has lost track of
        /// it, which is the difference between a dropped item and a dead run.</summary>
        public void UpdateLeash()
        {
            if (IsHeld) return;
            if (SecondsSinceDrop < leashDelay) return;
            if (transform.position.y < killBelowY
                || Vector3.Distance(transform.position, _homePos) > leashRadius)
            {
                Debug.Log("[Item] '" + displayName + "' was lost at " + transform.position.ToString("F1")
                          + " (home " + _homePos.ToString("F1") + ") - returning it so the run stays winnable");
                ReturnHome();
            }
        }

        public void PickUp(Transform holder)
        {
            if (holder == null || IsHeld) return;
            _holder = holder;
            _throwDir = holder.forward;
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            // A slight spin while carried, so a dropped item does not land perfectly flat and
            // present its thinnest dimension to the pickup raycast.
            _rb.angularVelocity = Vector3.zero;
        }

        public void Drop()
        {
            if (!IsHeld) return;
            // Throw along where the player faces NOW, not where they faced at pickup:
            // _throwDir is only the fallback for a holder that vanished mid-hold.
            var dir = _holder != null ? _holder.forward : _throwDir;
            _holder = null;
            _rb.isKinematic = false;
            _rb.useGravity = true;
            _droppedAt = Time.time;
            // Impulse along the throw, plus a small spin, and a ceiling on the downward speed so the
            // item cannot be punted through the floor.
            _rb.AddForce(dir * throwSpeed, ForceMode.Impulse);
            _rb.AddTorque(new Vector3(0.4f, 0.9f, 0.3f) * throwSpeed, ForceMode.Impulse);
        }

        /// <summary>Silently returns the item to where it started.</summary>
        public void ReturnHome()
        {
            _holder = null;
            _rb.isKinematic = true;
            _rb.useGravity = false;
            transform.SetPositionAndRotation(_homePos, _homeRot);
            _rb.position = _homePos;
            _rb.rotation = _homeRot;
            // stay kinematic so the item does not roll away before the locker is reachable
        }

        public void ReleaseInPlace(Transform parent)
        {
            _holder = null;
            // Defensive fetch: in edit mode Awake never ran, but the component exists.
            // (Edit-mode self tests drive Interact without a play loop.)
            var rb = _rb != null ? _rb : GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }
            if (parent != null) transform.SetParent(parent, true);
        }

        void FixedUpdate()
        {
            if (IsHeld)
            {
                Vector3 target = _holder.position
                                 + _holder.forward * holdDistance
                                 + Vector3.up * holdHeight;
                float t = 1f - Mathf.Exp(-followSpeed * Time.fixedDeltaTime);
                _rb.MovePosition(Vector3.Lerp(_rb.position, target, t));
                _rb.MoveRotation(Quaternion.Slerp(_rb.rotation, _holder.rotation, t));
                return;
            }
            UpdateLeash();
        }
    }
}
