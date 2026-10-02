using System.Collections.Generic;
using UnityEngine;

namespace EscapeRoom
{
    /// <summary>Puzzle 1 (Fase 1, Nó-Zero): plasma routing on a hex mesh.
    ///
    /// Odd-r offset grid of pointy-top hexes. Click = +60° (mask shifts +1 per
    /// click, spinner turns +60° about +Z — pip geometry matches by construction,
    /// and Self Test 13 asserts it).
    ///
    /// Win rule (D-180 strict, D-184): emitter→receptor energized AND no
    /// energized port may dangle — into the void (panel edge included) or into
    /// a neighbour missing the mating port. Non-energized decoys dangle freely
    /// (they cannot tap the route by construction). Two terminator caps carry
    /// the leak gameplay. Scramble starts from a verified solved state, so a
    /// solution always exists.</summary>
    public class EsHexGrid : MonoBehaviour
    {
        // Six directions, CCW from east: E NE NW W SW SE.
        public const int DIRS = 6;
        public static int Opp(int d) { return (d + 3) % 6; }

        /// <summary>Odd-r offset neighbour, pointy-top, Y-UP (rows grow upward,
        /// matching the layout formula). A y-down table here is a vertical
        /// mirror: logic stays self-consistent (round-trip still passes, the
        /// all-blank board still solves) while geometrically mating ports never
        /// meet — 400/400 fills clean, 0/400 witnesses. That exact failure is
        /// why Self Test 13 also asserts layout DISTANCE, which a mirror breaks.</summary>
        public static bool Neighbour(int c, int r, int d, int cols, int rows,
                                     out int nc, out int nr)
        {
            nc = c; nr = r;
            switch (d)
            {
                case 0: nc = c + 1; break;                    // E
                case 1: if ((r & 1) == 1) { nc = c + 1; nr = r + 1; } else { nc = c; nr = r + 1; } break; // NE
                case 2: if ((r & 1) == 1) { nc = c; nr = r + 1; } else { nc = c - 1; nr = r + 1; } break; // NW
                case 3: nc = c - 1; break;                    // W
                case 4: if ((r & 1) == 1) { nc = c; nr = r - 1; } else { nc = c - 1; nr = r - 1; } break; // SW
                case 5: if ((r & 1) == 1) { nc = c + 1; nr = r - 1; } else { nc = c; nr = r - 1; } break; // SE
                default: return false;
            }
            if (nc < 0 || nr < 0 || nc >= cols || nr >= rows) return false;
            return true;
        }

        /// <summary>Panel-local centre of a cell. Single source of truth for
        /// SpawnNode and the distance check (same formula, one place).</summary>
        public static Vector2 CellLocal(int c, int r, float size)
        {
            return new Vector2(size * 1.7320508f * (c + 0.5f * (r & 1)), size * 1.5f * r);
        }

        public static int RotPlus(int mask, int steps)
        {
            steps = ((steps % 6) + 6) % 6;
            int m = mask & 63;
            for (int i = 0; i < steps; i++)
                m = ((m << 1) | ((m >> 5) & 1)) & 63;
            return m;
        }

        [Header("Grade")]
        public int cols = 4;
        public int rows = 4;
        public float cellSize = 0.30f;
        public int seed = 2142;

        [Header("Materiais (assets, nunca runtime)")]
        public Material baseMat;
        public Material dimMat;
        public Material liveMat;
        public Material srcMat;

        [Header("Fios")]
        public AudioClip rotateClip;
        public AudioClip solvedClip;
        public UnityEngine.UI.Text statusLabel;
        public EscapeGameManager gm;

        public bool powerRestored { get; private set; }
        public List<EsHexNode> nodes = new List<EsHexNode>();
        public Vector2 boundsLocal { get; private set; }
        /// <summary>Terminator caps placed (single-port rotatable side branches).</summary>
        public int capsPlaced { get; private set; }

        [Header("Leitura da rota (Fase 1b)")]
        public Material liveTileMat;
        public Material leafMat;
        public Material leafLiveMat;
        static MaterialPropertyBlock _mpb;
        Color _liveEm;
        bool _liveEmCached;

        EsHexNode[,] _cell;

