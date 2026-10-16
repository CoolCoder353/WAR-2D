using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace WAR2D.Dev.Logging
{
    public enum LogCategory : byte { Sim, Net, Replication, Steam, Lobby, Match, UI, Audio, Art, Replay, Dev, Unity }
    public enum LogLevel : byte { Debug, Info, Warn, Error }

    /// <summary>One log line.</summary>
    public struct LogEntry
    {
        public long Seq;
        public DateTime Utc;
        public int Frame;
        /// <summary>The sim tick when it was written, or -1.</summary>
        public int Tick;
        public LogCategory Category;
        public LogLevel Level;
        public string Message;
    }

    /// <summary>
    /// The game's structured log: a category and level per entry, an in-memory ring (the developer console and
    /// the debug API read it), a per-session file, and Unity's own log. Unity and Mirror messages are captured
    /// into <see cref="LogCategory.Unity"/>. Thread-safe; never call it from Burst jobs.
    /// </summary>
    public static class WarLog
    {
        private static readonly object Gate = new object();
        private static LogEntry[] ring = new LogEntry[8192];
        private static int head, count;
        private static long seq;
        private static readonly LogLevel[] Levels = NewLevels();
        private static LogFile file;
        [ThreadStatic] private static bool forwarding;

        /// <summary>Supplies the sim tick for new entries (set by the server's sim; null gives -1).</summary>
        public static Func<int> TickSource;

        /// <summary>False in tests: entries stay out of Unity's log (and so out of the test runner's log assertions).</summary>
        internal static bool ForwardToUnity = true;

        /// <summary>The newest entry's sequence number (0 when empty).</summary>
        public static long LastSeq { get { lock (Gate) return seq; } }

        public static void Debug(LogCategory c, string message) => Write(c, LogLevel.Debug, message);
        public static void Info(LogCategory c, string message) => Write(c, LogLevel.Info, message);
        public static void Warn(LogCategory c, string message) => Write(c, LogLevel.Warn, message);
        public static void Error(LogCategory c, string message) => Write(c, LogLevel.Error, message);

        public static LogLevel LevelOf(LogCategory c) => Levels[(int)c];

        /// <summary>Entries below <paramref name="level"/> in category <paramref name="c"/> are dropped.</summary>
        public static void SetLevel(LogCategory c, LogLevel level) => Levels[(int)c] = level;

        public static void Write(LogCategory c, LogLevel level, string message)
        {
            if (level < Levels[(int)c]) return;
            int tick = -1;
            try { tick = TickSource?.Invoke() ?? -1; } catch { }
            LogEntry e;
            lock (Gate)
            {
                e = new LogEntry
                {
                    Seq = ++seq, Utc = DateTime.UtcNow, Frame = MainThreadFrame, Tick = tick,
                    Category = c, Level = level, Message = message ?? "",
                };
                ring[(head + count) % ring.Length] = e;
                if (count < ring.Length) count++;
                else head = (head + 1) % ring.Length;
            }
            file?.Write(e);
            if (!ForwardToUnity || c == LogCategory.Unity) return;
            forwarding = true;
            try
            {
                string text = $"[{c}] {message}";
                if (level == LogLevel.Error) UnityEngine.Debug.LogError(text);
                else if (level == LogLevel.Warn) UnityEngine.Debug.LogWarning(text);
                else UnityEngine.Debug.Log(text);
            }
            finally { forwarding = false; }
        }

        /// <summary>The newest entries (oldest first), filtered.</summary>
        public static List<LogEntry> Tail(int max, LogCategory? category = null, LogLevel min = LogLevel.Debug, long afterSeq = 0)
        {
            var result = new List<LogEntry>();
            lock (Gate)
            {
                for (int i = count - 1; i >= 0 && result.Count < max; i--)
                {
                    LogEntry e = ring[(head + i) % ring.Length];
                    if (e.Seq <= afterSeq) break;
                    if (category.HasValue && e.Category != category.Value) continue;
                    if (e.Level < min) continue;
                    result.Add(e);
                }
            }
            result.Reverse();
            return result;
        }

        private static int mainThreadId = -1;

        /// <summary>The frame on the main thread; -1 from other threads (Time.frameCount is main-thread only).</summary>
        private static int MainThreadFrame => Thread.CurrentThread.ManagedThreadId == mainThreadId ? Time.frameCount : -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Boot()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            Application.logMessageReceivedThreaded -= Capture;
            Application.logMessageReceivedThreaded += Capture;
            if (!(Application.isEditor && Application.isBatchMode))
            {
                file?.Dispose();
                file = LogFile.Open(System.IO.Path.Combine(Application.persistentDataPath, "logs"));
            }
            Application.quitting -= Close;
            Application.quitting += Close;
        }

        private static void Close() { file?.Dispose(); file = null; }

        /// <summary>Unity's and Mirror's own messages, into the Unity category (skipping WarLog's own forwarding).</summary>
        private static void Capture(string condition, string stackTrace, LogType type)
        {
            if (forwarding) return;
            LogLevel level = type == LogType.Log ? LogLevel.Info : type == LogType.Warning ? LogLevel.Warn : LogLevel.Error;
            Write(LogCategory.Unity, level, level == LogLevel.Error && !string.IsNullOrEmpty(stackTrace) ? condition + "\n" + stackTrace : condition);
        }

        internal static void ResetForTests(int capacity = 8192)
        {
            lock (Gate) { ring = new LogEntry[capacity]; head = 0; count = 0; seq = 0; }
            Array.Copy(NewLevels(), Levels, Levels.Length);
            TickSource = null;
        }

        private static LogLevel[] NewLevels()
        {
            var levels = new LogLevel[Enum.GetValues(typeof(LogCategory)).Length];
            for (int i = 0; i < levels.Length; i++) levels[i] = LogLevel.Info;
            return levels;
        }
    }
}
