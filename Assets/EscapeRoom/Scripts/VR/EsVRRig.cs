using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR;
using TrackedPoseDriver = UnityEngine.InputSystem.XR.TrackedPoseDriver;

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
    /// <para><b>Controls.</b> Trigger or grip: interact (the hand that pressed becomes the pointer).
    /// Left stick: walk. Right stick: snap turn. Left menu button held 2 s, or F5 on the PC keyboard:
    /// restart for the next visitor. F6 or right stick click: recentre.</para>
    ///
    /// <para><b>Execution order -200</b>, so the press, the hand and the UI target are settled before
    /// <see cref="PlayerInteractor"/> reads them in its own Update.</para></summary>
    [DefaultExecutionOrder(-200)]
    public class EsVRRig : MonoBehaviour
    {
        [Header("Locomocao")]
        public float moveSpeed = 1.6f;
        public float snapTurnDegrees = 30f;
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
        InputAction _move, _turn, _menu, _recentre;

        class Hand
        {
            public XRNode node;
            public Transform grip, aim, hold;
            public InputAction trigger, gripBtn, aimPos;
            public LineRenderer laser;
            public Transform dot;
            public Renderer dotRenderer;
        }

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
            // OpenXR can take a few frames to bring the display up. Wait for it rather than guess;
            // with no headset the rig removes itself and the desktop game runs untouched.
            float until = Time.realtimeSinceStartup + 4f;
            while (!XRSettings.isDeviceActive && Time.realtimeSinceStartup < until) yield return null;
            if (!XRSettings.isDeviceActive)
            {
                Debug.Log("[VR] Nenhum headset ativo - jogo segue no modo desktop.");
                Destroy(gameObject);
                yield break;
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

        void OnDestroy()
        {
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
            var h = new Hand { node = node };
            string usage = "{" + side + "}";

            h.grip = new GameObject(side + " Grip").transform;
            h.grip.SetParent(_offset, false);
            var gtpd = h.grip.gameObject.AddComponent<TrackedPoseDriver>();
            gtpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            gtpd.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            gtpd.ignoreTrackingState = true;
            gtpd.positionInput = new InputActionProperty(Value(side + "GripPos", "Vector3",
                "<XRController>" + usage + "/devicePosition"));
            gtpd.rotationInput = new InputActionProperty(Value(side + "GripRot", "Quaternion",
                "<XRController>" + usage + "/deviceRotation"));

            // The aim (pointer) pose is the one OpenXR defines for rays: it points where the
            // controller visibly points, while the grip pose follows the fist and aims ~40 deg low.
            h.aim = new GameObject(side + " Aim").transform;
            h.aim.SetParent(_offset, false);
            var atpd = h.aim.gameObject.AddComponent<TrackedPoseDriver>();
            atpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            atpd.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            atpd.ignoreTrackingState = true;
            h.aimPos = Value(side + "AimPos", "Vector3", "<XRController>" + usage + "/pointerPosition");
            atpd.positionInput = new InputActionProperty(h.aimPos);
            atpd.rotationInput = new InputActionProperty(Value(side + "AimRot", "Quaternion",
                "<XRController>" + usage + "/pointerRotation"));

            h.hold = new GameObject(side + " Hold").transform;
            h.hold.SetParent(h.grip, false);
            h.hold.localPosition = new Vector3(0f, 0f, 0.09f);

            BuildControllerModel(h.grip, side);
            BuildLaser(h);

            h.trigger = Button(side + "Trigger", "<XRController>" + usage + "/{TriggerButton}",
                               "<XRController>" + usage + "/triggerPressed");
            h.gripBtn = Button(side + "GripBtn", "<XRController>" + usage + "/{GripButton}",
                               "<XRController>" + usage + "/gripPressed");
            return h;
        }

        void BuildInput()
        {
            _move = Value("Move", "Vector2", "<XRController>{LeftHand}/{Primary2DAxis}");
            _turn = Value("Turn", "Vector2", "<XRController>{RightHand}/{Primary2DAxis}");
            _menu = Button("Menu", "<XRController>{LeftHand}/{MenuButton}", "<XRController>{LeftHand}/menu");
            _recentre = Button("Recentre", "<XRController>{RightHand}/{Primary2DAxisClick}");
        }

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

            UpdateHands();
            UpdateOperator();
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
                // Fallback for a runtime without the OpenXR aim pose: ride the grip instead of
                // sitting at the origin with a laser out of the floor.
                // Only once the controller exists: an asleep controller has no controls either,
                // and must not be mistaken for one without an aim pose.
                var ctrl = h.node == XRNode.LeftHand
                    ? UnityEngine.InputSystem.XR.XRController.leftHand
                    : UnityEngine.InputSystem.XR.XRController.rightHand;
                if (ctrl != null && h.aimPos.controls.Count == 0 && h.aim.parent != h.grip)
                {
                    var tpd = h.aim.GetComponent<TrackedPoseDriver>();
                    if (tpd != null) tpd.enabled = false;
                    h.aim.SetParent(h.grip, false);
                    h.aim.localPosition = Vector3.zero;
                    h.aim.localRotation = Quaternion.identity;
                }

                bool pressed = h.trigger.WasPressedThisFrame() || h.gripBtn.WasPressedThisFrame();
                if (!pressed) continue;
                // Switching hands while carrying would teleport the item; the other hand only takes
                // over the pointer when nothing is held.
                if (h != _active && _interactor.Held == null) SetActiveHand(h);
                if (h == _active) EsVR.MarkPress();
            }
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
            if (_menu.IsPressed()) _menuHeld += Time.unscaledDeltaTime; else _menuHeld = 0f;
            if (_menuHeld >= restartHoldSeconds) { _menuHeld = -999f; restart = true; }
            if (restart && EscapeGameManager.Instance != null)
            {
                EscapeGameManager.Instance.Restart();
                return;
            }
            if ((kb != null && kb.f6Key.wasPressedThisFrame) || _recentre.WasPressedThisFrame()) Recentre();
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
            Vector2 stick = EsVR.ModalOpen ? Vector2.zero : _move.ReadValue<Vector2>();
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
            float tx = _turn.ReadValue<Vector2>().x;
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
            if (rumble != null) rumble.SendImpulse(amplitude, seconds);
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

        void BuildControllerModel(Transform grip, string side)
        {
            var body = EsTheme.Box(side + "Model", grip, new Vector3(0f, -0.01f, -0.02f),
                                   new Vector3(0.045f, 0.035f, 0.13f), EsTheme.Lit(EsTheme.ShellDark, 0.3f, 0.5f));
            var tip = EsTheme.Box(side + "Tip", grip, new Vector3(0f, 0.005f, 0.05f),
                                  new Vector3(0.03f, 0.012f, 0.02f), EsTheme.Glow(EsTheme.ScreenOn, 1.5f));
            foreach (var c in new[] { body.GetComponent<Collider>(), tip.GetComponent<Collider>() })
                if (c != null) Destroy(c);
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
                "VR:  GATILHO interage   |   ANALOGICO ESQ. anda   |   ANALOGICO DIR. gira",
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