        // ---------------------------------------------------------------- build (edit mode)
        /// <summary>Generates, verifies (strict-solved at solution), scrambles and
        /// spawns. Deterministic per seed, so every Build Level ships the same puzzle.</summary>
        public void Generate()
        {
            foreach (Transform ch in transform)
                if (ch.name.StartsWith("Node_")) Object.DestroyImmediate(ch.gameObject);
            nodes.Clear();

            var rng = new System.Random(seed);
            _cell = new EsHexNode[cols, rows];

            // --- D-184: emitter->receptor + strict no-leak (D-180). The NetWalk-total
            // rule (D-182) proved too punishing in real play: a single dark pip
            // anywhere fails the board. Distractors are inert by construction
            // (path ports all face path neighbours - measured mating 0/400), so
            // the leak gameplay lives entirely in the two terminator caps.
            int r0 = rows / 2;
            List<int> path = null;
            for (int attempt = 0; attempt < 200 && path == null; attempt++)
                path = RandomPath(rng, 0, r0, cols - 1, r0, cols + 2);
            if (path == null) path = StraightPath(r0);   // fallback always exists

            var baseMask = new int[cols, rows];
            var solvedRot = new int[cols, rows];
            var fixedCell = new bool[cols, rows];
            var kind = new NodeKind[cols, rows];

            // Path ports: exactly the real in/out steps, incoming edge mirrored
            // (Opp of travel dir). The emitter/receptor carry ONLY their step
            // direction - a fixed east/west port would dangle on diagonal exits.
            var onPath = new HashSet<int>(path);
            if (!AssignPathMasks(path, baseMask, kind, fixedCell))
            {
                path = StraightPath(r0);
                onPath = new HashSet<int>(path);
                AssignPathMasks(path, baseMask, kind, fixedCell);
            }

            // Terminator caps: two interior path cells grow one spare port toward
            // an adjacent free cell, which becomes a single-port rotatable cap
            // (teal leaf). A cap turned away dangles its path cell while the
            // receptor stays fed - the fed-with-leak witness the rule exists for.
            var capCells = new HashSet<int>();
            int capsWanted = 2;
            for (int i = 1; i < path.Count - 1 && capsWanted > 0; i++)
            {
                int c = path[i] / rows, r = path[i] % rows;
                int have = baseMask[c, r];
                var opts = new List<int>();
                for (int d = 0; d < 6; d++)
                {
                    if ((have & (1 << d)) != 0) continue;
                    int nc, nr;
                    if (!Neighbour(c, r, d, cols, rows, out nc, out nr)) continue;
                    int nk = nc * rows + nr;
                    if (onPath.Contains(nk) || capCells.Contains(nk)) continue;
                    opts.Add(d);
                }
                if (opts.Count == 0) continue;
                int pick = opts[rng.Next(opts.Count)];
                int xc, xr;
                Neighbour(c, r, pick, cols, rows, out xc, out xr);
                baseMask[c, r] = have | (1 << pick);
                int xk = xc * rows + xr;
                baseMask[xc, xr] = 1 << Opp(pick);
                solvedRot[xc, xr] = 0;
                fixedCell[xc, xr] = false;
                kind[xc, xr] = NodeKind.Normal;
                capCells.Add(xk);
                capsWanted--;
            }
            capsPlaced = 2 - capsWanted;
            if (capsPlaced == 0)
                Debug.LogWarning("[HexGrid] no room for terminator caps - leak rule vacuous on this board.");

            var adjSet = new List<int>();
            foreach (int key in onPath)
            {
                int c = key / rows, r = key % rows;
                for (int d = 0; d < 6; d++)
                {
                    int nc, nr;
                    if (!Neighbour(c, r, d, cols, rows, out nc, out nr)) continue;
                    int nk = nc * rows + nr;
                    if (!onPath.Contains(nk) && !capCells.Contains(nk) && !adjSet.Contains(nk)) adjSet.Add(nk);
                }
            }
            int forced = adjSet.Count > 0 ? adjSet[rng.Next(adjSet.Count)] : -1;

            // --- distractors (visual decoys, electrically inert) + 2 corrupted
            // blanks. Accepted only when strict-clean WITH a one-move witness.
            int[] archetypes = { (1 << 0) | (1 << 3),          // straight E-W
                                 (1 << 0) | (1 << 1),          // elbow E-NE
                                 (1 << 0) | (1 << 1) | (1 << 2) }; // tee
            bool ok = false;
            int strictPass = 0, witnessPass = 0;
            for (int attempt = 0; attempt < 400 && !ok; attempt++)
            {
                var free = new List<int>();
                for (int c = 0; c < cols; c++)
                    for (int r = 0; r < rows; r++)
                    {
                        int key = c * rows + r;
                        if (!onPath.Contains(key) && !capCells.Contains(key)) free.Add(key);
                    }
                free.Remove(forced);
                var blanks = new HashSet<int>();
                for (int b = 0; b < 2 && free.Count > 0; b++)
                {
                    int pick = free[rng.Next(free.Count)];
                    free.Remove(pick);
                    blanks.Add(pick);
                }
                for (int c = 0; c < cols; c++)
                    for (int r = 0; r < rows; r++)
                    {
                        int key = c * rows + r;
                        if (onPath.Contains(key) || capCells.Contains(key)) continue;
                        if (blanks.Contains(key))
                        { baseMask[c, r] = 0; solvedRot[c, r] = 0; fixedCell[c, r] = true; kind[c, r] = NodeKind.Blank; }
                        else
                        {
                            baseMask[c, r] = archetypes[rng.Next(archetypes.Length)];
                            solvedRot[c, r] = rng.Next(6);
                            fixedCell[c, r] = false;
                            kind[c, r] = NodeKind.Normal;
                        }
                    }
                ok = StrictSolved(baseMask, solvedRot);
                if (ok)
                {
                    strictPass++;
                    if (HasOneMoveWitness(baseMask, solvedRot, fixedCell)) { witnessPass++; }
                    else ok = false;
                }
                if (attempt == 0)
                {
                    var sb = new System.Text.StringBuilder("[HexGrid] attempt0 path=");
                    foreach (int key in onPath) sb.Append(key + " ");
                    sb.Append("| cells: ");
                    for (int c = 0; c < cols; c++)
                        for (int r = 0; r < rows; r++)
                            sb.Append(string.Format("({0},{1})m{2}r{3}{4} ", c, r, baseMask[c, r], solvedRot[c, r],
                                fixedCell[c, r] ? "F" : (onPath.Contains(c * rows + r) ? "P" : (capCells.Contains(c * rows + r) ? "C" : "D"))));
                    sb.Append("live=" + Energized(baseMask, solvedRot).Count);
                    Debug.Log(sb.ToString());
                }
            }
            Debug.Log("[HexGrid] fill acceptance: strict-clean=" + strictPass + "/400 witness=" + witnessPass + "/400");
            if (!ok)
            {
                Debug.LogWarning("[HexGrid] no strict-clean fill in 400 tries - falling back (caps survive: they ARE the leak rule).");
                for (int c = 0; c < cols; c++)
                    for (int r = 0; r < rows; r++)
                    {
                        int key = c * rows + r;
                        if (onPath.Contains(key) || capCells.Contains(key)) continue;
                        baseMask[c, r] = 0; solvedRot[c, r] = 0;
                        fixedCell[c, r] = true; kind[c, r] = NodeKind.Blank;
                    }
            }

            // --- spawn, then scramble (guaranteed non-solved)
            Mesh tile = HexTileMesh(cellSize * 0.92f, 0.09f);
            float w = 0f, h = 0f;
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                {
                    var node = SpawnNode(tile, c, r, baseMask[c, r], fixedCell[c, r], kind[c, r]);
                    node.solvedRotation = solvedRot[c, r];
                    node.isPath = onPath.Contains(c * rows + r);
                    _cell[c, r] = node;
                    nodes.Add(node);
                    w = Mathf.Max(w, node.transform.localPosition.x);
                    h = Mathf.Max(h, node.transform.localPosition.y);
                }
            boundsLocal = new Vector2(w, h);
            for (int guard = 0; guard < 60; guard++)
            {
                foreach (var n in nodes)
                    if (!n.isFixed) n.rotation = rng.Next(6);
                ApplyAllVisuals();
                if (!IsSolved()) break;
            }
        }

