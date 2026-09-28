using System.Collections;
using UnityEngine;

namespace EscapeRoom
{
    /// <summary>The sealed exit. Accepts the release card, then plays the heavy servo open.</summary>
    public class BlastDoor : MonoBehaviour, IInteractable
    {
        public ItemSocket cardSocket;
        public Transform doorLeaf;          // rotates open around its own pivot
        public float openAngle = -105f;
        public float openDuration = 2.6f;
        public AudioClip servoClip;
        public AudioClip lockedClip;
        public Renderer statusLamp;
        public Color lockedColor = new Color32(0xE8, 0x3A, 0x3A, 0xFF);
        public Color openColor   = new Color32(0x3A, 0xD8, 0x7A, 0xFF);

        bool _open;
        Coroutine _anim;

        public bool IsOpen => _open;

        void Start()
        {
            if (cardSocket != null) cardSocket.onFilled += OnCardInserted;
            Tint(lockedColor);
        }

        void OnDestroy()
        {
            if (cardSocket != null) cardSocket.onFilled -= OnCardInserted;
        }

        public string PromptFor(GrabbableItem held)
        {
            if (_open) return "";
            if (cardSocket != null && cardSocket.IsFilled) return "";
            return "Painel exige o Cartao de Liberacao";
        }

        public bool Interact(GrabbableItem held)
        {
            if (_open) return false;
            if (cardSocket != null) return cardSocket.Interact(held);
            if (lockedClip != null) EsAudio.Play(lockedClip, transform.position);
            return false;
        }

        void OnCardInserted(ItemSocket socket, GrabbableItem item)
        {
            if (_open) return;
            _open = true;
            if (servoClip != null) EsAudio.Play(servoClip, transform.position, 1f);
            Tint(openColor);
            if (_anim != null) StopCoroutine(_anim);
            _anim = StartCoroutine(Swing());
            var gm = EscapeGameManager.Instance;
            if (gm != null) gm.Win();
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
    }
}
