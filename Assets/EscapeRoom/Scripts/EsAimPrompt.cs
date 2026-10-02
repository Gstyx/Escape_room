using UnityEngine;
using UnityEngine.UI;

namespace EscapeRoom
{
    /// <summary>Reads PlayerInteractor.Prompt onto the HUD aim label.
    ///
    /// This is deliberately NOT the removed EsInteractPrompt (D-92): that one labelled what the
    /// E key would press, and the E key is gone because it typed the keypad code. This label is
    /// read-only - it names what the crosshair is on ("Clique para inserir CHAVE DE ACESSO" vs
    /// "Incompativel com CELULA DE ENERGIA") and can never actuate anything, so the D-89/D-94
    /// concern does not apply. Without it the 3D sockets have zero aim feedback: the crosshair
    /// highlight only tracks canvas targets, and a refused insert is just a buzz.
    ///
    /// Same click-safety guards as the crosshair: overlay canvas without a GraphicRaycaster,
    /// raycastTarget off, CanvasGroup.blocksRaycasts off. Hidden when the cursor is unlocked,
    /// when the real cursor is the aim again.</summary>
    public class EsAimPrompt : MonoBehaviour
    {
        Text _label;
        PlayerInteractor _inter;

        void Awake()
        {
            _label = GetComponentInChildren<Text>(true);
            if (_label != null)
            {
                _label.raycastTarget = false;
                _label.enabled = false;
            }
        }

        void Update()
        {
            if (_label == null) return;
            if (_inter == null) _inter = Object.FindFirstObjectByType<PlayerInteractor>();
            string p = _inter != null ? _inter.Prompt : "";
            bool show = Cursor.lockState == CursorLockMode.Locked && !string.IsNullOrEmpty(p);
            _label.enabled = show;
            if (show && _label.text != p) _label.text = p;
        }
    }
}
