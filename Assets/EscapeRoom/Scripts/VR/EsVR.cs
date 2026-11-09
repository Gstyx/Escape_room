using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EscapeRoom
{
    /// <summary>Shared VR state, read by the desktop interaction hub so it can take its ray and its
    /// click from a controller instead of the screen centre.
    ///
    /// <para><b>Why the hub is reused instead of replaced.</b> Every puzzle in this room is reached
    /// through two routines: <see cref="PlayerInteractor"/> (3D items and sockets) and
    /// <see cref="EsCrosshairPointer"/> (world-space canvas buttons). Both already answer the
    /// question "what is the player aiming at, and did they click". In VR only the two inputs change
    /// - the ray comes from the hand, the click from the trigger - so the puzzles, their guards and
    /// their self tests stay exactly as they are (D-234).</para>
    ///
    /// <para><b>Desktop is untouched when no headset is running.</b> <see cref="Active"/> only goes
    /// up once <see cref="EsVRRig"/> has confirmed an XR display; until then every branch below
    /// reads false and the mouse path runs as before.</para></summary>
    public static class EsVR
    {
        /// <summary>True while a headset is rendering and the VR rig owns the player.</summary>
        public static bool Active { get; internal set; }

        /// <summary>Aim pose of the hand that last pressed the trigger. Its forward is the laser.</summary>
        public static Transform AimOrigin { get; internal set; }

        /// <summary>Where a picked-up item is carried: a point just in front of the active hand.</summary>
        public static Transform HoldPoint { get; internal set; }

        /// <summary>The canvas element the laser is on this frame, or null. When set,
        /// <see cref="PlayerInteractor"/> leaves the press to the UI - the same "UI wins" rule the
        /// desktop path applies through <c>IsPointerOverGameObject</c>.</summary>
        public static GameObject UiHovered { get; internal set; }

        /// <summary>True while a full-screen card (intro, end) floats in front of the player. The
        /// world is inert behind it, which on desktop is what the unlocked cursor guarantees.</summary>
        public static bool ModalOpen { get; internal set; }

        /// <summary>The former overlay cards (intro, end) that carry buttons. While one is open,
        /// only these canvases answer the laser.</summary>
        static readonly HashSet<Canvas> _modalCanvases = new HashSet<Canvas>();

        public static bool IsModalCanvas(Canvas root) { return root != null && _modalCanvases.Contains(root); }

        internal static void RegisterModal(Canvas root) { if (root != null) _modalCanvases.Add(root); }

        static int _pressFrame = -1;

        /// <summary>Interact pressed (trigger or grip, either hand) on this frame.</summary>
        public static bool PressedThisFrame => Active && _pressFrame == Time.frameCount;

        internal static void MarkPress() { _pressFrame = Time.frameCount; }

        /// <summary>Set by the rig when the active hand is TOUCHING something it can use (D-236):
        /// the ray then runs from just behind the touch point into it, so "grab what my hand is on"
        /// goes through the same hub, guards and prompts as the laser.</summary>
        public static bool TouchActive { get; internal set; }
        static Ray _touchRay;
        internal static void SetTouch(bool on, Ray r) { TouchActive = on; _touchRay = r; }

        public static Ray AimRay => TouchActive ? _touchRay
            : AimOrigin != null
                ? new Ray(AimOrigin.position, AimOrigin.forward)
                : new Ray(Vector3.zero, Vector3.forward);

        // Statics survive play sessions here (scene and domain reload are disabled, see AGENTS.md),
        // so they are cleared before every run instead of trusted to start empty.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = false;
            AimOrigin = null;
            HoldPoint = null;
            UiHovered = null;
            ModalOpen = false;
            TouchActive = false;
            _modalCanvases.Clear();
            _pressFrame = -1;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        /// <summary>The rig is created at runtime, not saved into the scene: the scene stays
        /// reproducible from <c>Build Level</c>, and REINICIAR (a scene reload) gets a fresh rig.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SpawnRig();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) { SpawnRig(); }

        static void SpawnRig()
        {
            if (Object.FindAnyObjectByType<PlayerInteractor>() == null) return;   // not the game scene
            if (Object.FindAnyObjectByType<EsVRRig>() != null) return;
            new GameObject("EsVRRig").AddComponent<EsVRRig>();
        }
    }
}
