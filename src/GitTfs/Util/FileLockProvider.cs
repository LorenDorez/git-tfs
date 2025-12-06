using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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

            while (DateTime.UtcNow < endTime)
            {
                // Check if there's a stale lock and remove it
                if (File.Exists(_lockFilePath))
                {
                    var currentLockInfo = GetLockInfo(lockName);
                    if (currentLockInfo != null)
                    {
                        var lockAge = DateTime.UtcNow - currentLockInfo.AcquiredAt;
                        var maxAge = TimeSpan.FromSeconds(7200); // 2 hours default

                        if (lockAge > maxAge)
                        {
                            Trace.WriteLine($"WARNING: Stale lock detected and removed");
                            Trace.WriteLine($"  Lock age: {lockAge.TotalHours:F1}h");
                            Trace.WriteLine($"  Last held by: {currentLockInfo.Hostname} (PID {currentLockInfo.Pid})");
                            if (!string.IsNullOrEmpty(currentLockInfo.PipelineId))
                            {
                                Trace.WriteLine($"  Pipeline: {currentLockInfo.AcquiredBy} #{currentLockInfo.BuildNumber}");
                            }

                            ForceUnlock(lockName);
                        }
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

                        // Write lock info to file
                        var json = JsonSerializer.Serialize(lockInfo, new JsonSerializerOptions { WriteIndented = true });
                        writer.Write(json);
                    }

                    Trace.WriteLine($"✅ Lock acquired: {_lockFilePath}");
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
                Trace.WriteLine($"❌ Failed to acquire lock after {timeout.TotalSeconds}s");
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
                    Trace.WriteLine($"✅ Lock released: {_lockFilePath}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"⚠️ Warning: Failed to release lock: {ex.Message}");
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
                    Trace.WriteLine($"🔓 Force unlocked: {_lockFilePath}");
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
                return JsonSerializer.Deserialize<LockInfo>(json);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Warning: Failed to read lock info: {ex.Message}");
                return null;
            }
        }
    }
}