        /// <summary>Winding self-avoiding walk for the route backbone. Only the
        /// masks along it conduct; everything else is decoy or blank.</summary>
        List<int> RandomPath(System.Random rng, int c0, int r0, int c1, int r1, int minLen)
        {
            var path = new List<int>();
            var seen = new HashSet<int>();
            int c = c0, r = r0;
            for (int step = 0; step < cols * rows; step++)
            {
                path.Add(c * rows + r);
                seen.Add(c * rows + r);
                if (c == c1 && r == r1) return path.Count >= minLen ? path : null;
                var opts = new List<int>();
                for (int d = 0; d < 6; d++)
                {
                    int nc, nr;
                    if (!Neighbour(c, r, d, cols, rows, out nc, out nr)) continue;
                    if (seen.Contains(nc * rows + nr)) continue;
                    opts.Add(d);
                }
                if (opts.Count == 0) return null;
                int pick = opts[rng.Next(opts.Count)];
                int tc, tr;
                Neighbour(c, r, pick, cols, rows, out tc, out tr);
                c = tc; r = tr;
            }
            return null;
        }

        List<int> StraightPath(int r0)
        {
            var path = new List<int>();
            for (int c = 0; c < cols; c++) path.Add(c * rows + r0);
            return path;
        }

        /// <summary>Route masks: exactly the real in/out steps, incoming edge
        /// mirrored (Opp of travel dir). Returns false on broken adjacency.</summary>
        bool AssignPathMasks(List<int> p, int[,] bases, NodeKind[,] kinds, bool[,] fixes)
        {
            for (int i = 0; i < p.Count; i++)
            {
                int c = p[i] / rows, r = p[i] % rows;
                int m = 0;
                if (i > 0)
                {
                    int pc = p[i - 1] / rows, pr = p[i - 1] % rows;
                    int back = DirTo(pc, pr, c, r);
                    if (back < 0) return false;
                    m |= 1 << Opp(back);
                }
                if (i < p.Count - 1)
                {
                    int qc = p[i + 1] / rows, qr = p[i + 1] % rows;
                    int fwd = DirTo(c, r, qc, qr);
                    if (fwd < 0) return false;
                    m |= 1 << fwd;
                }
                bases[c, r] = m;
                kinds[c, r] = (i == 0) ? NodeKind.Emitter
                              : (i == p.Count - 1) ? NodeKind.Receptor : NodeKind.Normal;
                if (kinds[c, r] != NodeKind.Normal) fixes[c, r] = true;
            }
            return true;
        }

