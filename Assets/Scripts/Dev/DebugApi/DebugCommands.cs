namespace WAR2D.Dev.DebugApi
{
    /// <summary>Registers every API command.</summary>
    public static class DebugCommands
    {
        public static void RegisterAll(DebugRegistry r)
        {
            r.Add("ping", DebugRole.Any, "ping: answers pong", _ => "pong");
            r.Add("help", DebugRole.Any, "help: every command", _ =>
            {
                var list = new System.Collections.Generic.List<object>();
                foreach (DebugRegistry.Entry e in r.All) list.Add(new { name = e.Name, role = e.Role.ToString(), help = e.Help });
                return list;
            });
            ReadCommands.Register(r);
            SessionCommands.Register(r);
            ScenarioCommands.Register(r);
            ViewCommands.Register(r);
            ReplayCommands.Register(r);
            // Tasks 6-9 and 14 add: SessionCommands.Register(r); ReadCommands.Register(r); ScenarioCommands.Register(r);
            // ViewCommands.Register(r); ReplayCommands.Register(r);
        }
    }
}
