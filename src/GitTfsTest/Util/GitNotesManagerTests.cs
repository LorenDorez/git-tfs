using System;
using System.IO;
using GitTfs.Util;
using LibGit2Sharp;
using Xunit;

namespace GitTfs.Test.Util
{
    public class GitNotesManagerTests : IDisposable
    {
        private readonly string _tempPath;
        private readonly Repository _repository;
        private readonly GitNotesManager _notesManager;

        public GitNotesManagerTests()
        {
            // Create a temporary directory for the test repository
            _tempPath = Path.Combine(Path.GetTempPath(), "git-tfs-test-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempPath);
            
            // Initialize a git repository
            Repository.Init(_tempPath);
            _repository = new Repository(_tempPath);
            
            _notesManager = new GitNotesManager(_repository);
        }

        public void Dispose()
        {
            _repository?.Dispose();
            if (Directory.Exists(_tempPath))
            {
                Directory.Delete(_tempPath, true);
            }
        }

        [Fact]
        public void AddNote_WhenCommitExists_ThenNoteIsAdded()
        {
            // Arrange
            var commitSha = CreateTestCommit("Test commit");
            var tfsUrl = "http://tfs.example.com/tfs";
            var tfsPath = "$/MyProject/trunk";
            var changesetId = 12345;

            // Act
            _notesManager.AddNote(commitSha, tfsUrl, tfsPath, changesetId);

            // Assert
            var noteInfo = _notesManager.GetNote(commitSha);
            Assert.NotNull(noteInfo);
            Assert.Equal(changesetId, noteInfo.ChangesetId);
            Assert.Equal(tfsUrl, noteInfo.TfsUrl);
            Assert.Equal(tfsPath, noteInfo.TfsRepositoryPath);
        }

        [Fact]
        public void GetNote_WhenNoNoteExists_ThenReturnsNull()
        {
            // Arrange
            var commitSha = CreateTestCommit("Test commit without note");

            // Act
            var noteInfo = _notesManager.GetNote(commitSha);

            // Assert
            Assert.Null(noteInfo);
        }

        [Fact]
        public void AddNote_WhenNoteAlreadyExists_ThenNoteIsUpdated()
        {
            // Arrange
            var commitSha = CreateTestCommit("Test commit");
            var tfsUrl = "http://tfs.example.com/tfs";
            var tfsPath = "$/MyProject/trunk";
            var changesetId1 = 12345;
            var changesetId2 = 67890;

            // Act
            _notesManager.AddNote(commitSha, tfsUrl, tfsPath, changesetId1);
            _notesManager.AddNote(commitSha, tfsUrl, tfsPath, changesetId2);

            // Assert
            var noteInfo = _notesManager.GetNote(commitSha);
            Assert.NotNull(noteInfo);
            Assert.Equal(changesetId2, noteInfo.ChangesetId); // Should have the updated value
        }

        [Fact]
        public void GetNote_WhenCommitDoesNotExist_ThenReturnsNull()
        {
            // Arrange - use a constant for non-existent commit SHA
            const string NonExistentSha = "0000000000000000000000000000000000000000";

            // Act
            var noteInfo = _notesManager.GetNote(NonExistentSha);

            // Assert
            Assert.Null(noteInfo);
        }

        [Fact]
        public void AddNote_WithNullTfsPath_ThenHandlesCorrectly()
        {
            // Arrange
            var commitSha = CreateTestCommit("Test commit");
            var tfsUrl = "http://tfs.example.com/tfs";
            var changesetId = 12345;

            // Act
            _notesManager.AddNote(commitSha, tfsUrl, null, changesetId);

            // Assert
            var noteInfo = _notesManager.GetNote(commitSha);
            Assert.NotNull(noteInfo);
            Assert.Equal(changesetId, noteInfo.ChangesetId);
            Assert.Equal(tfsUrl, noteInfo.TfsUrl);
            Assert.Null(noteInfo.TfsRepositoryPath);
        }

        private string CreateTestCommit(string message)
        {
            // Create a test file
            var testFile = Path.Combine(_tempPath, "test.txt");
            File.WriteAllText(testFile, $"Test content - {Guid.NewGuid()}");
            
            // Stage the file
            LibGit2Sharp.Commands.Stage(_repository, "test.txt");
            
            // Create a commit
            var signature = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
            var commit = _repository.Commit(message, signature, signature);
            
            return commit.Sha;
        }
    }
}