        int DirTo(int c0, int r0, int c1, int r1)
        {
            for (int d = 0; d < 6; d++)
            {
                int nc, nr;
                if (Neighbour(c0, r0, d, cols, rows, out nc, out nr) && nc == c1 && nr == r1)
                    return d;
            }
            return -1;
        }

        /// <summary>True when some single rotation off the given (solved) state
        /// feeds the receptor through a leak. Mutates rots transiently, restores.</summary>
        bool HasOneMoveWitness(int[,] bases, int[,] rots, bool[,] fixes)
        {
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                {
                    if (fixes[c, r]) continue;
                    for (int t = 0; t < 6; t++)
                    {
                        if (t == rots[c, r]) continue;
                        int old = rots[c, r];
                        rots[c, r] = t;
                        var live = Energized(bases, rots);
                        bool fed = live.Contains((cols - 1) * rows + rows / 2);
                        bool clean = fed && StrictSolved(bases, rots);
                        rots[c, r] = old;
                        if (fed && !clean) return true;
                    }
                }
            return false;
        }

        // ---------------------------------------------------------------- logic
        int EffMask(int c, int r, int[,] bases, int[,] rots)
        {
            return RotPlus(bases[c, r], rots[c, r]);
        }

        HashSet<int> Energized(int[,] bases, int[,] rots)
        {
            return new HashSet<int>(LiveDepths(bases, rots).Keys);
        }

        /// <summary>Energized cells with BFS depth from the emitter. Depth drives
        /// the flow pulse (Update), so the route reads emitter-to-receptor.</summary>
        Dictionary<int, int> LiveDepths(int[,] bases, int[,] rots)
        {
            var depth = new Dictionary<int, int>();
            int r0 = rows / 2;
            var queue = new Queue<int>();
            queue.Enqueue(0 * rows + r0);
            depth[0 * rows + r0] = 0;
            while (queue.Count > 0)
            {
                int key = queue.Dequeue();
                int c = key / rows, r = key % rows;
                int m = EffMask(c, r, bases, rots);
                for (int d = 0; d < 6; d++)
                {
                    if ((m & (1 << d)) == 0) continue;
                    int nc, nr;
                    if (!Neighbour(c, r, d, cols, rows, out nc, out nr)) continue;
                    if ((EffMask(nc, nr, bases, rots) & (1 << Opp(d))) == 0) continue;
                    int nk = nc * rows + nr;
                    if (!depth.ContainsKey(nk)) { depth[nk] = depth[key] + 1; queue.Enqueue(nk); }
                }
            }
            return depth;
        }

