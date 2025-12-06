using System;

namespace GitTfs.Util
{
    /// <summary>
    /// Interface for distributed locking mechanisms.
    /// Allows for different locking strategies (file-based, Azure Blob, Redis, etc.)
    /// </summary>
    public interface ILockProvider
    {
        /// <summary>
        /// Attempts to acquire a lock with the given name, waiting up to the specified timeout.
        /// </summary>
        /// <param name="lockName">Unique name for the lock</param>
        /// <param name="timeout">Maximum time to wait for the lock</param>
        /// <param name="lockInfo">Information about who is acquiring the lock</param>
        /// <returns>True if lock was acquired, false otherwise</returns>
        bool TryAcquireLock(string lockName, TimeSpan timeout, LockInfo lockInfo);

        /// <summary>
        /// Releases a previously acquired lock.
        /// </summary>
        /// <param name="lockName">Name of the lock to release</param>
        void ReleaseLock(string lockName);

        /// <summary>
        /// Checks if a lock is stale (older than the maximum age).
        /// </summary>
        /// <param name="lockName">Name of the lock to check</param>
        /// <param name="maxAge">Maximum age before a lock is considered stale</param>
        /// <returns>True if the lock is stale, false otherwise</returns>
        bool IsStale(string lockName, TimeSpan maxAge);

        /// <summary>
        /// Forcibly removes a lock, regardless of who owns it.
        /// </summary>
        /// <param name="lockName">Name of the lock to remove</param>
        void ForceUnlock(string lockName);

        /// <summary>
        /// Gets information about the current lock holder.
        /// </summary>
        /// <param name="lockName">Name of the lock to check</param>
        /// <returns>Lock information, or null if no lock exists</returns>
        LockInfo GetLockInfo(string lockName);
    }

    /// <summary>
    /// Information about a lock holder.
    /// </summary>
    public class LockInfo
    {
        public string Version { get; set; } = "1.0";
        public int Pid { get; set; }
        public string Hostname { get; set; }
        public string Workspace { get; set; }
        public DateTime AcquiredAt { get; set; }
        public string AcquiredBy { get; set; }
        public string PipelineId { get; set; }
        public string BuildNumber { get; set; }
        public string Direction { get; set; }
    }
}
