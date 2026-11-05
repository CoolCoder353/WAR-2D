using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace WAR2D.Dev.DebugApi
{
    /// <summary>
    /// Injected keyboard and mouse input (<c>key</c>, <c>text</c>, <c>mouse</c>, <c>scroll</c>). The events go through the Input
    /// System like real ones, so the game's hotkeys, box-select, orders, the minimap and UI Toolkit panels all react. Screen
    /// coordinates are pixels with the origin at the TOP left, the same as a <c>screenshot</c> image.
    /// </summary>
    public static class InputCommands
    {
        private static KeyboardState keys;
        private static MouseState mouseState;
        private static MonoBehaviour runner;
        private static bool configured;

        public static void Register(DebugRegistry r)
        {
            r.Add("key", DebugRole.Any, "key keys [action=tap|down|up] [hold=0.1]: press keys, e.g. keys=\"F1\" or [\"ctrl\",\"a\"] or \"ctrl+a\"; down/up leave them held/released", a =>
            {
                List<Key> list = ParseKeys(a["keys"]);
                string action = DebugArgs.Str(a, "action", "tap").ToLowerInvariant();
                float hold = DebugArgs.Float(a, "hold", 0.1f);
                if (action != "tap" && action != "down" && action != "up") throw new DebugError("action must be tap, down or up");
                return Run(KeyRoutine(list, action, hold));
            });
            r.Add("text", DebugRole.Any, "text text: type characters into the focused text field", a =>
            {
                string text = DebugArgs.Str(a, "text");
                Configure();
                Keyboard kb = GetKeyboard();
                foreach (char c in text) InputSystem.QueueTextEvent(kb, c);
                return "typed " + text.Length + " characters";
            });
            r.Add("mouse", DebugRole.Any, "mouse x y [space=screen|world|tile] [button=left|right|middle] [action=click|down|up|move|drag] [to=[x,y]] [hold=0.08] [duration=0.4]: move/click/drag the pointer (screen pixels are from the top left, as in a screenshot; the reply gives the pixels used, the same way up)", a =>
            {
                string space = DebugArgs.Str(a, "space", "screen").ToLowerInvariant();
                string action = DebugArgs.Str(a, "action", "click").ToLowerInvariant();
                string button = DebugArgs.Str(a, "button", "left").ToLowerInvariant();
                MouseButton mb = button == "left" ? MouseButton.Left : button == "right" ? MouseButton.Right : button == "middle" ? MouseButton.Middle : throw new DebugError("button must be left, right or middle");
                if (Array.IndexOf(new[] { "click", "down", "up", "move", "drag" }, action) < 0) throw new DebugError("action must be click, down, up, move or drag");
                Vector2 from = ToScreen(space, DebugArgs.Float(a, "x"), DebugArgs.Float(a, "y"));
                Vector2 to = from;
                if (action == "drag")
                {
                    if (!(a["to"] is JArray t) || t.Count != 2) throw new DebugError("drag needs to=[x, y]");
                    to = ToScreen(space, (float)t[0], (float)t[1]);
                }
                return Run(MouseRoutine(action, mb, from, to, DebugArgs.Float(a, "hold", 0.08f), DebugArgs.Float(a, "duration", 0.4f)), new { x = from.x, y = Screen.height - from.y, to = new { x = to.x, y = Screen.height - to.y }, button, action });
            });
            r.Add("scroll", DebugRole.Any, "scroll dy [x y] [space]: turn the wheel (positive zooms in); optionally at a position first", a =>
            {
                float dy = DebugArgs.Float(a, "dy");
                Vector2? at = DebugArgs.Has(a, "x") ? ToScreen(DebugArgs.Str(a, "space", "screen").ToLowerInvariant(), DebugArgs.Float(a, "x"), DebugArgs.Float(a, "y")) : (Vector2?)null;
                return Run(ScrollRoutine(dy, at));
            });
        }

        /// <summary>Releases everything this class holds down and restores the input settings it changed.</summary>
        public static void Reset()
        {
            if (!configured) return;
            if (runner != null) UnityEngine.Object.Destroy(runner.gameObject);
            runner = null;
            try
            {
                if (Keyboard.current != null) InputSystem.QueueStateEvent(Keyboard.current, default(KeyboardState));
                if (mouseState.buttons != 0 && Mouse.current != null) InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = mouseState.position });
            }
            catch (Exception) { /* the input system may already be gone */ }
            keys = default;
            mouseState = default;
            RestoreSettings();
            configured = false;
        }

        private sealed class Runner : MonoBehaviour { }

        /// <summary>Plays <paramref name="routine"/> over the next frames; the command answers when it finishes.</summary>
        private static DebugWait Run(IEnumerator routine, object result = null)
        {
            Configure();
            if (runner == null)
            {
                var go = new GameObject("DebugApiInput") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                runner = go.AddComponent<Runner>();
            }
            bool done = false;
            runner.StartCoroutine(Wrap(routine, () => done = true));
            return new DebugWait { Poll = () => done ? (result ?? "done") : null, TimeoutSeconds = 30f };
        }

        private static IEnumerator Wrap(IEnumerator inner, Action finished)
        {
            yield return inner;
            finished();
        }

        private static IEnumerator KeyRoutine(List<Key> list, string action, float hold)
        {
            Keyboard kb = GetKeyboard();
            if (action != "up") { foreach (Key k in list) keys.Set(k, true); InputSystem.QueueStateEvent(kb, keys); }
            if (action == "tap")
            {
                yield return new WaitForSecondsRealtime(hold);
                yield return null; // at least a frame, so wasPressedThisFrame and wasReleasedThisFrame land in different ones
            }
            if (action != "down") { foreach (Key k in list) keys.Set(k, false); InputSystem.QueueStateEvent(kb, keys); }
            yield return null;
        }

        private static IEnumerator MouseRoutine(string action, MouseButton button, Vector2 from, Vector2 to, float hold, float duration)
        {
            Mouse m = GetMouse();
            SetPosition(m, from);
            yield return null; // hover and the Point action settle before a button changes
            switch (action)
            {
                case "move": break;
                case "down": SetButton(m, button, true); break;
                case "up": SetButton(m, button, false); break;
                case "click":
                    SetButton(m, button, true);
                    yield return new WaitForSecondsRealtime(hold);
                    yield return null;
                    SetButton(m, button, false);
                    break;
                case "drag":
                    SetButton(m, button, true);
                    yield return null;
                    float t0 = Time.realtimeSinceStartup;
                    while (duration > 0f && Time.realtimeSinceStartup - t0 < duration)
                    {
                        SetPosition(m, Vector2.Lerp(from, to, (Time.realtimeSinceStartup - t0) / duration));
                        yield return null;
                    }
                    SetPosition(m, to);
                    yield return null;
                    SetButton(m, button, false);
                    break;
            }
            yield return null;
        }

        private static IEnumerator ScrollRoutine(float dy, Vector2? at)
        {
            Mouse m = GetMouse();
            if (at.HasValue) { SetPosition(m, at.Value); yield return null; }
            mouseState.scroll = new Vector2(0f, dy);
            InputSystem.QueueStateEvent(m, mouseState);
            yield return null;
            mouseState.scroll = Vector2.zero;
            InputSystem.QueueStateEvent(m, mouseState);
            yield return null;
        }

        private static void SetPosition(Mouse m, Vector2 inputPos)
        {
            mouseState.position = inputPos;
            InputSystem.QueueStateEvent(m, mouseState);
        }

        private static void SetButton(Mouse m, MouseButton b, bool down)
        {
            mouseState = mouseState.WithButton(b, down);
            InputSystem.QueueStateEvent(m, mouseState);
        }

        /// <summary>A position in <paramref name="space"/> as Input System pixels (origin bottom left).</summary>
        private static Vector2 ToScreen(string space, float x, float y)
        {
            switch (space)
            {
                case "screen": return new Vector2(x, Screen.height - y);
                case "world":
                case "tile":
                    Camera cam = ViewCommands.ViewCamera() ?? throw new DebugError("no camera (not in a match or a replay)");
                    Vector3 w = space == "tile" ? new Vector3(x + 0.5f, y + 0.5f, 0f) : new Vector3(x, y, 0f);
                    Vector3 s = cam.WorldToScreenPoint(w);
                    return new Vector2(s.x, s.y);
                default: throw new DebugError("space must be screen, world or tile");
            }
        }

        private static Keyboard GetKeyboard() => Keyboard.current ?? InputSystem.AddDevice<Keyboard>("DebugApiKeyboard");

        private static Mouse GetMouse() => Mouse.current ?? InputSystem.AddDevice<Mouse>("DebugApiMouse");

        private static List<Key> ParseKeys(JToken token)
        {
            var names = new List<string>();
            if (token is JArray arr) foreach (JToken t in arr) names.AddRange(((string)t).Split('+'));
            else if (token != null && token.Type == JTokenType.String) names.AddRange(((string)token).Split('+'));
            else throw new DebugError("keys must be a name or a list of names");
            var result = new List<Key>();
            foreach (string raw in names)
            {
                string n = raw.Trim();
                switch (n.ToLowerInvariant())
                {
                    case "ctrl": case "control": n = "LeftCtrl"; break;
                    case "shift": n = "LeftShift"; break;
                    case "alt": n = "LeftAlt"; break;
                    case "esc": n = "Escape"; break;
                    case "del": n = "Delete"; break;
                    case "return": n = "Enter"; break;
                    case ".": case "period": n = "Period"; break;
                    case ",": case "comma": n = "Comma"; break;
                    default: if (n.Length == 1 && char.IsDigit(n[0])) n = "Digit" + n; break;
                }
                if (!Enum.TryParse(n, true, out Key key) || key == Key.None) throw new DebugError($"unknown key '{raw}' (names are Input System Key values: A, Digit1, F1, Escape, Space, LeftShift, UpArrow ...)");
                result.Add(key);
            }
            if (result.Count == 0) throw new DebugError("no keys given");
            return result;
        }

        private static InputSettings.BackgroundBehavior? oldBackground;
#if UNITY_EDITOR
        private static InputSettings.EditorInputBehaviorInPlayMode? oldEditorBehavior;
#endif

        /// <summary>Lets injected input through when the game window is not focused (the editor's Game view, or a build in the background).</summary>
        private static void Configure()
        {
            if (configured) return;
            configured = true;
            InputSettings s = InputSystem.settings;
            if (s == null) return;
            oldBackground = s.backgroundBehavior;
            s.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
            oldEditorBehavior = s.editorInputBehaviorInPlayMode;
            s.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        }

        private static void RestoreSettings()
        {
            InputSettings s = InputSystem.settings;
            if (s == null) return;
            if (oldBackground.HasValue) s.backgroundBehavior = oldBackground.Value;
#if UNITY_EDITOR
            if (oldEditorBehavior.HasValue) s.editorInputBehaviorInPlayMode = oldEditorBehavior.Value;
            oldEditorBehavior = null;
#endif
            oldBackground = null;
        }
    }
}