        bool StrictSolved(int[,] bases, int[,] rots)
        {
            var live = Energized(bases, rots);
            int r0 = rows / 2;
            if (!live.Contains((cols - 1) * rows + r0)) return false;   // receptor dark
            foreach (int key in live)
            {
                int c = key / rows, r = key % rows;
                int m = EffMask(c, r, bases, rots);
                for (int d = 0; d < 6; d++)
                {
                    if ((m & (1 << d)) == 0) continue;
                    int nc, nr;
                    // void (edge included) or unmated neighbour = leak
                    if (!Neighbour(c, r, d, cols, rows, out nc, out nr)) return false;
                    if ((EffMask(nc, nr, bases, rots) & (1 << Opp(d))) == 0) return false;
                }
            }
            return true;
        }

        void Snapshot(out int[,] bases, out int[,] rots)
        {
            bases = new int[cols, rows];
            rots = new int[cols, rows];
            foreach (var n in nodes) { bases[n.col, n.row] = n.baseMask; rots[n.col, n.row] = n.rotation; }
        }

        public bool IsSolved()
        {
            int[,] b, r;
            Snapshot(out b, out r);
            return StrictSolved(b, r);
        }

        public bool ReceptorEnergized()
        {
            int[,] b, r;
            Snapshot(out b, out r);
            return Energized(b, r).Contains((cols - 1) * rows + rows / 2);
        }

        // ---------------------------------------------------------------- interaction
        public void RotateNode(EsHexNode n)
        {
            if (powerRestored || n == null || n.isFixed) return;
            n.rotation = (n.rotation + 1) % 6;
            ApplyVisual(n);
            if (rotateClip != null) EsAudio.Play(rotateClip, n.transform.position);
            RefreshAll();
            CheckAndReward();
        }

        /// <summary>Silent rotation for tests: no sound, no reward.</summary>
        public void SetRotationSilent(EsHexNode n, int rot)
        {
            n.rotation = ((rot % 6) + 6) % 6;
            ApplyVisual(n);
            RefreshAll();
        }

        /// <summary>Test hook: unlatch the reward without touching the board.</summary>
        public void DebugResetPower()
        {
            powerRestored = false;
            RefreshAll();
        }

        public void CheckAndReward()
        {
            if (powerRestored || !IsSolved()) return;
            powerRestored = true;
            if (solvedClip != null) EsAudio.Play(solvedClip, transform.position);
            if (gm != null) gm.SetPowerRestored();
            RefreshAll();
        }

        public void RefreshAll()
        {
            int[,] b, r;
            Snapshot(out b, out r);
            var live = LiveDepths(b, r);
            foreach (var n in nodes)
            {
                int key = n.col * rows + n.row;
                bool on = live.ContainsKey(key);
                n.depth = on ? live[key] : 0;
                n.SetLit(on, liveMat, dimMat);
                // The live route reads as one continuous glowing shape, not
                // scattered pips: the whole tile lights. Emitter/receptor keep
                // their amber bodies; blanks stay dark (they never energize).
                // Single-pip leaves (D-183) wear their own teal, dark or live,
                // so the ends-to-plug are identifiable before anything lights.
                if (n.tileRenderer != null)
                {
                    if (n.kind == NodeKind.Normal && n.isLeaf)
                        n.tileRenderer.sharedMaterial = on
                            ? (leafLiveMat != null ? leafLiveMat : baseMat)
                            : (leafMat != null ? leafMat : baseMat);
                    else if (n.kind == NodeKind.Normal)
                        n.tileRenderer.sharedMaterial = (on && liveTileMat != null) ? liveTileMat : baseMat;
                }
            }
            if (statusLabel != null)
            {
                if (powerRestored) { statusLabel.text = "MALHA ESTAVEL - ENERGIA RESTAURADA"; statusLabel.color = EsTheme.ScreenOn; }
                else if (live.ContainsKey((cols - 1) * rows + rows / 2)) { statusLabel.text = "RECEPTOR ALIMENTADO - VAZAMENTO NA MALHA"; statusLabel.color = EsTheme.Amber; }
                else { statusLabel.text = "LIGUE O EMISSOR AO RECEPTOR"; statusLabel.color = EsTheme.Warn; }
            }
        }

        void ApplyAllVisuals()
        {
            foreach (var n in nodes) ApplyVisual(n);
            RefreshAll();
        }

        void ApplyVisual(EsHexNode n)
        {
            if (n.spinner != null)
                n.spinner.localRotation = Quaternion.Euler(0f, 0f, n.rotation * 60f);
        }

        void Start()
        {
            // Materials survive as assets; re-assert pip/tile assignment in play.
            ApplyAllVisuals();
        }

