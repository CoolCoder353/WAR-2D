using System.Globalization;
using System.Text;
using Unity.Mathematics;

/// <summary>Validation of untrusted command arguments.</summary>
public static class CommandValidator
{
    public const int MaxBoxSpan = 256;
    public const int MaxNicknameLength = 24;

    /// <summary>Squad numbers are 0..9 (keys 1..0).</summary>
    public static bool IsSquadIndexValid(int squad) => squad >= 0 && squad < WAR2D.Sim.Squads.Count;

    /// <summary>Order kinds are the <see cref="OrderKind"/> values 0..7.</summary>
    public static bool IsOrderKindValid(byte kind) => kind <= (byte)OrderKind.Detonate;

    /// <summary>True for orders that name a tile: moves and work.</summary>
    public static bool TargetsTile(OrderKind kind) =>
        kind == OrderKind.Move || kind == OrderKind.AttackMove || kind == OrderKind.Build || kind == OrderKind.Repair || kind == OrderKind.Dig;

    /// <summary>A known order kind, with a goal inside the inclusive bounds when the order moves.</summary>
    public static bool IsOrderValid(byte kind, int2 goal, int2 min, int2 max)
    {
        if (!IsOrderKindValid(kind)) return false;
        OrderKind order = (OrderKind)kind;
        if (order == OrderKind.Dig) return IsInside(goal, min + 1, max - 1); // never the outer ring (the border)
        return !TargetsTile(order) || IsInside(goal, min, max);
    }

    /// <summary>
    /// A Dig drag's box from its two corner tiles: clamped to the map's inside (never the border ring) and to
    /// <paramref name="maxSide"/> tiles a side, measured from <paramref name="from"/> (where the drag began).
    /// False when the drag began outside the map.
    /// </summary>
    public static bool TryDigArea(int2 from, int2 to, int2 min, int2 max, int maxSide, out int2 lo, out int2 hi)
    {
        lo = hi = default;
        if (maxSide < 1 || !IsInside(from, min, max)) return false;
        int2 inMin = min + 1, inMax = max - 1;
        if (math.any(inMin > inMax)) return false;
        from = math.clamp(from, inMin, inMax);
        to = math.clamp(to, from - (maxSide - 1), from + (maxSide - 1));
        to = math.clamp(to, inMin, inMax);
        lo = math.min(from, to);
        hi = math.max(from, to);
        return true;
    }

    /// <summary>True when the tile is inside the inclusive bounds.</summary>
    public static bool IsInside(int2 tile, int2 min, int2 max) =>
        tile.x >= min.x && tile.y >= min.y && tile.x <= max.x && tile.y <= max.y;

    public static bool IsBoxValid(int2 a, int2 b)
    {
        long dx = (long)a.x - b.x;
        long dy = (long)a.y - b.y;
        return System.Math.Abs(dx) <= MaxBoxSpan && System.Math.Abs(dy) <= MaxBoxSpan;
    }

    /// <summary>Strips control characters and TMP rich-text brackets, trims and truncates.</summary>
    public static bool TrySanitizeNickname(string raw, out string clean)
    {
        clean = null;
        if (raw == null) return false;
        var sb = new StringBuilder(MaxNicknameLength);
        foreach (char c in raw.Trim())
        {
            if (char.IsControl(c) || c == '<' || c == '>' || char.IsSurrogate(c)) continue;
            // Format characters (zero-width spaces, right-to-left overrides) make names invisible or spoofable.
            UnicodeCategory category = char.GetUnicodeCategory(c);
            if (category == UnicodeCategory.Format || category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator) continue;
            sb.Append(c);
            if (sb.Length == MaxNicknameLength) break;
        }
        string result = sb.ToString().Trim();
        if (result.Length == 0) return false;
        clean = result;
        return true;
    }
}
