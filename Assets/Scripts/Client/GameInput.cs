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
        pan.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
        pan.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow")
            .With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");

        map.AddAction("Zoom", InputActionType.Value, "<Mouse>/scroll/y");
        map.AddAction("FastPan", InputActionType.Button, "<Keyboard>/shift");
        map.AddAction("Select", InputActionType.Button, "<Mouse>/leftButton");
        map.AddAction("Command", InputActionType.Button, "<Mouse>/rightButton");
        map.AddAction("Rotate", InputActionType.Button, "<Keyboard>/r");
        map.AddAction("Point", InputActionType.Value, "<Pointer>/position");

        map.Enable();
    }
}
