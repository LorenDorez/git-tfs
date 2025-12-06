using System;
using System.IO;
using System.Threading;
using GitTfs.Util;
using Xunit;

namespace GitTfs.Test.Util
{
    public class FileLockProviderTest : BaseTest, IDisposable
    {
        private readonly string _testLockFile;

        public FileLockProviderTest()
        {
            _testLockFile = Path.Combine(Path.GetTempPath(), $"test-lock-{Guid.NewGuid()}.lock");
        }

        [Fact]
        public void ShouldAcquireLockWhenNotHeld()
        {
            var provider = new FileLockProvider(_testLockFile);
            var lockInfo = new LockInfo { Workspace = "test", AcquiredBy = "test-user" };

            var acquired = provider.TryAcquireLock("test", TimeSpan.FromSeconds(5), lockInfo);

            Assert.True(acquired);
            Assert.True(File.Exists(_testLockFile));

            // Cleanup
            provider.ReleaseLock("test");
        }

        [Fact]
        public void ShouldNotAcquireLockWhenAlreadyHeld()
        {
            var provider1 = new FileLockProvider(_testLockFile);
            var provider2 = new FileLockProvider(_testLockFile);

            var lockInfo1 = new LockInfo { Workspace = "test", AcquiredBy = "user1" };
            var lockInfo2 = new LockInfo { Workspace = "test", AcquiredBy = "user2" };

            // First acquisition should succeed
            var acquired1 = provider1.TryAcquireLock("test", TimeSpan.FromSeconds(1), lockInfo1);
            Assert.True(acquired1);

            // Second acquisition should fail (timeout after 1 second)
            var acquired2 = provider2.TryAcquireLock("test", TimeSpan.FromSeconds(1), lockInfo2);
            Assert.False(acquired2);

            // Cleanup
            provider1.ReleaseLock("test");
        }

        [Fact]
        public void ShouldReleaseLock()
        {
            var provider = new FileLockProvider(_testLockFile);
            var lockInfo = new LockInfo { Workspace = "test", AcquiredBy = "test-user" };

            provider.TryAcquireLock("test", TimeSpan.FromSeconds(5), lockInfo);
            Assert.True(File.Exists(_testLockFile));

            provider.ReleaseLock("test");
            Assert.False(File.Exists(_testLockFile));
        }

        [Fact]
        public void ShouldGetLockInfo()
        {
            var provider = new FileLockProvider(_testLockFile);
            var originalLockInfo = new LockInfo
            {
                Workspace = "test-workspace",
                AcquiredBy = "test-user",
                PipelineId = "123",
                BuildNumber = "456"
            };

            provider.TryAcquireLock("test", TimeSpan.FromSeconds(5), originalLockInfo);

            var retrievedLockInfo = provider.GetLockInfo("test");
            Assert.NotNull(retrievedLockInfo);
            Assert.Equal("test-workspace", retrievedLockInfo.Workspace);
            Assert.Equal("test-user", retrievedLockInfo.AcquiredBy);
            Assert.Equal("123", retrievedLockInfo.PipelineId);
            Assert.Equal("456", retrievedLockInfo.BuildNumber);

            // Cleanup
            provider.ReleaseLock("test");
        }

        [Fact]
        public void ShouldForceUnlock()
        {
            var provider = new FileLockProvider(_testLockFile);
            var lockInfo = new LockInfo { Workspace = "test", AcquiredBy = "test-user" };

            provider.TryAcquireLock("test", TimeSpan.FromSeconds(5), lockInfo);
            Assert.True(File.Exists(_testLockFile));

            provider.ForceUnlock("test");
            Assert.False(File.Exists(_testLockFile));
        }

        [Fact]
        public void ShouldDetectStaleLock()
        {
            var provider = new FileLockProvider(_testLockFile);
            var lockInfo = new LockInfo
            {
                Workspace = "test",
                AcquiredBy = "test-user",
                AcquiredAt = DateTime.UtcNow.AddHours(-3) // 3 hours ago
            };

            // Manually create a stale lock
            using (var fs = new FileStream(_testLockFile, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(lockInfo);
                writer.Write(json);
            }

            var isStale = provider.IsStale("test", TimeSpan.FromHours(2));
            Assert.True(isStale);

            // Cleanup
            provider.ForceUnlock("test");
        }

        [Fact]
        public void ShouldNotDetectFreshLockAsStale()
        {
            var provider = new FileLockProvider(_testLockFile);
            var lockInfo = new LockInfo { Workspace = "test", AcquiredBy = "test-user" };

            provider.TryAcquireLock("test", TimeSpan.FromSeconds(5), lockInfo);

            var isStale = provider.IsStale("test", TimeSpan.FromHours(2));
            Assert.False(isStale);

            // Cleanup
            provider.ReleaseLock("test");
        }

        [Fact]
        public void ShouldCreateLockDirectoryIfNotExists()
        {
            var lockDir = Path.Combine(Path.GetTempPath(), $"lock-dir-{Guid.NewGuid()}");
            var lockFile = Path.Combine(lockDir, "test.lock");

            Assert.False(Directory.Exists(lockDir));

            var provider = new FileLockProvider(lockFile);
            var lockInfo = new LockInfo { Workspace = "test", AcquiredBy = "test-user" };

            provider.TryAcquireLock("test", TimeSpan.FromSeconds(5), lockInfo);

            Assert.True(Directory.Exists(lockDir));
            Assert.True(File.Exists(lockFile));

            // Cleanup
            provider.ReleaseLock("test");
            if (Directory.Exists(lockDir))
            {
                Directory.Delete(lockDir, true);
            }
        }

        public virtual void Dispose()
        {
            // Ensure lock file is cleaned up after each test
            if (File.Exists(_testLockFile))
            {
                try
                {
                    File.Delete(_testLockFile);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}
