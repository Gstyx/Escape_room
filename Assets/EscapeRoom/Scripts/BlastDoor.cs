using System.Collections;
using UnityEngine;

namespace EscapeRoom
{
    /// <summary>The sealed exit. Opens only on the optical key: the reader
    /// approves the acrylic plates and force-opens the leaf.</summary>
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

        bool _open;
        Coroutine _anim;

        public bool IsOpen => _open;

        void Start()
        {
            Tint(lockedColor);
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
        /// <summary>The only opener: the optical reader approves the acrylic
        /// key and the bunker door swings open.</summary>
        public void ForceOpen()
        {
            if (_open) return;
            _open = true;
            if (servoClip != null) EsAudio.Play(servoClip, transform.position, 1f);
            Tint(openColor);
            if (Application.isPlaying)
            {
                if (_anim != null) StopCoroutine(_anim);
                _anim = StartCoroutine(Swing());
            }
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

        /// <summary>Test hook: relatch shut without touching materials (the lamp
        /// keeps its open tint in edit mode; gameplay tinting is untouched).</summary>
        public void DebugReset()
        {
            _open = false;
        }
    }
}
