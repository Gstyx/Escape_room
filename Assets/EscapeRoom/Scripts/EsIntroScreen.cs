using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeRoom
{
    /// <summary>The opening card. The room is visible behind it and inert; INICIAR is what
    /// starts the game.
    ///
    /// <para><b>It lives on the CANVAS, not on the panel it hides.</b> <c>Dismiss()</c> deactivates
    /// <see cref="introPanel"/> and then starts a coroutine that boots the terminal. The first
    /// version put this component on the panel, so the coroutine was started on a GameObject it
    /// had just switched off - and a coroutine on an inactive GameObject never advances. The boot
    /// silently did not happen: the card vanished, the cursor locked, the room stayed
    /// <c>Offline</c> with an empty timer and no objective line. Measured, not guessed:
    /// <c>Phase=Offline, IsRunning=False, TimerText=""</c> with <c>frameCount=11156</c>, so the
    /// coroutine was not late - it was dead. The overlay canvas is never deactivated, so hosting
    /// this there makes the ordering harmless rather than load-bearing.</para>
    ///
    /// <para><b>Why the world is inert for free.</b> <see cref="EsCrosshairPointer"/> returns early
    /// when <c>Cursor.lockState != Locked</c> - every puzzle click in this game is dispatched by
    /// the crosshair, so leaving the cursor unlocked while this panel is up means the puzzles
    /// cannot be interacted with. No puzzle had to be switched off, and nothing here can drift out
    /// of sync with the pointer, because the pointer is the thing being obeyed.</para>
    ///
    /// <para><b>What that does NOT cover, and both had to be handled.</b> Two systems read input
    /// without consulting the cursor lock, and each one produced a specific defect:
    /// <see cref="EsFirstPersonCameraRig"/> reads <c>Mouse.delta</c> unconditionally, so moving
    /// the mouse over the card rotated the camera behind it; and
    /// <c>StarterAssetsInputs.OnApplicationFocus</c> re-locks the cursor whenever the app
    /// regains focus, so alt-tabbing away and back during the card left the player with a locked
    /// cursor and a visible INICIAR that does nothing when clicked. The camera is gated by a
    /// static flag; the cursor is re-asserted every frame, which wins that race by construction
    /// instead of by ordering.</para>
    ///
    /// <para><b>INICIAR also boots the terminal.</b> Not decoration: the reserve is started by
    /// <c>BootRoutine</c>, and the terminal only paints its timer while Online
    /// (<c>SetPhaseVisuals</c> writes an empty string when Offline). Starting the clock without
    /// booting the terminal leaves the player with no visible countdown until they walk to the
    /// wall and find FORCAR REINICIO - the one thing the card is supposed to have explained.</para></summary>
    public class EsIntroScreen : MonoBehaviour
    {
        [Header("UI (Screen Space Overlay)")]
        public GameObject introPanel;
        public Button startButton;

        [Header("Refs")]
        public TerminalController terminal;

        /// <summary>True from the moment the player dismisses the card. Guards the cursor
        /// re-assertion so a late frame cannot unlock the mouse after the game has begun.</summary>
        public bool Dismissed { get; private set; }

        MonoBehaviour _starterInputs;   // StarterAssetsInputs, reached by reflection (asmdef)
        bool _starterLockedFieldFound;

        void Awake()
        {
            // The card is up before anything else can consume a click, so this ordering is the
            // safe one: gate first, release later.
            EsFirstPersonCameraRig.LookFrozen = true;

            // Let the Starter Assets focus handler know we are NOT playing yet, so alt-tab away
            // and back does not hand the player a locked cursor over a clickable button.
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
            {
                if (mb == null || mb.GetType().Name != "StarterAssetsInputs") continue;
                _starterInputs = mb;
                _starterLockedFieldFound = mb.GetType().GetField("cursorLocked") != null;
                break;
            }

            if (startButton != null) startButton.onClick.AddListener(Dismiss);
            if (introPanel != null) introPanel.SetActive(true);
            UnlockCursor();
        }

        void OnDestroy()
        {
            // Static flag, so it MUST be cleared here. A scene reload while the card is up (the
            // end panel's REINICIAR) would otherwise leave LookFrozen true with no intro left to
            // clear it, and the camera would be dead for the whole next run - which looks exactly
            // like a broken game and never logs an error.
            EsFirstPersonCameraRig.LookFrozen = false;
            if (_starterLockedFieldFound && _starterInputs != null)
                _starterInputs.GetType().GetField("cursorLocked").SetValue(_starterInputs, true);
        }

        void Update()
        {
            if (Dismissed || introPanel == null || !introPanel.activeSelf) return;

            // THE point of this loop: re-assert the unlocked cursor every frame. The Starter
            // Assets focus callback can relock it at any moment (alt-tab back into the game), and
            // "one frame late" is enough for the player to click INICIAR into a locked cursor and
            // conclude the button is broken. Holding the state here beat trying to win a race
            // against a callback I do not control.
            UnlockCursor();

            // Zero the movement input too. Walking around behind a title card is not a crash,
            // but it does mean the player's first step of the game is one they took before they
            // agreed to start - and the spawn pose is deliberately aimed at the terminal.
            if (_starterLockedFieldFound && _starterInputs != null)
                _starterInputs.GetType().GetField("move").SetValue(_starterInputs, Vector2.zero);
        }

        void UnlockCursor()
        {
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
            if (_starterLockedFieldFound && _starterInputs != null)
                _starterInputs.GetType().GetField("cursorLocked").SetValue(_starterInputs, false);
        }

        public void Dismiss()
        {
            if (Dismissed) return;
            Dismissed = true;

            if (introPanel != null) introPanel.SetActive(false);
            if (startButton != null) startButton.onClick.RemoveListener(Dismiss);

            EsFirstPersonCameraRig.LookFrozen = false;

            // Hand the cursor to the game, and tell the Starter Assets handler to keep it locked
            // from now on - otherwise the FIRST alt-tab after INICIAR would leave the mouse free.
            if (_starterLockedFieldFound && _starterInputs != null)
                _starterInputs.GetType().GetField("cursorLocked").SetValue(_starterInputs, true);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            // Deferred by one frame. ForceReboot reads TerminalController.Phase, and Unity calls
            // Start() in an undefined order: if the terminal's Start() has not run yet it would
            // overwrite Online with Offline one frame later and the reboot would be lost. Yielding
            // first makes every Start() in the scene settled, so there is nothing left to clobber.
            StartCoroutine(BootAfterSettle());
        }

        IEnumerator BootAfterSettle()
        {
            yield return null;
            if (terminal != null) terminal.ForceReboot();
        }
    }
}
