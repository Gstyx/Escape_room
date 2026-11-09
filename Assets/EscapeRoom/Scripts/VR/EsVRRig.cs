using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR;
using TrackedPoseDriver = UnityEngine.InputSystem.XR.TrackedPoseDriver;
using XRDev = UnityEngine.XR.InputDevice;
using XRDevs = UnityEngine.XR.InputDevices;
using XRUse = UnityEngine.XR.CommonUsages;

namespace EscapeRoom
{
    /// <summary>Turns the desktop first-person player into a VR player, at runtime, only when a
    /// headset is actually rendering (D-234).
    ///
    /// <para><b>What it takes over.</b> The Starter Assets controller and
    /// <see cref="EsFirstPersonCameraRig"/> are switched off: both drive the camera from the mouse,
    /// and in VR the head IS the camera. The main camera is re-parented under a tracking origin and
    /// driven by a <see cref="TrackedPoseDriver"/>. The <c>CharacterController</c> capsule stays
    /// the body - it still collides, still carries tag <c>Player</c> for the exit trigger - and is
    /// kept under the head every frame.</para>
    ///
    /// <para><b>What it does NOT touch.</b> No puzzle script knows VR exists. The hub reads
    /// <see cref="EsVR"/> for its ray and click; this rig feeds that state and draws the laser.</para>
    ///
    /// <para><b>Controls.</b> Trigger or grip: interact with what the hand touches, else with what the laser points at (D-236); the hand that pressed becomes the pointer. WASD / Q R on the PC keyboard also walk / turn.
    /// Left stick: walk. Right stick: snap turn. Left menu button held 2 s, or F5 on the PC keyboard:
    /// restart for the next visitor. F6 or right stick click: recentre.</para>
    ///
    /// <para><b>Execution order -200</b>, so the press, the hand and the UI target are settled before
    /// <see cref="PlayerInteractor"/> reads them in its own Update.</para></summary>
    [DefaultExecutionOrder(-200)]
    public class EsVRRig : MonoBehaviour
    {
        [Header("Locomocao")]
        // D-237: the fair booths have no room to walk, so the visitor stands still and ALL travel is
        // the left stick. 2,0 m/s crosses the 24 m room in ~12 s (1,6 took 15 s); 45 deg snaps mean
        // fewer flicks for someone who cannot turn their body much in a small booth.
        public float moveSpeed = 2.0f;
        public float snapTurnDegrees = 45f;
        public float stickDeadzone = 0.2f;

        [Header("Laser")]
        public float laserLength = 6f;
        /// <summary>Angular forgiveness for canvas buttons, the VR form of
        /// <see cref="EsCrosshairPointer.looseRadiusPixels"/>: 260 px at 1080p is about 14 deg on
        /// the desktop camera. Hands shake more than a mouse and less than that is enough.</summary>
        public float looseAngle = 4f;
        /// <summary>Puzzle input (the keypad) keeps a tight cone, for the same reason as on desktop:
        /// forgiveness that reaches the neighbouring digit types the answer for the player.</summary>
        public float snapAngle = 1.2f;

        [Header("Painel flutuante (intro / fim)")]
        public float panelDistance = 1.4f;
        public float panelScale = 0.0008f;          // 1920 canvas px -> 1,54 m at 1,4 m

        [Header("Operador da feira")]
        public float restartHoldSeconds = 2f;

        // --- scene refs
        Camera _cam;
        CharacterController _body;
        Transform _bodyT;
        PlayerInteractor _interactor;
        EsCrosshairPointer _pointer;
        float _spawnYaw;

        // --- rig
        Transform _origin, _offset;
        Hand _left, _right, _active;
        Hand[] _hands;
        float _vy;
        bool _turnLatched;
        float _menuHeld;
        bool _built;
        bool _followHead;

        // --- ui
        readonly List<FloatingPanel> _panels = new List<FloatingPanel>();
        Text _promptLabel;
        Canvas _promptCanvas;
        Text _wristTime;
        Canvas _wristCanvas;

        // --- input
        readonly List<InputAction> _actions = new List<InputAction>();

        class Hand
        {
            public XRNode node;
            public float outward;                       // -1 left, +1 right: the back of the hand
            public Transform grip, aim, hold;
            public InputAction trigger, gripBtn, triggerAxis, gripAxis, stick, menuBtn, stickClick;
            public InputAction gripPos, gripRot, aimPos, aimRot;
            public LineRenderer laser;
            public Transform dot;
            public Renderer dotRenderer;
            // D-236: second input path (UnityEngine.XR.InputDevices) and its edge detection.
            public bool legacyTrigger, legacyGrip, legacyMenu, legacyClick;
            public bool tracked;
            public float curlIndex, curlFist;
            public readonly List<Transform> fingerJoints = new List<Transform>();
            public readonly List<float> jointWeight = new List<float>();
            public Transform indexRoot, indexMid;
            public Renderer[] skin;
            public bool touching;
        }

        static readonly Color GloveColor = new Color32(0xE6, 0xEE, 0xF2, 0xFF);
        static readonly Color GloveTouch = new Color32(0x6C, 0xF0, 0x9C, 0xFF);
        Material _gloveMat, _gloveTouchMat, _cuffMat;
        float _xrSince = -1f;
        Canvas _warnCanvas;
        Text _warnText;

        class FloatingPanel
        {
            public Canvas canvas;
            public RectTransform rt;
            public bool wasOpen;
            public bool interactive;
        }

        // ------------------------------------------------------------------ lifecycle

