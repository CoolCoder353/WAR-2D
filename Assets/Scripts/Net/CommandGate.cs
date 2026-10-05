using Mirror;

/// <summary>Placeholder until Task 12 adds rate limiting.</summary>
public static class CommandGate
{
    public static bool Allow(NetworkConnectionToClient conn, string command) => conn != null && conn.identity != null;
    public static void Forget(int connectionId) { }
}
