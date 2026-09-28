using UnityEngine;
using UnityEngine.SceneManagement;

namespace EscapeRoom
{
    /// <summary>Owns the reserve countdown (the diegetic timer), the win/lose states and the
    /// ambience bed. The countdown deliberately starts only when the player reboots the
    /// terminal, so exploring and reading the room is free.</summary>
    public class EscapeGameManager : MonoBehaviour
    {
        public static EscapeGameManager Instance { get; private set; }

        [Header("Reserva critica")]
        public float reserveSeconds = 360f;
        public float warnAtSeconds = 120f;
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
                int total = Mathf.Max(0, Mathf.CeilToInt(_remaining));
                var ts = System.TimeSpan.FromSeconds(total);
                terminal.SetTimer(string.Format("{0:00}:{1:00}", ts.Minutes, ts.Seconds));
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
            NoteMilestone("RESERVA EM CARGA");
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
            float bonus = _remaining;
            var ts = System.TimeSpan.FromSeconds(Mathf.CeilToInt(bonus));
            if (terminal != null)
                terminal.ShowEnd("SAIDA LIBERADA",
                    "Você escapou da Filial 9 com " + string.Format("{0:00}:{1:00}", ts.Minutes, ts.Seconds)
                    + " de reserva restantes.\n\nA porta blindada se selou atras de voce.",
                    new Color(0.24f, 0.85f, 0.64f));
        }

        public void Lose()
        {
            if (_finished) return;
            _finished = true;
            if (failClip != null) EsAudio.Play(failClip, Vector3.zero, 1f, 0f);
            if (alarmLight != null) alarmLight.intensity = 0f;
            if (terminal != null)
                terminal.ShowEnd("SINAL PERDIDO",
                    "A reserva de emergencia acabou. As magnetotravas\nreengataram e o terminal se fechou.",
                    new Color(0.91f, 0.23f, 0.23f));
        }

        public void Restart()
        {
            var scene = SceneManager.GetActiveScene();
            Time.timeScale = 1f;
            SceneManager.LoadScene(scene.buildIndex >= 0 ? scene.buildIndex : 0);
        }
    }
}
