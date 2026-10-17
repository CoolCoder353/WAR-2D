namespace WAR2D.Dev.DebugApi
{
    public static class ReplayCommands
    {
        public static void Register(DebugRegistry r)
        {
            r.Add("record", DebugRole.Server, "record: the recorder's state", _ =>
            {
                var rec = WorldStateManager.Instance?.Recorder;
                return rec == null ? (object)new { recording = false } : new { recording = !rec.Stopped && !rec.Finished, stopped = rec.Stopped, finished = rec.Finished, path = rec.FinishedPath };
            });
            r.Add("replay", DebugRole.Any, "replay (open path | seek seconds | speed x | play | pause | stop | status)", a =>
            {
                string what = DebugArgs.Str(a, "do");
                var p = WAR2D.Client.Replay.ReplayPlayer.Instance;
                switch (what)
                {
                    case "open":
                        if (WAR2D.Client.Replay.ReplayPlayer.Open(DebugArgs.Str(a, "path"), out string error) == null) throw new DebugError(error);
                        return "open";
                    case "seek": (p ?? throw new DebugError("no replay open")).Seek(DebugArgs.Float(a, "seconds")); return p.Seconds;
                    case "speed": (p ?? throw new DebugError("no replay open")).Speed = Unity.Mathematics.math.clamp(DebugArgs.Float(a, "x"), 0.5f, 8f); return p.Speed;
                    case "play": (p ?? throw new DebugError("no replay open")).Playing = true; return "playing";
                    case "pause": (p ?? throw new DebugError("no replay open")).Playing = false; return "paused";
                    case "stop": p?.Close(); return "closed";
                    case "status": return p == null ? (object)new { open = false } : new { open = true, p.Seconds, p.DurationSeconds, p.Speed, p.Playing };
                    default: throw new DebugError("do must be open, seek, speed, play, pause, stop or status");
                }
            });
            r.Add("replays", DebugRole.Any, "replays: saved replays", _ => WAR2D.Net.Replay.ReplayLibrary.List());
        }
    }
}
