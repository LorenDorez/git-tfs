using System;
using System.IO;
using System.Linq;
using GitTfs.Core;
using LibGit2Sharp;
using StructureMap;
using Xunit;

namespace GitTfs.Test.Core
{
    public class GitRepositoryConfigureNotesTests : IDisposable
    {
        private readonly string _tempPath;
        private readonly Repository _libGit2Repository;
        private readonly GitRepository _gitRepository;

        public GitRepositoryConfigureNotesTests()
        {
            // Create a temporary directory for the test repository
            _tempPath = Path.Combine(Path.GetTempPath(), "git-tfs-test-notes-config-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempPath);

            // Initialize a git repository
            Repository.Init(_tempPath);
            _libGit2Repository = new Repository(_tempPath);

            // Create a dummy remote
            _libGit2Repository.Network.Remotes.Add("origin", "https://github.com/test/test.git");

            _gitRepository = new GitRepository(_tempPath, new Container(), new Globals(), new RemoteConfigConverter());
        }

        public void Dispose()
        {
            _libGit2Repository?.Dispose();
            if (Directory.Exists(_tempPath))
            {
                try
                {
                    Directory.Delete(_tempPath, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }

        [Fact]
        public void ConfigureRemoteToSyncNotes_WhenRemoteExists_ThenAddsNotesRefspecs()
        {
            // Act
            _gitRepository.ConfigureRemoteToSyncNotes("origin");

            // Assert
            var fetchRefspecs = _libGit2Repository.Config.Find("remote.origin.fetch", ConfigurationLevel.Local).ToList();
            var pushRefspecs = _libGit2Repository.Config.Find("remote.origin.push", ConfigurationLevel.Local).ToList();

            Assert.Contains(fetchRefspecs, rs => rs.Value == "+refs/notes/tfvc-sync:refs/notes/tfvc-sync");
            Assert.Contains(pushRefspecs, rs => rs.Value == "+refs/notes/tfvc-sync:refs/notes/tfvc-sync");
        }

        [Fact]
        public void ConfigureRemoteToSyncNotes_WhenCalledTwice_ThenDoesNotDuplicateRefspecs()
        {
            // Act
            _gitRepository.ConfigureRemoteToSyncNotes("origin");
            _gitRepository.ConfigureRemoteToSyncNotes("origin");

            // Assert
            var fetchRefspecs = _libGit2Repository.Config.Find("remote.origin.fetch", ConfigurationLevel.Local).ToList();
            var pushRefspecs = _libGit2Repository.Config.Find("remote.origin.push", ConfigurationLevel.Local).ToList();

            var notesRefspec = "+refs/notes/tfvc-sync:refs/notes/tfvc-sync";
            var fetchCount = fetchRefspecs.Count(rs => rs.Value == notesRefspec);
            var pushCount = pushRefspecs.Count(rs => rs.Value == notesRefspec);

            Assert.Equal(1, fetchCount);
            Assert.Equal(1, pushCount);
        }

        [Fact]
        public void ConfigureRemoteToSyncNotes_WhenRemoteDoesNotExist_ThenDoesNothing()
        {
            // Act - should not throw
            _gitRepository.ConfigureRemoteToSyncNotes("nonexistent");

            // Assert
            var fetchRefspecs = _libGit2Repository.Config.Find("remote.nonexistent.fetch", ConfigurationLevel.Local).ToList();
            var pushRefspecs = _libGit2Repository.Config.Find("remote.nonexistent.push", ConfigurationLevel.Local).ToList();

            Assert.Empty(fetchRefspecs);
            Assert.Empty(pushRefspecs);
        }

        [Fact]
        public void ConfigureRemoteToSyncNotes_WhenGitNotesDisabled_ThenDoesNotAddRefspecs()
        {
            // Arrange
            _gitRepository.SetConfig("git-tfs.use-notes", false);

            // Act
            _gitRepository.ConfigureRemoteToSyncNotes("origin");

            // Assert
            var fetchRefspecs = _libGit2Repository.Config.Find("remote.origin.fetch", ConfigurationLevel.Local).ToList();
            var pushRefspecs = _libGit2Repository.Config.Find("remote.origin.push", ConfigurationLevel.Local).ToList();

            var notesRefspec = "+refs/notes/tfvc-sync:refs/notes/tfvc-sync";
            Assert.DoesNotContain(fetchRefspecs, rs => rs.Value == notesRefspec);
            Assert.DoesNotContain(pushRefspecs, rs => rs.Value == notesRefspec);
        }
    }
}
