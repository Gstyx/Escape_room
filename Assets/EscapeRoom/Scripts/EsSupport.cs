using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeRoom
{
    /// <summary>Anything the player can click on.</summary>
    public interface IInteractable
    {
        /// <param name="held">Item currently in hand, or null.</param>
        /// <returns>true if the device consumed the held item; false leaves it in hand.</returns>
        bool Interact(GrabbableItem held);
        string PromptFor(GrabbableItem held);
    }

    /// <summary>Shared palette + primitive factory so every built material matches.</summary>
    public static class EsTheme
    {
        public static readonly Color Shell     = new Color32(0x2A, 0x30, 0x38, 0xFF);
        public static readonly Color ShellDark = new Color32(0x16, 0x1A, 0x1F, 0xFF);
        public static readonly Color Metal     = new Color32(0x5A, 0x64, 0x70, 0xFF);
        public static readonly Color ScreenBg  = new Color32(0x03, 0x10, 0x0E, 0xFF);
        public static readonly Color ScreenOn  = new Color32(0x3C, 0xE8, 0xA4, 0xFF);
        public static readonly Color Warn      = new Color32(0xE8, 0x3A, 0x3A, 0xFF);
        public static readonly Color Amber     = new Color32(0xE8, 0xA8, 0x3A, 0xFF);
        public static readonly Color KeyGold   = new Color32(0xD8, 0xB0, 0x40, 0xFF);

    /// <summary>The key's lit band. Amber, and amber ON PURPOSE: the card is CardCyan and the cell
    /// is CellGreen, so a cyan or green tint on the key would hand the player a colour that already
    /// means a different prop. Amber belongs to nothing else in the locker and is already the
    /// "confirm" colour on the keypad, so it marks the key without impersonating either object.</summary>
    public static readonly Color KeyAccent = new Color32(0xFF, 0xB0, 0x20, 0xFF);
        public static readonly Color CellGreen = new Color32(0x3A, 0xD8, 0x7A, 0xFF);
        public static readonly Color CardCyan  = new Color32(0x4A, 0xC8, 0xE8, 0xFF);

        public static Material Lit(Color c, float metallic = 0f, float smoothness = 0.35f)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            var m = new Material(sh) { name = "Es_" + ColorUtility.ToHtmlStringRGB(c) };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            return m;
        }

        public static Material Glow(Color c, float intensity = 2.5f)
        {
            var m = Lit(c, 0f, 0.5f);
            m.EnableKeyword("_EMISSION");
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", c * intensity);
            return m;
        }

        public static GameObject Box(string name, Transform parent, Vector3 localPos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            var r = go.GetComponent<Renderer>();
            if (r != null && mat != null) r.sharedMaterial = mat;
            return go;
        }
    }

    /// <summary>Legacy UI.Text factory. Deliberately not TextMeshPro: this project has no
    /// TMP Essential Resources imported, so TMP would fail to find a default font.</summary>
    public static class EsUi
    {
        static Font _font;
        public static Font Font
        {
            get
            {
                if (_font == null)
                {
                    _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    if (_font == null)
                    {
                        var names = Font.GetOSInstalledFontNames();
                        if (names != null && names.Length > 0)
                            _font = Font.CreateDynamicFontFromOSFont(names[0], 16);
                    }
                }
                return _font;
            }
        }

        public static void Stretch(RectTransform rt, float pad = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(pad, pad);
            rt.offsetMax = new Vector2(-pad, -pad);
        }

        public static RectTransform Rect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
                                        Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            return rt;
        }

        public static Text Label(Transform parent, string name, string content, int size, Color color,
                                 TextAnchor anchor, FontStyle style = FontStyle.Normal)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = Font;
            t.text = content;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.fontStyle = style;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;   // labels must never swallow the click meant for the button
            t.supportRichText = false;
            return t;
        }

        public static Image Img(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        public static Button ButtonWithLabel(Transform parent, string name, string label, int size,
                                             Color bg, Color fg)
        {
            var img = Img(parent, name, bg);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var c = btn.colors;
            c.normalColor = Color.white;
            c.highlightedColor = new Color(1.35f, 1.35f, 1.35f, 1f);
            c.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            c.selectedColor = Color.white;
            c.fadeDuration = 0.06f;
            btn.colors = c;
            var txt = Label(btn.transform, "Label", label, size, fg, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(txt.rectTransform, 4f);
            return btn;
        }

        /// <summary>World-space sign / readout built from a Canvas. Used for the wall notes.</summary>
        public static Canvas WorldCanvas(Transform parent, string name, Vector3 localPos, Vector3 localEuler,
                                         Vector2 sizeUnits, Color bg)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localEulerAngles = localEuler;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = sizeUnits;

            var bgImg = Img(go.transform, "Bg", bg);
            Stretch(bgImg.rectTransform);

            var ray = go.AddComponent<GraphicRaycaster>();
            ray.enabled = false;   // notes are read-only, never intercept clicks
            return canvas;
        }
    }

    /// <summary>Centralised audio. Clips are generated by Audio/generate_audio.py.
    ///
    /// Two invariants, both learned the hard way:
    ///
    /// 1. Statics are reset on play start. This project has Enter Play Mode Options with
    ///    domain reload DISABLED (<c>m_EnterPlayModeOptions: 3</c>), so static fields survive
    ///    from one play session to the next while the <c>DontDestroyOnLoad</c> pool GameObject
    ///    is destroyed on exit. Without the reset, the second session holds a list of dead
    ///    AudioSources.
    /// 2. Audio can never abort gameplay code. <c>MissingReferenceException</c> thrown from
    ///    inside an <c>onClick</c> handler unwinds the rest of that handler, which is how a dead
    ///    pool silently killed the reboot: <c>ForceReboot()</c> played a click sound on the line
    ///    before starting its coroutine, so the throw stopped the reboot from ever starting and
    ///    the button looked completely dead. Every entry point here returns quietly instead.
    /// </summary>
    public static class EsAudio
    {
        const int POOL_SIZE = 8;

        static AudioSource _music;
        static List<AudioSource> _pool;
        static int _next;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _music = null;
            _pool = null;
            _next = 0;
        }

        /// <summary>Drops the pool if any source in it has been destroyed.
        /// Unity overloads <c>==</c> on AudioSource so a destroyed component compares equal to
        /// null, but <c>List&lt;AudioSource&gt;</c> is a plain C# object: a bare
        /// <c>_pool == null</c> test is a plain reference check and stays false while every
        /// element inside is dead. That asymmetry was the bug.</summary>
        static void DropPoolIfAnySourceIsDead()
        {
            if (_pool == null) return;
            for (int i = 0; i < _pool.Count; i++)
            {
                if (_pool[i] == null) { _pool = null; return; }
            }
        }

        public static void Music(AudioClip clip, float volume)
        {
            if (clip == null) return;
            // AudioSource is a UnityEngine.Object, so this overload of == already detects a
            // destroyed component and rebuilds. No extra guard needed on the play call below.
            if (_music == null)
            {
                var go = new GameObject("EsMusic");
                Object.DontDestroyOnLoad(go);
                _music = go.AddComponent<AudioSource>();
                _music.loop = true;
                _music.spatialBlend = 0f;
                _music.playOnAwake = false;
            }
            if (_music.clip == clip && _music.isPlaying) return;
            _music.clip = clip;
            _music.volume = volume;
            _music.Play();
        }

        public static void Play(AudioClip clip, Vector3 pos, float volume = 1f, float spatial = 1f)
        {
            if (clip == null) return;
            var src = Next();
            if (src == null) return;     // pool unrecoverable; never throw over a sound effect
            src.transform.position = pos;
            src.spatialBlend = spatial;
            src.clip = clip;
            src.volume = volume;
            src.Play();
        }

        static AudioSource Next()
        {
            DropPoolIfAnySourceIsDead();
            if (_pool == null)
            {
                _pool = new List<AudioSource>();
                var go = new GameObject("EsSfxPool");
                Object.DontDestroyOnLoad(go);
                for (int i = 0; i < POOL_SIZE; i++)
                {
                    var s = go.AddComponent<AudioSource>();
                    s.playOnAwake = false;
                    s.rolloffMode = AudioRolloffMode.Linear;
                    s.minDistance = 1.5f;
                    s.maxDistance = 24f;
                    _pool.Add(s);
                }
                _next = 0;
            }
            var r = _pool[_next];
            _next = (_next + 1) % _pool.Count;
            return r;
        }
    }
}
