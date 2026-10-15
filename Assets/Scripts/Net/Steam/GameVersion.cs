namespace WAR2D.Net.Steam
{
    /// <summary>The build's version, compared exactly between host and client (the wire format changes between builds).</summary>
    public static class GameVersion
    {
        public static string Current => UnityEngine.Application.version;

        public static bool IsCompatible(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a == b;
    }
}
