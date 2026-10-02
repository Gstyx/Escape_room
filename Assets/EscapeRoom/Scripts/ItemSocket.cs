using System;
using UnityEngine;

namespace EscapeRoom
{
    /// <summary>A physical slot that accepts exactly one kind of item.
    ///
    /// Interact() returns whether the item was consumed. Returning false leaves the item in
    /// the player's hand, which is what makes a wrong item a real (but non-punishing) dead end.
    /// </summary>
    public class ItemSocket : MonoBehaviour, IInteractable
    {
        [Tooltip("Item key this slot accepts, e.g. 'chave' or 'cartao'.")]
        public string acceptsKey = "";
        public string acceptsLabel = "";
        public Transform seat;            // where an accepted item is seated
        public Transform stowParent;      // where the item is parented once accepted
        public Renderer indicator;
        public Color idleColor = new Color32(0x3A, 0x42, 0x4C, 0xFF);
        public Color okColor   = new Color32(0x3A, 0xD8, 0x7A, 0xFF);
        public Color badColor  = new Color32(0xE8, 0x3A, 0x3A, 0xFF);

        public AudioClip acceptClip;
        public AudioClip rejectClip;

        public bool IsFilled { get; private set; }
        public GrabbableItem Item { get; private set; }

        /// <summary>Why the last Interact refused, in screen-facing words. Empty after a success.
        /// A buzz alone reads as "broken" - the terminal shows this on its status line, so a
        /// refused insert teaches instead of confusing (which slot, whose item, locked or not).
        /// ASCII-only by file convention.</summary>
        public string lastRejectReason { get; private set; } = "";

        /// <summary>Optional veto, evaluated BEFORE the item is consumed. Returning false
        /// refuses the interaction and leaves the item in the player's hand. This is how the
        /// terminal enforces the key-before-cell order rule: checking after the fact would
        /// already have swallowed the item.</summary>
        public System.Func<GrabbableItem, bool> guard;

        /// <summary>Optional reason for a guard refusal, shown to the player. When the guard
        /// fails and this returns non-empty, it becomes lastRejectReason instead of the
        /// generic "awaiting release" line - e.g. a depleted cell names the charger.</summary>
        public System.Func<GrabbableItem, string> guardReason;

        /// <summary>(socket, acceptedItem)</summary>
        public event Action<ItemSocket, GrabbableItem> onFilled;
        public event Action<ItemSocket> onRejected;

        void Awake()
        {
            if (indicator != null)
                indicator.material.SetColor(
                    indicator.material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color", idleColor);
        }

        public string PromptFor(GrabbableItem held)
        {
            if (IsFilled) return "";
            string what = string.IsNullOrEmpty(acceptsLabel) ? acceptsKey : acceptsLabel;
            if (held == null) return "Requer: " + what;
            if (held.ItemKey != acceptsKey) return "Incompativel com " + what;
            if (guard != null && !guard(held)) return "Bloqueado: " + what;
            return "Clique para inserir " + what;
        }

        /// <summary>Same as <see cref="PromptFor"/> but never reveals whether the guard passes.
        ///
        /// The guard is the ORDER puzzle (D-10): the cell cannot boot the key reader, so the key
        /// must go in first. The player is meant to work that out by trying, from the distinct
        /// refusal sound and the terminal's own message. Announcing "Bloqueado: CELULA DE ENERGIA"
        /// before the click would hand the second deduction over for free, so the aim prompt shows
        /// only what is harmless to know - which item this slot wants, and whether what you are
        /// holding is even the right kind of thing. Everything the player already knows for free.
        /// </summary>
        public string PromptForAim(GrabbableItem held)
        {
            if (IsFilled) return "";
            string what = string.IsNullOrEmpty(acceptsLabel) ? acceptsKey : acceptsLabel;
            if (held == null) return "Requer: " + what;
            if (held.ItemKey != acceptsKey) return "Incompativel com " + what;
            return "Clique para inserir " + what;
        }

        public bool Interact(GrabbableItem held)
        {
            if (IsFilled) return false;
            string what = string.IsNullOrEmpty(acceptsLabel) ? acceptsKey : acceptsLabel;
            if (held == null)
            {
                lastRejectReason = "ESTE SUPORTE RECEBE: " + what;
                Buzz(false);
                return false;
            }

            if (held.ItemKey != acceptsKey)
            {
                lastRejectReason = "SLOT ERRADO - SUPORTE DE " + what
                                 + ", VOCE SEGURA " + held.displayName.ToUpperInvariant();
                Buzz(false);
                if (onRejected != null) onRejected(this);
                return false;                    // item stays in hand
            }

            // order / phase rule, evaluated before anything is consumed
            if (guard != null && !guard(held))
            {
                lastRejectReason = guardReason != null ? (guardReason(held) ?? "") : "";
                if (string.IsNullOrEmpty(lastRejectReason))
                    lastRejectReason = "BLOQUEADO - " + what + " AGUARDA LIBERACAO";
                Buzz(false);
                if (onRejected != null) onRejected(this);
                return false;                    // item stays in hand
            }

            IsFilled = true;
            Item = held;
            lastRejectReason = "";
            held.ReleaseInPlace(stowParent != null ? stowParent : transform);
            if (seat != null)
                held.transform.SetPositionAndRotation(seat.position, seat.rotation);
            Tint(okColor);
            Buzz(true);
            if (onFilled != null) onFilled(this, held);
            return true;
        }

        public void Reset()
        {
            IsFilled = false;
            Item = null;
            lastRejectReason = "";
            Tint(idleColor);
        }

        void Tint(Color c)
        {
            if (indicator == null) return;
            var m = indicator.material;
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * 2.2f);
            }
        }

        void Buzz(bool good)
        {
            var clip = good ? acceptClip : rejectClip;
            if (clip != null) EsAudio.Play(clip, transform.position);
            if (!good) Tint(badColor);
        }
    }
}
