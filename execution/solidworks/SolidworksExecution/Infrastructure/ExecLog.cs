using System;
using System.IO;

namespace SolidworksExecution.Infrastructure
{
    // Minimal file logger. Writes next to the exe as execution.log.
    // Must NEVER throw — logging failures are swallowed.
    public static class ExecLog
    {
        private static readonly object _lock = new object();

        public static readonly string FilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "execution.log");

        public static void Write(string message)
        {
            try
            {
                lock (_lock)
                {
                    // rotate at 5 MB: execution.log → execution.log.1 (one previous file kept)
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 5L * 1024 * 1024)
                    {
                        File.Copy(FilePath, FilePath + ".1", true);
                        File.Delete(FilePath);
                    }
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch
            {
                // logging must never break execution
            }
        }
    }
}
