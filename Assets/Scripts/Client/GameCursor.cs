using UnityEngine;
using WAR2D.Art;

/// <summary>
/// The approved v0.8 pixel cursors (<see cref="PixelIcons"/>, drawn at 2×): Attack while attack-move is armed,
/// Move while a move is armed, Invalid over a spot the server refused for the building preview, and the
/// pointer otherwise. Sets the OS cursor only when the choice changes.
/// </summary>
public sealed class GameCursor : MonoBehaviour
{
    /// <summary>Cursor scale: 16 px art at 2× (32 px), the size OS cursors expect.</summary>
    public const int Scale = 2;

    private string shown;

    /// <summary>The cursor id for the current client state.</summary>
    public static string Choose(OrderKind? armed, bool placing, bool? previewValid)
    {
        if (placing && previewValid == false) return "CursorInvalid";
        if (armed == OrderKind.AttackMove) return "CursorAttack";
        if (armed == OrderKind.Move) return "CursorMove";
        return "CursorDefault";
    }

    private void Update()
    {
        BuildingPlacement placement = BuildingPlacement.Instance;
        string id = Choose(UnitCommander.Instance != null ? UnitCommander.Instance.ArmedOrder : null,
            placement != null && placement.IsPlacing, placement != null ? placement.PreviewValid : null);
        if (id == shown) return;
        shown = id;
        // The pointer's tip is its top-left pixel; the others aim with their centre.
        Vector2 hotspot = id == "CursorDefault" ? Vector2.zero : new Vector2(PixelIcons.Size * Scale / 2f, PixelIcons.Size * Scale / 2f);
        Cursor.SetCursor(PixelIcons.Texture(id, Scale), hotspot, CursorMode.Auto);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (Application.isBatchMode) return; // headless servers have no cursor
        var go = new GameObject("GameCursor");
        DontDestroyOnLoad(go);
        go.AddComponent<GameCursor>();
    }
}
