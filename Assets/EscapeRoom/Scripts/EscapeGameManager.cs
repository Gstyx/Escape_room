using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EscapeRoom
{
    /// <summary>Owns the life-support countdown (the diegetic timer), the win/lose
    /// states and the ambience bed. Fase 0 (A.E.G.I.S.): 60 minutes of life
    /// support, room starts on red emergency lighting; the countdown still
    /// starts only when the player reboots the terminal, so exploring free.</summary>
    public class EscapeGameManager : MonoBehaviour
    {
        public static EscapeGameManager Instance { get; private set; }

        [Header("Suporte vital (A.E.G.I.S.)")]
        public float reserveSeconds = 3600f;
        public float warnAtSeconds = 600f;
        public float beepEverySeconds = 2f;

        [Header("Refs")]
        public TerminalController terminal;
        public BlastDoor exitDoor;
        public Light roomLight;
        public Light alarmLight;
        public Renderer alarmStrip;
        public AudioClip ambientClip;
        public AudioClip winClip;
        public AudioClip failClip;

        [Header("Ambiente")]
        public float alarmPulseSpeed = 2.2f;
        public float musicVolume = 0.28f;

        [Header("Purificacao (derrota em dois tempos)")]
        // D-222: the defeat used to be one event - the panel simply appeared. The player's request is
        // for the fiction to be SAYED before it is shown: the terminal display announces that the
        // purification protocol has started, and only then does the end panel take the screen.
        public float purificationNoticeSeconds = 4f;

        float _remaining;
        bool _running;
        bool _finished;
        bool _purifying;
        float _beepTimer;
        float _pulse;
        int _milestones;

        public float Remaining => _remaining;
        public bool IsRunning => _running;

        void Awake()
        {
            EsTheme.ApplyAmbientFill();   // D-235
            Instance = this;
            _remaining = reserveSeconds;
            if (ambientClip != null) EsAudio.Music(ambientClip, musicVolume);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (_running && !_finished) TickReserve();
            if (alarmLight != null && _finished == false) PulseAlarm();
        }

        void TickReserve()
        {
            _remaining -= Time.deltaTime;
            _beepTimer -= Time.deltaTime;

            if (_remaining <= warnAtSeconds && _remaining > 0f && _beepTimer <= 0f)
            {
                _beepTimer = beepEverySeconds;
                if (terminal != null) terminal.Beep();
            }

            if (terminal != null)
            {
                // Total minutes, not TimeSpan.Minutes: 3600 s must read "60:00",
                // not "00:00" (TimeSpan would fold the hour away).
                int total = Mathf.Max(0, Mathf.CeilToInt(_remaining));
                terminal.SetTimer(string.Format("{0:00}:{1:00}", total / 60, total % 60));
            }

            if (_remaining <= 0f)
            {
                _remaining = 0f;
                _running = false;
                Lose();
            }
        }

        void PulseAlarm()
        {
            bool critical = _running && _remaining <= warnAtSeconds;
            _pulse += Time.deltaTime * alarmPulseSpeed;
            float k = critical ? (Mathf.Sin(_pulse) * 0.5f + 0.5f) : 0f;
            if (alarmLight != null) alarmLight.intensity = critical ? Mathf.Lerp(0.2f, 2.4f, k) : 0f;
            if (alarmStrip != null)
            {
                var m = alarmStrip.material;
                if (m != null && m.HasProperty("_EmissionColor"))
                {
                    var c = Color.Lerp(Color.black, new Color(0.85f, 0.12f, 0.12f), k);
                    m.SetColor("_EmissionColor", c * 2.5f);
                }
            }
        }

        public void StartReserve()
        {
            if (_running || _finished) return;
            _remaining = reserveSeconds;
            _running = true;
            NoteMilestone("SUPORTE VITAL EM CONTAGEM");
        }

        public void NoteMilestone(string label)
        {
            _milestones++;
        }

        public void Win()
        {
            if (_finished) return;
            _finished = true;
            _running = false;
            if (winClip != null) EsAudio.Play(winClip, Vector3.zero, 1f, 0f);
            if (alarmLight != null) alarmLight.intensity = 0f;
            int bonus = Mathf.CeilToInt(_remaining);
            if (terminal != null)
                // D-220: the reward is ONLY getting out - the player's decision. No treasure line, no
                // closing flourish, no villain satisfied or punished. And "O A.E.G.I.S. liberou a porta"
                // was factually wrong under the current fiction: the protocol did not decide to let
                // him out, the manual reactor override did - which it leaves live on purpose, because
                // isolating a zone is not the same as trapping the staff inside it. The title stays
                // (closing the quarantine IS being able to leave, D-218); the body is one clause and
                // a clock.
                terminal.ShowEnd("QUARENTENA ENCERRADA",
                    "Voce saiu do No-Zero com " + string.Format("{0:00}:{1:00}", bonus / 60, bonus % 60)
                    + " de suporte vital restante.",
                    new Color(0.24f, 0.85f, 0.64f));
        }

        public void Lose()
        {
            if (_finished || _purifying) return;
            // _finished goes up NOW, not at the end: the panel is delayed by a coroutine, and during
            // that window Win() would still be reachable and could fire a victory on top of a defeat.
            // _purifying separately guards a second Lose() arriving from the next Update tick.
            _finished = true;
            _purifying = true;
            if (failClip != null) EsAudio.Play(failClip, Vector3.zero, 1f, 0f);
            if (alarmLight != null) alarmLight.intensity = 0f;
            StartCoroutine(PurifyThenEnd());
        }

        /// <summary>The defeat in two beats: the display says it, then the screen shows it.
        ///
        /// <para><b>The cursor stays locked through the notice</b>, on purpose. ShowEnd is what releases
        /// it, and releasing it early would tell the player the game is over a beat before the fiction
        /// does. For these seconds they are still locked inside a sealed branch, which is the point.</para>
        ///
        /// <para><b>WaitForSecondsRealtime, not WaitForSeconds.</b> The pause is dramatic timing, not
        /// game time: if anything ever left <c>timeScale</c> at zero the defeat would never land and the
        /// player would sit behind a locked cursor forever - the one failure mode worth insuring
        /// against here.</para>
        ///
        /// <para>Both strings are written at RUNTIME, so <c>Check UI text overflow</c> cannot see them -
        /// they were sized against the measured rects instead (see the note at the ShowEnd call).</para>
        /// </summary>
        IEnumerator PurifyThenEnd()
        {
            if (terminal != null)
                // D-225: HoldStatus, NOT SetStatus. The terminal polls its status line twice a
                // second to track the puzzles, and the notice owns that same line for the next
                // four seconds. With a plain SetStatus the poll would overwrite it about eight
                // times and the two-beat defeat would read as a flicker - the first beat would
                // exist for a fraction of a second and the player would never get to read it.
                terminal.HoldStatus("O PROTOCOLO DE PURIFICACAO FOI INICIADO",
                                    new Color(0.91f, 0.23f, 0.23f));
            yield return new WaitForSecondsRealtime(purificationNoticeSeconds);

            if (terminal != null)
                // D-221/D-222: the defeat says what the whole fiction rests on - the protocol
                // succeeded, and he was inside what got cleaned. The player's wording won over mine
                // twice: my "Voce respirou o que nao dava para ver" was cut, and "ficou limpa" became
                // "foi purificada", which is more exact - "limpa" is a judgement about the room,
                // "purificada" is what the A.E.G.I.S. does to it.
                //
                // Sized, not verified: ShowEnd writes at RUNTIME and the overflow check reads the
                // canvas default. EndBody is 320 px tall (~5 lines at 52 pt) and 1920-280 px wide
                // (~63 chars); this body is 56 chars on one line and the title is 19 against the 23
                // it replaces, so neither can wrap where the text they replaced did not.
                terminal.ShowEnd("PURIFICACAO INICIADA",
                    "O A.E.G.I.S. cumpriu o protocolo. A filial foi purificada.",
                    new Color(0.91f, 0.23f, 0.23f));
        }

        // ---------------------------------------------------------------- A.E.G.I.S. light states
        // Fase 0: the room starts on red emergency lighting. Fase 1 (energy mesh)
        // calls SetPowerRestored() as the puzzle reward (red -> blue).
        [Header("A.E.G.I.S. - estados de luz")]
        // The room's ACTUAL ceiling lighting, off until the hex mesh restores power (D-217).
        //
        // The player's read was exactly right: several lamps burned from the moment play started,
        // which made "energy restored by the puzzle" look like it was already on. SetPowerRestored()
        // only ever recoloured `roomLight`, and never touched these - so the reward for solving the
        // hex was a tint change on one red lamp while the ceiling burned the whole time. The fix is
        // not a text change, it is making the reward real: the room starts dark and solving the
        // mesh LIGHTS IT.
        public Light[] ceilingLamps;
        public float ceilingLampIntensity = 2.50f;   // D-235: 0.16 -> 2.50

        public Color emergencyColor = new Color(1f, 0.16f, 0.14f);
        // 0,85 -> 0,55. These two fields are the REAL intensity of the room light, not BuildLights:
        // SetEmergencyLighting()/SetPowerRestored() overwrite whatever BuildLights set, so editing
        // the builder alone changes the range and silently leaves the intensity at 0,85 (D-152, the
        // same shape of bug one more time - one value, two owners, and the owner that runs last wins).
        // emergencyIntensity and restoredIntensity are scaled by the same 0,65 factor, because
        // restoredIntensity used to be HIGHER (1,15) than the emergency state: solving the hex grid
        // was making the room brighter than before it, from a light with no visible fixture.
        public float emergencyIntensity = 2.00f;   // D-235: 0.40 -> 2.00
        public Color restoredColor = new Color(0.55f, 0.75f, 1f);
        public float restoredIntensity = 3.00f;   // D-235: 0.55 -> 3.00

        public void SetEmergencyLighting()
        {
            // Not `return` on a null roomLight: the two halves are independent. A guard on one
            // used to skip the other, which is the same "one value, one owner" shape as D-202.
            if (roomLight != null)
            {
                roomLight.color = emergencyColor;
                roomLight.intensity = emergencyIntensity;
            }
            SetCeilingLamps(false);
            NoteMilestone("QUARENTENA ABSOLUTA");
        }

        public void SetPowerRestored()
        {
            if (roomLight != null)
            {
                roomLight.color = restoredColor;
                roomLight.intensity = restoredIntensity;
            }
            SetCeilingLamps(true);
            NoteMilestone("MALHA DE ENERGIA RESTAURADA");

            // D-226: the room turning on is not just a colour change - the log station reads
            // `grid.powerRestored` to decide whether it is usable, and it only repaints its own
            // label from inside its own handlers (Start, SelectBlock, PlaceSlot). Nothing was
            // telling it that the gate had opened, so a player who solved the mesh BEFORE
            // rebooting the terminal - a legal order, and one the objective line suggests -
            // walked up to a station that said "SEM ENERGIA - RESTAURE A MALHA HEXAGONAL" with
            // the mesh already lit and the ceiling already burning. Measured: LogStatus read
            // "SEM ENERGIA" while the terminal read "REDE ALIMENTADA" and hex read "MALHA
            // ESTAVEL - ENERGIA RESTAURADA", all three on screen at once.
            //
            // The station is repainted through the terminal, because the terminal already holds
            // the reference to it and this is the only place that knows the transition happened.
            // EsHexGrid calls SetPowerRestored() as its reward, so this is the single choke point
            // for the whole edge - no puzzle has to remember to notify anything.
            if (terminal != null) terminal.OnPowerRestored();
        }

        /// <summary>Turns the ceiling lighting on or off as one unit. Null-safe and idempotent,
        /// because it is called from both light states and the builder sets the array up front.</summary>
        public void SetCeilingLamps(bool on)
        {
            if (ceilingLamps == null) return;
            foreach (var l in ceilingLamps)
                if (l != null) l.intensity = on ? ceilingLampIntensity : 0f;
        }

        public void Restart()
        {
            var scene = SceneManager.GetActiveScene();
            Time.timeScale = 1f;
            SceneManager.LoadScene(scene.buildIndex >= 0 ? scene.buildIndex : 0);
        }

        /// <summary>Test hook: unlatch the end state (D-230). Self tests drive Win() through
        /// the exit trigger in edit mode, and the latch would otherwise survive onto whatever
        /// runs next - StartReserve/Win/Lose all refuse while _finished stands, which reads as
        /// "the timer never starts" with no error anywhere. Only the latch is cleared; the
        /// timer fields are untouched because no test touches them.</summary>
        public void DebugResetFinish()
        {
            _finished = false;
            _purifying = false;
        }
    }
}
