using System.Text;
using Unity.Mathematics;

/// <summary>Validation of untrusted command arguments.</summary>
public static class CommandValidator
{
    public const int MaxBoxSpan = 256;
    public const int MaxNicknameLength = 24;

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
            sb.Append(c);
            if (sb.Length == MaxNicknameLength) break;
        }
        string result = sb.ToString().Trim();
        if (result.Length == 0) return false;
        clean = result;
        return true;
    }
}
