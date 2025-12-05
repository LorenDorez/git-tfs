using GitTfs.Core;
using GitTfs.Core.TfsInterop;
using GitTfs.Commands;
using GitTfs.Util;
using LibGit2Sharp;
using StructureMap;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace GitTfs.Test.Integration
{
    /// <summary>
    /// Tests for handling repositories with commits that don't have TFS changeset metadata (git notes or commit messages).
    /// This simulates scenarios like adding a .gitignore file after quick-clone.
    /// </summary>
    public class GitNotesWithNonTfsCommitsTests : BaseTest, IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly IntegrationHelper h = new IntegrationHelper();

        public GitNotesWithNonTfsCommitsTests(ITestOutputHelper output)
        {
            _output = output;
            h.SetupFake(_ => { });
            _output.WriteLine("Repository in folder: " + h.Workdir);
        }

        public void Dispose() => h.Dispose();

        [Fact]
        public void GetLastParentTfsCommits_WhenHeadHasNoNotes_ThenReturnsParentWithNotes()
        {
            // Arrange: Set up a fake TFS server
            h.SetupFake(r =>
            {
                r.Changeset(194604, "Initial commit from TFS", DateTime.Parse("2024-01-01 12:00:00 -05:00"))
                 .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject");
            });

            string tfsCommit = null;
            string nonTfsCommit = null;
            h.SetupGitRepo("repo", g =>
            {
                // Create a commit that looks like it came from TFS (using git notes)
                tfsCommit = g.Commit("Initial commit from TFS");
            });

            using (var repo = h.Repository("repo"))
            {
                var gitRepository = new GitRepository(repo.Info.WorkingDirectory, new Container(), null, new RemoteConfigConverter());
                
                // Add git note to simulate git-tfs adding metadata
                gitRepository.AddTfsNote(tfsCommit, "http://server/tfs", "$/MyProject/trunk", 194604);
                
                // Now commit a file directly to git (simulating adding .gitignore after quick-clone)
                using (var repo2 = h.Repository("repo"))
                {
                    var gitignorePath = Path.Combine(repo2.Info.WorkingDirectory, ".gitignore");
                    File.WriteAllText(gitignorePath, "*.obj\n*.exe\n");
                    LibGit2Sharp.Commands.Stage(repo2, ".gitignore");
                    var signature = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
                    nonTfsCommit = repo2.Commit("Add .gitignore", signature, signature).Sha;
                }
                
                // Act: Get the last TFS commits from HEAD (which should skip the .gitignore commit)
                var changesets = gitRepository.GetLastParentTfsCommits("HEAD");
                
                // Assert: Should find the TFS commit (with git note), not the .gitignore commit
                Assert.Single(changesets);
                Assert.Equal(tfsCommit, changesets.First().GitCommit);
                Assert.Equal(194604, changesets.First().ChangesetId);
            }
        }

        [Fact]
        public void GetLastParentTfsCommits_WhenMultipleNonTfsCommitsOnTop_ThenReturnsParentWithNotes()
        {
            // Arrange
            h.SetupFake(r =>
            {
                r.Changeset(100, "TFS commit", DateTime.Parse("2024-01-01 12:00:00 -05:00"))
                 .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject");
            });

            string tfsCommit = null;
            h.SetupGitRepo("repo", g =>
            {
                tfsCommit = g.Commit("Initial TFS commit");
            });

            using (var repo = h.Repository("repo"))
            {
                var gitRepository = new GitRepository(repo.Info.WorkingDirectory, new Container(), null, new RemoteConfigConverter());
                gitRepository.AddTfsNote(tfsCommit, "http://server/tfs", "$/MyProject", 100);
                
                // Add multiple commits without TFS metadata
                using (var repo2 = h.Repository("repo"))
                {
                    var signature = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
                    
                    File.WriteAllText(Path.Combine(repo2.Info.WorkingDirectory, ".gitignore"), "*.obj\n");
                    LibGit2Sharp.Commands.Stage(repo2, ".gitignore");
                    repo2.Commit("Add .gitignore", signature, signature);
                    
                    File.WriteAllText(Path.Combine(repo2.Info.WorkingDirectory, "README.md"), "# Project\n");
                    LibGit2Sharp.Commands.Stage(repo2, "README.md");
                    repo2.Commit("Add README", signature, signature);
                    
                    File.AppendAllText(Path.Combine(repo2.Info.WorkingDirectory, "README.md"), "\nMore docs\n");
                    LibGit2Sharp.Commands.Stage(repo2, "README.md");
                    repo2.Commit("Update README", signature, signature);
                }
                
                // Act
                var changesets = gitRepository.GetLastParentTfsCommits("HEAD");
                
                // Assert: Should skip all 3 non-TFS commits and find the TFS one
                Assert.Single(changesets);
                Assert.Equal(tfsCommit, changesets.First().GitCommit);
                Assert.Equal(100, changesets.First().ChangesetId);
            }
        }

        [Fact]
        public void InitHistory_WhenRemoteRefHasNonTfsCommitsOnTop_ThenFindsCorrectMaxChangeset()
        {
            // Arrange: Simulate a quick-clone followed by adding .gitignore
            h.SetupFake(r =>
            {
                r.Changeset(50, "TFS commit", DateTime.Parse("2024-01-01 12:00:00 -05:00"))
                 .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/file.txt");
            });

            string tfsCommit = null;
            h.SetupGitRepo("repo", g =>
            {
                tfsCommit = g.Commit("TFS changeset C50");
            });

            using (var repo = h.Repository("repo"))
            {
                var container = new Container();
                var gitRepository = new GitRepository(repo.Info.WorkingDirectory, container, null, new RemoteConfigConverter());
                
                // Add git note
                gitRepository.AddTfsNote(tfsCommit, "http://localhost:8888/tfs", "$/MyProject", 50);
                
                // Create TFS remote ref pointing to the TFS commit
                var remoteRef = "refs/remotes/tfs/default";
                repo.Refs.Add(remoteRef, tfsCommit);
                
                // Add a .gitignore commit on top
                var signature = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
                File.WriteAllText(Path.Combine(repo.Info.WorkingDirectory, ".gitignore"), "*.log\n");
                LibGit2Sharp.Commands.Stage(repo, ".gitignore");
                var gitignoreCommit = repo.Commit("Add .gitignore", signature, signature).Sha;
                
                // Update the remote ref to point to the .gitignore commit (simulating manual commit after fetch)
                repo.Refs.UpdateTarget(remoteRef, gitignoreCommit);
                
                // Create a mock remote
                var remoteInfo = new RemoteInfo
                {
                    Id = "default",
                    Url = "http://localhost:8888/tfs",
                    Repository = "$/MyProject"
                };
                
                var mockTfsHelper = new Mock<ITfsHelper>();
                mockTfsHelper.SetupAllProperties();
                mockTfsHelper.Object.Url = remoteInfo.Url;
                
                var globals = new Globals { Repository = gitRepository };
                var configPropertyLoader = new ConfigPropertyLoader(globals);
                var configProperties = new ConfigProperties(configPropertyLoader);
                
                var remote = new GitTfsRemote(remoteInfo, gitRepository, remoteInfo.RemoteOptions, globals, mockTfsHelper.Object, configProperties);
                
                // Act: Access MaxChangesetId which triggers InitHistory
                var maxChangesetId = remote.MaxChangesetId;
                var maxCommitHash = remote.MaxCommitHash;
                
                // Assert: Should find changeset 50 and the TFS commit SHA, not the .gitignore commit
                Assert.Equal(50, maxChangesetId);
                Assert.Equal(tfsCommit, maxCommitHash);
            }
        }

        [Fact]
        public void GetLastParentTfsCommits_WhenMergeCommitWithOneParentHavingNoNotes_ThenReturnsOnlyTfsParents()
        {
            // Arrange: Create a merge where one branch has TFS commits and one doesn't
            h.SetupFake(r =>
            {
                r.Changeset(1, "TFS commit", DateTime.Parse("2024-01-01 12:00:00 -05:00"))
                 .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject");
            });

            string c1 = null;
            string c2NonTfs = null;
            string c3 = null;
            h.SetupGitRepo("repo", g =>
            {
                c1 = g.Commit("TFS commit on master");
                g.CreateBranch("feature");
                c2NonTfs = g.Commit("Non-TFS commit on feature");
                g.Checkout("master");
                c3 = g.Commit("Another TFS commit on master");
                g.Merge("feature");
            });

            using (var repo = h.Repository("repo"))
            {
                var gitRepository = new GitRepository(repo.Info.WorkingDirectory, new Container(), null, new RemoteConfigConverter());
                
                // Add git notes only to commits that should have TFS metadata
                gitRepository.AddTfsNote(c1, "http://server/tfs", "$/MyProject", 1);
                gitRepository.AddTfsNote(c3, "http://server/tfs", "$/MyProject", 2);
                // c2NonTfs deliberately has NO git note
                
                // Act
                var changesets = gitRepository.GetLastParentTfsCommits("HEAD");
                
                // Assert: Should find c3 from master, but not c2NonTfs from feature branch
                Assert.Single(changesets);
                Assert.Equal(c3, changesets.First().GitCommit);
            }
        }

        [Fact]
        public void GetLastParentTfsCommits_WithLegacyCommitMessages_AndNonTfsCommitsOnTop_ThenFindsLegacyCommit()
        {
            // Arrange: Test backwards compatibility with commit message format
            h.SetupFake(r =>
            {
                r.Changeset(42, "Legacy commit", DateTime.Parse("2024-01-01 12:00:00 -05:00"))
                 .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject");
            });

            string legacyTfsCommit = null;
            h.SetupGitRepo("repo", g =>
            {
                // Commit with git-tfs-id in commit message (legacy format)
                legacyTfsCommit = g.Commit("Legacy TFS commit\n\ngit-tfs-id: [http://server/tfs]$/MyProject;C42");
            });

            using (var repo = h.Repository("repo"))
            {
                var gitRepository = new GitRepository(repo.Info.WorkingDirectory, new Container(), null, new RemoteConfigConverter());
                
                // Add non-TFS commit on top
                var signature = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
                File.WriteAllText(Path.Combine(repo.Info.WorkingDirectory, ".gitignore"), "*.tmp\n");
                LibGit2Sharp.Commands.Stage(repo, ".gitignore");
                repo.Commit("Add .gitignore", signature, signature);
                
                // Act
                var changesets = gitRepository.GetLastParentTfsCommits("HEAD");
                
                // Assert: Should skip .gitignore and find legacy commit
                Assert.Single(changesets);
                Assert.Equal(legacyTfsCommit, changesets.First().GitCommit);
                Assert.Equal(42, changesets.First().ChangesetId);
            }
        }
    }
}