        IEnumerator Start()
        {
            // D-236: wait for the headset WITHOUT a deadline. The old 4 s timeout destroyed the rig
            // when Quest Link / SteamVR took longer to bring the session up - the headset then
            // showed the desktop game: no hands, no stick walking, mouse-only clicks. Polling is
            // free and the desktop game is untouched while no headset is rendering.
            bool told = false;
            float t0 = Time.realtimeSinceStartup;
            while (!XrRunning())
            {
                if (!told && Time.realtimeSinceStartup - t0 > 4f)
                {
                    told = true;
                    Debug.Log("[VR] Nenhum headset ativo ainda - jogo segue no modo desktop (o rig VR monta se o headset ligar).");
                }
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (!Build())
            {
                Destroy(gameObject);
                yield break;
            }
            // Tracking can report the origin for a few frames before the first real head pose.
            yield return new WaitForSecondsRealtime(0.35f);
            Recentre();
            yield return new WaitForSecondsRealtime(0.8f);
            Recentre();
            // Only now does the body follow the head. Before the first real pose the capsule would
            // chase wherever the visitor stands in the play area and leave the spawn behind.
            _followHead = true;
        }

        static bool XrRunning()
        {
            if (XRSettings.isDeviceActive) return true;
            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            foreach (var d in displays) if (d.running) return true;
            return false;
        }

        void OnDestroy()
        {
            Application.onBeforeRender -= OnBeforeRender;
            foreach (var a in _actions) { a.Disable(); a.Dispose(); }
            _actions.Clear();
            if (_built)
            {
                EsVR.Active = false;
                EsVR.AimOrigin = null;
                EsVR.HoldPoint = null;
                EsVR.UiHovered = null;
                EsVR.ModalOpen = false;
            }
        }

        bool Build()
        {
            _cam = Camera.main;
            _interactor = Object.FindAnyObjectByType<PlayerInteractor>();
            _pointer = Object.FindAnyObjectByType<EsCrosshairPointer>();
            foreach (var cc in Object.FindObjectsByType<CharacterController>())
                if (_body == null || cc.CompareTag("Player")) _body = cc;
            if (_cam == null || _body == null || _interactor == null)
            {
                Debug.LogError("[VR] Cena sem camera/capsula/interactor - rig VR nao montado.");
                return false;
            }
            _bodyT = _body.transform;
            _spawnYaw = _bodyT.eulerAngles.y;

            // D-236: in a headset the PC window is often NOT focused (Link dashboard, SteamVR,
            // the operator clicking elsewhere). Player Settings had runInBackground off, which
            // pauses the game, and the Input System by default drops device input on focus loss.
            Application.runInBackground = true;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
            InputSystem.settings.editorInputBehaviorInPlayMode =
                InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif

            _gloveMat = EsTheme.Lit(GloveColor, 0f, 0.45f);
            _gloveTouchMat = EsTheme.Glow(GloveTouch, 1.2f);
            _cuffMat = EsTheme.Lit(new Color32(0x2C, 0x5E, 0x6E, 0xFF), 0.1f, 0.5f);

            DisableDesktopControl();
            BuildOrigin();
            _left = BuildHand(XRNode.LeftHand, "LeftHand");
            _right = BuildHand(XRNode.RightHand, "RightHand");
            _hands = new[] { _left, _right };
            SetActiveHand(_right);
            BuildInput();
            IgnoreItemsVsBody();
            ConvertOverlays();
            BuildPromptLabel();
            BuildWrist();
            BuildWarning();
            Application.onBeforeRender += OnBeforeRender;
            _xrSince = Time.realtimeSinceStartup;
            LogDevices();

            // The UI module would otherwise turn the XR trigger into a second, mouse-positioned
            // click. Same rule as the locked desktop cursor: one pointer, not two.
            var es = EventSystem.current;
            if (es != null)
                foreach (var m in es.GetComponents<BaseInputModule>()) m.enabled = false;

            EsVR.Active = true;
            _built = true;
            Debug.Log("[VR] Rig VR ativo: " + XRSettings.loadedDeviceName);
            return true;
        }

        void DisableDesktopControl()
        {
            // Reached by type name, as EsIntroScreen does: the Starter Assets live in their own asmdef.
            foreach (var mb in _bodyT.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                string n = mb.GetType().Name;
                if (n == "FirstPersonController" || n == "StarterAssetsInputs" || n == "PlayerInput")
                    mb.enabled = false;
            }
            var rig = _cam.GetComponent<EsFirstPersonCameraRig>();
            if (rig != null) rig.enabled = false;
            foreach (var mb in _cam.GetComponents<MonoBehaviour>())
                if (mb != null && mb.GetType().Name.Contains("CinemachineBrain")) mb.enabled = false;

            // Crosshair and aim label are screen-centre HUDs; a screen centre does not exist in a
            // headset. The laser and the floating label below replace them.
            foreach (var x in Object.FindObjectsByType<EsCrosshair>()) x.gameObject.SetActive(false);
            foreach (var x in Object.FindObjectsByType<EsAimPrompt>()) x.gameObject.SetActive(false);
        }

        void BuildOrigin()
        {
            _origin = new GameObject("XR Origin").transform;
            _origin.SetPositionAndRotation(FeetPosition(), Quaternion.Euler(0f, _spawnYaw, 0f));
            _offset = new GameObject("Camera Offset").transform;
            _offset.SetParent(_origin, false);

            // Floor origin: the visitor's own height is the eye height. Without floor support the
            // headset reports from where it started, so lift it to a standing adult's eyes.
            bool floor = false;
            var subs = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subs);
            foreach (var s in subs)
                if (s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor)) floor = true;
            _offset.localPosition = floor ? Vector3.zero : new Vector3(0f, 1.6f, 0f);

            _cam.transform.SetParent(_offset, false);
            _cam.transform.localPosition = Vector3.zero;
            _cam.transform.localRotation = Quaternion.identity;
            _cam.nearClipPlane = 0.03f;

            var tpd = _cam.gameObject.AddComponent<TrackedPoseDriver>();
            tpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            tpd.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            tpd.ignoreTrackingState = true;
            tpd.positionInput = new InputActionProperty(Value("HeadPos", "Vector3",
                "<XRHMD>/centerEyePosition", "<XRHMD>/devicePosition"));
            tpd.rotationInput = new InputActionProperty(Value("HeadRot", "Quaternion",
                "<XRHMD>/centerEyeRotation", "<XRHMD>/deviceRotation"));
        }

