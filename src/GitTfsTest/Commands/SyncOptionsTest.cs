using System;
using System.IO;
using GitTfs.Commands;
using GitTfs.Core;
using Xunit;

namespace GitTfs.Test.Commands
{
    public class SyncOptionsTest : BaseTest
    {
        [Fact]
        public void ShouldSetDefaultValues()
        {
            var options = new SyncOptions();

            Assert.Equal(300, options.LockTimeout);
            Assert.Equal(7200, options.MaxLockAge);
            Assert.True(options.AutoMerge);
            Assert.Equal("file", options.LockProvider);
        }

        [Fact]
        public void ShouldThrowExceptionWhenLockTimeoutExceedsMaximum()
        {
            var options = new SyncOptions
            {
                LockTimeout = 10800 // 3 hours, exceeds maximum of 2 hours
            };

            var exception = Assert.Throws<GitTfsException>(() => options.Validate());
            Assert.Contains("cannot exceed 7200 seconds", exception.Message);
        }

        [Fact]
        public void ShouldThrowExceptionWhenInitWorkspaceWithoutRequiredParameters()
        {
            var options = new SyncOptions
            {
                InitWorkspace = true
            };

            var exception = Assert.Throws<GitTfsException>(() => options.Validate());
            Assert.Contains("--init-workspace requires", exception.Message);
        }

        [Fact]
        public void ShouldThrowExceptionWhenBothFromTfvcAndToTfvcSpecified()
        {
            var options = new SyncOptions
            {
                FromTfvc = true,
                ToTfvc = true
            };

            var exception = Assert.Throws<GitTfsException>(() => options.Validate());
            Assert.Contains("Cannot specify both --from-tfvc and --to-tfvc", exception.Message);
        }

        [Fact]
        public void ShouldThrowExceptionWhenUnsupportedLockProviderSpecified()
        {
            var options = new SyncOptions
            {
                LockProvider = "redis"
            };

            var exception = Assert.Throws<GitTfsException>(() => options.Validate());
            Assert.Contains("Unsupported lock provider", exception.Message);
        }

        [Fact]
        public void ShouldAllowValidConfiguration()
        {
            var options = new SyncOptions
            {
                FromTfvc = true,
                LockTimeout = 600,
                MaxLockAge = 3600,
                WorkspaceRoot = "/tmp/test",
                WorkspaceName = "test-workspace"
            };

            // Should not throw
            options.Validate();
        }

        [Fact]
        public void ShouldAllowValidInitWorkspaceConfiguration()
        {
            var options = new SyncOptions
            {
                InitWorkspace = true,
                TfvcUrl = "https://dev.azure.com/org",
                TfvcPath = "$/Project/Main",
                GitRemoteUrl = "https://github.com/org/repo.git"
            };

            // Should not throw
            options.Validate();
        }
    }
}
