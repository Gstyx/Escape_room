using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Renders review screenshots of the room (player-eye views + a roofless top view)
/// to the folder in env ES_SHOTS_DIR (default: Screenshots/ next to Assets). Batchmode entry:
/// -executeMethod EsShots.Capture</summary>
public static class EsShots
{
    /// <summary>Batchmode: Build Level, puzzle self tests, text overflow, then screenshots.</summary>
    public static void BuildTestCapture()
    {
        EditorSceneManager.OpenScene("Assets/trabalho_marqueto.unity", OpenSceneMode.Single);
        EsSceneBuilder.Build();
        EsSceneBuilder.SelfTest13HexGrid();
        EsSceneBuilder.SelfTest14LogSort();
        EsSceneBuilder.SelfTest15Plates();
        EsSceneBuilder.CheckUiOverflow();
        EditorSceneManager.SaveOpenScenes();
        Capture();
    }

    [MenuItem("Tools/Escape Room/Capture review screenshots")]
    public static void Capture()
    {
        EditorSceneManager.OpenScene("Assets/trabalho_marqueto.unity", OpenSceneMode.Single);
        EscapeRoom.EsTheme.ApplyAmbientFill();   // what EscapeGameManager.Awake does in play
        string dir = System.Environment.GetEnvironmentVariable("ES_SHOTS_DIR");
        if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Application.dataPath, "../Screenshots");
        Directory.CreateDirectory(dir);

        var root = GameObject.Find("EscapeRoom_Root");
        Bounds b = new Bounds(Vector3.zero, Vector3.zero);
        bool init = false;
        if (root != null)
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (r.bounds.size.magnitude > 60f) continue;
                if (!init) { b = r.bounds; init = true; } else b.Encapsulate(r.bounds);
            }
        Debug.Log("[Shots] root bounds " + b);

        var camGo = new GameObject("ShotCam");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 75f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 200f;
        var rt = new RenderTexture(1280, 720, 24);
        cam.targetTexture = rt;

        Vector3 eye = new Vector3(1.5f, 1.65f, 0.5f);
        var player = GameObject.Find("Player");
        if (player != null) eye = player.transform.position + Vector3.up * 1.6f;
        for (int i = 0; i < 8; i++)
        {
            camGo.transform.position = eye;
            camGo.transform.rotation = Quaternion.Euler(8f, i * 45f, 0f);
            Save(cam, rt, Path.Combine(dir, "eye_" + (i * 45).ToString("000") + ".png"));
        }

        // stations, read off the scene
        Shoot(cam, rt, dir, "HexPanel", "st_hex");
        Shoot(cam, rt, dir, "LogStation", "st_logs");
        Shoot(cam, rt, dir, "PlateReader", "st_reader");
        // D-236: the wall boards, head-on and from the side (the side view is what shows a gap).
        Oblique(cam, rt, dir, "MuralPlantao09", "board_mural");
        Oblique(cam, rt, dir, "RegistroManutencao", "board_log");
        Oblique(cam, rt, dir, "BranchSign", "board_branch");

        // same eye views with the power restored (the state most of a run is played in)
        var gm = Object.FindFirstObjectByType<EscapeRoom.EscapeGameManager>();
        if (gm != null)
        {
            gm.SetPowerRestored();
            for (int i = 0; i < 8; i += 2)
            {
                camGo.transform.position = eye;
                camGo.transform.rotation = Quaternion.Euler(8f, i * 45f, 0f);
                Save(cam, rt, Path.Combine(dir, "lit_" + (i * 45).ToString("000") + ".png"));
            }
        }

        // roofless top view: hide anything above 2.6 m
        var hidden = new System.Collections.Generic.List<Renderer>();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            if (r.enabled && r.bounds.min.y > 2.4f) { r.enabled = false; hidden.Add(r); }
        cam.orthographic = true;
        cam.orthographicSize = Mathf.Max(b.extents.x, b.extents.z) * 0.62f;
        camGo.transform.position = new Vector3(b.center.x, 30f, b.center.z);
        camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        var top = new GameObject("TopLight").AddComponent<Light>();
        top.type = LightType.Directional; top.intensity = 1.2f;
        top.transform.rotation = Quaternion.Euler(70f, 30f, 0f);
        Save(cam, rt, Path.Combine(dir, "top.png"));
        cam.orthographic = false;
        camGo.transform.position = new Vector3(b.center.x, 14f, b.min.z - 6f);
        camGo.transform.LookAt(b.center);
        Save(cam, rt, Path.Combine(dir, "top_persp.png"));
        foreach (var r in hidden) r.enabled = true;

        Object.DestroyImmediate(top.gameObject);
        Object.DestroyImmediate(camGo);
        Debug.Log("[Shots] done -> " + dir);
    }

    static void Shoot(Camera cam, RenderTexture rt, string dir, string name, string file)
    {
        GameObject go = null;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            if (t.name == name) { go = t.gameObject; break; }
        if (go == null) { Debug.Log("[Shots] not found " + name); return; }
        Bounds b = new Bounds(go.transform.position, Vector3.zero);
        foreach (var r in go.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
        Vector3 fwd = go.transform.forward; fwd.y = 0;
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        fwd.Normalize();
        foreach (float sgn in new[] { 1f, -1f })
        {
            Vector3 p = b.center + fwd * sgn * 2.2f; p.y = 1.6f;
            cam.transform.position = p;
            cam.transform.LookAt(b.center);
            Save(cam, rt, Path.Combine(dir, file + (sgn > 0 ? "_a" : "_b") + ".png"));
        }
        Debug.Log("[Shots] " + name + " at " + b.center + " fwd " + fwd);
    }

    /// <summary>A world canvas faces its local -Z, so the viewer stands on -forward.</summary>
    static void Oblique(Camera cam, RenderTexture rt, string dir, string name, string file)
    {
        var go = GameObject.Find(name);
        if (go == null) { Debug.Log("[Shots] not found " + name); return; }
        var t = go.transform;
        Vector3 front = -t.forward;
        foreach (var v in new[] { new Vector2(0f, 2.4f), new Vector2(1.6f, 1.0f) })
        {
            cam.transform.position = t.position + front * v.y + t.right * v.x;
            cam.transform.LookAt(t.position);
            Save(cam, rt, Path.Combine(dir, file + (v.x == 0f ? "_front" : "_side") + ".png"));
        }
    }

    static void Save(Camera cam, RenderTexture rt, string path)
    {
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
    }
}
