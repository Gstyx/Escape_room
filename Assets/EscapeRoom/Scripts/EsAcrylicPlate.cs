using UnityEngine;

namespace EscapeRoom
{
    /// <summary>One acrylic secret-share plate (Puzzle 3). A 4x4 matrix of
    /// opaque (1) / transparent (0) cells. Rotation is stepped 90 deg clockwise;
    /// Rotated() maps the rotated view back onto the base matrix with the same
    /// convention the builder and the self test use:
    /// new[r][c] = old[3-c][r].
    ///
    /// Rotation after seating happens through the reader's own GIRAR buttons,
    /// never by clicking the plate itself: a seated plate is still a
    /// GrabbableItem, and the interactor's free-hand branch picks grabbables
    /// up before devices ever see the click.</summary>
    public class EsAcrylicPlate : MonoBehaviour
    {
        public int plateId;
        public int[] baseCells = new int[16];
        public int rotation;
        public Transform cellsRoot;

        /// <summary>Base (r,c) that the rotated view cell (r,c) shows.</summary>
        public int Rotated(int r, int c)
        {
            int rr = r, cc = c;
            for (int k = 0; k < ((rotation % 4) + 4) % 4; k++)
            {
                int nr = 3 - cc;
                int ncc = rr;
                rr = nr; cc = ncc;
            }
            return baseCells[rr * 4 + cc];
        }

        public static int[,] RotatedMatrix(int[] baseCells, int rot)
        {
            var m = new int[4, 4];
            int k = ((rot % 4) + 4) % 4;
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                {
                    int rr = r, cc = c;
                    for (int i = 0; i < k; i++)
                    {
                        int nr = 3 - cc;
                        int ncc = rr;
                        rr = nr; cc = ncc;
                    }
                    m[r, c] = baseCells[rr * 4 + cc];
                }
            return m;
        }

        public static int[] OrCombine(int[] a, int ra, int[] b, int rb, int[] c, int rc)
        {
            var ma = RotatedMatrix(a, ra);
            var mb = RotatedMatrix(b, rb);
            var mc = RotatedMatrix(c, rc);
            var res = new int[16];
            for (int r = 0; r < 4; r++)
                for (int cc = 0; cc < 4; cc++)
                    res[r * 4 + cc] = ma[r, cc] | mb[r, cc] | mc[r, cc];
            return res;
        }

        public void Rotate()
        {
            rotation = (rotation + 1) % 4;
            ApplyVisual();
        }

        /// <summary>Test hook: set rotation without sound or reader traffic.</summary>
        public void SetRotationSilent(int rot)
        {
            rotation = ((rot % 4) + 4) % 4;
            ApplyVisual();
        }

        public void ApplyVisual()
        {
            if (cellsRoot != null)
                cellsRoot.localRotation = Quaternion.Euler(0f, 0f, -90f * rotation);
        }

        public string PromptFor(GrabbableItem held)
        {
            return "Placa de acrilico " + (plateId + 1) + " - rotacao " + (rotation * 90) + " deg";
        }
    }
}
