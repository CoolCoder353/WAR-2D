using WAR2D.Dev.Logging;
using System;
using UnityEngine.InputSystem;

/// <summary>
/// The player's key bindings: <c>GameInput</c>'s binding overrides as JSON (saved with the settings), and
/// conflict checks for the Controls settings.
/// </summary>
public static class Rebinding
{
    /// <summary>
    /// Actions that share a key on purpose or that the player never binds (Shift is Fast pan, Append and Queue
    /// order at once; the pointer and scroll are fixed): they never count as a conflict. The squad keys do,
    /// since a key bound to Stop would otherwise also select a squad.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> NotRebindable = new System.Collections.Generic.HashSet<string> { "FastPan", "AppendModifier", "Point", "Zoom", "DragPan" };

    /// <summary>Applies saved overrides to the map (empty or malformed JSON keeps the defaults).</summary>
    public static void Load(InputActionMap map, string json)
    {
        if (map == null) return;
        map.RemoveAllBindingOverrides();
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            map.LoadBindingOverridesFromJson(json);
        }
        catch (Exception e)
        {
            WarLog.Warn(LogCategory.UI, $"saved bindings could not be read ({e.Message}); using the defaults.");
            map.RemoveAllBindingOverrides();
        }
    }

    /// <summary>The map's overrides as JSON.</summary>
    public static string Save(InputActionMap map) => map == null ? "" : map.SaveBindingOverridesAsJson();

    /// <summary>
    /// The other action in the same map already using <paramref name="bindingPath"/> (as a single,
    /// non-composite binding), or null.
    /// </summary>
    public static InputAction ConflictWith(InputAction action, string bindingPath)
    {
        if (action?.actionMap == null || string.IsNullOrEmpty(bindingPath)) return null;
        foreach (InputAction other in action.actionMap.actions)
        {
            if (other == action || NotRebindable.Contains(other.name)) continue;
            foreach (InputBinding binding in other.bindings)
                if (!binding.isComposite && !binding.isPartOfComposite && SameControl(binding.effectivePath, bindingPath))
                    return other;
        }
        return null;
    }

    /// <summary>
    /// True when two binding paths name the same key. A modifier's plain path (<c>&lt;Keyboard&gt;/ctrl</c>, the defaults) covers its
    /// left and right keys, which is what a rebind records (bug review 2026-11-05: Stop rebound to Left Ctrl reported no conflict
    /// with a Ctrl default). Left and right are still different keys.
    /// </summary>
    public static bool SameControl(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(Plain(a), b, StringComparison.OrdinalIgnoreCase) || string.Equals(a, Plain(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A left or right modifier's path as its plain one (<c>/leftCtrl</c> to <c>/ctrl</c>); any other path as it is.</summary>
    private static string Plain(string path)
    {
        int slash = path.LastIndexOf('/');
        if (slash < 0) return path;
        string control = path.Substring(slash + 1);
        foreach (string side in Sides)
            if (control.StartsWith(side, StringComparison.OrdinalIgnoreCase) && control.Length > side.Length)
            {
                string rest = control.Substring(side.Length);
                foreach (string modifier in Modifiers)
                    if (string.Equals(rest, modifier, StringComparison.OrdinalIgnoreCase)) return path.Substring(0, slash + 1) + modifier;
            }
        return path;
    }

    private static readonly string[] Sides = { "left", "right" };
    private static readonly string[] Modifiers = { "ctrl", "shift", "alt", "meta" };

    /// <summary>Swaps the first bindings of two actions (the conflict's "Swap bindings").</summary>
    public static void Swap(InputAction a, InputAction b)
    {
        string pathA = a.bindings[0].effectivePath, pathB = b.bindings[0].effectivePath;
        a.ApplyBindingOverride(0, pathB);
        b.ApplyBindingOverride(0, pathA);
    }

    /// <summary>Overrides a <c>GameInput</c> action's first binding (tests and tools).</summary>
    public static void Override(string action, string path) => GameInput.Map[action].ApplyBindingOverride(0, path);

    /// <summary>A <c>GameInput</c> action's first binding path, with overrides.</summary>
    public static string PathOf(string action) => GameInput.Map[action].bindings[0].effectivePath;

    /// <summary>Drops every override (Reset to defaults).</summary>
    public static void ResetAll() => GameInput.Map.RemoveAllBindingOverrides();

    /// <summary>The game's overrides as JSON.</summary>
    public static string SaveGame() => Save(GameInput.Map);

    /// <summary>A key's name with control characters spelled out: on Linux the input system names Delete after its character, U+007F.</summary>
    public static string CleanDisplay(string display)
    {
        if (string.IsNullOrEmpty(display)) return display;
        return display.Replace("\u007f", "Delete").Replace("\b", "Backspace").Replace("\t", "Tab").Replace("\r", "Enter").Replace("\n", "Enter").Replace("\u001b", "Escape");
    }

    /// <summary>A binding as players read it ("A", "Left mouse", "Esc").</summary>
    public static string Display(InputAction action) =>
        action == null || action.bindings.Count == 0 ? "" : CleanDisplay(action.GetBindingDisplayString(0, InputBinding.DisplayStringOptions.DontIncludeInteractions));
}