        Hand BuildHand(XRNode node, string side)
        {
            var h = new Hand { node = node, outward = node == XRNode.LeftHand ? -1f : 1f };
            string c = "<XRController>{" + side + "}/";

            // D-236: poses are written by hand every frame (UpdateHandPoses), from the Input
            // System when it has the controller and from UnityEngine.XR.InputDevices otherwise.
            // Relying on one TrackedPoseDriver path left the hands lying on the floor at the
            // origin whenever that path had no device - which is what "no hands" looked like.
            h.grip = new GameObject(side + " Grip").transform;
            h.grip.SetParent(_offset, false);
            h.aim = new GameObject(side + " Aim").transform;
            h.aim.SetParent(_offset, false);
            h.gripPos = Value(side + "GripPos", "Vector3", c + "devicePosition", c + "gripPosition");
            h.gripRot = Value(side + "GripRot", "Quaternion", c + "deviceRotation", c + "gripRotation");
            // The aim (pointer) pose is the one OpenXR defines for rays: it points where the
            // controller visibly points, while the grip pose follows the fist and aims ~40 deg low.
            h.aimPos = Value(side + "AimPos", "Vector3", c + "pointerPosition");
            h.aimRot = Value(side + "AimRot", "Quaternion", c + "pointerRotation");

            h.hold = new GameObject(side + " Hold").transform;
            h.hold.SetParent(h.grip, false);
            h.hold.localPosition = new Vector3(0f, 0f, 0.07f);

            BuildHandModel(h, side);
            BuildLaser(h);

            h.trigger = Button(side + "Trigger", c + "{TriggerButton}", c + "triggerPressed");
            h.gripBtn = Button(side + "GripBtn", c + "{GripButton}", c + "gripPressed");
            h.triggerAxis = Value(side + "TriggerAxis", "Axis", c + "{Trigger}", c + "trigger");
            h.gripAxis = Value(side + "GripAxis", "Axis", c + "{Grip}", c + "grip");
            h.stick = Value(side + "Stick", "Vector2", c + "{Primary2DAxis}", c + "thumbstick", c + "joystick", c + "trackpad");
            h.menuBtn = Button(side + "Menu", c + "{MenuButton}", c + "menu", c + "menuButton");
            h.stickClick = Button(side + "StickClick", c + "{Primary2DAxisClick}", c + "thumbstickClicked");
            return h;
        }

        void BuildInput()
        {
            // Sticks, menu and recentre live on the hands now (both input paths, see Hand).
        }

        // ------------------------------------------------------------------ input, two paths

        static XRDev Legacy(Hand h) { return XRDevs.GetDeviceAtXRNode(h.node); }

        static bool HasControl(InputAction a) { return a != null && a.controls.Count > 0; }

        Vector2 Stick(Hand h)
        {
            Vector2 v = HasControl(h.stick) ? h.stick.ReadValue<Vector2>() : Vector2.zero;
            if (v.sqrMagnitude > 0.0025f) return v;
            Vector2 l;
            var d = Legacy(h);
            if (d.isValid && d.TryGetFeatureValue(XRUse.primary2DAxis, out l)) return l;
            return v;
        }

        static float Axis(InputAction a, XRDev d, UnityEngine.XR.InputFeatureUsage<float> u)
        {
            float v = HasControl(a) ? a.ReadValue<float>() : 0f;
            float l;
            if (d.isValid && d.TryGetFeatureValue(u, out l)) v = Mathf.Max(v, l);
            return v;
        }

        /// <summary>Edge from the legacy XR path; the Input System edge is OR-ed in by the caller.</summary>
        static bool LegacyEdge(XRDev d, UnityEngine.XR.InputFeatureUsage<bool> u, ref bool last)
        {
            bool now = false;
            if (d.isValid) d.TryGetFeatureValue(u, out now);
            bool edge = now && !last;
            last = now;
            return edge;
        }

        void UpdateHandPoses()
        {
            if (_hands == null) return;
            foreach (var h in _hands)
            {
                Vector3 gp = Vector3.zero, ap = Vector3.zero;
                Quaternion gr = Quaternion.identity, ar = Quaternion.identity;
                bool gripOk = false, aimOk = false;

                if (HasControl(h.gripPos))
                {
                    gp = h.gripPos.ReadValue<Vector3>();
                    gr = h.gripRot.ReadValue<Quaternion>();
                    gripOk = gp != Vector3.zero;
                }
                if (HasControl(h.aimPos))
                {
                    ap = h.aimPos.ReadValue<Vector3>();
                    ar = h.aimRot.ReadValue<Quaternion>();
                    aimOk = ap != Vector3.zero;
                }
                var d = Legacy(h);
                if (!gripOk && d.isValid
                    && d.TryGetFeatureValue(XRUse.devicePosition, out gp)
                    && d.TryGetFeatureValue(XRUse.deviceRotation, out gr))
                    gripOk = gp != Vector3.zero;
                if (!aimOk && d.isValid
                    && d.TryGetFeatureValue(new UnityEngine.XR.InputFeatureUsage<Vector3>("PointerPosition"), out ap)
                    && d.TryGetFeatureValue(new UnityEngine.XR.InputFeatureUsage<Quaternion>("PointerRotation"), out ar))
                    aimOk = ap != Vector3.zero;

                if (gr.x == 0f && gr.y == 0f && gr.z == 0f && gr.w == 0f) gr = Quaternion.identity;
                if (ar.x == 0f && ar.y == 0f && ar.z == 0f && ar.w == 0f) ar = Quaternion.identity;

                h.tracked = gripOk;
                if (gripOk) h.grip.SetLocalPositionAndRotation(gp, gr);
                if (aimOk) h.aim.SetLocalPositionAndRotation(ap, ar);
                else if (gripOk)
                    // No aim pose on this runtime: point from the fist, tilted up to where the
                    // controller visibly points.
                    h.aim.SetLocalPositionAndRotation(gp, gr * Quaternion.Euler(-35f, 0f, 0f));

                // Untracked (asleep / not paired): keep the hand off the floor and out of sight
                // instead of lying at the origin.
                if (h.skin != null)
                    foreach (var r in h.skin) if (r != null && r.enabled != gripOk) r.enabled = gripOk;
            }
        }

