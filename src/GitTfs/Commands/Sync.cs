using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using GitTfs.Core;
using GitTfs.Util;
using NDesk.Options;
using StructureMap;

namespace GitTfs.Commands
{
    [Pluggable("sync")]
    [Description("sync [options]\n\nRobust bidirectional synchronization between Git and TFVC")]
    [RequiresValidGitRepository(exempt: true)]  // Exempt because --init-workspace creates the repo
    public class Sync : GitTfsCommand
    {
        private readonly Globals _globals;
        private readonly Fetch _fetch;
        private readonly Rcheckin _rcheckin;
        private readonly SyncOptions _options;

        public Sync(Globals globals, Fetch fetch, Rcheckin rcheckin)
        {
            _globals = globals;
            _fetch = fetch;
            _rcheckin = rcheckin;
            _options = new SyncOptions();
        }

        public OptionSet OptionSet => _options.GetOptionSet();

        public int Run()
        {
            try
            {
                _options.Validate();

                // Handle --init-workspace first
                if (_options.InitWorkspace)
                {
                    return InitializeWorkspace();
                }

                // Verify git notes are enabled
                if (!VerifyGitNotesEnabled())
                {
                    return GitTfsExitCodes.InvalidArguments;
                }

                // Determine lock file path if not specified
                var lockFile = _options.LockFile;
                if (string.IsNullOrEmpty(lockFile) && !_options.NoLock)
                {
                    var workspaceRoot = _options.WorkspaceRoot ?? Directory.GetCurrentDirectory();
                    var workspaceName = _options.WorkspaceName ?? Path.GetFileName(_globals.GitDir ?? Directory.GetCurrentDirectory());
                    var agentWorkspace = Path.Combine(workspaceRoot, workspaceName);
                    lockFile = Path.Combine(agentWorkspace, "locks", $"{workspaceName}.lock");
                }

                // Acquire lock if needed
                ILockProvider lockProvider = null;
                var lockAcquired = false;
                var lockName = _options.WorkspaceName ?? "default";

                if (!_options.NoLock)
                {
                    lockProvider = new FileLockProvider(lockFile);
                    
                    var lockInfo = new LockInfo
                    {
                        Workspace = lockName,
                        AcquiredBy = Environment.GetEnvironmentVariable("BUILD_DEFINITIONNAME") ?? "Manual Sync",
                        PipelineId = Environment.GetEnvironmentVariable("BUILD_BUILDID") ?? "",
                        BuildNumber = Environment.GetEnvironmentVariable("BUILD_BUILDNUMBER") ?? "",
                        Direction = _options.FromTfvc ? "tfvc-to-git" : (_options.ToTfvc ? "git-to-tfvc" : "bidirectional")
                    };

                    Trace.WriteLine($"Attempting to acquire lock: {lockFile}");
                    lockAcquired = lockProvider.TryAcquireLock(lockName, TimeSpan.FromSeconds(_options.LockTimeout), lockInfo);

                    if (!lockAcquired)
                    {
                        Console.Error.WriteLine($"❌ Failed to acquire lock within {_options.LockTimeout} seconds");
                        return GitTfsExitCodes.ForceRequired;
                    }
                }

                try
                {
                    // Perform sync
                    return PerformSync();
                }
                finally
                {
                    // Release lock
                    if (lockAcquired && lockProvider != null)
                    {
                        lockProvider.ReleaseLock(lockName);
                    }
                }
            }
            catch (GitTfsException ex)
            {
                Console.Error.WriteLine(ex.Message);
                if (ex.RecommendedSolutions != null && ex.RecommendedSolutions.Any())
                {
                    Console.Error.WriteLine("\nRecommended solutions:");
                    foreach (var solution in ex.RecommendedSolutions)
                    {
                        Console.Error.WriteLine($"  - {solution}");
                    }
                }
                return GitTfsExitCodes.ExceptionThrown;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"❌ ERROR: {ex.Message}");
                Trace.WriteLine($"Stack trace: {ex.StackTrace}");
                return GitTfsExitCodes.ExceptionThrown;
            }
        }

