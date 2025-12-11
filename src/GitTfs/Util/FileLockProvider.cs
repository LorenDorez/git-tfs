using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace GitTfs.Util
{
    /// <summary>
    /// File-based implementation of ILockProvider.
    /// Uses file system locks for synchronization between processes.
    /// </summary>
    public class FileLockProvider : ILockProvider
    {
        private readonly string _lockFilePath;
        private const int DefaultMaxLockAgeSeconds = 7200; // 2 hours

        public FileLockProvider(string lockFilePath)
        {
            if (string.IsNullOrWhiteSpace(lockFilePath))
                throw new ArgumentNullException(nameof(lockFilePath));

            _lockFilePath = lockFilePath;

            // Ensure lock directory exists
            var lockDir = Path.GetDirectoryName(_lockFilePath);
            if (!string.IsNullOrEmpty(lockDir) && !Directory.Exists(lockDir))
            {
                Directory.CreateDirectory(lockDir);
            }
        }

        public bool TryAcquireLock(string lockName, TimeSpan timeout, LockInfo lockInfo)
        {
            var startTime = DateTime.UtcNow;
            var endTime = startTime.Add(timeout);
            var defaultMaxLockAge = TimeSpan.FromSeconds(DefaultMaxLockAgeSeconds);

            while (DateTime.UtcNow < endTime)
            {
                // Check if there's a stale lock and remove it
                if (File.Exists(_lockFilePath))
                {
                    var currentLockInfo = GetLockInfo(lockName);
                    if (currentLockInfo != null && IsLockStale(currentLockInfo, defaultMaxLockAge))
                    {
                        LogStaleLockRemoval(currentLockInfo);
                        ForceUnlock(lockName);
                    }
                }

                // Try to acquire the lock
                try
                {
                    // Use FileStream with exclusive access to create the lock
                    using (var fs = new FileStream(_lockFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(fs))
                    {
                        // Set lock info
                        lockInfo.Pid = Process.GetCurrentProcess().Id;
                        lockInfo.Hostname = Environment.MachineName;
                        lockInfo.AcquiredAt = DateTime.UtcNow;

                        // Write lock info to file (simple JSON format)
                        var json = SerializeLockInfo(lockInfo);
                        writer.Write(json);
                    }

                    Trace.WriteLine($"[OK] Lock acquired: {_lockFilePath}");
                    return true;
                }
                catch (IOException)
                {
                    // Lock file already exists, wait and retry
                    var elapsed = DateTime.UtcNow - startTime;
                    var lockHolder = GetLockInfo(lockName);
                    if (lockHolder != null)
                    {
                        Trace.WriteLine($"Lock held by: {lockHolder.Hostname} (PID {lockHolder.Pid}), " +
                                      $"elapsed: {elapsed.TotalSeconds:F0}s / timeout: {timeout.TotalSeconds:F0}s");
                    }

                    Thread.Sleep(1000); // Wait 1 second before retrying
                }
            }

            // Timeout reached
            var finalLockInfo = GetLockInfo(lockName);
            if (finalLockInfo != null)
            {
                Trace.WriteLine($"[FAIL] Failed to acquire lock after {timeout.TotalSeconds}s");
                Trace.WriteLine($"   Lock held by: {finalLockInfo.Hostname} (PID {finalLockInfo.Pid})");
                Trace.WriteLine($"   Acquired at: {finalLockInfo.AcquiredAt:yyyy-MM-dd HH:mm:ss} UTC");
            }

            return false;
        }

        public void ReleaseLock(string lockName)
        {
            if (File.Exists(_lockFilePath))
            {
                try
                {
                    File.Delete(_lockFilePath);
                    Trace.WriteLine($"[OK] Lock released: {_lockFilePath}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[WARN] Warning: Failed to release lock: {ex.Message}");
                }
            }
        }

        public bool IsStale(string lockName, TimeSpan maxAge)
        {
            var lockInfo = GetLockInfo(lockName);
            if (lockInfo == null)
                return false;

            var age = DateTime.UtcNow - lockInfo.AcquiredAt;
            return age > maxAge;
        }

        public void ForceUnlock(string lockName)
        {
            if (File.Exists(_lockFilePath))
            {
                try
                {
                    File.Delete(_lockFilePath);
                    Trace.WriteLine($"[FORCE] Force unlocked: {_lockFilePath}");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to force unlock: {ex.Message}", ex);
                }
            }
        }

        public LockInfo GetLockInfo(string lockName)
        {
            if (!File.Exists(_lockFilePath))
                return null;

            try
            {
                var json = File.ReadAllText(_lockFilePath);
                return DeserializeLockInfo(json);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Warning: Failed to read lock info: {ex.Message}");
                return null;
            }
        }

        private string SerializeLockInfo(LockInfo lockInfo)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"Pid\": {lockInfo.Pid},");
            sb.AppendLine($"  \"Hostname\": \"{EscapeJson(lockInfo.Hostname)}\",");
            sb.AppendLine($"  \"AcquiredAt\": \"{lockInfo.AcquiredAt:O}\",");
            sb.AppendLine($"  \"AcquiredBy\": \"{EscapeJson(lockInfo.AcquiredBy)}\",");
            sb.AppendLine($"  \"PipelineId\": \"{EscapeJson(lockInfo.PipelineId)}\",");
            sb.AppendLine($"  \"BuildNumber\": \"{EscapeJson(lockInfo.BuildNumber)}\"");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private LockInfo DeserializeLockInfo(string json)
        {
            var lockInfo = new LockInfo();
            
            // Simple JSON parsing (matches our serialized format)
            var lines = json.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim().TrimEnd(',');
                if (trimmed.StartsWith("\"Pid\":"))
                {
                    var value = ExtractJsonValue(trimmed);
                    lockInfo.Pid = int.Parse(value);
                }
                else if (trimmed.StartsWith("\"Hostname\":"))
                {
                    lockInfo.Hostname = ExtractJsonStringValue(trimmed);
                }
                else if (trimmed.StartsWith("\"AcquiredAt\":"))
                {
                    var value = ExtractJsonStringValue(trimmed);
                    lockInfo.AcquiredAt = DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
                }
                else if (trimmed.StartsWith("\"AcquiredBy\":"))
                {
                    lockInfo.AcquiredBy = ExtractJsonStringValue(trimmed);
                }
                else if (trimmed.StartsWith("\"PipelineId\":"))
                {
                    lockInfo.PipelineId = ExtractJsonStringValue(trimmed);
                }
                else if (trimmed.StartsWith("\"BuildNumber\":"))
                {
                    lockInfo.BuildNumber = ExtractJsonStringValue(trimmed);
                }
            }
            
            return lockInfo;
        }

        private string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private string ExtractJsonValue(string line)
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex < 0)
                return "";
            return line.Substring(colonIndex + 1).Trim();
        }

        private string ExtractJsonStringValue(string line)
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex < 0)
                return "";
            var value = line.Substring(colonIndex + 1).Trim();
            // Remove quotes
            value = value.Trim('"');
            // Unescape
            return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private bool IsLockStale(LockInfo lockInfo, TimeSpan maxAge)
        {
            if (lockInfo == null)
                return false;

            var lockAge = DateTime.UtcNow - lockInfo.AcquiredAt;
            return lockAge > maxAge;
        }

        private void LogStaleLockRemoval(LockInfo lockInfo)
        {
            var lockAge = DateTime.UtcNow - lockInfo.AcquiredAt;
            Trace.WriteLine($"WARNING: Stale lock detected and removed");
            Trace.WriteLine($"  Lock age: {lockAge.TotalHours:F1}h");
            Trace.WriteLine($"  Last held by: {lockInfo.Hostname} (PID {lockInfo.Pid})");
            if (!string.IsNullOrEmpty(lockInfo.PipelineId))
            {
                Trace.WriteLine($"  Pipeline: {lockInfo.AcquiredBy} #{lockInfo.BuildNumber}");
            }
        }
    }
}