        void OnBeforeRender() { if (_built) UpdateHandPoses(); }

        InputAction Value(string name, string layout, params string[] paths)
        {
            var a = new InputAction(name, InputActionType.Value, expectedControlType: layout);
            foreach (var p in paths) a.AddBinding(p);
            a.Enable();
            _actions.Add(a);
            return a;
        }

        InputAction Button(string name, params string[] paths)
        {
            var a = new InputAction(name, InputActionType.Button);
            foreach (var p in paths) a.AddBinding(p);
            a.Enable();
            _actions.Add(a);
            return a;
        }

        /// <summary>A carried item hangs inside the capsule radius in VR (the hand is 30 cm from the
        /// chest, the capsule is 50 cm wide), so without this the body would be pushed by its own
        /// key. Dropped items are not something to stand on either.</summary>
        void IgnoreItemsVsBody()
        {
            foreach (var g in Object.FindObjectsByType<GrabbableItem>(FindObjectsInactive.Include))
                foreach (var c in g.GetComponentsInChildren<Collider>(true))
                    Physics.IgnoreCollision(_body, c, true);
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            if (!_built) return;

            UpdateHandPoses();
            UpdateHands();
            UpdateTouch();
            UpdateOperator();
            UpdateWarning();
            UpdatePanels();
            UpdateBody();

            // UI first, so the interactor can see that the press belongs to a canvas.
            EsVR.UiHovered = null;
            if (_pointer != null)
                EsVR.UiHovered = _pointer.TickVR(EsVR.AimRay, laserLength, SolidDistance(EsVR.AimRay),
                                                 snapAngle, looseAngle, EsVR.PressedThisFrame);
            if (EsVR.PressedThisFrame) Haptic(_active, 0.35f, 0.05f);
        }

        void LateUpdate()
        {
            if (!_built) return;
            UpdateLaser();
            UpdatePromptLabel();
            UpdateWrist();
        }

        void UpdateHands()
        {
            foreach (var h in _hands)
            {
                var d = Legacy(h);
                // Both edges are always evaluated, so neither path's "last" state goes stale.
                bool lt = LegacyEdge(d, XRUse.triggerButton, ref h.legacyTrigger);
                bool lg = LegacyEdge(d, XRUse.gripButton, ref h.legacyGrip);
                bool pressed = h.trigger.WasPressedThisFrame() || h.gripBtn.WasPressedThisFrame() || lt || lg;

                // Finger curl for the hand model: index follows the trigger, the fist the grip.
                h.curlIndex = Mathf.Lerp(h.curlIndex, Axis(h.triggerAxis, d, XRUse.trigger), 0.5f);
                h.curlFist = Mathf.Lerp(h.curlFist, Axis(h.gripAxis, d, XRUse.grip), 0.5f);
                PoseFingers(h);

                if (!pressed) continue;
                // Switching hands while carrying would teleport the item; the other hand only takes
                // over the pointer when nothing is held.
                if (h != _active && _interactor.Held == null) SetActiveHand(h);
                if (h == _active) EsVR.MarkPress();
            }
        }

        /// <summary>D-236: grab what the hand is ON. Visitors reach for objects before they ever
        /// think of a laser. When the active hand is within reach of something the hub can use, the
        /// aim ray is bent into it, so the same PlayerInteractor branch (pick up / insert / press)
        /// runs - no puzzle learns that hands exist. The other hand takes over the pointer when it
        /// is the one touching and nothing is carried.</summary>
        void UpdateTouch()
        {
            const float reach = 0.13f;
            Ray best = default(Ray);
            Hand bestHand = null;
            float bestD = float.MaxValue;
            foreach (var h in _hands)
            {
                h.touching = false;
                if (!h.tracked || EsVR.ModalOpen) continue;
                if (_interactor.Held != null && h != _active) continue;   // the carrying hand only
                Vector3 palm = h.hold.position;
                foreach (var c in Physics.OverlapSphere(palm, reach, ~0, QueryTriggerInteraction.Collide))
                {
                    if (c == null || c == _body) continue;
                    if (PlayerInteractor.IsHeldPart(_interactor.Held, c.transform)) continue;
                    var g = c.GetComponentInParent<GrabbableItem>();
                    bool usable = (g != null && !g.IsHeld && !g.pickupLocked)
                                  || c.GetComponentInParent<IInteractable>() != null;
                    if (!usable) continue;
                    Vector3 p = c.ClosestPoint(palm);
                    float dist = Vector3.Distance(p, palm);
                    if (dist >= bestD) continue;
                    Vector3 dir = p - palm;
                    if (dir.sqrMagnitude < 1e-6f) dir = (c.bounds.center - palm);
                    if (dir.sqrMagnitude < 1e-6f) dir = h.aim.forward;
                    dir.Normalize();
                    bestD = dist;
                    bestHand = h;
                    best = new Ray(p - dir * 0.25f, dir);
                }
            }
            if (bestHand != null)
            {
                bestHand.touching = true;
                if (bestHand != _active && _interactor.Held == null) SetActiveHand(bestHand);
            }
            EsVR.SetTouch(bestHand != null && bestHand == _active, best);
            foreach (var h in _hands) SetSkin(h, h.touching);
        }

        void SetActiveHand(Hand h)
        {
            _active = h;
            EsVR.AimOrigin = h.aim;
            EsVR.HoldPoint = h.hold;
            foreach (var x in _hands)
                if (x != null && x.laser != null) x.laser.gameObject.SetActive(x == h);
        }

