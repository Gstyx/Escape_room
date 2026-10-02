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

        float _remaining;
        bool _running;
        bool _finished;
        float _beepTimer;
        float _pulse;
        int _milestones;

        public float Remaining => _remaining;
        public bool IsRunning => _running;

        void Awake()
        {
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
                terminal.ShowEnd("QUARENTENA ENCERRADA",
                    "Voce escapou do No-Zero com " + string.Format("{0:00}:{1:00}", bonus / 60, bonus % 60)
                    + " de suporte vital restante.\n\nO A.E.G.I.S. liberou a porta do bunker.",
                    new Color(0.24f, 0.85f, 0.64f));
        }

        public void Lose()
        {
            if (_finished) return;
            _finished = true;
            if (failClip != null) EsAudio.Play(failClip, Vector3.zero, 1f, 0f);
            if (alarmLight != null) alarmLight.intensity = 0f;
            if (terminal != null)
                terminal.ShowEnd("SUPORTE VITAL ESGOTADO",
                    "O suporte vital foi drenado.\nO A.E.G.I.S. manteve a quarentena absoluta.",
                    new Color(0.91f, 0.23f, 0.23f));
        }

        // ---------------------------------------------------------------- A.E.G.I.S. light states
        // Fase 0: the room starts on red emergency lighting. Fase 1 (energy mesh)
        // calls SetPowerRestored() as the puzzle reward (red -> blue).
        [Header("A.E.G.I.S. - estados de luz")]
        public Color emergencyColor = new Color(1f, 0.16f, 0.14f);
        public float emergencyIntensity = 0.85f;
        public Color restoredColor = new Color(0.55f, 0.75f, 1f);
        public float restoredIntensity = 1.15f;

        public void SetEmergencyLighting()
        {
            if (roomLight == null) return;
            roomLight.color = emergencyColor;
            roomLight.intensity = emergencyIntensity;
            NoteMilestone("QUARENTENA ABSOLUTA");
        }

        public void SetPowerRestored()
        {
            if (roomLight == null) return;
            roomLight.color = restoredColor;
            roomLight.intensity = restoredIntensity;
            NoteMilestone("MALHA DE ENERGIA RESTAURADA");
        }

        public void Restart()
        {
            var scene = SceneManager.GetActiveScene();
            Time.timeScale = 1f;
            SceneManager.LoadScene(scene.buildIndex >= 0 ? scene.buildIndex : 0);
        }
    }
}
