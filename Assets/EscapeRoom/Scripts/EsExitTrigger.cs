using UnityEngine;

namespace EscapeRoom
{
    /// <summary>The win, moved past the doorway (D-230). Fires once when the player walks
    /// through the open blast door onto the deck.
    ///
    /// <para><b>Tag, not proximity.</b> Dropped plates have leashes and can end up past the
    /// doorway; a distance check would let a kicked plate win the game. The capsule carries
    /// tag <c>Player</c>, items do not, so only the player counts.</para>
    ///
    /// <para><b>Door-open required.</b> The bulkhead and the closed leaf physically seal the
    /// opening, so nothing legitimately arrives here with the door shut - but a teleported
    /// test capsule (or a future hole in the wall) must not win either. Both refs are
    /// serialized, set by the builder; gm falls back to the Instance singleton.</para>
    ///
    /// <para><b>One shot.</b> <c>EscapeGameManager.Win()</c> already refuses repeats via
    /// <c>_finished</c>, and this flag stops a second call from ever reaching it - the
    /// trigger volume stays live under the player's feet after the win.</para></summary>
    public class EsExitTrigger : MonoBehaviour
    {
        public BlastDoor door;
        public EscapeGameManager gm;

        bool _fired;

        void OnTriggerEnter(Collider other)
        {
            TryWin(other);
        }

        /// <summary>Public so a self test can drive the real routine with the real capsule
        /// collider instead of a copy - the FirstSolidHit convention (PlayerInteractor).</summary>
        public void TryWin(Collider other)
        {
            if (_fired) return;
            if (other == null || !other.CompareTag("Player")) return;
            if (door != null && !door.IsOpen) return;
            var g = gm != null ? gm : EscapeGameManager.Instance;
            if (g == null) return;
            _fired = true;
            g.Win();
        }

        /// <summary>Test hook.</summary>
        public void DebugReset()
        {
            _fired = false;
        }
    }
}
