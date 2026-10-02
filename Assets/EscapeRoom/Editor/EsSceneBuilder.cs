using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using EscapeRoom;

/// <summary>
/// Builds the whole Escape Room set as REAL scene objects (so everything is saved into the
/// .unity file, not spawned at runtime) and wires every serialized reference.
///
/// Idempotent: wipes any previous "EscapeRoom_Root" before rebuilding.
///
/// Measured room bounds (from the scene itself):
///   walls  x[-12.11 .. 12.07]  z[-11.75 .. 12.17]
///   floor  x[ -9.10 .. 11.80]  z[-11.80 ..  8.90]   (tile pitch ~2.95)
/// The floor does NOT reach the south wall, so a filler slab is added there.
/// </summary>
public static class EsSceneBuilder
{
    const string ROOT = "EscapeRoom_Root";
    const string MAT_DIR = "Assets/EscapeRoom/Materials";
    const string VFX_DIR = "Assets/EscapeRoom/VFX";
    const string AUDIO = "Assets/EscapeRoom/Audio/";

    // layout (all on verified floor)
    const float WALL_N = -11.75f;
    const float WALL_E = 11.72f;
    const float ROW = -10.6f;          // props line, safely on the z=-11.8 floor row
    const float WALL_W = -11.81f;
    const float EXIT_X = -6.0f;        // centre of the exit opening in the north wall
    const string KIT = "Assets/Barking_Dog/3D Free Modular Kit/Prefabs/";

    // Where the player starts looking. 0 (the prefab default) faces the south wall while every
    // objective is on the north wall. Position is the user's; only the yaw is ours.
    const float SPAWN_YAW = 180f;

    // One thickness for every kit wall tile, in metres. The kit ships three (0.25 / 0.29 / 0.32 m
    // were measured across this scene's 93 tiles) and the room is meant to read as poured concrete
    // rather than a fence, so 0.40 m is the target. Depth is the safe axis to grow: the exit
    // opening is cut along the tiles' WIDTH, so thickening eats into the corridor and the room by
    // 7.5 cm a side and does not narrow the doorway by a millimetre.
    const float WALL_THICKNESS = 0.40f;

    // The one prefab this builder treats as a wall. Named because the thickness rule keys off it,
    // and a string literal repeated in a comparison is a string literal waiting to be mistyped.
    const string WALL_PREFAB = "Wall_Simple_01";

    // The corner arch. Kept as a constant for the same reason as WALL_PREFAB, and because the arch
    // was invisible to every wall pass for a long time: <see cref="FillWallToCeiling"/> filters on
    // IsKitWallTile plus the two filler names, and an arch matches none of the three, so its 78 cm
    // slot under the ceiling survived a pass that had closed every other wall in the room. A name
    // filter cannot see what it was not told to look for (D-140), and this is the second time that
    // sentence has cost something.
    const string ARC_PREFAB = "Wall_Arc";

    static Material _shell, _shellDark, _metal, _gold, _glowOn, _alarm, _paper, _exitGlow, _void,
                  _kitWall,
                  _frost, _carpet, _belt, _screenOff, _panelGlow, _cabRed, _fiber,
                  _hexBase, _hexDim, _hexLive, _hexSrc, _hexLiveTile, _hexLeaf, _hexLeafLive;

    [MenuItem("Tools/Escape Room/Build Level")]
    public static void Build()
    {
        // NOTE: deliberately no SaveCurrentModifiedScenesIfUserWantsTo() here. That call can
        // raise a modal dialog, which blocks the editor main thread and kills the MCP bridge.
        // Saving is non-interactive and the build is purely additive.
        EditorSceneManager.SaveOpenScenes();

        var scene = SceneManager.GetActiveScene();
        var scenePath = scene.path;
        if (string.IsNullOrEmpty(scenePath))
        {
            scenePath = "Assets/trabalho_marqueto.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
        }

        var old = GameObject.Find(ROOT);
        if (old != null) Object.DestroyImmediate(old);
        // Play sessions with scene-reload disabled (this project) leak DontDestroyOnLoad
        // pools and dragged items into the saved scene, and old builds left HUD copies at
        // the scene root (BuildHud used to parent nothing and only clean the crosshair).
        CleanStrayRootObjects();

        EnsureFolders();
        BuildMaterials();
        MakeKitMaterialsTwoSided();
        ThickenKitWalls(WALL_THICKNESS);
        MeasureKitWalls();

        var root = new GameObject(ROOT);

        BuildShell(root.transform);
        BuildRoomRevamp(root.transform);
        BuildExterior(root.transform);
        var terminal = BuildTerminal(root.transform);
        var door = BuildBlastDoor(root.transform);
        BuildNotes(root.transform);
        BuildProps(root.transform);
        BuildDressing(root.transform);
        BuildAegisDressing(root.transform);
        var lights = BuildLights(root.transform);
        var gm = BuildGameManager(root.transform, terminal, door, lights);
        gm.SetEmergencyLighting();
        var hexGrid = BuildHexPanel(root.transform, gm);
        BuildLogStation(root.transform, hexGrid);
        BuildPlateReaderStation(root.transform, door, gm);
        WireTerminalObjectives(terminal);

        BuildEventSystem(root.transform);
        BuildHud(root.transform);
        // The floor grid lives under EscapeRoom_Root, and Build() has just destroyed and recreated
        // that root - so building the grid anywhere but here means it silently disappears on the
        // next Build Level and the hand-placed run stays deactivated underneath. A floor that only
        // exists after you remember to run a second menu item is not part of the level.
        AlignAndFillFloor();
        ClearDoorwayIntruders();
        // These two were built as separate menu items and never joined the chain, which is why a
        // plain "Build Level" kept handing back the stacked west wall and the 70 cm slots under the
        // ceiling: the repairs were real, they were just not part of producing the level. Order
        // matters - tidy the runs first, so the ceiling filler measures the walls that survive.
        RemoveMiddleOfRoomWalls();
        // SealWallGaps was also missing from the chain, and it is the pass that closes a hole NOBODY
        // else closes: TidyWallRuns only re-spaces pieces that overlap the coverage front, so a piece
        // starting 3 m PAST the front is left exactly where it is and the hole stays. That is how the
        // east wall kept a 302 cm gap at z -8,76..-5,74 through many Build Levels - the repair existed,
        // it just was not part of producing the level. Sealing first, then tidying, so the run is
        // continuous and flush instead of continuous and gapped.
        SealWallGaps();
        TidyWallRuns();
        FillWallToCeiling();
        RaiseArchesToCeiling();
        // after BuildBlastDoor, which recreates the frame at 2,60 m every run - so fitting it has to
        // be part of producing the level or the 20 cm gaps either side of the door come straight back
        FitDoorFrameToOpening();
        ConfigureAtmosphere();
        FixFirstPersonRig();
        WireWorldSpaceCanvases();
        BuildPostFx();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[EscapeRoom] Level built and saved into " + scenePath);
    }

    // =====================================================================
    // self test (dev aid, kept in the project on purpose)
    //
    // Drives the real UnityEvent chain - button.onClick.Invoke() - so we can prove the
    // wiring end to end without a human clicking. What it does NOT cover is the last mile:
    // whether the EventSystem actually delivers a locked-cursor click to the GraphicRaycaster.
    // =====================================================================
    /// <summary>Reports every legacy UI.Text whose laid-out content is taller or wider than its
    /// own rect. EsUi deliberately uses verticalOverflow = Overflow so a puzzle clue is never
    /// silently truncated - but that makes overflow a VISIBLE defect, so it needs a guard.
    /// Run it in Play Mode (preferred values need the font resolved).</summary>
    [MenuItem("Tools/Escape Room/Check UI text overflow")]
    public static void CheckUiOverflow()
    {
        int bad = 0, total = 0;
        foreach (var t in Object.FindObjectsByType<UnityEngine.UI.Text>(FindObjectsSortMode.None))
        {
            if (t == null || string.IsNullOrEmpty(t.text)) continue;
            total++;
            var rt = t.rectTransform;
            float h = rt.rect.height, w = rt.rect.width;
            float ph = t.preferredHeight, pw = t.preferredWidth;
            if (ph > h + 0.5f || pw > w + 0.5f)
            {
                bad++;
                var owner = t.transform.parent != null ? t.transform.parent.name : "?";
                var snippet = t.text.Replace("\n", "|");
                if (snippet.Length > 28) snippet = snippet.Substring(0, 28) + "...";
                Debug.LogWarning(string.Format(
                    "[UIOverflow] {0}/{1}  '{2}'  needs {3:F0}x{4:F0} but rect is {5:F0}x{6:F0}  (font {7})",
                    owner, t.name, snippet, pw, ph, w, h, t.fontSize));
            }
        }
        Debug.Log(string.Format("[UIOverflow] checked {0} Text components, {1} overflowing", total, bad));
    }

    /// <summary>Reports the world bounds of the door-area props against the exit opening, so
    /// anything poking through a wall shows up as numbers instead of a mystery (D-52).</summary>
    /// <summary>Raycasts from a few viewpoints toward the EXIT sign and logs everything hit, so
    /// "what is that bar in front of the sign" becomes an answer instead of a guess (D-55).</summary>
    /// <summary>Reports what the crosshair actually hits: the 3D raycast plus every UI raycast,
    /// so "I cannot click the button" becomes numbers instead of a mystery (D-61).</summary>
    [MenuItem("Tools/Escape Room/What is under the crosshair")]
    public static void WhatIsUnderCrosshair()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[Crosshair] Camera.main is null"); return; }

        var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        Debug.Log("[Crosshair] cam " + cam.transform.position.ToString("F2")
                  + " fwd " + cam.transform.forward.ToString("F2")
                  + " lock=" + Cursor.lockState + " visible=" + Cursor.visible);

        // The rig is the fix for "I can only look left and right". If its body is null or the mouse
        // device is missing, pitch silently stays at 0 and the symptom comes back with no error.
        var rig = cam.GetComponent<EsFirstPersonCameraRig>();
        if (rig == null) Debug.LogError("[Crosshair] NO EsFirstPersonCameraRig on MainCamera - pitch is dead");
        else Debug.Log("[Crosshair] rig body=" + (rig.body == null ? "NULL" : rig.body.name)
                       + " pitch=" + rig.Pitch.ToString("F1")
                       + " sensitivity=" + rig.sensitivity
                       + " mouse=" + (UnityEngine.InputSystem.Mouse.current == null ? "NULL (no pitch!)" : "ok"));

        RaycastHit hit;
        if (Physics.Raycast(ray, out hit, 50f, ~0, QueryTriggerInteraction.Ignore))
            Debug.Log(string.Format("[Crosshair] 3D d={0:F2} '{1}' at {2}",
                hit.distance, hit.collider.name, hit.point.ToString("F2")));
        else
            Debug.Log("[Crosshair] 3D nothing within 50 m");

        var es = EventSystem.current;
        if (es == null) { Debug.LogError("[Crosshair] EventSystem.current is NULL"); return; }
        var md = es.GetComponent<UnityEngine.EventSystems.BaseInputModule>();
        Debug.Log("[Crosshair] EventSystem '" + es.name + "' over=" + es.IsPointerOverGameObject()
                  + " module=" + (md == null ? "NULL" : md.GetType().Name));

        int total = 0;
        var ped = new UnityEngine.EventSystems.PointerEventData(es)
        {
            position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f)
        };
        foreach (var gr in Object.FindObjectsByType<UnityEngine.UI.GraphicRaycaster>(FindObjectsSortMode.None))
        {
            if (gr == null || !gr.enabled) continue;
            var list = new List<UnityEngine.EventSystems.RaycastResult>();
            gr.Raycast(ped, list);
            if (list.Count == 0) continue;
            total += list.Count;
            Debug.Log("[Crosshair] UI '" + gr.name + "' -> " + list.Count + " hit(s)");
            for (int i = 0; i < list.Count && i < 4; i++)
                Debug.Log("        " + list[i].gameObject.name + " module=" + list[i].module);
        }
        if (total == 0) Debug.Log("[Crosshair] UI no raycaster hit at screen centre");
    }

    /// <summary>Gives every World Space canvas a world camera.
    ///
    /// This is the whole "the button does not respond" bug. <c>GraphicRaycaster.eventCamera</c>
    /// returns null for a World Space canvas whose <c>worldCamera</c> is null, and
    /// <c>GraphicRaycaster.Raycast</c> then takes its Screen Space Overlay branch: it treats
    /// <c>PointerEventData.position</c> (screen pixels) as canvas-local coordinates and tests it
    /// against rect corners that live in world space. The comparison can never succeed, so the
    /// raycast returns nothing, no <c>PointerClick</c> is ever generated, and <c>onClick</c> never
    /// fires - with no error anywhere. The terminal, the mural and the maintenance log were all
    /// built this way.
    ///
    /// Screen Space Overlay canvases (the end-screen victory/defeat panel) are left alone: their
    /// event camera is null by design and a world camera would be meaningless.
    /// </summary>
    static void WireWorldSpaceCanvases()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[Canvas] Camera.main is null - cannot wire world canvases"); return; }

        int n = 0;
        foreach (var c in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
        {
            if (c == null || c.renderMode != RenderMode.WorldSpace) continue;
            if (c.worldCamera == cam) continue;
            c.worldCamera = cam;
            n++;
            Debug.Log("[Canvas] worldCamera -> MainCamera on '" + c.name + "'"
                      + "  (root=" + c.rootCanvas.name + ")");
        }
        if (n > 0) Debug.Log("[Canvas] Wired " + n + " World Space canvas(es). Clicks now resolve.");
    }

    /// <summary>The one test that closes the last mile: hit-test, click, observe the state change.
    ///
    /// Self Test 1 calls <c>onClick.Invoke()</c>, which proves the listener is wired but skips
    /// hit-testing entirely - and a wrong hit-test is exactly what broke the button twice already
    /// (D-62 worldCamera, then the locked cursor reporting desktop coordinates). So this stands
    /// the player where the button is dead ahead, performs the identical hit-test
    /// <see cref="EsCrosshairPointer"/> performs, fires the click through
    /// <c>ExecuteEvents</c>, and then checks that the terminal actually left the Offline phase.
    ///
    /// It moves the player, so it is a self test and not part of the build. The rig applies
    /// rotation in LateUpdate, hence the one-frame delay before reading the camera.
    /// </summary>
    [MenuItem("Tools/Escape Room/Self Test 5 - click the button through the crosshair")]
    public static void SelfTest5CrosshairClick()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[SelfTest5] Camera.main is NULL"); return; }
        var tc = Object.FindFirstObjectByType<TerminalController>();
        if (tc == null) { Debug.LogError("[SelfTest5] no TerminalController"); return; }
        var btn = tc.rebootButton;
        if (btn == null) { Debug.LogError("[SelfTest5] tc.rebootButton is NULL"); return; }
        if (!btn.gameObject.activeInHierarchy)
        {
            Debug.LogError("[SelfTest5] the reboot button is already hidden - the terminal has "
                         + "rebooted. Run this BEFORE Self Test 1.");
            return;
        }
        var es = EventSystem.current;
        if (es == null) { Debug.LogError("[SelfTest5] EventSystem.current is NULL"); return; }
        if (es.GetComponent<EsCrosshairPointer>() == null)
        { Debug.LogError("[SelfTest5] EsCrosshairPointer is not on the EventSystem"); return; }

        var rig = cam.GetComponent<EsFirstPersonCameraRig>();
        if (rig == null || rig.body == null) { Debug.LogError("[SelfTest5] camera rig is not wired"); return; }

        var rt = btn.transform as RectTransform;
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        Vector3 target = (corners[0] + corners[2]) * 0.5f;

        // Stand 2 m back, facing the button dead ahead. Position only - the world is not changed.
        Vector3 flat = target - rig.body.position;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f) flat = Vector3.forward;
        flat.Normalize();
        var standAt = target - flat * 2.0f;
        rig.body.position = new Vector3(standAt.x, rig.body.position.y, standAt.z);

        // Aim synchronously. The rig applies its angles immediately, so the camera transform is
        // already correct for the hit-test below - no dependency on a frame boundary.
        float yaw, pitch;
        rig.LookAtPoint(target, out yaw, out pitch);
        rig.SetLook(yaw, pitch);

        Vector3 eye = cam.transform.position;
        float dist = Vector3.Distance(eye, target);
        Vector2 centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        Vector2 onButton = RectTransformUtility.WorldToScreenPoint(cam, target);

        Vector2 c0 = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 c2 = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

        Debug.Log(string.Format(
            "[SelfTest5] player {0}  button {1}  d={2:F2} m  view={3}x{4}",
            rig.body.position.ToString("F2"), target.ToString("F2"), dist, Screen.width, Screen.height));
        Debug.Log(string.Format(
            "[SelfTest5] aimed yaw={0:F1} pitch={1:F1}; button projects to {2}  on-screen size {3:F1} x {4:F1} px  offset from crosshair {5}",
            yaw, pitch, onButton.ToString("F1"), Mathf.Abs(c2.x - c0.x), Mathf.Abs(c2.y - c0.y),
            (onButton - centre).ToString("F1")));

        var ped = new UnityEngine.EventSystems.PointerEventData(es)
        {
            position = centre,
            button = UnityEngine.EventSystems.PointerEventData.InputButton.Left,
            clickTime = Time.unscaledTime,
            clickCount = 1,
            eligibleForClick = true
        };
        var hits = new List<UnityEngine.EventSystems.RaycastResult>();
        es.RaycastAll(ped, hits);
        Debug.Log("[SelfTest5] crosshair RaycastAll -> " + hits.Count + " hit(s)");

        GameObject handler = null;
        for (int i = 0; i < hits.Count; i++)
        {
            var h = UnityEngine.EventSystems.ExecuteEvents
                .GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(hits[i].gameObject);
            Debug.Log("        " + hits[i].gameObject.name + (h != null ? "   <== HAS CLICK HANDLER" : ""));
            if (h != null && handler == null) handler = h;
        }

        if (handler == null)
        {
            Debug.LogError("[SelfTest5] FAIL - the crosshair is not on a clickable object even "
                         + "though the player is aimed at the button from 2 m.");
            return;
        }

        var phaseBefore = tc.Phase;
        ped.pointerPress = handler;
        ped.pointerClick = handler;
        UnityEngine.EventSystems.ExecuteEvents.Execute(
            handler, ped, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);

        var phaseAfter = tc.Phase;
        Debug.Log("[SelfTest5] phase " + phaseBefore + " -> " + phaseAfter
                  + "   RESULT = " + (phaseAfter != phaseBefore ? "PASS (real click landed)" : "FAIL (click did nothing)"));
    }

    /// <summary>Gives a UI element a discrete hit plane of the SAME size, just in front of it.
    ///
    /// The size is deliberately not enlarged. An earlier version padded the target to 1.44 x 0.56 m
    /// under a 0.90 x 0.216 m button, which meant the click landed anywhere near the button - over
    /// the status text, the mural, the hint - and read as the game being sloppy rather than
    /// forgiving. The target is now exactly the element, so aiming means aiming.
    ///
    /// What the offset buys is a clean, unambiguous hit plane instead of a coplanar one. Coplanar
    /// graphics are ordered by hierarchy index with nothing forcing a winner, so which of two
    /// same-depth elements gets the click is a tie-break rather than a guarantee. Lifting the
    /// target a couple of centimetres toward the viewer makes the front element genuinely nearer
    /// the camera, which is a real geometric fact rather than an accident of sibling order.
    ///
    /// It works because the click path already walks up the hierarchy: `ExecuteEvents
    /// .GetEventHandler` starts at whatever was hit and climbs to the nearest
    /// `IPointerClickHandler`, so a hit on this invisible child still resolves to the Button on the
    /// parent.
    ///
    /// Alpha 0 is safe for raycasting - `GraphicRaycaster` consults `raycastTarget` and ignores
    /// alpha - but it draws nothing, which `Check canvas overlaps` has to account for or this
    /// reads as a real overlap against whatever it covers.
    ///
    /// RectTransform children are not clipped, and the direction toward the viewer is the canvas's
    /// local -Z, so the offset goes in local Z.
    /// </summary>
    static RectTransform AddHitTarget(RectTransform target, float forwardMetres, string suffix)
    {
        var go = new GameObject("HitArea" + suffix, typeof(RectTransform));
        go.transform.SetParent(target, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = target.sizeDelta;          // exactly the element, not padded

        float lossy = Mathf.Abs(target.lossyScale.z);
        if (lossy < 0.000001f) lossy = 0.0009f;   // terminal canvas scale, as a floor
        rt.localPosition = new Vector3(0f, 0f, -forwardMetres / lossy);

        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0f);
        img.raycastTarget = true;
        return rt;
    }

    /// <summary>Reports World Space canvas elements whose rectangles overlap.
    ///
    /// Three separate bugs in this project produced no error, no warning and no exception: a
    /// null <c>worldCamera</c>, a locked cursor reporting off-screen coordinates, and a button
    /// placed 70 units into the status text. All three were found by measuring, not by reading
    /// logs. Overlapping UI is exactly as silent as the others, and it is pure arithmetic, so it
    /// is worth checking mechanically instead of by eye.
    ///
    /// Skips three things on purpose: rects covering most of the canvas (backgrounds are meant to
    /// sit under everything), ancestor/descendant pairs (a button's own label lives inside it),
    /// and disabled objects (the keypad is hidden until the reboot, so it is not a conflict).
    /// </summary>
    [MenuItem("Tools/Escape Room/Check canvas overlaps")]
    public static void CheckCanvasOverlaps()
    {
        int problems = 0;
        foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
        {
            if (canvas == null || canvas.renderMode != RenderMode.WorldSpace) continue;

            var root = canvas.transform as RectTransform;
            if (root == null) continue;
            float cmPerUnit = root.lossyScale.y * 100f;
            if (Mathf.Abs(cmPerUnit) < 0.0001f) continue;

            // Everything is compared in the canvas's OWN local space. GetWorldCorners returns
            // world positions, which carry the canvas's world offset and its 84 deg yaw, so
            // dividing those by the scale produced y values above the canvas height and an
            // axis-aligned box inflated by the rotation. The local space of a RectTransform with
            // localScale 0.0018 and sizeDelta 900x640 IS 900x640 canvas units, exactly.
            var space = root.transform;
            var rootRect = BoundsIn(root, space);

            var rts = new List<RectTransform>();
            foreach (var rt in root.GetComponentsInChildren<RectTransform>(true))
            {
                if (rt == root) continue;
                if (rt.GetComponent<Graphic>() == null) continue;     // layout-only nodes
                if (!rt.gameObject.activeInHierarchy) continue;         // hidden = not a conflict
                // Fully transparent graphics draw nothing, so they cannot overlap anything
                // visually. Without this, the invisible hit plane (AddHitTarget) reads as a real
                // overlap against the status text it deliberately covers.
                var g0 = rt.GetComponent<Graphic>();
                if (g0 != null && g0.color.a <= 0.001f) continue;      // invisible hit padding
                var t = rt.GetComponent<Text>();
                if (t != null && string.IsNullOrWhiteSpace(t.text)) continue;   // draws nothing
                var b = BoundsIn(rt, space);
                if (b.width <= 0f || b.height <= 0f) continue;
                // The area exemption is for background PLATES only. Applying it to text as well
                // silently dropped the mural from the check - its body legitimately fills more
                // than 70% of its frame, and a coverage gap is worse than a false alarm.
                if (t == null && Area(b) > Area(rootRect) * 0.70f) continue;
                rts.Add(rt);
            }

            Debug.Log("[CanvasOverlap] '" + canvas.name + "': canvas " + rootRect.width.ToString("F0") + "x"
                      + rootRect.height.ToString("F0") + " units, " + rts.Count
                      + " visible element(s); 1 unit = " + cmPerUnit.ToString("F2") + " cm");

            for (int i = 0; i < rts.Count; i++)
            {
                for (int j = i + 1; j < rts.Count; j++)
                {
                    if (rts[i].transform.IsChildOf(rts[j]) || rts[j].transform.IsChildOf(rts[i])) continue;
                    var a = BoundsIn(rts[i], space);
                    var b = BoundsIn(rts[j], space);
                    float ox = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
                    float oy = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
                    if (ox <= 0f || oy <= 0f) continue;

                    problems++;
                    Debug.Log(string.Format(
                        "[CanvasOverlap]   OVERLAP '{0}' x '{1}' = {2:F1} x {3:F1} units ({4:F2} x {5:F2} cm)"
                        + "\n        '{0}' y {6:F0}..{7:F0}   '{1}' y {8:F0}..{9:F0}",
                        rts[i].name, rts[j].name, ox, oy, ox * cmPerUnit, oy * cmPerUnit,
                        a.yMin, a.yMax, b.yMin, b.yMax));
                }
            }
        }
        if (problems == 0) Debug.Log("[CanvasOverlap] No overlapping canvas elements. RESULT = clean");
        else Debug.LogError("[CanvasOverlap] " + problems + " overlapping pair(s) found.");
    }

    /// <summary>Runs the overlap check against the terminal's post-reboot layout as well.
    ///
    /// The reboot is a 1.4 s coroutine, and the Unity player loop does not tick while the Editor
    /// window is unfocused, so driving the terminal by clicking leaves it parked in Booting with
    /// the hint still empty - the second layout never gets checked. This reconstructs the
    /// post-reboot state directly, checks it, then restores.
    ///
    /// The rebuild has to hide the reboot button too, not just populate the hint. Checking the
    /// button and the hint together would measure a fiction: after the reboot the button is
    /// hidden. The two are mutually exclusive, and the check has to reproduce the real state
    /// or it is measuring a fiction.
    ///
    /// Worth knowing when reading the numbers: a RectTransform's local space is centred on its
    /// pivot, so a 1920x1080 canvas spans y -540..+540, not 0..1080.
    /// </summary>
    [MenuItem("Tools/Escape Room/Check post-reboot terminal layout")]
    public static void CheckPostRebootLayout()
    {
        var tc = Object.FindFirstObjectByType<TerminalController>();
        if (tc == null) { Debug.LogError("[CanvasOverlap] no TerminalController"); return; }
        if (tc.rebootButton == null) { Debug.LogError("[CanvasOverlap] rebootButton is NULL"); return; }

        bool btnWas = tc.rebootButton.gameObject.activeSelf;
        string hint = tc.hintText != null ? tc.hintText.text : null;

        // post-reboot: button gone, objective hint populated
        tc.rebootButton.gameObject.SetActive(false);
        if (tc.hintText != null) tc.RefreshObjective();
        Canvas.ForceUpdateCanvases();   // regenerate the text so tight bounds are valid

        Debug.Log("[CanvasOverlap] --- POST-REBOOT state (button hidden, objective hint shown) ---");
        CheckCanvasOverlaps();

        tc.rebootButton.gameObject.SetActive(btnWas);
        if (tc.hintText != null) tc.hintText.text = hint;
        Canvas.ForceUpdateCanvases();
        Debug.Log("[CanvasOverlap] --- restored: button=" + btnWas + " ---");
    }

    /// <summary>Proves the aiming maths, the distance case, and the HUD's click safety.
    ///
    /// <see cref="EsCrosshairPointer"/> can only be fully exercised by reading it, because firing
    /// it needs a real <c>wasPressedThisFrame</c> that MCP cannot synthesise. Its target search is
    /// exposed as <c>FindTarget</c> so the decision can be tested directly, and this deliberately
    /// stands the player at the SPAWN - 11.3 m away, panel ~13 degrees off the crosshair, button
    /// 121x29 px - because that is the case the player actually reported as impossible, and every
    /// earlier version of this test stood at 2 m with the crosshair already on the button, which
    /// is why it kept passing while the game stayed hard to play.
    ///
    /// Also asserted: the rectangle distance really is a distance (zero inside, 30 outside), a
    /// tight radius really does exclude a distant target so the loose pick cannot become "click
    /// anything", and neither HUD canvas can intercept a click.
    /// </summary>
    [MenuItem("Tools/Escape Room/Self Test 6 - locked-cursor click target")]
    public static void SelfTest6LockedCursor()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[SelfTest6] Camera.main is NULL"); return; }
        var es = EventSystem.current;
        if (es == null) { Debug.LogError("[SelfTest6] EventSystem.current is NULL"); return; }
        var tc = Object.FindFirstObjectByType<TerminalController>();
        if (tc == null || tc.rebootButton == null) { Debug.LogError("[SelfTest6] no reboot button"); return; }
        if (!tc.rebootButton.gameObject.activeInHierarchy)
        { Debug.LogError("[SelfTest6] the reboot button is hidden - run before Self Test 1"); return; }

        var rig = cam.GetComponent<EsFirstPersonCameraRig>();
        if (rig == null || rig.body == null) { Debug.LogError("[SelfTest6] camera rig not wired"); return; }

        var rt = tc.rebootButton.transform as RectTransform;
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        Vector3 target = (corners[0] + corners[2]) * 0.5f;

        // Deliberately NOT aimed at the button. The player spawns 11.3 m away looking at the room,
        // not at a pixel, and that is the case that was failing. Standing at the spawn bearing and
        // letting the interact radius do the work is the real scenario, so it is the one asserted.
        var spawn = new Vector3(1.50f, rig.body.position.y, 0.50f);
        rig.body.position = spawn;
        float yaw, pitch;
        rig.LookAtPoint(target, out yaw, out pitch);
        rig.SetLook(192.7f, -3f);        // spawn bearing: the panel sits ~12.7 deg right of dead ahead

        var xhair = es.GetComponent<EsCrosshairPointer>();
        if (xhair == null) { Debug.LogError("[SelfTest6] EsCrosshairPointer is not on the EventSystem"); return; }

        int fails = 0;
        Vector2 centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        Vector2 onButton = RectTransformUtility.WorldToScreenPoint(cam, target);

        Debug.Log("[SelfTest6] snap radius = " + xhair.snapRadiusPixels.ToString("F0")
                  + " px, loose radius = " + xhair.looseRadiusPixels.ToString("F0") + " px (non-puzzle only)");
        Debug.Log("[SelfTest6] crosshair " + centre.ToString("F0") + "; button projects to "
                  + onButton.ToString("F0") + ", offset "
                  + (onButton - centre).ToString("F0") + " px");

        // 1. no interact key. A key that presses whatever is nearest would type the code for the
        //    player, since the keypad IS a canvas. Asserted by reflection so a re-add cannot slip
        //    in without a test failing.
        bool noEKey = xhair.GetType().GetField("interactRadiusPixels") == null
                   && xhair.GetType().GetField("prompt") == null;
        if (!noEKey) fails++;
        Debug.Log("[SelfTest6] interact key still absent = " + noEKey + "   "
                  + (noEKey ? "PASS" : "FAIL - a loose key path could still enter the code"));

        // 2. THE PUDDLE INVARIANT. The safe keypad must be unreachable at the
        //    loose radius, even though the reboot button at the very same
        //    moment is reachable. If this ever passes for a key, the puzzle
        //    has been answered by aiming, which is the exact thing the
        //    interact key was removed for.
        var safepad = Object.FindFirstObjectByType<EsSafeKeypad>();
        var kp = safepad != null ? safepad.keypadRoot : null;
        bool keypadMarked = kp != null && kp.GetComponent<EsPuzzleInput>() != null;
        int keysChecked = 0, keysUnreachable = 0;
        if (kp != null)
        {
            foreach (var b in kp.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            {
                keysChecked++;
                if (EsCrosshairPointer.IsPuzzleInput(b.gameObject)) keysUnreachable++;
            }
        }
        bool ok2 = keypadMarked && keysChecked > 0 && keysUnreachable == keysChecked;
        if (!ok2) fails++;
        Debug.Log("[SelfTest6] keypad marked as puzzle input = " + keypadMarked
                  + ", keys judged at the tight radius = " + keysUnreachable + "/" + keysChecked
                  + "   " + (ok2 ? "PASS" : "FAIL - the loose radius could reach a digit"));

        // 3. the rectangle distance really is a distance (zero inside, 30 outside). This is what
        //    the tolerance rests on; a ring of samples is not coverage.
        float dInside = EsCrosshairPointer.RectDistance(new Vector2(0, 0), new Vector2(100, 100), new Vector2(50, 50));
        float dOutside = EsCrosshairPointer.RectDistance(new Vector2(0, 0), new Vector2(100, 100), new Vector2(130, 50));
        bool ok3 = Mathf.Approximately(dInside, 0f) && Mathf.Approximately(dOutside, 30f);
        if (!ok3) fails++;
        Debug.Log("[SelfTest6] rect distance: inside=" + dInside.ToString("F1")
                  + " (want 0), 30px outside=" + dOutside.ToString("F1") + " (want 30)   "
                  + (ok3 ? "PASS" : "FAIL"));

        // 4. the hit plane: same size as the face, offset toward the viewer. Probing a point 2 cm in
        //    FRONT of the button's face centre must resolve to the button, which is the whole
        //    contract AddHitTarget promises. Probing inside the face would prove nothing, since
        //    the visible Image is a raycast target too.
        var btnRt = tc.rebootButton.transform as RectTransform;
        var hitRt = btnRt != null ? btnRt.Find("HitArea_Reboot") as RectTransform : null;
        if (hitRt == null)
        {
            fails++;
            Debug.LogError("[SelfTest6] RebootButton has no HitArea_Reboot child");
        }
        else
        {
            bool sameSize = hitRt.sizeDelta == btnRt.sizeDelta;
            if (!sameSize) fails++;
            float offsetM = Vector3.Distance(hitRt.position, btnRt.position);
            Vector3 inFront = btnRt.position + btnRt.forward * 0.02f;
            var resolvedF = ResolveAt(es, RectTransformUtility.WorldToScreenPoint(cam, inFront));
            bool ok5 = sameSize && resolvedF == tc.rebootButton.gameObject;
            if (!ok5) fails++;
            Debug.Log("[SelfTest6] hit plane " + hitRt.sizeDelta.ToString("F0")
                      + " (face " + btnRt.sizeDelta.ToString("F0")
                      + ", same size = " + sameSize + "), offset " + (offsetM * 100f).ToString("F1")
                      + " cm toward the viewer; aim 2 cm in front of the face -> "
                      + (resolvedF == null ? "NULL" : resolvedF.name) + "   "
                      + (ok5 ? "PASS" : "FAIL"));
        }

        // 5. every safe key must have a hit plane of exactly its own size, offset toward the
        //    viewer. This is checked GEOMETRICALLY rather than by raycast, and deliberately so: a
        //    probe there could only ever report INCONCLUSIVE. Since
        //    the hit plane is the same rect as the face, "same size" is the whole contract - and
        //    unlike the old padding there is no way for one key's target to overlap a neighbour's
        //    face, which is the bug that padding created and that this removes by construction.
        var keypadGo = kp;
        int planesChecked = 0, planesBad = 0;
        if (keypadGo == null)
        {
            Debug.LogWarning("[SelfTest6] keypadRoot is NULL - skipping the keypad hit planes");
        }
        else
        {
            var kps = keypadGo.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < kps.Length; i++)
            {
                var k = kps[i];
                if (!k.name.StartsWith("SKey_")) continue;
                planesChecked++;
                var kh = k.Find("HitArea_SKey") as RectTransform;
                bool same = kh != null && kh.sizeDelta == k.sizeDelta;
                float off = kh != null ? Vector3.Distance(kh.position, k.position) : 0f;
                // the offset must be along the canvas normal, i.e. purely in local Z
                bool alongZ = kh != null && Mathf.Abs(kh.localPosition.x) < 0.001f
                                           && Mathf.Abs(kh.localPosition.y) < 0.001f
                                           && kh.localPosition.z < 0f;
                if (!same || !alongZ || off <= 0.001f)
                {
                    planesBad++;
                    if (planesBad <= 3)
                        Debug.LogError("[SelfTest6]   " + k.name + ": hit plane "
                            + (kh == null ? "MISSING" : kh.sizeDelta.ToString("F0") + " vs face "
                               + k.sizeDelta.ToString("F0") + ", offset "
                               + (off * 100f).ToString("F1") + " cm, alongZ=" + alongZ));
                }
            }
            if (planesBad > 0) fails += planesBad;
            Debug.Log("[SelfTest6] keypad hit planes: " + (planesChecked - planesBad) + "/" + planesChecked
                      + " correct (same size as the face, offset along the canvas normal toward "
                      + "the viewer)   " + (planesBad == 0 ? "PASS" : "FAIL"));
        }

        // 6. the crosshair must be invisible to the UI event system. It sits at the exact point every
        //    locked click is resolved at, so a raycaster on its canvas would swallow every click in
        //    the game - and it would do it silently, because a crosshair blocking a button looks
        //    exactly like a broken button. Asserted structurally rather than inferred from case 1.
        var xh = Object.FindFirstObjectByType<EsCrosshair>();
        if (xh == null)
        {
            fails++;
            Debug.LogError("[SelfTest6] no EsCrosshair in the scene");
        }
        else
        {
            bool hasRaycaster = xh.GetComponent<UnityEngine.UI.GraphicRaycaster>() != null;
            int targets = 0;
            foreach (var g in xh.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                if (g.raycastTarget) targets++;
            var grp = xh.GetComponent<CanvasGroup>();
            bool blocks = grp != null && grp.blocksRaycasts;
            bool ok6 = !hasRaycaster && targets == 0 && !blocks;
            if (!ok6) fails++;
            Debug.Log("[SelfTest6] crosshair: GraphicRaycaster=" + hasRaycaster
                      + "  raycastTarget graphics=" + targets
                      + "  CanvasGroup.blocksRaycasts=" + blocks + "   "
                      + (ok6 ? "PASS (cannot intercept clicks)" : "FAIL - it can eat clicks"));
        }

        // 4. the keypad must have an UNAMBIGUOUS confirm key, and DEL must be visually distinct.
        //    The confirm key used to be labelled "C", which is not a symbol anyone reads as
        //    "submit" - the player reported having no way to press enter after typing the code.
        //    Asserted so the labels cannot quietly go back to something cryptic.
        string confirm = null, del = null;
        string plateHex = "?", textHex = "?";
        bool delIsRed = false;
        if (kp != null)
        {
            foreach (var btn in kp.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            {
                var lbl = btn.GetComponentInChildren<UnityEngine.UI.Text>();
                if (lbl == null) continue;
                string s = lbl.text.Trim();
                if (s == "ENTER") confirm = s;
                if (s != "DEL") continue;
                del = s;
                // The PLATE is what the player reads as the key's colour, so that is what gets
                // asserted. Asserting the label's own colour was checking the wrong object: the
                // label is a child, so GetComponentInParent<Graphic> returns the label itself.
                var plate = btn.targetGraphic != null ? btn.targetGraphic : btn.GetComponent<UnityEngine.UI.Graphic>();
                if (plate != null)
                {
                    var c = plate.color;
                    plateHex = ColorUtility.ToHtmlStringRGB(c);
                    delIsRed = c.r > c.g * 1.6f && c.r > c.b * 1.6f;
                }
                textHex = ColorUtility.ToHtmlStringRGB(lbl.color);
            }
        }
        bool ok4 = confirm == "ENTER" && del == "DEL" && delIsRed;
        if (!ok4) fails++;
        Debug.Log("[SelfTest6] confirm = '" + (confirm ?? "NONE") + "', DEL plate #" + plateHex
                  + " label #" + textHex + " (red = " + delIsRed + ")   "
                  + (ok4 ? "PASS" : "FAIL"));

        // 5. THE PUZZLE GUARD, tested by behaviour rather than by reflection. The keypad is forced
        //    visible and the reboot button hidden - the real post-reboot state - with the crosshair
        //    near the keypad but not on it. FindTarget walks the canvas hierarchy directly, so a
        //    force-activated keypad is fully visible to it; this needs no GraphicRegistry and no
        //    live coroutine. This is the assertion that stops the aiming aid from reaching the
        //    answer, and it is the one that would catch a real regression.
        if (kp != null)
        {
            bool kpWas = kp.activeSelf, btnWas = tc.rebootButton.gameObject.activeSelf;
            kp.SetActive(true);
            tc.rebootButton.gameObject.SetActive(false);
            Canvas.ForceUpdateCanvases();

            string said;
            // The invariant, stated so it cannot be faked: whatever comes back must be a key that is
            // genuinely within the TIGHT radius. Measuring one nominated key and then moving the
            // camera does not work - the aim shift changes the distance to every key at once, and
            // the nearest one to the new aim is a different key than the one measured.
            var cross2 = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            float nearest = float.MaxValue;
            foreach (var b in kp.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            {
                var brt = b.transform as RectTransform;
                if (brt == null) continue;
                var cc = new Vector3[4];
                brt.GetWorldCorners(cc);
                Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, (cc[0] + cc[2]) * 0.5f);
                nearest = Mathf.Min(nearest, (sp - cross2).magnitude);
            }

            var picked = xhair.FindTarget();
            bool pickedIsKey = picked != null && EsCrosshairPointer.IsPuzzleInput(picked);
            float pickedGap = 0f;
            if (pickedIsKey)
            {
                var prt = picked.GetComponent<RectTransform>();
                var cc2 = new Vector3[4];
                prt.GetWorldCorners(cc2);
                pickedGap = (RectTransformUtility.WorldToScreenPoint(cam, (cc2[0] + cc2[2]) * 0.5f) - cross2).magnitude;
            }
            bool ok5b = !pickedIsKey || pickedGap <= xhair.snapRadiusPixels;
            said = "nearest key " + nearest.ToString("F0") + " px away; picked "
                 + (picked == null ? "NOTHING" : picked.name + (pickedIsKey ? " at " + pickedGap.ToString("F0") + " px" : " (not a key)"))
                 + "; tight radius " + xhair.snapRadiusPixels.ToString("F0");
            kp.SetActive(kpWas);
            tc.rebootButton.gameObject.SetActive(btnWas);
            Canvas.ForceUpdateCanvases();

            if (!ok5b) fails++;
            Debug.Log("[SelfTest6] loose radius vs a digit: " + said + "   "
                      + (ok5b ? "PASS" : "FAIL - the aiming aid can enter the code"));
        }

        // 6. the OTHER half of the same compromise: the non-puzzle panel must still be reachable
        //    from the spawn by an ordinary click. Case 5 proves the tight radius protects the
        //    digits; this proves the loose radius does what it was added for. Without this pair the
        //    pointer could be tightened indefinitely and every assertion would still pass while the
        //    terminal stayed unusable - which is exactly the state the last two rounds were in.
        {
            bool kpWas = kp != null && kp.activeSelf;
            if (kp != null) kp.SetActive(false);
            tc.rebootButton.gameObject.SetActive(true);
            Canvas.ForceUpdateCanvases();

            float gap = (onButton - centre).magnitude;
            var picked6 = xhair.FindTarget();
            bool ok6 = picked6 == tc.rebootButton.gameObject && gap <= xhair.looseRadiusPixels;
            if (!ok6) fails++;
            Debug.Log("[SelfTest6] reboot button " + gap.ToString("F0") + " px from the crosshair"
                      + " (loose " + xhair.looseRadiusPixels.ToString("F0") + ") -> picked "
                      + (picked6 == null ? "NOTHING" : picked6.name) + "   "
                      + (ok6 ? "PASS (reachable from the spawn)" : "FAIL - panel unreachable again"));

            if (kp != null) kp.SetActive(kpWas);
            tc.rebootButton.gameObject.SetActive(true);
            Canvas.ForceUpdateCanvases();
        }

        // 7. the removed interact prompt must be GONE from the saved scene, not merely unused.
        //    Its black plate was left on screen because the type was deleted before the object was
        //    destroyed, and a deleted component type is no reason for the GameObject to vanish.
        int stale = 0;
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (tr == null) continue;
            if (tr.name == "EsInteractPrompt" || tr.name == "PromptText") stale++;
            if (tr.name == "Plate" && tr.parent != null && tr.parent.name == "PromptText") stale++;
        }
        bool ok7 = stale == 0;
        if (!ok7) fails++;
        Debug.Log("[SelfTest6] stale interact-prompt objects in the scene = " + stale + "   "
                  + (ok7 ? "PASS (no black bar)" : "FAIL - the prompt plate survived"));

        // 8. the hover highlight. It cannot be observed through the running game here, because the
        //    player loop does not tick while the Editor window is unfocused, so it is exercised
        //    directly instead: on, off, and the plate colour back to EXACTLY its base. Round-tripping
        //    the colour is the part that actually matters - a highlight that drifts a little lighter
        //    every hover is invisible after ten passes and impossible to notice in a test that only
        //    checks "it changed".
        var rb = tc.rebootButton;
        var hl = rb != null ? rb.GetComponent<EsHoverHighlight>() : null;
        int withHl = 0, totalClickable = 0;
        foreach (var cv in Object.FindObjectsByType<Canvas>())
        {
            if (cv == null || cv.renderMode == RenderMode.ScreenSpaceOverlay) continue;
            foreach (var b in cv.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            {
                totalClickable++;
                if (b.GetComponent<EsHoverHighlight>() != null) withHl++;
            }
        }
        bool ok8a = hl != null && totalClickable > 0 && withHl == totalClickable;
        Color roundTrip = Color.clear;
        if (hl != null)
        {
            // The live game may already have this button hovered (the spawn
            // view puts the crosshair within the loose radius, and the
            // crosshair pointer drives SetHovered every frame in play). Force
            // the known-off state first, or "before" measures mid-hover and
            // the assertion races the game instead of testing the component.
            hl.SetHovered(false);
            var plate = rb.targetGraphic;
            Color before = plate.color;
            hl.SetHovered(true);
            Color lit = plate.color;
            hl.SetHovered(false);
            roundTrip = plate.color;
            bool litDiffers = lit != before;
            bool restored = roundTrip == before;
            bool ok8b = litDiffers && restored;
            if (!ok8b) fails++;
            Debug.Log("[SelfTest6] hover: plate #" + ColorUtility.ToHtmlStringRGB(before)
                      + " -> lit #" + ColorUtility.ToHtmlStringRGB(lit)
                      + " -> back #" + ColorUtility.ToHtmlStringRGB(roundTrip)
                      + "   " + (ok8b ? "PASS (changes, and returns exactly)" : "FAIL"));
        }
        if (!ok8a) fails++;
        Debug.Log("[SelfTest6] clickable canvas elements carrying a highlight = " + withHl + "/" + totalClickable
                  + "   " + (ok8a ? "PASS" : "FAIL - some buttons cannot show aim feedback"));

        // 9. the crosshair must be able to say "you can click now", which is a different message
        //    from "here is the target" and the player asked for both.
        var xhud = Object.FindFirstObjectByType<EsCrosshair>();
        bool ok9 = xhud != null;
        if (ok9)
        {
            // the dot is the last child built, so it is the one SetArmed recolours
            var dots = xhud.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
            xhud.SetArmed(true);
            Color armed = dots.Length > 0 ? dots[dots.Length - 1].color : Color.clear;
            xhud.SetArmed(false);
            Color idle = dots.Length > 0 ? dots[dots.Length - 1].color : Color.clear;
            bool ok9b = armed != idle;
            if (!ok9b) fails++;
            Debug.Log("[SelfTest6] crosshair dot: idle #" + ColorUtility.ToHtmlStringRGB(idle)
                      + " armed #" + ColorUtility.ToHtmlStringRGB(armed) + "   "
                      + (ok9b ? "PASS (it can signal 'click now')" : "FAIL"));
        }
        else fails++;
        Debug.Log("[SelfTest6] crosshair present = " + ok9 + "   " + (ok9 ? "PASS" : "FAIL"));

        Debug.Log("[SelfTest6] RESULT = " + (fails == 0 ? "PASS" : fails + " FAILURE(S)"));
    }

    /// <summary>Topmost object under a screen point that can receive a click. Mirrors
    /// <c>EsCrosshairPointer.HandlerUnder</c> so the self tests exercise the same resolution the
    /// game uses, rather than a second implementation that could disagree with it.</summary>
    static GameObject ResolveAt(EventSystem es, Vector2 screenPoint)
    {
        var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = screenPoint };
        var hits = new List<UnityEngine.EventSystems.RaycastResult>();
        es.RaycastAll(ped, hits);
        for (int i = 0; i < hits.Count; i++)
        {
            var h = UnityEngine.EventSystems.ExecuteEvents
                .GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(hits[i].gameObject);
            if (h != null) return h;
        }
        return null;
    }

    /// <summary>Reports what the crosshair is on RIGHT NOW, without moving the player.
    ///
    /// Self Test 5 aims for the player, which proves the whole chain but hides the one thing a
    /// player actually gets wrong: whether they are aimed at the button. This does not touch the
    /// camera, so it can be run while standing in front of the terminal looking at it, which is
    /// the only way to tell "my aim is off" apart from "the click path is broken".
    ///
    /// It also prints the frozen OS cursor position next to the crosshair, because with the cursor
    /// locked those two are different points and the gap between them is the whole bug.
    /// </summary>
    [MenuItem("Tools/Escape Room/Is the crosshair on the button")]
    public static void IsCrosshairOnButton()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[Aim] Camera.main is NULL"); return; }
        var es = EventSystem.current;
        if (es == null) { Debug.LogError("[Aim] EventSystem.current is NULL"); return; }

        var mouse = UnityEngine.InputSystem.Mouse.current;
        Vector2 mp = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
        Vector2 centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

        var btnTc = Object.FindFirstObjectByType<TerminalController>();
        bool btnActive = btnTc != null && btnTc.rebootButton != null
                        && btnTc.rebootButton.gameObject.activeInHierarchy;

        Debug.Log("[Aim] view " + Screen.width + "x" + Screen.height
                  + "  lock=" + Cursor.lockState
                  + "  FOV=" + cam.fieldOfView.ToString("F0")
                  + "  reboot button visible=" + btnActive
                  + "  player->terminal=" + Vector3.Distance(cam.transform.position, new Vector3(-1f, 1.49f, -10.53f)).ToString("F2") + " m");
        Debug.Log("[Aim] crosshair at " + centre.ToString("F0")
                  + "   frozen OS cursor at " + mp.ToString("F0")
                  + "   distance between them = " + (mp - centre).ToString("F0") + " px");

        if (mouse != null && mp.x >= 0f && mp.y >= 0f && mp.x <= Screen.width && mp.y <= Screen.height)
            Debug.LogWarning("[Aim] The OS cursor is inside the view, so the normal input path WILL raycast "
                           + "at " + mp.ToString("F0") + " - not at the crosshair. EsCrosshairPointer must cover it.");
        else
            Debug.Log("[Aim] The OS cursor is outside the view; the normal path can never hit anything.");

        var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = centre };
        var hits = new List<UnityEngine.EventSystems.RaycastResult>();
        es.RaycastAll(ped, hits);
        Debug.Log("[Aim] crosshair raycast -> " + hits.Count + " hit(s)");
        for (int i = 0; i < hits.Count && i < 5; i++)
        {
            var h = UnityEngine.EventSystems.ExecuteEvents
                .GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(hits[i].gameObject);
            Vector2 s = RectTransformUtility.WorldToScreenPoint(cam, hits[i].worldPosition);
            Debug.Log("        " + hits[i].gameObject.name
                      + (h != null ? "   <== CLICKABLE" : "   (no handler)")
                      + "   at screen " + s.ToString("F0"));
        }

        bool onSomethingClickable = false;
        for (int i = 0; i < hits.Count; i++)
            if (UnityEngine.EventSystems.ExecuteEvents
                    .GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(hits[i].gameObject) != null)
            { onSomethingClickable = true; break; }

        Debug.Log(onSomethingClickable
            ? "[Aim] RESULT = the crosshair IS on a clickable object. A click here will land."
            : "[Aim] RESULT = the crosshair is on NOTHING clickable. Move the aim - this is an aiming "
            + "problem, not a click-path problem.");
    }

    /// <summary>Makes the invisible raycast visible: the sorted candidate list, with every field
    /// the EventSystem actually sorts on.
    ///
    /// There is no built-in gizmo for this, and "the raycast is not visible" is the hardest kind of
    /// bug to reason about because the winning element is chosen by a comparison chain nobody
    /// remembers. From <c>EventSystem.RaycastComparer</c>, in order:
    ///
    ///   eventCamera.depth -> sortOrderPriority -> renderOrderPriority -> sortingLayer
    ///   -> sortingOrder -> depth -> distance -> index
    ///
    /// The two that matter here and are almost always assumed wrong:
    ///
    ///   DISTANCE IS NEARLY LAST. Being physically closer does not win. Only after six other
    ///   criteria have tied does distance decide, and after that comes the hierarchy index, which
    ///   is why coplanar siblings fall to whichever was created last. That is the whole reason the
    ///   hit planes are lifted toward the viewer rather than left coplanar (D-81).
    ///
    ///   GraphicRaycaster beats everything of a different kind, because its sortOrderPriority is
    ///   what wins that slot. So world-space UI always beats a physics raycast at the same point.
    ///
    /// Prints the crosshair's own candidates and then the decision the crosshair pointer makes from
    /// them, because those are two different questions and they can disagree - the pointer measures
    /// the exact distance from the crosshair to each screen rect, while EventSystem sorts its own
    /// list. When they disagree, the pointer wins, because the pointer is what dispatches the click.
    /// </summary>
    [MenuItem("Tools/Escape Room/Show what the crosshair raycast hit")]
    public static void ShowCrosshairRaycast()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[Raycast] Camera.main is NULL"); return; }
        var es = EventSystem.current;
        if (es == null) { Debug.LogError("[Raycast] EventSystem.current is NULL"); return; }

        var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        Debug.Log("[Raycast] crosshair at " + centre.ToString("F0") + "  view " + Screen.width + "x"
                  + Screen.height + "  lock=" + Cursor.lockState);

        RaycastHit phys;
        var ray = cam.ScreenPointToRay(centre);
        Debug.Log(Physics.Raycast(ray, out phys, 50f, ~0, QueryTriggerInteraction.Ignore)
            ? string.Format("[Raycast] physics  d={0:F2} '{1}'  (sorts AFTER all GraphicRaycasters)",
                            phys.distance, phys.collider.name)
            : "[Raycast] physics  nothing within 50 m");

        var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = centre };
        var hits = new List<UnityEngine.EventSystems.RaycastResult>();
        es.RaycastAll(ped, hits);
        Debug.Log("[Raycast] EventSystem.RaycastAll at the crosshair -> " + hits.Count
                  + " candidate(s), already sorted by RaycastComparer (index 0 wins)");

        for (int i = 0; i < hits.Count && i < 8; i++)
        {
            var r = hits[i];
            var h = UnityEngine.EventSystems.ExecuteEvents
                .GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(r.gameObject);
            string sl = SortingLayerName(r.sortingLayer);
            var g = r.gameObject.GetComponent<UnityEngine.UI.Graphic>();
            float alpha = g != null ? g.color.a : -1f;
            Debug.Log(string.Format(
                "[Raycast]   {0}. {1}{2}  module={3}  depth={4}  sortingLayer={5}  sortingOrder={6}  distance={7:F3}  index={8}  alpha={9:F2}",
                i, r.gameObject.name,
                h != null ? "  <== CLICKABLE" : "  (no click handler)",
                r.module != null ? r.module.GetType().Name : "null",
                r.depth, sl, r.sortingOrder,
                r.module != null && r.module.eventCamera != null ? r.distance : -1f,
                r.index, alpha));
        }

        var xp = es.GetComponent<EsCrosshairPointer>();
        if (xp == null) { Debug.Log("[Raycast] no EsCrosshairPointer on the EventSystem"); return; }

        var hover = xp.Hovered;
        Debug.Log("[Raycast] crosshair pointer currently highlights: "
                  + (hover == null ? "NOTHING" : hover.name)
                  + "   (snap " + xp.snapRadiusPixels.ToString("F0") + " px, loose "
                  + xp.looseRadiusPixels.ToString("F0") + " px for non-puzzle targets)");

        if (hover != null)
        {
            var hl = hover.GetComponent<EsHoverHighlight>();
            Debug.Log("[Raycast]   highlight component present = " + (hl != null)
                      + ", hovered = " + (hl != null && hl.Hovered));
        }
    }

    /// <summary>Name for a sorting layer id. There is no NameFromID on SortingLayer, so this walks
    /// the layers table; the raw id is still printed alongside so an unknown one is still legible.</summary>
    static string SortingLayerName(int id)
    {
        var all = SortingLayer.layers;
        for (int i = 0; i < all.Length; i++)
            if (all[i].id == id) return all[i].name + "(" + id + ")";
        return "id " + id;
    }

    /// <summary>Proves that a click DISPATCHED BY THE CROSSHAIR reaches a keypad key.
    ///
    /// Self Test 2 calls <c>onClick.Invoke()</c> directly, which proves the listener is wired but
    /// skips every step that could actually lose the click: the target search, the deferral against
    /// the frozen OS cursor, and the <c>ExecuteEvents</c> dispatch. Those are the steps that were
    /// failing when the player reported that clicking the digits did nothing, so they are the steps
    /// worth testing.
    ///
    /// It aims at SKey_3 the way the player would, presses through the identical code path
    /// <see cref="EsCrosshairPointer"/> uses, and then reads the keypad ECHO to see whether a digit
    /// actually landed. The echo is the observable, not the click: a dispatch that looks fine and
    /// types nothing is exactly the bug being hunted.
    /// </summary>
    [MenuItem("Tools/Escape Room/Self Test 7 - crosshair click on a safe digit")]
    public static void SelfTest7CrosshairKeypad()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[SelfTest7] Camera.main is NULL"); return; }
        var es = EventSystem.current;
        if (es == null) { Debug.LogError("[SelfTest7] EventSystem.current is NULL"); return; }
        var pad = Object.FindFirstObjectByType<EsSafeKeypad>();
        if (pad == null || pad.keypadRoot == null) { Debug.LogError("[SelfTest7] no safe keypad"); return; }
        var xp = es.GetComponent<EsCrosshairPointer>();
        if (xp == null) { Debug.LogError("[SelfTest7] no EsCrosshairPointer"); return; }
        var rig = cam.GetComponent<EsFirstPersonCameraRig>();
        if (rig == null || rig.body == null) { Debug.LogError("[SelfTest7] camera rig not wired"); return; }

        // Deterministic even after Self Test 2 solved the safe: a fresh entry.
        pad.DebugReset();
        Canvas.ForceUpdateCanvases();

        var key3 = pad.keypadRoot.transform.Find("SKey_3") as RectTransform;
        if (key3 == null) { Debug.LogError("[SelfTest7] SKey_3 not found"); return; }

        var corners = new Vector3[4];
        key3.GetWorldCorners(corners);
        Vector3 target = (corners[0] + corners[2]) * 0.5f;

        // Stand where a player stands to use the safe: close, in front of it.
        Vector3 flat = target - rig.body.position;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f) flat = Vector3.forward;
        flat.Normalize();
        var stand = target - flat * 1.6f;
        rig.body.position = new Vector3(stand.x, rig.body.position.y, stand.z);
        float yaw, pitch;
        rig.LookAtPoint(target, out yaw, out pitch);
        rig.SetLook(yaw, pitch);

        // The identical sequence EsCrosshairPointer.Update runs on a click.
        var found = xp.FindTarget();
        var mouse = UnityEngine.InputSystem.Mouse.current;
        var normal = mouse != null
            ? ResolveAt(es, mouse.position.ReadValue())
            : null;
        Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, target);

        Debug.Log("[SelfTest7] aimed at SKey_3 from " + Vector3.Distance(cam.transform.position, target).ToString("F2")
                  + " m; FindTarget='" + (found == null ? "NOTHING" : found.name)
                  + "', frozenOScursor=" + (mouse != null ? mouse.position.ReadValue().ToString("F0") : "n/a")
                  + " -> normalPathWouldHit='" + (normal == null ? "nothing" : normal.name) + "'");
        Debug.Log("[SelfTest7] SKey_3 on screen at " + sp.ToString("F0") + " of " + Screen.width + "x" + Screen.height);

        if (found != key3.gameObject)
        {
            Debug.LogError("[SelfTest7] FAIL - FindTarget did not return SKey_3, so no click could reach it");
        }
        else
        {
            var ped = new UnityEngine.EventSystems.PointerEventData(es)
            {
                position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f),
                button = UnityEngine.EventSystems.PointerEventData.InputButton.Left,
                clickTime = Time.unscaledTime,
                clickCount = 1,
                eligibleForClick = true,
                pointerPress = found,
                pointerClick = found
            };
            UnityEngine.EventSystems.ExecuteEvents.Execute(
                found, ped, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
        }

        // The observable: did a digit actually land?
        string shown = "?";
        foreach (var t in pad.keypadRoot.GetComponentsInChildren<UnityEngine.UI.Text>(true))
            if (t.name == "SafeEcho") shown = t.text;
        Debug.Log("[SelfTest7] safe echo = '" + shown + "'   "
                  + (shown.StartsWith("3") ? "PASS (the digit landed)" : "FAIL (nothing was typed)"));
    }


    /// <summary>Reports every geometry overlap involving the generated <c>Shell</c>.
    ///
    /// Overlapping floor and ceiling geometry is a z-fighting bug that is invisible in the
    /// hierarchy and impossible to reason about from transforms, because what matters is the
    /// RENDERED bounds - the kit's own floor and ceiling pieces are separate objects with their own
    /// scale, and the filler pieces are sized to close gaps between them. Two coplanar surfaces at
    /// the same height will both be drawn and the view breaks.
    ///
    /// Only pairs involving the generated Shell are reported, because the Shell is what this project
    /// adds; the kit's own pieces overlapping each other is not ours to arbitrate. The intersection
    /// box is printed in centimetres because "they overlap" is not actionable - "they overlap by
    /// 4 cm" is.
    /// </summary>
    [MenuItem("Tools/Escape Room/Check shell geometry overlaps")]
    public static void CheckShellOverlaps()
    {
        var shell = GameObject.Find("EscapeRoom_Root/Shell");
        if (shell == null) { Debug.LogError("[ShellOverlap] EscapeRoom_Root/Shell not found"); return; }

        var mine = new List<Renderer>();
        foreach (var r in shell.GetComponentsInChildren<Renderer>()) mine.Add(r);
        if (mine.Count == 0) { Debug.LogError("[ShellOverlap] Shell has no renderers"); return; }

        var all = Object.FindObjectsByType<Renderer>();
        int pairs = 0, coplanarPairs = 0;
        Debug.Log("[ShellOverlap] Shell has " + mine.Count + " renderer(s); checking against "
                  + all.Length + " in the scene");

        foreach (var a in mine)
        {
            var ba = a.bounds;
            Debug.Log("[ShellOverlap] '" + a.name + "' bounds " + BoundsText(ba));

            foreach (var b in all)
            {
                if (b == a) continue;
                if (a.transform.IsChildOf(b.transform) || b.transform.IsChildOf(a.transform)) continue;
                if (b.GetComponentInParent<Transform>() == shell) continue;   // both ours

                var bb = b.bounds;
                if (!ba.Intersects(bb)) continue;
                var inter = Intersection(ba, bb);
                if (inter.x <= 0.005f || inter.y <= 0.005f || inter.z <= 0.005f) continue;
                pairs++;

                // Interpenetration is not the problem; COPLANAR faces are. Two solids that merely
                // intersect have no shared surface, and the seam is invisible. Two solids whose
                // faces lie in the same plane are both drawn at the same depth and flicker, and
                // that is the artefact the player reported. Telling those apart is what makes this
                // check worth running - a checker that calls every intersection a z-fight trains you
                // to ignore it.
                float coplanar = CoplanarFaces(ba, bb);
                string path = b.transform.parent != null
                    ? b.transform.parent.name + "/" + b.name : b.name;

                if (coplanar >= 0f)
                {
                    coplanarPairs++;
                    Debug.Log(string.Format(
                        "[ShellOverlap]   COPLANAR with '{0}' on the {1} plane at {2:F3} m"
                        + "  (overlap {3:F1} x {4:F1} x {5:F1} cm)   <== Z-FIGHTS",
                        path, AxisName(coplanar), coplanar, inter.x * 100f, inter.y * 100f, inter.z * 100f));
                }
                else
                {
                    Debug.Log(string.Format(
                        "[ShellOverlap]   intersects '{0}' by {1:F1} x {2:F1} x {3:F1} cm (no shared face - harmless)",
                        path, inter.x * 100f, inter.y * 100f, inter.z * 100f));
                }
            }
        }

        if (coplanarPairs == 0)
            Debug.Log("[ShellOverlap] " + pairs + " intersection(s), 0 coplanar. RESULT = clean (no z-fighting)");
        else
            Debug.LogError("[ShellOverlap] " + coplanarPairs + " COPLANAR pair(s) of " + pairs
                           + " intersection(s) - these will z-fight.");
    }

    const float COPLANAR_TOL = 0.005f;

    /// <summary>Distance at which two bounds share a face plane, or -1 when they do not.
    /// Returns the plane's offset so the log can name the height/depth it sits at.</summary>
    static float CoplanarFaces(Bounds a, Bounds b)
    {
        if (Mathf.Abs(a.min.y - b.min.y) < COPLANAR_TOL) return a.min.y;
        if (Mathf.Abs(a.max.y - b.max.y) < COPLANAR_TOL) return a.max.y;
        if (Mathf.Abs(a.min.x - b.min.x) < COPLANAR_TOL) return a.min.x;
        if (Mathf.Abs(a.max.x - b.max.x) < COPLANAR_TOL) return a.max.x;
        if (Mathf.Abs(a.min.z - b.min.z) < COPLANAR_TOL) return a.min.z;
        if (Mathf.Abs(a.max.z - b.max.z) < COPLANAR_TOL) return a.max.z;
        return -1f;
    }

    static string AxisName(float plane)
    {
        // The caller knows the overlap is against a horizontal surface in every case observed; a
        // vertical tie would need the axis threaded through, so report the value and let the log
        // line above it identify which.
        return "y=" + plane.ToString("F3");
    }

    /// <summary>Size of the overlapping volume of two bounds, zero on any axis that does not
    /// overlap. Returned as a size rather than a Bounds because only the extents are reported.</summary>
    static Vector3 Intersection(Bounds a, Bounds b)
    {
        Vector3 mn = Vector3.Max(a.min, b.min);
        Vector3 mx = Vector3.Min(a.max, b.max);
        Vector3 size = mx - mn;
        return new Vector3(Mathf.Max(0f, size.x), Mathf.Max(0f, size.y), Mathf.Max(0f, size.z));
    }

    static string BoundsText(Bounds b)
    {
        return string.Format("x {0:F2}..{1:F2}  y {2:F2}..{3:F2}  z {4:F2}..{5:F2}",
            b.min.x, b.max.x, b.min.y, b.max.y, b.min.z, b.max.z);
    }

    /// <summary>Checks every grabbable item: collider coverage, reachability by raycast, and size.
    ///
    /// The key and the cell were rebuilt as multi-part geometry (D-101), and a multi-part prop is
    /// exactly where collider assumptions break. D-55 already paid for this once: the kit's
    /// Column_01_Top ships with no collider at all and the player walked through it, invisibly.
    ///
    /// Two failure modes are checked separately because they look identical to the player and are
    /// not the same bug. A collider that does not COVER the mesh means the click misses a part you
    /// can see. A collider that is covered but not REACHABLE means the item is on the floor, under
    /// a wall, or beyond the 3.4 m interaction range - and since the key and the cell gate the
    /// puzzle, that is an unwinnable run rather than a bug report.
    /// </summary>
    [MenuItem("Tools/Escape Room/Check grabbable items")]
    public static void CheckGrabbableItems()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[Items] Camera.main is NULL"); return; }
        var items = Object.FindObjectsByType<GrabbableItem>();
        if (items.Length == 0) { Debug.LogError("[Items] no GrabbableItem in the scene"); return; }

        int bad = 0;
        Debug.Log("[Items] " + items.Length + " grabbable item(s); reachability probed from 1.5 m in front of each");

        foreach (var it in items)
        {
            var go = it.gameObject;
            var rend = new Bounds();
            bool first = true;
            int meshes = 0;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!first) rend.Encapsulate(r.bounds); else { rend = r.bounds; first = false; }
                meshes++;
            }

            var col = go.GetComponent<Collider>();
            bool hasCol = col != null;
            float covH = 0f, covV = 0f;
            if (hasCol)
            {
                var cb = col.bounds;
                covH = cb.size.x > 0f ? Mathf.Min(1f, cb.size.x / Mathf.Max(0.0001f, rend.size.x)) : 0f;
                covV = cb.size.y > 0f ? Mathf.Min(1f, cb.size.y / Mathf.Max(0.0001f, rend.size.y)) : 0f;
            }
            bool covers = hasCol && covH > 0.85f && covV > 0.85f;

            // reachability: park a ray 1.5 m out along the item's own forward and see if it lands
            Vector3 probeFrom = go.transform.position + go.transform.forward * 1.5f;
            Vector3 dir = (go.transform.position - probeFrom).normalized;
            bool reachable = Physics.Raycast(probeFrom, dir, 2.0f, ~0, QueryTriggerInteraction.Collide);

            var rb = go.GetComponent<Rigidbody>();
            Debug.Log(string.Format(
                "[Items] '{0}' parts={1} mesh={2:F3} x {3:F3} x {4:F3} m  collider={5} cover={6:P0}  raycast={7}  kinematic={8}",
                it.displayName, meshes, rend.size.x, rend.size.y, rend.size.z,
                hasCol ? col.GetType().Name : "NONE", covH, reachable ? "HIT" : "MISS", rb != null && rb.isKinematic));

            if (!hasCol) { bad++; continue; }
            if (!covers) { bad++; Debug.LogError("[Items]   collider does not cover the mesh"); }
            if (!reachable) { bad++; Debug.LogError("[Items]   not reachable by a raycast 1.5 m away - the player cannot pick it up"); }
        }

        if (bad == 0) Debug.Log("[Items] RESULT = all items have a covering, reachable collider");
        else Debug.LogError("[Items] " + bad + " problem(s).");
    }

    /// <summary>Proves the end panel hands the mouse back.
    ///
    /// D-99 switched the input module off while the cursor is locked, which is what made REINICIAR
    /// unclickable: the end panel is a Screen Space OVERLAY canvas, and with the module off the only
    /// pointer that exists is the crosshair, which no longer aims at a menu. The fix is in
    /// <c>ShowEnd</c>, which releases the cursor - but "it should release the cursor" is a comment,
    /// not a fact, and the failure is a dead button on the payoff screen.
    ///
    /// Sets the panel up, asserts the lock is gone and the module is usable again, then restores.
    /// </summary>
    /// <summary>The drop path must not leave the interactor claiming a dropped item.
    ///
    /// Reproduces the stuck state directly rather than trying to fake a mouse press: force the
    /// interactor to claim an item, drop that item behind its back so the two disagree, then assert
    /// the reconcile heals it. That is the exact state the player got stuck in - the interactor
    /// believed it held something, so every click took the drop branch and the pick-up branch was
    /// unreachable, for the rest of the run.
    ///
    /// The first version of the pickup diagnostic missed this entirely, because it called
    /// <c>Drop()</c> on the item and skipped the interactor - the very layer the bug was in. A test
    /// that exercises the component under suspicion is worth more here than one that exercises the
    /// component next to it.</summary>
    [MenuItem("Tools/Escape Room/Self Test 9 - drop does not wedge the interactor")]
    public static void SelfTest9DropDoesNotWedge()
    {
        var inter = Object.FindFirstObjectByType<PlayerInteractor>();
        if (inter == null) { Debug.LogError("[SelfTest9] no PlayerInteractor in the scene"); return; }
        var item = Object.FindFirstObjectByType<GrabbableItem>();
        if (item == null) { Debug.LogError("[SelfTest9] no GrabbableItem in the scene"); return; }

        // 1. the interactor claims it, and the item agrees
        inter.ForceClaim(item);
        item.PickUp(inter.HoldTransform);
        Debug.Log("[SelfTest9] claimed + picked: interactor.Held=" + (inter.Held != null)
                  + " item.IsHeld=" + item.IsHeld + "  (both should be True)");

        // 2. it gets dropped BEHIND the interactor's back - this is the whole failure
        item.Drop();
        bool stuck = inter.Held != null && !item.IsHeld;
        Debug.Log("[SelfTest9] after a drop: interactor.Held=" + (inter.Held != null ? "set" : "null")
                  + " item.IsHeld=" + item.IsHeld + "   <- this is the stuck state");
        Debug.Log("[SelfTest9] with the interactor still claiming a dropped item, every click takes "
                  + "the DROP branch and the pick-up branch is unreachable: "
                  + (stuck ? "REPRODUCED" : "not reproduced (the drop branch already clears it)"));

        // 3. the reconcile must clear it
        inter.ReconcileHeld();
        bool healed = inter.Held == null;
        Debug.Log("[SelfTest9] after ReconcileHeld(): interactor.Held=" + (inter.Held != null ? "set" : "null"));

        // 4. and the item must be pickable again by the real code path
        inter.ForceClaim(null);
        item.PickUp(inter.HoldTransform);
        bool repickable = item.IsHeld;
        item.Drop();
        inter.ReconcileHeld();
        Debug.Log("[SelfTest9] re-pick after the drop: IsHeld=" + repickable
                  + "   (should be True)");

        bool pass = healed && repickable;
        Debug.Log("[SelfTest9] VERDICT = " + (pass ? "PASS" : "FAIL")
                  + " (reconcile cleared the claim: " + healed + ", item re-pickable: " + repickable + ")");
    }

    /// <summary>The held item must not eat the insert click.
    ///
    /// A plate carried to the reader hangs 1.9 m in front of the camera,
    /// directly under the crosshair ray. A single
    /// Raycast returns whatever is nearest, so a click aimed at the socket could land on the plate
    /// instead - no device, DROP branch, insert becomes drop. PlayerInteractor.FirstSolidHit
    /// skips the held subtree; this stands the player 2 m in front of a plate slot with a plate
    /// at the hold point and asserts the click resolves to the slot.
    ///
    /// Geometry only, no PickUp: in edit mode the rigidbody was never awoken, so PickUp would
    /// NRE on _rb - and the ray only cares about geometry, which is identical either way.</summary>
    [MenuItem("Tools/Escape Room/Self Test 10 - held item does not block its own insert")]
    public static void SelfTest10HeldItemRay()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[SelfTest10] Camera.main is NULL"); return; }
        var rig = cam.GetComponent<EsFirstPersonCameraRig>();
        if (rig == null || rig.body == null) { Debug.LogError("[SelfTest10] camera rig not wired"); return; }
        var inter = Object.FindFirstObjectByType<PlayerInteractor>();
        if (inter == null) { Debug.LogError("[SelfTest10] no PlayerInteractor"); return; }
        GrabbableItem key = null;
        foreach (var it in Object.FindObjectsByType<GrabbableItem>(FindObjectsSortMode.None))
            if (it.itemKey == "placa") key = it;
        if (key == null) { Debug.LogError("[SelfTest10] no plate item"); return; }
        ItemSocket slot = null;
        foreach (var s in Object.FindObjectsByType<ItemSocket>(FindObjectsSortMode.None))
            if (s.acceptsKey == "placa") slot = s;
        if (slot == null) { Debug.LogError("[SelfTest10] no plate slot"); return; }

        var hold = inter.HoldTransform != null ? inter.HoldTransform : cam.transform;
        Vector3 bodyPos0 = rig.body.position;
        Quaternion bodyRot0 = rig.body.rotation;
        Vector3 keyPos0 = key.transform.position;
        Quaternion keyRot0 = key.transform.rotation;

        var scol = slot.GetComponent<Collider>();
        Vector3 seat = scol != null ? scol.bounds.center : slot.transform.position;
        Vector3 flat = seat - rig.body.position;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f) flat = Vector3.forward;
        flat.Normalize();
        rig.body.position = new Vector3(seat.x - flat.x * 2.0f, bodyPos0.y, seat.z - flat.z * 2.0f);
        float yaw, pitch;
        rig.LookAtPoint(seat, out yaw, out pitch);
        rig.SetLook(yaw, pitch);

        // The key hanging in the hand: same formula GrabbableItem follows in FixedUpdate.
        Vector3 target = hold.position + hold.forward * key.holdDistance + Vector3.up * key.holdHeight;
        key.transform.SetPositionAndRotation(target, hold.rotation);

        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        var hits = Physics.RaycastAll(ray, inter.interactDistance, ~0, QueryTriggerInteraction.Collide);
        RaycastHit first;
        bool hasHit = PlayerInteractor.FirstSolidHit(hits, key, out first);
        IInteractable dev = hasHit ? first.collider.GetComponentInParent<IInteractable>() : null;

        bool pass10 = dev == (IInteractable)slot;
        Debug.Log("[SelfTest10] held plate at " + target.ToString("F2") + ", socket " + seat.ToString("F2")
                  + " d=" + Vector3.Distance(cam.transform.position, seat).ToString("F2")
                  + "; first solid hit = " + (hasHit ? first.collider.name + " at " + first.distance.ToString("F2") : "NONE")
                  + " -> " + (dev == null ? "nothing" : (dev == (IInteractable)slot ? "the plate slot" : "something else")));
        Debug.Log("[SelfTest10] VERDICT = " + (pass10 ? "PASS (the insert click reaches the slot past the held plate)"
                                                      : "FAIL (the held plate or an occluder eats the click - insert becomes drop)"));

        key.transform.SetPositionAndRotation(keyPos0, keyRot0);
        rig.body.SetPositionAndRotation(bodyPos0, bodyRot0);
    }


    /// <summary>Puzzle 1 (Fase 1), end to end in edit mode: the parity table, the
    /// solved state, pip-geometry-vs-logic, the reward, one leak breaking it, and
    /// the discriminating case — receptor fed while a leak stands, which the
    /// simplified path-only rule would WRONGLY accept. That last case is the
    /// whole reason the win rule is strict (D-180). Clips muted (no audio pool
    /// in edit mode); room light and rotations captured and restored.</summary>
    [MenuItem("Tools/Escape Room/Self Test 13 - hex grid solve and leak rule")]
    public static void SelfTest13HexGrid()
    {
        var grid = Object.FindFirstObjectByType<EsHexGrid>();
        if (grid == null) { Debug.LogError("[SelfTest13] no EsHexGrid in the scene"); return; }
        int fails = 0;

        var saveRot = new Dictionary<EsHexNode, int>();
        foreach (var n in grid.nodes) saveRot[n] = n.rotation;
        var rc = grid.rotateClip; var sc = grid.solvedClip;
        grid.rotateClip = null; grid.solvedClip = null;
        bool powWas = grid.powerRestored;
        grid.DebugResetPower();
        Color lightC = Color.black; float lightI = 0f;
        bool hasLight = grid.gm != null && grid.gm.roomLight != null;
        if (hasLight) { lightC = grid.gm.roomLight.color; lightI = grid.gm.roomLight.intensity; }

        // 1. neighbour parity table round-trips everywhere
        int bad = 0, total = 0;
        for (int c = 0; c < grid.cols; c++)
            for (int r = 0; r < grid.rows; r++)
                for (int d = 0; d < 6; d++)
                {
                    int nc, nr;
                    if (!EsHexGrid.Neighbour(c, r, d, grid.cols, grid.rows, out nc, out nr)) continue;
                    total++;
                    int bc, br;
                    if (!EsHexGrid.Neighbour(nc, nr, EsHexGrid.Opp(d), grid.cols, grid.rows, out bc, out br)
                        || bc != c || br != r) bad++;
                }
        bool ok1 = bad == 0 && total > 0;
        if (!ok1) fails++;
        Debug.Log("[SelfTest13] neighbour round-trip " + (total - bad) + "/" + total + "   "
                  + (ok1 ? "PASS" : "FAIL"));

        // 1b. table-vs-layout DISTANCE: every table link must join cells exactly
        // one hex pitch apart. Round-trip alone cannot catch a mirrored table
        // (a mirror preserves Opp-symmetry and still round-trips 66/66) while
        // geometrically mating ports never meet - the 400/400-clean failure.
        int badDist = 0, totalDist = 0;
        float hexPitch = Mathf.Sqrt(3f) * grid.cellSize;
        for (int c = 0; c < grid.cols; c++)
            for (int r = 0; r < grid.rows; r++)
                for (int d = 0; d < 6; d++)
                {
                    int nc, nr;
                    if (!EsHexGrid.Neighbour(c, r, d, grid.cols, grid.rows, out nc, out nr)) continue;
                    totalDist++;
                    float dist = (EsHexGrid.CellLocal(nc, nr, grid.cellSize)
                                - EsHexGrid.CellLocal(c, r, grid.cellSize)).magnitude;
                    if (Mathf.Abs(dist - hexPitch) > 0.001f)
                    {
                        badDist++;
                        if (badDist <= 3)
                            Debug.Log("[SelfTest13]   off-pitch link (" + c + "," + r + ") dir " + d
                                      + " -> (" + nc + "," + nr + ") dist " + dist.ToString("F3")
                                      + " want " + hexPitch.ToString("F3"));
                    }
                }
        bool ok1c = badDist == 0 && totalDist > 0;
        if (!ok1c) fails++;
        Debug.Log("[SelfTest13] neighbour layout distance " + (totalDist - badDist) + "/" + totalDist + "   "
                  + (ok1c ? "PASS" : "FAIL (mirrored table)"));

        // route-adjacent rotatable decoys (the 2 terminator caps live here):
        // the leak rule needs something to bite.
        int adjRot = 0;
        var pathKeys = new HashSet<int>();
        foreach (var n in grid.nodes) if (n.isPath) pathKeys.Add(n.col * grid.rows + n.row);
        foreach (var n in grid.nodes)
        {
            if (n.isPath || n.isFixed) continue;
            for (int d = 0; d < 6; d++)
            {
                int nc, nr;
                if (EsHexGrid.Neighbour(n.col, n.row, d, grid.cols, grid.rows, out nc, out nr)
                    && pathKeys.Contains(nc * grid.rows + nr)) { adjRot++; break; }
            }
        }
        bool ok1b = adjRot >= 1;
        if (!ok1b) fails++;
        Debug.Log("[SelfTest13] route-adjacent rotatables = " + adjRot + "   "
                  + (ok1b ? "PASS (caps present)" : "FAIL (no caps?)"));

        // 2. the shipped solution solves
        foreach (var n in grid.nodes) grid.SetRotationSilent(n, n.solvedRotation);
        bool ok2 = grid.IsSolved();
        if (!ok2) fails++;
        Debug.Log("[SelfTest13] solution rotations -> IsSolved=" + ok2 + "   "
                  + (ok2 ? "PASS" : "FAIL"));

        // 3. pip geometry matches logic on the emitter
        EsHexNode emitter = null;
        foreach (var n in grid.nodes) if (n.kind == NodeKind.Emitter) emitter = n;
        bool ok3 = false;
        if (emitter != null)
        {
            int bits = 0, d0 = -1;
            for (int d = 0; d < 6; d++) if ((emitter.baseMask & (1 << d)) != 0) { bits++; d0 = d; }
            float want = ((d0 + emitter.solvedRotation) * 60f) % 360f;
            if (bits >= 1 && emitter.pips.Count == bits)
            {
                foreach (var p in emitter.pips)
                {
                    Vector3 lp = p.transform.localPosition;
                    float got = Mathf.Atan2(lp.y, lp.x) * Mathf.Rad2Deg;
                    if (got < 0f) got += 360f;
                    float diff = Mathf.Abs(got - want);
                    if (diff > 180f) diff = 360f - diff;
                    if (diff < 2f) { ok3 = true; break; }
                }
            }
            Debug.Log("[SelfTest13] emitter port dir=" + d0 + " want=" + want.ToString("F1")
                      + " pips=" + emitter.pips.Count + "   " + (ok3 ? "PASS" : "FAIL"));
        }
        else Debug.LogError("[SelfTest13] no emitter node");
        if (!ok3) fails++;

        // 4. the reward fires: latch + room goes blue
        grid.CheckAndReward();
        bool blue = hasLight && grid.gm.roomLight.color.b > grid.gm.roomLight.color.r;
        bool ok4 = grid.powerRestored && blue;
        if (!ok4) fails++;
        Debug.Log("[SelfTest13] reward: latched=" + grid.powerRestored + " room="
                  + (hasLight ? grid.gm.roomLight.color.ToString("F2") : "n/a") + "   "
                  + (ok4 ? "PASS (red -> blue)" : "FAIL"));

        // 5. one leak breaks it
        EsHexNode pathNode = null;
        foreach (var n in grid.nodes) if (n.isPath && !n.isFixed) { pathNode = n; break; }
        grid.DebugResetPower();
        if (pathNode != null) grid.SetRotationSilent(pathNode, (pathNode.solvedRotation + 1) % 6);
        bool ok5 = !grid.IsSolved();
        if (!ok5) fails++;
        Debug.Log("[SelfTest13] one path node +60 -> IsSolved=" + grid.IsSolved() + "   "
                  + (ok5 ? "PASS" : "FAIL"));

        // 6. THE discriminating case: receptor fed, leak standing. Searched over
        // one- then two-move perturbations of the solution; path-only validation
        // would accept these, the strict rule must not.
        foreach (var n in grid.nodes) grid.SetRotationSilent(n, n.solvedRotation);
        grid.DebugResetPower();
        bool found = DiscriminatingLeak(grid, 1) || DiscriminatingLeak(grid, 2);
        if (!found) fails++;
        Debug.Log("[SelfTest13] receptor-fed-with-leak rejected = " + found + "   "
                  + (found ? "PASS (strict rule bites)" : "FAIL - path-only would accept everything"));

        // 7. the REAL click path reaches a node: stand 2 m in front of one and
        // resolve through FirstSolidHit, the same routine play uses (SelfTest10
        // pattern). Proves colliders, layers and distance, not just logic.
        // Runs BEFORE the restore below, while clips are still muted.
        var cam = Camera.main;
        var rig = cam != null ? cam.GetComponent<EsFirstPersonCameraRig>() : null;
        var inter = Object.FindFirstObjectByType<PlayerInteractor>();
        EsHexNode target = null;
        foreach (var n in grid.nodes) if (!n.isFixed) { target = n; break; }
        bool ok7 = false;
        if (cam != null && rig != null && rig.body != null && inter != null && target != null)
        {
            Vector3 bodyPos0 = rig.body.position;
            Quaternion bodyRot0 = rig.body.rotation;
            var tcol = target.GetComponent<Collider>();
            Vector3 seat = tcol != null ? tcol.bounds.center : target.transform.position;
            // Due south of the node, not along the body->node diagonal: from the
            // southwest the locker stands in front of the panel's west edge
            // (measured: LockerRight wins the ray at d=0.99). A real player
            // sidesteps to face the panel; the test stands where they would.
            rig.body.position = new Vector3(seat.x, bodyPos0.y, seat.z + 2.0f);
            float yaw, pitch;
            rig.LookAtPoint(seat, out yaw, out pitch);
            rig.SetLook(yaw, pitch);
            Physics.SyncTransforms();
            int rotBefore = target.rotation;
            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            var hits = Physics.RaycastAll(ray, inter.interactDistance, ~0, QueryTriggerInteraction.Collide);
            RaycastHit first;
            EsHexNode got = null;
            if (PlayerInteractor.FirstSolidHit(hits, null, out first))
                got = first.collider.GetComponentInParent<EsHexNode>();
            if (got == target)
            {
                got.Interact(null);
                ok7 = target.rotation == (rotBefore + 1) % 6;
                grid.SetRotationSilent(target, rotBefore);
            }
            rig.body.SetPositionAndRotation(bodyPos0, bodyRot0);
            Physics.SyncTransforms();
            Debug.Log("[SelfTest13] click path -> " + (got == null ? "NOTHING" : got.name)
                      + " rotated=" + ok7 + "   " + (ok7 ? "PASS" : "FAIL"));
        }
        else Debug.LogError("[SelfTest13] no camera rig/interactor for the click-path case");
        if (!ok7) fails++;

        // 8. leaf visual contract (D-183): single-pip hexes never wear route
        // colors (teal dark/live only), multi-pip normals never wear leaf
        // colors. Holds lit or dark - RefreshAll already ran for this board.
        int leaves = 0, badLeaf = 0;
        foreach (var n in grid.nodes)
        {
            if (n.kind != NodeKind.Normal || n.tileRenderer == null) continue;
            var tm = n.tileRenderer.sharedMaterial;
            if (n.isLeaf)
            {
                leaves++;
                if (tm != grid.leafMat && tm != grid.leafLiveMat) badLeaf++;
            }
            else if (tm == grid.leafMat || tm == grid.leafLiveMat) badLeaf++;
        }
        bool ok8 = leaves >= 2 && badLeaf == 0 && grid.leafMat != null && grid.leafLiveMat != null
                   && grid.leafMat != grid.baseMat && grid.leafLiveMat != grid.liveTileMat;
        if (!ok8) fails++;
        Debug.Log("[SelfTest13] leaf colors: leaves=" + leaves + " violations=" + badLeaf + "   "
                  + (ok8 ? "PASS (1-pip hexes teal)" : "FAIL"));

        // restore: rotations, latch, light, clips
        foreach (var n in grid.nodes) grid.SetRotationSilent(n, saveRot[n]);
        grid.DebugResetPower();
        if (powWas)
        {
            foreach (var n in grid.nodes) grid.SetRotationSilent(n, n.solvedRotation);
            grid.CheckAndReward();
            foreach (var n in grid.nodes) grid.SetRotationSilent(n, saveRot[n]);
            grid.DebugResetPower();
        }
        if (hasLight) { grid.gm.roomLight.color = lightC; grid.gm.roomLight.intensity = lightI; }
        grid.rotateClip = rc; grid.solvedClip = sc;

        Debug.Log("[SelfTest13] VERDICT = " + (fails == 0 ? "PASS (8/8)" : "FAIL (" + fails + ")")
                  + " - board restored");
    }

    /// <summary>Searches k-move perturbations of the shipped solution for a state
    /// with the receptor energized but IsSolved() false. depth=1 covers single
    /// slips; depth=2 covers pairs. Leaves the board solved and unlatched.</summary>
    static bool DiscriminatingLeak(EsHexGrid grid, int depth)
    {
        var movers = new List<EsHexNode>();
        foreach (var n in grid.nodes) if (!n.isFixed) movers.Add(n);
        if (depth == 1)
        {
            foreach (var n in movers)
                for (int r = 0; r < 6; r++)
                {
                    if (r == n.solvedRotation) continue;
                    grid.SetRotationSilent(n, r);
                    bool hit = grid.ReceptorEnergized() && !grid.IsSolved();
                    grid.SetRotationSilent(n, n.solvedRotation);
                    if (hit)
                    {
                        Debug.Log("[SelfTest13]   witness: " + n.name + " at " + r
                                  + " feeds the receptor through a leak");
                        return true;
                    }
                }
            return false;
        }
        for (int i = 0; i < movers.Count; i++)
            for (int r = 0; r < 6; r++)
            {
                if (r == movers[i].solvedRotation) continue;
                grid.SetRotationSilent(movers[i], r);
                for (int j = i + 1; j < movers.Count; j++)
                    for (int s = 0; s < 6; s++)
                    {
                        if (s == movers[j].solvedRotation) continue;
                        grid.SetRotationSilent(movers[j], s);
                        bool hit = grid.ReceptorEnergized() && !grid.IsSolved();
                        grid.SetRotationSilent(movers[j], movers[j].solvedRotation);
                        if (hit)
                        {
                            grid.SetRotationSilent(movers[i], movers[i].solvedRotation);
                            Debug.Log("[SelfTest13]   witness: " + movers[i].name + "@" + r
                                      + " + " + movers[j].name + "@" + s);
                            return true;
                        }
                    }
                grid.SetRotationSilent(movers[i], movers[i].solvedRotation);
            }
        return false;
    }

    /// <summary>Self Test 14: log sort solves to PIN 3719 (edit-safe, no UI drive).</summary>
    [MenuItem("Tools/Escape Room/Self Test 14 - log sort yields PIN 3719")]
    public static void SelfTest14LogSort()
    {
        int fails = 0;
        var log = Object.FindFirstObjectByType<EsLogSortPuzzle>();
        if (log == null) { Debug.LogError("[SelfTest14] no EsLogSortPuzzle"); return; }

        uint[] vals = new uint[log.packetHex.Length];
        for (int i = 0; i < vals.Length; i++) vals[i] = EsLogSortPuzzle.ParseHex(log.packetHex[i]);
        bool chrono = vals[0] < vals[1] && vals[1] < vals[2] && vals[2] < vals[3];
        Debug.Log("[SelfTest14] packets chronological = " + chrono + "   "
                  + (!chrono ? "FAIL" : "PASS"));
        if (!chrono) fails++;

        var want = EsLogSortPuzzle.SortedIndices(log.packetHex);
        bool want0123 = want.Length == 4 && want[0] == 0 && want[1] == 1 && want[2] == 2 && want[3] == 3;
        Debug.Log("[SelfTest14] sorted indices = " + string.Join(",", want) + "   "
                  + (!want0123 ? "FAIL" : "PASS"));
        if (!want0123) fails++;

        var grid = log.grid;
        log.grid = null;   // the gate needs play-mode power; the sort logic does not
        log.DebugReset();
        log.SetSlotsForTest(new int[] { 0, 1, 2, 3 });
        string pin = log.DerivedPin();
        bool okSolve = log.solved && pin == "3719";
        Debug.Log("[SelfTest14] solved=" + log.solved + " pin=" + pin + "   "
                  + (!okSolve ? "FAIL" : "PASS (3719)"));
        if (!okSolve) fails++;

        log.DebugReset();
        log.SetSlotsForTest(new int[] { 2, 0, 3, 1 });
        bool okReject = !log.solved;
        Debug.Log("[SelfTest14] scrambled accepted = " + log.solved + "   "
                  + (!okReject ? "FAIL" : "PASS (refused)"));
        if (!okReject) fails++;

        log.DebugReset();
        log.grid = grid;
        Debug.Log("[SelfTest14] VERDICT = " + (fails == 0 ? "PASS (4/4)" : "FAIL (" + fails + ")"));
    }

    /// <summary>Self Test 15: plates seat, gabarito rotations OR to digit 4,
    /// the reader latches and the door opens. Full scene drive with restore.</summary>
    [MenuItem("Tools/Escape Room/Self Test 15 - plates OR to digit 4 and open the door")]
    public static void SelfTest15Plates()
    {
        int fails = 0;
        var reader = Object.FindFirstObjectByType<EsPlateReader>();
        if (reader == null) { Debug.LogError("[SelfTest15] no EsPlateReader"); return; }
        var door = Object.FindFirstObjectByType<BlastDoor>();
        if (door == null) { Debug.LogError("[SelfTest15] no BlastDoor"); return; }
        var plates = Object.FindObjectsByType<EsAcrylicPlate>(FindObjectsSortMode.None);
        if (plates.Length != 3) { Debug.LogError("[SelfTest15] plates=" + plates.Length + " (want 3)"); return; }
        System.Array.Sort(plates, (a, b) => a.plateId.CompareTo(b.plateId));

        int[][] expectBase = new int[][]
        {
            new int[] { 0,1,0,0, 0,1,0,0, 0,0,0,0, 1,1,1,0 },
            new int[] { 1,0,0,0, 0,0,0,0, 0,0,1,1, 0,0,0,0 },
            new int[] { 0,0,1,0, 0,0,0,0, 0,0,1,0, 0,1,0,0 },
        };
        for (int i = 0; i < 3; i++)
        {
            bool same = true;
            for (int k = 0; k < 16; k++) if (plates[i].baseCells[k] != expectBase[i][k]) same = false;
            Debug.Log("[SelfTest15] plate " + (i + 1) + " base = " + (same ? "PASS" : "FAIL"));
            if (!same) fails++;
        }

        int[] gab = new int[] { 0, 3, 2 };
        int[] or = EsAcrylicPlate.OrCombine(plates[0].baseCells, gab[0],
                                            plates[1].baseCells, gab[1],
                                            plates[2].baseCells, gab[2]);
        bool orOk = true;
        for (int k = 0; k < 16; k++) if (or[k] != reader.templateCells[k]) orOk = false;
        Debug.Log("[SelfTest15] OR at gabarito = digit 4: " + (orOk ? "PASS" : "FAIL"));
        if (!orOk) fails++;

        // full drive: seat, rotate, latch. Wire() explicitly: assembly reloads
        // wipe non-serialized C# event subscriptions, so a build-time Wire
        // may already be gone; in play Start() does this same call.
        reader.Wire();
        var saveParent = new Transform[3];
        var savePos = new Vector3[3];
        var saveRot = new Quaternion[3];
        for (int i = 0; i < 3; i++)
        {
            var t = plates[i].transform;
            saveParent[i] = t.parent; savePos[i] = t.localPosition; saveRot[i] = t.localRotation;
            var g = plates[i].GetComponent<GrabbableItem>();
            if (g != null && reader.sockets[i] != null) reader.sockets[i].Interact(g);
        }
        bool seated = reader.sockets[0].IsFilled && reader.sockets[1].IsFilled && reader.sockets[2].IsFilled;
        Debug.Log("[SelfTest15] seated 3/3 = " + (seated ? "PASS" : "FAIL"));
        if (!seated) fails++;

        for (int i = 0; i < 3; i++)
        {
            var p = reader.SeatedPlate(i);
            if (p == null) { Debug.LogError("[SelfTest15] slot " + i + " empty after seating"); fails++; continue; }
            int want = gab[p.plateId];
            p.SetRotationSilent(want);
        }
        bool locked = true;
        foreach (var s in reader.sockets)
            if (s != null && s.Item != null && !s.Item.pickupLocked) locked = false;
        Debug.Log("[SelfTest15] seated plates pickup-locked = " + (locked ? "PASS" : "FAIL"));
        if (!locked) fails++;

        var tc = Object.FindFirstObjectByType<TerminalController>();
        bool endWas = tc != null && tc.endPanel != null && tc.endPanel.activeSelf;
        reader.CheckAndReward();
        bool okWin = reader.approved && door.IsOpen;
        Debug.Log("[SelfTest15] approved=" + reader.approved + " door open=" + door.IsOpen + "   "
                  + (!okWin ? "FAIL" : "PASS (A.E.G.I.S. cleared)"));
        if (!okWin) fails++;

        // restore everything the drive touched
        foreach (var s in reader.sockets) if (s != null) s.Reset();
        for (int i = 0; i < 3; i++)
        {
            var g = plates[i].GetComponent<GrabbableItem>();
            if (g != null) g.pickupLocked = false;
            plates[i].SetRotationSilent(0);
            var t = plates[i].transform;
            t.SetParent(saveParent[i], false);
            t.localPosition = savePos[i];
            t.localRotation = saveRot[i];
        }
        reader.DebugReset();
        door.DebugReset();
        if (tc != null && tc.endPanel != null) tc.endPanel.SetActive(endWas);
        Debug.Log("[SelfTest15] VERDICT = " + (fails == 0 ? "PASS (6/6)" : "FAIL (" + fails + ")")
                  + " - scene restored");
    }

    [MenuItem("Tools/Escape Room/Self Test 8 - end panel releases the mouse")]
    public static void SelfTest8EndPanelCursor()
    {
        var tc = Object.FindFirstObjectByType<TerminalController>();
        if (tc == null) { Debug.LogError("[SelfTest8] no TerminalController"); return; }
        var es = EventSystem.current;
        if (es == null) { Debug.LogError("[SelfTest8] no EventSystem"); return; }
        var module = es.GetComponent<BaseInputModule>();
        if (module == null) { Debug.LogError("[SelfTest8] no BaseInputModule"); return; }

        bool panelWas = tc.endPanel != null && tc.endPanel.activeSelf;
        bool btnWas = tc.rebootButton != null && tc.rebootButton.gameObject.activeSelf;
        string savedTitle = tc.endTitle != null ? tc.endTitle.text : null;
        Color savedColor = tc.endTitle != null ? tc.endTitle.color : Color.white;
        string savedBody = tc.endBody != null ? tc.endBody.text : null;

        // arrange the state the failure needs: cursor locked, module off
        Cursor.lockState = CursorLockMode.Locked;
        module.enabled = false;
        Debug.Log("[SelfTest8] before: lock=" + Cursor.lockState + " module.enabled=" + module.enabled);

        tc.ShowEnd("TESTE", "teste", Color.white);

        bool unlocked = Cursor.lockState != CursorLockMode.Locked;
        Debug.Log("[SelfTest8] after ShowEnd: lock=" + Cursor.lockState + " cursor.visible=" + Cursor.visible
                  + " panel=" + (tc.endPanel != null && tc.endPanel.activeSelf) + "   "
                  + (unlocked ? "PASS (mouse is back, so the module returns and REINICIAR works)"
                             : "FAIL - the cursor is still locked, so REINICIAR stays dead"));

        if (tc.endPanel != null) tc.endPanel.SetActive(panelWas);
        if (tc.rebootButton != null) tc.rebootButton.gameObject.SetActive(btnWas);
        if (tc.endTitle != null) { tc.endTitle.text = savedTitle; tc.endTitle.color = savedColor; }
        if (tc.endBody != null) tc.endBody.text = savedBody;
    }

    /// <summary>Measures the doorway and the wall thickness, and reports anything standing in the way.
    ///
    /// Two questions that look alike in the Hierarchy and are not: how THICK the walls are, and
    /// whether anything CROSSES the exit opening. The second is reported by firing rays straight
    /// through the opening from both sides at several heights - inside, where the player stands, and
    /// beyond, in the corridor - because a wall that blocks the exit is invisible from the approach
    /// if the approach is on the other side of it, which is exactly the reported symptom.
    ///
    /// Rays are cast with <c>QueryTriggerInteraction.Ignore</c> so triggers such as the ceiling
    /// slab do not report as blockers; the first version of the EXIT-sign check used <c>Collide</c>
    /// and had to be corrected (D-57).
    /// </summary>
    [MenuItem("Tools/Escape Room/Check doorway and wall thickness")]
    public static void CheckDoorwayAndWalls()
    {
        const float OPEN_L = -7.5f, OPEN_R = -4.5f;
        const float DOOR_Z = -11.45f;

        // ---- wall thickness, measured from rendered bounds, grouped so 99 tiles report as a few types
        var bySize = new Dictionary<string, (int count, float thick, float wide, float tall)>();
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!IsKitWallTile(tr)) continue;
            var rend = tr.GetComponent<Renderer>();
            if (rend == null) continue;
            var b = rend.bounds;
            // the tiles are rotated 90 deg, so the SHORT horizontal axis is the thickness
            float hx = b.size.x, hz = b.size.z;
            float thick = Mathf.Min(hx, hz);
            float wide = Mathf.Max(hx, hz);
            string key = string.Format("{0:F2}x{1:F2}", thick, b.size.y);
            if (bySize.TryGetValue(key, out var e))
                bySize[key] = (e.count + 1, e.thick, e.wide, e.tall);
            else
                bySize[key] = (1, thick, wide, b.size.y);
        }
        Debug.Log("[Doorway] kit wall thickness, from rendered bounds (tiles are rotated 90 deg):");
        foreach (var kv in bySize)
            Debug.Log(string.Format("[Doorway]   {0} tile(s) -> thickness {1:F2} m, "
                                  + "face width {2:F2} m, height {3:F2} m",
                kv.Value.count, kv.Value.thick, kv.Value.wide, kv.Value.tall));

        // ---- anything standing in the opening
        //
        // Swept across the WIDTH, not probed at the centre. The first version fired one column at
        // midX and reported the opening clear, which is true and useless: a 3 m opening is only
        // clear if all 3 m of it are. A wall clipping one edge reads as fine at the centre and is
        // exactly the "half the doorway is wall" the player reported.
        int blocked = 0, tested = 0, doorHits = 0;
        var wallXs = new List<float>();
        float[] xs = { -7.40f, -7.00f, -6.50f, -6.00f, -5.50f, -5.00f, -4.60f };
        float[] heights = { 0.3f, 1.0f, 1.8f, 2.6f, 3.3f };

        foreach (float x in xs)
        {
            foreach (float h in heights)
            {
                // The player stands at z > -11.75 and the exit is NORTH of them, so "looking at the
                // exit" from inside is -Z, which is Vector3.back. An earlier version had these two
                // directions swapped and so probed the empty room on both sides, reporting the
                // opening clear while two wall tiles stood in it. The mesh sweep below is what
                // actually caught it.
                var fromIn = new Vector3(x, h, DOOR_Z + 2.5f);
                var fromOut = new Vector3(x, h, DOOR_Z - 2.5f);
                RaycastHit hitIn, hitOut;
                bool inBlocked = Physics.Raycast(fromIn, Vector3.back, out hitIn, 5.0f, ~0, QueryTriggerInteraction.Ignore);
                bool outBlocked = Physics.Raycast(fromOut, Vector3.forward, out hitOut, 5.0f, ~0, QueryTriggerInteraction.Ignore);
                tested++;
                if (!inBlocked && !outBlocked) continue;

                // A hit is only a DEFECT if it is not the door. The frame, the leaf and the bulkhead
                // are supposed to be in the opening - that is what a closed blast door looks like -
                // and counting them made every run report "blocked" and train the reader to ignore
                // the line. A wall TILE is the thing that must never be there.
                bool inWall = inBlocked && IsWallCollider(hitIn.collider);
                bool outWall = outBlocked && IsWallCollider(hitOut.collider);
                if (!inWall && !outWall) { doorHits++; continue; }

                blocked++;
                if (inWall) wallXs.Add(x);
                if (outWall) wallXs.Add(x);
                // Named by PATH, not by collider name: every wall collider in this kit is called
                // "Wall_Simple_01" - the child mesh - so the name alone cannot say WHICH tile is
                // standing in the door, and a finding you cannot point at is one you cannot act on.
                string who = inWall ? PathOf(hitIn.collider.transform) : PathOf(hitOut.collider.transform);
                Debug.Log(string.Format(
                    "[Doorway]   x={0:F2} y={1:F1}  inside={2} corridor={3}   <== WALL TILE IN THE OPENING: {4}",
                    x, h,
                    inBlocked ? "'" + hitIn.collider.name + "' d=" + hitIn.distance.ToString("F2") : "clear",
                    outBlocked ? "'" + hitOut.collider.name + "' d=" + hitOut.distance.ToString("F2") : "clear",
                    who));
            }
        }

        if (blocked == 0)
            Debug.Log("[Doorway] RESULT: the opening is clear of wall tiles across its full width, "
                      + "from both sides (" + tested + " probes, " + doorHits
                      + " of them hitting the door itself, which is correct)");
        else
            Debug.LogError("[Doorway] " + blocked + " of " + tested + " probes hit a WALL TILE at x = "
                           + string.Join(", ", wallXs) + " - each 10 cm column is one tile face.");

        // ---- geometry with NO collider at all
        //
        // The raycast above can only see colliders, so a clean result proves nothing about a mesh
        // that has none - and a wall standing in the doorway with no collider is exactly a thing
        // that is VISIBLE from one side and invisible from the other: you see it, you walk into it,
        // and the raycast walks straight through. So the opening is also swept as a volume against
        // every renderer, which catches meshes that physics cannot.
        var prism = new Bounds(new Vector3((OPEN_L + OPEN_R) * 0.5f, 1.9f, DOOR_Z),
                               new Vector3(OPEN_R - OPEN_L, 3.8f, 3.0f));
        int meshesIn = 0;
        foreach (var rend in Object.FindObjectsByType<Renderer>())
        {
            if (!rend.bounds.Intersects(prism)) continue;
            var path = rend.transform.parent != null
                ? rend.transform.parent.name + "/" + rend.name : rend.name;
            bool hasCol = rend.GetComponentInParent<Collider>() != null;
            // a surface that merely touches the prism edge is not "crossing" it
            var inter = Intersection(rend.bounds, prism);
            bool crosses = inter.x > 0.02f && inter.y > 0.02f && inter.z > 0.02f;
            if (!crosses) continue;
            meshesIn++;
            Debug.Log(string.Format(
                "[Doorway]   MESH in the opening: '{0}'  overlap {1:F0} x {2:F0} x {3:F0} cm  collider={4}{5}",
                path, inter.x * 100f, inter.y * 100f, inter.z * 100f,
                hasCol ? "yes" : "NONE",
                hasCol ? "" : "   <== VISUAL ONLY, physics cannot see it"));
        }

        if (meshesIn == 0)
            Debug.Log("[Doorway] RESULT: no MESH crosses the opening either. The doorway is clear.");
        else
            Debug.LogError("[Doorway] " + meshesIn + " mesh(es) occupy the exit opening.");
    }

    /// <summary>Disables any wall tile standing inside the exit opening, and says which.
    ///
    /// Two of the user's own <c>Wall_Simple_01</c> tiles cross the doorway by 1.63 m and 1.45 m, so
    /// the only exit was walled off - visible from the corridor, and a dead end from inside, which is
    /// the reported "a wall is crossing the door and you cannot see it from one side". The tiles are
    /// 3 m wide against a 3 m opening, so a single one cannot be slid aside without leaving a gap
    /// either side of it; the opening is a hole in a run of tiles, not a tile that slid.
    ///
    /// They are DISABLED, not moved and not deleted. The project constraint is that the user's 110
    /// objects are not removed or repositioned, and disabling is the only one of those three that is
    /// true - and it is reversible in one click, which moving would not be. The build logs every
    /// name it touches, so nothing happens quietly.
    ///
    /// The opening is swept as a prism against every renderer, so a tile that intrudes is found by
    /// its geometry rather than by a name pattern that a re-export would break. Only tiles that
    /// actually occupy the volume are touched.
    /// </summary>
    static void ClearDoorwayIntruders()
    {
        const float OPEN_L = -7.5f, OPEN_R = -4.5f;
        const float DOOR_Z = -11.45f;

        var prism = new Bounds(new Vector3((OPEN_L + OPEN_R) * 0.5f, 1.9f, DOOR_Z),
                               new Vector3(OPEN_R - OPEN_L, 3.8f, 3.0f));

        int disabled = 0;
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!IsKitWallTile(tr)) continue;
            if (!tr.gameObject.activeSelf) continue;
            var rend = tr.GetComponent<Renderer>();
            if (rend == null) continue;

            var inter = Intersection(rend.bounds, prism);
            if (inter.x <= 0.02f || inter.y <= 0.02f || inter.z <= 0.02f) continue;

            // Only the wall RUN is eligible. Anything already ours is additive geometry and should
            // not be silently switched off by a rule meant for the user's tiles.
            if (tr.IsChildOf(GameObject.Find(ROOT).transform)) continue;

            string parent = tr.parent != null ? tr.parent.name : "(root)";
            Debug.Log("[EscapeRoom] DISABLING wall tile '" + parent + "/" + tr.name
                      + "' - it stands " + (inter.x * 100f).ToString("F0") + " cm inside the exit"
                      + " opening (x " + OPEN_L + " .. " + OPEN_R + "). Re-enable it in the Hierarchy"
                      + " if you want the exit blocked again.");
            tr.gameObject.SetActive(false);
            disabled++;
        }

        if (disabled == 0) Debug.Log("[EscapeRoom] No wall tile intrudes into the exit opening.");
    }

    /// <summary>Reports the state of the player's items, the doorway and the wall build, in one go.
    /// The counterpart to <see cref="ClearDoorwayIntruders"/>: the fix is invisible in the
    /// Hierarchy unless you know to look, so the check has to be able to confirm it.</summary>
    [MenuItem("Tools/Escape Room/Check exit opening is clear")]
    public static void CheckExitOpeningClear()
    {
        const float OPEN_L = -7.5f, OPEN_R = -4.5f;
        const float DOOR_Z = -11.45f;
        var prism = new Bounds(new Vector3((OPEN_L + OPEN_R) * 0.5f, 1.9f, DOOR_Z),
                               new Vector3(OPEN_R - OPEN_L, 3.8f, 3.0f));

        int intruders = 0;
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!IsKitWallTile(tr)) continue;
            if (!tr.gameObject.activeSelf) continue;
            var rend = tr.GetComponent<Renderer>();
            if (rend == null) continue;
            var inter = Intersection(rend.bounds, prism);
            if (inter.x <= 0.02f || inter.y <= 0.02f || inter.z <= 0.02f) continue;
            intruders++;
            Debug.LogError("[ExitOpening] ACTIVE tile '" + tr.name + "' occupies " + (inter.x * 100f).ToString("F0")
                           + " cm of the opening");
        }

        if (intruders == 0)
            Debug.Log("[ExitOpening] RESULT = the opening is clear of active wall tiles");
        else
            Debug.LogError("[ExitOpening] " + intruders + " active tile(s) still block the exit.");
    }

    /// <summary>Brings every kit wall tile to one thickness.
    ///
    /// Measured on this scene's own tiles, the run held three thicknesses at once - 0.25, 0.29 and
    /// 0.32 m against a 3.00 m face - so a run that changes thickness between tiles shows a step at
    /// every joint and reads as broken geometry from any oblique angle. They are set to
    /// <see cref="WALL_THICKNESS"/> instead.
    ///
    /// The thickness axis comes from the tile's mesh, and the local scale is corrected by
    /// measuring the result rather than computed from the transform tree. Both are there to make
    /// the pass idempotent, and each replaced a version that visibly was not - one picked the axis
    /// off the world bounds and could name a different axis on the next run than on this one, the
    /// other assumed the mesh sat on the tile and the group above it sat at scale 1. Neither held
    /// here: these tiles carry the mesh on a child, and a pass that compounds or flips moves the
    /// thickness a little further every time the level is rebuilt.
    ///
    /// Runs BEFORE <see cref="MeasureKitWalls"/>, so the backing shell and the upper wall bands are
    /// derived from the thickened walls instead of the thin ones - otherwise the shell would be
    /// fitted to surfaces that no longer exist.
    /// </summary>

    // =====================================================================
    // DIAGNOSTICOS DOS RELATOS DE 2026-09-27
    //
    // Four player reports, none of which the existing checks could see. Each got its own menu item
    // rather than one combined report, because they are four different questions and a report that
    // answers all of them at once is a report nobody can act on.
    // =====================================================================

    /// <summary>Everything standing in the exit doorway, whatever it is called.
    ///
    /// The earlier check only ever considered objects named <c>Wall_Simple_01</c>, and the player
    /// kept reporting a wall across the exit after that check said the opening was clear. The
    /// likeliest reason is a wall in the kit under a DIFFERENT name - this scene also contains
    /// <c>Wall_Arc_90_01</c> - which a name filter cannot see and a pass-by-name cannot fix. So this
    /// one has no name filter at all: it sweeps the doorway volume and reports whatever is found,
    /// with the full path, so the finding can be pointed at.
    ///
    /// The volume is also deliberately deeper than the door. The earlier prism was 3 m deep and its
    /// near edge fell at z = -9.95, while a wall that stands across the approach can have its near
    /// face at z = -9.60 and still be plainly in the way - which is exactly what the corridor's own
    /// side wall turned out to be (D-110). A prism that is too shallow reports "clear" on a doorway
    /// that is not.
    /// </summary>
    [MenuItem("Tools/Escape Room/Report everything in the exit doorway")]
    public static void ReportDoorwayContents()
    {
        const float OPEN_L = -7.5f, OPEN_R = -4.5f;
        // deeper than the door on purpose: covers the room-side approach and the corridor
        var prism = new Bounds(new Vector3((OPEN_L + OPEN_R) * 0.5f, 1.95f, -11.70f),
                               new Vector3(3.4f, 3.9f, 6.0f));

        int n = 0;
        var rows = new List<string>();
        foreach (var rend in Object.FindObjectsByType<Renderer>())
        {
            if (!rend.bounds.Intersects(prism)) continue;
            var inter = Intersection(rend.bounds, prism);
            // has to be laterally INSIDE the opening by a real margin to be "in the doorway"
            if (inter.x <= 0.05f) continue;
            n++;

            var m = rend.sharedMaterial;
            string mat = m != null ? m.name : "(none)";
            rows.Add(string.Format(
                "  x-overlap {0,5:F0} cm  at ({1,6:F2},{2,6:F2},{3,6:F2})  mat={4}  {5}",
                inter.x * 100f, rend.bounds.center.x, rend.bounds.center.y, rend.bounds.center.z,
                mat, PathOf(rend.transform)));
        }
        rows.Sort();
        Debug.Log("[DoorContents] " + n + " renderer(s) with >5 cm of lateral overlap with the exit "
                  + "opening (x -7,5..-4,5), listed by how far in they reach:");
        for (int i = 0; i < rows.Count && i < 24; i++) Debug.Log("[DoorContents]" + rows[i]);
        if (rows.Count > 24) Debug.Log("[DoorContents] ... and " + (rows.Count - 24) + " more.");
    }

    /// <summary>What the key and the cell are actually made of, material by material.
    ///
    /// The report was that the key's texture does not match the theme. The key is built from
    /// hand-made geometry (D-101's predecessor, D-55) with a material this builder creates, so the
    /// first question is not "which texture" but "which material ended up on which part" - a
    /// multi-part model picks up whatever the shared-material path hands it, and one part on the
    /// wrong material is enough to make the whole prop read as a different game.
    /// </summary>
    [MenuItem("Tools/Escape Room/Report the key and cell materials")]
    public static void ReportItemMaterials()
    {
        // Found by COMPONENT, not by name. The items start hidden (the locker is shut until the
        // code is accepted), and GameObject.Find does not see inactive objects - so the first
        // version of this reported "not found" for both props and looked like a missing-item bug.
        foreach (var gi in Object.FindObjectsByType<GrabbableItem>(FindObjectsSortMode.None))
        {
            var root = gi.gameObject;
            var rends = root.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) continue;

            var counts = new Dictionary<string, int>();
            foreach (var rend in rends)
            {
                var m = rend.sharedMaterial;
                string key = m != null ? m.name : "(none)";
                counts[key] = counts.ContainsKey(key) ? counts[key] + 1 : 1;
            }
            Debug.Log("[ItemMat] " + root.name + " (" + rends.Length + " part(s)) materials: "
                      + string.Join(", ", counts.Keys.Select(k => k + " x" + counts[k]))
                      + "   active=" + root.activeInHierarchy);

            foreach (var rend in rends)
            {
                var m = rend.sharedMaterial;
                if (m == null) { Debug.Log("[ItemMat]   " + rend.name + ": NO MATERIAL"); continue; }
                Color baseCol = Color.magenta;
                if (m.HasProperty("_BaseColor")) baseCol = m.GetColor("_BaseColor");
                else if (m.HasProperty("_Color")) baseCol = m.GetColor("_Color");
                float metal = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : -1f;
                float smooth = m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness") : -1f;
                string tex = m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null
                    ? m.GetTexture("_BaseMap").name : "no texture";
                Debug.Log(string.Format("[ItemMat]   {0,-20} {1,-16} {2,-20} base=#{3} metal={4:F2} smooth={5:F2} map={6}",
                    rend.name, m.name, m.shader != null ? m.shader.name : "?",
                    ColorUtility.ToHtmlStringRGB(baseCol), metal, smooth, tex));
            }
        }
    }

    /// <summary>Prints every light and the ambient/fog settings, so a brightness change can be diffed
    /// against this rather than remembered. Call it before a reboot and again after.</summary>
    [MenuItem("Tools/Escape Room/Snapshot the lighting")]
    public static void SnapshotLighting()
    {
        Debug.Log(string.Format("[Light] ambientMode={0} ambientIntensity={1:F3} ambientSky=#{2} " +
                               "fog={3} fogDensity={4:F4} fogColor=#{5} reflectionIntensity={6:F2}",
            RenderSettings.ambientMode, RenderSettings.ambientIntensity,
            ColorUtility.ToHtmlStringRGB(RenderSettings.ambientSkyColor),
            RenderSettings.fog, RenderSettings.fogDensity,
            ColorUtility.ToHtmlStringRGB(RenderSettings.fogColor),
            RenderSettings.reflectionIntensity));

        var rows = new List<string>();
        foreach (var l in Object.FindObjectsByType<Light>())
        {
            rows.Add(string.Format(
                "  {0,-6} range={1,6:F2} intensity={2,7:F3} #{3} shadows={4} enabled={5}  {6}",
                l.type, l.range, l.intensity, ColorUtility.ToHtmlStringRGB(l.color),
                l.shadows, l.enabled, PathOf(l.transform)));
        }
        rows.Sort();
        foreach (var r in rows) Debug.Log("[Light]" + r);
        Debug.Log("[Light] " + rows.Count + " light(s).");
    }

    /// <summary>Reports the floor and ceiling slabs: how many distinct heights each surface has,
    /// and where the tiles do or do not meet.
    ///
    /// The report was that the floor and ceiling tiles need to be coplanar and continuous. "Coplanar"
    /// is the easy half and can be answered exactly: group every horizontal slab by the world Y of
    /// its top or bottom face, and the number of groups IS the number of distinct heights. The
    /// continuous half needs the extents, so each group also gets its X and Z coverage, sorted, and
    /// any step between neighbours is printed as a gap or an overlap with its size in
    /// centimetres - a step of a few millimetres is invisible, a step of 2 cm is the seam the
    /// player is describing.
    /// </summary>
    [MenuItem("Tools/Escape Room/Check floor and ceiling tiles")]
    public static void CheckFloorCeilingTiles()
    {
        ReportSlabSet("FLOOR", -0.35f, 0.35f, false, FloorAndCeilingOnly);   // top face near y = 0
        ReportSlabSet("CEILING", 3.0f, 4.5f, true, FloorAndCeilingOnly);     // bottom face near 3.70
    }

    /// <summary>Names every floor tile that is NOT on the base layer, so it can be acted on.
    ///
    /// The user's own Floor_01 run is the last real defect in the room and it is THEIRS: 67 tiles
    /// laid in two layers whose tops are 2 cm apart, so for a 12 m stretch of the room two floor
    /// surfaces fight for the same pixels. The project constraint is that their objects are not
    /// moved, so this item does not move them either - it produces the list, because "your floor is
    /// not coplanar" is not actionable and "these 34 are 2 cm proud" is.
    ///
    /// Anything of ours is reported separately, since that half we are allowed to fix.</summary>
    [MenuItem("Tools/Escape Room/List the floor tiles off the base layer")]
    public static void ListFloorTilesOffBase()
    {
        var user = new List<string>();
        var ours = new List<string>();

        var pieces = new List<Renderer>();
        foreach (var rend in Object.FindObjectsByType<Renderer>())
        {
            var b = rend.bounds;
            if (b.size.y > 1.2f) continue;
            if (b.size.x < 1.0f || b.size.z < 1.0f) continue;
            if (b.max.y < -0.35f || b.max.y > 0.35f) continue;
            pieces.Add(rend);
        }
        if (pieces.Count == 0) { Debug.Log("[FloorBase] no floor pieces found"); return; }

        // The base is the layer MOST tiles are on, not the LOWEST one. Taking the lowest made this
        // report name all 67 of the user's tiles, because the lowest thing in the room is the
        // corridor floor I deliberately sank 4 cm as a coplanarity guard (D-114) - it is a safety
        // net under the user's run, not the datum the run should be measured against, and calling
        // it the base turned the item into noise.
        var tally = new Dictionary<string, int>();
        foreach (var rend in pieces)
        {
            string k = rend.bounds.max.y.ToString("F3");
            tally[k] = tally.ContainsKey(k) ? tally[k] + 1 : 1;
        }
        string baseKey = null; int best = -1;
        foreach (var kv in tally) if (kv.Value > best) { best = kv.Value; baseKey = kv.Key; }
        float baseY = float.Parse(baseKey, System.Globalization.CultureInfo.InvariantCulture);

        const float TOL = 0.005f;          // 5 mm: under that, a step is not the defect
        foreach (var rend in pieces)
        {
            float top = rend.bounds.max.y;
            if (Mathf.Abs(top - baseY) <= TOL) continue;
            string path = PathOf(rend.transform);
            if (path.StartsWith(ROOT, System.StringComparison.Ordinal)) ours.Add(path + "  (y = " + top.ToString("F3") + ")");
            else user.Add(path + "  (y = " + top.ToString("F3") + ")");
        }

        Debug.Log("[FloorBase] " + best + " of " + pieces.Count + " floor pieces share the base top y = "
                  + baseKey + " m. Off it: " + user.Count + " of the user's tile(s), " + ours.Count + " of ours.");
        if (user.Count > 0)
        {
            Debug.Log("[FloorBase] YOUR tiles to bring to y = " + baseKey
                      + " (select them in the Hierarchy, set their Y, and the step disappears):");
            for (int i = 0; i < user.Count; i++) Debug.Log("[FloorBase]   " + user[i]);
        }
        foreach (var o in ours) Debug.Log("[FloorBase] ours: " + o);
    }

    /// <summary>True for pieces that are actually a floor or a ceiling.
    ///
    /// The first version of the slab check took "flat, wide, thin, in the right Y band" as meaning
    /// floor-or-ceiling, and duly reported the ceiling as being at FOUR different heights. Three of
    /// them were the `cables` props of the rooftop, hung in open air at y 3.4 / 3.6 / 3.8 as set
    /// dressing - not ceilings at all. The real ceiling (`Ceiling` and `CorridorCeiling`) was at
    /// 3.700 on both, i.e. already coplanar, and the report said it was broken.
    ///
    /// A check that invents a defect is worse than no check, because it makes the real ones harder
    /// to see. So the test is now explicit: a piece counts only if it is named as structure. That
    /// is a whitelist, and a whitelist can be incomplete - but it fails by omission, which the
    /// log makes obvious, rather than by accusing the art.</summary>
    static bool FloorAndCeilingOnly(Renderer r)
    {
        var n = r.name.ToLowerInvariant();
        var p = r.transform.parent != null ? r.transform.parent.name.ToLowerInvariant() : "";
        return n.Contains("floor") || n.Contains("ceiling") || n.Contains("roof")
            || p.Contains("shell") || p.Contains("exitcorridor");
    }
    /// <summary>One horizontal surface, grouped by the height of the face the player sees.
    ///
    /// Grouping is by the VISIBLE face, not by the centre: two slabs can share a centre height and
    /// still have their tops 2 cm apart, which is invisible in a centre-based grouping and is
    /// exactly the defect. The face is the side that looks into the room - the top of a floor, the
    /// bottom of a ceiling.
    ///
    /// Continuity is asked as a question about AREA, by sampling a grid and counting how many pieces
    /// cover each point. The previous version projected the pieces onto a single axis and called the
    /// steps "seams", which was wrong in a way that buried the finding: a floor is a 2D grid, so a
    /// north-south tile and an east-west tile "overlap 300 cm" along X while never touching, and all
    /// 33 of those phantom overlaps were noise printed above the two real defects.</summary>
    static void ReportSlabSet(string label, float yLo, float yHi, bool ceiling,
                              System.Func<Renderer, bool> accept)
    {
        var groups = new Dictionary<string, List<Renderer>>();
        foreach (var rend in Object.FindObjectsByType<Renderer>())
        {
            if (!accept(rend)) continue;
            var b = rend.bounds;
            if (b.size.y > 1.2f) continue;                       // not a slab
            if (b.size.x < 1.0f || b.size.z < 1.0f) continue;    // not a floor/ceiling piece
            float face = ceiling ? b.min.y : b.max.y;
            if (face < yLo || face > yHi) continue;

            string key = face.ToString("F3");
            if (!groups.ContainsKey(key)) groups[key] = new List<Renderer>();
            groups[key].Add(rend);
        }

        var keys = new List<string>(groups.Keys);
        keys.Sort();
        Debug.Log("[Slabs] " + label + ": " + keys.Count + " distinct height(s) on the visible face"
                  + (keys.Count > 1 ? "   <== NOT COPLANAR" : ""));

        foreach (var k in keys)
        {
            var list = groups[k];
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var r in list)
            {
                var b = r.bounds;
                minX = Mathf.Min(minX, b.min.x); maxX = Mathf.Max(maxX, b.max.x);
                minZ = Mathf.Min(minZ, b.min.z); maxZ = Mathf.Max(maxZ, b.max.z);
            }
            Debug.Log(string.Format("[Slabs]   face y = {0} m : {1} piece(s), x {2:F2}..{3:F2}, z {4:F2}..{5:F2}",
                k, list.Count, minX, maxX, minZ, maxZ));

            const float STEP = 0.25f;                 // 25 cm: finer than any seam worth naming
            int fights = 0;
            var blame = new Dictionary<string, int>();
            for (float x = minX + STEP * 0.5f; x < maxX; x += STEP)
            {
                for (float z = minZ + STEP * 0.5f; z < maxZ; z += STEP)
                {
                    var covering = new List<string>();
                    foreach (var r in list)
                    {
                        var b = r.bounds;
                        if (x < b.min.x || x > b.max.x || z < b.min.z || z > b.max.z) continue;
                        covering.Add(PathOf(r.transform));
                    }
                    if (covering.Count < 2) continue;
                    fights++;
                    for (int i = 0; i < covering.Count; i++)
                        blame[covering[i]] = blame.ContainsKey(covering[i]) ? blame[covering[i]] + 1 : 1;
                }
            }
            Debug.Log(string.Format("[Slabs]     sampled at {0:F0} cm: {1} point(s) covered twice or more"
                                     + (fights > 0 ? "   <== OVERLAPPING SURFACES" : ""),
                STEP * 100f, fights));
            var top = new List<KeyValuePair<string, int>>(blame);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < top.Count && i < 6; i++)
                Debug.Log("[Slabs]       " + top[i].Value + " sample(s): " + top[i].Key);
        }
    }
    /// <summary>How the floor and wall tiles are actually laid out, as numbers.
    ///
    /// The player reports the next tile starting in the middle of the previous one, and named two
    /// walls that overlap: <c>Wall_Simple_01 (5)</c> and <c>Wall_Simple_01_00</c>. That is a claim
    /// about SPACING, so the answer has to be spacing.
    ///
    /// Getting this measurement right took three corrections, and each one had been inventing
    /// defects rather than finding them:
    ///
    ///  1. Tiles arrive as a named group holding a mesh child - <c>Wall_Simple_01 (5)</c> inside
    ///     <c>Wall_Simple_01 (15)</c> - so a plain name test counts every tile TWICE and then
    ///     reports the tile as overlapping ITSELF by its full 3.00 m. Only the innermost match is
    ///     a tile, which is the same rule <see cref="IsKitWallTile"/> already uses.
    ///  2. Heights were compared as raw floats, so tiles at 2.900 and 2.8999 were two heights and
    ///     2.900 was listed three times. Rounded to the millimetre, which is finer than any step
    ///     worth naming.
    ///  3. Runs were found by walking the tiles in order and starting a new run whenever the
    ///     perpendicular coordinate moved by more than half a tile. With a tolerance that loose,
    ///     scattered tiles CHAIN into one enormous run - 106 entries in a 25.7 m row - and every
    ///     one of those entries then looked like an overlap. Runs are now found by snapping the
    ///     perpendicular coordinate to a grid and grouping equal keys, which cannot chain.</summary>
    [MenuItem("Tools/Escape Room/Report the tile layout")]
    public static void ReportTileLayout()
    {
        ReportOneTileSet("Floor_01");
        ReportOneTileSet(WALL_PREFAB);
    }

    /// <summary>True for a tile of the given family - the object that carries its own mesh.
    ///
    /// The general form of <see cref="IsKitWallTile"/>, so the floor is measured by the same rule as
    /// the walls, and for the same reason: in this scene a <c>Floor_01</c> is always a transform with
    /// a child holding the mesh, so "innermost name match" picks the child while the group above it
    /// is the thing actually placed, scaled and measured. Counting the group measures its first
    /// child. See the note on <see cref="IsKitWallTile"/> for what that cost.</summary>
    static bool IsInnermostTile(Transform tr, string prefix)
    {
        if (tr == null) return false;
        if (!tr.name.StartsWith(prefix, System.StringComparison.Ordinal)) return false;
        return tr.GetComponent<Renderer>() != null;
    }

    static void ReportOneTileSet(string prefix)
    {
        var tiles = new List<Transform>();
        var rends = new List<Renderer>();
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!IsInnermostTile(tr, prefix)) continue;
            if (!tr.gameObject.activeSelf) continue;
            var r = tr.GetComponent<Renderer>();
            if (r == null) continue;
            tiles.Add(tr); rends.Add(r);
        }
        if (tiles.Count == 0) { Debug.Log("[Layout] no active " + prefix + " tile"); return; }

        // the true face size, from the rendered bounds, averaged over the set: a floor plane has no
        // mesh depth at all, so a mesh-only measure reads 0.00 and every spacing looks wrong
        var faceSamples = new List<float>();
        foreach (var r in rends)
        {
            var b = r.bounds;
            faceSamples.Add(Mathf.Max(b.size.x, b.size.z));
        }
        faceSamples.Sort();
        float face = faceSamples[faceSamples.Count / 2];      // median, not mean: one odd tile cannot move it

        var heights = new List<float>();
        foreach (var r in rends)
        {
            float y = Mathf.Round(r.bounds.max.y * 1000f) / 1000f;
            if (!heights.Contains(y)) heights.Add(y);
        }
        heights.Sort();
        Debug.Log(string.Format("[Layout] {0}: {1} active tile(s), median face {2:F2} m, {3} distinct top "
                               + "height(s){4}", prefix, tiles.Count, face, heights.Count,
            heights.Count > 1 ? "   <== NOT COPLANAR" : ""));
        foreach (var h in heights) Debug.Log("[Layout]   top y = " + h.ToString("F3"));

        // Only the group of tiles that makes up the room floor is a floor. There are also tiles at
        // y = 2.9 - platforms and the rooftop - and treating those as part of the same surface is how
        // a report ends up demanding 140 tiles be coplanar when 70 of them are 3 m in the air.
        float roomY = 0f;
        for (int i = 0; i < heights.Count; i++) if (heights[i] < 1f) { roomY = heights[i]; break; }
        var roomIdx = new List<int>();
        for (int i = 0; i < rends.Count; i++)
            if (Mathf.Abs(rends[i].bounds.max.y - roomY) < 0.005f) roomIdx.Add(i);
        Debug.Log("[Layout]   treating the y = " + roomY.ToString("F3") + " layer as THE surface: "
                  + roomIdx.Count + " tile(s); the other " + (tiles.Count - roomIdx.Count)
                  + " are at other heights and are left alone.");

        var cells = new Dictionary<string, int>();
        foreach (var i in roomIdx)
        {
            var p = rends[i].bounds.center;
            string k = Mathf.Round(p.x * 100f) / 100f + " / " + Mathf.Round(p.z * 100f) / 100f;
            cells[k] = cells.ContainsKey(k) ? cells[k] + 1 : 1;
        }
        int stacked = 0, stackSpots = 0;
        foreach (var kv in cells) if (kv.Value > 1) { stackSpots++; stacked += kv.Value - 1; }
        Debug.Log("[Layout]   " + cells.Count + " distinct (x,z) positions for " + roomIdx.Count
                  + " tiles; " + stackSpots + " position(s) hold more than one (" + stacked + " extra)");

        // ---- runs, by snapping the perpendicular coordinate so runs cannot chain
        const float SNAP = 0.5f;
        foreach (int axis in new[] { 0, 1 })
        {
            var byBand = new Dictionary<long, List<int>>();
            foreach (var i in roomIdx)
            {
                var b = rends[i].bounds;
                bool runsAlongX = b.size.x >= b.size.z;
                if ((axis == 0) != runsAlongX) continue;        // axis 0 collects the X-running tiles
                float perp = axis == 0 ? b.center.z : b.center.x;
                long key = (long)Mathf.Round(perp / SNAP);
                if (!byBand.ContainsKey(key)) byBand[key] = new List<int>();
                byBand[key].Add(i);
            }

            var keys = new List<long>(byBand.Keys);
            keys.Sort();
            foreach (var key in keys)
            {
                var bnd = byBand[key];
                if (bnd.Count < 2) continue;
                bnd.Sort((a, b) => Along(rends[a], axis).CompareTo(Along(rends[b], axis)));
                float perp = 0f;
                foreach (var i in bnd) perp += axis == 0 ? rends[i].bounds.center.z : rends[i].bounds.center.x;
                perp /= bnd.Count;

                int gaps = 0, overlaps = 0, doubles = 0;
                float worstOverlap = 0f, worstGap = 0f;
                string worstName = "";
                for (int i = 1; i < bnd.Count; i++)
                {
                    float step = AlongStart(rends[bnd[i]], axis) - AlongEnd(rends[bnd[i - 1]], axis);
                    if (step > 0.001f) { gaps++; worstGap = Mathf.Max(worstGap, step); }
                    else if (step < -0.001f)
                    {
                        overlaps++;
                        if (-step > worstOverlap)
                        {
                            worstOverlap = -step;
                            worstName = PathOf(tiles[bnd[i - 1]].transform) + "  +  "
                                      + PathOf(tiles[bnd[i]].transform);
                        }
                    }
                    if (Mathf.Abs(step) < 0.001f) doubles++;
                }
                // a run of n tiles in a span of s wants (n-1) gaps of s/(n-1); anything tighter than
                // the face length means the tiles are inside each other
                float span = AlongEnd(rends[bnd[bnd.Count - 1]], axis) - AlongStart(rends[bnd[0]], axis);
                float want = (bnd.Count - 1) > 0 ? span / (bnd.Count - 1) : face;
                string flag = doubles > 0 ? "   <== " + doubles + " ON THE SAME SPOT"
                    : (overlaps > 0 ? "   <== " + overlaps + " OVERLAP(S)"
                    : (gaps > 0 ? "   <== " + gaps + " GAP(S)" : "   even"));
                Debug.Log(string.Format("[Layout]   run along {0}, row {1} = {2:F2}: {3} tile(s), span {4:F2} "
                                      + "({5:F2} each, face is {6:F2}){7}",
                    axis == 0 ? "X" : "Z", axis == 0 ? "z" : "x", perp, bnd.Count, span, want, face, flag));
                if (overlaps > 0)
                    Debug.Log(string.Format("[Layout]      worst overlap {0:F0} cm: {1}",
                        worstOverlap * 100f, worstName));
                if (gaps > 0)
                    Debug.Log(string.Format("[Layout]      worst gap {0:F0} cm", worstGap * 100f));
            }
        }
    }

    static float Along(Renderer r, int axis) { var c = r.bounds.center; return axis == 0 ? c.x : c.z; }
    static float AlongStart(Renderer r, int axis)
    { var b = r.bounds; return axis == 0 ? b.min.x : b.min.z; }
    static float AlongEnd(Renderer r, int axis)
    { var b = r.bounds; return axis == 0 ? b.max.x : b.max.z; }

    /// <summary>Puts the room floor back the way the player left it: the hand-placed run active, the
    /// grid gone.
    ///
    /// Its reason for existing is arithmetic, not taste. While the grid carried the float-drift bug
    /// it grew on every run, each run deactivating the previous grid and leaving a larger one, so
    /// the scene accumulated grids out past 54 x 60 m before the cause was found. Nothing is
    /// deleted here: the hand-placed tiles are re-activated and the grid object is destroyed, and
    /// the grid is one menu item away again.</summary>
    [MenuItem("Tools/Escape Room/Restore the hand-placed room floor")]
    public static void RestoreHandPlacedFloor()
    {
        const string PREFIX = "Floor_01";
        int back = 0, gridOff = 0;
        foreach (var tr in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                                                                FindObjectsSortMode.None))
        {
            if (!IsInnermostTile(tr, PREFIX)) continue;
            // The fill tiles hang off ROOT directly rather than under a holder object, so the reset
            // has to recognise them by name too - otherwise "restore" leaves the generated grid in
            // place and is not a restore at all.
            bool isGenerated = tr.name.StartsWith(PREFIX + "_F", System.StringComparison.Ordinal)
                            || tr.name.StartsWith(PREFIX + "_G", System.StringComparison.Ordinal);
            if (isGenerated)
            {
                if (tr.gameObject.activeSelf) { tr.gameObject.SetActive(false); gridOff++; }
                continue;
            }
            if (tr.gameObject.activeSelf) continue;
            tr.gameObject.SetActive(true);
            back++;
        }
        var grid = GameObject.Find("FloorGrid");
        bool hadGrid = grid != null;
        if (hadGrid) Object.DestroyImmediate(grid);

        Debug.Log("[FloorGrid] re-activated " + back + " hand-placed tile(s), switched off "
                  + gridOff + " generated tile(s)"
                  + (hadGrid ? ", removed the FloorGrid holder." : "."));
    }

    static int CountLevels(List<Renderer> rends)
    {
        var seen = new List<float>();
        foreach (var r in rends)
        {
            float y = Mathf.Round(r.bounds.max.y * 1000f) / 1000f;
            if (!seen.Contains(y)) seen.Add(y);
        }
        return seen.Count;
    }

    static float HighestOf(List<Renderer> rends)
    {
        float m = float.MinValue;
        foreach (var r in rends) m = Mathf.Max(m, r.bounds.max.y);
        return m;
    }
    /// <summary>Pushes overlapping wall tiles apart along the run they belong to, until none overlap.
    ///
    /// The conservative counterpart to the floor rebuild, and deliberately so. Walls carry INTENT -
    /// which side of the room, which direction, whether there is a doorway - so this never adds,
    /// removes or re-spans a wall. It resolves the one defect the player named, two tiles lying
    /// inside each other, by moving one of them along the run axis by exactly the amount they
    /// overlap.
    ///
    /// <para><b>Two walls only count as overlapping if they run the SAME way and sit in the same
    /// row.</b> Without the axis test, two walls that merely cross - which is what a corner is - look
    /// like they are inside each other, and a pass that "fixed" that would tear every corner of the
    /// room apart. The row tolerance is a fixed 0.5 m rather than a fraction of a tile: the walls are
    /// 0.40 m thick, so half a tile would happily merge two walls that are meant to be a corner.</para>
    ///
    /// <para><b>The overlap is measured symmetrically, and the mover is chosen by POSITION, not by
    /// list order.</b> This is the bug that made the first version of this pass move tiles 26 metres:
    /// it computed <c>a.max - b.min</c> from the pair's order in the scan list, so whenever tile j
    /// sat BEFORE tile i along the run, the "overlap" came out as the whole distance between them -
    /// 11 m, 17 m, 26 m - and it dutifully shoved j further away each pass. Order is an accident of
    /// enumeration; position is the fact. The overlap is now
    /// <c>min(aMax,bMax) - max(aMin,bMin)</c>, and whichever tile is further along the run is the one
    /// that moves.</para>
    ///
    /// <para><b>A shift larger than half a tile is refused, not performed.</b> Two tiles of a 3.00 m
    /// kit that share a row and interpenetrate by more than 1.5 m are not an overlap to be nudged
    /// apart - they are a mis-detection or a wall that belongs somewhere else entirely, and moving
    /// it by a quarter of the room is how a repair turns into damage. Those pairs are counted and
    /// named, and left for a human. The pass is non-destructive by construction, not by care.</para>
    ///
    /// <para>Iterated, because a chain of three mutually overlapping tiles needs the middle one to
    /// move before the outer two can be compared. Capped, and it says so rather than spinning.</para></summary>
    [MenuItem("Tools/Escape Room/De-overlap the wall tiles")]
    public static void DeOverlapWalls()
    {
        const float SLOP = 0.002f;         // 2 mm: below this they are flush enough
        const float SAME_ROW = 0.5f;       // half the wall thickness
        const float MAX_SHIFT = 1.5f;      // half a 3.00 m tile: past this it is not a nudge
        int moved = 0, rounds = 0, refused = 0;
        var report = new List<string>();
        var refusals = new List<string>();

        for (int pass = 0; pass < 40; pass++)
        {
            rounds++;
            int passMoved = 0;
            var tiles = new List<Transform>();
            var rends = new List<Renderer>();
            foreach (var tr in Object.FindObjectsByType<Transform>())
            {
                if (!IsKitWallTile(tr)) continue;
                if (!tr.gameObject.activeSelf) continue;
                var r = tr.GetComponent<Renderer>();
                if (r == null) continue;
                tiles.Add(tr); rends.Add(r);
            }

            for (int i = 0; i < tiles.Count; i++)
            {
                for (int j = i + 1; j < tiles.Count; j++)
                {
                    var a = rends[i].bounds; var b = rends[j].bounds;
                    bool aAlongX = a.size.x >= a.size.z, bAlongX = b.size.x >= b.size.z;
                    if (aAlongX != bAlongX) continue;                 // a corner, not an overlap
                    int axis = aAlongX ? 0 : 1;
                    float aPerp = axis == 0 ? a.center.z : a.center.x;
                    float bPerp = axis == 0 ? b.center.z : b.center.x;
                    if (Mathf.Abs(aPerp - bPerp) > SAME_ROW) continue;   // different rows

                    float aStart = axis == 0 ? a.min.x : a.min.z;
                    float aEnd = axis == 0 ? a.max.x : a.max.z;
                    float bStart = axis == 0 ? b.min.x : b.min.z;
                    float bEnd = axis == 0 ? b.max.x : b.max.z;
                    float aMid = (aStart + aEnd) * 0.5f, bMid = (bStart + bEnd) * 0.5f;

                    // symmetric: the shared stretch, regardless of who came first in the list
                    float overlap = Mathf.Min(aEnd, bEnd) - Mathf.Max(aStart, bStart);
                    if (overlap <= SLOP) continue;

                    if (overlap > MAX_SHIFT)
                    {
                        refused++;
                        if (refusals.Count < 10)
                            refusals.Add("  " + PathOf(tiles[i].transform) + " + "
                                       + PathOf(tiles[j].transform) + "  share "
                                       + (overlap * 100f).ToString("F0") + " cm in one row - over the "
                                       + (MAX_SHIFT * 100f).ToString("F0")
                                       + " cm cap, so NOT moved");
                        continue;
                    }

                    // the one further along the run is the one that gives way
                    int mover = aMid >= bMid ? i : j;
                    Vector3 p = tiles[mover].transform.position;
                    if (axis == 0) p.x += overlap + SLOP; else p.z += overlap + SLOP;
                    tiles[mover].transform.position = p;
                    passMoved++; moved++;
                    if (report.Count < 14)
                        report.Add("  " + PathOf(tiles[i].transform) + " + " + PathOf(tiles[j].transform)
                                   + "  -> moved " + PathOf(tiles[mover].transform) + " "
                                   + ((overlap + SLOP) * 100f).ToString("F1")
                                   + " cm along " + (axis == 0 ? "X" : "Z"));
                }
            }
            if (passMoved == 0) break;
        }

        Debug.Log("[WallDeOverlap] " + moved + " shift(s) over " + rounds + " pass(es), "
                  + refused + " pair(s) refused for exceeding the " + (MAX_SHIFT * 100f).ToString("F0")
                  + " cm cap.");
        foreach (var r in report) Debug.Log("[WallDeOverlap]" + r);
        foreach (var r in refusals) Debug.LogWarning("[WallDeOverlap]" + r);
        if (refused > 0)
            Debug.LogWarning("[WallDeOverlap] a refused pair means two wall tiles genuinely occupy the "
                             + "same run. That is a placement decision, not a spacing error - check "
                             + "whether one of them should not be there at all.");
    }


    /// <summary>Reopens the scene from disk, throwing away unsaved changes.
    ///
    /// Written because a tool of mine was destructive and the scene had unsaved damage on it. The
    /// builder saves at the end of every Build Level, so the file on disk is always the last known
    /// good state, and an editor script can get back to it without a dialog: the confirmation Unity
    /// puts up when you close a dirty scene comes from the UI, not from OpenScene.
    ///
    /// It is a blunt instrument by design - it discards EVERY unsaved change, not just the ones you
    /// wish you had not made - so it says so in the log rather than pretending to be selective.</summary>
    [MenuItem("Tools/Escape Room/Revert the scene to the last save")]
    public static void RevertSceneToLastSave()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var path = scene.path;
        if (string.IsNullOrEmpty(path))
        {
            Debug.LogError("[Revert] the active scene has never been saved, so there is nothing on disk "
                           + "to go back to.");
            return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;   // user pressed Cancel
        Debug.LogWarning("[Revert] discarding every unsaved change in " + path
                         + " and reloading it from disk.");
        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        Debug.Log("[Revert] reloaded " + path + " from disk.");
    }
    /// <summary>Lists the real gaps in the user's north wall run, tile by tile.
    ///
    /// This is the measurement that explains the wall overlaps. `BuildRoomRevamp` plugs the north
    /// run with tiles at hardcoded x positions (-3, 0, 3, 6, 9, 11.7, plus -11.9 for the corner),
    /// which is only correct if the user's own run sits on a 3.00 m grid. It does not: their tiles
    /// have gaps of 1-3 cm and overlaps of 10-15 cm within a row, exactly like the floor did
    /// (D-115). So a filler placed on the ideal grid lands ON a real tile instead of in a real gap,
    /// and the pair overlaps by however far the run has drifted - which is the
    /// <c>Wall_Simple_01 (5)</c> against <c>Wall_Simple_01_00</c> the player named.
    ///
    /// Until this was measured the cause was a guess, and the guess sent a de-overlap pass sliding
    /// wall tiles metres around the room (D-118). The fix is to stop assuming the grid and start
    /// measuring the gaps, so this item exists to hand the builder the real numbers.</summary>
    [MenuItem("Tools/Escape Room/Report the gaps in the north wall run")]
    public static void ReportNorthWallGaps()
    {
        const float ROW = 0.6f;          // 60 cm: the row this run occupies
        var tiles = new List<Transform>();
        var rends = new List<Renderer>();
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!IsKitWallTile(tr)) continue;
            if (!tr.gameObject.activeSelf) continue;
            var r = tr.GetComponent<Renderer>();
            if (r == null) continue;
            var b = r.bounds;
            if (b.size.x < b.size.z) continue;                    // only tiles running along X
            if (Mathf.Abs(b.center.z - WALL_N) > ROW) continue;   // only the north run
            tiles.Add(tr); rends.Add(r);
        }
        if (tiles.Count == 0) { Debug.Log("[NorthWall] no tiles found on the north run"); return; }

        var order = new List<int>();
        for (int i = 0; i < rends.Count; i++) order.Add(i);
        order.Sort((a, b) => rends[a].bounds.min.x.CompareTo(rends[b].bounds.min.x));

        Debug.Log("[NorthWall] " + tiles.Count + " KIT tile(s) on the run at z = " + WALL_N.ToString("F2")
                  + ", x " + rends[order[0]].bounds.min.x.ToString("F2") + ".."
                  + rends[order[order.Count - 1]].bounds.max.x.ToString("F2")
                  + ".  Box fillers are NOT listed here - they are named NorthFiller, not "
                  + WALL_PREFAB + " - and the exit opening at x -7,5..-4,5 is a hole on purpose, so a "
                  + "reported gap that spans it is not a defect.");
        float cursor = rends[order[0]].bounds.min.x;
        for (int k = 0; k < order.Count; k++)
        {
            int i = order[k];
            var b = rends[i].bounds;
            if (k > 0)
            {
                float gap = b.min.x - cursor;
                if (gap > 0.01f)
                    Debug.Log(string.Format("[NorthWall]   GAP {0:F0} cm at x {1:F2}..{2:F2}{3}",
                        gap * 100f, cursor, b.min.x,
                        (b.min.x > -7.5f && cursor < -4.5f) ? "   (spans the exit opening)" : ""));
                else if (gap < -0.01f)
                    Debug.Log(string.Format("[NorthWall]   overlap {0:F0} cm at x {1:F2}   <- already inside",
                        -gap * 100f, b.min.x));
            }
            Debug.Log(string.Format("[NorthWall]   tile {0,-44} x {1:F2}..{2:F2}  z {3:F2}",
                PathOf(tiles[i].transform), b.min.x, b.max.x, b.center.z));
            cursor = Mathf.Max(cursor, b.max.x);
        }
    }

    /// <summary>Fills the north wall run from the gaps the user's own tiles leave, and nothing else.
    ///
    /// The user's tiles on this row are treated as FIXED - they are the player's work and this does
    /// not move them. Everything this adds is sized from the gap it goes into, so it cannot overlap
    /// a neighbour by construction rather than by luck.
    ///
    /// Three cases, because the measured gaps are not all the same shape:
    ///
    ///  * a gap at least one tile wide gets a kit tile centred in it - the cheap case, and the
    ///    reason the previous hardcoded grid produced a wall that looked right;
    ///  * a narrower gap gets a box filler of exactly the gap's width, because a 3.00 m tile cannot
    ///    go into a 1.4 m hole without poking out the other side. The measured run has two of these,
    ///    at x -8.90..-7.50 and x -4.50..-3.11, and either one would have taken a kit tile straight
    ///    through the exit opening;
    ///  * anything narrower than a filler's own thickness is left, because a sliver of wall is not
    ///    worth a seam to close.
    ///
    /// The exit opening is skipped explicitly, and the run's ends are filled out to the west wall
    /// and the room's east edge, which is the hole D-53 was about - except that the west filler is
    /// now placed from the measured west wall position instead of the hardcoded -11.9.</summary>
    static void BuildNorthRunFromGaps(Transform parent)
    {
        const float ROW = 0.6f;              // the row this run occupies
        const float MIN_FILL = 0.30f;        // narrower than this and a filler is all seam
        const float OPEN_L = -7.5f, OPEN_R = -4.5f;   // the exit opening, on purpose

        // ---- the user's tiles on this row, as fixed boundaries
        var edges = new List<float[]>();
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!IsKitWallTile(tr)) continue;
            if (!tr.gameObject.activeSelf) continue;
            if (tr.IsChildOf(GameObject.Find(ROOT).transform)) continue;   // not ours
            var r = tr.GetComponent<Renderer>();
            if (r == null) continue;
            var b = r.bounds;
            if (b.size.x < b.size.z) continue;                    // only tiles running along X
            if (Mathf.Abs(b.center.z - WALL_N) > ROW) continue;
            edges.Add(new[] { b.min.x, b.max.x });
        }
        edges.Sort((a, b) => a[0].CompareTo(b[0]));

        if (edges.Count == 0)
        {
            Debug.LogWarning("[NorthRun] the user's north run has no tile at all - sealing it with "
                             + "one full row instead of guessing at gaps.");
            for (float x = -10.5f; x < WALL_E; x += 3f)
                Kit(WALL_PREFAB, parent, new Vector3(x, 0f, WALL_N), new Vector3(0f, 90f, 0f));
            return;
        }

        // Merge the user's extents into continuous stretches, so a pair of their tiles that already
        // touch reads as one 6 m boundary rather than a 0 cm gap between two of them.
        var spans = new List<float[]>();
        foreach (var e in edges)
        {
            if (spans.Count > 0 && e[0] <= spans[spans.Count - 1][1] + 0.05f)
                spans[spans.Count - 1][1] = Mathf.Max(spans[spans.Count - 1][1], e[1]);
            else spans.Add(new[] { e[0], e[1] });
        }

        // The run has to reach the west wall and the room's east edge, so the ends count as gaps too
        float west = -13.40f;                    // hides outside the room, past the west wall (D-53)
        float east = WALL_E + 1.5f;              // the east wall's own thickness
        int kit = 0, boxes = 0, skipped = 0;

        // gaps BEFORE the first span
        FillGap(parent, west, spans[0][0], OPEN_L, OPEN_R, ref kit, ref boxes, ref skipped);
        // gaps BETWEEN spans
        for (int i = 1; i < spans.Count; i++)
            FillGap(parent, spans[i - 1][1], spans[i][0], OPEN_L, OPEN_R, ref kit, ref boxes, ref skipped);
        // gap AFTER the last span
        FillGap(parent, spans[spans.Count - 1][1], east, OPEN_L, OPEN_R, ref kit, ref boxes, ref skipped);

        Debug.Log("[NorthRun] sealed from " + spans.Count + " user span(s) at x "
                  + string.Join(", ", spans.ConvertAll(s => s[0].ToString("F2") + ".." + s[1].ToString("F2")))
                  + ": " + kit + " kit tile(s), " + boxes + " box filler(s), " + skipped
                  + " gap(s) left (under " + MIN_FILL.ToString("F2") + " m or inside the exit opening).");
    }

    /// <summary>Puts one thing in one gap, choosing the thing by how wide the gap is.
    ///
    /// The opening test is on the part of the gap that is actually wall: a gap that starts inside the
    /// exit opening still has wall on its far side, and treating the whole gap as "the opening"
    /// would leave that wall unfilled.</summary>
    static void FillGap(Transform parent, float from, float to, float openL, float openR,
                        ref int kit, ref int boxes, ref int skipped)
    {
        const float MIN_FILL = 0.30f;
        if (to - from < MIN_FILL) { skipped++; return; }

        // the opening is a hole, so exclude it and fill whatever wall remains on either side
        float segs = to - from;
        if (to > openL && from < openR)
        {
            // fill the piece west of the opening, then the piece east of it
            if (openL - from >= MIN_FILL) FillSolid(parent, from, openL, ref kit, ref boxes);
            else skipped++;
            if (to - openR >= MIN_FILL) FillSolid(parent, openR, to, ref kit, ref boxes);
            else skipped++;
            return;
        }
        FillSolid(parent, from, to, ref kit, ref boxes);
    }

    /// <summary>One solid piece of wall exactly as wide as the gap, no wider.
    ///
    /// Two anchoring conventions meet here and mixing them is what put a 1.5 m overlap into the first
    /// version of this: a KIT tile is placed by its near edge, while a box is placed by its CENTRE.
    /// That is not a choice, it is a fact about the prefab - `Kit(WALL_PREFAB, t, new Vector3(-3, 0,
    /// WALL_N), rotY 90)` has always produced world bounds x -3.00..0.00, and every position in the
    /// original builder relied on it. So a tile goes at <c>from + i*3</c>, and a filler box goes at
    /// <c>from + w/2</c>. Using the centre convention for both shifted the whole run 1.5 m and made
    /// the first filler overlap the second.</summary>
    static void FillSolid(Transform parent, float from, float to, ref int kit, ref int boxes)
    {
        const float THICK = 0.40f;              // matches WALL_THICKNESS
        const float HEIGHT = 3.00f;             // the kit wall's height
        float w = to - from;

        // a whole number of tiles, edge-anchored, then whatever is left over
        int n = Mathf.FloorToInt(w / 3f);
        for (int i = 0; i < n; i++)
        {
            Kit(WALL_PREFAB, parent,
                new Vector3(from + i * 3f, 0f, WALL_N), new Vector3(0f, 90f, 0f));
            kit++;
        }
        float left = w - n * 3f;
        if (left >= 0.30f)
        {
            // The remainder filler gets the KIT's material, not the shell's.
            //
            // It was `_shell` - a flat, untextured surface - and the player reported the walls as
            // "all there but without texture". Measured: 17 m2 of wall in these boxes against 379 m2
            // of textured kit tile, and two of the three are 1.32 m and 1.39 m wide, right beside the
            // terminal where the wall is looked at head-on. So 4% of the wall area was visibly a
            // different, blank material, and it read as the texture having gone missing.
            //
            // The kit uses ONE atlas for everything, with each model's UVs pointing at its own
            // region, so re-using `Diffuse_01` does not smear the neighbouring panel across the
            // filler - the filler's own box UVs land in the same region the kit's wall occupies,
            // which is exactly the look wanted. Geometry is unchanged; only the finish is.
            EsTheme.Box("NorthFiller", parent,
                new Vector3(from + n * 3f + left * 0.5f, HEIGHT * 0.5f, WALL_N),
                new Vector3(left, HEIGHT, THICK), _kitWall);
            boxes++;
        }
    }


    // =====================================================================
    // POR QUE NAO CONSIGO PEGAR O ITEM DE NOVO
    // =====================================================================

    /// <summary>Reports, for every droppable item, whether a click aimed at it would actually reach it.
    ///
    /// The report was "when I drop any of the props on the ground I cannot pick it up again", and
    /// nothing in <see cref="GrabbableItem"/> or <see cref="PlayerInteractor"/> explains it by
    /// reading: the drop clears the holder, the collider is never disabled, no layer is touched, and
    /// the leash would bring a lost item back. So this measures the one thing that decides it - the
    /// raycast - by firing a ray from the camera at each item's centre and reporting what comes back
    /// FIRST.
    ///
    /// "First" is the whole question. The pickup is a single raycast with a 3.4 m limit, so an item
    /// can be right there, lit, in reach, and still be unpickable because something else - the floor
    /// it is lying on, a crate edge, the terminal - is nearer to the camera along the same line. That
    /// is a fact about geometry that no amount of reading the interaction code will produce.
    ///
    /// It also simulates the real round trip - pick up, drop, let it settle - because "I cannot pick
    /// it up" and "it is 40 cm from the spawn point" are different bugs with the same symptom.</summary>
    [MenuItem("Tools/Escape Room/Why can I not pick this up")]
    public static void WhyCannotPickUp()
    {
        var cam = Camera.main;
        var interactor = Object.FindFirstObjectByType<PlayerInteractor>();
        if (cam == null) { Debug.LogError("[Pickup] no Camera.main - cannot test the ray"); return; }
        float reach = interactor != null ? interactor.interactDistance : 3.4f;
        Vector3 eye = cam.transform.position;

        Debug.Log("[Pickup] camera at " + eye.ToString("F2") + ", reach " + reach.ToString("F2") + " m");

        foreach (var it in Object.FindObjectsByType<GrabbableItem>(FindObjectsSortMode.None))
        {
            var t = it.transform;
            var col = t.GetComponent<Collider>();
            var rb = t.GetComponent<Rigidbody>();

            string where = "collider=" + (col == null ? "NONE" : col.GetType().Name)
                + " enabled=" + (col != null && col.enabled)
                + " layer=" + t.gameObject.layer + " (" + LayerMask.LayerToName(t.gameObject.layer) + ")"
                + " rb=" + (rb == null ? "none" : (rb.isKinematic ? "kinematic" : "dynamic") + "/grav="
                           + rb.useGravity)
                + " active=" + t.gameObject.activeInHierarchy
                + " held=" + it.IsHeld;

            Vector3 centre = col != null ? col.bounds.center : t.position;
            float dist = Vector3.Distance(eye, centre);

            // the ray a player would fire: camera -> the item's centre
            Vector3 dir = (centre - eye).normalized;
            RaycastHit hit;
            bool blocked = Physics.Raycast(eye, dir, out hit, dist + 0.05f, ~0,
                                          QueryTriggerInteraction.Collide);
            string first = "nothing at all";
            string verdict = "PICKABLE - the ray lands on it";
            if (blocked)
            {
                bool isIt = hit.collider.transform == t || hit.collider.transform.IsChildOf(t);
                first = "'" + hit.collider.name + "' at " + hit.distance.ToString("F2") + " m"
                      + (isIt ? "  <- the item itself" : "");
                verdict = isIt ? "PICKABLE - the ray lands on it"
                               : "BLOCKED by '" + hit.collider.name + "' "
                               + hit.distance.ToString("F2") + " m in front of the item, which is "
                               + dist.ToString("F2") + " m away";
            }
            if (dist > reach) verdict = "OUT OF REACH at " + dist.ToString("F2") + " m (limit "
                                     + reach.ToString("F2") + ")";

            Debug.Log(string.Format("[Pickup] {0,-20} at {1}  {2:F2} m away\n           {3}\n           {4}",
                it.displayName, t.position.ToString("F2"), dist, where, verdict));
        }
    }

    /// <summary>Runs the real round trip on every item and reports where it ends up.
    ///
    /// Pick up, drop, wait for the physics to settle, then measure. The point is to separate the
    /// three ways "I cannot pick it up again" can happen, which the player experiences as one
    /// sentence: the item flew somewhere unexpected, the item landed out of reach, or the item is
    /// where it should be and the ray still does not reach it. Only the third is a code bug, and the
    /// report says which one happened.</summary>
    [MenuItem("Tools/Escape Room/Simulate drop and re-pick every item")]
    public static void SimulateDropAndRepick()
    {
        var interactor = Object.FindFirstObjectByType<PlayerInteractor>();
        if (interactor == null) { Debug.LogError("[Pickup] no PlayerInteractor in the scene"); return; }
        var holder = interactor.HoldTransform;
        if (holder == null) { Debug.LogError("[Pickup] the interactor has no hold transform"); return; }

        foreach (var it in Object.FindObjectsByType<GrabbableItem>(FindObjectsSortMode.None))
        {
            var home = it.transform.position;
            it.PickUp(holder);
            bool picked = it.IsHeld;
            it.Drop();
            bool dropped = !it.IsHeld;

            // let it fall and settle. MovePosition needs FixedUpdate, so spin the physics by hand.
            Vector3 vel = Vector3.zero;
            float lowest = it.transform.position.y;
            for (int step = 0; step < 240; step++)
            {
                Physics.Simulate(0.02f);
                var r = it.GetComponent<Rigidbody>();
                vel = r != null ? r.linearVelocity : Vector3.zero;
                lowest = Mathf.Min(lowest, it.transform.position.y);
            }
            var settled = it.transform.position;
            float drift = Vector3.Distance(settled, home);

            Debug.Log(string.Format("[Pickup] {0,-20} picked={1} dropped={2}  home {3} -> settled {4}  "
                                  + "drift {5:F2} m  lowest y {6:F2}  speed {7:F2} m/s  "
                                  + "secsSinceDrop {8:F1}",
                it.displayName, picked, dropped, home.ToString("F2"), settled.ToString("F2"),
                drift, lowest, vel.magnitude, it.SecondsSinceDrop));
        }
        Debug.Log("[Pickup] round trip done. If drift is small and the item is within reach, the "
                + "problem is the RAY, not the throw - run Why can I not pick this up.");
    }


    // =====================================================================
    // COBERTURA DO PISO - ONDE O JOGADOR CAI NO VAZIO
    // =====================================================================

    /// <summary>Maps the floor: fires a ray straight DOWN across the whole room and reports every
    /// square metre that has no floor under it.
    ///
    /// Written for a regression I introduced. The player reported falling into the void beside the
    /// locker after the floor was rebuilt as a grid (D-118), and the grid was verified by a
    /// slab-overlap check that reported "0 points covered twice or more" - which says the tiles do
    /// not fight each other and says NOTHING about whether they cover the room. An overlap check can
    /// be perfectly happy about a hole: a hole is not an overlap.
    ///
    /// So coverage is asked as coverage. A 25 cm grid of downward rays over the room plus the corridor
    /// mouth, each one reporting the first solid surface it finds, so the output is both "is there
    /// floor here" and "how far did it fall before it found one". A ray that finds nothing inside
    /// `depth` is a hole the player can fall through, and it is printed with its world position so
    /// it can be walked to.</summary>
    [MenuItem("Tools/Escape Room/Check the floor has no holes")]
    public static void CheckFloorCoverage()
    {
        const float STEP = 0.25f;
        // The ray must START just above the floor and REACH well past it. It started at y = 6.0 with
        // a 4.0 m reach in the first version, which only ever got down to y = 2.0 - it could not see
        // the floor at all, and would have declared every sample in the room a hole. The same
        // no-margin mistake appeared in the grid's own coverage check, where a ray from y = 4 for
        // exactly 4.0 m ended ON the floor plane and reported 47 false holes. Both are the same bug:
        // a coverage ray with no slack either misses the surface or never reaches it.
        const float FROM = 1.5f;        // start the ray this far above the floor
        const float REACH = 3.0f;        // and give up after this far: deeper is a hole, not a floor

        // the area to audit: the room the shell defines, grown by a tile each way so the EDGES are
        // audited too. A hole at the edge is the likeliest kind - that is where a centred grid with a
        // 10 cm margin leaves a strip - and it is exactly the strip nobody looks at in a screenshot.
        var ceil = GameObject.Find(ROOT + "/Shell/Ceiling");
        if (ceil == null) { Debug.LogError("[Coverage] no " + ROOT + "/Shell/Ceiling"); return; }
        var cb = ceil.GetComponentInChildren<Renderer>().bounds;
        float pad = 3.0f;
        float minX = cb.min.x - pad, maxX = cb.max.x + pad;
        float minZ = cb.min.z - pad, maxZ = cb.max.z + pad;

        int holes = 0, found = 0;
        var holeList = new List<Vector3>();
        var byCollider = new Dictionary<string, int>();
        float lowestFloor = float.MaxValue, highestFloor = float.MinValue;

        for (float x = minX; x <= maxX; x += STEP)
        {
            for (float z = minZ; z <= maxZ; z += STEP)
            {
                var from = new Vector3(x, FROM, z);
                RaycastHit hit;
                if (!Physics.Raycast(from, Vector3.down, out hit, REACH, ~0,
                                     QueryTriggerInteraction.Ignore))
                {
                    holes++;
                    if (holeList.Count < 40) holeList.Add(new Vector3(x, 0f, z));
                    continue;
                }
                found++;
                string n = hit.collider.name;
                byCollider[n] = byCollider.ContainsKey(n) ? byCollider[n] + 1 : 1;
                lowestFloor = Mathf.Min(lowestFloor, hit.point.y);
                highestFloor = Mathf.Max(highestFloor, hit.point.y);
            }
        }

        int total = found + holes;
        Debug.Log(string.Format("[Coverage] {0} sample(s) at {1:F0} cm over x {2:F2}..{3:F2}, "
                               + "z {4:F2}..{5:F2}: {6} have floor, {7} DO NOT{8}. Floor heights "
                               + "y {9:F3}..{10:F3}.",
            total, STEP * 100f, minX, maxX, minZ, maxZ, found, holes,
            holes > 0 ? "   <== THE PLAYER CAN FALL THROUGH HERE" : "", lowestFloor, highestFloor));
        foreach (var kv in byCollider)
            Debug.Log("[Coverage]   " + kv.Value + " sample(s) hit '" + kv.Key + "'");
        foreach (var h in holeList)
            Debug.LogError("[Coverage] NO FLOOR at " + h.ToString("F2"));

        // Where the room actually is, so a hole can be judged against the walls rather than guessed.
        Debug.Log(string.Format("[Coverage] the room the shell defines is x {0:F2}..{1:F2}, "
                               + "z {2:F2}..{3:F2} - that is what the floor has to cover.",
            cb.min.x, cb.max.x, cb.min.z, cb.max.z));
    }


    // =====================================================================
    // ALINHAR E PREENCHER O PISO INTEIRO
    // =====================================================================

    /// <summary>Snaps every floor tile onto one lattice and fills every empty cell of that lattice
    /// out to the walls. The whole floor, in one pass.
    ///
    /// The player found the two things the 8x8 grid got wrong, and both were mine:
    ///
    ///  * <b>It stopped 9 m short.</b> The lattice was sized from the CEILING, which is 24.20 m deep.
    ///    The kit walls are not: they run to z = +21.18, so nearly a third of the walled area had no
    ///    floor at all, and standing there dropped the player into the void. A grid that covers the
    ///    room is not the same as a grid that covers the walls - and the walls are what say where the
    ///    floor has to be.
    ///  * <b>It was 10-20 cm short at the edges</b>, and I had justified that as "deliberate margin,
    ///    hidden under the walls". It was not hidden. The walls are 0.40 m thick and the room is not
    ///    a multiple of 3, so that strip was simply absent floor, and the player's own tiles landed in
    ///    it at y = -0.030 and y = -0.083, a 3-8 cm step below the grid.
    ///
    /// The lattice is the SAME one the existing grid already uses, derived from the ceiling exactly as
    /// before, so the tiles that are already correct stay correct and the player's new ones snap onto
    /// the same lines instead of forming a second grid beside it. That is the whole point of deriving
    /// it rather than measuring the tiles: a lattice measured from tiles drifts, and this one already
    /// drifted once (D-118).
    ///
    /// Coverage is asserted, not hoped for: the last few lines walk the filled area and raycast
    /// straight down on every cell, so "no holes" is a measured statement. An overlap check cannot do
    /// this - a hole is not an overlap, which is exactly how the 8x8 grid passed every check while the
    /// floor was missing.</summary>
    [MenuItem("Tools/Escape Room/Align and fill the whole floor")]
    public static void AlignAndFillFloor()
    {
        const string PREFIX = "Floor_01";
        const float ROOM_LEVEL = 1.0f;    // above this is the roof deck, not the floor
        const float FLOOR_Y = 0.0f;       // one plane. The grid was here; the rest joins it.

        // ---- 1. the lattice, derived from the ceiling exactly as the 8x8 grid was
        var ceil = GameObject.Find(ROOT + "/Shell/Ceiling");
        if (ceil == null)
        {
            Debug.LogError("[Floor] no " + ROOT + "/Shell/Ceiling - run Build Level first.");
            return;
        }
        var cb = ceil.GetComponentInChildren<Renderer>().bounds;
        var pf = AssetDatabase.LoadAssetAtPath<GameObject>(KIT + PREFIX + ".prefab");
        if (pf == null) { Debug.LogError("[Floor] prefab not found"); return; }
        var pmf = pf.GetComponentInChildren<MeshFilter>();
        if (pmf == null || pmf.sharedMesh == null) { Debug.LogError("[Floor] prefab has no mesh"); return; }
        var ms = pmf.sharedMesh.bounds.size;
        float pitch = Mathf.Max(ms.x, ms.z);

        // ---- where the prefab's pivot sits inside its own geometry
        //
        // Floor_01 is a plane whose pivot is NOT at its centre - the bounds start half a pitch to
        // one side of the transform. So "put the transform at the cell centre" and "the tile covers
        // the cell" are two different statements, and the pass was quietly doing both at once: it
        // PLACED by the centre and MEASURED by the bounds edge. The lattice therefore sat half a
        // pitch off its own cells, which is not a cosmetic error - it made 60 cells hold two tiles
        // and 60 hold none, on every single run, and the count never changed because the error was
        // perfectly repeatable.
        //
        // So the offset is MEASURED from the prefab rather than assumed, and both halves of the pass
        // then speak about the same thing: placement puts the tile's EDGE on the cell edge, and the
        // cell index is read from that same edge.
        var probe = (GameObject)PrefabUtility.InstantiatePrefab(pf);
        probe.transform.position = Vector3.zero;
        probe.transform.rotation = Quaternion.identity;
        var pb = probe.GetComponentInChildren<Renderer>().bounds;
        float pivotX = pb.min.x, pivotY = pb.min.y, pivotZ = pb.min.z;
        Object.DestroyImmediate(probe);
        Debug.Log(string.Format("[Floor] prefab pivot offset from its bounds min: ({0:F3}, {1:F3}, {2:F3})"
                                + " - its plane is edge-anchored, not centred",
                    pivotX, pivotY, pivotZ));

        int roomNx = Mathf.Max(1, Mathf.RoundToInt(cb.size.x / pitch));
        int roomNz = Mathf.Max(1, Mathf.RoundToInt(cb.size.z / pitch));
        float originX = (cb.min.x + cb.max.x) * 0.5f - roomNx * pitch * 0.5f;
        float originZ = (cb.min.z + cb.max.z) * 0.5f - roomNz * pitch * 0.5f;

        // ---- 2. the area the walls say has to be floored
        //
        // Every wall tile, ours and the player's, because the floor has to reach the OUTER face of
        // the walls: "under the walls" is what the player asked for, and a floor that stops at the
        // inner face leaves a 40 cm trench exactly where somebody walks along the wall.
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        var wallCount = 0;
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!IsKitWallTile(tr)) continue;
            if (!tr.gameObject.activeSelf) continue;
            var r = tr.GetComponent<Renderer>();
            if (r == null) continue;
            var b = r.bounds;
            if (b.size.y < 1.0f) continue;                  // not a standing wall
            minX = Mathf.Min(minX, b.min.x); maxX = Mathf.Max(maxX, b.max.x);
            minZ = Mathf.Min(minZ, b.min.z); maxZ = Mathf.Max(maxZ, b.max.z);
            wallCount++;
        }
        if (wallCount == 0) { Debug.LogError("[Floor] no wall tiles found to bound the floor"); return; }

        // the cell range that covers the walled area
        int i0 = Mathf.FloorToInt((minX - originX) / pitch);
        int i1 = Mathf.CeilToInt((maxX - originX) / pitch) - 1;
        int j0 = Mathf.FloorToInt((minZ - originZ) / pitch);
        int j1 = Mathf.CeilToInt((maxZ - originZ) / pitch) - 1;

        // ---- 3. gather the floor tiles that are already down
        //
        // ACTIVE ones only, and that word is load-bearing. The first version listed active and
        // inactive alike, so every tile a previous run had already switched off came back as a
        // candidate, won a cell on the height sort, and got switched back ON - displacing the tile
        // that was actually holding that cell. Run 3 reported 282 "duplicates" against 140 cells:
        // it was churning through its own graveyard. A tile that is already off has already been
        // dealt with and has no vote.
        var tiles = new List<Transform>();
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!IsInnermostTile(tr, PREFIX)) continue;
            if (!tr.gameObject.activeSelf) continue;
            var r = tr.GetComponent<Renderer>();
            if (r == null) continue;
            if (r.bounds.max.y > ROOM_LEVEL) continue;        // the roof deck
            tiles.Add(tr);
        }

        // The one-time leftover from the 8x8 grid, which parented its tiles to a holder object that
        // each later run replaced without destroying. The fill tiles now hang off ROOT directly, so
        // there is nothing to replace and nothing to leak.
        var oldGrid = GameObject.Find("FloorGrid");
        if (oldGrid != null) Object.DestroyImmediate(oldGrid);
        var root = GameObject.Find(ROOT);

        // ---- 4. snap what is there onto the lattice, one tile per cell
        //
        // Sorted by height so the tile that WINS a contested cell is the one nearest the floor plane.
        // Two tiles in one cell cannot both stay, and which one is arbitrary otherwise - and the
        // user's newer tiles sit lower than the grid, so "keep the lower one" would systematically
        // prefer the ones that need moving.
        var cells = new Dictionary<long, Transform>();
        var loose = new List<Transform>(tiles);
        loose.Sort((a, b) => ColliderY(b).CompareTo(ColliderY(a)));

        int snapped = 0, duplicates = 0, alreadyDead = 0;
        foreach (var tr in loose)
        {
            // Indexed by the tile's near EDGE, not by its bounds centre. The kit's Floor_01 is a
            // plane whose pivot sits on one edge, so its centre is half a pitch from where the
            // transform was put - and reading the centre made every tile compute the cell one step
            // BEHIND the cell it actually occupies. The pass then moved every tile a cell along,
            // collided each with its neighbour, deactivated the loser and refilled the hole: 110
            // snapped against 119 deactivated, on every single run, which is not alignment, it is a
            // demolition that happens to leave a floor behind. The edge is the one landmark that does
            // not care how the prefab is anchored.
            var bb = tr.GetComponent<Renderer>().bounds;
            int i = Mathf.RoundToInt((bb.min.x - originX) / pitch);
            int j = Mathf.RoundToInt((bb.min.z - originZ) / pitch);
            long key = ((long)i << 32) ^ (uint)j;
            if (cells.ContainsKey(key))
            {
                tr.gameObject.SetActive(false);
                duplicates++;
                continue;
            }
            cells[key] = tr;
            // place so the tile's EDGE lands on the cell edge, undoing the prefab's pivot offset
            var want = new Vector3(originX + i * pitch - pivotX, FLOOR_Y - pivotY,
                                   originZ + j * pitch - pivotZ);
            bool moved = tr.transform.position != want || !Mathf.Approximately(ColliderY(tr), FLOOR_Y);
            if (moved) { tr.transform.position = want; snapped++; }
            if (!tr.gameObject.activeSelf) { tr.gameObject.SetActive(true); alreadyDead++; }
        }

        // ---- 5. fill every empty cell
        int added = 0;
        for (int i = i0; i <= i1; i++)
        {
            for (int j = j0; j <= j1; j++)
            {
                long key = ((long)i << 32) ^ (uint)j;
                if (cells.ContainsKey(key)) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(pf);
                go.name = PREFIX + "_F" + i.ToString("D3") + "_" + j.ToString("D3");
                if (root != null) go.transform.SetParent(root.transform, true);
                go.transform.position = new Vector3(originX + i * pitch - pivotX, FLOOR_Y - pivotY,
                                                   originZ + j * pitch - pivotZ);
                go.transform.rotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                cells[key] = go.transform;
                added++;
            }
        }

        // ---- 6. report, and ASSERT the coverage
        //
        // SyncTransforms first, always. This pass creates ~30 prefabs and reactivates tiles in the
        // same tick it then raycasts, and with auto-sync off the colliders are not in the physics
        // scene yet - so the verification was reading a world one frame stale and declaring real
        // floor missing. Three false holes, all in the last row and column, all cells that had just
        // been written. Forcing the sync costs a millisecond and makes the assertion mean something.
        Physics.SyncTransforms();

        int cellsTotal = (i1 - i0 + 1) * (j1 - j0 + 1);
        Debug.Log(string.Format("[Floor] lattice {0:F2} m from {1}/Shell/Ceiling, origin ({2:F2}, {3:F2}); "
                               + "{4} wall tile(s) bound the area at x {5:F2}..{6:F2}, z {7:F2}..{8:F2}; "
                               + "cell range i {9}..{10}, j {11}..{12} = {13} cells",
            pitch, ROOT, originX, originZ, wallCount, minX, maxX, minZ, maxZ,
            i0, i1, j0, j1, cellsTotal));
        Debug.Log(string.Format("[Floor] snapped {0} existing tile(s) onto the lattice, deactivated "
                               + "{1} duplicate(s), reactivated {2}, and added {3} new tile(s). "
                               + "Every floor top is now y = {4:F3}.",
            snapped, duplicates, alreadyDead, added, FLOOR_Y));

        // the check that the previous one could not do: is every cell actually floored?
        int empty = 0;
        var emptyList = new List<string>();
        for (int i = i0; i <= i1; i++)
        {
            for (int j = j0; j <= j1; j++)
            {
                var centre = new Vector3(originX + (i + 0.5f) * pitch, 0f, originZ + (j + 0.5f) * pitch);
                // 1.5 m of margin ABOVE and 3 m of reach BELOW, deliberately. The first version cast
                // from y = 4 for exactly 4.0 m, which ENDS the ray on the floor plane - and a ray
                // that ends on a zero-thickness plane is a coin flip. It reported 47 holes on cells
                // that a ray from y = 1.5 hits dead centre at d = 1.49. A coverage check with no
                // margin manufactures the holes it is looking for, which is a fine way to spend an
                // afternoon and no way to ship a floor.
                if (Physics.Raycast(centre + Vector3.up * 1.5f, Vector3.down, 3f, ~0,
                                    QueryTriggerInteraction.Ignore)) continue;
                empty++;
                if (emptyList.Count < 20)
                    emptyList.Add(string.Format("({0:F1}, {1:F1})", centre.x, centre.z));
            }
        }
        if (empty == 0)
            Debug.Log("[Floor] VERIFIED: every one of the " + cellsTotal + " cells has floor under it.");
        else
            Debug.LogError("[Floor] " + empty + " cell(s) STILL HAVE NO FLOOR: "
                           + string.Join("  ", emptyList));
    }

    /// <summary>Top of an item's collider, or its renderer bounds centre, for sorting and snapping.</summary>
    static float ColliderY(Transform tr)
    {
        var c = tr.GetComponentInChildren<Collider>();
        if (c != null) return c.bounds.max.y;
        var r = tr.GetComponentInChildren<Renderer>();
        return r != null ? r.bounds.max.y : tr.position.y;
    }


    // =====================================================================
    // AUDITORIA: TILES DESATIVADOS, TETOS, GAPS E EMPILHAMENTO DE PAREDES
    // =====================================================================

    /// <summary>One audit of the three things the player reported: leftover disabled tiles, the
    /// ceilings, and the gaps in the walls - with an explicit count of walls stacked on each other.
    ///
    /// <para><b>Ceilings are audited as COVERAGE, not as alignment.</b> The floor taught this the hard
    /// way: an alignment check said the ceiling was one clean height at 3.700 while the player was
    /// looking at a ceiling with a hole in it. So the ceiling is sampled the way the floor is - a grid
    /// of upward rays over the walled area, each one reporting the first surface it meets - and a
    /// sample that reaches the sky is a hole. `0 coplanar` and `1 distinct height` are statements about
    /// agreement, and a hole is perfectly consistent with both.</para>
    ///
    /// <para><b>Walls are audited per RUN, with overlaps measured symmetrically</b> - and only the
    /// innermost match of a named group counts as a tile, or every tile overlaps itself by its own
    /// width (D-115). A run's spacing is compared against the tile's real face, and anything closer
    /// than that is reported as stacked, which is the thing the player asked me not to create: two
    /// walls occupying the same piece of floor.</para>
    ///
    /// <para><b>The disabled-tile inventory separates mine from the player's</b>, because the two are
    /// not the same thing to delete. Mine are reproducible from a menu item; theirs are not.</para></summary>
    [MenuItem("Tools/Escape Room/Audit tiles, ceilings and walls")]
    public static void AuditEverything()
    {
        // ---------------------------------------------------------------- A. disabled inventory
        foreach (var prefix in new[] { "Floor_01", WALL_PREFAB })
        {
            int mine = 0, theirs = 0, mineActive = 0, theirsActive = 0;
            foreach (var tr in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                                                                    FindObjectsSortMode.None))
            {
                if (!IsInnermostTile(tr, prefix)) continue;
                bool generated = tr.name.StartsWith(prefix + "_F", System.StringComparison.Ordinal)
                              || tr.name.StartsWith(prefix + "_G", System.StringComparison.Ordinal);
                if (generated) { if (tr.gameObject.activeSelf) mineActive++; else mine++; }
                else { if (tr.gameObject.activeSelf) theirsActive++; else theirs++; }
            }
            Debug.Log(string.Format("[Audit] {0}: {1} generated ({2} active / {3} DISABLED), "
                                   + "{4} hand-placed ({5} active / {6} DISABLED)",
                prefix, mine + mineActive, mineActive, mine, theirs + theirsActive, theirsActive, theirs));
        }

        // ---------------------------------------------------------------- B. ceiling coverage
        AuditCeilingCoverage();

        // ---------------------------------------------------------------- C. wall runs
        AuditWallRuns();
    }

    /// <summary>Samples the ceiling by firing UP from inside the room and reporting what stops each
    /// ray. A sample that reaches the sky is a hole, whatever the heights say.</summary>
    static void AuditCeilingCoverage()
    {
        const float STEP = 0.50f;
        const float FROM = 1.5f;        // eye-ish height, inside the room
        const float REACH = 4.5f;       // up to 6.0 m: the shell's top is 5.43

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!IsKitWallTile(tr)) continue;
            if (!tr.gameObject.activeSelf) continue;
            var r = tr.GetComponent<Renderer>();
            if (r == null) continue;
            if (r.bounds.size.y < 1.0f) continue;
            minX = Mathf.Min(minX, r.bounds.min.x); maxX = Mathf.Max(maxX, r.bounds.max.x);
            minZ = Mathf.Min(minZ, r.bounds.min.z); maxZ = Mathf.Max(maxZ, r.bounds.max.z);
        }
        if (minX > maxX) { Debug.Log("[Ceiling] no wall tiles to bound the audit"); return; }

        int open = 0, solid = 0;
        var byCollider = new Dictionary<string, int>();
        var openList = new List<string>();
        for (float x = minX; x <= maxX; x += STEP)
        {
            for (float z = minZ; z <= maxZ; z += STEP)
            {
                var from = new Vector3(x, FROM, z);
                RaycastHit hit;
                // Collide, NOT Ignore. The room's own Ceiling is a TRIGGER, and Ignore skips triggers -
                // so the check was blind to the very slab whose job is to stop the sky, and reported
                // 1816 holes in a room that has none. A coverage check that excludes the thing doing
                // the covering is not a coverage check.
                if (Physics.Raycast(from, Vector3.up, out hit, REACH, ~0, QueryTriggerInteraction.Collide))
                {
                    solid++;
                    string n = hit.collider.name;
                    byCollider[n] = byCollider.ContainsKey(n) ? byCollider[n] + 1 : 1;
                }
                else
                {
                    open++;
                    if (openList.Count < 25)
                        openList.Add(string.Format("({0:F1}, {1:F1})", x, z));
                }
            }
        }
        Debug.Log(string.Format("[Ceiling] over x {0:F2}..{1:F2}, z {2:F2}..{3:F2} at {4:F0} cm: "
                               + "{5} sample(s) reach a surface, {6} REACH THE SKY{7}",
            minX, maxX, minZ, maxZ, STEP * 100f, solid, open,
            open > 0 ? "   <== THE CEILING HAS HOLES" : ""));
        foreach (var kv in byCollider) Debug.Log("[Ceiling]   " + kv.Value + " sample(s) hit '" + kv.Key + "'");
        if (open > 0) Debug.LogWarning("[Ceiling] open samples: " + string.Join("  ", openList));
    }

    /// <summary>Disables one of any two wall tiles that are stacked on each other.
    ///
    /// The player asked for the gaps to be closed "without stacking several walls on top of each
    /// other", and the audit says 15 pairs are stacked: seven on the west run and six on the east. Two
    /// tiles sharing more than 90% of a face are not two walls, they are one wall and a copy of it,
    /// and the copy is what gets switched off - the alternative, sliding them apart, would move the
    /// player's geometry to tidy up my own diagnostics.
    ///
    /// <para><b>Which one is the copy:</b> mine goes before theirs, every time. The player's tiles are
    /// authored content and this project has no business deleting them; the additive tiles this
    /// builder placed are its own, and a builder that cannot recognise its own surplus is a builder
    /// that accumulates. When both are the player's, the later one in the run goes.</para>
    ///
    /// <para><b>Run membership includes the box fillers</b>, so a filler sitting against a kit tile
    /// is never mistaken for a duplicate of it - the fillers are edge-anchored and flush, which is
    /// the state this whole exercise is for.</para>
    ///
    /// <para>Disabled, not destroyed, and every removal is logged with both names. Safe to run twice:
    /// the second pass finds nothing.</para></summary>
    [MenuItem("Tools/Escape Room/Remove stacked walls")]
    public static void RemoveStackedWalls()
    {
        const float STACK = 0.90f;       // share of a face that makes a tile a copy rather than a wall

        var rends = new List<Renderer>();
        var owners = new List<Transform>();
        var mine = new List<bool>();
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!tr.gameObject.activeSelf) continue;
            var r = tr.GetComponent<Renderer>();
            if (r == null) continue;
            if (!IsKitWallTile(tr) && tr.name != "NorthFiller" && tr.name != "GapFiller") continue;
            rends.Add(r); owners.Add(tr);
            var root = GameObject.Find(ROOT);
            mine.Add(root != null && tr.IsChildOf(root.transform));
        }
        if (rends.Count == 0) { Debug.LogError("[Unstack] no wall pieces found"); return; }

        var faceSamples = new List<float>();
        foreach (var r in rends) faceSamples.Add(Mathf.Max(r.bounds.size.x, r.bounds.size.z));
        faceSamples.Sort();
        float face = faceSamples[faceSamples.Count / 2];

        int removed = 0;
        var log = new List<string>();
        var dead = new HashSet<int>();

        for (int i = 0; i < rends.Count; i++)
        {
            if (dead.Contains(i)) continue;
            for (int j = i + 1; j < rends.Count; j++)
            {
                if (dead.Contains(j)) continue;
                var a = rends[i].bounds; var b = rends[j].bounds;
                bool aAlongX = a.size.x >= a.size.z, bAlongX = b.size.x >= b.size.z;
                if (aAlongX != bAlongX) continue;                  // a corner, not a copy
                int axis = aAlongX ? 0 : 1;
                float aPerp = axis == 0 ? a.center.z : a.center.x;
                float bPerp = axis == 0 ? b.center.z : b.center.x;
                if (Mathf.Abs(aPerp - bPerp) > 0.6f) continue;    // different rows

                float over = Mathf.Min(axis == 0 ? a.max.x : a.max.z, axis == 0 ? b.max.x : b.max.z)
                           - Mathf.Max(axis == 0 ? a.min.x : a.min.z, axis == 0 ? b.min.x : b.min.z);
                if (over < face * STACK) continue;                // flush or a small lap is fine

                // mine yields to theirs; among equals the later one in the scan yields
                int victim = mine[i] && !mine[j] ? i : (mine[j] && !mine[i] ? j : j);
                dead.Add(victim);
                removed++;
                if (log.Count < 16)
                    log.Add("  disabled '" + PathOf(owners[victim].transform) + "' - it sat "
                            + (over * 100f).ToString("F0") + " cm inside '"
                            + PathOf(owners[victim == i ? j : i].transform) + "'"
                            + (mine[victim] ? " (ours)" : " (the player's)"));
            }
        }

        foreach (int i in dead) owners[i].gameObject.SetActive(false);
        Debug.Log("[Unstack] " + removed + " stacked wall tile(s) disabled - each shared more than "
                  + (STACK * 100f).ToString("F0") + "% of a face with another tile in the same row. "
                  + "DEACTIVATED, not deleted; re-enable in the Hierarchy to undo.");
        foreach (var l in log) Debug.Log("[Unstack]" + l);
    }

    /// <summary>Reports every wall run with its real spacing, its gaps, and anything genuinely stacked.
    ///
    /// <para>A run is built from kit tiles AND the box fillers already sitting in the wall line, in
    /// ONE list. Without the fillers the north run reports a 10.3 m gap that is neither a hole nor a
    /// defect: it is the exit opening plus the `NorthFiller` boxes, invisible to a check that only
    /// looks for `Wall_Simple_01`. Two lessons wearing the same coat - a check that only sees the
    /// names it knows will always find something to complain about.</para>
    ///
    /// <para>Runs are found by SNAPPING the perpendicular coordinate to a grid, never by walking the
    /// tiles and starting a run on a threshold: a loose threshold chains scattered tiles into one
    /// enormous run and then reports every one of them as an overlap (D-115). Overlap is measured
    /// SYMMETRICALLY, so it does not depend on which tile the scan happened to list first (D-120).</para>
    ///
    /// <para>And <b>flush is not stacked</b>. Tiles that merely touch are butted end to end, which is
    /// the state the whole exercise is trying to reach. An earlier version counted flush as stacking
    /// and announced "20 pairs of walls on top of each other" for a room whose walls were correct.</para></summary>
    static void AuditWallRuns()
    {
        const float SNAP = 0.5f;
        var rends = new List<Renderer>();
        var names = new List<string>();
        foreach (var tile in Object.FindObjectsByType<Transform>())
        {
            if (!tile.gameObject.activeSelf) continue;
            // A tile carries its own Renderer (IsKitWallTile), so read THAT renderer - never
            // GetComponentInChildren on a group, which returns the group's first child and produced
            // phantom 7 m and 12 m "tiles" that overlapped everything and manufactured the stacking.
            var own = tile.GetComponent<Renderer>();
            if (own == null) continue;
            bool isKit = IsKitWallTile(tile);
            bool isFiller = tile.name == "NorthFiller" || tile.name == "GapFiller";
            if (!isKit && !isFiller) continue;
            rends.Add(own);
            names.Add(PathOf(tile));
        }
        if (rends.Count == 0) { Debug.Log("[Runs] no active wall pieces"); return; }

        int totalGaps = 0, totalOverlaps = 0, totalStacked = 0;
        var faceSamples = new List<float>();
        foreach (var r in rends) faceSamples.Add(Mathf.Max(r.bounds.size.x, r.bounds.size.z));
        faceSamples.Sort();
        float face = faceSamples[faceSamples.Count / 2];

        foreach (int axis in new[] { 0, 1 })
        {
            var byBand = new Dictionary<long, List<int>>();
            for (int i = 0; i < rends.Count; i++)
            {
                var b = rends[i].bounds;
                bool alongX = b.size.x >= b.size.z;
                if ((axis == 0) != alongX) continue;
                float perp = axis == 0 ? b.center.z : b.center.x;
                long key = (long)Mathf.Round(perp / SNAP);
                if (!byBand.ContainsKey(key)) byBand[key] = new List<int>();
                byBand[key].Add(i);
            }
            var keys = new List<long>(byBand.Keys);
            keys.Sort();
            foreach (var key in keys)
            {
                var bnd = byBand[key];
                if (bnd.Count < 2) continue;
                bnd.Sort((a, b) => Along(rends[a], axis).CompareTo(Along(rends[b], axis)));
                float perp = 0f;
                foreach (var i in bnd) perp += axis == 0 ? rends[i].bounds.center.z : rends[i].bounds.center.x;
                perp /= bnd.Count;

                int gaps = 0, overlaps = 0, stacked = 0;
                float worstGap = 0f, worstOver = 0f;
                string worstAt = "";
                for (int i = 1; i < bnd.Count; i++)
                {
                    float aEnd = AlongEnd(rends[bnd[i - 1]], axis);
                    float bStart = AlongStart(rends[bnd[i]], axis);
                    float over = aEnd - bStart;
                    if (over > face * 0.9f)
                    {
                        stacked++;
                        if (over > worstOver)
                        { worstOver = over; worstAt = names[bnd[i - 1]] + "  +  " + names[bnd[i]]; }
                    }
                    else if (over > 0.01f) overlaps++;

                    float step = bStart - aEnd;
                    if (step > 0.01f) { gaps++; worstGap = Mathf.Max(worstGap, step); }
                }
                totalGaps += gaps; totalOverlaps += overlaps; totalStacked += stacked;
                if (gaps == 0 && overlaps == 0 && stacked == 0) continue;   // an even run, say nothing
                string flag = gaps > 0
                    ? string.Format("  <== {0} GAP(S), worst {1:F0} cm", gaps, worstGap * 100f)
                    : overlaps > 0 ? string.Format("  <== {0} overlap(s)", overlaps)
                    : string.Format("  <== {0} STACKED on the same spot", stacked);
                Debug.Log(string.Format("[Runs] run along {0}, row {1} = {2:F2}: {3} piece(s){4}",
                    axis == 0 ? "X" : "Z", axis == 0 ? "z" : "x", perp, bnd.Count, flag));
                if (worstAt != "")
                    Debug.LogWarning("[Runs]      worst: " + worstOver.ToString("F0") + " cm, " + worstAt);
            }
        }
        Debug.Log(string.Format("[Runs] {0} wall piece(s), median face {1:F2} m: {2} gap(s), "
                               + "{3} overlap(s), {4} pair(s) genuinely STACKED{5}",
            rends.Count, face, totalGaps, totalOverlaps, totalStacked,
            totalStacked > 0 ? "   <== WALLS ON TOP OF WALLS" : "   <== none stacked"));
    }


    // =====================================================================
    // OS TRES PEDIDOS: TILES DESATIVADOS, TETOS, GAPS DAS PAREDES
    // =====================================================================

    /// <summary>Destroys the leftover disabled tiles. Both sets, because both are now redundant.
    ///
    /// After the floor was rebuilt as a lattice (D-123) the disabled tiles are not a safety net any
    /// more - every cell is occupied by an active tile, verified - so they are copies of geometry
    /// that is already in the scene. The audit found 18 disabled hand-placed `Floor_01` and 819
    /// objects for 140 cells, most of the excess being mine from the runs that churned before the
    /// pivot offset was fixed (D-124).
    ///
    /// They are DESTROYED, not deactivated, and that is a departure worth naming: D-117 kept
    /// everything reversible. It is still right for anything the player authored, so the rule drawn
    /// here is the narrow one - a disabled tile is destroyed only if some ACTIVE tile already
    /// occupies the same lattice cell, i.e. it is provably a duplicate. Anything disabled with no
    /// active replacement is left alone and reported, because that one might be the player's only
    /// copy of something. The item is safe to run twice; the second run finds nothing to do.</summary>
    [MenuItem("Tools/Escape Room/Delete the redundant disabled tiles")]
    public static void DeleteRedundantDisabledTiles()
    {
        const string PREFIX = "Floor_01";

        // what cell does each ACTIVE tile occupy? the lattice, as AlignAndFillFloor defines it
        var ceil = GameObject.Find(ROOT + "/Shell/Ceiling");
        if (ceil == null) { Debug.LogError("[Cleanup] no " + ROOT + "/Shell/Ceiling"); return; }
        var cb = ceil.GetComponentInChildren<Renderer>().bounds;
        var pf = AssetDatabase.LoadAssetAtPath<GameObject>(KIT + PREFIX + ".prefab");
        var pmf = pf != null ? pf.GetComponentInChildren<MeshFilter>() : null;
        if (pmf == null || pmf.sharedMesh == null) { Debug.LogError("[Cleanup] prefab has no mesh"); return; }
        var ms = pmf.sharedMesh.bounds.size;
        float pitch = Mathf.Max(ms.x, ms.z);
        int roomNx = Mathf.Max(1, Mathf.RoundToInt(cb.size.x / pitch));
        int roomNz = Mathf.Max(1, Mathf.RoundToInt(cb.size.z / pitch));
        float originX = (cb.min.x + cb.max.x) * 0.5f - roomNx * pitch * 0.5f;
        float originZ = (cb.min.z + cb.max.z) * 0.5f - roomNz * pitch * 0.5f;

        var occupied = new HashSet<long>();
        var dead = new List<Transform>();
        foreach (var tr in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                                                               FindObjectsSortMode.None))
        {
            if (!IsInnermostTile(tr, PREFIX)) continue;
            var r = tr.GetComponent<Renderer>();
            if (r == null) continue;
            if (r.bounds.max.y > 1.0f) continue;                 // the roof deck, not the floor
            var b = r.bounds;
            int i = Mathf.RoundToInt((b.min.x - originX) / pitch);
            int j = Mathf.RoundToInt((b.min.z - originZ) / pitch);
            long key = ((long)i << 32) ^ (uint)j;
            if (tr.gameObject.activeSelf) occupied.Add(key);
            else dead.Add(tr);
        }

        int destroyed = 0, kept = 0;
        var keptNames = new List<string>();
        foreach (var tr in dead)
        {
            var r = tr.GetComponent<Renderer>();
            var b = r.bounds;
            int i = Mathf.RoundToInt((b.min.x - originX) / pitch);
            int j = Mathf.RoundToInt((b.min.z - originZ) / pitch);
            long key = ((long)i << 32) ^ (uint)j;
            if (occupied.Contains(key)) { Object.DestroyImmediate(tr.gameObject); destroyed++; }
            else
            {
                kept++;
                if (keptNames.Count < 10) keptNames.Add(tr.name);
            }
        }
        Debug.Log("[Cleanup] destroyed " + destroyed + " disabled tile(s) that duplicate an active one; "
                  + "kept " + kept + " with no active replacement"
                  + (kept > 0 ? " (" + string.Join(", ", keptNames) + (kept > 10 ? ", ..." : "") + ")"
                             : "."));
    }

    /// <summary>Makes the room's overhead ONE height.
    ///
    /// Measured, the room had two: my `Ceiling` slab with its underside at 3.70 m, and a deck of the
    /// player's own `Floor_01` tiles lying at 2.90 m - which is 80 cm lower, inside the room, and is
    /// what an upward ray actually hits over most of the floor. A ceiling check that looked only for
    /// agreement between ceiling pieces said "1 distinct height, 3.700 m" and was right, because the
    /// 2.90 deck is named `Floor_01` and so was filed as a FLOOR, not a ceiling. Same lesson as the
    /// floor hole: the check looked at the property it knew and the defect lived in another one.
    ///
    /// The 3.70 m slab is the designed one - the clear height the jump height and the camera height
    /// are both derived from (D-45) - so the 2.90 deck is what goes, and only where it is INSIDE the
    /// room. Anything outside the ceiling's footprint is left alone: the rooftop deck beyond the
    /// corridor is meant to be open to the sky (D-42), and swallowing it would put a lid over the
    /// ending the level is built around.
    ///
    /// Deactivated, not destroyed, and the count and the bounds are logged so it is reversible.</summary>
    [MenuItem("Tools/Escape Room/Flatten the ceiling to one height")]
    public static void FlattenCeiling()
    {
        const string PREFIX = "Floor_01";
        const float DECK_Y = 3.20f;      // anything whose top is between 2 and 3.2 is a false ceiling

        var ceil = GameObject.Find(ROOT + "/Shell/Ceiling");
        if (ceil == null) { Debug.LogError("[CeilingFix] no " + ROOT + "/Shell/Ceiling"); return; }
        var cb = ceil.GetComponentInChildren<Renderer>().bounds;

        int off = 0, outside = 0;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var tr in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                                                                FindObjectsSortMode.None))
        {
            if (!IsInnermostTile(tr, PREFIX)) continue;
            var r = tr.GetComponent<Renderer>();
            if (r == null) continue;
            var b = r.bounds;
            if (b.max.y < 2.0f || b.max.y > DECK_Y) continue;      // not the 2.9 m deck
            bool inside = b.min.x >= cb.min.x - 0.5f && b.max.x <= cb.max.x + 0.5f
                       && b.min.z >= cb.min.z - 0.5f && b.max.z <= cb.max.z + 0.5f;
            if (!inside) { outside++; continue; }
            if (!tr.gameObject.activeSelf) continue;
            tr.gameObject.SetActive(false);
            off++;
            minX = Mathf.Min(minX, b.min.x); maxX = Mathf.Max(maxX, b.max.x);
            minZ = Mathf.Min(minZ, b.min.z); maxZ = Mathf.Max(maxZ, b.max.z);
        }

        Debug.Log("[CeilingFix] the room's overhead is now one surface: the " + ROOT
                  + "/Shell/Ceiling slab with its underside at y = " + cb.min.y.ToString("F2") + " m.");
        if (off > 0)
            Debug.Log("[CeilingFix] switched OFF " + off + " false-ceiling tile(s) that lay inside the "
                      + "room, x " + minX.ToString("F1") + ".." + maxX.ToString("F1") + ", z "
                      + minZ.ToString("F1") + ".." + maxZ.ToString("F1")
                      + " - DEACTIVATED, not deleted. Re-enable them in the Hierarchy to undo.");
        else
            Debug.Log("[CeilingFix] no false-ceiling tile left inside the room.");
        if (outside > 0)
            Debug.Log("[CeilingFix] " + outside + " tile(s) at that height lie OUTSIDE the room and were "
                      + "left active on purpose - the rooftop beyond the corridor is meant to be open sky.");
    }

    /// <summary>Fills every gap in every wall run, with a filler sized to the gap.
    ///
    /// The constraint the player set is the one this is built around: <b>nothing gets stacked</b>. So
    /// no kit tile is ever added here. A kit tile is 3.00 m; a gap of 1.4 m cannot take one without
    /// sticking 1.6 m out the far side and into its neighbour, which is precisely the "several walls
    /// on top of each other" this is meant to avoid. Every filler is a box whose WIDTH IS THE GAP, so
    /// it is flush on both sides by construction and cannot overlap anything.
    ///
    /// Gaps are measured between consecutive tiles in a run, symmetrically, and the run is built from
    /// both kit tiles and the existing box fillers so that a filler is not mistaken for a hole (which
    /// is what made the north run report a false 10.3 m gap). Tiles that merely touch are left
    /// alone - flush is the desired state, and an earlier version of the audit counted it as stacking
    /// (D-126).</summary>
    [MenuItem("Tools/Escape Room/Seal every gap in the walls")]
    public static void SealWallGaps()
    {
        const float MIN_FILL = 0.15f;     // narrower than this is a seam, not a gap
        const float HEIGHT = 3.00f;
        const float THICK = 0.40f;

        // ---- collect the wall line: kit tiles AND the box fillers already in it
        var pieces = new List<Renderer>();
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!tr.gameObject.activeSelf) continue;
            var r = tr.GetComponent<Renderer>();
            if (r == null) continue;
            if (!IsKitWallTile(tr) && tr.name != "NorthFiller") continue;
            pieces.Add(r);
        }
        if (pieces.Count == 0) { Debug.LogError("[Seal] no wall pieces found"); return; }

        // The exit opening, and the ONLY place in this room where a gap in a wall is the design.
        // A filler is a 3.00 m wall; this gap is 3.00 m wide. Filling it is not a near miss, it is a
        // wall across the door - which is the exact defect the player reported at the start of this
        // work (D-101) and the one the whole doorway check exists to keep out. The first version of
        // this item had no idea the opening was there and put a 3.00 m filler straight across it,
        // plus a 10.30 m one beside it. A repair that does not know what is intentional will always
        // repair the one thing that was deliberate.
        const float OPEN_L = -7.5f, OPEN_R = -4.5f, OPEN_ROW = 0.8f;

        int added = 0, tooNarrow = 0, skippedOpening = 0;
        var addedLog = new List<string>();
        var host = new GameObject("WallGapFiller");
        var root = GameObject.Find(ROOT);
        if (root != null) host.transform.SetParent(root.transform, false);
        host.transform.position = Vector3.zero;

        foreach (int axis in new[] { 0, 1 })
        {
            // A band is 1.00 m, not 0.50. The north run has the player's tiles at z = -11.75 and
            // mine at z = -11.55, and a 0.50 m key puts those 20 cm apart into DIFFERENT bands - so
            // the run split in two, the fillers on it were not seen as part of it, and each half then
            // reported the doorway as a gap. One coarse key instead of two fine ones, and the wall
            // line is a wall line.
            var byBand = new Dictionary<long, List<Renderer>>();
            for (int i = 0; i < pieces.Count; i++)
            {
                var b = pieces[i].bounds;
                bool alongX = b.size.x >= b.size.z;
                if ((axis == 0) != alongX) continue;
                float perp = axis == 0 ? b.center.z : b.center.x;
                long key = (long)Mathf.Round(perp / 1.0f);
                if (!byBand.ContainsKey(key)) byBand[key] = new List<Renderer>();
                byBand[key].Add(pieces[i]);
            }
            var keys = new List<long>(byBand.Keys);
            keys.Sort();
            foreach (var key in keys)
            {
                var bnd = byBand[key];
                if (bnd.Count < 2) continue;
                bnd.Sort((a, b) => Along(a, axis).CompareTo(Along(b, axis)));
                float perp = 0f;
                foreach (var r in bnd) perp += axis == 0 ? r.bounds.center.z : r.bounds.center.x;
                perp /= bnd.Count;

                for (int i = 1; i < bnd.Count; i++)
                {
                    float aEnd = AlongEnd(bnd[i - 1], axis);
                    float bStart = AlongStart(bnd[i], axis);
                    float gap = bStart - aEnd;
                    if (gap <= MIN_FILL) { if (gap > 0.001f) tooNarrow++; continue; }

                    // never fill the exit opening
                    if (axis == 0 && Mathf.Abs(perp - WALL_N) < OPEN_ROW
                        && aEnd < OPEN_R && bStart > OPEN_L)
                    {
                        skippedOpening++;
                        continue;
                    }

                    float centre = (aEnd + bStart) * 0.5f;
                    var pos = axis == 0
                        ? new Vector3(centre, HEIGHT * 0.5f, perp)
                        : new Vector3(perp, HEIGHT * 0.5f, centre);
                    var size = axis == 0
                        ? new Vector3(gap, HEIGHT, THICK)
                        : new Vector3(THICK, HEIGHT, gap);
                    EsTheme.Box("GapFiller", host.transform, pos, size, _shell);
                    added++;
                    if (addedLog.Count < 14)
                        addedLog.Add("  " + (axis == 0 ? "X" : "Z") + " gap of "
                                    + (gap * 100f).ToString("F1") + " cm at "
                                    + (axis == 0 ? "x" : "z") + " = " + centre.ToString("F2"));
                }
            }
        }

        Debug.Log("[Seal] added " + added + " gap filler(s), each exactly as wide as its gap, so "
                  + "nothing overlaps and nothing is stacked." + (tooNarrow > 0
                  ? " " + tooNarrow + " gap(s) under " + (MIN_FILL * 100f).ToString("F0")
                    + " cm were left as seams." : "")
                  + (skippedOpening > 0
                    ? " " + skippedOpening + " gap(s) on the north row were SKIPPED because they are "
                      + "the exit opening at x " + OPEN_L.ToString("F1") + ".." + OPEN_R.ToString("F1")
                      + " - a hole on purpose." : ""));
        foreach (var l in addedLog) Debug.Log("[Seal]" + l);
        SaveSceneNow();
    }

    /// <summary>Rewrites every wall run as a flush, continuous line, and switches off the surplus.
    ///
    /// The player asked for the wall gaps closed "without stacking several walls on top of each
    /// other". The west run turned out to be the stacking, not a gap, and it is worth stating exactly
    /// what it was: EIGHT pieces, each **12.29 m long**, all coplanar at x = -12.01, each offset about
    /// 3 m from the one before. Ninety-eight metres of wall over a thirty-two metre run, every pair
    /// overlapping by 9.3-9.6 m. Coplanar and overlapping is the combination guaranteed to fight, and
    /// what shows through the fight is the dark void behind - which is the black the player reported
    /// between <c>Wall_Simple_01 (14)</c> and <c>Wall_Simple_01 (2)</c>. There was never a gap there;
    /// there were eight walls where three belong.
    ///
    /// <para><b>Redundancy is judged by INTERVAL.</b> The first version kept a piece whenever it ended
    /// beyond the current coverage front - and every 12 m piece does, because each adds 3 m of reach,
    /// so all eight were kept and nothing changed. The question is not "does it reach further" but
    /// "is any part of it already wall": a 12 m piece whose left 9 m is already covered is 9 m of
    /// surplus however useful its tail is. A piece is switched off when it is entirely inside wall
    /// that is already kept; a piece that spans a real gap is kept.</para>
    ///
    /// <para>Kept pieces are then slid to BUTT the coverage front, with 1 cm of lap - enough to kill a
    /// seam, far too little to fight.</para>
    ///
    /// <para><b>The exit opening is never bridged.</b> A tidy pass that does not know the opening runs
    /// a wall across the door - which is what the first gap-filler did, twice (D-129). When a piece
    /// would cross the opening's span, the run stops there.</para>
    ///
    /// <para>Disabled, never destroyed, every removal logged. Safe to run twice.</para></summary>
    [MenuItem("Tools/Escape Room/Tidy every wall run")]
    public static void TidyWallRuns()
    {
        const float LAP = 0.008f;         // 8 mm of lap: kills the seam, and stays under the 1 cm the audit calls an overlap
        const float MIN_SHIFT = 0.001f;    // 1 mm: below this a "move" is floating-point noise, not work
        const float HOLE = 0.05f;         // 5 cm: a real gap, not a rounding wobble
        const float OPEN_L = -7.5f, OPEN_R = -4.5f, OPEN_ROW = 0.8f;

        var rends = new List<Renderer>();
        var owners = new List<Transform>();
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!tr.gameObject.activeSelf) continue;
            var own = tr.GetComponent<Renderer>();
            if (own == null) continue;
            if (!IsKitWallTile(tr) && tr.name != "NorthFiller" && tr.name != "GapFiller") continue;
            rends.Add(own); owners.Add(tr);
        }
        if (rends.Count == 0) { Debug.LogError("[Tidy] no wall pieces found"); return; }

        int disabled = 0, moved = 0, stoppedAtOpening = 0;
        var log = new List<string>();

        foreach (int axis in new[] { 0, 1 })
        {
            var byBand = new Dictionary<long, List<int>>();
            for (int i = 0; i < rends.Count; i++)
            {
                var b = rends[i].bounds;
                bool alongX = b.size.x >= b.size.z;
                if ((axis == 0) != alongX) continue;
                float perp = axis == 0 ? b.center.z : b.center.x;
                long key = (long)Mathf.Round(perp / 1.0f);
                if (!byBand.ContainsKey(key)) byBand[key] = new List<int>();
                byBand[key].Add(i);
            }
            var keys = new List<long>(byBand.Keys);
            keys.Sort();
            foreach (var key in keys)
            {
                var bnd = byBand[key];
                if (bnd.Count < 2) continue;
                bnd.Sort((a, b) => AlongStart(rends[a], axis).CompareTo(AlongStart(rends[b], axis)));
                float perp = 0f;
                foreach (var i in bnd) perp += axis == 0 ? rends[i].bounds.center.z : rends[i].bounds.center.x;
                perp /= bnd.Count;
                bool openingHere = axis == 0 && Mathf.Abs(perp - WALL_N) < OPEN_ROW;

                // covered intervals along the run axis, in order
                var covered = new List<float[]>();
                int runKept = 0;

                foreach (int i in bnd)
                {
                    float start = AlongStart(rends[i], axis);
                    float end = AlongEnd(rends[i], axis);
                    float len = end - start;
                    float front = covered.Count == 0 ? float.MinValue : covered[covered.Count - 1][1];

                    // entirely inside wall already kept? then it is surplus.
                    bool insideKept = false;
                    foreach (var iv in covered)
                        if (start < iv[1] - HOLE && end > iv[0] + HOLE) { insideKept = true; break; }
                    bool addsNew = end > front + HOLE;

                    if (insideKept && !addsNew)
                    {
                        owners[i].gameObject.SetActive(false);
                        disabled++;
                        if (log.Count < 20)
                            log.Add("  off '" + PathOf(owners[i].transform) + "' - all "
                                    + len.ToString("F2") + " m already wall");
                        continue;
                    }

                    // would this bridge the exit opening? if so the run stops here.
                    if (openingHere && covered.Count > 0)
                    {
                        float newStart = start < front - LAP ? front - LAP : start;
                        if (newStart < OPEN_R && end > OPEN_L && start > runFirstStart(bnd, rends, axis))
                        {
                            stoppedAtOpening++;
                            if (log.Count < 20)
                                log.Add("  run stops at x = " + front.ToString("F2")
                                        + " - the exit opening begins at " + OPEN_L.ToString("F1"));
                            break;
                        }
                    }

                    // Slide so the piece OVERLAPS its neighbour by LAP - it laps, it does not gap.
                    //
                    // The sign here was wrong and the comment above it described the intent rather
                    // than the code: `shift = front + SEAM - start` starts the piece SEAM AFTER the
                    // coverage front, which is a 1 cm GAP, the exact opposite of the "1 cm of lap"
                    // the constant claimed to be. It read as harmless - 1 cm in a 40 cm wall - and it
                    // hid two things. The audit kept reporting 18 gaps that no run could ever close,
                    // because the pass that owns the runs was manufacturing one at every butt joint;
                    // and the pass was not idempotent, re-spacing the same 6 pieces on every single
                    // Build Level, because a piece sitting 1 cm past the front always looks like it
                    // needs moving again. A repair that re-applies forever is not converging, and the
                    // log line "6 re-spaced" every run is what finally made it visible.
                    //
                    // LAP is 8 mm, not 10: the audit calls anything over 1 cm an overlap, so 8 mm
                    // laps flush - killing the seam - without ever registering as one.
                    if (covered.Count > 0 && start < front - LAP && end > front)
                    {
                        float shift = front - LAP - start;

                        // Below a millimetre this is not a move, it is a rounding artefact, and the
                        // run reports it as one anyway: the piece sits a hair short of the front, the
                        // test fires, the shift rounds to 0,0 cm - and because the arithmetic leaves
                        // it a hair short again, EVERY subsequent Build Level fires it again. Six
                        // phantom moves on every run, forever, which is what made me spend a cycle
                        // hunting a conflict between two passes that do not actually disagree.
                        //
                        // So a move has to be a move. Below MIN_SHIFT the piece is already flush for
                        // any purpose that can see it, and the honest report is silence.
                        if (Mathf.Abs(shift) >= MIN_SHIFT)
                        {
                            var p = owners[i].transform.position;
                            if (axis == 0) p.x += shift; else p.z += shift;
                            owners[i].transform.position = p;
                            moved++;
                            if (log.Count < 20)
                                log.Add("  moved '" + PathOf(owners[i].transform) + "' by "
                                        + (shift * 100f).ToString("F1") + " cm along "
                                        + (axis == 0 ? "X" : "Z") + " to lap its neighbour");
                            start = front - LAP; end = start + len;
                        }
                    }

                    covered.Add(new[] { start, end });
                    runKept++;
                }
                if (log.Count < 20)
                    log.Add("  run at " + (axis == 0 ? "z" : "x") + " = " + perp.ToString("F2")
                            + ": kept " + runKept + " of " + bnd.Count);
            }
        }

        Debug.Log("[Tidy] " + moved + " re-spaced to butt, " + disabled + " surplus switched off, "
                  + stoppedAtOpening + " run(s) stopped at the exit opening (never bridged).");
        foreach (var l in log) Debug.Log("[Tidy]" + l);
        SaveSceneNow();
    }

    static float runFirstStart(List<int> bnd, List<Renderer> rends, int axis)
    { return AlongStart(rends[bnd[0]], axis); }

    /// <summary>Fills the gap between every wall's top and the ceiling, one piece per wall, sized to
    /// the gap exactly.
    ///
    /// The report: "walls with a gap between them and the ceiling". Measured, the tops are at EIGHT
    /// different heights - 2.29, 2.80, 2.89, 3.00, 3.80, 4.00, 4.80 m - against a ceiling underside at
    /// 3.70 m. So some walls leave a 70 cm slot, one leaves 141 cm, and the 4.80 m ones punch 110 cm
    /// THROUGH the ceiling. The `Band_*` pieces were meant to cover this but sit at 3.80..4.63, i.e.
    /// above the 3.70 ceiling, so they fill nothing you can see from inside.
    ///
    /// <para>Per wall, not per run: a run can be a row of tiles at mixed heights, and a band sized to
    /// the tallest would overshoot the short ones and one sized to the shortest leaves the tall ones
    /// open. So each wall gets a filler exactly its own gap tall, from its own top up to the ceiling -
    /// flush at both ends by construction.</para>
    ///
    /// <para>The kit's material, not the shell's: an earlier version of the gap filler used the
    /// shell material and the player reported the walls as "without texture" (D-132).</para>
    ///
    /// <para>Walls already reaching the ceiling, or above it, get nothing. Walls poking through are
    /// left as they are - trimming a wall the player made taller is a placement decision, not a gap to
    /// fill - and reported so the decision is visible. Safe to run twice.</para></summary>
    [MenuItem("Tools/Escape Room/Fill the gap from every wall to the ceiling")]
    public static void FillWallToCeiling()
    {
        var ceil = GameObject.Find(ROOT + "/Shell/Ceiling");
        if (ceil == null) { Debug.LogError("[WallTop] no " + ROOT + "/Shell/Ceiling"); return; }
        float ceilY = ceil.GetComponentInChildren<Renderer>().bounds.min.y;    // 3.70, the underside
        const float MIN_FILL = 0.05f;

        var host = GameObject.Find(ROOT + "/WallTopFiller");
        if (host == null)
        {
            host = new GameObject("WallTopFiller");
            host.transform.SetParent(GameObject.Find(ROOT).transform, false);
            host.transform.position = Vector3.zero;
        }
        else
        {
            // a second run would double the fillers, so the previous ones go
            // Collect first, destroy second. Two separate mistakes lived in these three lines and
            // both are silent:
            //
            // (1) Iterating `host.transform` yields the child TRANSFORM, and DestroyImmediate
            //     refuses to destroy a Transform - it logs "Can't destroy Transform component" and
            //     leaves the object standing. So the old fillers were never cleared.
            // (2) Even with the GameObject, destroying children WHILE enumerating them invalidates
            //     the enumerator and skips about every other one. Which is why the count settled at
            //     49 instead of 25: each run destroyed roughly half of the previous 49, then added
            //     25 back. 24 - 24 - 25 = 49, every time.
            //
            // A repair pass that is not idempotent does not merely fail to help: each run makes the
            // scene worse, and here it was manufacturing 24 coplanar duplicates - the same
            // walls-on-top-of-walls defect just removed from the west run (D-134).
            var stale = new List<GameObject>();
            foreach (UnityEngine.Object ch in host.transform)
                stale.Add(((Transform)ch).gameObject);
            foreach (var go in stale) Object.DestroyImmediate(go);
        }

        int filled = 0, above = 0, reached = 0, fallen = 0;
        var filledLog = new List<string>();
        var fallenLog = new List<string>();
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!tr.gameObject.activeSelf) continue;
            var own = tr.GetComponent<Renderer>();
            if (own == null) continue;
            // The fillers are walls too. Left out the first time, and that is why three NorthFillers
            // kept a 70 cm slot to the ceiling after a pass that reported every other wall closed:
            // IsKitWallTile asks for the Wall_Simple_01 name, and a filler is named NorthFiller.
            // A name filter cannot see what it was not told to look for - the same lesson as
            // Door_Arch_01 in D-112, and the reason a gap audit has to ask the same question twice.
            if (!IsKitWallTile(tr) && tr.name != "NorthFiller" && tr.name != "GapFiller") continue;
            var b = own.bounds;
            float top = b.max.y;

            // A wall that is not standing up is not a wall with a gap; it is a wall lying on the
            // floor, and the gap above it is most of the room. <c>Wall_Simple_01 (4)</c> has its top
            // at y = -0.08 - it is face down on the ground. Standing a 3.78 m pillar on it would
            // have been the pass inventing architecture out of a dropped prop, so these are reported
            // and left alone: whether a fallen wall gets stood back up is the player's call, not
            // something a gap filler gets to decide by arithmetic.
            if (b.max.y < 0.5f)
            {
                fallen++;
                if (fallenLog.Count < 10)
                    fallenLog.Add("  " + PathOf(tr) + "  y " + b.min.y.ToString("F2") + ".."
                                  + b.max.y.ToString("F2") + " @ " + b.center.ToString("F1")
                                  + " - it is lying on the floor, not standing");
                continue;
            }

            if (top >= ceilY - MIN_FILL) { if (top > ceilY + 0.05f) above++; else reached++; continue; }
            float gap = ceilY - top;
            if (gap < MIN_FILL) { reached++; continue; }

            // The filler runs from the wall's top to the ceiling - and DOWN to the floor as well if
            // the wall is floating. Two of the user's walls sit 51 and 52 cm off the ground
            // (<c>Wall_Simple_01 (5)</c> at y 0.52, <c>(9)</c> at y 0.51), which is the same defect at
            // the other end of the same wall, and the player reported one end of it. A wall that does
            // not touch either surface is not a wall, it is a panel; so the filler spans the full
            // room height for that footprint and there is nothing left to see through.
            bool floats = b.min.y > MIN_FILL;
            float bottom = floats ? 0f : top;
            float height = ceilY - bottom;

            var size = new Vector3(b.size.x + 0.02f, height, b.size.z + 0.02f);
            var centre = new Vector3(b.center.x, bottom + height * 0.5f, b.center.z);
            EsTheme.Box("WallTop", host.transform, centre, size, _kitWall);
            filled++;
            if (filledLog.Count < 14)
                filledLog.Add("  " + PathOf(tr) + "  y " + b.min.y.ToString("F2") + ".."
                              + b.max.y.ToString("F2") + " -> " + bottom.ToString("F2") + ".."
                              + ceilY.ToString("F2") + (floats
                                  ? "  (it was floating, so the filler runs floor to ceiling)"
                                  : "  +" + (gap * 100f).ToString("F0") + " cm to the ceiling"));
        }

        Debug.Log("[WallTop] ceiling underside y = " + ceilY.ToString("F2") + "; filled " + filled
                  + " wall(s) up to it, " + reached + " already reached it, " + above
                  + " already poke through it (left as-is - that is a placement decision, not a gap), "
                  + fallen + " lying on the floor (left as-is).");
        foreach (var l in filledLog) Debug.Log("[WallTop]" + l);
        foreach (var l in fallenLog) Debug.LogWarning("[WallTop]" + l);
        SaveSceneNow();
    }

    /// <summary>Switches off every wall standing free in the middle of the room floor.
    ///
    /// <para>The report: "remove the walls that are in the middle of the room". Measured against the
    /// room's own footprint, exactly <b>two</b> walls qualify, and both are mine:
    /// <c>Wall_Simple_01_06</c> at x 4,3 z −8,0..−5,0 (3,90 m from the nearest wall) and
    /// <c>Wall_Simple_01_11</c> at z 5,1 x 6,0..9,0 (3,30 m). They are the interior partition stubs
    /// the builder lays down: one running north-south from the north wall, one running east-west from
    /// the middle toward the east wall. Each stops 3 m short of anything, so they divide nothing -
    /// they are obstacles in open floor.</para>
    ///
    /// <para><b>The datum is the ceiling's footprint, not the tiles</b> (D-118), and that matters
    /// here more than anywhere else. Measuring against the tiles would make the output the next
    /// input: remove a tile, the room measures smaller, and the next run calls another wall
    /// "perimeter". The ceiling never moves, so the room has a fixed size to be judged against.</para>
    ///
    /// <para><b>The test is INCLUSION, not distance from a centre.</b> A wall is "in the middle" only
    /// if its WHOLE footprint sits more than a metre inside the room. A distance-from-centre test
    /// would catch a 3 m perimeter wall whose centre happens to be 2 m in, and would miss a short
    /// pillar standing dead centre. Containment cannot be fooled by a wall's size or its
    /// orientation.</para>
    ///
    /// <para><b>What it will not touch, and what it will.</b> The margin keeps every perimeter wall,
    /// however deeply it notches inward. It also keeps <c>_04</c>, <c>_05</c> and <c>_12</c>, which
    /// are the same species of stub as the ones being removed but do reach a wall, so they are
    /// partitions rather than clutter - and those are listed in the log rather than silently spared,
    /// because "the pass decided" is not an answer the player can check.</para>
    ///
    /// <para><b>I was wrong about the exit, and it is worth writing down.</b> I claimed removing
    /// <c>_03</c> would "wall the player's way out". It would do the opposite. <c>_00</c> and
    /// <c>_03</c> are the two walls <i>flanking</i> the 3 m doorway, and taking a flank away opens
    /// that side - it cannot seal anything. A wall on the edge of an opening is a boundary, and
    /// deleting a boundary adds space. Getting that backwards would have made me refuse the player's
    /// explicit instruction on the grounds that it was dangerous, which is the worst way to be wrong:
    /// it substitutes my guess for what they asked for.</para>
    ///
    /// <para>Switched off, never destroyed (D-102, D-117), every removal named in the log with its
    /// position. Idempotent: a wall already off is not counted again.</para>
    /// </summary>
    [MenuItem("Tools/Escape Room/Remove the walls in the middle of the room")]
    public static void RemoveMiddleOfRoomWalls()
    {
        // the ceiling is the datum, and it is found by name because there is exactly one
        UnityEngine.GameObject ceil = null;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t.name == "Ceiling") { ceil = t.gameObject; break; }
        if (ceil == null) { Debug.LogError("[Middle] no Ceiling - cannot measure the room"); return; }
        var cr = ceil.GetComponentInChildren<Renderer>();
        if (cr == null) { Debug.LogError("[Middle] the Ceiling has no Renderer"); return; }
        var cb = cr.bounds;

        const float MIDDLE_M = 1.0f;      // a metre in from every wall: inside this, it is perimeter
        float rx0 = cb.min.x, rx1 = cb.max.x, rz0 = cb.min.z, rz1 = cb.max.z;

        // The player named <c>Wall_Simple_01_03</c>, and no geometric rule finds it - so it is named
        // here rather than inferred. That is not a shortcut, it is the honest reading of the shape:
        // _03, _00 and _05 are the SAME object - a 3 m stub hanging off the north wall, same
        // orientation, same 0,25 m thickness, same 3,90 m reach. A test that catches _03 catches
        // _05, and a test that spares _05 spares _03. The only thing that separates them is which
        // one the player pointed at, so the pass has to be told the rest.
        //
        // _00 comes along because it is _03's twin on the other side of the same doorway: remove one
        // flank of a corridor and leave the other and the result reads as damage, not as a decision.
        // _05, _04 and _12 are here because the player was told they were kept and said otherwise.
        var named = new List<string> { "Wall_Simple_01_03", "Wall_Simple_01_00", "Wall_Simple_01_05",
                                       "Wall_Simple_01_04", "Wall_Simple_01_12" };

        // _13 is the odd one out and gets its own list. It lies OUTSIDE the room - x 12,0..15,0,
        // past the east wall - and it was described as such before the player chose it, so removing it
        // is a decision they made with the position in front of them. It cannot go in the list above,
        // because the room-containment check that stops the corridor's namesake from being taken
        // would stop this one too, and the rule would quietly overrule them. One list for "named and
        // inside", one for "named and deliberately not" - so the exception is visible in the source
        // instead of hiding inside a condition.
        var namedOutside = new List<string> { "Wall_Simple_01_13" };

        int removed = 0, alreadyOff = 0, kept = 0;
        var log = new List<string>();
        var nearMiss = new List<string>();

        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            // A tile carries its own Renderer; a group does not, and measuring a group measures its
            // first child (D-136). All of these are groups, so this distinction is the whole test -
            // the group is found, and its own mesh child is what gets switched off.
            if (!t.name.StartsWith(WALL_PREFAB, System.StringComparison.Ordinal)) continue;
            if (t.GetComponent<Renderer>() != null) continue;        // that is the mesh, not the group
            var rend = t.GetComponentInChildren<Renderer>();
            if (rend == null) continue;
            var b = rend.bounds;

            bool wholeFootprintInside = b.min.x > rx0 + MIDDLE_M && b.max.x < rx1 - MIDDLE_M
                                     && b.min.z > rz0 + MIDDLE_M && b.max.z < rz1 - MIDDLE_M;
            bool wasNamed = named.Contains(t.name) || namedOutside.Contains(t.name);

            // A name is not an identity. There are TWO <c>Wall_Simple_01_05</c> in this scene and TWO
            // <c>Wall_Simple_01_00</c> (D-136): the stubs inside the room, and the exit corridor's
            // walls outside it. Matching the bare name would take the corridor's flank with it, and
            // that flank is a hundred metres of approach the player walks through. So a named removal
            // must ALSO be inside this room, which is a question about geometry and not about
            // spelling - the corridor's copy sits at z -18,40..-15,40, entirely beyond the wall.
            if (wasNamed)
            {
                bool insideRoom = b.max.x > rx0 && b.min.x < rx1 && b.max.z > rz0 && b.min.z < rz1;
                if (!insideRoom && !namedOutside.Contains(t.name))
                {
                    if (nearMiss.Count < 8)
                        nearMiss.Add("  kept '" + PathOf(t) + "'  x " + b.min.x.ToString("F2") + ".."
                                     + b.max.x.ToString("F2") + "  z " + b.min.z.ToString("F2") + ".."
                                     + b.max.z.ToString("F2") + " - same NAME as one that was removed, "
                                     + "but it is outside the room (the exit corridor), so the name "
                                     + "did not get it");
                    kept++;
                    continue;
                }
            }
            if (!wholeFootprintInside && !wasNamed)
            {
                // Report the ones that are stubs off a wall but were NOT named, so the choice is
                // visible instead of buried: _04, _05 and _12 are the same species as the ones being
                // removed, and whether they go is the player's call, not this pass's.
                if (Mathf.Max(b.size.x, b.size.z) >= 2.0f && Mathf.Min(b.size.x, b.size.z) < 1.0f
                    && nearMiss.Count < 8)
                    nearMiss.Add("  kept '" + PathOf(t) + "'  x " + b.min.x.ToString("F2") + ".."
                                 + b.max.x.ToString("F2") + "  z " + b.min.z.ToString("F2") + ".."
                                 + b.max.z.ToString("F2") + " - same shape as the ones removed, "
                                 + "but it reaches a wall, so it is a partition and not clutter");
                kept++;
                continue;
            }

            if (!t.gameObject.activeSelf) { alreadyOff++; continue; }

            t.gameObject.SetActive(false);
            removed++;
            if (log.Count < 16)
                log.Add("  off '" + PathOf(t) + "'  x " + b.min.x.ToString("F2") + ".."
                        + b.max.x.ToString("F2") + "  z " + b.min.z.ToString("F2") + ".."
                        + b.max.z.ToString("F2") + "  - "
                        + (wasNamed
                            // say WHY, and do not dress a named removal up as a geometric finding
                            ? (namedOutside.Contains(t.name)
                                ? "named by the player, and it stands OUTSIDE the room"
                                : "named by the player; it does reach a wall, so this was a choice, not clutter")
                            : "standing free in the room, "
                              + Mathf.Min(Mathf.Min(b.min.x - rx0, rx1 - b.max.x),
                                          Mathf.Min(b.min.z - rz0, rz1 - b.max.z)).ToString("F2")
                              + " m from the nearest wall"));
        }

        Debug.Log("[Middle] room x " + rx0.ToString("F2") + ".." + rx1.ToString("F2") + " z "
                  + rz0.ToString("F2") + ".." + rz1.ToString("F2") + " (from the ceiling, the fixed "
                  + "datum). Switched off " + removed + " wall(s) from the middle of the room, "
                  + kept + " kept as perimeter or partition, " + alreadyOff
                  + " already off. The doorway itself was never bridged.");
        foreach (var l in log) Debug.Log("[Middle]" + l);
        foreach (var l in nearMiss) Debug.Log("[Middle]" + l);
        SaveSceneNow();
    }

    /// <summary>Stretches every arch in Y until it meets the ceiling, keeping its base where it was.
    ///
    /// The report: "adjust the arcs so they reach the ceiling, like <c>Wall_Arc_90_01</c>".
    /// Measured, all three active arches top out at y = 2,92 against a 3,70 ceiling - a 78 cm slot,
    /// the same defect the walls had (D-138), and it survived that fix because the ceiling pass
    /// filters on <c>IsKitWallTile</c> plus the two filler names and an arch matches none of the
    /// three. Second time that lesson has cost something.
    ///
    /// <para><b>Stretched, not boxed - and the measurement is why.</b> The obvious fix is a filler on
    /// top, exactly as the walls got. That would be wrong here, and the raycast says so: sampling a
    /// 5x5 grid across an arch's 3x3 bounding box and firing down from y = 6, almost every sample
    /// hits the FLOOR. The box is nearly all empty air, because a 90-degree corner arch is a curved
    /// wall that occupies a fraction of its own bounds. Filling those bounds would not close a
    /// gap, it would build a solid block where the arch curves away, and it would be visible from
    /// inside the room.</para>
    ///
    /// <para>So the arc is scaled in <b>Y only</b>, which leaves X and Z - and therefore the
    /// footprint, and the corner it sits in - exactly as they were. The 26% vertical stretch makes
    /// the corner fillet taller, which reads as a taller arch rather than a mistake.</para>
    ///
    /// <para><b>The GROUP is scaled, not the mesh.</b> Each arc is a root object holding the mesh plus
    /// three <c>Collider_*</c> children, and those colliders are SIBLINGS of the mesh. Scaling the mesh
    /// alone would grow the visible arch and leave its top 78 cm with no collision - a wall the
    /// player walks straight through, in a room whose whole subject is not being able to leave.</para>
    ///
    /// <para><b>The pivot is measured, not trusted.</b> These groups happen to pivot at the base, so
    /// the scale grows upward from the floor, but that is a fact about this prefab and not a
    /// guarantee. The base is re-measured after scaling and the group translated back if it moved -
    /// trusting a pivot is how one of my passes shifted a run 26 m (D-120).</para>
    ///
    /// <para>Idempotent: an arc already within 2 cm of the ceiling is skipped, so a second run finds
    /// nothing to do. The exit opening is never near an arch and is not consulted at all.</para></summary>
    [MenuItem("Tools/Escape Room/Raise every arch to the ceiling")]
    public static void RaiseArchesToCeiling()
    {
        UnityEngine.GameObject ceil = null;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t.name == "Ceiling") { ceil = t.gameObject; break; }
        if (ceil == null) { Debug.LogError("[Arc] no Ceiling in the scene"); return; }
        var cr = ceil.GetComponentInChildren<Renderer>();
        if (cr == null) { Debug.LogError("[Arc] the Ceiling has no Renderer"); return; }
        float ceilY = cr.bounds.min.y;                     // 3,70 - the underside, not the middle
        const float MIN = 0.02f;                           // 2 cm: below this it is already touching

        int raised = 0, already = 0;
        var log = new List<string>();

        foreach (var grp in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!grp.name.StartsWith(ARC_PREFAB, System.StringComparison.Ordinal)) continue;
            if (grp.GetComponent<Renderer>() != null) continue;      // that is the mesh, not the group
            if (!grp.gameObject.activeSelf) continue;
            var rend = grp.GetComponentInChildren<Renderer>();
            if (rend == null) continue;

            var b = rend.bounds;
            if (ceilY - b.max.y <= MIN) { already++; continue; }

            float bottom0 = b.min.y;
            float want = ceilY - bottom0;                 // world height that puts the top on the ceil
            float have = b.max.y - b.min.y;
            if (have <= 0.01f) { already++; continue; }   // flat: nothing to stretch
            float factor = want / have;

            var s = grp.localScale;
            grp.localScale = new Vector3(s.x, s.y * factor, s.z);

            // the pivot is only a claim until it has been measured
            var after = rend.bounds;
            if (Mathf.Abs(after.min.y - bottom0) > 0.005f)
            {
                var p = grp.position;
                p.y += bottom0 - after.min.y;
                grp.position = p;
                after = rend.bounds;
            }

            raised++;
            if (log.Count < 12)
                log.Add("  " + PathOf(grp) + "  y " + bottom0.ToString("F2") + ".." + b.max.y.ToString("F2")
                        + " -> " + after.min.y.ToString("F2") + ".." + after.max.y.ToString("F2")
                        + "  (x" + factor.ToString("F3") + " in Y, base held at " + bottom0.ToString("F2")
                        + ", top " + (after.max.y - ceilY > 0.005f ? "OVER by "
                                        + ((after.max.y - ceilY) * 100f).ToString("F1") + "cm"
                                        : "flush with")
                        + " the " + ceilY.ToString("F2") + " ceiling)");
        }

        Debug.Log("[Arc] ceiling underside y = " + ceilY.ToString("F2") + "; stretched " + raised
                  + " arch(es) up to it in Y (X and Z untouched, so the footprint is unchanged), "
                  + already + " already reached it. The exit opening was not touched.");
        foreach (var l in log) Debug.Log("[Arc]" + l);
        SaveSceneNow();
    }

    /// <summary>Fits the blast door's frame to the opening it actually sits in, measured not assumed.</summary>
    ///
    /// <para>The report: "at the exit door there are gaps on both sides". Measured, the opening in the
    /// north wall is x -7,50..-4,50 - three metres - and the frame is built for 2,60:
    /// <c>Frame_L</c> spans x -7,30..-7,00 and <c>Frame_R</c> x -5,00..-4,70, leaving
    /// <b>20 cm of open wall on each side</b> of the door, with the void of the corridor showing
    /// through. <c>Frame_T</c> has the same 20 cm short at each end. So the player walks up to what
    /// looks like a sealed blast door and sees daylight either side of it.</para>
    ///
    /// <para><b>The opening is MEASURED, from the wall pieces, not written down.</b> The frame is
    /// fitted to whatever gap the north wall actually has at the door, found by asking which pieces
    /// end at the opening's edges. A hard-coded <c>-7.5f</c> would be a second copy of a fact that
    /// <see cref="WALL_N"/> and the wall layout already own, and the two would drift apart the first
    /// time a wall moved - which is exactly how a 20 cm mismatch gets in and stays.</para>
    ///
    /// <para>Each leg is <b>widened toward its own side</b>, not moved: the inner edges of the two
    /// legs and of the header are the door's clear opening, and those are what must not change -
    /// narrowing the clear width would shrink the doorway the player has to walk through. So the leg
    /// grows outward to the wall and the header grows to the new leg span, and the leaf, stripes and
    /// card reader are untouched because they are inside the clear opening already.</para>
    ///
    /// <para>The doorway is never bridged: the measured opening has to be non-empty and plausible, and
    /// a frame that already spans it does nothing (D-129).</para></summary>
    [MenuItem("Tools/Escape Room/Fit the blast door frame to the opening")]
    public static void FitDoorFrameToOpening()
    {
        var door = GameObject.Find(ROOT + "/BlastDoor");
        if (door == null) { Debug.LogError("[DoorFit] no " + ROOT + "/BlastDoor"); return; }

        // --- measure the opening from the wall pieces that flank it ---
        //
        // The sentinels are MinValue/MaxValue and NOT the other way round, and that is not a detail:
        // I first wrote openL = float.MaxValue and then tested `b.max.x > openL`, which can never be
        // true - no wall coordinate is greater than 3,4e38. So flankL stayed 0 forever, the pass
        // refused to do anything, and the frame kept its 20 cm gaps either side of the door.
        //
        // The lesson is not the sign. It is that my own replication snippet had ALREADY printed
        // "flankL=0 flankR=0" and I read past it, because I had an explanation ready (a band test
        // failing) and a number that contradicted me was easier to reinterpret than to chase. The
        // diagnostic that finally settled it was printing the pieces themselves, not more counters.
        float openL = float.MinValue, openR = float.MaxValue;
        int flankL = 0, flankR = 0;
        foreach (var t in Object.FindObjectsByType<Transform>())
        {
            if (!t.gameObject.activeSelf) continue;
            var own = t.GetComponent<Renderer>();
            if (own == null) continue;
            if (!t.name.StartsWith(WALL_PREFAB, System.StringComparison.Ordinal)
                && t.name != "NorthFiller" && t.name != "GapFiller") continue;
            var b = own.bounds;
            if (Mathf.Abs(b.center.z - WALL_N) > 0.7f) continue;      // the north row only
            // the piece that ends nearest to, and on the left of, the door's centre line
            if (b.max.x <= EXIT_X) { if (b.max.x > openL) { openL = b.max.x; flankL++; } }
            else { if (b.min.x < openR) { openR = b.min.x; flankR++; } }
        }
        // A repair that gives up must say WHAT it saw. "could not find both flanks" with no numbers
        // is a message that costs a cycle on its own, because it cannot distinguish "the wall is
        // gone" from "my filter is wrong" - and on the first run of this pass it was the second,
        // and the message said nothing that would have revealed it.
        int candidatos = 0;
        float zMin = float.MaxValue, zMax = float.MinValue;
        var perto = new List<string>();
        foreach (var t in Object.FindObjectsByType<Transform>())
        {
            if (!t.gameObject.activeSelf) continue;
            var own = t.GetComponent<Renderer>();
            if (own == null) continue;
            if (!t.name.StartsWith(WALL_PREFAB, System.StringComparison.Ordinal)
                && t.name != "NorthFiller" && t.name != "GapFiller") continue;
            candidatos++;
            float cz = own.bounds.center.z;
            zMin = Mathf.Min(zMin, cz); zMax = Mathf.Max(zMax, cz);
            if (perto.Count < 6) perto.Add("'" + t.name + "' z=" + cz.ToString("F4")
                                          + " dz=" + Mathf.Abs(cz - WALL_N).ToString("F4")
                                          + " max.x=" + own.bounds.max.x.ToString("F2")
                                          + " min.x=" + own.bounds.min.x.ToString("F2"));
        }
        if (flankL == 0 || flankR == 0)
        {
            Debug.LogError("[DoorFit] no both flanks: flankL=" + flankL + " flankR=" + flankR
                          + "  openL=" + openL.ToString("F2") + " openR=" + openR.ToString("F2")
                          + "  WALL_N=" + WALL_N.ToString("F2") + " EXIT_X=" + EXIT_X.ToString("F2")
                          + "  wall/filler pieces named: " + candidatos + "  their z spans "
                          + zMin.ToString("F2") + ".." + zMax.ToString("F2")
                          + (candidatos == 0 ? "  <- none at all: the wall is gone, or the name filter is wrong"
                                             : "  <- pieces exist, so the band or side test is what failed"));
            for (int i = 0; i < perto.Count; i++) Debug.LogError("[DoorFit]   perto: " + perto[i]);
            return;
        }
        float width = openR - openL;
        if (width < 0.5f || width > 8f)
        { Debug.LogError("[DoorFit] implausible opening " + width.ToString("F2") + " m - refusing"); return; }

        // --- fit the three frame pieces to it ---
        int changed = 0;
        var log = new List<string>();

        var frameL = door.transform.Find("Frame_L");
        var frameR = door.transform.Find("Frame_R");
        var frameT = door.transform.Find("Frame_T");
        if (frameL == null || frameR == null || frameT == null)
        { Debug.LogError("[DoorFit] Frame_L / Frame_R / Frame_T not all present under " + ROOT + "/BlastDoor"); return; }

        // The clear opening is the LEAF's span, not the legs'.
        //
        // My first version froze each leg's own inner edge and grew it outward, which is right in
        // intent and wrong in effect: widening a leg moves its inner edge too, so after the fit the
        // clear width had GROWN from 2,00 m to 2,50 m and the 2,00 m leaf no longer filled the frame -
        // a new 50 cm gap, on the opposite side, made while fixing the first one. The inner edge has
        // to come from something that does not move when the leg widens, and the leaf is exactly
        // that: it is the thing that defines the doorway the player walks through.
        //
        // So: leaf edges are the inner edges (pinned), the wall opening is the outer edges (pinned),
        // and each leg's width falls out of the two. Nothing is free to drift.
        // Two locals, one declaration. Writing `float innerL = innerR = 0f;` declares innerL and
        // ASSIGNS to innerR - which has to already exist, so it is a compile error, and it produced
        // seven CS0103s that my brace-depth check could not see. A depth check proves the file is
        // STRUCTURED; it says nothing about whether a name is declared, and I had claimed the file
        // was correct on the strength of one. That is the overclaim worth remembering: the check I
        // ran bounds what I am allowed to conclude from it, and not one step further.
        float innerL = 0f, innerR = 0f;
        bool leafFound = false;
        var leaf = door.transform.Find("DoorPivot");
        if (leaf != null)
        {
            var lr = leaf.GetComponentInChildren<Renderer>();
            if (lr != null) { innerL = lr.bounds.min.x; innerR = lr.bounds.max.x; leafFound = true; }
        }
        if (!leafFound)
        {
            // no leaf to read: fall back to the legs' own inner edges, which is what I had, and say
            // so, because in that case the clear width CAN drift and the reader should know it
            innerL = frameL.GetComponentInChildren<Renderer>().bounds.max.x;
            innerR = frameR.GetComponentInChildren<Renderer>().bounds.min.x;
            Debug.LogWarning("[DoorFit] no DoorPivot leaf found; using the legs' own inner edges as "
                             + "the clear opening. If the legs are later widened, the opening follows "
                             + "them and can drift.");
        }
        if (innerR - innerL < 0.3f)
        { Debug.LogError("[DoorFit] implausible clear opening " + (innerR - innerL).ToString("F2") + " m"); return; }

        // Fit by RATIO, then pin the reference edge by measurement.
        //
        // The first version multiplied the local scale by the target WIDTH (0,30 x 0,50 = 0,15) instead
        // of by the ratio, so it shrank the leg to 3 cm - and because it multiplied again on every run,
        // the second run shrank it further. A pass that compounds its own error is worse than one that
        // does nothing. With the ratio, targetWidth == currentWidth on the second run, the ratio is 1,
        // nothing changes, and the pass converges - the only property that makes it safe in the chain.
        const float TOL = 0.002f;      // 2 mm: below this the leg already meets the wall

        for (int lado = 0; lado < 3; lado++)
        {
            Transform f = lado == 0 ? frameL : lado == 1 ? frameR : frameT;
            string nome = lado == 0 ? "Frame_L" : lado == 1 ? "Frame_R" : "Frame_T";
            var rend = f.GetComponentInChildren<Renderer>();
            var b0 = rend.bounds;
            float larguraAtual = b0.max.x - b0.min.x;
            if (larguraAtual < 0.001f) continue;

            // for the legs: outer edge at the wall, inner edge at the leaf. for the header: both ends
            // at the wall, because it closes the top of the whole opening.
            // refEhMax says which edge of the piece the reference lands on: Frame_L's leaf edge is its
            // MAX.x, Frame_R's is its MIN.x - the two legs face each other - and the header is pinned
            // by MIN.x, because it closes the whole opening rather than framing the leaf.
            float larguraAlvo, bordaReferencia; bool refEhMax;
            if (lado == 0) { larguraAlvo = innerL - openL; bordaReferencia = innerL; refEhMax = true; }
            else if (lado == 1) { larguraAlvo = openR - innerR; bordaReferencia = innerR; refEhMax = false; }
            else { larguraAlvo = openR - openL; bordaReferencia = openL; refEhMax = false; }
            if (larguraAlvo <= 0.01f) continue;

            if (Mathf.Abs(larguraAlvo - larguraAtual) <= TOL)
            {
                if (log.Count < 8) log.Add("  " + nome + " already fits (" + larguraAtual.ToString("F3")
                                          + " m) - nothing to do");
                continue;
            }

            float razao = larguraAlvo / larguraAtual;
            f.localScale = new Vector3(f.localScale.x * razao, f.localScale.y, f.localScale.z);

            // then place it by where it actually ended up, not by where the maths hoped
            var b1 = rend.bounds;
            var p = f.position;
            p.x += bordaReferencia - (refEhMax ? b1.max.x : b1.min.x);
            f.position = p;

            var b2 = rend.bounds;
            changed++;
            if (log.Count < 8)
                log.Add("  " + nome + " " + larguraAtual.ToString("F2") + " -> " + larguraAlvo.ToString("F2")
                        + " m wide, spans " + b2.min.x.ToString("F2") + ".." + b2.max.x.ToString("F2")
                        + (lado == 2 ? " (closes the full opening)" : " (pinned to the leaf edge)"));
        }

        Debug.Log("[DoorFit] opening measured from the wall at x " + openL.ToString("F2") + ".."
                  + openR.ToString("F2") + " = " + width.ToString("F2") + " m. Fitted " + changed
                  + " frame piece(s) to it. The clear width between the legs ("
                  + (innerR - innerL).ToString("F2") + " m) and the leaf, stripes and card reader were "
                  + "not touched - shrinking the doorway the player walks through is not a fix.");
        foreach (var l in log) Debug.Log("[DoorFit]" + l);
        SaveSceneNow();
    }


    /// <summary>Writes the scene to disk, so a repair pass is never a change that only exists in RAM.</summary>
    ///
    /// <para>Every repair pass here mutates the scene, and until now only <see cref="Build"/> ever
    /// called <c>SaveScene</c> - so running a pass on its own changed the level in memory and threw it
    /// away on exit. That is not a theoretical risk: a <c>FitDoorFrameToOpening</c> run standalone moved
    /// the frame's inner edge and the change was lost when the project was closed. It also means the
    /// menu item and the result on disk could disagree, which is the worst combination for a tool whose
    /// whole purpose is to be trusted.</para>
    ///
    /// <para>So the pass saves itself. The cost is one file write per pass; the benefit is that "I ran
    /// the repair" and "the repair is on disk" are the same statement, which is the only way a repair
    /// can be verified later - by closing the project and looking again.</para>
    ///
    /// <para>Gated on <c>isDirty</c> at first, and that was wrong in a way the log made obvious: run
    /// from inside <see cref="Build"/> it printed "scene unchanged - nothing to write" six times during
    /// a build that had changed the walls, the frame and the arches, because the chain only marks the
    /// scene dirty at its very end. <b>A save that silently declines to save is worse than no save**,
    /// because it reports success. So it always writes, and the log says what it wrote.</para></summary>
    static void SaveSceneNow()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!scene.IsValid()) { Debug.LogWarning("[Save] no active scene to save"); return; }
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        if (UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene))
            Debug.Log("[Save] scene written to " + scene.path);
        else
            Debug.LogError("[Save] SaveScene FAILED - the repair is in memory only and will be lost");
    }

    /// <summary>True for a wall TILE - the object that actually carries the mesh.
    ///
    /// This used to mean "the innermost name match", and that was wrong in a way that quietly
    /// corrupted every measurement built on it. In this scene NOTHING named <c>Wall_Simple_01*</c>
    /// carries a mesh directly: the user's objects are GROUPS - <c>Wall_Simple_01 (15)</c> holds
    /// eight children, each one a real tile - and the kit instances have the same shape. So the
    /// innermost match was the mesh child, the group above it was the accepted match, and
    /// <c>GetComponentInChildren&lt;Renderer&gt;()</c> on a group returns its FIRST child and says
    /// nothing about the other seven. Every number this file derived from a "tile" was therefore a
    /// measurement of one arbitrary child of a group - which is how a 3 m tile came to report a
    /// 12.29 m face, how <see cref="ThickenKitWalls"/> thickened one child in eight, and how the
    /// unstack pass reported fifteen walls "stacked" that were seven separate tiles in a row.
    ///
    /// The rule is now the one that cannot be wrong: a tile is a transform with its OWN
    /// <see cref="Renderer"/>. A group has none, so a group is never a tile, and nothing measures
    /// one by accident again.
    ///
    /// Names are also NOT unique, which bit the same code twice: <c>Wall_Simple_01 (6)</c> and
    /// <c>Wall_Simple_01 (7)</c> each exist TWICE - once at the scene root and once inside
    /// <c>Wall_Simple_01 (15)</c> - and the copies inside the group are the ones standing in the exit
    /// doorway (D-102). So anything acting on a wall matches on geometry or hierarchy, never on name.
    /// </summary>
    static bool IsKitWallTile(Transform tr)
    {
        if (tr == null) return false;
        if (!tr.name.StartsWith(WALL_PREFAB, System.StringComparison.Ordinal)) return false;
        return tr.GetComponent<Renderer>() != null;
    }

    /// <summary>Prints what a wall tile actually IS, so sizing rules are written against the real
    /// hierarchy instead of a guess about it.
    ///
    /// Added because the thickness pass reported tiles landing 2.6 m from a 0.40 m target, and the
    /// only way to tell whether that is a wrong axis or the wrong RENDERER is to see the subtree.
    /// The name is not enough: these tiles arrive as named groups, some carrying their mesh on a
    /// child, and <c>GetComponentInChildren&lt;Renderer&gt;()</c> returns the FIRST renderer in the
    /// subtree, which is not necessarily the wall.
    /// </summary>
    [MenuItem("Tools/Escape Room/What is a wall tile made of")]
    public static void WhatIsAWallTileMadeOf()
    {
        int shown = 0;
        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!IsKitWallTile(tr)) continue;
            shown++;

            var path = tr.name;
            for (var p = tr.parent; p != null; p = p.parent) path = p.name + "/" + path;
            Debug.Log("[Tile] " + path + "  localScale " + tr.localScale.ToString("F3")
                      + "  rotY " + tr.eulerAngles.y.ToString("F0")
                      + "  right " + tr.right.ToString("F2"));

            foreach (var rend in tr.GetComponentsInChildren<Renderer>())
            {
                var mf = rend.GetComponent<MeshFilter>();
                string mesh = mf != null && mf.sharedMesh != null
                    ? mf.sharedMesh.bounds.size.ToString("F3") : "(no mesh)";
                string rpath = rend.name;
                for (var p = rend.transform.parent; p != null && p != tr.parent; p = p.parent)
                    rpath = p.name + "/" + rpath;
                Debug.Log("[Tile]   renderer '" + rpath + "'  mesh " + mesh
                          + "  world " + rend.bounds.size.ToString("F3")
                          + "  localScale " + rend.transform.localScale.ToString("F3"));
            }

            if (shown >= 3) break;
        }
        Debug.Log("[Tile] " + shown + " tile(s) printed (first 3).");
    }

    /// <summary>Scales ONE wall tile so its depth measures <paramref name="target"/> in world
    /// units, and reports how far off it finished.
    ///
    /// The single rule for wall thickness, shared by the pass over the user's tiles
    /// (<see cref="ThickenKitWalls"/>) and by the tiles this builder creates (<see cref="Kit"/>).
    /// It used to be two rules, and the room came out half-thickened: the pass ran before the build
    /// created the new wall tiles, so the user's 25 went to 0.40 m and the 19 fresh ones stayed at
    /// the prefab's 0.25 m - which is the step at every joint that the pass exists to remove.
    ///
    /// The DEPTH axis is read off the mesh, because a kit wall is 3.00 m of face and a fraction of
    /// a metre of depth and that ratio belongs to the mesh - it does not move when the tile is
    /// scaled. Reading it back off the world bounds is not stable: once face and depth come close
    /// the answer can flip, and a rule that flips the axis it is scaling never converges.
    ///
    /// Which LOCAL axis that is, is a fact about the mesh - the kit's is (0.25, 3.00, 3.00), so the
    /// depth is local X. Which WORLD axis it lands on is a separate fact about the tile's yaw: at
    /// rotY 90 the same local X is world Z. The first version used the mesh answer as if it were the
    /// world answer, so every rotY 90 tile - most of the room - was measured across its 3.00 m face,
    /// came out 2.6 m from a 0.40 m target and was left alone while the summary counted it as done.
    /// The worst-miss figure is what gave that away, which is why it is returned and logged.
    ///
    /// The correction is by MEASUREMENT: the factor is a ratio of two measured world lengths and one
    /// local scale changes by exactly that factor, so the result lands on target whatever scales are
    /// inherited above and between the tile and the mesh - which is not knowable from names, because
    /// these tiles carry their mesh on a CHILD rather than on themselves.
    /// </summary>
    /// <returns>false when the tile has no usable mesh to size; <paramref name="why"/> says which.</returns>
    static bool SetWallThickness(Transform tr, float target, out float miss, out string why)
    {
        miss = 0f; why = null;
        var rend = tr.GetComponent<Renderer>();
        if (rend == null) { why = "no renderer"; return false; }

        var mf = rend.GetComponent<MeshFilter>();
        bool depthIsLocalX;
        if (mf != null && mf.sharedMesh != null)
        {
            var ms = mf.sharedMesh.bounds.size;
            depthIsLocalX = ms.x < ms.z;
        }
        else
        {
            // No mesh on the renderer itself, so fall back to the bounds - but only for a tile that
            // is obviously elongated. A near-square is skipped rather than guessed at.
            var b0 = rend.bounds;
            if (b0.size.x <= 0f || b0.size.z <= 0f
                || Mathf.Min(b0.size.x, b0.size.z) / Mathf.Max(b0.size.x, b0.size.z) > 0.5f)
            { why = "not elongated (" + b0.size.x.ToString("F2") + " x " + b0.size.z.ToString("F2") + ")"; return false; }
            depthIsLocalX = b0.size.x < b0.size.z;
        }

        // Does the tile's local X land on world X, or on world Z? From the basis vector, not from
        // the euler angle, so a tile under a rotated group is handled and a tile under a merely
        // scaled group is not mistaken for a 90-degree one.
        var right = tr.right;
        if (Mathf.Abs(right.x) <= 0.5f && Mathf.Abs(right.z) <= 0.5f) { why = "on its side"; return false; }
        bool localXIsWorldX = Mathf.Abs(right.x) > 0.5f;
        bool depthIsWorldX = depthIsLocalX ? localXIsWorldX : !localXIsWorldX;

        for (int step = 0; step < 4; step++)
        {
            var b = rend.bounds;
            float world = depthIsWorldX ? b.size.x : b.size.z;
            if (Mathf.Abs(world - target) < 0.002f) break;
            float factor = target / Mathf.Max(0.0001f, world);
            Vector3 ls = tr.localScale;
            if (depthIsLocalX) ls.x *= factor; else ls.z *= factor;
            tr.localScale = ls;
        }

        var bf = rend.bounds;
        miss = Mathf.Abs((depthIsWorldX ? bf.size.x : bf.size.z) - target);
        return true;
    }

    static void ThickenKitWalls(float targetThickness)
    {
        int done = 0, skipped = 0;
        float worstError = 0f;
        var skippedNames = new List<string>();

        foreach (var tr in Object.FindObjectsByType<Transform>())
        {
            if (!IsKitWallTile(tr)) continue;
            if (!SetWallThickness(tr, targetThickness, out float miss, out string why))
            { skipped++; skippedNames.Add(tr.name + " (" + why + ")"); continue; }
            if (miss > worstError) worstError = miss;
            done++;
        }

        if (skipped > 0)
        {
            // Named, because "2 tiles were skipped" does not tell you whether the room now has a
            // thin patch in the middle of it. A tile left at 0.25 m beside 0.40 m neighbours shows a
            // step at every joint, which is the whole reason this pass exists.
            Debug.LogWarning("[EscapeRoom] " + skipped + " wall tile(s) skipped, left as they were: "
                             + string.Join(", ", skippedNames));
        }
        if (done > 0)
        {
            // The failure flag keys off the miss, not off the iteration count. Running out of
            // iterations at 1 mm is a success - the loop is a ratio correction and it lands wherever
            // the last step can reach - and a flag that fires there trains the reader to ignore the
            // one time a tile really is 2.6 m wide.
            const float TOLERANCE = 0.005f;                     // 5 mm on a 0.40 m wall: 1.2%
            Debug.Log("[EscapeRoom] " + done + " kit wall tile(s) at " + targetThickness.ToString("F2")
                      + " m thick; worst miss " + (worstError * 1000f).ToString("F0") + " mm"
                      + (worstError > TOLERANCE ? "   <== OVER TOLERANCE" : ""));
        }
    }

    /// <summary>The object's path from the scene root, for naming a finding you can then act on.</summary>
    static string PathOf(Transform tr)
    {
        var s = tr.name;
        for (var p = tr.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    /// <summary>True when a collider belongs to a WALL, as opposed to the door standing in one.
    ///
    /// The doorway check used to treat every hit as a defect, and the blast door's own frame, leaf,
    /// bulkhead and card reader - four out of the five things that are SUPPOSED to be in the
    /// opening - counted as failures. A check that reports "blocked" on a correct scene is a check
    /// whose "blocked" nobody reads, and the real defect (D-101, two tiles across the exit) was in
    /// the same line as the door.
    ///
    /// Asked of the collider's OWN name and its parents', because the kit's wall collider sits on a
    /// child mesh that is merely called <c>Wall_Simple_01</c> - so the name is the same thing
    /// <see cref="IsKitWallTile"/> keys off, and reusing that prefix keeps one meaning of "wall".
    /// </summary>
    static bool IsWallCollider(Collider col)
    {
        if (col == null) return false;
        for (var t = col.transform; t != null; t = t.parent)
            if (t.name.StartsWith(WALL_PREFAB, System.StringComparison.Ordinal)) return true;
        return false;
    }

    static Rect BoundsIn(RectTransform rt, Transform space)
    {
        var r = RawBounds(rt, space);

        // A Text rect is a layout box, routinely far larger than the glyphs it draws: a centred
        // full-width title overlaps every corner-anchored label without ever looking like it.
        // Measuring the generated text instead is what makes this check trustworthy - and it is
        // still what caught the real defect, because the button's rect is a real rect.
        var txt = rt.GetComponent<Text>();
        if (txt == null || string.IsNullOrWhiteSpace(txt.text)) return r;
        if (txt.cachedTextGenerator == null || txt.cachedTextGenerator.vertexCount == 0) return r;

        var settings = txt.GetGenerationSettings(Vector2.zero);
        float w = Mathf.Min(txt.cachedTextGenerator.GetPreferredWidth(txt.text, settings), r.width);
        float h = Mathf.Min(txt.cachedTextGenerator.GetPreferredHeight(txt.text, settings), r.height);
        if (w <= 0f || h <= 0f) return r;

        float x = txt.alignment == TextAnchor.UpperLeft || txt.alignment == TextAnchor.MiddleLeft
               || txt.alignment == TextAnchor.LowerLeft ? r.xMin
               : txt.alignment == TextAnchor.UpperRight || txt.alignment == TextAnchor.MiddleRight
               || txt.alignment == TextAnchor.LowerRight ? r.xMax - w
               : r.center.x - w * 0.5f;

        float y = txt.alignment == TextAnchor.LowerLeft || txt.alignment == TextAnchor.LowerCenter
               || txt.alignment == TextAnchor.LowerRight ? r.yMin
               : txt.alignment == TextAnchor.UpperLeft || txt.alignment == TextAnchor.UpperCenter
               || txt.alignment == TextAnchor.UpperRight ? r.yMax - h
               : r.center.y - h * 0.5f;

        return Rect.MinMaxRect(x, y, x + w, y + h);
    }

    static Rect RawBounds(RectTransform rt, Transform space)
    {
        var c = new Vector3[4];
        rt.GetWorldCorners(c);
        for (int i = 0; i < 4; i++) c[i] = space.InverseTransformPoint(c[i]);
        float xMin = Mathf.Min(Mathf.Min(c[0].x, c[1].x), Mathf.Min(c[2].x, c[3].x));
        float xMax = Mathf.Max(Mathf.Max(c[0].x, c[1].x), Mathf.Max(c[2].x, c[3].x));
        float yMin = Mathf.Min(Mathf.Min(c[0].y, c[1].y), Mathf.Min(c[2].y, c[3].y));
        float yMax = Mathf.Max(Mathf.Max(c[0].y, c[1].y), Mathf.Max(c[2].y, c[3].y));
        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    static float Area(Rect r) { return Mathf.Max(0f, r.width) * Mathf.Max(0f, r.height); }

    /// <summary>Asks the question directly: if the crosshair were on the reboot button right now,
    /// would the EventSystem hand the click to it?
    ///
    /// Self Test 1 proves the <c>onClick</c> chain fires. The crosshair diagnostic proves what is
    /// under the *screen centre*. Neither answers the real question, because the terminal is a
    /// world-space canvas seen at an angle and from 10 m away, so screen centre is never the
    /// button. This projects the button's own rect centre to screen space, builds a real
    /// <see cref="PointerEventData"/> there and runs the EventSystem's own <c>RaycastAll</c> - the
    /// identical call <c>InputSystemUIInputModule</c> makes. If the Button shows up in the results,
    /// a real mouse click at that pixel reaches it.
    /// </summary>
    [MenuItem("Tools/Escape Room/Can the reboot button be clicked")]
    public static void CanRebootButtonBeClicked()
    {
        var es = EventSystem.current;
        if (es == null) { Debug.LogError("[ButtonHit] EventSystem.current is NULL"); return; }

        var tc = Object.FindFirstObjectByType<TerminalController>();
        if (tc == null) { Debug.LogError("[ButtonHit] no TerminalController in scene"); return; }
        var btn = tc.rebootButton;
        if (btn == null) { Debug.LogError("[ButtonHit] tc.rebootButton is NULL"); return; }

        var rt = btn.transform as RectTransform;
        if (rt == null) { Debug.LogError("[ButtonHit] button transform is not a RectTransform"); return; }

        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        Vector3 centre = (corners[0] + corners[2]) * 0.5f;
        var cam = btn.targetGraphic != null ? btn.targetGraphic.canvas.worldCamera : null;

        Debug.Log("[ButtonHit] button='" + btn.name + "' active=" + btn.gameObject.activeInHierarchy
                  + " interactable=" + btn.interactable
                  + " worldCentre=" + centre.ToString("F2")
                  + " canvasMode=" + btn.targetGraphic.canvas.renderMode
                  + " worldCamera=" + (cam == null ? "NULL" : cam.name)
                  + " graphic=" + (btn.targetGraphic == null ? "NULL" : btn.targetGraphic.name));

        // A World Space canvas with no worldCamera projects through the fallback camera, which
        // silently produces garbage coordinates instead of failing. That is its own bug, so it is
        // called out rather than allowed to look like a hit-test miss.
        if (btn.targetGraphic.canvas.renderMode == RenderMode.WorldSpace && cam == null)
        {
            Debug.LogError("[ButtonHit] Canvas is World Space but worldCamera is NULL - "
                         + "the GraphicRaycaster will raycast from the wrong place.");
            return;
        }

        Vector2 screen = RectTransformUtility.WorldToScreenPoint(cam, centre);
        Vector3 viewport = cam != null ? cam.WorldToViewportPoint(centre) : Vector3.zero;
        Debug.Log("[ButtonHit] screen point = " + screen.ToString("F1")
                  + "  viewport = " + viewport.ToString("F3")
                  + "  insideScreen = " + (screen.x >= 0 && screen.y >= 0
                                          && screen.x <= Screen.width && screen.y <= Screen.height));

        // On-screen size. A button can be perfectly wired and still be unclickable because it
        // projects to a handful of pixels. Projecting the four corners is the only honest measure.
        Vector2 c0 = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 c2 = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
        float pxW = Mathf.Abs(c2.x - c0.x);
        float pxH = Mathf.Abs(c2.y - c0.y);
        float dist = Vector3.Distance(cam.transform.position, centre);
        float worldW = Vector3.Distance(corners[0], corners[3]);
        Debug.Log("[ButtonHit] game view = " + Screen.width + "x" + Screen.height
                  + "  FOV = " + cam.fieldOfView.ToString("F0")
                  + "  button distance = " + dist.ToString("F2") + " m"
                  + "  button world width = " + worldW.ToString("F2") + " m"
                  + "  => VISUAL ON-SCREEN SIZE = " + pxW.ToString("F1") + " x " + pxH.ToString("F1") + " px"
                  + (pxW < 24 || pxH < 12 ? "   <== visual too small to aim at" : ""));

        // The number that actually decides whether a click lands is the hit target, which is
        // deliberately larger than the art. Report it separately or the visual alone misleads.
        var hit = btn.transform.Find("HitArea_Reboot") as RectTransform;
        if (hit != null)
        {
            var hc = new Vector3[4];
            hit.GetWorldCorners(hc);
            Vector2 h0 = RectTransformUtility.WorldToScreenPoint(cam, hc[0]);
            Vector2 h2 = RectTransformUtility.WorldToScreenPoint(cam, hc[2]);
            Debug.Log("[ButtonHit] HIT PLANE = " + Mathf.Abs(h2.x - h0.x).ToString("F1") + " x "
                      + Mathf.Abs(h2.y - h0.y).ToString("F1") + " px  (same size as the face, "
                      + (Vector3.Distance(hit.position, rt.position) * 100f).ToString("F1")
                      + " cm toward the viewer)");
        }
        else Debug.LogWarning("[ButtonHit] no HitArea_Reboot child - the hit plane was not created");

        // Where a real click would actually land. With a locked cursor the pointer position is
        // not where the player perceives the crosshair to be, so this is the number that matters.
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse != null)
        {
            Vector2 mp = mouse.position.ReadValue();
            Debug.Log("[ButtonHit] Mouse.current.position = " + mp.ToString("F1")
                      + "  screenCentre = (" + (Screen.width * 0.5f).ToString("F0") + ", "
                      + (Screen.height * 0.5f).ToString("F0") + ")"
                      + "  offsetFromCentre = " + (mp - new Vector2(Screen.width * 0.5f, Screen.height * 0.5f)).ToString("F1")
                      + "  lock=" + Cursor.lockState);

            var pedM = new UnityEngine.EventSystems.PointerEventData(es) { position = mp };
            var resM = new List<UnityEngine.EventSystems.RaycastResult>();
            es.RaycastAll(pedM, resM);
            Debug.Log("[ButtonHit] RaycastAll AT THE MOUSE -> " + resM.Count + " hit(s)");
            for (int i = 0; i < resM.Count && i < 3; i++)
                Debug.Log("        " + resM[i].gameObject.name);

            // This is the exact hit-test EsCrosshairPointer performs for a locked cursor.
            bool mouseOffScreen = mp.x < 0f || mp.y < 0f || mp.x > Screen.width || mp.y > Screen.height;
            var pedC = new UnityEngine.EventSystems.PointerEventData(es)
            { position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f) };
            var resC = new List<UnityEngine.EventSystems.RaycastResult>();
            es.RaycastAll(pedC, resC);
            bool centreHits = false;
            for (int i = 0; i < resC.Count; i++)
            {
                var h = UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(resC[i].gameObject);
                if (h != null) { centreHits = true; break; }
            }
            Debug.Log("[ButtonHit] mouse is off-screen = " + mouseOffScreen
                      + "  -> EsCrosshairPointer " + (mouseOffScreen ? "TAKES OVER" : "stands aside")
                      + " | raycast at crosshair -> " + resC.Count + " hit(s), click handler found = " + centreHits);
        }
        else Debug.LogWarning("[ButtonHit] Mouse.current is NULL");

        var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = screen };
        var results = new List<UnityEngine.EventSystems.RaycastResult>();
        es.RaycastAll(ped, results);

        Debug.Log("[ButtonHit] EventSystem.RaycastAll -> " + results.Count + " hit(s)");
        bool found = false;
        for (int i = 0; i < results.Count; i++)
        {
            var go = results[i].gameObject;
            bool isBtn = go == btn.gameObject || go.transform.IsChildOf(btn.transform);
            if (isBtn) found = true;
            Debug.Log("        " + go.name + "  module=" + results[i].module + (isBtn ? "   <== THE BUTTON" : ""));
        }

        if (found)
        {
            Debug.Log("[ButtonHit] RESULT = REACHABLE. A real click at " + screen.ToString("F0")
                      + " hits the button. Distance is the only remaining question.");
        }
        else
        {
            Debug.LogError("[ButtonHit] RESULT = NOT REACHABLE. Nothing under the button's own centre. "
                         + "The GraphicRaycaster is not covering the button, or the canvas has no camera.");
        }
    }

    [MenuItem("Tools/Escape Room/What blocks the EXIT sign")]
    public static void WhatBlocksSign()
    {
        var sign = GameObject.Find("ExitSign");
        if (sign == null) { Debug.LogError("[SignBlock] ExitSign not found"); return; }
        var rt = sign.GetComponent<RectTransform>();
        if (rt == null) { Debug.LogError("[SignBlock] no RectTransform"); return; }
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        Vector3 c = (corners[0] + corners[2]) * 0.5f;

        var eyes = new[] {
            new Vector3(-6.0f, 2.42f, -9.90f),
            new Vector3(-6.0f, 1.70f, -6.00f),
            new Vector3(-9.8f, 1.70f, -6.40f),
        };
        foreach (var e in eyes)
        {
            var dir = (c - e).normalized;
            float dSign = Vector3.Distance(e, c);
            // Collide, not Ignore: a trigger would be invisible to this check, which is how the
            // occluder slipped past the first version of this tool.
            var hits = Physics.RaycastAll(e, dir, dSign + 2.0f, ~0, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            Debug.Log("[SignBlock] eye " + e.ToString("F2") + "  sign d=" + dSign.ToString("F2") + "  hits=" + hits.Length);
            for (int i = 0; i < hits.Length; i++)
            {
                var tr = hits[i].collider.transform;
                var sb = new System.Text.StringBuilder();
                while (tr != null) { sb.Insert(0, tr.name + "/"); tr = tr.parent; }
                string mark = hits[i].distance < dSign - 0.01f ? "   <<< NA FRENTE DA PLACA" : "";
                Debug.Log(string.Format("   {0}. d={1:F2}  {2}{3}", i, hits[i].distance, sb, mark));
            }
        }
    }

    [MenuItem("Tools/Escape Room/Check door props vs opening")]
    public static void CheckDoorProps()
    {
        const float OPEN_L = -7.5f, OPEN_R = -4.5f;   // the north-wall opening
        Debug.Log(string.Format("[DoorProps] opening x in [{0}, {1}]  (width {2})", OPEN_L, OPEN_R, OPEN_R - OPEN_L));

        var archPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(KIT + "Door_Arch_01.prefab");
        if (archPrefab != null)
        {
            var rs = archPrefab.GetComponentsInChildren<Renderer>();
            if (rs.Length > 0)
            {
                var b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                Debug.Log(string.Format("[DoorProps] Door_Arch_01 prefab size {0:F2} x {1:F2} x {2:F2} (placed at 0.97 scale)",
                    b.size.x, b.size.y, b.size.z));
            }
        }

        // Measure the PLACED instance, not the prefab asset: the arch is scaled to 0.97 for a
        // reveal, so reading the asset (3.00 m) reported a false positive on every run.
        foreach (var nm in new[] { "SealPlate", "StatusLamp", "ExitSign", "Door_Arch_01_00" })
        {
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (t.name != nm) continue;
                var rs = t.GetComponentsInChildren<Renderer>();
                if (rs.Length == 0)
                {
                    // UI-only object (canvas): measure the rect of its root instead
                    var crt = t.GetComponent<RectTransform>();
                    if (crt == null) continue;
                    var corners = new Vector3[4];
                    crt.GetWorldCorners(corners);
                    float lo = Mathf.Min(Mathf.Min(corners[0].x, corners[1].x), Mathf.Min(corners[2].x, corners[3].x));
                    float hi = Mathf.Max(Mathf.Max(corners[0].x, corners[1].x), Mathf.Max(corners[2].x, corners[3].x));
                    Debug.Log(string.Format("[DoorProps] {0,-18} x[{1:F2},{2:F2}]   {3}",
                        nm, lo, hi, (lo < OPEN_L - 0.01f || hi > OPEN_R + 0.01f) ? ">>> ATRAVESSA A PAREDE" : "ok"));
                    continue;
                }
                var b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                bool pokes = b.min.x < OPEN_L - 0.01f || b.max.x > OPEN_R + 0.01f;
                Debug.Log(string.Format("[DoorProps] {0,-18} x[{1:F2},{2:F2}] y[{3:F2},{4:F2}] z[{5:F2},{6:F2}]   {7}",
                    nm, b.min.x, b.max.x, b.min.y, b.max.y, b.min.z, b.max.z,
                    pokes ? ">>> ATRAVESSA A PAREDE" : "ok"));
            }
        }
    }

    [MenuItem("Tools/Escape Room/Measure Kenney models")]
    public static void MeasureKenney()
    {
        var guids = AssetDatabase.FindAssets("t:Model", new[] { "Assets/KenneyModularSpace" });
        if (guids.Length == 0) { Debug.LogError("[Measure] no models found"); return; }
        for (int i = 0; i < guids.Length; i++)
        {
            var path = AssetDatabase.GUIDToAssetPath(guids[i]);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) { Debug.Log("[Measure] " + Path.GetFileName(path) + " -> NO RENDERER"); continue; }
            var b = rends[0].bounds;
            for (int k = 1; k < rends.Length; k++) b.Encapsulate(rends[k].bounds);
            Debug.Log(string.Format("[Measure] {0,-32} size {1,6:F2} x {2,6:F2} x {3,6:F2}   pivotY {4,6:F2}",
                                    Path.GetFileName(path), b.size.x, b.size.y, b.size.z, b.min.y));
        }
    }

    [MenuItem("Tools/Escape Room/Self Test 1 - Reboot (invokes the OnClick button)")]
    public static void SelfTest1()
    {
        var tc = Object.FindAnyObjectByType<TerminalController>();
        if (tc == null) { Debug.LogError("[SelfTest] TerminalController not found. Are you in Play Mode?"); return; }
        if (tc.rebootButton == null) { Debug.LogError("[SelfTest] rebootButton reference is NULL - builder wiring broken"); return; }

        Debug.Log("[SelfTest] phase BEFORE          = " + tc.Phase);
        Debug.Log("[SelfTest] button active         = " + tc.rebootButton.gameObject.activeInHierarchy);
        Debug.Log("[SelfTest] persistent listeners = " + tc.rebootButton.onClick.GetPersistentEventCount());

        tc.rebootButton.onClick.Invoke();

        Debug.Log("[SelfTest] phase AFTER Invoke()  = " + tc.Phase + "   (expect Booting)");
        var gm = EscapeGameManager.Instance;
        Debug.Log("[SelfTest] reserve running       = " + (gm != null && gm.IsRunning));
    }

    [MenuItem("Tools/Escape Room/Self Test 2 - Enter PIN 3719 on the safe")]
    public static void SelfTest2()
    {
        // Drives the real safe buttons (play mode: Start wired the listeners).
        // Self-contained: resets first, so running after a solved safe is still valid.
        var pad = Object.FindFirstObjectByType<EsSafeKeypad>();
        if (pad == null) { Debug.LogError("[SelfTest2] no EsSafeKeypad in the scene"); return; }
        if (pad.keypadRoot == null) { Debug.LogError("[SelfTest2] keypadRoot is NULL"); return; }
        pad.DebugReset();

        int allFound = 1;
        foreach (var cap in new[] { "3", "7", "1", "9", "ENTER" })
        {
            bool pressed = false;
            for (int i = 0; i < pad.keypadRoot.transform.childCount; i++)
            {
                var b = pad.keypadRoot.transform.GetChild(i).GetComponent<UnityEngine.UI.Button>();
                var lbl = pad.keypadRoot.transform.GetChild(i).GetComponentInChildren<UnityEngine.UI.Text>();
                if (b != null && lbl != null && lbl.text == cap) { b.onClick.Invoke(); pressed = true; break; }
            }
            if (!pressed) allFound = 0;
            Debug.Log("[SelfTest2] pressed '" + cap + "' -> found=" + pressed);
        }

        Debug.Log("[SelfTest2] VERDICT = " + ((allFound == 1 && pad.solved)
                  ? "PASS (3, 7, 1, 9, ENTER opened the safe)" : "FAIL"));
    }

    [MenuItem("Tools/Escape Room/Self Test 3 - Wrong PIN 1111 (must be refused)")]
    public static void SelfTest3()
    {
        var pad = Object.FindFirstObjectByType<EsSafeKeypad>();
        if (pad == null) { Debug.LogError("[SelfTest3] no EsSafeKeypad in the scene"); return; }
        if (pad.keypadRoot == null) { Debug.LogError("[SelfTest3] keypadRoot is NULL"); return; }
        pad.DebugReset();

        foreach (var cap in new[] { "1", "1", "1", "1", "ENTER" })
        {
            for (int i = 0; i < pad.keypadRoot.transform.childCount; i++)
            {
                var b = pad.keypadRoot.transform.GetChild(i).GetComponent<UnityEngine.UI.Button>();
                var lbl = pad.keypadRoot.transform.GetChild(i).GetComponentInChildren<UnityEngine.UI.Text>();
                if (b != null && lbl != null && lbl.text == cap) { b.onClick.Invoke(); break; }
            }
        }
        bool refused = !pad.solved;
        Debug.Log("[SelfTest3] wrong PIN refused = " + refused + "   "
                  + (refused ? "PASS (safe stays shut)" : "FAIL (wrong PIN opened the safe)"));
    }


    // =====================================================================
    // ROOM RE-IMPROVEMENT
    //
    // Measured defects in the original layout (see DECISOES.md D-38):
    //   * north wall had ONE tile at x=-9 out of the ~7 the 24 m span needs, so a ~19 m
    //     stretch was wide open to the void
    //   * east wall was missing its northernmost tile (z -11.8 .. -8.8)
    //   * the plan read as one 24 x 24 square with nothing breaking it up
    // =====================================================================
    static GameObject Kit(string prefabName, Transform parent, Vector3 localPos, Vector3 localEuler,
                          bool ensureCollider = false)
    {
        var pf = AssetDatabase.LoadAssetAtPath<GameObject>(KIT + prefabName + ".prefab");
        if (pf == null) { Debug.LogError("[EscapeRoom] kit prefab not found: " + prefabName); return null; }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(pf);
        go.name = prefabName + "_" + (parent.childCount.ToString("D2"));
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = Vector3.one;
        // Collider fitted BEFORE the rotation, so bounds are still axis aligned in the
        // instance's own space. Required for Column_01_Top, which ships with NO collider at all:
        // without it the player walks straight through the columns and physics raycasts cannot
        // see them either - which is how a column sat invisibly in front of the EXIT sign for
        // several iterations before the raycast diagnostic existed (D-55).
        if (ensureCollider && go.GetComponentInChildren<Collider>() == null)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                var bc = go.AddComponent<BoxCollider>();
                bc.center = go.transform.InverseTransformPoint(b.center);
                bc.size = new Vector3(b.size.x, b.size.y, b.size.z);
            }
        }
        go.transform.localEulerAngles = localEuler;
        // A tile this builder creates is as much a wall as one of the user's, and it must go
        // through the SAME thickness rule. The pass over the user's tiles runs before this one, so
        // without this the room ends up with the user's walls at 0.40 m and ours at the prefab's
        // 0.25 m - half the run thick and half thin, which is the defect the pass exists to remove.
        // Done AFTER the euler, because the rule reads which world axis the depth lands on.
        if (prefabName == WALL_PREFAB)
            SetWallThickness(go.transform, WALL_THICKNESS, out _, out _);
        return go;
    }

    /// <summary>Full-screen results overlay (victory / defeat). Also carries the restart
    /// OnClick, so the second functional state of the button survives the move off world space.</summary>
    static Canvas BuildEndOverlay(Transform root, out GameObject panel, out Text title,
                                  out Text body, out Button restart)
    {
        var go = new GameObject("EndOverlay", typeof(RectTransform));
        go.transform.SetParent(root, false);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        go.AddComponent<GraphicRaycaster>();

        panel = new GameObject("EndPanel", typeof(RectTransform));
        panel.transform.SetParent(go.transform, false);
        var prt = (RectTransform)panel.transform;
        EsUi.Stretch(prt);
        var bg = EsUi.Img(panel.transform, "Bg", new Color(0.02f, 0.03f, 0.04f, 0.93f));
        EsUi.Stretch(bg.rectTransform);

        title = EsUi.Label(panel.transform, "EndTitle", "", 96, Color.white, TextAnchor.UpperCenter, FontStyle.Bold);
        title.rectTransform.anchorMin = new Vector2(0, 1); title.rectTransform.anchorMax = new Vector2(1, 1);
        title.rectTransform.offsetMin = new Vector2(60, -340); title.rectTransform.offsetMax = new Vector2(-60, -160);

        body = EsUi.Label(panel.transform, "EndBody", "", 52, new Color(0.82f, 0.9f, 0.86f), TextAnchor.UpperCenter);
        body.rectTransform.anchorMin = new Vector2(0, 1); body.rectTransform.anchorMax = new Vector2(1, 1);
        body.rectTransform.offsetMin = new Vector2(140, -700); body.rectTransform.offsetMax = new Vector2(-140, -380);

        restart = EsUi.ButtonWithLabel(panel.transform, "RestartButton", "REINICIAR", 56,
                                       new Color(0.32f, 0.10f, 0.10f, 1f), Color.white);
        var rrt = (RectTransform)restart.transform;
        rrt.anchorMin = rrt.anchorMax = new Vector2(0.5f, 0f);
        rrt.pivot = new Vector2(0.5f, 0f);
        rrt.sizeDelta = new Vector2(820f, 150f);
        rrt.anchoredPosition = new Vector2(0f, 200f);

        panel.SetActive(false);
        return canvas;
    }

    /// <summary>
    /// Opaque backing ring that sits just OUTSIDE every kit wall.
    ///
    /// The kit's wall tiles are ~3 m against a 2.97 m pitch and stop short of the ceiling, so
    /// vertical seams and a wall-top gap let the skybox through - which reads as a window onto
    /// an empty world. Rather than chase each seam by hand, this ring is oversized on purpose:
    /// it is 0.6 m thick, spans y -0.4 .. 4.4 (well past the 3.2-3.4 ceiling) and overlaps the
    /// corners, so any gap in the decorative walls reveals flat black instead of the void.
    /// The north run is split to leave the exit opening clear. See DECISOES.md D-41.
    /// </summary>
    /// <summary>Ceiling clear height. Adaptive: never higher than the kit walls, so raising the
    /// ceiling can never open a gap above them (which the backing shell would have to cover).
    /// The jump is trimmed to match, otherwise the capsule clips the ceiling (D-45).</summary>
    const float CEIL_WANTED = 3.70f;
    const float JUMP_HEIGHT = 0.85f;

    static float _wallTopY = 3.03f, _wallMinX, _wallMaxX, _wallMinZ, _wallMaxZ;
    static int _wallCount;

    /// <summary>Measured: the Barking_Dog walls are only ~3.03 m tall. That is the real reason
    /// the old 3.20 m ceiling had a 17 cm slot above the walls - the "window onto nothing" the
    /// player reported. So the walls get extended upward (BuildUpperWalls) instead of the
    /// ceiling being pushed through them, and the jump is trimmed to match (D-45).</summary>
    static float CeilBottom => CEIL_WANTED;

    /// <summary>Single measurement pass, shared by the ceiling and the backing shell.</summary>
    static void MeasureKitWalls()
    {
        _wallMinX = float.MaxValue; _wallMaxX = float.MinValue;
        _wallMinZ = float.MaxValue; _wallMaxZ = float.MinValue;
        _wallTopY = 0f; _wallCount = 0;
        var existing = GameObject.Find(ROOT);
        var rootT = existing != null ? existing.transform : null;

        foreach (var tr in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (!IsKitWallTile(tr)) continue;   // tiles only; the groups would inflate _wallCount
            if (rootT != null && tr.IsChildOf(rootT)) continue;   // skip our own tiles
            var rend = tr.GetComponent<Renderer>();
            if (rend == null) continue;
            var b = rend.bounds;
            _wallMinX = Mathf.Min(_wallMinX, b.min.x); _wallMaxX = Mathf.Max(_wallMaxX, b.max.x);
            _wallMinZ = Mathf.Min(_wallMinZ, b.min.z); _wallMaxZ = Mathf.Max(_wallMaxZ, b.max.z);
            _wallTopY = Mathf.Max(_wallTopY, b.max.y);
            _wallCount++;
        }
        if (_wallCount == 0) { _wallTopY = 3.2f; Debug.LogWarning("[EscapeRoom] no kit walls measured"); }
        Debug.Log(string.Format("[EscapeRoom] kit walls: {0} pcs, topY {1:F2}, ceiling bottom -> {2:F2} (wanted {3:F2})",
                                 _wallCount, _wallTopY, CeilBottom, CEIL_WANTED));
    }

    /// <summary>Extends the kit walls from their 3.03 m top up to the new ceiling, as a solid
    /// service band. Slightly proud of the wall face on purpose: it reads as a two-tone
    /// industrial wall instead of a filler, and the north run is split for the exit.</summary>
    static void BuildUpperWalls(Transform root)
    {
        if (_wallCount == 0) return;
        var g = new GameObject("UpperWalls");
        g.transform.SetParent(root, false);
        var t = g.transform;

        const float BT = 0.70f;                 // band thickness
        float y0 = _wallTopY - 0.20f;          // overlap the wall top, no seam
        float y1 = CEIL_WANTED + 0.10f;        // overlap the ceiling slab
        float H = y1 - y0, Y = (y0 + y1) * 0.5f;

        float wIn = _wallMinX + BT * 0.5f, wOut = _wallMinX - BT * 0.5f;
        float eIn = _wallMaxX - BT * 0.5f, eOut = _wallMaxX + BT * 0.5f;
        float nIn = _wallMinZ + BT * 0.5f, nOut = _wallMinZ - BT * 0.5f;
        float sIn = _wallMaxZ - BT * 0.5f, sOut = _wallMaxZ + BT * 0.5f;
        float zLen = sIn - nIn, zMid = (nIn + sIn) * 0.5f;

        EsTheme.Box("Band_W", t, new Vector3((wIn + wOut) * 0.5f, Y, zMid), new Vector3(BT, H, zLen), _shell);
        EsTheme.Box("Band_E", t, new Vector3((eIn + eOut) * 0.5f, Y, zMid), new Vector3(BT, H, zLen), _shell);
        EsTheme.Box("Band_S", t, new Vector3(0f, Y, (sIn + sOut) * 0.5f),
                    new Vector3(_wallMaxX - _wallMinX + BT, H, BT), _shell);

        // north, split around the exit opening (x -7.5 .. -4.5)
        const float OPEN_L = -7.5f, OPEN_R = -4.5f;
        float x0 = _wallMinX - BT, x1 = _wallMaxX + BT;
        EsTheme.Box("Band_N_left", t, new Vector3((x0 + OPEN_L) * 0.5f, Y, (nIn + nOut) * 0.5f),
                    new Vector3(OPEN_L - x0, H, BT), _shell);
        EsTheme.Box("Band_N_right", t, new Vector3((OPEN_R + x1) * 0.5f, Y, (nIn + nOut) * 0.5f),
                    new Vector3(x1 - OPEN_R, H, BT), _shell);
        // header over the opening
        EsTheme.Box("Band_N_header", t, new Vector3((OPEN_L + OPEN_R) * 0.5f, Y + H * 0.36f, (nIn + nOut) * 0.5f),
                    new Vector3(OPEN_R - OPEN_L, H * 0.28f, BT), _shell);
    }

    static void BuildBackingShell(Transform root)
    {
        // Perimeter comes from MeasureKitWalls(), which derives it from the walls' real render
        // bounds. Guessing the tile thickness is what made the first attempt cover the walls:
        // the ring has to sit entirely OUTSIDE bounds.min / bounds.max, never inside.
        float minX = _wallMinX, maxX = _wallMaxX;
        float minZ = _wallMinZ, maxZ = _wallMaxZ;

        if (_wallCount == 0)
        {
            Debug.LogWarning("[EscapeRoom] No kit walls found - skipping backing shell.");
            return;
        }

        const float TH = 0.6f;      // ring thickness
        const float PAD = 0.35f;    // clearance so the ring never clips the wall
        // always punch past the ceiling we actually built, not a guessed constant
        float top = Mathf.Max(_wallTopY + 0.6f, CeilBottom + 0.9f);
        float bot = -0.5f;
        float H = top - bot, Y = (top + bot) * 0.5f;

        float wx0 = minX - PAD - TH, wx1 = minX - PAD;      // west ring, entirely outside
        float ex0 = maxX + PAD,       ex1 = maxX + PAD + TH; // east ring
        float nz0 = minZ - PAD - TH, nz1 = minZ - PAD;      // north ring
        float sz0 = maxZ + PAD,       sz1 = maxZ + PAD + TH; // south ring
        float zMid = (nz1 + sz0) * 0.5f, zLen = sz0 - nz1;
        float xMid = (wx1 + ex0) * 0.5f, xLen = ex0 - wx1;

        var g = new GameObject("BackingShell");
        g.transform.SetParent(root, false);
        var t = g.transform;

        EsTheme.Box("Void_W", t, new Vector3((wx0 + wx1) * 0.5f, Y, zMid), new Vector3(TH, H, zLen), _void);
        EsTheme.Box("Void_E", t, new Vector3((ex0 + ex1) * 0.5f, Y, zMid), new Vector3(TH, H, zLen), _void);
        EsTheme.Box("Void_S", t, new Vector3(xMid, Y, (sz0 + sz1) * 0.5f), new Vector3(xLen, H, TH), _void);

        // north run, split around the exit opening so the corridor stays reachable
        const float OPEN_L = -7.5f, OPEN_R = -4.5f;
        EsTheme.Box("Void_N_left", t, new Vector3((wx1 + OPEN_L) * 0.5f, Y, (nz0 + nz1) * 0.5f),
                    new Vector3(OPEN_L - wx1, H, TH), _void);
        EsTheme.Box("Void_N_right", t, new Vector3((OPEN_R + ex0) * 0.5f, Y, (nz0 + nz1) * 0.5f),
                    new Vector3(ex0 - OPEN_R, H, TH), _void);
        // header above the opening, otherwise you see over the door frame into the roof gap
        EsTheme.Box("Void_N_header", t, new Vector3((OPEN_L + OPEN_R) * 0.5f, top - 0.7f, (nz0 + nz1) * 0.5f),
                    new Vector3(OPEN_R - OPEN_L, 1.4f, TH), _void);

        Debug.Log(string.Format("[EscapeRoom] BackingShell fitted to kit walls: x[{0:F2},{1:F2}] z[{2:F2},{3:F2}] " +
                               "topY {4:F2} from {5} wall pieces",
                               minX, maxX, minZ, maxZ, top, _wallCount));
    }

    static void BuildRoomRevamp(Transform root)
    {
        BuildBackingShell(root);
        BuildUpperWalls(root);

        var g = new GameObject("RoomRevamp");
        g.transform.SetParent(root, false);
        var t = g.transform;
        // --- 1. THE NORTH RUN IS BUILT FROM ITS MEASURED GAPS, not from an assumed 3 m grid.
        //
        // It used to be `foreach (var x in new[] { -3, 0, 3, 6, 9, 11.7 })`, which is only correct
        // if the user's own run sits on a 3.00 m grid. It does not. Measured with
        // `Report the gaps in the north wall run`, the whole run is EIGHT tiles and seven of them are
        // OURS: the user's only tile here is `Wall_Simple_01 (15)` at x -3.11..-0.10, and our tile
        // placed at x = -3.00 spans -3.00..0.00 - **2.90 m inside it**. That is the
        // `Wall_Simple_01 (5)` versus `Wall_Simple_01_00` overlap the player named, and it is not a
        // spacing error to nudge: on an ideal grid there is no position that misses.
        //
        // So the run is derived: take the user's tiles as fixed, walk the gaps between them, and
        // fill each gap with something that FITS the gap. A gap of at least one tile gets a kit tile
        // centred in it; a narrower gap gets a box filler of exactly the gap's width; and the exit
        // opening is left alone, because it is a hole on purpose.
        //
        // The result is exact by construction: every filler is sized and placed from the measured
        // gap, so it cannot overlap a neighbour, and a later edit to the user's run makes the next
        // build re-measure rather than inherit a stale assumption.
        BuildNorthRunFromGaps(t);


        // --- 2. east wall was missing its northern tile (existing run starts at z=-8.76)
        Kit("Wall_Simple_01", t, new Vector3(WALL_E, 0f, -10.3f), Vector3.zero);

        // --- 3. NO Door_Arch_01 here. It was a decorative arch prop, 1.50 x 3.75 x 1.00 m, placed
        //        at the opening to "frame" it, and it stood straight across the exit - a 1.50 m
        //        wide obstruction in a 3.00 m opening, visible from the room as a wall and
        //        invisible from the corridor behind its own 1.00 m of depth. Both doorway checks
        //        had been reporting "clear" the whole time, because every one of them filtered on
        //        the name `Wall_Simple_01` and this prefab is called `Door_Arch_01`: a name filter
        //        cannot see what it was not told to look for (D-112).
        //
        //        It was also redundant. The blast door brings its own Frame_T / Frame_L / Frame_R,
        //        and that is the frame you actually walk through, so nothing readable is lost. The
        //        ExitSign's z was chosen to clear the arch's front face at z ~ -10.75 (D-56); with
        //        the arch gone that clearance is simply no longer needed, and the sign is left
        //        where it is rather than moved on the strength of a measurement about an object
        //        that no longer exists.

        // --- 4. interior partition at x=4.5 (runs z -12.5..-6.5). Turns the square into an
        //        L and gives the workbench a storage nook. The 2.5 m gap at z -6.5..-4 is
        //        the doorway into it.
        foreach (var z in new[] { -11.0f, -8.0f })
            Kit("Wall_Simple_01", t, new Vector3(4.5f, 0f, z), Vector3.zero);

        // --- 5. columns: vertical interest and a reason not to read the room as a box.
        //        The NW column sat at x=-5, z=-7.5, directly on the approach to the exit door
        //        (the door occupies x -7.3..-4.7), and it blocked the EXIT sign from the hall.
        //        Moved to x=-3.2: clear of the door and of the terminal (x -2.05..0.05).
        foreach (var p in new[] { new Vector3(-5f, 0f, 3.5f), new Vector3(7.5f, 0f, 3.5f),
                                  new Vector3(-3.2f, 0f, -7.5f), new Vector3(7.5f, 0f, -7.5f) })
            Kit("Column_01_Top", t, p, Vector3.zero, true);   // true = add a collider

        // --- 6. second partition, mirrored: an alcove in the south-east. Together with the
        //        x=4.5 wall the plan reads as two alcoves off a central hall instead of one
        //        24 x 24 square.
        foreach (var x in new[] { 6f, 9f, 11.7f })
            Kit("Wall_Simple_01", t, new Vector3(x, 0f, 5.0f), new Vector3(0f, 90f, 0f));

        BuildExitCorridor(t);
        TameKitLights();
    }

    /// <summary>The Barking_Dog Light_01 prefab is magenta (0.89, 0.21, 1.0) at intensity 1.5.
    /// Six of them sit in the original layout and they wash the whole room purple. They are
    /// the user's objects, so nothing is deleted - they are only dimmed to a real accent, and
    /// they are matched by colour signature rather than by name. Revert by raising the
    /// intensity back to 1.5. See DECISOES.md D-40.</summary>
    static void TameKitLights()
    {
        int n = 0;
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (l.type != LightType.Point) continue;
            bool magenta = l.color.b > 0.85f && l.color.r > 0.55f && l.color.g < 0.5f;
            if (!magenta) continue;
            l.intensity = 0.30f;
            l.range = 5.0f;
            n++;
        }
        if (n > 0) Debug.Log("[EscapeRoom] Tamed " + n + " magenta kit lights (1.5 -> 0.30).");
    }

    /// <summary>Makes every kit material two-sided.
    ///
    /// The kit ships URP/Lit with <c>_Cull = 2</c> (Back) and <c>doubleSidedGI = false</c>, and its
    /// wall meshes are single-sided planes. Inside the room that reads as solid; from the corridor
    /// or the rooftop the same wall vanishes and you look straight through into the void, which is
    /// what "walls are transparent from the other side" means. Flipping Cull to Off costs a few
    /// triangles on a handful of quads and removes the whole class of bug. The alternative -
    /// mirroring a duplicate wall per tile - doubles the draw calls and the collider count for the
    /// same result, so it is rejected.
    ///
    /// <c>doubleSidedGI</c> is set too: without it Unity still bakes lightmaps and probes as if the
    /// surface were one-sided, so the far side of every wall comes out black.
    /// </summary>
    static void MakeKitMaterialsTwoSided()
    {
        const string dir = "Assets/Barking_Dog/3D Free Modular Kit/Meshes/Materials/";
        int changed = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { dir }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) continue;

            bool dirty = false;
            if (m.HasProperty("_Cull") && m.GetFloat("_Cull") != 0f)
            {
                m.SetFloat("_Cull", 0f);
                dirty = true;
            }
            if (!m.doubleSidedGI)
            {
                m.doubleSidedGI = true;
                dirty = true;
            }
            if (!dirty) continue;

            EditorUtility.SetDirty(m);
            changed++;
            Debug.Log("[EscapeRoom] Two-sided: " + m.name + " (_Cull -> Off, doubleSidedGI on)");
        }
        AssetDatabase.SaveAssets();
        if (changed == 0) Debug.LogWarning("[EscapeRoom] No kit material found to make two-sided - check the path.");
    }

    /// <summary>The payoff: a real airlock beyond the blast door, ending in light.</summary>
    static void BuildExitCorridor(Transform root)
    {
        var c = new GameObject("ExitCorridor");
        c.transform.SetParent(root, false);
        c.transform.localPosition = new Vector3(EXIT_X, 0f, 0f);

        // Side walls run along Z and must overlap generously: with only two tiles per side the
        // run stopped at z=-17.6 while the floor reached -18.7, and the skybox showed through
        // the slot beside the end wall.
        //
        // The 1.78 m offset was measured against the OLD 0.25 m tile, so the inner face landed at
        // x = -4.675 - already 17.5 cm inside the 3 m opening. Thickening the tile to 0.40 m (D-103)
        // moved that face to -4.60, and the doorway check started reporting the corridor's own wall
        // as standing in the exit. The fix is here rather than in the check, because the check was
        // right: the wall really was 10 cm inside the opening. 1.98 m puts both inner faces clear of
        // the opening's edges (-4.80 and -8.36 against -4.5 and -7.5) and makes the airlock 40 cm
        // wider, which suits a room you are meant to walk out of.
        foreach (var x in new[] { -1.98f, 1.98f })
            foreach (var z in new[] { -12.6f, -15.5f, -18.4f })
                Kit(WALL_PREFAB, c.transform, new Vector3(x, 0f, z), Vector3.zero);

        var metal = AssetDatabase.LoadAssetAtPath<Material>(MAT_DIR + "/M_Metal.mat");
        var dark  = AssetDatabase.LoadAssetAtPath<Material>(MAT_DIR + "/M_ShellDark.mat");

        // Dropped 4 cm so its top sits at y = -0.04, NOT at 0.00. The user's own Floor_01 run
        // already reaches z = -19.70, i.e. under this whole corridor, and its top is at y = 0.000 -
        // so a corridor floor at exactly 0.00 is a coplanar pair over 8.6 m of the exit route, and
        // coplanar is the one case two floors always fight over. Sitting below it means the user's
        // floor is what you see and this is a fallback in case their run is ever shortened, which
        // is the right way round: it is our geometry standing in for theirs, so it should lose.
        EsTheme.Box("CorridorFloor", c.transform, new Vector3(0f, -0.09f, -15.4f),
                    new Vector3(3.6f, 0.10f, 8.6f), dark);
        // ceiling STOPS at the threshold: past it the roof is open to the sky, which is the
        // whole point of an exit.
        //
        // Its south edge must land exactly on the main Ceiling's north edge (z = -11.90). It used to
        // sit at z -18.7 .. -11.3, so it reached 0.60 m BACK INTO the room and overlapped the main
        // slab in plan - and both undersides were at exactly y = 3.70, which is a coplanar pair and
        // therefore a z-fighting band right where the player looks when they walk out. The
        // undersides being level is what makes the ceiling read as continuous; they just must not
        // also occupy the same patch of floor plan. 3.60 puts its south edge flush at -11.90.
        EsTheme.Box("CorridorCeiling", c.transform, new Vector3(0f, CeilBottom + 0.08f, -15.60f),
                    new Vector3(3.6f, 0.16f, 7.4f), dark);
        // No back wall on purpose: BuildExterior replaces the dead end with a real rooftop
        // (DECISOES.md D-42)

        var gl = new GameObject("ThresholdLight");
        gl.transform.SetParent(c.transform, false);
        gl.transform.localPosition = new Vector3(0f, 2.5f, -19.2f);
        var l = gl.AddComponent<Light>();
        l.type = LightType.Point; l.range = 12f; l.intensity = 2.2f;
        l.color = new Color(0.85f, 0.92f, 1f);

        // ribbed airlock detail so the corridor does not read as an empty box
        for (int i = 0; i < 4; i++)
            EsTheme.Box("Rib" + i, c.transform, new Vector3(0f, 2.72f, -12.4f - i * 1.7f),
                        new Vector3(3.4f, 0.14f, 0.16f), metal);

        // EXIT sign. Two constraints stacked up here:
        //  1) yaw 180 - a world space canvas is read from its local -Z face, and the room is at
        //     z > -11 (D-33);
        //  2) z = -10.55. The Door_Arch's real front face is at z ~ -10.75, NOT -11.25: the
        //     prefab's mesh is offset from its pivot, so its 1.00 m depth does not straddle the
        //     origin. At -11.15 the arch was still nearer than the sign along a ray coming from
        //     the west, and its post was drawn straight over the "I" (D-56). -10.55 clears it.
        var sign = EsUi.WorldCanvas(c.transform, "ExitSign", new Vector3(0f, 2.42f, -10.55f),
                                    new Vector3(0f, 180f, 0f), new Vector2(480f, 150f),
                                    new Color(0.05f, 0.22f, 0.11f, 1f));
        sign.GetComponent<RectTransform>().localScale = Vector3.one * 0.0050f;   // ~2.40 x 0.75 m
        var st = EsUi.Label(sign.transform, "T", "SAÍDA", 96, new Color(0.45f, 1f, 0.62f),
                            TextAnchor.MiddleCenter, FontStyle.Bold);
        EsUi.Stretch(st.rectTransform, 6f);
    }

    // =====================================================================
    // EXTERIOR - the payoff for solving the puzzle
    //
    // Assets: Kenney "Modular Space Kit" (CC0, kenney.nl) + a Poly Haven night HDR (CC0).
    // Both were downloaded into Assets/KenneyModularSpace/. The kit is a single colormap
    // atlas, so everything shares one material and reads as one art style.
    // Measured model sizes (Tools/Escape Room/Measure Kenney models):
    //   gate 4.20x4.62x1.40 | room-large 20x4.25x20 | room-small 12x4.25x12
    //   template-floor-big 8x4.23x8 | template-wall-half 2x4.05x1 | cables 1.91x0.16x2.10
    // See DECISOES.md D-42 / D-43.
    // =====================================================================
    const string KENNEY = "Assets/KenneyModularSpace/";
    // night_bridge was the first pick and it was wrong: a dusk shot full of bare tree
    // branches, which fights the futuristic theme. modern_buildings_night is a lit night
    // skyline of modern towers, which is the actual subject (D-44).
    const string HDR_PATH = KENNEY + "rooftop_night_1k.hdr";

    static GameObject Kenney(string model, Transform parent, Vector3 localPos, Vector3 localEuler,
                             Vector3 localScale, bool collide)
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(KENNEY + model + ".fbx");
        if (src == null) { Debug.LogError("[EscapeRoom] Kenney model missing: " + model); return null; }
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(src);
        inst.name = "KN_" + model;
        inst.transform.SetParent(parent, false);
        // Collider is fitted BEFORE the rotation is applied, so bounds are still axis aligned
        // in the instance's own space and the box lands correctly once it is turned.
        inst.transform.localPosition = localPos;
        inst.transform.localScale = localScale;
        if (collide)
        {
            var rends = inst.GetComponentsInChildren<Renderer>();
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                var bc = inst.AddComponent<BoxCollider>();
                bc.center = inst.transform.InverseTransformPoint(b.center);
                bc.size = new Vector3(b.size.x / localScale.x, b.size.y / localScale.y, b.size.z / localScale.z);
            }
        }
        inst.transform.localEulerAngles = localEuler;
        return inst;
    }

    static void BuildExterior(Transform root)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(KENNEY + "template-floor-big.fbx") == null)
        {
            Debug.LogWarning("[EscapeRoom] Kenney models not found - skipping exterior.");
            return;
        }

        var e = new GameObject("Exterior");
        e.transform.SetParent(root, false);
        e.transform.localPosition = new Vector3(EXIT_X, 0f, 0f);
        var t = e.transform;
        var one = Vector3.one;

        // ---- rooftop deck. The slab is 4.23 thick with its pivot at the base, so dropping it
        //      by 4.23 puts its TOP exactly on y=0, flush with the corridor floor.
        Kenney("template-floor-big", t, new Vector3(0f, -4.23f, -22.0f), Vector3.zero, one, true);
        Kenney("template-floor-big", t, new Vector3(0f, -4.23f, -30.0f), Vector3.zero, one, true);
        Kenney("template-floor-big", t, new Vector3(-8.0f, -4.23f, -26.0f), Vector3.zero, one, true);
        Kenney("template-floor-big", t, new Vector3(8.0f, -4.23f, -26.0f), Vector3.zero, one, true);

        // ---- parapet around the deck edge (2 x 4.05 x 1), so the drop reads as a drop
        foreach (var p in new[] { new Vector3(0f, 0f, -33.6f), new Vector3(-4f, 0f, -33.6f),
                                  new Vector3(4f, 0f, -33.6f), new Vector3(-11.6f, 0f, -26f),
                                  new Vector3(11.6f, 0f, -26f), new Vector3(-11.6f, 0f, -22f),
                                  new Vector3(11.6f, 0f, -22f) })
            Kenney("template-wall-half", t, p, Vector3.zero, one, true);

        // ---- gate landmark across the deck, rotated to face the player coming out
        Kenney("gate", t, new Vector3(0.5f, 0f, -31.5f), new Vector3(0f, 8f, 0f), one, true);
        Kenney("gate-door-window", t, new Vector3(-9.5f, 0f, -29.0f), new Vector3(0f, -64f, 0f), one, true);
        Kenney("stairs", t, new Vector3(9.0f, 0f, -29.5f), new Vector3(0f, 180f, 0f), one, true);

        // ---- cable runs overhead, the detail that sells "facility"
        Kenney("cables", t, new Vector3(-3.5f, 3.4f, -21.0f), new Vector3(0f, 25f, 0f), one, false);
        Kenney("cables", t, new Vector3(3.5f, 3.6f, -24.0f), new Vector3(0f, -15f, 0f), one, false);
        Kenney("cables", t, new Vector3(0f, 3.8f, -28.0f), new Vector3(0f, 70f, 0f), one, false);

        // ---- city below: the room blocks are 20 x 4.25 x 20, so a non-uniform scale turns
        //      them into towers. They top out under the deck, which is what sells the height.
        var towers = new[] {
            new { p = new Vector3(-46f, -30f, -30f), s = new Vector3(2.6f, 7f, 2.6f), r = 12f, m = "room-large" },
            new { p = new Vector3( 40f, -34f, -38f), s = new Vector3(2.2f, 8f, 2.2f), r = -25f, m = "room-large" },
            new { p = new Vector3( 12f, -28f, -78f), s = new Vector3(3.0f, 6f, 3.0f), r = 40f, m = "room-large" },
            new { p = new Vector3(-70f, -32f, -66f), s = new Vector3(2.4f, 7f, 2.4f), r = 5f,  m = "room-large" },
            new { p = new Vector3( 66f, -30f, -80f), s = new Vector3(2.0f, 6f, 2.0f), r = -8f, m = "room-small" },
            new { p = new Vector3(-18f, -30f, -96f), s = new Vector3(2.8f, 8f, 2.8f), r = 22f, m = "room-large" },
            new { p = new Vector3( 84f, -33f,  10f), s = new Vector3(2.2f, 7f, 2.2f), r = 60f, m = "room-large" },
            new { p = new Vector3(-88f, -31f,  18f), s = new Vector3(2.0f, 6f, 2.0f), r = -50f, m = "room-small" },
        };
        foreach (var tw in towers)
            Kenney(tw.m, t, tw.p, new Vector3(0f, tw.r, 0f), tw.s, false);

        // ground far below so the towers are not floating in the void
        EsTheme.Box("CityGround", t, new Vector3(0f, -40f, -40f), new Vector3(520f, 4f, 520f), _shellDark);

        // ---- city glow spilling onto the deck, warm and low
        foreach (var lp in new[] { new Vector3(-14f, 2.0f, -28f), new Vector3(16f, 2.4f, -24f),
                                   new Vector3(0f, 3.0f, -31f) })
        {
            var g = new GameObject("CityGlow");
            g.transform.SetParent(t, false);
            g.transform.localPosition = lp;
            var li = g.AddComponent<Light>();
            li.type = LightType.Point; li.range = 34f; li.intensity = 3.2f;
            li.color = new Color(1f, 0.72f, 0.42f);
        }

        SetupSky();
        EnableMoonlight();
    }

    /// <summary>Panoramic skybox from the CC0 night HDRI. Ambient stays on the dark Trilight
    /// values on purpose: switching it to Skybox would flood the sealed interior with night
    /// light and undo the whole lighting pass (D-43).</summary>
    static void SetupSky()
    {
        if (!File.Exists(HDR_PATH)) { Debug.LogWarning("[EscapeRoom] HDRI missing: " + HDR_PATH); return; }
        var imp = AssetImporter.GetAtPath(HDR_PATH) as TextureImporter;
        if (imp != null && imp.textureType != TextureImporterType.Default)
        {
            imp.textureType = TextureImporterType.Default;   // equirect, not cubemap
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
        }
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(HDR_PATH);
        if (tex == null) { Debug.LogWarning("[EscapeRoom] HDRI not importable as Texture2D"); return; }

        var sh = Shader.Find("Skybox/Panoramic");
        if (sh == null) { Debug.LogWarning("[EscapeRoom] Skybox/Panoramic missing"); return; }
        var m = new Material(sh) { name = "M_ExteriorSky" };
        m.SetTexture("_MainTex", tex);
        m.SetFloat("_Exposure", 0.38f);
        m.SetFloat("_Rotation", 185f);
        var path = VFX_DIR + "/M_ExteriorSky.mat";
        AssetDatabase.CreateAsset(m, path);
        RenderSettings.skybox = AssetDatabase.LoadAssetAtPath<Material>(path);
        Debug.Log("[EscapeRoom] Skybox set from " + Path.GetFileName(HDR_PATH) + " (CC0 Poly Haven).");
    }

    /// <summary>Moonlight has to be shadowed, otherwise it passes straight through the backing
    /// shell and relights the sealed interior. Shadow distance is raised to cover the towers.</summary>
    static void EnableMoonlight()
    {
        Light sun = null;
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.type == LightType.Directional) { sun = l; break; }
        if (sun == null) return;
        sun.intensity = 1.35f;
        sun.color = new Color(0.62f, 0.72f, 0.98f);
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.85f;
        QualitySettings.shadowDistance = 220f;
        QualitySettings.shadowCascades = 4;
    }

    // =====================================================================
    // assets
    // =====================================================================
    static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/EscapeRoom")) AssetDatabase.CreateFolder("Assets", "EscapeRoom");
        foreach (var f in new[] { MAT_DIR, VFX_DIR })
            if (!AssetDatabase.IsValidFolder(f))
                AssetDatabase.CreateFolder("Assets/EscapeRoom", f.Substring("Assets/EscapeRoom/".Length));
        AssetDatabase.SaveAssets();
    }

    static Material Mat(string name, Color c, float metallic, float smooth, Color emission, float ei)
    {
        string path = MAT_DIR + "/" + name + ".mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            m = new Material(sh);
            AssetDatabase.CreateAsset(m, path);
        }
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (emission.maxColorComponent > 0f)
        {
            m.EnableKeyword("_EMISSION");
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", emission * ei);
        }
        EditorUtility.SetDirty(m);
        return m;
    }

    static void BuildMaterials()
    {
        _shell    = Mat("M_Shell",    EsTheme.Shell,    0.55f, 0.35f, Color.black, 0f);
        _shellDark= Mat("M_ShellDark",EsTheme.ShellDark,0.35f, 0.20f, Color.black, 0f);
        _metal    = Mat("M_Metal",    EsTheme.Metal,    0.85f, 0.60f, Color.black, 0f);
        // The kit's own wall material, so an additive wall piece is indistinguishable from a kit tile.
        // Loaded as an ASSET and not recreated: the kit is a single atlas shared by every model, and a
        // runtime-created copy of it would not survive the saved scene (D-41's reasoning).
        _kitWall   = AssetDatabase.LoadAssetAtPath<Material>(
                         "Assets/Barking_Dog/3D Free Modular Kit/Meshes/Materials/Diffuse_01.mat");
        if (_kitWall == null)
            Debug.LogWarning("[EscapeRoom] the kit wall material (Diffuse_01) was not found - gap "
                             + "fillers will render untextured.");

        _gold     = Mat("M_Gold",     EsTheme.KeyGold,  0.75f, 0.65f, EsTheme.KeyGold, 0.35f);
        _glowOn   = Mat("M_GlowOn",   EsTheme.ScreenOn, 0.00f, 0.50f, EsTheme.ScreenOn, 2.20f);
        _alarm    = Mat("M_Alarm",    new Color(1,1,1), 0.00f, 0.40f, Color.black,   0f);
        _paper    = Mat("M_Paper",    new Color32(0xD8,0xD2,0xC0,0xFF), 0f, 0.10f, Color.black, 0f);
        // exit glow as a real asset: a runtime-created material would not persist in the
        // saved scene and would render magenta after a reload
        _exitGlow = Mat("M_ExitGlow", new Color(1f, 0.95f, 0.85f), 0f, 0.40f, new Color(1f, 0.93f, 0.80f), 3.4f);
        // near-black, fully rough: any sliver of it that shows through a seam must read as
        // shadow, never as sky (D-41)
        _void     = Mat("M_VoidBlock", new Color(0.012f, 0.014f, 0.018f), 0f, 0f, Color.black, 0f);
        // --- dressing palette (Filial 9 ambience pass)
        _carpet   = Mat("M_Carpet",   new Color32(0x23,0x2A,0x3A,0xFF), 0f, 0.95f, Color.black, 0f);
        _belt     = Mat("M_Belt",     new Color32(0x1E,0x2A,0x52,0xFF), 0f, 0.90f, Color.black, 0f);
        _screenOff= Mat("M_ScreenOff",new Color32(0x05,0x07,0x0A,0xFF), 0.20f, 0.60f, Color.black, 0f);
        _panelGlow= Mat("M_PanelGlow",new Color(0.85f, 0.95f, 1f), 0f, 0.40f, new Color(0.75f, 0.90f, 1f), 2.4f);
        _cabRed   = Mat("M_CabRed",   new Color32(0x8C,0x1A,0x12,0xFF), 0.30f, 0.45f, Color.black, 0f);
        // Exposed fibre-optic runs (A.E.G.I.S. Fase 0): dark sheath, red emergency glow.
        _fiber    = Mat("M_Fiber",    new Color32(0x1A,0x08,0x08,0xFF), 0f, 0.40f, new Color(1f, 0.20f, 0.15f), 2.0f);
        // Hex mesh puzzle (Fase 1): dark tile, dim-red dead ports, cyan live ports,
        // amber emitter/receptor bodies. Assets, never runtime (D-23/D-49).
        _hexBase  = Mat("M_HexBase",  new Color32(0x16,0x1A,0x1F,0xFF), 0.60f, 0.35f, Color.black, 0f);
        _hexDim   = Mat("M_HexDim",   new Color32(0x3A,0x0E,0x0C,0xFF), 0f, 0.50f, new Color(0.85f, 0.12f, 0.12f), 0.2f);
        _hexLive  = Mat("M_HexLive",  EsTheme.CardCyan, 0f, 0.50f, EsTheme.CardCyan, 2.0f);
        _hexSrc   = Mat("M_HexSrc",   new Color32(0x4A,0x32,0x0E,0xFF), 0.30f, 0.40f, EsTheme.KeyAccent, 1.2f);
        // Live tile body (Fase 1b readability): the whole hex glows so the route
        // reads as one shape instead of scattered pips.
        _hexLiveTile = Mat("M_HexLiveTile", new Color32(0x0A,0x2A,0x30,0xFF), 0f, 0.50f, EsTheme.CardCyan, 1.2f);
        // Single-pip leaves (D-183): slate-teal bodies so the ends-to-plug read
        // before anything lights; teal-cyan when live, distinct from route cyan.
        _hexLeaf = Mat("M_HexLeaf", new Color32(0x1E,0x2E,0x3A,0xFF), 0.30f, 0.40f, new Color(0.10f, 0.35f, 0.45f), 0.5f);
        _hexLeafLive = Mat("M_HexLeafLive", new Color32(0x0C,0x3A,0x40,0xFF), 0f, 0.50f, new Color(0.15f, 0.85f, 0.80f), 1.6f);
        _frost    = Mat("M_Frost",    new Color(0.55f, 0.85f, 0.95f, 0.45f), 0.10f, 0.60f,
                        new Color(0.30f, 0.70f, 0.90f), 0.35f);
        // transparent URP/Lit, guarded like everything else (D-23): without this the
        // alpha in the base colour is ignored and the "ice" renders as solid plastic.
        if (_frost.HasProperty("_Surface")) _frost.SetFloat("_Surface", 1f);
        if (_frost.HasProperty("_Blend")) _frost.SetFloat("_Blend", 0f);
        if (_frost.HasProperty("_SrcBlend")) _frost.SetFloat("_SrcBlend", 1f);
        if (_frost.HasProperty("_DstBlend")) _frost.SetFloat("_DstBlend", 10f);
        if (_frost.HasProperty("_ZWrite")) _frost.SetFloat("_ZWrite", 0f);
        _frost.SetOverrideTag("RenderType", "Transparent");
        _frost.renderQueue = 3000;
        if (_frost.HasProperty("_Surface")) _frost.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        AssetDatabase.SaveAssets();
    }

    static AudioClip Clip(string n) => AssetDatabase.LoadAssetAtPath<AudioClip>(AUDIO + n + ".wav");

    // =====================================================================
    // shell: ceiling + the missing southern floor strip
    // =====================================================================
    static void BuildShell(Transform root)
    {
        var g = new GameObject("Shell");
        g.transform.SetParent(root, false);

        // Floor filler for the strip z in [8.9 .. 12.0], where the user's kit floor stops.
        //
        // It used to BUTT against the kit floor at exactly z = 8.9, with both top faces at
        // exactly y = 0.00. Two coplanar surfaces meeting along a line is a z-fighting seam, and
        // any sub-millimetre difference in the kit floor's real position turns it into a visible
        // flicker strip. Fixed the standard way instead: overlap generously and sit slightly LOWER,
        // so the kit floor is unambiguously the one on top and no two faces are ever coplanar.
        // z 8.55 .. 12.00 (0.35 m under the kit floor), y -0.21 .. -0.01 (1 cm below its surface).
        EsTheme.Box("FloorFiller_South", g.transform,
            new Vector3(1.35f, -0.11f, 10.275f), new Vector3(21.0f, 0.20f, 3.45f), _shellDark);

        // Ceiling raised to the measured clear height. Its BOTTOM sits on CeilBottom, so the
        // slab centre is half a thickness above that. Trigger, so it can never launch the player.
        float cb = CeilBottom;
        var ceil = EsTheme.Box("Ceiling", g.transform,
            new Vector3(0.1f, cb + 0.10f, 0.2f), new Vector3(24.4f, 0.20f, 24.2f), _shellDark);
        var bc = ceil.GetComponent<BoxCollider>();
        if (bc != null) bc.isTrigger = true;
    }

    // =====================================================================
    // terminal + locker + world space canvas
    // =====================================================================
    static TerminalController BuildTerminal(Transform root)
    {
        var t = new GameObject("Terminal");
        t.transform.SetParent(root, false);
        t.transform.position = new Vector3(-1.0f, 0f, ROW);

        // body
        EsTheme.Box("Body", t.transform, new Vector3(0f, 0.95f, -0.25f), new Vector3(2.10f, 1.90f, 0.55f), _shell);
        EsTheme.Box("Kick", t.transform, new Vector3(0f, 0.06f, -0.25f), new Vector3(2.10f, 0.12f, 0.50f), _shellDark);
        EsTheme.Box("Canopy", t.transform, new Vector3(0f, 1.95f, -0.10f), new Vector3(2.20f, 0.12f, 0.80f), _metal);

        // screen surface (glow driven by the controller)
        var screen = EsTheme.Box("ScreenPanel", t.transform, new Vector3(0f, 1.40f, 0.04f),
                                 new Vector3(1.72f, 1.00f, 0.05f), _glowOn);

        var lightGo = new GameObject("ScreenLight");
        lightGo.transform.SetParent(t.transform, false);
        lightGo.transform.localPosition = new Vector3(0f, 1.38f, 0.55f);
        var pl = lightGo.AddComponent<Light>();
        pl.type = LightType.Point;
        // Range 4.2 and a peak of 1.35, down from 7 and 2.6. Measured by snapshotting every light
        // before and after a reboot: the screen light was the ONLY thing that changed (ambient,
        // fog and all 14 other lights were byte-identical), which is what "the room gets brighter
        // when I restart" was. It is the terminal's own screen, so it should read on the terminal
        // and on the player's face - not lift the whole room (D-113).
        pl.range = 4.2f;
        pl.intensity = 0f;                 // starts dark; ForceReboot lights it
        // Cyan, not the screen material's green. The cell is CellGreen and a permanently-lit green
        // glow in the middle of the room is a cue for the wrong object; the terminal's own UI is
        // cyan throughout, so the light matches the thing it is supposedly coming from.
        pl.color = EsTheme.CardCyan;

        // ---- world space canvas on the screen
        var canvasGo = new GameObject("TerminalCanvas", typeof(RectTransform));
        canvasGo.transform.SetParent(t.transform, false);
        canvasGo.transform.localPosition = new Vector3(0f, 1.40f, 0.075f);
        // A world space Canvas is read correctly when its local -Z points at the viewer.
        // The room is at z > -10.6, so the canvas must be yawed 180 deg; at 0 the text
        // renders mirrored. localPosition is unaffected by the yaw.
        canvasGo.transform.localEulerAngles = new Vector3(0f, 180f, 0f);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var crt = (RectTransform)canvasGo.transform;
        crt.sizeDelta = new Vector2(1920f, 1080f);
        crt.localScale = Vector3.one * 0.00090f;      // -> 1.73 x 0.97 world units
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 1f;
        scaler.referencePixelsPerUnit = 100f;
        canvasGo.AddComponent<GraphicRaycaster>();

        var bg = EsUi.Img(canvasGo.transform, "Bg", EsTheme.ScreenBg);
        EsUi.Stretch(bg.rectTransform);

        var title = EsUi.Label(canvasGo.transform, "TitleText", "NO-ZERO // TERMINAL A.E.G.I.S.",
                              54, EsTheme.ScreenOn * 0.75f, TextAnchor.UpperCenter, FontStyle.Bold);
        title.rectTransform.anchorMin = new Vector2(0, 1); title.rectTransform.anchorMax = new Vector2(1, 1);
        title.rectTransform.offsetMin = new Vector2(0, -130); title.rectTransform.offsetMax = new Vector2(0, -30);

        var status = EsUi.Label(canvasGo.transform, "StatusText", "QUARENTENA ABSOLUTA - SISTEMAS OFFLINE",
                                62, EsTheme.Warn, TextAnchor.MiddleCenter, FontStyle.Bold);
        status.rectTransform.anchorMin = new Vector2(0, 1); status.rectTransform.anchorMax = new Vector2(1, 1);
        status.rectTransform.offsetMin = new Vector2(40, -290); status.rectTransform.offsetMax = new Vector2(-40, -160);

        var timer = EsUi.Label(canvasGo.transform, "TimerText", "", 58, EsTheme.Amber,
                               TextAnchor.UpperRight, FontStyle.Bold);
        timer.rectTransform.anchorMin = new Vector2(1, 1); timer.rectTransform.anchorMax = new Vector2(1, 1);
        timer.rectTransform.pivot = new Vector2(1, 1);
        timer.rectTransform.offsetMin = new Vector2(-300, -125); timer.rectTransform.offsetMax = new Vector2(-20, -35);

        // ---- THE required OnClick button
        // Height matters more than it looks. At the 11 m the player spawns from, and measured in
        // a 659x272 docked Game view, the original 1000x150 canvas units projected to 30.6 x 4.6
        // pixels - a 4 px tall target is not clickable no matter how correct the wiring is.
        //
        // Placement is constrained, so it is derived rather than eyeballed. On this 1920x1080
        // canvas, measured from the bottom: StatusText occupies 790..920, and the hint
        // text (500..570) only appears once the button is already hidden, so it is not
        // a constraint. That
        // leaves a 310 unit band (480..790) and a 240 unit button centred in it clears both
        // neighbours by 35 units. An earlier 280 unit button centred at y=180 spanned 580..860
        // and ate 70 units into StatusText, which is the "SISTEMA OFFLINE is overlapping
        // FORCAR REINICIO" report. 240 units still gives ~39 px of height at 2 m.
        var reboot = EsUi.ButtonWithLabel(canvasGo.transform, "RebootButton", "FORCAR REINICIO",
                                         52, new Color(0.10f, 0.35f, 0.28f, 1f), EsTheme.ScreenOn);
        var rrt2 = reboot.GetComponent<RectTransform>();
        rrt2.anchorMin = new Vector2(0.5f, 0.5f);
        rrt2.anchorMax = new Vector2(0.5f, 0.5f);
        rrt2.pivot = new Vector2(0.5f, 0.5f);
        rrt2.anchoredPosition = new Vector2(0f, 95f);      // centre at canvas y 635
        rrt2.sizeDelta = new Vector2(1000f, 240f);        // spans 515..755, inside 480..790

        // Hit plane: the same 1000x240 the player sees, lifted 2.5 cm toward the viewer so it is a
        // genuinely nearer element rather than a coplanar sibling in a hierarchy tie-break. The
        // target is NOT oversized - see AddHitTarget for why that was reverted.
        AddHitTarget(rrt2, 0.025f, "_Reboot");

        // Aim feedback, driven from EsCrosshairPointer's own search rather than from
        // IPointerEnterHandler - see EsHoverHighlight for why the usual mechanism would lie here.
        if (rrt2.GetComponent<EsHoverHighlight>() == null) rrt2.gameObject.AddComponent<EsHoverHighlight>();

        var hint = EsUi.Label(canvasGo.transform, "HintText", "", 44, EsTheme.Amber, TextAnchor.LowerCenter);
        hint.rectTransform.anchorMin = new Vector2(0, 0); hint.rectTransform.anchorMax = new Vector2(1, 0);
        hint.rectTransform.offsetMin = new Vector2(30f, 725f); hint.rectTransform.offsetMax = new Vector2(-30f, 775f);

        // ---- end panel lives on a SCREEN SPACE overlay, not on the terminal canvas.
        //      Win() fires while the player is standing at the blast door, so a world space
        //      panel 11 m away on the terminal would be invisible exactly when it matters.
        Canvas endOverlay = BuildEndOverlay(root, out GameObject endPanel, out Text endTitle,
                                            out Text endBody, out Button restart);

        // ---- controller: reboot, timer and the objective line only. The old
        // credential/key/cell/card chain is gone (D-190): the three puzzles
        // own their state (hex, log sort, safe PIN, plate reader).
        var tc = t.AddComponent<TerminalController>();
        tc.screenLight = pl;
        tc.screenPanel = screen.GetComponent<Renderer>();
        tc.titleText = title;
        tc.statusText = status;
        tc.timerText = timer;
        tc.rebootButton = reboot;
        tc.hintText = hint;
        tc.endPanel = endPanel;
        tc.endTitle = endTitle;
        tc.endBody = endBody;
        tc.restartButton = restart;
        tc.clickClip = Clip("ui_click");
        tc.powerUpClip = Clip("power_up");
        tc.alarmClip = Clip("alarm_beep");
        return tc;
    }

    static ItemSocket MakeSocket(Transform parent, string name, Vector3 pos, string key, string label, Material mat)
    {
        var go = EsTheme.Box(name, parent, pos, new Vector3(0.44f, 0.30f, 0.16f), mat);
        var s = go.AddComponent<ItemSocket>();
        s.acceptsKey = key;
        s.acceptsLabel = label;
        s.indicator = go.GetComponent<Renderer>();
        var seat = new GameObject("Seat");
        seat.transform.SetParent(go.transform, false);
        s.seat = seat.transform;
        s.stowParent = parent;
        return s;
    }


    /// <summary>Wraps a visual-only GameObject in an interactable: one collider fitted to the
    /// combined bounds of all its meshes, one rigidbody, one GrabbableItem.
    ///
    /// The collider is fitted to the RENDERED bounds rather than assumed from a size parameter,
    /// which is the whole point of multi-part models - a box of guessed dimensions
    /// would be wrong for a plate with a frame on it, and D-55 already paid for guessing colliders once
    /// when the kit's Column_01_Top shipped without one and the player walked through it.
    /// </summary>
    static GrabbableItem FinishItem(GameObject visual, Transform parent, string name, Vector3 pos,
                                    string key, string label, float mass)
    {
        visual.transform.SetParent(parent, false);
        visual.transform.localPosition = pos;

        var b = new Bounds();
        bool first = true;
        foreach (var rend in visual.GetComponentsInChildren<Renderer>())
        {
            if (!first) { b.Encapsulate(rend.bounds); }
            else { b = rend.bounds; first = false; }
        }

        var box = visual.AddComponent<BoxCollider>();
        box.center = visual.transform.InverseTransformPoint(b.center);
        Vector3 size = b.size;
        // a per-axis scale on the visual would make a world-space box collider wrong, so the
        // collider is built in local units: divide out the lossy scale
        Vector3 lossy = visual.transform.lossyScale;
        box.size = new Vector3(size.x / Mathf.Abs(lossy.x), size.y / Mathf.Abs(lossy.y), size.z / Mathf.Abs(lossy.z));

        var rb = visual.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        var it = visual.AddComponent<GrabbableItem>();
        it.itemKey = key;
        it.displayName = label;
        return it;
    }

    // =====================================================================
    // blast door
    // =====================================================================
    static BlastDoor BuildBlastDoor(Transform root)
    {
        var d = new GameObject("BlastDoor");
        d.transform.SetParent(root, false);
        // sits IN the north-wall opening, so opening it actually leads somewhere
        d.transform.position = new Vector3(EXIT_X, 0f, -11.45f);

        EsTheme.Box("Frame_L", d.transform, new Vector3(-1.15f, 1.35f, 0f), new Vector3(0.30f, 2.70f, 0.50f), _metal);
        EsTheme.Box("Frame_R", d.transform, new Vector3(1.15f, 1.35f, 0f), new Vector3(0.30f, 2.70f, 0.50f), _metal);
        EsTheme.Box("Frame_T", d.transform, new Vector3(0f, 2.60f, 0f), new Vector3(2.60f, 0.30f, 0.50f), _metal);
        EsTheme.Box("Bulkhead", d.transform, new Vector3(0f, 1.35f, -0.30f), new Vector3(2.60f, 2.70f, 0.12f), _shellDark);

        var hinge = new GameObject("DoorPivot");
        hinge.transform.SetParent(d.transform, false);
        hinge.transform.localPosition = new Vector3(-1.00f, 0f, 0f);
        var leaf = EsTheme.Box("DoorLeaf", hinge.transform, new Vector3(1.00f, 1.35f, 0f),
                               new Vector3(2.00f, 2.70f, 0.14f), _shell);
        // hazard stripes
        for (int i = 0; i < 4; i++)
            EsTheme.Box("Stripe" + i, hinge.transform, new Vector3(0.30f + i * 0.45f, 0.30f, 0.09f),
                        new Vector3(0.22f, 0.55f, 0.02f), i % 2 == 0 ? _gold : _shellDark);

        var lampGo = EsTheme.Box("StatusLamp", d.transform, new Vector3(0f, 2.30f, 0.10f),
                                 new Vector3(0.60f, 0.10f, 0.06f), _alarm);

        // Seal plate where the card reader used to sit: the door now opens
        // only on the optical key (D-190), so there is no slot anymore.
        EsTheme.Box("SealPlate", d.transform, new Vector3(1.02f, 1.15f, 0.30f),
                    new Vector3(0.36f, 0.55f, 0.06f), _metal);

        var bd = d.AddComponent<BlastDoor>();
        bd.doorLeaf = hinge.transform;
        bd.statusLamp = lampGo.GetComponent<Renderer>();
        bd.servoClip = Clip("servo_door");
        bd.lockedClip = Clip("error_buzz");
        return bd;
    }

    // =====================================================================
    // wall notes (the puzzle inputs)
    // =====================================================================
    static void BuildNotes(Transform root)
    {
        // ---- the shift board on the west wall: this is puzzle input #1
        // NOTE the -90 yaw. A world space canvas shows its face toward local -Z as seen from
        // +Z, so a board hung on the west wall (x = -11.8) must be yawed -90 to read
        // correctly from inside the room. +90 renders the text mirrored.
        var board = EsUi.WorldCanvas(root, "MuralPlantao09",
            new Vector3(WALL_W + 0.20f, 1.75f, -3.0f), new Vector3(0f, -90f, 0f),
            new Vector2(1024f, 740f), new Color32(0x14, 0x2A, 0x33, 0xFF));
        board.GetComponent<RectTransform>().localScale = Vector3.one * 0.0019f;   // ~1.95 x 1.41 m

        var bTitle = EsUi.Label(board.transform, "T", "PROTOCOLO NO-ZERO", 68, new Color(0.55f, 0.92f, 0.80f),
                                TextAnchor.UpperCenter, FontStyle.Bold);
        bTitle.rectTransform.anchorMin = new Vector2(0, 1); bTitle.rectTransform.anchorMax = new Vector2(1, 1);
        bTitle.rectTransform.offsetMin = new Vector2(20, -135); bTitle.rectTransform.offsetMax = new Vector2(-20, -25);

        // No column alignment: the legacy dynamic font is proportional, so padded columns
        // collapse into each other. Stacked blocks read cleanly at any resolution.
        //
        // 11 lines at 36 pt * 1.15 = ~456 px against ~560 px of usable height. The old 46 pt
        // needed ~583 px and spilled out the bottom of the board - EsUi sets
        // verticalOverflow = Overflow on purpose, so a puzzle clue is never silently clipped,
        // which also means overflow is VISIBLE rather than harmless. See D-50.
        var bBody = EsUi.Label(board.transform, "B",
            "2142 - NO-ZERO\n     PESQUISA CRIPTOGRAFICA\n\n" +
            "A.E.G.I.S. VIU UM FANTASMA\n     E SELOU TUDO ......... 60:00\n\n" +
            "O REATOR SO REINICIA\nA MAO, EM 3 PASSOS:\nENERGIA, LOGS E CHAVE.",
            36, new Color(0.88f, 0.94f, 0.91f), TextAnchor.UpperLeft);
        bBody.rectTransform.anchorMin = Vector2.zero; bBody.rectTransform.anchorMax = Vector2.one;
        bBody.rectTransform.offsetMin = new Vector2(55, 30); bBody.rectTransform.offsetMax = new Vector2(-55, -150);

        // ---- maintenance log next to it: puzzle input #2
        var log = EsUi.WorldCanvas(root, "RegistroManutencao",
            new Vector3(WALL_W + 0.20f, 1.55f, 0.4f), new Vector3(0f, -84f, -5f),
            new Vector2(900f, 640f), new Color32(0x1A, 0x1C, 0x18, 0xFF));
        log.GetComponent<RectTransform>().localScale = Vector3.one * 0.0018f;   // ~1.62 x 1.15 m

        var lTitle = EsUi.Label(log.transform, "T", "CADERNO DO TECNICO", 44,
                                new Color(0.95f, 0.80f, 0.45f), TextAnchor.UpperCenter, FontStyle.Bold);
        lTitle.rectTransform.anchorMin = new Vector2(0, 1); lTitle.rectTransform.anchorMax = new Vector2(1, 1);
        lTitle.rectTransform.offsetMin = new Vector2(15, -100); lTitle.rectTransform.offsetMax = new Vector2(-15, -25);

        var lBody = EsUi.Label(log.transform, "B",
            "MALHA HEXAGONAL\n" +
            "EMISSOR ATE O RECEPTOR\nSEM PORTA PARA O VAZIO\n\n" +
            "LOGS: DO MENOR PARA\nO MAIOR. O ULTIMO\nDIGITO ABRE O COFRE\n\n" +
            "PLACAS: GIRE ATE A\nLUZ DESENHAR O 4",
            40, new Color(0.88f, 0.86f, 0.78f), TextAnchor.UpperLeft);
        lBody.rectTransform.anchorMin = Vector2.zero; lBody.rectTransform.anchorMax = Vector2.one;
        lBody.rectTransform.offsetMin = new Vector2(45, 40); lBody.rectTransform.offsetMax = new Vector2(-45, -120);
    }

    // =====================================================================
    // props (asset-count + set dressing)
    // =====================================================================
    static void BuildProps(Transform root)
    {
        // crates: a stack in the main hall and a pair in the SE alcove, so the floor is not
        // a bare 24 x 24 expanse
        MakeCrate(root, "Crate_A", new Vector3(6.4f, 0f, 1.4f), 0f);
        MakeCrate(root, "Crate_B", new Vector3(6.4f, 0.95f, 1.4f), 14f);
        MakeCrate(root, "Crate_C", new Vector3(8.0f, 0f, 7.6f), -22f);
        MakeCrate(root, "Crate_D", new Vector3(9.2f, 0f, 7.9f), 8f);

        // workbench, now inside the nook created by the x=4.5 partition
        var bench = new GameObject("Workbench");
        bench.transform.SetParent(root, false);
        bench.transform.position = new Vector3(8.2f, 0f, -8.6f);
        bench.transform.localEulerAngles = new Vector3(0f, 180f, 0f);
        EsTheme.Box("Top", bench.transform, new Vector3(0f, 0.92f, 0f), new Vector3(2.6f, 0.08f, 0.8f), _metal);
        foreach (var sx in new[] { -1.2f, 1.2f })
            EsTheme.Box("Leg" + sx, bench.transform, new Vector3(sx, 0.46f, 0f),
                        new Vector3(0.10f, 0.92f, 0.7f), _shellDark);
        EsTheme.Box("Backboard", bench.transform, new Vector3(0f, 1.25f, -0.35f),
                    new Vector3(2.6f, 0.60f, 0.06f), _shellDark);

        // pipe run along the north wall (dressing + reads as "facility")
        for (int i = 0; i < 5; i++)
            EsTheme.Box("Pipe" + i, root, new Vector3(-9.0f + i * 4.5f, 2.85f, WALL_N + 0.35f),
                        new Vector3(4.4f, 0.16f, 0.16f), _metal);
    }

    static void MakeCrate(Transform root, string name, Vector3 pos, float yaw)
    {
        var crate = new GameObject(name);
        crate.transform.SetParent(root, false);
        crate.transform.position = pos;
        crate.transform.localEulerAngles = new Vector3(0f, yaw, 0f);
        EsTheme.Box("Body", crate.transform, new Vector3(0f, 0.45f, 0f), new Vector3(1.0f, 0.9f, 1.0f), _shell);
        EsTheme.Box("Lid", crate.transform, new Vector3(0f, 0.93f, 0f), new Vector3(1.06f, 0.08f, 1.06f), _metal);
        EsTheme.Box("Band", crate.transform, new Vector3(0f, 0.45f, 0f), new Vector3(1.04f, 0.10f, 1.04f), _metal);
        var rb = crate.AddComponent<Rigidbody>();
        rb.mass = 4f;
    }

    /// <summary>Removes scene-root leftovers that no build owns: HUD copies from older
    /// builds, DontDestroyOnLoad audio pools and items dragged out during play sessions
    /// (this project runs with scene-reload disabled, so play-time mutations persist
    /// into the saved scene). Root-only (parent == null): anything under the level root
    /// is rebuilt anyway, and the user's own objects are never touched by name-match.</summary>
    static void CleanStrayRootObjects()
    {
        var strays = new HashSet<string> {
            "EsAimPrompt", "EsCrosshair", "EsSfxPool", "EsMusic",
            "CelulaDeEnergia", "ChaveDeAcesso", "CartaoDeLiberacao"
        };
        int n = 0;
        foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (go == null || !strays.Contains(go.name)) continue;
            string dead = go.name;
            Object.DestroyImmediate(go);
            n++;
            Debug.Log("[EscapeRoom] stray root object removed: " + dead);
        }
        if (n > 0) Debug.Log("[EscapeRoom] " + n + " stray root object(s) cleaned.");
    }

    // =====================================================================
    // dressing: Filial 9 ambience. A 24x24 bare floor reads as a warehouse, not a
    // deactivated automated bank branch, so this pass furnishes it: queue rails to the
    // terminal, a dead teller counter, waiting benches, dark ATMs, a server rack, ice
    // pillars (the arctic signature), a carpet runner, ceiling light panels, hanging
    // signs and facility smalls. Everything lives under Dressing (wiped per build).
    //
    // Keep-out volumes (never built into): the exit opening x[-7.5,-4.5] z[-13.5,-9.5],
    // the terminal lane x[-2.5,0.5] z[-10,-7], the archive front x[9.3,11.3] z[0.8,3.2].
    // Floor decals sit 0.015 proud with their UNDERSIDE sunk (top 0.025 / bottom -0.005):
    // flush faces z-fight (D-114), and the kit floor itself runs two layers (D-116).
    // =====================================================================
    static void BuildDressing(Transform root)
    {
        var dress = new GameObject("Dressing");
        dress.transform.SetParent(root, false);

        // ---- queue rails toward the terminal (side runs only, both ends open: a cross
        // ---- belt would fence the player out of the lane they must walk)
        foreach (var px in new[] { -2.6f, 0.6f })
            foreach (var pz in new[] { -7.5f, -5.5f })
                QueuePost(dress.transform, new Vector3(px, 0f, pz));
        EsTheme.Box("BeltW", dress.transform, new Vector3(-2.6f, 0.80f, -6.5f),
                    new Vector3(0.06f, 0.12f, 2.0f), _belt);
        EsTheme.Box("BeltE", dress.transform, new Vector3(0.6f, 0.80f, -6.5f),
                    new Vector3(0.06f, 0.12f, 2.0f), _belt);

        // ---- dead teller counter, west-centre, front facing east
        var counter = new GameObject("TellerCounter");
        counter.transform.SetParent(dress.transform, false);
        counter.transform.position = new Vector3(-5.5f, 0f, 0.5f);
        EsTheme.Box("Base", counter.transform, new Vector3(0f, 0.525f, 0f),
                    new Vector3(4.0f, 1.05f, 0.6f), _shellDark);
        EsTheme.Box("Top", counter.transform, new Vector3(0f, 1.08f, 0f),
                    new Vector3(4.3f, 0.06f, 0.9f), _metal);
        foreach (var fx in new[] { -1.5f, 0f, 1.5f })
            EsTheme.Box("Fin" + fx, counter.transform, new Vector3(fx, 1.36f, 0f),
                        new Vector3(0.06f, 0.50f, 0.7f), _shell);
        foreach (var sx in new[] { -1.0f, 1.0f })
        {
            EsTheme.Box("ScreenPost" + sx, counter.transform, new Vector3(sx, 1.36f, -0.1f),
                        new Vector3(0.08f, 0.50f, 0.08f), _metal);
            EsTheme.Box("Screen" + sx, counter.transform, new Vector3(sx, 1.62f, -0.1f),
                        new Vector3(0.52f, 0.36f, 0.05f), _screenOff);
        }

        // ---- waiting benches, south-centre, facing north
        MakeBench(dress.transform, new Vector3(-3.0f, 0f, 6.0f));
        MakeBench(dress.transform, new Vector3(2.5f, 0f, 6.0f));

        // ---- dark ATMs, east wall, facing west (unpowered: the branch is deactivated)
        MakeAtm(dress.transform, new Vector3(11.0f, 0f, 5.5f));
        MakeAtm(dress.transform, new Vector3(11.0f, 0f, 7.0f));

        // ---- server rack, east wall north of the archive, facing west
        var rack = new GameObject("ServerRack");
        rack.transform.SetParent(dress.transform, false);
        rack.transform.position = new Vector3(11.0f, 0f, -3.5f);
        EsTheme.Box("Cabinet", rack.transform, new Vector3(0f, 1.0f, 0f),
                    new Vector3(0.8f, 2.0f, 0.9f), _shellDark);
        foreach (var ly in new[] { 0.8f, 1.2f, 1.6f })
            EsTheme.Box("Led" + ly, rack.transform, new Vector3(-0.41f, ly, 0f),
                        new Vector3(0.02f, 0.03f, 0.7f), _panelGlow);
        EsTheme.Box("Vent", rack.transform, new Vector3(0f, 2.03f, 0f),
                    new Vector3(0.7f, 0.06f, 0.8f), _metal);

        // ---- ice pillars, the arctic signature, near the four corners
        foreach (var cp in new[] {
            new Vector3(-10.3f, 0f, -10.2f), new Vector3(10.3f, 0f, -10.2f),
            new Vector3(-10.3f, 0f, 10.3f), new Vector3(10.3f, 0f, 10.3f) })
            MakeFrostPillar(dress.transform, cp);

        // ---- carpet runner: south door to the queue entry + mats (no colliders:
        // ---- floor decals must never answer a physics ray)
        FlatCarpet(dress.transform, "Runner1", new Vector3(-0.25f, 0.01f, 7.5f),
                   new Vector3(1.8f, 0.03f, 5.0f));
        FlatCarpet(dress.transform, "Runner2", new Vector3(-1.0f, 0.01f, 0f),
                   new Vector3(1.8f, 0.03f, 10.0f));
        FlatCarpet(dress.transform, "ArchiveMat", new Vector3(10.3f, 0.01f, 2.8f),
                   new Vector3(1.6f, 0.03f, 1.6f));
        FlatCarpet(dress.transform, "BenchMat", new Vector3(8.2f, 0.01f, -8.6f),
                   new Vector3(2.8f, 0.03f, 1.4f));

        // ---- ceiling light panels over the centre (fake glow: the real light is RoomLight)
        foreach (var px in new[] { -3.0f, 1.0f })
            foreach (var pz in new[] { -4.0f, 0.0f, 4.0f })
                MakeCeilingPanel(dress.transform, new Vector3(px, 3.32f, pz));

        // ---- hanging queue sign, readable from both sides (D-33 on each face).
        // Board centre 2.62: its bottom (2.37) clears the 2.03 m player capsule (D-178).
        MakeHangingSign(dress.transform, new Vector3(-1.0f, 2.62f, -5.5f), "ATENDIMENTO");
        // ---- branch sign on the south wall, facing into the room
        var bs = EsUi.WorldCanvas(dress.transform, "BranchSign", new Vector3(2.0f, 2.4f, 11.74f),
                                  new Vector3(0f, 0f, 0f), new Vector2(640f, 280f),
                                  new Color32(0x14, 0x2A, 0x22, 0xFF));
        bs.GetComponent<RectTransform>().localScale = Vector3.one * 0.0016f;
        // plaque geometry (top-strip title + shortened body): a full-rect MiddleCenter
        // title's glyph box reaches the body text and trips the overlap check.
        var bs1 = EsUi.Label(bs.transform, "T", "NO-ZERO", 64,
                             new Color(0.55f, 0.92f, 0.80f), TextAnchor.UpperCenter, FontStyle.Bold);
        bs1.rectTransform.anchorMin = new Vector2(0, 1); bs1.rectTransform.anchorMax = new Vector2(1, 1);
        bs1.rectTransform.offsetMin = new Vector2(15, -95); bs1.rectTransform.offsetMax = new Vector2(-15, -12);
        var bs2 = EsUi.Label(bs.transform, "B", "PESQUISA CRIPTOGRAFICA", 34,
                             new Color(0.88f, 0.94f, 0.91f), TextAnchor.UpperCenter);
        bs2.rectTransform.anchorMin = Vector2.zero; bs2.rectTransform.anchorMax = Vector2.one;
        bs2.rectTransform.offsetMin = new Vector2(15, 12); bs2.rectTransform.offsetMax = new Vector2(-15, -140);

        // ---- facility smalls: fire cabinet by the exit, bins by the benches
        EsTheme.Box("FireCabinet", dress.transform, new Vector3(-3.6f, 1.1f, -11.3f),
                    new Vector3(0.7f, 1.4f, 0.4f), _cabRed);
        EsTheme.Box("FireStripe", dress.transform, new Vector3(-3.6f, 1.35f, -11.09f),
                    new Vector3(0.5f, 0.18f, 0.02f), _paper);
        MakeBin(dress.transform, new Vector3(-4.6f, 0f, 4.6f));
        MakeBin(dress.transform, new Vector3(4.1f, 0f, 4.6f));

        Debug.Log("[EscapeRoom] Dressing built: queue, counter, benches, ATMs, rack, "
                  + "ice, runner, panels, signs, smalls");
    }

    static void QueuePost(Transform parent, Vector3 pos)
    {
        var p = new GameObject("QueuePost");
        p.transform.SetParent(parent, false);
        p.transform.position = pos;
        Cyl(p.transform, "Base", new Vector3(0f, 0.02f, 0f), 0.16f, 0.04f, _metal);
        Cyl(p.transform, "Pole", new Vector3(0f, 0.50f, 0f), 0.035f, 0.95f, _metal);
        var knob = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        knob.name = "Knob";
        knob.transform.SetParent(p.transform, false);
        knob.transform.localPosition = new Vector3(0f, 1.0f, 0f);
        knob.transform.localScale = Vector3.one * 0.11f;
        knob.GetComponent<Renderer>().sharedMaterial = _metal;
    }

    static void Cyl(Transform parent, string name, Vector3 localPos, float r, float h, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = new Vector3(r * 2f, h / 2f, r * 2f);
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    static void MakeBench(Transform parent, Vector3 pos)
    {
        var b = new GameObject("Bench");
        b.transform.SetParent(parent, false);
        b.transform.position = pos;
        EsTheme.Box("Seat", b.transform, new Vector3(0f, 0.45f, 0f),
                    new Vector3(1.8f, 0.09f, 0.5f), _shell);
        foreach (var lx in new[] { -0.75f, 0.75f })
            EsTheme.Box("Leg" + lx, b.transform, new Vector3(lx, 0.225f, 0f),
                        new Vector3(0.09f, 0.45f, 0.45f), _shellDark);
        EsTheme.Box("Back", b.transform, new Vector3(0f, 0.80f, 0.27f),
                    new Vector3(1.8f, 0.50f, 0.09f), _shell);
    }

    static void MakeAtm(Transform parent, Vector3 pos)
    {
        var a = new GameObject("DeadAtm");
        a.transform.SetParent(parent, false);
        a.transform.position = pos;
        EsTheme.Box("Body", a.transform, new Vector3(0f, 0.90f, 0f),
                    new Vector3(0.7f, 1.8f, 0.65f), _shellDark);
        EsTheme.Box("Plinth", a.transform, new Vector3(0f, 0.06f, 0f),
                    new Vector3(0.8f, 0.12f, 0.75f), _metal);
        EsTheme.Box("Screen", a.transform, new Vector3(-0.33f, 1.40f, 0f),
                    new Vector3(0.05f, 0.50f, 0.40f), _screenOff);
        EsTheme.Box("Slot", a.transform, new Vector3(-0.33f, 1.05f, 0f),
                    new Vector3(0.05f, 0.06f, 0.30f), _metal);
        EsTheme.Box("Top", a.transform, new Vector3(0f, 1.83f, 0f),
                    new Vector3(0.74f, 0.06f, 0.69f), _metal);
    }

    static void MakeFrostPillar(Transform parent, Vector3 pos)
    {
        var f = new GameObject("FrostPillar");
        f.transform.SetParent(parent, false);
        f.transform.position = pos;
        EsTheme.Box("Base", f.transform, new Vector3(0f, 0.06f, 0f),
                    new Vector3(0.70f, 0.12f, 0.70f), _metal);
        EsTheme.Box("Ice", f.transform, new Vector3(0f, 1.42f, 0f),
                    new Vector3(0.55f, 2.60f, 0.55f), _frost);
        EsTheme.Box("Cap", f.transform, new Vector3(0f, 2.76f, 0f),
                    new Vector3(0.62f, 0.08f, 0.62f), _metal);
    }

    static void FlatCarpet(Transform parent, string name, Vector3 pos, Vector3 size)
    {
        var go = EsTheme.Box(name, parent, Vector3.zero, size, _carpet);
        go.transform.position = pos;
        Object.DestroyImmediate(go.GetComponent<Collider>());
    }

    static void MakeCeilingPanel(Transform parent, Vector3 pos)
    {
        var p = new GameObject("CeilPanel");
        p.transform.SetParent(parent, false);
        p.transform.position = pos;
        EsTheme.Box("Glow", p.transform, Vector3.zero,
                    new Vector3(1.4f, 0.06f, 0.8f), _panelGlow);
        foreach (var rx in new[] { -0.5f, 0.5f })
            EsTheme.Box("Rod" + rx, p.transform, new Vector3(rx, 0.20f, 0f),
                        new Vector3(0.04f, 0.35f, 0.04f), _metal);
    }

    static void MakeHangingSign(Transform parent, Vector3 pos, string text)
    {
        var s = new GameObject("HangSign");
        s.transform.SetParent(parent, false);
        s.transform.position = pos;
        EsTheme.Box("Board", s.transform, Vector3.zero,
                    new Vector3(1.6f, 0.5f, 0.08f), _shellDark);
        // Rods run from the board top (2.87) to just under the 3.70 ceiling.
        foreach (var rx in new[] { -0.6f, 0.6f })
            EsTheme.Box("Rod" + rx, s.transform, new Vector3(rx, 0.69f, 0f),
                        new Vector3(0.04f, 0.88f, 0.04f), _metal);
        foreach (var side in new[] { 1f, -1f })
        {
            var cv = EsUi.WorldCanvas(s.transform, "Face" + (side > 0 ? "S" : "N"),
                                      new Vector3(0f, 0f, side * 0.045f),
                                      new Vector3(0f, side > 0 ? 180f : 0f, 0f),
                                      new Vector2(640f, 200f),
                                      new Color32(0x14, 0x2A, 0x22, 0xFF));
            cv.GetComponent<RectTransform>().localScale = Vector3.one * 0.0016f;
            SignTitle(cv.transform, text, 64);
        }
    }

    static void SignTitle(Transform parent, string text, int size)
    {
        var t = EsUi.Label(parent, "T", text, size, new Color(0.55f, 0.92f, 0.80f),
                           TextAnchor.MiddleCenter, FontStyle.Bold);
        t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
        t.rectTransform.offsetMin = new Vector2(15, 12); t.rectTransform.offsetMax = new Vector2(-15, -12);
    }

    static void MakeBin(Transform parent, Vector3 pos)
    {
        var b = new GameObject("Bin");
        b.transform.SetParent(parent, false);
        b.transform.position = pos;
        Cyl(b.transform, "Can", new Vector3(0f, 0.28f, 0f), 0.22f, 0.55f, _shellDark);
        Cyl(b.transform, "Rim", new Vector3(0f, 0.56f, 0f), 0.24f, 0.05f, _metal);
    }

    // =====================================================================
    // A.E.G.I.S. Fase 0: denied-access holograms + exposed fibre-optic runs.
    // Holograms are their own World Canvases (no cross-canvas overlap), carry
    // no colliders and no click handlers, and blink alpha-only (EsHologramFlicker)
    // so they can never eat a crosshair click or trip the overlap checks.
    // Fibre runs sit 5 cm proud of the north inner face (-11.35), above head
    // height; the drop hides behind the terminal body below y ~2.0.
    // =====================================================================
    static void BuildAegisDressing(Transform root)
    {
        var g = new GameObject("Aegis");
        g.transform.SetParent(root, false);

        Hologram(g.transform, "HoloTerminal", new Vector3(-1.0f, 2.55f, -10.0f), 0f);
        Hologram(g.transform, "HoloExit", new Vector3(-3.6f, 2.35f, -11.05f), 2.1f);

        foreach (var fy in new[] { 2.50f, 2.60f, 2.70f })
            EsTheme.Box("Fiber" + fy, g.transform, new Vector3(0f, fy, -11.30f),
                        new Vector3(20f, 0.03f, 0.03f), _fiber);
        EsTheme.Box("FiberDrop", g.transform, new Vector3(-2.0f, 1.6f, -11.30f),
                    new Vector3(0.03f, 1.9f, 0.03f), _fiber);

        Debug.Log("[EscapeRoom] A.E.G.I.S. dressing built: 2 holograms + fibre runs");
    }

    static void Hologram(Transform parent, string name, Vector3 pos, float phase)
    {
        var cv = EsUi.WorldCanvas(parent, name, pos, new Vector3(0f, 180f, 0f),
                                  new Vector2(640f, 200f),
                                  new Color(0.10f, 0.02f, 0.02f, 0.72f));
        cv.GetComponent<RectTransform>().localScale = Vector3.one * 0.004f;   // 2.56 x 0.80 m
        var t = EsUi.Label(cv.transform, "Msg", "ACESSO NEGADO", 64,
                           new Color(1f, 0.25f, 0.22f),
                           TextAnchor.MiddleCenter, FontStyle.Bold);
        t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
        t.rectTransform.offsetMin = new Vector2(15, 12); t.rectTransform.offsetMax = new Vector2(-15, -12);
        var fl = t.gameObject.AddComponent<EsHologramFlicker>();
        fl.speed = 3.1f;
        fl.phase = phase;
    }

    // =====================================================================
    // Puzzle 1 (Fase 1): hex plasma-routing panel on the north wall.
    // Identity rotation (3D boxes need no yaw); only the status canvas takes
    // yaw 180 per D-33. Grid centred on x=2.9: clear of the locker (east edge
    // 1.7), the x=4.5 partition (west face 4.3) and the terminal lane.
    // Clicks arrive as 3D IInteractable via the existing crosshair ray.
    // =====================================================================
    static EsHexGrid BuildHexPanel(Transform root, EscapeGameManager gm)
    {
        var panel = new GameObject("HexPanel");
        panel.transform.SetParent(root, false);

        var grid = panel.AddComponent<EsHexGrid>();
        grid.cols = 4;
        grid.rows = 4;
        grid.cellSize = 0.27f;
        grid.seed = 2142;
        grid.baseMat = _hexBase;
        grid.dimMat = _hexDim;
        grid.liveMat = _hexLive;
        grid.srcMat = _hexSrc;
        grid.liveTileMat = _hexLiveTile;
        grid.leafMat = _hexLeaf;
        grid.leafLiveMat = _hexLeafLive;
        grid.rotateClip = Clip("lock_insert");
        grid.solvedClip = Clip("unlock_chime");
        grid.gm = gm;
        grid.Generate();

        float w = grid.boundsLocal.x, h = grid.boundsLocal.y;
        panel.transform.position = new Vector3(2.9f - w * 0.5f, 0.62f, -11.28f);

        EsTheme.Box("HexBoard", panel.transform, new Vector3(w * 0.5f, h * 0.5f, -0.065f),
                    new Vector3(w + 0.36f, h + 0.52f, 0.09f), _shellDark);

        var st = EsUi.WorldCanvas(panel.transform, "HexStatus", new Vector3(w * 0.5f, h + 0.42f, 0.02f),
                                  new Vector3(0f, 180f, 0f), new Vector2(640f, 200f),
                                  new Color32(0x14, 0x1A, 0x1A, 0xFF));
        st.GetComponent<RectTransform>().localScale = Vector3.one * 0.0016f;
        var t1 = EsUi.Label(st.transform, "T", "MALHA DE ENERGIA", 56,
                            new Color(0.55f, 0.92f, 0.80f), TextAnchor.UpperCenter, FontStyle.Bold);
        t1.rectTransform.anchorMin = new Vector2(0, 1); t1.rectTransform.anchorMax = new Vector2(1, 1);
        t1.rectTransform.offsetMin = new Vector2(15, -95); t1.rectTransform.offsetMax = new Vector2(-15, -12);
        var t2 = EsUi.Label(st.transform, "B", "MALHA INSTAVEL - GIRE OS NOS", 34,
                            new Color(0.88f, 0.94f, 0.91f), TextAnchor.UpperCenter);
        t2.rectTransform.anchorMin = Vector2.zero; t2.rectTransform.anchorMax = Vector2.one;
        t2.rectTransform.offsetMin = new Vector2(15, 12); t2.rectTransform.offsetMax = new Vector2(-15, -100);
        grid.statusLabel = t2;
        grid.RefreshAll();

        Debug.Log("[EscapeRoom] Hex panel built: " + grid.nodes.Count + " nodes, "
                  + PathDbg(grid) + " (seed 2142)");
        return grid;
    }

    static void WireTerminalObjectives(TerminalController tc)
    {
        // The hint line names the current objective; the puzzles own their
        // state, the terminal only reads it. Wired here (not in BuildTerminal)
        // because the stations are built after the terminal.
        if (tc == null) return;
        tc.hex = Object.FindFirstObjectByType<EsHexGrid>();
        tc.logPuzzle = Object.FindFirstObjectByType<EsLogSortPuzzle>();
        tc.safePad = Object.FindFirstObjectByType<EsSafeKeypad>();
        tc.plateReader = Object.FindFirstObjectByType<EsPlateReader>();
        Debug.Log("[EscapeRoom] Terminal objectives wired: hex/logs/safe/reader");
    }

    static string PathDbg(EsHexGrid grid)
    {
        int path = 0, fixed_ = 0;
        foreach (var n in grid.nodes) { if (n.isPath) path++; if (n.isFixed) fixed_++; }
        return path + " on-path, " + fixed_ + " fixed, " + grid.capsPlaced + " caps";
    }

    // =====================================================================
    // Puzzle 2 (Fase 2): log-decrypt console + safe with its own PIN keypad.
    // WEST wall at (-11.3, z=5.0), root yawed +90 so its face looks +X into
    // the room (rigid transform: child canvases keep local yaw 180, composing
    // to world -90, the west-wall rule). North wall already holds hex,
    // terminal and door; stacking a fourth station there made every puzzle a
    // queue on one wall. Flow now walks the room: hex (north) -> logs/safe
    // (west) -> plates across to the reader (south-east) -> door (north).
    // Click-click (block, then slot): with a locked cursor the click resolves
    // at the screen centre, so a real drag cannot be aimed. The safe keypad is decoupled from
    // TerminalController: its own EsSafeKeypad, PIN 3719 (last chars of the
    // chronologically ordered packets). The three acrylic plates live in the
    // safe cavity, unreachable until the door swings (same guarantee as the
    // locker, D-32).
    // =====================================================================
    static void BuildLogStation(Transform root, EsHexGrid grid)
    {
        var st = new GameObject("LogStation");
        st.transform.SetParent(root, false);
        st.transform.position = new Vector3(-11.30f, 0f, 5.0f);
        st.transform.localEulerAngles = new Vector3(0f, 90f, 0f);

        // safe cavity: back / sides / top / floor, open front (+z, the room)
        EsTheme.Box("SafeBack", st.transform, new Vector3(0f, 0.55f, -0.25f), new Vector3(1.6f, 1.1f, 0.06f), _shell);
        EsTheme.Box("SafeLeft", st.transform, new Vector3(-0.8f, 0.55f, 0f), new Vector3(0.06f, 1.1f, 0.5f), _shell);
        EsTheme.Box("SafeRight", st.transform, new Vector3(0.8f, 0.55f, 0f), new Vector3(0.06f, 1.1f, 0.5f), _shell);
        EsTheme.Box("SafeTop", st.transform, new Vector3(0f, 1.1f, 0f), new Vector3(1.6f, 0.06f, 0.5f), _shell);
        EsTheme.Box("SafeFloor", st.transform, new Vector3(0f, 0.03f, 0f), new Vector3(1.6f, 0.06f, 0.5f), _metal);
        var hinge = new GameObject("SafeDoorPivot");
        hinge.transform.SetParent(st.transform, false);
        hinge.transform.localPosition = new Vector3(-0.8f, 0f, 0.25f);
        EsTheme.Box("SafeDoor", hinge.transform, new Vector3(0.8f, 0.55f, 0f), new Vector3(1.6f, 1.1f, 0.06f), _metal);

        // upper pillar carrying both canvases
        EsTheme.Box("Pillar", st.transform, new Vector3(0f, 1.75f, -0.19f), new Vector3(2.0f, 1.5f, 0.1f), _shell);

        // ---- log canvas (1500x800 @ 0.0009 -> 1.35 x 0.72 m)
        var logGo = new GameObject("LogCanvas", typeof(RectTransform));
        logGo.transform.SetParent(st.transform, false);
        logGo.transform.localPosition = new Vector3(0f, 2.0f, -0.135f);
        logGo.transform.localEulerAngles = new Vector3(0f, 180f, 0f);
        var logCv = logGo.AddComponent<Canvas>();
        logCv.renderMode = RenderMode.WorldSpace;
        var logRt = (RectTransform)logGo.transform;
        logRt.sizeDelta = new Vector2(1500f, 800f);
        logRt.localScale = Vector3.one * 0.0009f;
        logGo.AddComponent<CanvasScaler>();
        logGo.AddComponent<GraphicRaycaster>();
        var logBg = EsUi.Img(logGo.transform, "Bg", EsTheme.ScreenBg);
        EsUi.Stretch(logBg.rectTransform);
        if (logGo.GetComponent<EsPuzzleInput>() == null)
        {
            var mark = logGo.AddComponent<EsPuzzleInput>();
            mark.what = "log packet ordering";
        }

        var lt = EsUi.Label(logGo.transform, "T", "REGISTRO DE ACESSO - A.E.G.I.S.", 46,
                            EsTheme.ScreenOn, TextAnchor.UpperCenter, FontStyle.Bold);
        lt.rectTransform.anchorMin = new Vector2(0, 1); lt.rectTransform.anchorMax = new Vector2(1, 1);
        lt.rectTransform.offsetMin = new Vector2(0, -90); lt.rectTransform.offsetMax = new Vector2(0, -12);

        var puzzle = st.AddComponent<EsLogSortPuzzle>();
        puzzle.grid = grid;
        puzzle.selectClip = Clip("ui_click");
        puzzle.placeClip = Clip("lock_insert");
        puzzle.solvedClip = Clip("unlock_chime");
        puzzle.errorClip = Clip("error_buzz");

        const float bw = 330f, bh = 110f, gap = 20f, x0 = 60f;
        for (int i = 0; i < 4; i++)
        {
            var b = EsUi.ButtonWithLabel(logGo.transform, "Block_" + i, "0x000000", 40,
                                         new Color(0.07f, 0.22f, 0.19f, 1f), EsTheme.ScreenOn);
            var brt = (RectTransform)b.transform;
            brt.anchorMin = brt.anchorMax = new Vector2(0, 1);
            brt.pivot = new Vector2(0, 1);
            brt.sizeDelta = new Vector2(bw, bh);
            brt.anchoredPosition = new Vector2(x0 + i * (bw + gap), -130f);
            AddHitTarget(brt, 0.015f, "_Block" + i);
            if (b.GetComponent<EsHoverHighlight>() == null) b.gameObject.AddComponent<EsHoverHighlight>();
            puzzle.blockButtons[i] = b;
            puzzle.blockLabels[i] = b.GetComponentInChildren<Text>();

            var s = EsUi.ButtonWithLabel(logGo.transform, "Slot_" + i, "----", 40,
                                         new Color(0.10f, 0.13f, 0.18f, 1f), EsTheme.Amber);
            var srt = (RectTransform)s.transform;
            srt.anchorMin = srt.anchorMax = new Vector2(0, 1);
            srt.pivot = new Vector2(0, 1);
            srt.sizeDelta = new Vector2(bw, bh);
            srt.anchoredPosition = new Vector2(x0 + i * (bw + gap), -280f);
            AddHitTarget(srt, 0.015f, "_Slot" + i);
            if (s.GetComponent<EsHoverHighlight>() == null) s.gameObject.AddComponent<EsHoverHighlight>();
            puzzle.slotButtons[i] = s;
            puzzle.slotLabels[i] = s.GetComponentInChildren<Text>();
        }
        var lst = EsUi.Label(logGo.transform, "LogStatus", "ORDENE OS PACOTES DO MENOR PARA O MAIOR", 38,
                             EsTheme.Amber, TextAnchor.MiddleCenter, FontStyle.Bold);
        lst.rectTransform.anchorMin = new Vector2(0, 1); lst.rectTransform.anchorMax = new Vector2(1, 1);
        lst.rectTransform.offsetMin = new Vector2(20, -520); lst.rectTransform.offsetMax = new Vector2(-20, -420);
        puzzle.statusLabel = lst;
        puzzle.Refresh();

        // ---- safe keypad canvas (1000x760 @ 0.0009 -> 0.90 x 0.68 m)
        var padGo = new GameObject("SafePad", typeof(RectTransform));
        padGo.transform.SetParent(st.transform, false);
        padGo.transform.localPosition = new Vector3(0f, 1.22f, -0.135f);
        padGo.transform.localEulerAngles = new Vector3(0f, 180f, 0f);
        var padCv = padGo.AddComponent<Canvas>();
        padCv.renderMode = RenderMode.WorldSpace;
        var padRt = (RectTransform)padGo.transform;
        padRt.sizeDelta = new Vector2(1000f, 760f);
        padRt.localScale = Vector3.one * 0.0009f;
        padGo.AddComponent<CanvasScaler>();
        padGo.AddComponent<GraphicRaycaster>();
        var padBg = EsUi.Img(padGo.transform, "Bg", EsTheme.ScreenBg);
        EsUi.Stretch(padBg.rectTransform);

        var keys = new GameObject("SafeKeys", typeof(RectTransform));
        keys.transform.SetParent(padGo.transform, false);
        var krt = (RectTransform)keys.transform;
        krt.anchorMin = Vector2.zero; krt.anchorMax = Vector2.one;
        krt.offsetMin = Vector2.zero; krt.offsetMax = Vector2.zero;
        if (keys.GetComponent<EsPuzzleInput>() == null)
        {
            var mark = keys.AddComponent<EsPuzzleInput>();
            mark.what = "safe PIN entry";
        }
        var echo = EsUi.Label(keys.transform, "SafeEcho", "____", 64, EsTheme.Amber,
                              TextAnchor.MiddleCenter, FontStyle.Bold);
        echo.rectTransform.anchorMin = new Vector2(0, 1); echo.rectTransform.anchorMax = new Vector2(1, 1);
        echo.rectTransform.offsetMin = new Vector2(0, -100); echo.rectTransform.offsetMax = new Vector2(0, -10);

        string[] caps = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "DEL", "ENTER" };
        const float cw = 280f, ch = 100f, kgap = 12f, kx0 = 68f, ky0 = 120f;
        for (int i = 0; i < caps.Length; i++)
        {
            int cx = i % 3, cy = i / 3;
            string cap = caps[i];
            bool special = cap == "ENTER" || cap == "DEL";
            Color plate = cap == "ENTER" ? new Color(0.20f, 0.46f, 0.16f, 1f)
                        : cap == "DEL" ? new Color(0.46f, 0.11f, 0.09f, 1f)
                        : new Color(0.07f, 0.22f, 0.19f, 1f);
            Color fg = cap == "DEL" ? EsTheme.Warn : cap == "ENTER" ? EsTheme.Amber : EsTheme.ScreenOn;
            var b = EsUi.ButtonWithLabel(keys.transform, "SKey_" + cap, cap, special ? 30 : 44, plate, fg);
            var brt = (RectTransform)b.transform;
            brt.anchorMin = brt.anchorMax = new Vector2(0, 1);
            brt.pivot = new Vector2(0, 1);
            brt.sizeDelta = new Vector2(cw, ch);
            brt.anchoredPosition = new Vector2(kx0 + cx * (cw + kgap), -ky0 - cy * (ch + kgap));
            AddHitTarget(brt, 0.015f, "_SKey");
            if (b.GetComponent<EsHoverHighlight>() == null) b.gameObject.AddComponent<EsHoverHighlight>();
        }
        // Total reset: a mistyped PIN restarts clean with one deliberate click
        // instead of four DELs. Sits below the grid, above the status line.
        var clr = EsUi.ButtonWithLabel(keys.transform, "SKey_LIMPAR", "LIMPAR", 30,
                                       new Color(0.46f, 0.11f, 0.09f, 1f), EsTheme.Warn);
        var clrt = (RectTransform)clr.transform;
        clrt.anchorMin = clrt.anchorMax = new Vector2(0, 1);
        clrt.pivot = new Vector2(0, 1);
        clrt.sizeDelta = new Vector2(864f, 80f);
        clrt.anchoredPosition = new Vector2(68f, -570f);
        AddHitTarget(clrt, 0.015f, "_SKey");
        if (clr.GetComponent<EsHoverHighlight>() == null) clr.gameObject.AddComponent<EsHoverHighlight>();

        var sst = EsUi.Label(keys.transform, "SafeStatus", "PIN 4 DIGITOS - DEL APAGA - LIMPAR ZERA", 30,
                             EsTheme.Amber, TextAnchor.MiddleCenter, FontStyle.Bold);
        sst.rectTransform.anchorMin = new Vector2(0, 1); sst.rectTransform.anchorMax = new Vector2(1, 1);
        sst.rectTransform.offsetMin = new Vector2(10, -744); sst.rectTransform.offsetMax = new Vector2(-10, -664);

        var pad = st.AddComponent<EsSafeKeypad>();
        pad.correctPin = "3719";
        pad.keypadRoot = keys;
        pad.echo = echo;
        pad.statusLabel = sst;
        pad.safeDoorPivot = hinge.transform;
        pad.clickClip = Clip("ui_click");
        pad.errorClip = Clip("error_buzz");
        pad.unlockClip = Clip("unlock_chime");
        pad.DebugReset();

        // ---- the three plates, standing in the safe cavity
        int[][] bases = new int[][]
        {
            new int[] { 0,1,0,0, 0,1,0,0, 0,0,0,0, 1,1,1,0 },
            new int[] { 1,0,0,0, 0,0,0,0, 0,0,1,1, 0,0,0,0 },
            new int[] { 0,0,1,0, 0,0,0,0, 0,0,1,0, 0,1,0,0 },
        };
        float[] px = new float[] { -0.45f, 0f, 0.45f };
        for (int i = 0; i < 3; i++)
        {
            var visual = PlateModel(st.transform, new Vector3(px[i], 0.62f, -0.05f), bases[i]);
            var item = FinishItem(visual, st.transform, "PlacaAcrilico" + (i + 1),
                                  new Vector3(px[i], 0.62f, -0.05f),
                                  "placa", "Placa de Acrilico " + (i + 1), 0.3f);
            var plate = visual.AddComponent<EsAcrylicPlate>();
            plate.plateId = i;
            plate.baseCells = bases[i];
            plate.rotation = 0;
            plate.cellsRoot = visual.transform.Find("Cells");
            plate.ApplyVisual();
        }

        Debug.Log("[EscapeRoom] Log station built: sort panel + safe PIN + 3 plates");
    }

    /// <summary>Acrylic plate visual: translucent sheet + opaque cells where the
    /// base matrix holds 1. Cells ride a "Cells" pivot so rotation turns the
    /// whole pattern rigidly, matching Rotated() by construction.</summary>
    static GameObject PlateModel(Transform parent, Vector3 pos, int[] baseCells)
    {
        var root = new GameObject("PlacaVisual");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = pos;
        EsTheme.Box("Sheet", root.transform, Vector3.zero, new Vector3(0.32f, 0.32f, 0.012f), _frost);
        var cells = new GameObject("Cells");
        cells.transform.SetParent(root.transform, false);
        cells.transform.localPosition = Vector3.zero;
        const float pitch = 0.07f;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                if (baseCells[r * 4 + c] == 0) continue;
                EsTheme.Box("Cell_" + r + "_" + c, cells.transform,
                    new Vector3((c - 1.5f) * pitch, (1.5f - r) * pitch, 0f),
                    new Vector3(0.06f, 0.06f, 0.016f), _shellDark);
            }
        return root;
    }

    // =====================================================================
    // Puzzle 3 (Fase 3): optical reader on the east wall SOUTH end (z=8.5),
    // by the ATMs and crates hardware corner - clear of the dead archive
    // (z 0.8..3.2) and the server rack (z -3.95..-3.05). North wall already
    // holds hex, terminal and door, west holds the log station: the three
    // puzzles now live on three walls and the run crosses the whole room.
    // Three sockets take any plate (all share itemKey "placa" - OR is
    // only rotations validate). The canvas previews the live OR and carries
    // one GIRAR button per slot; on the digit-4 template the reader latches
    // and the blast door force-opens.
    // =====================================================================
    static void BuildPlateReaderStation(Transform root, BlastDoor door, EscapeGameManager gm)
    {
        var rd = new GameObject("PlateReader");
        rd.transform.SetParent(root, false);
        rd.transform.position = new Vector3(11.30f, 0f, 8.5f);

        EsTheme.Box("ReaderBack", rd.transform, new Vector3(0.2f, 1.2f, 0f), new Vector3(0.1f, 2.0f, 2.2f), _shell);
        EsTheme.Box("ReaderTray", rd.transform, new Vector3(-0.1f, 0.82f, 0f), new Vector3(0.5f, 0.06f, 2.0f), _metal);

        var reader = rd.AddComponent<EsPlateReader>();
        reader.door = door;
        reader.gm = gm;
        reader.rotateClip = Clip("ui_click");
        reader.approveClip = Clip("success_stinger");
        reader.seatClip = Clip("lock_insert");

        float[] pz = new float[] { -0.6f, 0f, 0.6f };
        for (int i = 0; i < 3; i++)
        {
            var s = MakeSocket(rd.transform, "PlateSlot" + i, new Vector3(-0.05f, 1.15f, pz[i]),
                               "placa", "PLACA DE ACRILICO", _metal);
            s.transform.localEulerAngles = new Vector3(0f, 90f, 0f);
            s.acceptClip = Clip("lock_insert");
            s.rejectClip = Clip("error_buzz");
            reader.sockets[i] = s;
        }

        // canvas 1100x900 @ 0.0011 -> 1.21 x 0.99 m, east-wall yaw +90 (D-33)
        var cvGo = new GameObject("ReaderCanvas", typeof(RectTransform));
        cvGo.transform.SetParent(rd.transform, false);
        cvGo.transform.localPosition = new Vector3(0.1f, 2.05f, 0f);
        cvGo.transform.localEulerAngles = new Vector3(0f, 90f, 0f);
        var cv = cvGo.AddComponent<Canvas>();
        cv.renderMode = RenderMode.WorldSpace;
        var crt = (RectTransform)cvGo.transform;
        crt.sizeDelta = new Vector2(1100f, 900f);
        crt.localScale = Vector3.one * 0.0011f;
        cvGo.AddComponent<CanvasScaler>();
        cvGo.AddComponent<GraphicRaycaster>();
        var bg = EsUi.Img(cvGo.transform, "Bg", EsTheme.ScreenBg);
        EsUi.Stretch(bg.rectTransform);
        if (cvGo.GetComponent<EsPuzzleInput>() == null)
        {
            var mark = cvGo.AddComponent<EsPuzzleInput>();
            mark.what = "plate rotations";
        }

        var rt = EsUi.Label(cvGo.transform, "T", "LEITOR OPTICO - CHAVE UNIFICADA", 44,
                            EsTheme.ScreenOn, TextAnchor.UpperCenter, FontStyle.Bold);
        rt.rectTransform.anchorMin = new Vector2(0, 1); rt.rectTransform.anchorMax = new Vector2(1, 1);
        rt.rectTransform.offsetMin = new Vector2(0, -95); rt.rectTransform.offsetMax = new Vector2(0, -12);

        var prev = new GameObject("Preview", typeof(RectTransform));
        prev.transform.SetParent(cvGo.transform, false);
        var prt = (RectTransform)prev.transform;
        prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 1f);
        prt.pivot = new Vector2(0.5f, 1f);
        prt.sizeDelta = new Vector2(500f, 500f);
        prt.anchoredPosition = new Vector2(0f, -110f);
        const float cell = 110f, cgap = 12f, corg = (500f - (4 * cell + 3 * cgap)) / 2f;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                var img = EsUi.Img(prev.transform, "Pv_" + r + "_" + c, new Color(0.09f, 0.11f, 0.13f, 1f));
                var irt = img.rectTransform;
                irt.anchorMin = irt.anchorMax = new Vector2(0, 1);
                irt.pivot = new Vector2(0, 1);
                irt.sizeDelta = new Vector2(cell, cell);
                irt.anchoredPosition = new Vector2(corg + c * (cell + cgap), -(corg + r * (cell + cgap)));
                reader.previewCells[r * 4 + c] = img;
            }

        for (int i = 0; i < 3; i++)
        {
            var b = EsUi.ButtonWithLabel(cvGo.transform, "Gir_" + i, "GIRAR " + (i + 1), 36,
                                         new Color(0.07f, 0.22f, 0.19f, 1f), EsTheme.ScreenOn);
            var brt = (RectTransform)b.transform;
            brt.anchorMin = brt.anchorMax = new Vector2(0, 1);
            brt.pivot = new Vector2(0, 1);
            brt.sizeDelta = new Vector2(280f, 100f);
            brt.anchoredPosition = new Vector2(85f + i * 310f, -640f);
            AddHitTarget(brt, 0.015f, "_Gir" + i);
            if (b.GetComponent<EsHoverHighlight>() == null) b.gameObject.AddComponent<EsHoverHighlight>();
            reader.rotateButtons[i] = b;
        }
        var rst = EsUi.Label(cvGo.transform, "ReaderStatus", "LEITOR OPTICO - INSIRA AS 3 PLACAS (0/3)", 34,
                             EsTheme.Amber, TextAnchor.MiddleCenter, FontStyle.Bold);
        rst.rectTransform.anchorMin = new Vector2(0, 1); rst.rectTransform.anchorMax = new Vector2(1, 1);
        rst.rectTransform.offsetMin = new Vector2(20, -830); rst.rectTransform.offsetMax = new Vector2(-20, -760);
        reader.statusLabel = rst;
        reader.Refresh();
        reader.Wire();

        Debug.Log("[EscapeRoom] Plate reader built: 3 sockets + OR preview + door link");
    }


    // =====================================================================
    // lights
    // =====================================================================
    struct LightRefs { public Light room; public Light alarm; public Renderer strip; }

    static LightRefs BuildLights(Transform root)
    {
        var res = new LightRefs();

        // the interior sun: kill it, we are indoors
        Light sun = null;
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.type == LightType.Directional) { sun = l; break; }
        if (sun != null)
        {
            sun.intensity = 0.10f;
            sun.color = new Color(0.55f, 0.62f, 0.78f);
            sun.shadows = LightShadows.None;
        }

        var roomGo = new GameObject("RoomLight");
        roomGo.transform.SetParent(root, false);
        roomGo.transform.position = new Vector3(2.0f, 2.85f, 0.5f);
        var rl = roomGo.AddComponent<Light>();
        rl.type = LightType.Point; rl.range = 19f; rl.intensity = 0.85f;
        // Emergency red: Fase 0 (A.E.G.I.S.) starts the room in quarantine light.
        // Fase 1 (energy mesh) restores blue via EscapeGameManager.SetPowerRestored().
        // The cool fills (PropFill/SouthFill) stay: readability was measured (D-113).
        rl.color = new Color(1f, 0.16f, 0.14f);
        res.room = rl;

        // dedicated fill so the prop row (terminal / blast door) is readable before the
        // terminal screen turns on
        var fillGo = new GameObject("PropFill");
        fillGo.transform.SetParent(root, false);
        fillGo.transform.position = new Vector3(0.0f, 2.70f, -8.6f);
        var fl = fillGo.AddComponent<Light>();
        fl.type = LightType.Point; fl.range = 13f; fl.intensity = 0.70f;
        fl.color = new Color(0.72f, 0.84f, 0.95f);

        var alarmGo = new GameObject("AlarmLight");
        alarmGo.transform.SetParent(root, false);
        alarmGo.transform.position = new Vector3(2.0f, 2.60f, 0.5f);
        var al = alarmGo.AddComponent<Light>();
        al.type = LightType.Point; al.range = 20f; al.intensity = 0f;
        al.color = new Color(1f, 0.18f, 0.18f);
        res.alarm = al;

        var strip = EsTheme.Box("AlarmStrip", root, new Vector3(2.0f, 2.95f, 5.6f),
                                new Vector3(9.0f, 0.08f, 0.08f), _alarm);
        res.strip = strip.GetComponent<Renderer>();

        // dedicated warm light over the dead archive (east wall sits outside PropFill):
        // the five spines must read from metres away, not just up close.
        var arcGo = new GameObject("ArchiveLight");
        arcGo.transform.SetParent(root, false);
        arcGo.transform.position = new Vector3(10.30f, 2.75f, 2.0f);
        var arc = arcGo.AddComponent<Light>();
        arc.type = LightType.Point; arc.range = 6.5f; arc.intensity = 1.0f;
        arc.color = new Color(1.0f, 0.88f, 0.70f);

        // south fill so the waiting area does not fall off into darkness
        var southGo = new GameObject("SouthFill");
        southGo.transform.SetParent(root, false);
        southGo.transform.position = new Vector3(0f, 2.70f, 7.5f);
        var sf = southGo.AddComponent<Light>();
        sf.type = LightType.Point; sf.range = 14f; sf.intensity = 0.55f;
        sf.color = new Color(0.75f, 0.85f, 0.95f);
        return res;
    }

    // =====================================================================
    // game manager
    // =====================================================================
    static EscapeGameManager BuildGameManager(Transform root, TerminalController terminal,
                                              BlastDoor door, LightRefs lights)
    {
        var go = new GameObject("GameManager");
        go.transform.SetParent(root, false);
        var gm = go.AddComponent<EscapeGameManager>();
        gm.reserveSeconds = 3600f;
        gm.warnAtSeconds = 600f;
        gm.terminal = terminal;
        gm.exitDoor = door;
        gm.roomLight = lights.room;
        gm.alarmLight = lights.alarm;
        gm.alarmStrip = lights.strip;
        gm.ambientClip = Clip("ambient_pad");
        gm.winClip = Clip("success_stinger");
        gm.failClip = Clip("servo_door");

        // interactor on the player
        var player = GameObject.Find("Player");
        if (player != null)
        {
            player.transform.position = new Vector3(1.5f, 0.05f, 0.5f);
            player.transform.rotation = Quaternion.identity;
            var pi = player.GetComponent<PlayerInteractor>();
            if (pi == null) pi = player.AddComponent<PlayerInteractor>();
            pi.interactDistance = 3.4f;
        }
        return gm;
    }

    // =====================================================================
    // event system (prefab from Starter Assets: actions already assigned)
    // =====================================================================
    static void BuildEventSystem(Transform root)
    {
        foreach (var g in Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None)) Object.DestroyImmediate(g.gameObject);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/Starter Assets/Runtime/Mobile/Prefabs/EventSystem/UI_EventSystem.prefab");
        if (prefab == null)
        {
            Debug.LogError("[EscapeRoom] UI_EventSystem.prefab not found - UI clicks will not work.");
            return;
        }
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        inst.name = "EventSystem";
        inst.transform.SetParent(root, true);
        inst.transform.position = new Vector3(0f, 0f, 0f);

        // A locked cursor reports desktop coordinates, which in a docked Game view land outside
        // the panel, so InputSystemUIInputModule hit-tests a point that is not on screen. This
        // routes the locked click through the crosshair instead - see EsCrosshairPointer.
        if (inst.GetComponent<EsCrosshairPointer>() == null) inst.AddComponent<EsCrosshairPointer>();
    }

    /// <summary>Screen-centre crosshair on an overlay canvas that carries no GraphicRaycaster.
    /// See <see cref="EsCrosshair"/>: it is invisible to the UI event system, which is the
    /// guarantee that it can never intercept a click meant for the terminal.
    ///
    /// Plus a read-only aim label under it (<see cref="EsAimPrompt"/>). This is NOT the removed
    /// EsInteractPrompt: nothing is bound to it, so it cannot type the keypad code - it only
    /// names what PlayerInteractor sees ("Clique para inserir CHAVE DE ACESSO" vs
    /// "Incompativel com CELULA DE ENERGIA"). The crosshair highlight only tracks canvas
    /// targets, so without the label the 3D sockets have zero aim feedback.</summary>
    static void BuildHud(Transform root)
    {
        // Both HUD objects live under the level root now: scene-root copies from older
        // builds accumulated forever (the old code only destroyed the crosshair, never
        // the prompt - fifteen strays and counting), and strays break "first object"
        // lookups by returning an arbitrary copy.
        foreach (var old in Object.FindObjectsByType<EsCrosshair>()) Object.DestroyImmediate(old.gameObject);
        foreach (var old in Object.FindObjectsByType<EsAimPrompt>()) Object.DestroyImmediate(old.gameObject);

        // The interact prompt is gone, but any canvas already saved into the scene from an earlier
        // build is still sitting there with its black plate, and a type no longer existing is not a
        // reason for the object not to. Cleaned up by NAME, which is the only handle left once the
        // component type is deleted. This was a real bug: removing the prompt's construction without
        // removing the destruction loop left the plate on screen forever.
        foreach (var t in Object.FindObjectsByType<Transform>())
        {
            if (t == null) continue;
            if (t.name == "EsInteractPrompt" || t.name == "PromptText")
            {
                Object.DestroyImmediate(t.gameObject);
                continue;
            }
            if (t.name == "Plate" && t.parent != null && t.parent.name == "PromptText")
                Object.DestroyImmediate(t.gameObject);
        }

        var cross = new GameObject("EsCrosshair", typeof(RectTransform));
        cross.transform.SetParent(root, false);
        var cc = cross.AddComponent<Canvas>();
        cc.renderMode = RenderMode.ScreenSpaceOverlay;
        cc.sortingOrder = 500;
        // deliberately NO GraphicRaycaster on this canvas
        var crt = (RectTransform)cross.transform;
        crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
        crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
        cross.AddComponent<EsCrosshair>();

        // Aim label: same click-safety contract as the crosshair (overlay, no raycaster,
        // raycastTarget off, blocksRaycasts off). Named AimLabel, NOT PromptText: the stale
        // prompt cleanup above destroys anything still called PromptText, and Self Test 6
        // counts those names as orphans.
        var promptGo = new GameObject("EsAimPrompt", typeof(RectTransform));
        promptGo.transform.SetParent(root, false);
        var pc = promptGo.AddComponent<Canvas>();
        pc.renderMode = RenderMode.ScreenSpaceOverlay;
        pc.sortingOrder = 501;
        var prt = (RectTransform)promptGo.transform;
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;
        var pgrp = promptGo.AddComponent<CanvasGroup>();
        pgrp.blocksRaycasts = false;
        pgrp.interactable = false;
        var labelGo = new GameObject("AimLabel", typeof(RectTransform));
        labelGo.transform.SetParent(promptGo.transform, false);
        var lrt = (RectTransform)labelGo.transform;
        lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0f);
        lrt.pivot = new Vector2(0.5f, 0f);
        lrt.anchoredPosition = new Vector2(0f, 64f);
        lrt.sizeDelta = new Vector2(900f, 60f);
        var label = labelGo.AddComponent<UnityEngine.UI.Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = 26;
        label.alignment = TextAnchor.LowerCenter;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.color = new Color(0.92f, 0.96f, 1f, 0.92f);
        label.raycastTarget = false;
        label.text = "";
        promptGo.AddComponent<EsAimPrompt>();

        Debug.Log("[EscapeRoom] Crosshair added (F1 toggles, hides when the cursor is unlocked). "
                  + "No GraphicRaycaster, so it cannot intercept a click. No interact key: it "
                  + "would type the keypad code for the player.");
    }

    // =====================================================================
    // first person camera fix
    // =====================================================================
    static void FixFirstPersonRig()
    {
        var capsule = GameObject.Find("Player/PlayerCapsule");
        var camGo = GameObject.Find("Player/MainCamera");
        if (capsule == null || camGo == null) return;

        // rig by the field-name scan used for the camera pivot (robust across the Starter Assets
        // asmdef), then trim the jump so the capsule cannot clip the ceiling
        // The prefab already ships its own pitch pivot, PlayerCameraRoot. Prefer it; only
        // create a fallback if it is somehow missing. The controller writes to this object on
        // every frame that has look input, so it must never be null or line 147 throws and takes
        // the yaw down with it. The rig itself ignores it (see EsFirstPersonCameraRig).
        var pivot = capsule.transform.Find("PlayerCameraRoot") ?? capsule.transform.Find("CameraTarget");
        if (pivot == null)
        {
            var p = new GameObject("CameraTarget");
            p.transform.SetParent(capsule.transform, false);
            p.transform.localPosition = new Vector3(0f, 1.375f, 0f);
            pivot = p.transform;
        }

        foreach (var mb in capsule.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            var t = mb.GetType();
            var fld = t.GetField("CinemachineCameraTarget");
            if (fld == null) continue;
            if (fld.GetValue(mb) == null) fld.SetValue(mb, pivot.gameObject);

            var jump = t.GetField("JumpHeight");
            if (jump != null)
            {
                jump.SetValue(mb, JUMP_HEIGHT);
                Debug.Log("[EscapeRoom] JumpHeight -> " + JUMP_HEIGHT + " (ceiling at " + CeilBottom.ToString("F2") + ")");
            }
            break;
        }

        // drop the Cinemachine brain: it is what applies the third-person boom
        foreach (var mb in camGo.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            if (mb.GetType().FullName.Contains("CinemachineBrain"))
                Object.DestroyImmediate(mb);
        }

        var rig = camGo.GetComponent<EsFirstPersonCameraRig>();
        if (rig == null) rig = camGo.AddComponent<EsFirstPersonCameraRig>();
        rig.body = capsule.transform;      // the rig owns pitch and reads yaw from here
        rig.eyeHeight = 1.375f;
        rig.sensitivity = 0.12f;

        // Re-seat the capsule at the root origin. Tests (and play sessions
        // without scene reload) move the CAPSULE in world space, while the
        // build only ever re-seated the Player ROOT - so a stale capsule
        // offset survived every rebuild and the next run started wherever the
        // last test stood. That is exactly the "impossible" ST5 side-on
        // failure: aimed from 2 m east of the button at 87 deg off-normal.
        // The camera is re-posed by the rig every frame in play; its edit
        // pose is set sane for the same reason.
        capsule.transform.localPosition = Vector3.zero;
        camGo.transform.localPosition = new Vector3(0f, 1.375f, 0f);

        // neutralise the old third-person rig so it cannot fight us
        var old = GameObject.Find("Player/PlayerFollowCamera");
        if (old != null) old.SetActive(false);

        // Spawn facing. The Player GameObject is the user's, so its POSITION is left alone, but
        // it shipped with a yaw of 0, which points the camera at the south wall while the whole
        // objective (terminal, mural, blast door) sits on the north wall at z = -10.6. A player
        // who spawns facing a blank wall concludes the level is broken. 180 deg puts the north
        // wall dead ahead with the terminal slightly off-centre, so the room still has to be
        // read rather than handed over. Edit-time only; the rig does not enforce it at runtime.
        capsule.transform.rotation = Quaternion.Euler(0f, SPAWN_YAW, 0f);
        Debug.Log("[EscapeRoom] Player spawn yaw -> " + SPAWN_YAW + " (faces the terminal wall)");
    }

    // =====================================================================
    // atmosphere + post fx
    // =====================================================================
    static void ConfigureAtmosphere()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = new Color(0.035f, 0.045f, 0.060f);
        RenderSettings.fogDensity = 0.022f;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.10f, 0.13f, 0.18f);
        RenderSettings.ambientEquatorColor = new Color(0.07f, 0.09f, 0.12f);
        RenderSettings.ambientGroundColor = new Color(0.03f, 0.035f, 0.045f);
    }

    static void BuildPostFx()
    {
        string path = VFX_DIR + "/EscapeRoomPostFx.asset";
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
        }
        // Remove EVERY override before re-adding. Keeping the previous Bloom/Vignette/etc and
        // then calling Add<T>() again throws "Component already exists in the volume", which
        // aborted the rest of BuildPostFx - including renderPostProcessing on the camera, so
        // the whole grade silently stopped applying on every run after the first (D-46).
        foreach (var c in profile.components.ToList()) profile.Remove(c.GetType());

        var bloom = profile.Add<Bloom>(true);
        bloom.active = true; bloom.intensity.Override(0.85f); bloom.threshold.Override(0.92f);
        bloom.scatter.Override(0.65f); bloom.tint.Override(new Color(0.90f, 1f, 0.97f));

        var vig = profile.Add<Vignette>(true);
        vig.active = true; vig.intensity.Override(0.42f); vig.smoothness.Override(0.45f);

        var tone = profile.Add<Tonemapping>(true);
        tone.active = true; tone.mode.Override(TonemappingMode.ACES);

        var grade = profile.Add<ColorAdjustments>(true);
        grade.active = true; grade.postExposure.Override(-0.55f);
        grade.contrast.Override(6f); grade.saturation.Override(-6f);
        EditorUtility.SetDirty(profile);

        var root = GameObject.Find(ROOT);
        var postGo = root != null ? root.transform.Find("PostFx") : null;
        if (postGo == null)
        {
            var go = new GameObject("PostFx");
            if (root != null) go.transform.SetParent(root.transform, false);
        }
        var vol = Object.FindAnyObjectByType<Volume>();
        if (vol == null)
        {
            var target = root != null ? root.transform.Find("PostFx") : null;
            if (target == null) target = new GameObject("PostFx").transform;
            vol = target.gameObject.AddComponent<Volume>();
        }
        vol.isGlobal = true;
        vol.sharedProfile = profile;
        vol.priority = 0f;

        var cam = GameObject.Find("Player/MainCamera");
        if (cam != null)
        {
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) data = cam.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
        }
        AssetDatabase.SaveAssets();
    }
}
