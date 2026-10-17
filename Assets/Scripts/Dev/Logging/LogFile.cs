using System;
using System.IO;
using System.Linq;

namespace WAR2D.Dev.Logging
{
    /// <summary>One session's log file: <c>war2d-&lt;UTC time&gt;-&lt;pid&gt;.log</c>, keeping the newest <c>keep</c> files in its folder.</summary>
    public sealed class LogFile : IDisposable
    {
        private readonly StreamWriter writer;
        private readonly object gate = new object();

        /// <summary>The path of the file opened last (tests and the API's <c>info</c>).</summary>
        public static string LastPath { get; private set; }

        private LogFile(string path)
        {
            writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            LastPath = path;
        }

        /// <summary>Opens a new file in <paramref name="directory"/> and deletes the oldest beyond <paramref name="keep"/>.</summary>
        public static LogFile Open(string directory, int keep = 10)
        {
            Directory.CreateDirectory(directory);
            string name = $"war2d-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{System.Diagnostics.Process.GetCurrentProcess().Id}.log";
            var file = new LogFile(Path.Combine(directory, name));
            foreach (string old in Directory.GetFiles(directory, "war2d-*.log").OrderByDescending(p => p, StringComparer.Ordinal).Skip(keep))
            {
                try { File.Delete(old); } catch (IOException) { } // another process may hold it
            }
            return file;
        }

        /// <summary>Formats and appends one entry (thread-safe).</summary>
        public void Write(in LogEntry e)
        {
            string line = $"{e.Utc:HH:mm:ss.fff} f{e.Frame} t{e.Tick} {e.Level,-5} [{e.Category}] {e.Message}";
            lock (gate) writer.WriteLine(line);
        }

        public void Dispose()
        {
            lock (gate) writer.Dispose();
        }
    }
}
