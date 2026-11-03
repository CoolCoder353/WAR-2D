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
            r.Add("camera", DebugRole.Any, "camera x y [zoom]: centre the camera (zoom = orthographic size)", a =>
            {
                Camera cam = Camera.main ?? throw new DebugError("no main camera");
                var controller = cam.GetComponent<Character.Character_Controler>();
                var p = new Vector2(DebugArgs.Float(a, "x"), DebugArgs.Float(a, "y"));
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
            r.Add("screenshot", DebugRole.Any, "screenshot [path]: save a PNG at the end of this frame, answer with its path", a =>
            {
                string path = DebugArgs.Str(a, "path", Path.Combine(Application.persistentDataPath, "screenshots", $"shot-{System.DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png"));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                ScreenCapture.CaptureScreenshot(path);
                return SessionCommands.Wait(() => File.Exists(path) && new FileInfo(path).Length > 0 ? Path.GetFullPath(path) : null, 10);
            });
            r.Add("overlay", DebugRole.Any, "overlay layer on [args]: toggle a debug overlay (Flow, Routes, Targets, Fog, Sectors, Hash, Footprints, Interest, Terrain)", a =>
                OverlayHook?.Invoke(DebugArgs.Str(a, "layer"), DebugArgs.Bool(a, "on"), a) ?? throw new DebugError("overlays not available in this build"));
        }

        /// <summary>Set by <c>DebugOverlays</c>: toggles a layer and returns the enabled layers.</summary>
        public static System.Func<string, bool, JObject, object> OverlayHook;
    }
}
