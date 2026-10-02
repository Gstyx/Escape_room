using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeRoom
{
    /// <summary>Puzzle 2 (Fase 2, No-Zero): log decryption by chronological order.
    ///
    /// Four stabilized data packets carry hex Unix timestamps. The player puts
    /// them into four slots oldest-first (click a block, then click a slot -
    /// click-click instead of drag, because with a locked cursor the click is
    /// resolved at the screen centre and a real drag cannot be aimed).
    /// The backend validates the slot array against the sorted array. Once the
    /// order holds, the last absolute char of each string (string[^1] in C#)
    /// in slot order is the 4-digit safe PIN (here: 3-7-1-9).</summary>
    public class EsLogSortPuzzle : MonoBehaviour
    {
        public string[] packetHex = new string[]
        {
            "0x64F8A3",
            "0x64F8B7",
            "0x64F8C1",
            "0x64F8D9",
        };

        /// <summary>Display scramble: which packet each block button shows.</summary>
        public int[] displayOrder = new int[] { 2, 0, 3, 1 };

        /// <summary>Packet index per slot, -1 when empty.</summary>
        public int[] slotPacket = new int[] { -1, -1, -1, -1 };

        public int selectedDisplay = -1;
        public bool solved { get; private set; }

        [Header("Portao")]
        public EsHexGrid grid;

        [Header("UI")]
        public Button[] blockButtons = new Button[4];
        public Text[] blockLabels = new Text[4];
        public Button[] slotButtons = new Button[4];
        public Text[] slotLabels = new Text[4];
        public Text statusLabel;

        [Header("Audio")]
        public AudioClip selectClip;
        public AudioClip placeClip;
        public AudioClip solvedClip;
        public AudioClip errorClip;

        // ---------------------------------------------------------------- pure logic
        public static uint ParseHex(string s)
        {
            string t = s.StartsWith("0x") ? s.Substring(2) : s;
            return System.Convert.ToUInt32(t, 16);
        }

        public static int[] SortedIndices(string[] packets)
        {
            var idx = new List<int>();
            for (int i = 0; i < packets.Length; i++) idx.Add(i);
            idx.Sort((a, b) => ParseHex(packets[a]).CompareTo(ParseHex(packets[b])));
            return idx.ToArray();
        }

        public static string DerivePinStatic(string[] packets, int[] slotsInOrder)
        {
            var sb = new System.Text.StringBuilder();
            foreach (int p in slotsInOrder)
            {
                if (p < 0 || p >= packets.Length) return "";
                string s = packets[p];
                if (s.Length == 0) return "";
                sb.Append(s[s.Length - 1]);
            }
            return sb.ToString();
        }

        public bool CanInteract()
        {
            return grid == null || grid.powerRestored;
        }

        public bool IsSolved()
        {
            int[] want = SortedIndices(packetHex);
            if (slotPacket == null || slotPacket.Length != want.Length) return false;
            for (int i = 0; i < want.Length; i++)
                if (slotPacket[i] != want[i]) return false;
            return true;
        }

        public string DerivedPin()
        {
            return DerivePinStatic(packetHex, slotPacket);
        }

        // ---------------------------------------------------------------- interaction (click-click)
        public void SelectBlock(int dispIdx)
        {
            if (solved || !CanInteract()) return;
            if (dispIdx < 0 || dispIdx >= displayOrder.Length) return;
            selectedDisplay = dispIdx;
            if (selectClip != null) EsAudio.Play(selectClip, transform.position);
            Refresh();
        }

        public void PlaceSlot(int slot)
        {
            if (solved || !CanInteract()) return;
            if (slot < 0 || slot >= slotPacket.Length) return;
            if (selectedDisplay >= 0)
            {
                int packet = displayOrder[selectedDisplay];
                for (int i = 0; i < slotPacket.Length; i++)
                    if (slotPacket[i] == packet) slotPacket[i] = -1;
                slotPacket[slot] = packet;
                selectedDisplay = -1;
                if (placeClip != null) EsAudio.Play(placeClip, transform.position);
            }
            else if (slotPacket[slot] >= 0)
            {
                slotPacket[slot] = -1;
                if (placeClip != null) EsAudio.Play(placeClip, transform.position);
            }
            Refresh();
            CheckAndReward();
        }

        public void CheckAndReward()
        {
            if (solved || !IsSolved()) return;
            solved = true;
            if (solvedClip != null) EsAudio.Play(solvedClip, transform.position);
            Refresh();
        }

        /// <summary>Test hook: force a slot state without UI.</summary>
        public void SetSlotsForTest(int[] slots)
        {
            for (int i = 0; i < slotPacket.Length && i < slots.Length; i++)
                slotPacket[i] = slots[i];
            Refresh();
            CheckAndReward();
        }

        public void DebugReset()
        {
            solved = false;
            selectedDisplay = -1;
            for (int i = 0; i < slotPacket.Length; i++) slotPacket[i] = -1;
            Refresh();
        }

        // ---------------------------------------------------------------- UI
        void Start()
        {
            for (int i = 0; i < blockButtons.Length; i++)
            {
                int d = i;
                if (blockButtons[i] != null)
                    blockButtons[i].onClick.AddListener(() => SelectBlock(d));
            }
            for (int i = 0; i < slotButtons.Length; i++)
            {
                int s = i;
                if (slotButtons[i] != null)
                    slotButtons[i].onClick.AddListener(() => PlaceSlot(s));
            }
            Refresh();
        }

        public void Refresh()
        {
            for (int i = 0; i < 4; i++)
            {
                if (blockLabels[i] != null && i < displayOrder.Length)
                {
                    string hex = packetHex[displayOrder[i]];
                    blockLabels[i].text = (i == selectedDisplay ? "> " : "") + hex;
                }
                if (slotLabels[i] != null)
                    slotLabels[i].text = slotPacket[i] >= 0 ? packetHex[slotPacket[i]] : "----";
            }
            if (statusLabel != null)
            {
                if (solved)
                {
                    statusLabel.text = "ORDEM ACEITA - LEIA O ULTIMO DIGITO DE CADA PACOTE";
                    statusLabel.color = EsTheme.ScreenOn;
                }
                else if (!CanInteract())
                {
                    statusLabel.text = "SEM ENERGIA - RESTAURE A MALHA HEXAGONAL";
                    statusLabel.color = EsTheme.Warn;
                }
                else
                {
                    statusLabel.text = "ORDENE OS PACOTES DO MENOR PARA O MAIOR";
                    statusLabel.color = EsTheme.Amber;
                }
            }
        }
    }
}
