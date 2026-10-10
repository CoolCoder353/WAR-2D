using System.Collections.Generic;
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
    /// <summary>Held to drag the camera (middle mouse button).</summary>
    public static InputAction DragPan => Map["DragPan"];
    /// <summary>Opens or closes the in-match menu (Esc).</summary>
    public static InputAction Menu => Map["Menu"];
    /// <summary>Held while ordering to queue the order after the current one (Shift).</summary>
    public static InputAction QueueModifier => Map["QueueModifier"];

    /// <summary>Squad keys: index 0 is key 1, index 9 is key 0.</summary>
    public static InputAction Squad(int index) => Map[$"Squad{(index + 1) % 10}"];

    private static readonly List<RaycastResult> uiHits = new List<RaycastResult>();
    private static PointerEventData uiPointer;
    private static EventSystem uiSystem;
    private static int uiFrame = -1;
    private static bool overUI;

    /// <summary>
    /// True when the pointer is over a UI element (clicks there must not reach the world). Hits from a
    /// physics raycaster (Map_2's camera has one) are world objects, such as building and tilemap
    /// colliders, not UI: clicking a building must still select and order. Worked out once a frame.
    /// </summary>
    public static bool PointerOverUI
    {
        get
        {
            if (uiFrame == Time.frameCount) return overUI;
            uiFrame = Time.frameCount;
            overUI = false;
            EventSystem system = EventSystem.current;
            if (system == null || !system.IsPointerOverGameObject()) return false;
            if (uiPointer == null || uiSystem != system)
            {
                uiPointer = new PointerEventData(system);
                uiSystem = system;
            }
            uiPointer.Reset();
            uiPointer.position = Point.ReadValue<Vector2>();
            uiHits.Clear();
            system.RaycastAll(uiPointer, uiHits);
            foreach (RaycastResult hit in uiHits)
            {
                if (hit.module is PhysicsRaycaster) continue; // Physics2DRaycaster derives from it
                overUI = true;
                break;
            }
            return overUI;
        }
    }

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
        map.AddAction("DragPan", InputActionType.Button, "<Mouse>/middleButton");
        for (int key = 0; key <= 9; key++) map.AddAction($"Squad{key}", InputActionType.Button, $"<Keyboard>/{key}");

        map.Enable();
    }
}
