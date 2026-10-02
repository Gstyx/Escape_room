using System.Collections.Generic;
using UnityEngine;

namespace EscapeRoom
{
    /// <summary>One clickable hex. Click rotates +60° unless fixed or latched.
    ///
    /// Lives in its own file on purpose: sharing EsHexGrid.cs with a second
    /// MonoBehaviour broke scene deserialization (only file-order-late
    /// instances bound; the rest lost their component silently).</summary>
    public class EsHexNode : MonoBehaviour, IInteractable
    {
        public EsHexGrid grid;
        public int col, row;
        public int baseMask;
        public int rotation;
        public int solvedRotation;
        public bool isFixed;
        public bool isPath;
        public NodeKind kind;
        public Transform spinner;
        public List<Renderer> pips;
        public Renderer tileRenderer;
        public int depth;
        public bool lit;
        public bool pulsed;
        /// <summary>Single-port route piece (D-183): wears leaf colors.</summary>
        public bool isLeaf;

        public bool Interact(GrabbableItem held)
        {
            if (held != null) return false;   // never consume a held item
            if (grid == null || grid.powerRestored) return false;
            if (isFixed)
            {
                // A silent refusal reads as a broken node. Say why on the panel.
                if (grid.statusLabel != null)
                {
                    grid.statusLabel.text = kind == NodeKind.Blank
                        ? "NO CORROMPIDO - SEM SINAL"
                        : "NO FIXO - TERMINAL DO CIRCUITO";
                    grid.statusLabel.color = EsTheme.Amber;
                }
                return false;
            }
            grid.RotateNode(this);
            return true;
        }

        public string PromptFor(GrabbableItem held)
        {
            if (grid != null && grid.powerRestored) return "MALHA ESTAVEL";
            switch (kind)
            {
                case NodeKind.Emitter: return "EMISSOR DE PLASMA";
                case NodeKind.Receptor: return "RECEPTOR DO TERMINAL";
                case NodeKind.Blank: return "NO CORROMPIDO";
                default: return "Girar no hexagonal";
            }
        }

        public void SetLit(bool on, Material live, Material dim)
        {
            lit = on;
            if (pips != null)
                foreach (var p in pips)
                    if (p != null)
                    {
                        p.sharedMaterial = on ? live : dim;
                        if (!on && pulsed) p.SetPropertyBlock(null);
                    }
            if (!on) pulsed = false;
        }
    }
}