        /// <summary>Flow pulse along the live route, emitter-to-receptor. Per-node
        /// phase from BFS depth via MaterialPropertyBlock: no material instances,
        /// no asset mutation (which would leak past play sessions with domain
        /// reload off), and edit-mode tests never tick Update.</summary>
        void Update()
        {
            if (liveMat == null) return;
            if (!_liveEmCached) CacheLiveEmission();
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            float t = Time.time * 4.0f;
            foreach (var n in nodes)
            {
                if (n == null || !n.lit || n.pips == null) continue;
                float s = 0.70f + 0.45f * Mathf.Sin(t - n.depth * 0.9f);
                if (s < 0.15f) s = 0.15f;
                _mpb.SetColor("_EmissionColor", _liveEm * s);
                foreach (var p in n.pips)
                    if (p != null) { p.SetPropertyBlock(_mpb); n.pulsed = true; }
            }
        }

        bool CacheLiveEmission()
        {
            _liveEm = liveMat != null && liveMat.HasProperty("_EmissionColor")
                ? liveMat.GetColor("_EmissionColor") : Color.black;
            _liveEmCached = true;
            return true;
        }

        // ---------------------------------------------------------------- spawn
        EsHexNode SpawnNode(Mesh tile, int c, int r, int baseMask, bool fixedCell, NodeKind kind)
        {
            Vector2 lp = CellLocal(c, r, cellSize);
            var go = new GameObject(string.Format("Node_{0}_{1}", c, r));
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(lp.x, lp.y, 0f);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = tile;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = (kind == NodeKind.Emitter || kind == NodeKind.Receptor) ? srcMat : baseMat;
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = tile;
            mc.convex = true;

            var spin = new GameObject("Spinner");
            spin.transform.SetParent(go.transform, false);
            spin.transform.localPosition = Vector3.zero;

            var node = go.AddComponent<EsHexNode>();
            node.grid = this;
            node.col = c; node.row = r;
            node.baseMask = baseMask & 63;
            node.rotation = 0;
            node.isFixed = fixedCell;
            node.kind = kind;
            node.spinner = spin.transform;
            node.tileRenderer = mr;
            int ports = 0;
            for (int d = 0; d < 6; d++) if ((node.baseMask & (1 << d)) != 0) ports++;
            node.isLeaf = kind == NodeKind.Normal && ports == 1;

            // port pips ride the spinner so geometry always matches the mask
            node.pips = new List<Renderer>();
            for (int d = 0; d < 6; d++)
            {
                if ((node.baseMask & (1 << d)) == 0) continue;
                float a = d * 60f * Mathf.Deg2Rad;
                var pip = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pip.name = "Pip" + d;
                pip.transform.SetParent(spin.transform, false);
                pip.transform.localPosition = new Vector3(Mathf.Cos(a) * cellSize * 0.60f,
                                                          Mathf.Sin(a) * cellSize * 0.60f, 0.055f);
                pip.transform.localScale = Vector3.one * cellSize * 0.30f;
                Object.DestroyImmediate(pip.GetComponent<Collider>());
                var pr = pip.GetComponent<Renderer>();
                pr.sharedMaterial = dimMat;
                node.pips.Add(pr);
            }
            return node;
        }

        static Mesh HexTileMesh(float radius, float depth)
        {
            var m = new Mesh { name = "HexTile" };
            var v = new Vector3[14];
            v[0] = new Vector3(0f, 0f, depth * 0.5f);
            v[7] = new Vector3(0f, 0f, -depth * 0.5f);
            for (int i = 0; i < 6; i++)
            {
                float a = (30f + i * 60f) * Mathf.Deg2Rad;
                float x = Mathf.Cos(a) * radius, y = Mathf.Sin(a) * radius;
                v[1 + i] = new Vector3(x, y, depth * 0.5f);
                v[8 + i] = new Vector3(x, y, -depth * 0.5f);
            }
            var t = new List<int>();
            for (int i = 0; i < 6; i++)
            {
                int n = (i + 1) % 6;
                t.Add(0); t.Add(1 + i); t.Add(1 + n);          // top fan
                t.Add(7); t.Add(8 + n); t.Add(8 + i);          // bottom fan
                t.Add(1 + i); t.Add(8 + i); t.Add(8 + n);      // side
                t.Add(1 + i); t.Add(8 + n); t.Add(1 + n);      // side
            }
            m.vertices = v;
            m.triangles = t.ToArray();
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }

    public enum NodeKind { Normal, Emitter, Receptor, Blank }
}
