using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeRoom
{
    public enum TerminalPhase
    {
        Offline,        // dark. only the reboot button is alive
        Booting,        // brief power-up
        Online,         // timer running; the hint line walks the three puzzles
        Dead            // reserve exhausted
    }

    /// <summary>The room's main terminal: reboot starts the 60:00 life-support
    /// countdown, the screen counts it down, and the hint line always names
    /// the current objective across the three puzzles (hex -> logs/safe ->
    /// reader). It owns no puzzle state of its own.</summary>
    public class TerminalController : MonoBehaviour
    {
        [Header("Refs do set")]
        public Light screenLight;
        public Renderer screenPanel;

        [Header("UI (World Space)")]
        public Text titleText;
        public Text statusText;
        public Text timerText;
        public Button rebootButton;
        public Text hintText;
        public GameObject endPanel;
        public Text endTitle;
        public Text endBody;
        public Button restartButton;

        [Header("Audio")]
        public AudioClip clickClip;
        public AudioClip powerUpClip;
        public AudioClip alarmClip;

        [Header("Enigmas (guia de objetivos)")]
        public EsHexGrid hex;
        public EsLogSortPuzzle logPuzzle;
        public EsSafeKeypad safePad;
        public EsPlateReader plateReader;

        public TerminalPhase Phase { get; private set; }

        Coroutine _routine;
        float _objectiveTimer;

        void Start()
        {
            Phase = TerminalPhase.Offline;
            HookButtons();
            SetPhaseVisuals();
        }

        // ---------------------------------------------------------------- UI wiring
        // Bound at runtime (see DECISOES.md D-29): a runtime AddListener is a real OnClick
        // and avoids depending on UnityEventTools persistent-listener serialization.
        void HookButtons()
        {
            if (rebootButton != null) rebootButton.onClick.AddListener(ForceReboot);
            if (restartButton != null)
                restartButton.onClick.AddListener(() => { if (clickClip != null) EsAudio.Play(clickClip, transform.position); Restart(); });
        }

        void Update()
        {
            if (Phase != TerminalPhase.Online) return;
            _objectiveTimer -= Time.deltaTime;
            if (_objectiveTimer <= 0f)
            {
                _objectiveTimer = 0.5f;
                RefreshObjective();
            }
        }

        /// <summary>One line that always answers "what now". Order is the run
        /// order: power first (it gates the logs), then logs, then the safe,
        /// then the reader. Missing refs read as done, never as blocked.</summary>
        public void RefreshObjective()
        {
            if (hintText == null) return;
            string hint;
            if (hex != null && !hex.powerRestored)
                hint = "OBJETIVO: REATIVE A MALHA HEXAGONAL (NORTE)";
            else if (logPuzzle != null && !logPuzzle.solved)
                hint = "OBJETIVO: ORDENE OS LOGS DO MENOR PARA O MAIOR (OESTE)";
            else if (safePad != null && !safePad.solved)
                hint = "OBJETIVO: LEIA O ULTIMO DIGITO E ABRA O COFRE (OESTE)";
            else if (plateReader != null && !plateReader.approved)
                hint = "OBJETIVO: REVELE O DIGITO NO LEITOR OPTICO (LESTE)";
            else
                hint = "OBJETIVO: FUJA PELA PORTA DO BUNKER (NORTE)";
            hintText.text = hint;
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
            SetPhase(TerminalPhase.Online);
            var gm = EscapeGameManager.Instance;
            if (gm != null) gm.StartReserve();
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
            if (titleText != null) titleText.text = "NO-ZERO // TERMINAL A.E.G.I.S.";

            switch (Phase)
            {
                case TerminalPhase.Offline:
                    SetStatus("QUARENTENA ABSOLUTA - SISTEMAS OFFLINE", EsTheme.Warn);
                    SetTimer("");
                    break;
                case TerminalPhase.Booting:
                    SetStatus("REINICIANDO...", EsTheme.Amber);
                    break;
                case TerminalPhase.Online:
                    SetStatus("SISTEMAS NO AR - SIGA O OBJETIVO", EsTheme.ScreenOn);
                    RefreshObjective();
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

    /// <summary>Tiny rotation helper used by the safe door.</summary>
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