        void UpdateOperator()
        {
            var kb = Keyboard.current;
            bool restart = kb != null && kb.f5Key.wasPressedThisFrame;
            bool menuDown = _left.menuBtn.IsPressed();
            if (!menuDown)
            {
                bool m;
                var d = Legacy(_left);
                if (d.isValid && d.TryGetFeatureValue(XRUse.menuButton, out m)) menuDown = m;
            }
            if (menuDown) _menuHeld += Time.unscaledDeltaTime; else _menuHeld = 0f;
            if (_menuHeld >= restartHoldSeconds) { _menuHeld = -999f; restart = true; }
            if (restart && EscapeGameManager.Instance != null)
            {
                EscapeGameManager.Instance.Restart();
                return;
            }
            bool click = LegacyEdge(Legacy(_right), XRUse.primary2DAxisClick, ref _right.legacyClick);
            if ((kb != null && kb.f6Key.wasPressedThisFrame) || _right.stickClick.WasPressedThisFrame() || click)
                Recentre();
        }

        void UpdateBody()
        {
            float dt = Time.deltaTime;
            Vector3 before = _bodyT.position;

            // 1. Room-scale: the visitor walked physically, so the capsule follows the head. If a
            //    wall stops the capsule, the world is pushed back by the blocked part - the head can
            //    never end up inside geometry the body could not enter.
            Vector3 headDelta = _cam.transform.position - before;
            headDelta.y = 0f;
            if (_followHead && headDelta.sqrMagnitude > 0.000001f)
            {
                _body.Move(headDelta);
                Vector3 moved = _bodyT.position - before;
                moved.y = 0f;
                _origin.position += moved - headDelta;
            }

            // 2. Stick locomotion, relative to where the head looks. Frozen behind a modal card,
            //    as the desktop intro zeroes the Starter Assets move input.
            //    Left stick walks; WASD / arrows on the PC keyboard too, so the operator can walk a
            //    visitor out of a corner and the rig can be tested without a second person.
            Vector2 stick = Stick(_left);
            var kb = Keyboard.current;
            if (kb != null)
            {
                Vector2 k = new Vector2(
                    (kb.dKey.isPressed || kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed || kb.leftArrowKey.isPressed ? 1f : 0f),
                    (kb.wKey.isPressed || kb.upArrowKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed || kb.downArrowKey.isPressed ? 1f : 0f));
                if (k.sqrMagnitude > 0f) stick = k.normalized;
            }
            if (EsVR.ModalOpen) stick = Vector2.zero;
            Vector3 walk = Vector3.zero;
            if (stick.magnitude > stickDeadzone)
            {
                Vector3 fwd = Vector3.ProjectOnPlane(_cam.transform.forward, Vector3.up).normalized;
                Vector3 right = Vector3.Cross(Vector3.up, fwd);
                walk = (fwd * stick.y + right * stick.x) * moveSpeed;
            }

            // 3. Gravity, which the disabled Starter Assets controller used to own.
            if (_body.isGrounded && _vy < 0f) _vy = -1f;
            else _vy += Physics.gravity.y * dt;

            Vector3 p0 = _bodyT.position;
            _body.Move((walk + Vector3.up * _vy) * dt);
            Vector3 step = _bodyT.position - p0;
            _origin.position += new Vector3(step.x, 0f, step.z);
            _origin.position = new Vector3(_origin.position.x, FeetPosition().y, _origin.position.z);

            // 4. Snap turn around the head, so the visitor does not swing through the room.
            float tx = Stick(_right).x;
            // Q / R turn from the keyboard (E is already "interact" on desktop).
            if (kb != null && kb.qKey.isPressed) tx = -1f;
            if (kb != null && kb.rKey.isPressed) tx = 1f;
            if (!_turnLatched && Mathf.Abs(tx) > 0.7f)
            {
                _turnLatched = true;
                _origin.RotateAround(_cam.transform.position, Vector3.up, Mathf.Sign(tx) * snapTurnDegrees);
            }
            else if (_turnLatched && Mathf.Abs(tx) < 0.3f) _turnLatched = false;
        }

        Vector3 FeetPosition()
        {
            Vector3 centre = _bodyT.TransformPoint(_body.center);
            return new Vector3(_bodyT.position.x, centre.y - _body.height * 0.5f * _bodyT.lossyScale.y, _bodyT.position.z);
        }

        /// <summary>Puts the visitor's head over the capsule, facing the spawn direction (the
        /// terminal wall). Run at start and by the operator between visitors.</summary>
        public void Recentre()
        {
            if (_origin == null) return;
            float headYaw = _cam.transform.eulerAngles.y;
            _origin.RotateAround(_cam.transform.position, Vector3.up, _spawnYaw - headYaw);
            Vector3 d = _bodyT.position - _cam.transform.position;
            d.y = 0f;
            _origin.position += d;
            foreach (var p in _panels) if (p.wasOpen) PlacePanel(p);
        }

