using System;
using UnityEngine.InputSystem;

/// <summary>
/// The player's key bindings: <c>GameInput</c>'s binding overrides as JSON (saved with the settings), and
/// conflict checks for the Controls settings.
/// </summary>
public static class Rebinding
{
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
            UnityEngine.Debug.LogWarning($"[Rebinding] saved bindings could not be read ({e.Message}); using the defaults.");
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
            if (other == action) continue;
            foreach (InputBinding binding in other.bindings)
                if (!binding.isComposite && !binding.isPartOfComposite && string.Equals(binding.effectivePath, bindingPath, StringComparison.OrdinalIgnoreCase))
                    return other;
        }
        return null;
    }

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

    /// <summary>A binding as players read it ("A", "Left mouse", "Esc").</summary>
    public static string Display(InputAction action) =>
        action == null || action.bindings.Count == 0 ? "" : action.GetBindingDisplayString(0, InputBinding.DisplayStringOptions.DontIncludeInteractions);
}
