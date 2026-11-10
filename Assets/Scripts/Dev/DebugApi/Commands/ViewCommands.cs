using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.Dev.DebugApi
{
    /// <summary>Camera, selection, screenshots and overlays.</summary>
    public static class ViewCommands
    {
        public static void Register(DebugRegistry r)
        {
            r.Add("camera", DebugRole.Any, "camera [x y] [zoom]: centre the camera (zoom = orthographic size); no arguments answers where it is", a =>
            {
                Camera cam = ViewCamera() ?? throw new DebugError("no camera");
                var controller = cam.GetComponent<Character.Character_Controler>();
                if (!DebugArgs.Has(a, "x") && !DebugArgs.Has(a, "zoom")) return new { x = cam.transform.position.x, y = cam.transform.position.y, zoom = cam.orthographicSize };
                var p = DebugArgs.Has(a, "x") ? new Vector2(DebugArgs.Float(a, "x"), DebugArgs.Float(a, "y")) : (Vector2)cam.transform.position;
                if (controller != null) controller.CentreOn(p);
                else cam.transform.position = new Vector3(p.x, p.y, cam.transform.position.z);
                if (DebugArgs.Has(a, "zoom"))
                {
                    // Through the controller, so the Pixel Perfect Camera is switched off between whole-number scales.
                    float size = Mathf.Clamp(DebugArgs.Float(a, "zoom"), 3f, 30f);
                    if (controller != null) controller.SetSize(size);
                    else cam.orthographicSize = size;
                }
                return new { x = p.x, y = p.y, zoom = cam.orthographicSize };
            });
            r.Add("select", DebugRole.Client, "select ids: replace the local selection", a =>
            {
                if (!(a["ids"] is JArray ids)) throw new DebugError("ids must be a list");
                var commander = UnitCommander.Instance ?? throw new DebugError("no UnitCommander (not in a match)");
                commander.Selection.SelectIds(ids.Select(t => (int)t));
                return commander.Selection.Selected.Count;
            });
            r.Add("uiclick", DebugRole.Any, "uiclick name: press the named UI Toolkit button, as a click does", a =>
            {
                string name = DebugArgs.Str(a, "name", "");
                foreach (UnityEngine.UIElements.UIDocument doc in UnityEngine.Object.FindObjectsByType<UnityEngine.UIElements.UIDocument>(UnityEngine.FindObjectsSortMode.None))
                {
                    UnityEngine.UIElements.Button b = doc.rootVisualElement?.Q<UnityEngine.UIElements.Button>(name);
                    if (b == null) continue;
                    using (var submit = UnityEngine.UIElements.NavigationSubmitEvent.GetPooled())
                    {
                        submit.target = b;
                        b.SendEvent(submit);
                    }
                    return "pressed " + name;
                }
                return "no button " + name;
            });
            r.Add("rings", DebugRole.Any, "rings on: hold the Show ranges key's rings on or off", a =>
            {
                WAR2D.Client.Overlays.HeldRangeOverlay.ForcedOn = DebugArgs.Bool(a, "on");
                return WAR2D.Client.Overlays.HeldRangeOverlay.ForcedOn ? "rings on" : "rings off";
            });
            r.Add("screenshot", DebugRole.Any, "screenshot [path] [scale=1] [box=[x0,y0,x1,y1]]: save a PNG of the whole screen (world and UI) at the end of this frame; box crops in screen pixels from the top left, scale (0.1-8) resizes it, above 1 by whole pixels to read small text; answers with the path and sizes", a =>
            {
                string path = DebugArgs.Str(a, "path", Path.Combine(Application.persistentDataPath, "screenshots", $"shot-{System.DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png"));
                float scale = Mathf.Clamp(DebugArgs.Float(a, "scale", 1f), 0.1f, 8f);
                RectInt? crop = null;
                if (a["box"] is JArray box)
                {
                    if (box.Count != 4) throw new DebugError("'box' must be [x0, y0, x1, y1]");
                    int x0 = Mathf.Min((int)box[0], (int)box[2]), y0 = Mathf.Min((int)box[1], (int)box[3]);
                    crop = new RectInt(x0, y0, Mathf.Abs((int)box[2] - (int)box[0]), Mathf.Abs((int)box[3] - (int)box[1]));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                string full = Path.GetFullPath(path);
                object result = null;
                string error = null;
                ShotRunner().StartCoroutine(CaptureRoutine(full, scale, crop, r2 => result = r2, e => error = e));
                return SessionCommands.Wait(() => error != null ? throw new DebugError(error) : result, 10);
            });
            r.Add("ui", DebugRole.Any, "ui [filter] [all=true] [max=800] [compact=true]: list the visible UI Toolkit elements, open dropdown menus included (buttons, fields, labels; all=true adds every named element and those scrolled out of view) with name, text and screen rect/centre in screenshot pixels; covered=true means something else is on top; compact=true gives one 'name \"text\" x,y' line each", a =>
                ListUi(DebugArgs.Str(a, "filter", ""), DebugArgs.Bool(a, "all", false), DebugArgs.Int(a, "max", 800), DebugArgs.Bool(a, "compact", false)));
            r.Add("overlay", DebugRole.Any, "overlay layer on [args]: toggle a debug overlay (Flow, Routes, Targets, Fog, Sectors, Hash, Footprints, Interest, Terrain)", a =>
                OverlayHook?.Invoke(DebugArgs.Str(a, "layer"), DebugArgs.Bool(a, "on"), a) ?? throw new DebugError("overlays not available in this build"));
        }

        private sealed class ShotBehaviour : MonoBehaviour { }
        private static MonoBehaviour shotRunner;

        private static MonoBehaviour ShotRunner()
        {
            if (shotRunner != null) return shotRunner;
            var go = new GameObject("DebugApiScreenshot") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            return shotRunner = go.AddComponent<ShotBehaviour>();
        }

        /// <summary>The batch mode picture: the screen's cameras, drawn on request. UI Toolkit panels only draw to a real screen, so they are missing. Null with no graphics device.</summary>
        private static Texture2D DrawCameras()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
            int w = Screen.width, h = Screen.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 24), previous = RenderTexture.active;
            Camera[] cameras = Camera.allCameras;
            System.Array.Sort(cameras, (a, b) => a.depth.CompareTo(b.depth));
            foreach (Camera cam in cameras)
            {
                if (cam.targetTexture != null) continue; // draws to its own texture, not the screen
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = null;
            }
            RenderTexture.active = rt;
            var shot = new Texture2D(w, h, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            shot.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return shot;
        }

        private static System.Collections.IEnumerator CaptureRoutine(string path, float scale, RectInt? crop, System.Action<object> done, System.Action<string> fail)
        {
            // After every camera and UI Toolkit panel has drawn. Batch mode draws nothing by itself and never reaches the end of a frame.
            if (!Application.isBatchMode) yield return new WaitForEndOfFrame();
            Texture2D shot = null, cut = null;
            RenderTexture rt = null;
            try
            {
                shot = Application.isBatchMode ? DrawCameras() : ScreenCapture.CaptureScreenshotAsTexture();
                if (shot == null) { fail("the screen could not be captured (is the player running with -nographics?)"); yield break; }
                int w = shot.width, h = shot.height;
                RectInt c = crop ?? new RectInt(0, 0, w, h);
                int cx = Mathf.Clamp(c.x, 0, w - 1), cy = Mathf.Clamp(c.y, 0, h - 1);
                int cw = Mathf.Clamp(c.width, 1, w - cx), ch = Mathf.Clamp(c.height, 1, h - cy);
                // Texture rows start at the bottom; the box is from the top.
                cut = new Texture2D(cw, ch, TextureFormat.RGB24, false);
                cut.SetPixels(shot.GetPixels(cx, h - cy - ch, cw, ch));
                cut.Apply();
                Texture2D output = cut;
                if (scale > 1f) output = Enlarged(cut, scale);
                else if (scale < 1f)
                {
                    int sw = Mathf.Max(1, Mathf.RoundToInt(cw * scale)), sh = Mathf.Max(1, Mathf.RoundToInt(ch * scale));
                    rt = RenderTexture.GetTemporary(sw, sh, 0);
                    Graphics.Blit(cut, rt);
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = rt;
                    output = new Texture2D(sw, sh, TextureFormat.RGB24, false);
                    output.ReadPixels(new Rect(0, 0, sw, sh), 0, 0);
                    output.Apply();
                    RenderTexture.active = previous;
                }
                File.WriteAllBytes(path, output.EncodeToPNG());
                done(new { path, width = output.width, height = output.height, screenWidth = w, screenHeight = h, box = new[] { cx, cy, cx + cw, cy + ch }, scale });
                if (output != cut) Object.Destroy(output);
            }
            finally
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (shot != null) Object.Destroy(shot);
                if (cut != null) Object.Destroy(cut);
            }
        }

        /// <summary>A copy of <paramref name="source"/> larger by whole pixels, so small text can be read (a Blit would blur it).</summary>
        private static Texture2D Enlarged(Texture2D source, float scale)
        {
            int w = source.width, h = source.height, sw = Mathf.RoundToInt(w * scale), sh = Mathf.RoundToInt(h * scale);
            Color32[] from = source.GetPixels32();
            var to = new Color32[sw * sh];
            for (int y = 0; y < sh; y++)
            {
                int row = y * h / sh * w;
                for (int x = 0; x < sw; x++) to[y * sw + x] = from[row + x * w / sw];
            }
            var output = new Texture2D(sw, sh, TextureFormat.RGB24, false);
            output.SetPixels32(to);
            output.Apply();
            return output;
        }

        private static readonly System.Type[] Interactive =
        {
            typeof(Button), typeof(BaseField<string>), typeof(BaseField<bool>), typeof(BaseField<int>), typeof(BaseField<float>),
            typeof(BaseField<System.Enum>), typeof(Foldout), typeof(Toggle), typeof(Slider), typeof(SliderInt), typeof(DropdownField),
        };

        /// <summary>The camera of the match or of a replay (which may not be tagged main).</summary>
        internal static Camera ViewCamera() => Camera.main != null ? Camera.main : Object.FindAnyObjectByType<Camera>();

        private static object ListUi(string filter, bool all, int max, bool compact)
        {
            var items = new System.Collections.Generic.List<object>();
            var panels = new System.Collections.Generic.Dictionary<IPanel, (float sx, float sy)>();
            var roots = new System.Collections.Generic.HashSet<VisualElement>();
            foreach (UIDocument doc in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                VisualElement root = doc.rootVisualElement;
                if (root == null || root.panel == null || !doc.isActiveAndEnabled) continue;
                Rect rb = root.worldBound;
                if (rb.width <= 0f || rb.height <= 0f) continue;
                float sx = Screen.width / rb.width, sy = Screen.height / rb.height; // panel units to screen pixels
                panels[root.panel] = (sx, sy);
                roots.Add(root);
                Visit(root, doc.name, sx, sy, filter, all, max, compact, items, null);
                if (items.Count >= max) break;
            }
            // Open dropdown menus sit on the panel's own tree, beside the documents.
            foreach (var panel in panels) Visit(panel.Key.visualTree, "popup", panel.Value.sx, panel.Value.sy, filter, all, max, compact, items, roots);
            // Playtest 2026-11-10: a list cut at max looked complete, so elements past it (a gift dialog's) seemed missing.
            return new { screenWidth = Screen.width, screenHeight = Screen.height, count = items.Count, truncated = items.Count >= max, elements = items };
        }

        private static void Visit(VisualElement e, string doc, float sx, float sy, string filter, bool all, int max, bool compact, System.Collections.Generic.List<object> items, System.Collections.Generic.HashSet<VisualElement> skip)
        {
            if (items.Count >= max || (skip != null && skip.Contains(e))) return;
            if (e.resolvedStyle.display == DisplayStyle.None || e.resolvedStyle.visibility == Visibility.Hidden || e.resolvedStyle.opacity <= 0.01f) return;
            Rect wb = e.worldBound;
            bool sized = wb.width > 0.5f && wb.height > 0.5f;
            if (sized)
            {
                string text = (e as TextElement)?.text ?? (e as BaseField<string>)?.value ?? "";
                if (e is Button bt && string.IsNullOrEmpty(text)) text = bt.text;
                bool interactive = false;
                foreach (System.Type t in Interactive) if (t.IsInstanceOfType(e)) { interactive = true; break; }
                bool named = !string.IsNullOrEmpty(e.name);
                bool include = all ? named || interactive || !string.IsNullOrEmpty(text) : interactive || (e is Label && !string.IsNullOrEmpty(text));
                bool scrolledAway = ScrolledAway(e, wb);
                if (include && (all || !scrolledAway) && (filter.Length == 0 || Contains(e.name, filter) || Contains(text, filter)))
                {
                    var rect = new[] { Mathf.RoundToInt(wb.xMin * sx), Mathf.RoundToInt(wb.yMin * sy), Mathf.RoundToInt(wb.xMax * sx), Mathf.RoundToInt(wb.yMax * sy) };
                    var center = new[] { (rect[0] + rect[2]) / 2, (rect[1] + rect[3]) / 2 };
                    bool covered = Covered(e, wb);
                    if (compact) items.Add($"{e.name} \"{text}\" {center[0]},{center[1]}{(covered ? " covered" : "")}{(scrolledAway ? " scrolled away" : "")}{(e.enabledInHierarchy ? "" : " disabled")}");
                    else items.Add(new
                    {
                        doc,
                        name = e.name,
                        type = e.GetType().Name,
                        text,
                        interactive,
                        enabled = e.enabledInHierarchy,
                        covered,
                        scrolledAway,
                        rect,
                        center,
                    });
                }
            }
            foreach (VisualElement child in e.Children()) Visit(child, doc, sx, sy, filter, all, max, compact, items, skip);
        }

        /// <summary>True when the element is wholly outside the viewport of a scroll view it sits in.</summary>
        private static bool ScrolledAway(VisualElement e, Rect bounds)
        {
            for (ScrollView s = e.GetFirstAncestorOfType<ScrollView>(); s != null; s = s.GetFirstAncestorOfType<ScrollView>())
                if (!s.contentViewport.worldBound.Overlaps(bounds)) return true;
            return false;
        }

        /// <summary>True when what the pointer would hit at the element's centre is in another branch: something is on top of it.</summary>
        private static bool Covered(VisualElement e, Rect bounds)
        {
            VisualElement top = e.panel?.Pick(bounds.center);
            return top != null && top != e && !e.Contains(top) && !top.Contains(e);
        }

        private static bool Contains(string s, string f) => s != null && s.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Set by <c>DebugOverlays</c>: toggles a layer and returns the enabled layers.</summary>
        public static System.Func<string, bool, JObject, object> OverlayHook;
    }
}