        private bool VerifyGitNotesEnabled()
        {
            if (_globals.Repository == null)
            {
                // Skip check if repository doesn't exist yet (e.g., during init-workspace)
                return true;
            }

            var useNotes = _globals.Repository.GetConfig("git-tfs.use-notes");
            if (useNotes != "true")
            {
                Console.Error.WriteLine(@"
❌ ERROR: Git notes are required for 'git tfs sync' to function.

Git notes allow tracking TFS metadata without modifying commit messages,
which would change commit SHAs and break synchronization tracking.

To enable git notes:
  git config git-tfs.use-notes true

After enabling, you may need to:
  1. Re-clone/fetch to populate notes
  2. Push notes to remote: git push origin refs/notes/tfvc-sync
");
                return false;
            }

            return true;
        }

        private int PerformSync()
        {
            Console.WriteLine("Starting git-tfs sync...");
            
            // Ensure we're in a valid Git repository
            if (_globals.Repository == null)
            {
                Console.Error.WriteLine("❌ ERROR: Not a git repository. Run 'git tfs init' first or use --init-workspace.");
                return GitTfsExitCodes.InvalidArguments;
            }
            
            if (_options.DryRun)
            {
                Console.WriteLine("🔍 DRY RUN MODE - No changes will be made");
            }

            // Get current state
            var remote = _globals.Repository.ReadTfsRemote(_globals.RemoteId);
            if (remote == null)
            {
                Console.Error.WriteLine("❌ ERROR: No TFS remote found. Run 'git tfs init' first.");
                return GitTfsExitCodes.InvalidArguments;
            }

            Console.WriteLine($"TFS Remote: {remote.TfsRepositoryPath}");
            Console.WriteLine($"Current TFS changeset: C{remote.MaxChangesetId}");
            Console.WriteLine($"Current Git commit: {remote.MaxCommitHash?.Substring(0, 8)}");

            // Determine sync direction
            if (_options.FromTfvc)
            {
                return SyncFromTfvc(remote);
            }
            else if (_options.ToTfvc)
            {
                return SyncToTfvc(remote);
            }
            else
            {
                // Bidirectional sync
                return SyncBidirectional(remote);
            }
        }

        private int SyncFromTfvc(IGitTfsRemote remote)
        {
            Console.WriteLine("📥 Syncing from TFVC to Git...");

            if (_options.DryRun)
            {
                Console.WriteLine("Would fetch from TFVC and update Git");
                return GitTfsExitCodes.OK;
            }

            // Fetch from TFVC
            Trace.WriteLine("Fetching from TFVC...");
            var fetchResult = _fetch.Run(_globals.RemoteId);
            if (fetchResult != GitTfsExitCodes.OK)
            {
                Console.Error.WriteLine("❌ Fetch from TFVC failed");
                return fetchResult;
            }

            Console.WriteLine("✅ TFVC → Git sync completed successfully");
            return GitTfsExitCodes.OK;
        }

        private int SyncToTfvc(IGitTfsRemote remote)
        {
            Console.WriteLine("📤 Syncing from Git to TFVC...");

            if (_options.DryRun)
            {
                Console.WriteLine("Would check in Git commits to TFVC");
                return GitTfsExitCodes.OK;
            }

            // Check in to TFVC
            Trace.WriteLine("Checking in to TFVC...");
            var checkinResult = _rcheckin.Run();
            if (checkinResult != GitTfsExitCodes.OK)
            {
                Console.Error.WriteLine("❌ Checkin to TFVC failed");
                return checkinResult;
            }

            Console.WriteLine("✅ Git → TFVC sync completed successfully");
            return GitTfsExitCodes.OK;
        }

        private int SyncBidirectional(IGitTfsRemote remote)
        {
            Console.WriteLine("🔄 Performing bidirectional sync...");
            Console.WriteLine("   Note: This will sync TFVC → Git first, then Git → TFVC");

            if (_options.DryRun)
            {
                Console.WriteLine("Would perform bidirectional sync");
                return GitTfsExitCodes.OK;
            }

            // First, fetch from TFVC
            Console.WriteLine("\n📥 Step 1: Fetching from TFVC...");
            var fetchResult = _fetch.Run(_globals.RemoteId);
            if (fetchResult != GitTfsExitCodes.OK)
            {
                Console.Error.WriteLine("❌ Fetch from TFVC failed");
                return fetchResult;
            }

            // Then, check in to TFVC
            Console.WriteLine("\n📤 Step 2: Checking in to TFVC...");
            try
            {
                var checkinResult = _rcheckin.Run();
                if (checkinResult != GitTfsExitCodes.OK)
                {
                    Console.Error.WriteLine("❌ Checkin to TFVC failed");
                    return checkinResult;
                }
            }
            catch (GitTfsException ex)
            {
                // Check if this is the "no commits to checkin" scenario
                // This happens when there are no new Git commits since last sync
                if (ex.Message != null && 
                    (ex.Message.Contains("latest TFS commit should be parent") ||
                     ex.Message.Contains("No commits to checkin")))
                {
                    // This is expected when there are no new commits, not an error
                    Console.WriteLine("ℹ️  No new Git commits to sync to TFVC");
                }
                else
                {
                    // Re-throw if it's a different error
                    throw;
                }
            }

            Console.WriteLine("\n✅ Bidirectional sync completed successfully");
            return GitTfsExitCodes.OK;
        }

        private int InitializeWorkspace()
        {
            Console.WriteLine("🚀 Initializing workspace structure...");
            Console.WriteLine($"   Root directory: {_options.WorkspaceRoot ?? Directory.GetCurrentDirectory()}");
            Console.WriteLine($"   Workspace name: {_options.WorkspaceName ?? "default"}");
            
            var rootDir = _options.WorkspaceRoot ?? Directory.GetCurrentDirectory();
            var workspaceName = _options.WorkspaceName ?? "default";

            // Create directory structure under _workspace subfolder
            var workspaceDir = Path.Combine(rootDir, "_workspace");
            var toolsDir = Path.Combine(workspaceDir, "_tools", "git");
            var agentsDir = Path.Combine(workspaceDir, "_agents");
            var agentWorkspace = Path.Combine(agentsDir, workspaceName);
            var repoPath = Path.Combine(agentWorkspace, "repo");
            var locksDir = Path.Combine(agentWorkspace, "locks");

            // Create workspace directory structure
            Directory.CreateDirectory(workspaceDir);
            Directory.CreateDirectory(toolsDir);
            Directory.CreateDirectory(agentsDir);
            Console.WriteLine($"✅ Created workspace directory: {workspaceDir}");

            // Check for Git and optionally install
            var gitInstaller = new GitInstaller(Path.Combine(workspaceDir, "_tools"));
            string gitPath;
            if (!gitInstaller.IsGitAvailable(out gitPath))
            {
                Console.WriteLine("\n⚠️  Git not detected on this system");
                
                if (_options.AutoInstallGit)
                {
                    if (!gitInstaller.InstallGitPortable())
                    {
                        Console.WriteLine("\n❌ Failed to auto-install Git Portable");
                        Console.WriteLine("   Please install Git manually and try again.");
                        Console.WriteLine("   Download from: https://git-scm.com/download/win");
                        return GitTfsExitCodes.InvalidArguments;
                    }
                }
                else
                {
                    Console.WriteLine("   Git is required for git-tfs to function.");
                    Console.WriteLine("   Options:");
                    Console.WriteLine("     1. Run with --auto-install-git to automatically download Git Portable (~45MB)");
                    Console.WriteLine("     2. Install Git manually from: https://git-scm.com/download/win");
                    Console.WriteLine("     3. Add existing Git to PATH");
                    return GitTfsExitCodes.InvalidArguments;
                }
            }
            else
            {
                Console.WriteLine($"✅ Git detected: {gitPath}");
            }

            // Always create the agent workspace directory
            Directory.CreateDirectory(repoPath);
            Directory.CreateDirectory(locksDir);
            Console.WriteLine($"✅ Created agent workspace: {agentWorkspace}");
            Console.WriteLine($"   Repository directory: {repoPath}");

            // Create lock file path
            var lockFilePath = Path.Combine(locksDir, $"{workspaceName}.lock");
            Console.WriteLine($"   Lock file location: {lockFilePath}");

            Console.WriteLine("\n✅ Workspace initialization complete!");
            Console.WriteLine($"   Root directory: {rootDir}");
            Console.WriteLine($"   Workspace directory: {workspaceDir}");
            Console.WriteLine($"   Agent workspace: {agentWorkspace}");
            Console.WriteLine($"   Repository path: {repoPath}");
            Console.WriteLine($"   Tools directory: {Path.Combine(workspaceDir, "_tools")}");
            Console.WriteLine($"   Agents directory: {agentsDir}");
            Console.WriteLine($"   Lock directory: {locksDir}");
            
            Console.WriteLine("\nNext steps:");
            Console.WriteLine($"  1. cd {repoPath}");
            Console.WriteLine($"  2. ..\\..\\..\\..\\git-tfs.exe init <tfvc-url> <tfvc-path>");
            Console.WriteLine($"  3. ..\\..\\..\\..\\git-tfs.exe fetch");
            Console.WriteLine($"  4. git remote add origin <git-remote-url>");
            Console.WriteLine($"  5. git push origin main");
            Console.WriteLine($"  6. cd {rootDir}");
            Console.WriteLine($"  7. .\\git-tfs.exe sync --workspace-name={workspaceName}");
            
            Console.WriteLine("\nTo add another agent workspace:");
            Console.WriteLine($"  .\\git-tfs.exe sync --init-workspace --workspace-name=<NewWorkspaceName>");

            return GitTfsExitCodes.OK;
        }
    }
}
