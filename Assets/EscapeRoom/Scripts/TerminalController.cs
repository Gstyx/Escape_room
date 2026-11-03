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

        // D-225: the status line is polled (it tracks the puzzles), and the purification notice
        // from EscapeGameManager.Lose() is written into that same line for four whole seconds.
        // Without this flag the poll would stamp over the notice ~8 times in that window and the
        // two-beat defeat the player asked for would collapse back into one - the notice would
        // appear and vanish so fast it read as a flicker. EscapeGameManager therefore writes the
        // notice through HoldStatus(), which sets the flag; RefreshStatus() then refuses to touch
        // the line until ResetStatusHold() runs, which happens on reboot and on restart.
        bool _statusHeld;

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
                RefreshStatus();
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
                hint = "OBJETIVO: ORDENE OS LOGS POR HORARIO (OESTE)";
            else if (safePad != null && !safePad.solved)
                hint = "OBJETIVO: DIGITE A SENHA NO COFRE (OESTE)";
            else if (plateReader != null && !plateReader.approved)
                hint = "OBJETIVO: ENCAIXE AS 3 PLACAS NO LEITOR (LESTE)";
            else
                // "BUNKER" was mine and it was wrong (D-221): this room is a bank branch inside a crypto
                // research module - Filial 9, BANCO ARTICO - not a bunker, and the fiction never had
                // one. "SAIDA" is also 9 chars shorter than "PORTA DO BUNKER", so the objective line
                // cannot wrap where it used to; this string is written at RUNTIME, which means
                // `Check UI text overflow` never sees it and the length has to be reasoned about.
                hint = "OBJETIVO: FUJA PELA SAIDA (NORTE)";
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
                    // D-225: the status line stopped being a prompt. It used to read
                    // "SISTEMAS NO AR - SIGA O OBJETIVO", which restated the objective line one
                    // row above it - two lines, one piece of information. It now tracks the room's
                    // state instead, and RefreshStatus picks the string; the hold is cleared here
                    // so a reboot after a held status can write again.
                    ResetStatusHold();
                    RefreshStatus();
                    RefreshObjective();
                    break;
                case TerminalPhase.Dead:
                    ResetStatusHold();
                    SetStatus("SINAL PERDIDO", EsTheme.Warn);
                    break;
            }
        }

        public void SetStatus(string msg, Color color)
        {
            if (statusText != null) { statusText.text = msg; statusText.color = color; }
        }

        /// <summary>Write the status line and stop the puzzle poll from overwriting it.</summary>
        public void HoldStatus(string msg, Color color)
        {
            _statusHeld = true;
            SetStatus(msg, color);
        }

        public void ResetStatusHold() { _statusHeld = false; }

        /// <summary>The mesh came on. Repaint everything that reads the power gate.
        ///
        /// <para><b>Why this exists</b> (D-226): the log station gates itself on
        /// <c>grid.powerRestored</c> and paints its label from inside its own click handlers.
        /// Nothing told it the gate had opened, so a player who solved the mesh before rebooting
        /// the terminal arrived at a station reading <c>SEM ENERGIA - RESTAURE A MALHA HEXAGONAL
        /// </c> while the mesh was lit and the ceiling was burning. Two lines of the room
        /// contradicting each other on screen, and the wrong one was the one the player was
        /// looking at.</para>
        ///
        /// <para><b>Why it lives on the terminal</b> and not in each puzzle: the terminal already
        /// holds a reference to every station, and <c>EscapeGameManager.SetPowerRestored()</c> -
        /// the single choke point the hex reward goes through - already holds a reference to the
        /// terminal. So this is one edge, not four puzzles each remembering to notify someone.</para>
        ///
        /// <para>The status line is repainted too, and deliberately <i>after</i> the station: if the
        /// status ever needed a hold released here, the station would win.</para></summary>
        public void OnPowerRestored()
        {
            if (logPuzzle != null) logPuzzle.Refresh();
            RefreshStatus();
            RefreshObjective();
        }

        /// <summary>The status line as a **state of the room**, distinct from the objective
        /// line above it (D-225).
        ///
        /// <para>The split is the whole point. <c>RefreshObjective</c> says <i>what to do next</i>
        /// and walks the three puzzles; this line says <i>what the room has become</i>. Two lines
        /// carrying the same information would be one line, and the objective is already spoken
        /// for - so the status is free to describe progress instead of restating the task.</para>
        ///
        /// <para><b>It follows the same order as the objective</b> (power, logs, safe, reader),
        /// which is what makes it read as consequence rather than decoration: the room changes
        /// state exactly when the player changes it.</para>
        ///
        /// <para>Every string is written at RUNTIME, so <c>Check UI text overflow</c> cannot see
        /// them - they were measured against the real rect instead (see D-225 in DECISOES.md).</para></summary>
        public void RefreshStatus()
        {
            if (_statusHeld || statusText == null) return;

            string msg;
            Color color;
            if (hex != null && !hex.powerRestored)
            {
                if (hex.ReceptorEnergized())
                {
                    // D-228: the near-miss gets its own state. The player fed the receptor but a
                    // leak is standing, so the strict rule withholds the reward - and before this
                    // branch the terminal kept saying ISOLAMENTO SELADO, the same sentence as when
                    // nothing is done. Measured in the player's shoes: receptor lit, terminal
                    // unchanged, lights unchanged - which reads as "nothing happens". This mirrors
                    // the hex panel's own amber label on the big screen the player looks at next.
                    // It names the STATE, never the location: saying where the leak is would be
                    // solving the puzzle, saying that there is one is reporting the room.
                    msg = "RECEPTOR ALIMENTADO - VAZAMENTO NA MALHA";
                    color = EsTheme.Amber;
                }
                else
                {
                    // "ISOLAMENTO SELADO" is not scenery. It is the one fact the room can assert that
                    // the player cannot: there is no second way out, and the only way out is the one
                    // the objective line describes. Naming it here means the terminal is refusing to
                    // help and only reporting - which is what a system that is right and will not
                    // explain itself looks like (D-219). Deliberately NOT "SAIDA BLOQUEADA": that
                    // would make the containment sound like a lock and the A.E.G.I.S. like a jailer.
                    msg = "ISOLAMENTO SELADO - SAIDA SO PELA REATIVACAO";
                    color = EsTheme.Warn;
                }
            }
            else if (logPuzzle != null && !logPuzzle.solved)
            {
                msg = "REDE ALIMENTADA - LOGS PENDENTES";
                color = EsTheme.ScreenOn;
            }
            else if (safePad != null && !safePad.solved)
            {
                msg = "LOGS VERIFICADOS - COFRE TRAVADO";
                color = EsTheme.ScreenOn;
            }
            else if (plateReader != null && !plateReader.approved)
            {
                msg = "COFRE ABERTO - REGISTRO DE 2142";
                color = EsTheme.ScreenOn;
            }
            else
            {
                // Everything the room knows how to say, it has said. The line stops claiming
                // anything, which is what "the door is open" should look like on a display.
                msg = "PLACA AUTORIZADA - SAIDA LIBERADA";
                color = EsTheme.Amber;
            }
            SetStatus(msg, color);
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
