using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeRoom
{
    public enum TerminalPhase
    {
        Offline,        // dark. only the reboot button is alive
        Booting,        // brief power-up
        AwaitingCode,   // keypad up, waiting for the shift credential
        Granted,        // credential accepted: locker opens, key slot live
        AwaitingCell,   // key seated, waiting for the energy cell
        Authorized,     // both seated: card dispensed
        Dead            // reserve exhausted
    }

    /// <summary>The heart of the puzzle: reboot -> deduce the shift code -> unlock the
    /// locker -> seat key then cell (order matters) -> card is dispensed.</summary>
    public class TerminalController : MonoBehaviour
    {
        [Header("Regra da cena")]
        public string correctCode = "86";
        public string codeLength = "2";

        [Header("Refs do set")]
        public ItemSocket keySocket;
        public ItemSocket cellSocket;
        public Transform lockerDoor;
        public Transform cardAnchor;
        public GrabbableItem releaseCard;
        public Light screenLight;
        public Renderer screenPanel;

        [Header("UI (World Space)")]
        public Text titleText;
        public Text statusText;
        public Text timerText;
        public Button rebootButton;
        public GameObject keypadRoot;
        public Text keypadEcho;
        public Text hintText;
        public GameObject endPanel;
        public Text endTitle;
        public Text endBody;
        public Button restartButton;

        [Header("Audio")]
        public AudioClip clickClip;
        public AudioClip powerUpClip;
        public AudioClip errorClip;
        public AudioClip chimeClip;
        public AudioClip dispenseClip;
        public AudioClip alarmClip;

        public TerminalPhase Phase { get; private set; }

        readonly List<Button> _digitButtons = new List<Button>();
        readonly List<string> _entry = new List<string>();
        Coroutine _routine;

        void Start()
        {
            Phase = TerminalPhase.Offline;
            HookButtons();
            SetPhaseVisuals();
            if (keySocket != null)
            {
                keySocket.onFilled += OnKeySeated;
                keySocket.onRejected += OnSocketRejected;
                // the key slot only opens once the credential was accepted
                keySocket.guard = _ => Phase == TerminalPhase.Granted;
            }
            if (cellSocket != null)
            {
                cellSocket.onFilled += OnCellSeated;
                cellSocket.onRejected += OnSocketRejected;
                // the cell is dead weight until the key reader is powered: key goes first
                cellSocket.guard = _ => Phase == TerminalPhase.AwaitingCell;
            }
            if (releaseCard != null) releaseCard.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (keySocket != null) { keySocket.onFilled -= OnKeySeated; keySocket.onRejected -= OnSocketRejected; }
            if (cellSocket != null) { cellSocket.onFilled -= OnCellSeated; cellSocket.onRejected -= OnSocketRejected; }
        }

        // ---------------------------------------------------------------- UI wiring
        // Bound at runtime (see DECISOES.md D-29): a runtime AddListener is a real OnClick
        // and avoids depending on UnityEventTools persistent-listener serialization.
        void HookButtons()
        {
            if (rebootButton != null) rebootButton.onClick.AddListener(ForceReboot);
            if (restartButton != null)
                restartButton.onClick.AddListener(() => { if (clickClip != null) EsAudio.Play(clickClip, transform.position); Restart(); });

            if (keypadRoot == null) return;
            var digits = new List<Button>();
            var labels = new List<string>();
            for (int i = 0; i < keypadRoot.transform.childCount; i++)
            {
                var child = keypadRoot.transform.GetChild(i);
                var b = child.GetComponent<Button>();
                if (b == null) continue;
                var lbl = child.GetComponentInChildren<Text>();
                string cap = lbl != null ? lbl.text : "?";
                digits.Add(b);
                labels.Add(cap);
            }
            for (int i = 0; i < digits.Count; i++)
            {
                var btn = digits[i];
                string cap = labels[i];
                btn.onClick.AddListener(() => OnKeypadPress(cap));
            }
        }

        // A keypad press can legitimately arrive twice from one physical click: the
        // InputSystemUIInputModule dispatches at the frozen OS cursor position, and
        // EsCrosshairPointer dispatches at the crosshair. They normally disagree, so only one
        // finds a handler, but "normally" is not a guarantee - and a doubled digit is a visible
        // bug that would corrupt the code the player is typing.
        //
        // Keyed on the CAP as well as the frame, not on the frame alone. Both dispatch paths resolve
        // the same single mouse click, so they can only ever duplicate the SAME key - two different
        // keys arriving in one frame is not something a mouse can do, and blocking it would only
        // make the behaviour impossible to exercise from a self test. Deduping per (frame, key)
        // is therefore exactly as strong against the real duplicate, and it stops being a special
        // case that tests have to work around.
        int _lastKeyFrame = -1;
        string _lastKey = null;

        void OnKeypadPress(string cap)
        {
            if (_lastKeyFrame == Time.frameCount && _lastKey == cap) return;
            _lastKeyFrame = Time.frameCount;
            _lastKey = cap;

            if (clickClip != null) EsAudio.Play(clickClip, transform.position);
            // The key is labelled ENTER on the panel; "C" was the old caption and nobody read it
            // as "submit", which left the player with no way to confirm a code they had typed.
            if (cap == "ENTER") { SubmitCode(); return; }
            if (cap == "DEL")
            {
                if (_entry.Count > 0) _entry.RemoveAt(_entry.Count - 1);
                RefreshEcho();
                return;
            }
            if (_entry.Count >= correctCode.Length)
            {
                if (errorClip != null) EsAudio.Play(errorClip, transform.position);
                return;
            }
            _entry.Add(cap);
            RefreshEcho();
        }

        void RefreshEcho()
        {
            if (keypadEcho == null) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _entry.Count; i++) sb.Append(_entry[i]);
            for (int i = _entry.Count; i < correctCode.Length; i++) sb.Append('_');
            keypadEcho.text = sb.ToString();
        }

        void SubmitCode()
        {
            string guess = string.Concat(_entry.ToArray());
            if (guess == correctCode)
            {
                if (chimeClip != null) EsAudio.Play(chimeClip, transform.position);
                SetPhase(TerminalPhase.Granted);
                OpenLocker();
            }
            else
            {
                if (errorClip != null) EsAudio.Play(errorClip, transform.position);
                if (keypadEcho != null) keypadEcho.text = "RECUSADA";
                _entry.Clear();
            }
        }

        // ---------------------------------------------------------------- the button
        /// <summary>OnClick of the World Space button. Also starts the reserve countdown.</summary>
        public void ForceReboot()
        {
            if (Phase != TerminalPhase.Offline) return;
            if (clickClip != null) EsAudio.Play(clickClip, transform.position);
            if (powerUpClip != null) EsAudio.Play(powerUpClip, transform.position);
            if (_routine != null) StopCoroutine(_routine);
            _routine = StartCoroutine(BootRoutine());
        }

        IEnumerator BootRoutine()
        {
            SetPhase(TerminalPhase.Booting);
            if (screenLight != null) screenLight.intensity = 0.30f;
            float t = 0f;
            while (t < 1.4f)
            {
                t += Time.deltaTime;
                if (screenLight != null)
                    screenLight.intensity = Mathf.Lerp(0.30f, 1.35f, t / 1.4f);
                yield return null;
            }
            _entry.Clear();
            RefreshEcho();
            SetPhase(TerminalPhase.AwaitingCode);
            var gm = EscapeGameManager.Instance;
            if (gm != null) gm.StartReserve();
        }

        void OpenLocker()
        {
            if (lockerDoor != null && lockerDoor.gameObject.activeSelf)
            {
                var anim = lockerDoor.GetComponent<EsSimpleSwing>();
                if (anim == null) anim = lockerDoor.gameObject.AddComponent<EsSimpleSwing>();
                // Play(duration, delay, angle) - duration first! Passing the angle as the
                // duration made the door take 115 seconds to open.
                anim.Play(1.1f, 0f, -95f);
            }
        }

        // ---------------------------------------------------------------- sockets
        void OnKeySeated(ItemSocket s, GrabbableItem item)
        {
            SetPhase(TerminalPhase.AwaitingCell);
        }

        void OnCellSeated(ItemSocket s, GrabbableItem item)
        {
            // The cell cannot boot the key reader on its own: key must be seated first.
            if (Phase != TerminalPhase.AwaitingCell)
            {
                if (errorClip != null) EsAudio.Play(errorClip, transform.position);
                cellSocket.Reset();
                if (item != null) item.ReturnHome();
                if (statusText != null) statusText.text = "ORDEM ERRADA - O LEITOR DE CHAVE PRECISA DE ENERGIA";
                return;
            }
            Authorize();
        }

        void OnSocketRejected(ItemSocket s)
        {
            if (errorClip != null && s != null && s.rejectClip == null) EsAudio.Play(errorClip, transform.position);
        }

        public void Authorize()
        {
            SetPhase(TerminalPhase.Authorized);
            if (chimeClip != null) EsAudio.Play(chimeClip, transform.position);
            if (dispenseClip != null) EsAudio.Play(dispenseClip, transform.position);
            if (releaseCard != null)
            {
                releaseCard.gameObject.SetActive(true);
                if (cardAnchor != null)
                {
                    releaseCard.transform.SetParent(cardAnchor, true);
                    releaseCard.transform.localPosition = Vector3.zero;
                    releaseCard.transform.localRotation = Quaternion.identity;
                }
            }
            var gm = EscapeGameManager.Instance;
            if (gm != null) gm.NoteMilestone("CREDENCIAL REAUTORIZADA");
        }

        // ---------------------------------------------------------------- phases
        public void SetPhase(TerminalPhase p)
        {
            Phase = p;
            SetPhaseVisuals();
        }

        void SetPhaseVisuals()
        {
            bool powered = Phase != TerminalPhase.Offline;
            if (rebootButton != null) rebootButton.gameObject.SetActive(Phase == TerminalPhase.Offline);
            if (keypadRoot != null) keypadRoot.SetActive(Phase == TerminalPhase.AwaitingCode);
            if (screenLight != null && !powered) screenLight.intensity = 0f;
            if (screenPanel != null)
            {
                var m = screenPanel.material;
                if (m != null && m.HasProperty("_EmissionColor"))
                {
                    if (powered) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", EsTheme.ScreenOn * 2.2f); }
                    else m.SetColor("_EmissionColor", Color.black);
                }
            }
            if (titleText != null) titleText.text = "TERMINAL 09 - AUTOATENDIMENTO";
            if (hintText != null && Phase == TerminalPhase.AwaitingCode)
                hintText.text = "CREDENCIAL DE SOBREVIVENCIA: 2 DIGITOS";

            switch (Phase)
            {
                case TerminalPhase.Offline:
                    SetStatus("SISTEMA OFFLINE", EsTheme.Warn);
                    SetTimer("");
                    break;
                case TerminalPhase.Booting:
                    SetStatus("REINICIANDO...", EsTheme.Amber);
                    break;
                case TerminalPhase.AwaitingCode:
                    SetStatus("DIGITE A CREDENCIAL DO TURNO ATIVO", EsTheme.ScreenOn);
                    RefreshEcho();
                    break;
                case TerminalPhase.Granted:
                    SetStatus("CREDENCIAL ACEITA - INSERIR A CHAVE", EsTheme.ScreenOn);
                    break;
                case TerminalPhase.AwaitingCell:
                    SetStatus("CHAVE LIDA - INSERIR A CELULA", EsTheme.ScreenOn);
                    break;
                case TerminalPhase.Authorized:
                    SetStatus("AUTORIZADO - RETIRE O CARTAO", EsTheme.ScreenOn);
                    break;
                case TerminalPhase.Dead:
                    SetStatus("SINAL PERDIDO", EsTheme.Warn);
                    break;
            }
        }

        public void SetStatus(string msg, Color color)
        {
            if (statusText != null) { statusText.text = msg; statusText.color = color; }
        }

        public void SetTimer(string s)
        {
            if (timerText != null) timerText.text = s;
        }

        public void Beep() { if (alarmClip != null) EsAudio.Play(alarmClip, transform.position); }

        public void ShowEnd(string title, string body, Color color)
        {
            if (endPanel != null) endPanel.SetActive(true);
            if (endTitle != null) { endTitle.text = title; endTitle.color = color; }
            if (endBody != null) endBody.text = body;
            if (keypadRoot != null) keypadRoot.SetActive(false);
            if (rebootButton != null) rebootButton.gameObject.SetActive(false);
            if (screenLight != null) screenLight.intensity = 0.95f;

            // Hand the mouse back. The end panel is a Screen Space OVERLAY canvas, and the input
            // module is switched off while the cursor is locked (EsCrosshairPointer), because the
            // frozen OS cursor was delivering clicks to the wrong element. That makes REINICIAR
            // unreachable if the cursor stays locked - the player is left staring at a button that
            // does nothing, on the screen that is supposed to be the payoff. Releasing here lets
            // the module come back on its next update, and the panel is mouse-usable again.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void Restart()
        {
            var gm = EscapeGameManager.Instance;
            if (gm != null) gm.Restart();
        }
    }

    /// <summary>Tiny rotation helper used by the locker door.</summary>
    public class EsSimpleSwing : MonoBehaviour
    {
        public void Play(float duration, float delay, float angle)
        {
            StopAllCoroutines();
            StartCoroutine(Run(duration, delay, angle));
        }

        System.Collections.IEnumerator Run(float duration, float delay, float angle)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            Quaternion from = transform.localRotation;
            Quaternion to = from * Quaternion.Euler(0f, angle, 0f);
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                transform.localRotation = Quaternion.Slerp(from, to, Mathf.Clamp01(t / duration));
                yield return null;
            }
            transform.localRotation = to;
        }
    }
}
