using System.Collections;
using UnityEngine;

namespace EscapeRoom
{
    /// <summary>The sealed exit. Opens only on the exit card: the reader
    /// dispenses it on approval and its socket sits where the old card reader
    /// was sealed over (D-230). Walking through the open door wins the game at
    /// the exit trigger - the door itself never shows the end screen.</summary>
    public class BlastDoor : MonoBehaviour, IInteractable
    {
        public Transform doorLeaf;          // rotates open around its own pivot
        public float openAngle = -105f;
        public float openDuration = 2.6f;
        public AudioClip servoClip;
        public AudioClip lockedClip;
        public Renderer statusLamp;
        public Color lockedColor = new Color32(0xE8, 0x3A, 0x3A, 0xFF);
        public Color openColor   = new Color32(0x3A, 0xD8, 0x7A, 0xFF);

        /// <summary>The card socket on the seal plate (D-230). Serialized reference, set by
        /// the builder. The subscription is NOT serialized - C# event wiring never survives
        /// a scene save - so WireCardSocket() runs it at Start, and tests call it explicitly,
        /// which is the same Wire() convention EsPlateReader uses for its plate sockets.</summary>
        public ItemSocket cardSocket;

        /// <summary>The solid plate sealing the opening behind the leaf (D-230). Deactivated
        /// on ForceOpen: measured live, its BoxCollider fills the doorway prism, so opening
        /// the leaf without retracting it would swing a door onto a wall. Serialized
        /// reference, set by the builder.</summary>
        public GameObject bulkhead;

        bool _open;
        Coroutine _anim;

        public bool IsOpen => _open;

        void Start()
        {
            Tint(lockedColor);
            WireCardSocket();
        }

        /// <summary>Idempotent wiring: safe to call twice, unsubscribes first like Wire().</summary>
        public void WireCardSocket()
        {
            if (cardSocket == null) return;
            cardSocket.onFilled -= OnCardInserted;
            cardSocket.onFilled += OnCardInserted;
        }

        void OnDestroy()
        {
            if (cardSocket == null) return;
            cardSocket.onFilled -= OnCardInserted;
        }

        void OnCardInserted(ItemSocket s, GrabbableItem item)
        {
            ForceOpen();
        }

        public string PromptFor(GrabbableItem held)
        {
            if (_open) return "";
            return "Porta selada - revele o digito no leitor optico (leste)";
        }

        public bool Interact(GrabbableItem held)
        {
            if (_open) return false;
            if (lockedClip != null) EsAudio.Play(lockedClip, transform.position);
            return false;
        }
        /// <summary>The only opener: the exit card seated in the door socket swings
        /// the leaf and retracts the bulkhead. (Was "bunker door" - wrong: the room is a bank branch, D-221.)
        ///
        /// <para>D-230: this no longer calls <c>EscapeGameManager.Win()</c>. Opening the door
        /// used to show the end screen on the spot, over the reader, and the player never
        /// walked out. Now the win fires at the exit trigger past the doorway, so the panel
        /// says "Voce saiu do No-Zero" about something that literally just happened.</para></summary>
        public void ForceOpen()
        {
            if (_open) return;
            _open = true;
            if (servoClip != null) EsAudio.Play(servoClip, transform.position, 1f);
            Tint(openColor);
            if (bulkhead != null) bulkhead.SetActive(false);
            if (Application.isPlaying)
            {
                if (_anim != null) StopCoroutine(_anim);
                _anim = StartCoroutine(Swing());
            }
        }

        IEnumerator Swing()
        {
            if (doorLeaf == null) yield break;
            Quaternion from = doorLeaf.localRotation;
            Quaternion to = from * Quaternion.Euler(0f, openAngle, 0f);
            float t = 0f;
            while (t < openDuration)
            {
                t += Time.deltaTime;
                // ease-out: heavy door that decelerates hard
                float k = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / openDuration), 2.2f);
                doorLeaf.localRotation = Quaternion.Slerp(from, to, k);
                yield return null;
            }
            doorLeaf.localRotation = to;
        }

        void Tint(Color c)
        {
            if (statusLamp == null) return;
            var m = statusLamp.material;
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * 3f);
            }
        }

        /// <summary>Test hook: relatch shut without touching materials (the lamp
        /// keeps its open tint in edit mode; gameplay tinting is untouched).</summary>
        public void DebugReset()
        {
            _open = false;
        }
    }
}
