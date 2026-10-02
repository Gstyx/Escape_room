using UnityEngine;
using UnityEngine.UI;

namespace EscapeRoom
{
    /// <summary>Flatbed optical reader (Puzzle 3 output). Three sockets accept
    /// any acrylic plate (all share itemKey "placa": OR is commutative, so the
    /// stack order carries no information and only the rotations are checked).
    /// The live OR of the seated plates is previewed on a 4x4 lamp grid; when
    /// it equals the digit-4 template the reader latches, the A.E.G.I.S.
    /// prompt clears and the bunker door force-opens.</summary>
    public class EsPlateReader : MonoBehaviour
    {
        public ItemSocket[] sockets = new ItemSocket[3];

        /// <summary>Digit 4 in light (0 = light passes). Row-major.</summary>
        public int[] templateCells = new int[16]
        {
            0, 1, 1, 0,
            0, 1, 1, 0,
            0, 0, 0, 0,
            1, 1, 1, 0,
        };

        public Image[] previewCells = new Image[16];
        public Button[] rotateButtons = new Button[3];
        public Text statusLabel;
        public BlastDoor door;
        public EscapeGameManager gm;

        public AudioClip rotateClip;
        public AudioClip approveClip;
        public AudioClip seatClip;

        public bool approved { get; private set; }

        void Start()
        {
            Wire();
            for (int i = 0; i < rotateButtons.Length; i++)
            {
                int slot = i;
                if (rotateButtons[i] != null)
                    rotateButtons[i].onClick.AddListener(() => RotateSeated(slot));
            }
            Refresh();
        }

        /// <summary>Idempotent wiring, called by the builder after the sockets
        /// are assigned. Awake/Start do not run on AddComponent in edit mode
        /// (D-166/D-172), so without this the onFilled lock would only exist
        /// in play and the edit-mode self test would seat without locking.</summary>
        public void Wire()
        {
            foreach (var s in sockets)
            {
                if (s == null) continue;
                s.onFilled -= OnPlateSeated;
                s.onFilled += OnPlateSeated;
            }
        }

        void OnDestroy()
        {
            foreach (var s in sockets)
            {
                if (s == null) continue;
                s.onFilled -= OnPlateSeated;
            }
        }

        void OnPlateSeated(ItemSocket s, GrabbableItem item)
        {
            if (item != null) item.pickupLocked = true;
            if (seatClip != null) EsAudio.Play(seatClip, transform.position);
            Refresh();
            CheckAndReward();
        }

        public EsAcrylicPlate SeatedPlate(int slot)
        {
            if (sockets == null || slot < 0 || slot >= sockets.Length) return null;
            var s = sockets[slot];
            if (s == null || !s.IsFilled || s.Item == null) return null;
            return s.Item.GetComponent<EsAcrylicPlate>();
        }

        public void RotateSeated(int slot)
        {
            if (approved) return;
            var p = SeatedPlate(slot);
            if (p == null) return;
            p.Rotate();
            if (rotateClip != null) EsAudio.Play(rotateClip, transform.position);
            Refresh();
            CheckAndReward();
        }

        public int[] CurrentOr()
        {
            var res = new int[16];
            for (int i = 0; i < 3; i++)
            {
                var p = SeatedPlate(i);
                if (p == null) return null;
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                        res[r * 4 + c] |= p.Rotated(r, c);
            }
            return res;
        }

        public bool IsMatch()
        {
            var cur = CurrentOr();
            if (cur == null) return false;
            for (int i = 0; i < 16; i++)
                if (cur[i] != templateCells[i]) return false;
            return true;
        }

        public void CheckAndReward()
        {
            if (approved || !IsMatch()) return;
            approved = true;
            if (approveClip != null) EsAudio.Play(approveClip, transform.position);
            if (statusLabel != null)
            {
                statusLabel.text = "CHAVE ACEITA - EXECUCAO DE SEGURANCA ENCERRADA";
                statusLabel.color = EsTheme.ScreenOn;
            }
            Refresh();
            if (door != null) door.ForceOpen();
            else if (gm != null) gm.Win();
        }

        /// <summary>Test hook.</summary>
        public void DebugReset()
        {
            approved = false;
            Refresh();
        }

        public void Refresh()
        {
            var cur = CurrentOr();
            for (int i = 0; i < 16 && i < previewCells.Length; i++)
            {
                var img = previewCells[i];
                if (img == null) continue;
                if (cur == null) img.color = new Color(0.09f, 0.11f, 0.13f, 1f);
                else if (cur[i] == 0) img.color = new Color(0.29f, 0.78f, 0.91f, 1f);
                else img.color = new Color(0.05f, 0.06f, 0.08f, 1f);
            }
            if (statusLabel != null && !approved)
            {
                int n = 0;
                foreach (var s in sockets)
                    if (s != null && s.IsFilled) n++;
                if (n < 3)
                {
                    statusLabel.text = "LEITOR OPTICO - INSIRA AS 3 PLACAS (" + n + "/3)";
                    statusLabel.color = EsTheme.Amber;
                }
                else
                {
                    statusLabel.text = "GIRE AS PLACAS ATE REVELAR O DIGITO 4";
                    statusLabel.color = EsTheme.Amber;
                }
            }
        }
    }
}