        /// <summary>Distance to the first solid surface on the laser, so a canvas behind a wall
        /// cannot be clicked through it. The held item and the body do not count.</summary>
        float SolidDistance(Ray ray)
        {
            var hits = Physics.RaycastAll(ray, laserLength, ~0, QueryTriggerInteraction.Ignore);
            float best = laserLength;
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider == _body) continue;
                if (PlayerInteractor.IsHeldPart(_interactor.Held, h.collider.transform)) continue;
                if (h.distance < best) best = h.distance;
            }
            return best;
        }

        void Haptic(Hand h, float amplitude, float seconds)
        {
            if (h == null) return;
            var c = h.node == XRNode.LeftHand
                ? UnityEngine.InputSystem.XR.XRController.leftHand
                : UnityEngine.InputSystem.XR.XRController.rightHand;
            var rumble = c as UnityEngine.InputSystem.XR.XRControllerWithRumble;
            if (rumble != null) { rumble.SendImpulse(amplitude, seconds); return; }
            var d = Legacy(h);
            if (d.isValid) d.SendHapticImpulse(0u, amplitude, seconds);
        }

        // ------------------------------------------------------------------ visuals

        static Material _laserMat;

        static Material LaserMaterial()
        {
            if (_laserMat != null) return _laserMat;
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("UI/Default");
            _laserMat = new Material(sh) { name = "Es_VRLaser" };
            return _laserMat;
        }

        /// <summary>D-236: a light glove instead of the old 4 cm dark box, which vanished against
        /// the dark walls ("o boneco nao tem maos"). Built in grip space: the OpenXR grip pose sits in
        /// the closed fist with +Z out of the front of the fist, so the palm wraps the Z axis and
        /// the back of the hand faces outward (+X right hand, -X left hand). Fingers curl with the
        /// analog trigger (index) and grip (the other three); the glove glows green while it is
        /// touching something usable.</summary>
        void BuildHandModel(Hand h, string side)
        {
            float o = h.outward;
            var root = new GameObject(side + " Model").transform;
            root.SetParent(h.grip, false);
            var skin = new List<Renderer>();

            Part(root, "Palm", new Vector3(o * 0.022f, 0.005f, -0.025f), new Vector3(0.026f, 0.088f, 0.095f), _gloveMat, skin);
            Part(root, "Cuff", new Vector3(o * 0.018f, 0.0f, -0.095f), new Vector3(0.05f, 0.075f, 0.05f), _cuffMat, skin);

            // Four fingers stacked along Y (that is how a hand closes on a controller handle).
            float[] ys = { 0.035f, 0.012f, -0.011f, -0.032f };
            float[] len = { 0.042f, 0.046f, 0.043f, 0.034f };
            for (int i = 0; i < 4; i++)
            {
                var j1 = Joint(root, "Finger" + i, new Vector3(o * 0.022f, ys[i], 0.022f));
                Part(j1, "P", new Vector3(0f, 0f, len[i] * 0.5f), new Vector3(0.02f, 0.019f, len[i]), _gloveMat, skin);
                var j2 = Joint(j1, "J", new Vector3(0f, 0f, len[i]));
                Part(j2, "D", new Vector3(0f, 0f, len[i] * 0.4f), new Vector3(0.018f, 0.017f, len[i] * 0.8f), _gloveMat, skin);
                h.fingerJoints.Add(j1); h.jointWeight.Add(i == 0 ? -1f : 1f);
                h.fingerJoints.Add(j2); h.jointWeight.Add(i == 0 ? -1f : 1f);
                if (i == 0) { h.indexRoot = j1; h.indexMid = j2; }
            }
            // Thumb on top, along the controller's face, slightly inward.
            var t1 = Joint(root, "Thumb", new Vector3(o * 0.004f, 0.05f, -0.03f));
            t1.localRotation = Quaternion.Euler(-25f, -o * 25f, 0f);
            Part(t1, "T", new Vector3(0f, 0f, 0.028f), new Vector3(0.022f, 0.02f, 0.056f), _gloveMat, skin);

            h.skin = skin.ToArray();
        }

        static Transform Joint(Transform parent, string name, Vector3 pos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            return t;
        }

        static void Part(Transform parent, string name, Vector3 pos, Vector3 size, Material mat, List<Renderer> skin)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            // Immediate, not Destroy: a hand collider alive for one frame would be hit by the
            // laser and the body before it went away.
            DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            skin.Add(r);
        }

        /// <summary>Curl toward the palm side (-outward on X), which is a yaw in grip space.</summary>
        void PoseFingers(Hand h)
        {
            for (int i = 0; i < h.fingerJoints.Count; i++)
            {
                bool index = h.jointWeight[i] < 0f;
                float curl = index ? h.curlIndex : Mathf.Max(h.curlFist, 0.15f);
                float deg = (i % 2 == 0 ? 70f : 80f) * curl + 8f;
                h.fingerJoints[i].localRotation = Quaternion.Euler(0f, -h.outward * deg, 0f);
            }
        }

        void SetSkin(Hand h, bool touching)
        {
            if (h.skin == null) return;
            var m = touching ? _gloveTouchMat : _gloveMat;
            foreach (var r in h.skin)
                if (r != null && r.name != "Cuff" && r.sharedMaterial != m) r.sharedMaterial = m;
        }

        // ------------------------------------------------------------------ diagnostics

        /// <summary>Written to Player.log / Editor.log, so a failed headset test tells us WHY.</summary>
        void LogDevices()
        {
            var sb = new System.Text.StringBuilder("[VR] Dispositivos Input System:");
            foreach (var d in InputSystem.devices) sb.Append(" | ").Append(d.layout).Append(" ").Append(string.Join(",", d.usages));
            var xr = new List<XRDev>();
            XRDevs.GetDevices(xr);
            sb.Append("\n[VR] Dispositivos XR:");
            foreach (var d in xr) sb.Append(" | ").Append(d.name).Append(" (").Append(d.characteristics).Append(")");
            Debug.Log(sb.ToString());
        }

        void BuildWarning()
        {
            var go = new GameObject("VR Controller Warning", typeof(RectTransform));
            go.transform.SetParent(_cam.transform, false);
            go.transform.localPosition = new Vector3(0f, -0.15f, 1.2f);
            _warnCanvas = go.AddComponent<Canvas>();
            _warnCanvas.renderMode = RenderMode.WorldSpace;
            _warnCanvas.worldCamera = _cam;
            _warnCanvas.sortingOrder = 50;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(1100f, 260f);
            rt.localScale = Vector3.one * 0.0007f;
            var bg = EsUi.Img(go.transform, "Bg", new Color(0.25f, 0.05f, 0.04f, 0.88f));
            EsUi.Stretch(bg.rectTransform);
            bg.raycastTarget = false;
            _warnText = EsUi.Label(go.transform, "Text",
                "CONTROLES NAO DETECTADOS\nLigue os dois controles (aperte o botao Meta/Oculus)\ne confira se estao pareados no Quest Link / SteamVR.",
                40, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            EsUi.Stretch(_warnText.rectTransform, 16f);
            _warnText.raycastTarget = false;
            go.SetActive(false);
        }

        bool _loggedLate;

        void UpdateWarning()
        {
            if (_warnCanvas == null) return;
            bool any = _left.tracked || _right.tracked;
            bool show = !any && Time.realtimeSinceStartup - _xrSince > 3f;
            if (_warnCanvas.gameObject.activeSelf != show) _warnCanvas.gameObject.SetActive(show);
            if (show && !_loggedLate)
            {
                _loggedLate = true;
                Debug.LogWarning("[VR] Nenhum controle rastreado 3 s apos o rig montar.");
                LogDevices();
            }
            if (any && _loggedLate)
            {
                _loggedLate = false;
                Debug.Log("[VR] Controle(s) rastreado(s): esq=" + _left.tracked + " dir=" + _right.tracked);
            }
        }

        void BuildLaser(Hand h)
        {
            var go = new GameObject("Laser");
            go.transform.SetParent(h.aim, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.SetPosition(0, Vector3.zero);
            lr.SetPosition(1, Vector3.forward);
            lr.widthMultiplier = 0.004f;
            lr.sharedMaterial = LaserMaterial();
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            h.laser = lr;

            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "LaserDot";
            Destroy(dot.GetComponent<Collider>());
            dot.transform.SetParent(go.transform, false);
            dot.transform.localScale = Vector3.one * 0.012f;
            h.dot = dot.transform;
            h.dotRenderer = dot.GetComponent<Renderer>();
            h.dotRenderer.sharedMaterial = LaserMaterial();
            h.dotRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        static readonly Color LaserIdle = new Color(0.85f, 0.92f, 1f, 0.35f);
        static readonly Color LaserArmed = new Color(0.35f, 1f, 0.55f, 0.95f);

        void UpdateLaser()
        {
            if (_active == null || _active.laser == null) return;
            // Touching: the hand itself is the cursor (it glows), the laser would only clutter.
            bool showLaser = !EsVR.TouchActive && _active.tracked;
            if (_active.laser.enabled != showLaser)
            {
                _active.laser.enabled = showLaser;
                _active.dotRenderer.enabled = showLaser;
            }
            if (!showLaser) return;
            Ray ray = EsVR.AimRay;
            float len = SolidDistance(ray);
            if (_pointer != null && EsVR.UiHovered != null && _pointer.VRHitDistance > 0f)
                len = Mathf.Min(len, _pointer.VRHitDistance);

            bool armed = EsVR.UiHovered != null
                         || (!EsVR.ModalOpen && !string.IsNullOrEmpty(_interactor.Prompt)
                             && !_interactor.Prompt.StartsWith("Clique para largar"));
            var c = armed ? LaserArmed : LaserIdle;
            _active.laser.startColor = new Color(c.r, c.g, c.b, c.a * 0.4f);
            _active.laser.endColor = c;
            _active.laser.SetPosition(1, Vector3.forward * len);
            _active.dot.localPosition = Vector3.forward * len;
            _active.dotRenderer.material.color = c;
        }

        // ------------------------------------------------------------------ floating UI

        /// <summary>Screen Space Overlay canvases render nothing in a headset. The intro and end
        /// cards become world-space panels that appear in front of the visitor; buttons on them are
        /// reached by the same laser as everything else.</summary>
        void ConvertOverlays()
        {
            foreach (var cv in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include))
            {
                if (cv == null || !cv.isRootCanvas || cv.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                if (cv.GetComponent<EsCrosshair>() != null || cv.GetComponent<EsAimPrompt>() != null) continue;

                cv.renderMode = RenderMode.WorldSpace;
                cv.worldCamera = _cam;
                var rt = (RectTransform)cv.transform;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(1920f, 1080f);
                rt.localScale = Vector3.one * panelScale;
                var scaler = cv.GetComponent<CanvasScaler>();
                if (scaler != null) scaler.dynamicPixelsPerUnit = 2f;   // crisper legacy Text up close

                var p = new FloatingPanel
                {
                    canvas = cv, rt = rt,
                    interactive = cv.GetComponentInChildren<Selectable>(true) != null
                };
                _panels.Add(p);
                if (p.interactive) EsVR.RegisterModal(cv);
                if (cv.GetComponent<EsIntroScreen>() != null) AddVrControlsHint(cv.GetComponent<EsIntroScreen>());
            }
        }

        void AddVrControlsHint(EsIntroScreen intro)
        {
            if (intro.introPanel == null) return;
            var hint = EsUi.Label(intro.introPanel.transform, "VrControls",
                "VR:  ENCOSTE A MAO ou mire o laser + GATILHO   |   ANALOGICO ESQ. anda   |   DIR. gira",
                30, new Color(0.55f, 0.92f, 0.80f), TextAnchor.MiddleCenter);
            var r = hint.rectTransform;
            r.anchorMin = new Vector2(0f, 0f); r.anchorMax = new Vector2(1f, 0f);
            r.offsetMin = new Vector2(60f, 50f); r.offsetMax = new Vector2(-60f, 130f);
            hint.raycastTarget = false;
        }

        static bool IsOpen(FloatingPanel p)
        {
            if (p.canvas == null || !p.canvas.isActiveAndEnabled) return false;
            foreach (Transform child in p.canvas.transform)
                if (child.gameObject.activeSelf) return true;
            return false;
        }

        void UpdatePanels()
        {
            bool modal = false;
            foreach (var p in _panels)
            {
                bool open = IsOpen(p);
                if (open && !p.wasOpen) PlacePanel(p);
                else if (open) FollowLazily(p);
                p.wasOpen = open;
                if (open && p.interactive) modal = true;
            }
            EsVR.ModalOpen = modal;
        }

        /// <summary>In front of the eyes at reading distance - but never behind a wall the visitor
        /// happens to be facing, or the card would be hidden inside it.</summary>
        void PlacePanel(FloatingPanel p)
        {
            Vector3 head = _cam.transform.position;
            Vector3 fwd = Vector3.ProjectOnPlane(_cam.transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.0001f) fwd = _origin.forward;
            fwd.Normalize();
            float d = panelDistance;
            RaycastHit hit;
            if (Physics.Raycast(head, fwd, out hit, panelDistance + 0.2f, ~0, QueryTriggerInteraction.Ignore)
                && hit.collider != _body)
                d = Mathf.Max(0.6f, hit.distance - 0.15f);
            p.rt.position = head + fwd * d;
            p.rt.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            p.rt.localScale = Vector3.one * panelScale * (d / panelDistance);   // same angular size
        }

        void FollowLazily(FloatingPanel p)
        {
            Vector3 toPanel = Vector3.ProjectOnPlane(p.rt.position - _cam.transform.position, Vector3.up);
            Vector3 look = Vector3.ProjectOnPlane(_cam.transform.forward, Vector3.up);
            if (toPanel.sqrMagnitude < 0.0001f || look.sqrMagnitude < 0.0001f) return;
            if (Vector3.Angle(toPanel, look) > 55f) PlacePanel(p);
        }

        void BuildPromptLabel()
        {
            var go = new GameObject("VR Aim Label", typeof(RectTransform));
            _promptCanvas = go.AddComponent<Canvas>();
            _promptCanvas.renderMode = RenderMode.WorldSpace;
            _promptCanvas.worldCamera = _cam;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(900f, 70f);
            var bg = EsUi.Img(go.transform, "Bg", new Color(0.02f, 0.03f, 0.04f, 0.75f));
            EsUi.Stretch(bg.rectTransform);
            bg.raycastTarget = false;
            _promptLabel = EsUi.Label(go.transform, "Text", "", 34, new Color(0.92f, 0.96f, 1f), TextAnchor.MiddleCenter);
            EsUi.Stretch(_promptLabel.rectTransform, 8f);
            _promptLabel.raycastTarget = false;
            _promptLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            go.SetActive(false);
        }

        void UpdatePromptLabel()
        {
            string p = EsVR.ModalOpen ? "" : _interactor.Prompt;
            bool show = !string.IsNullOrEmpty(p) && EsVR.UiHovered == null;
            if (_promptCanvas.gameObject.activeSelf != show) _promptCanvas.gameObject.SetActive(show);
            if (!show) return;

            // "Clique" is the desktop verb; the text comes from the puzzles, so it is translated
            // here instead of teaching every PromptFor about controllers.
            string text = p.Replace("Clique para", "Gatilho:");
            if (_promptLabel.text != text) _promptLabel.text = text;

            // Floats just above where the laser lands, toward the visitor, at a constant angular size.
            Ray ray = EsVR.AimRay;
            float d = Mathf.Min(SolidDistance(ray), 3.4f);
            Vector3 at = ray.GetPoint(Mathf.Max(0.3f, d - 0.08f)) + Vector3.up * (0.06f + d * 0.03f);
            Vector3 head = _cam.transform.position;
            var rt = (RectTransform)_promptCanvas.transform;
            rt.position = at;
            rt.rotation = Quaternion.LookRotation(at - head, Vector3.up);
            rt.localScale = Vector3.one * Mathf.Max(0.00025f, Vector3.Distance(head, at) * 0.00045f);
        }

        /// <summary>A watch on the left hand: the life-support countdown, which on desktop is only on
        /// the terminal across the room, plus the controls for whoever is wearing the headset.</summary>
        void BuildWrist()
        {
            var go = new GameObject("VR Wrist", typeof(RectTransform));
            go.transform.SetParent(_left.grip, false);
            go.transform.localPosition = new Vector3(0f, 0.06f, -0.03f);
            _wristCanvas = go.AddComponent<Canvas>();
            _wristCanvas.renderMode = RenderMode.WorldSpace;
            _wristCanvas.worldCamera = _cam;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(420f, 190f);
            rt.localScale = Vector3.one * 0.00028f;
            var bg = EsUi.Img(go.transform, "Bg", new Color(0.02f, 0.05f, 0.05f, 0.85f));
            EsUi.Stretch(bg.rectTransform);
            bg.raycastTarget = false;

            _wristTime = EsUi.Label(go.transform, "Time", "--:--", 64, EsTheme.ScreenOn, TextAnchor.UpperCenter, FontStyle.Bold);
            var tr = _wristTime.rectTransform;
            tr.anchorMin = new Vector2(0f, 1f); tr.anchorMax = new Vector2(1f, 1f);
            tr.offsetMin = new Vector2(10f, -90f); tr.offsetMax = new Vector2(-10f, -8f);
            _wristTime.raycastTarget = false;

            var help = EsUi.Label(go.transform, "Help",
                "GATILHO interage\nESQ. anda  |  DIR. gira", 24, new Color(0.75f, 0.85f, 0.82f), TextAnchor.LowerCenter);
            var hr = help.rectTransform;
            hr.anchorMin = new Vector2(0f, 0f); hr.anchorMax = new Vector2(1f, 0f);
            hr.offsetMin = new Vector2(10f, 8f); hr.offsetMax = new Vector2(-10f, 90f);
            help.raycastTarget = false;
        }

        void UpdateWrist()
        {
            if (_wristCanvas == null) return;
            var t = _wristCanvas.transform;
            t.rotation = Quaternion.LookRotation(t.position - _cam.transform.position, Vector3.up);

            var gm = EscapeGameManager.Instance;
            string s = "--:--";
            Color c = EsTheme.ScreenOn;
            if (gm != null && gm.IsRunning)
            {
                int total = Mathf.Max(0, Mathf.CeilToInt(gm.Remaining));
                s = string.Format("{0:00}:{1:00}", total / 60, total % 60);
                if (gm.Remaining <= gm.warnAtSeconds) c = EsTheme.Warn;
            }
            if (_menuHeld > 0.25f)
            {
                s = "REINICIAR " + Mathf.CeilToInt(restartHoldSeconds - _menuHeld);
                c = EsTheme.Amber;
            }
            if (_wristTime.text != s) _wristTime.text = s;
            _wristTime.color = c;
        }
    }
}
