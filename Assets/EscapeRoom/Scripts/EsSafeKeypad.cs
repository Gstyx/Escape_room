using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeRoom
{
    /// <summary>Standalone safe keypad (Puzzle 2 output). Decoupled from
    /// TerminalController on purpose: the terminal owns the old survival-code
    /// flow, the safe owns the 4-digit PIN (default 3719, the last chars of
    /// the chronologically ordered log packets). On success the safe door
    /// swings open and the three acrylic plates inside become reachable.</summary>
    public class EsSafeKeypad : MonoBehaviour
    {
        public string correctPin = "3719";

        [Header("UI")]
        public GameObject keypadRoot;
        public Text echo;
        public Text statusLabel;

        [Header("Cofre")]
        public Transform safeDoorPivot;

        [Header("Ordem")]
        // D-229: who the safe takes orders from. The PIN is knowledge from the logs, so the
        // keypad stays dead until the logs are solved - measured live, typing 3719 with zero
        // puzzles solved opened the safe and skipped hex and logs outright. Null means unwired,
        // which reads as solved (same convention as the log station's grid gate).
        public EsLogSortPuzzle logPuzzle;

        [Header("Audio")]
        public AudioClip clickClip;
        public AudioClip errorClip;
        public AudioClip unlockClip;

        public bool solved { get; private set; }
        readonly List<string> _entry = new List<string>();
        int _lastKeyFrame = -1;
        string _lastKey;

        void Start()
        {
            if (keypadRoot == null) return;
            var btns = new List<Button>();
            var caps = new List<string>();
            for (int i = 0; i < keypadRoot.transform.childCount; i++)
            {
                var child = keypadRoot.transform.GetChild(i);
                var b = child.GetComponent<Button>();
                if (b == null) continue;
                var lbl = child.GetComponentInChildren<Text>();
                btns.Add(b);
                caps.Add(lbl != null ? lbl.text : "?");
            }
            for (int i = 0; i < btns.Count; i++)
            {
                var btn = btns[i];
                string cap = caps[i];
                btn.onClick.AddListener(() => OnKeyPress(cap));
            }
            RefreshEcho();
        }

        public void OnKeyPress(string cap)
        {
            if (solved) return;
            if (_lastKeyFrame == Time.frameCount && _lastKey == cap) return;
            _lastKeyFrame = Time.frameCount;
            _lastKey = cap;

            // D-229: knowledge only counts once it exists. The safe is PIN-gated by design -
            // there is no power lock on a mechanical keypad - but a correct PIN typed before
            // the logs are solved is a guess, not a deduction, and it used to open the safe.
            // Refused with the reason on the status line (an echo buzz alone reads as broken),
            // and the dedupe above already ran, so double-dispatched clicks cannot buzz twice.
            if (logPuzzle != null && !logPuzzle.solved)
            {
                if (errorClip != null) EsAudio.Play(errorClip, transform.position);
                if (statusLabel != null)
                {
                    statusLabel.text = "COFRE SEM DADOS - ORDENE OS LOGS PRIMEIRO";
                    statusLabel.color = EsTheme.Warn;
                }
                return;
            }

            if (clickClip != null) EsAudio.Play(clickClip, transform.position);
            if (cap == "ENTER") { Submit(); return; }
            if (cap == "LIMPAR") { ClearAll(); return; }
            if (cap == "DEL")
            {
                if (_entry.Count > 0) _entry.RemoveAt(_entry.Count - 1);
                RefreshEcho();
                return;
            }
            if (cap.Length != 1 || cap[0] < '0' || cap[0] > '9') return;
            if (_entry.Count >= correctPin.Length)
            {
                if (errorClip != null) EsAudio.Play(errorClip, transform.position);
                return;
            }
            _entry.Add(cap);
            RefreshEcho();
        }

        void Submit()
        {
            string guess = string.Concat(_entry.ToArray());
            if (guess == correctPin)
            {
                solved = true;
                if (unlockClip != null) EsAudio.Play(unlockClip, transform.position);
                OpenSafe();
            }
            else
            {
                if (errorClip != null) EsAudio.Play(errorClip, transform.position);
                if (echo != null) echo.text = "NEGADO";
                _entry.Clear();
            }
        }

        void OpenSafe()
        {
            if (safeDoorPivot != null && safeDoorPivot.gameObject.activeSelf)
            {
                var anim = safeDoorPivot.GetComponent<EsSimpleSwing>();
                if (anim == null) anim = safeDoorPivot.gameObject.AddComponent<EsSimpleSwing>();
                anim.Play(1.1f, 0f, -95f);
            }
            if (statusLabel != null)
            {
                statusLabel.text = "COFRE ABERTO - RETIRE AS 3 PLACAS DE ACRILICO";
                statusLabel.color = EsTheme.ScreenOn;
            }
            RefreshEcho();
        }

        /// <summary>Total reset: mistyped PINs restart clean instead of
        /// forcing digit-by-digit DEL.</summary>
        public void ClearAll()
        {
            _entry.Clear();
            RefreshEcho();
        }

        void RefreshEcho()
        {
            if (echo == null) return;
            if (solved) { echo.text = correctPin; return; }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _entry.Count; i++) sb.Append(_entry[i]);
            for (int i = _entry.Count; i < correctPin.Length; i++) sb.Append('_');
            echo.text = sb.ToString();
        }

        /// <summary>Test hook.</summary>
        public void DebugReset()
        {
            solved = false;
            _entry.Clear();
            RefreshEcho();
            if (statusLabel != null)
            {
                statusLabel.text = "PIN 4 DIGITOS - DEL APAGA - LIMPAR ZERA";
                statusLabel.color = EsTheme.Amber;
            }
        }

        /// <summary>Called when the logs are solved (D-229). Repaints the keypad's standing
        /// instruction, which the gate above may have overwritten with the refusal: without
        /// this a player who tried the safe early, solved the logs, and came back would read
        /// "ORDENE OS LOGS PRIMEIRO" with the logs already ordered - the D-226 stale-label
        /// shape, one station over. No-op once solved.</summary>
        public void OnLogsSolved()
        {
            if (solved || statusLabel == null) return;
            // D-235: name the PIN outright - the logs already showed it, and a visitor who
            // walked across the room should not have to remember four digits.
            statusLabel.text = "DIGITE " + correctPin + " E APERTE ENTER";
            statusLabel.color = EsTheme.Amber;
        }
    }
}
