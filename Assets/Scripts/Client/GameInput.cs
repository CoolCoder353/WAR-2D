using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>Gameplay input actions, defined in code (rebinding UI comes in v0.6).</summary>
public static class GameInput
{
    private static InputActionMap map;

    public static InputActionMap Map
    {
        get
        {
            if (map == null) Build();
            return map;
        }
    }

    public static InputAction Pan => Map["Pan"];
    public static InputAction Zoom => Map["Zoom"];
    public static InputAction FastPan => Map["FastPan"];
    public static InputAction Select => Map["Select"];
    public static InputAction Command => Map["Command"];
    public static InputAction Rotate => Map["Rotate"];
    public static InputAction Point => Map["Point"];
    /// <summary>Held with a squad key to assign the selection to that squad (Ctrl).</summary>
    public static InputAction AssignModifier => Map["AssignModifier"];
    /// <summary>Held while box-selecting to add to the selection (Shift).</summary>
    public static InputAction AppendModifier => Map["AppendModifier"];
    /// <summary>Arms attack-move: the next left or right click sends AttackMove (A).</summary>
    public static InputAction AttackMove => Map["AttackMove"];
    /// <summary>Stops the selection (S).</summary>
    public static InputAction Stop => Map["Stop"];
    /// <summary>Holds the selection in place (H).</summary>
    public static InputAction Hold => Map["Hold"];
    /// <summary>Opens or closes the in-match menu (Esc).</summary>
    public static InputAction Menu => Map["Menu"];
    /// <summary>Held while ordering to queue the order after the current one (Shift).</summary>
    public static InputAction QueueModifier => Map["QueueModifier"];

    /// <summary>Squad keys: index 0 is key 1, index 9 is key 0.</summary>
    public static InputAction Squad(int index) => Map[$"Squad{(index + 1) % 10}"];

    /// <summary>True when the pointer is over a UI element (clicks there must not reach the world).</summary>
    public static bool PointerOverUI => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    /// <summary>Pointer position on the z = 0 world plane (orthographic camera).</summary>
    public static Vector3 PointerWorld()
    {
        Camera cam = Camera.main;
        if (cam == null) return Vector3.zero;
        Vector2 screen = Point.ReadValue<Vector2>();
        Vector3 world = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -cam.transform.position.z));
        world.z = 0f;
        return world;
    }

    private static void Build()
    {
        map = new InputActionMap("Gameplay");

        InputAction pan = map.AddAction("Pan", InputActionType.Value);
        // Arrow keys only: A, S and H are order keys.
        pan.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow")
            .With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");

        map.AddAction("Zoom", InputActionType.Value, "<Mouse>/scroll/y");
        map.AddAction("FastPan", InputActionType.Button, "<Keyboard>/shift");
        map.AddAction("Select", InputActionType.Button, "<Mouse>/leftButton");
        map.AddAction("Command", InputActionType.Button, "<Mouse>/rightButton");
        map.AddAction("Rotate", InputActionType.Button, "<Keyboard>/r");
        map.AddAction("Point", InputActionType.Value, "<Pointer>/position");
        map.AddAction("AssignModifier", InputActionType.Button, "<Keyboard>/ctrl");
        map.AddAction("AppendModifier", InputActionType.Button, "<Keyboard>/shift");
        map.AddAction("AttackMove", InputActionType.Button, "<Keyboard>/a");
        map.AddAction("Stop", InputActionType.Button, "<Keyboard>/s");
        map.AddAction("Hold", InputActionType.Button, "<Keyboard>/h");
        map.AddAction("QueueModifier", InputActionType.Button, "<Keyboard>/shift");
        map.AddAction("Menu", InputActionType.Button, "<Keyboard>/escape");
        for (int key = 0; key <= 9; key++) map.AddAction($"Squad{key}", InputActionType.Button, $"<Keyboard>/{key}");

        map.Enable();
    }
}
