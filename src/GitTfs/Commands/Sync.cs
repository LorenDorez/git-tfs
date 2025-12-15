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
    // Note: Does not use [RequiresValidGitRepository] because --init-workspace creates the repo
    public class Sync : GitTfsCommand
    {
        private readonly Globals _globals;
        private readonly Fetch _fetch;
        private readonly SyncCheckin _syncCheckin;
        private readonly Clone _clone;
        private readonly QuickClone _quickClone;
        private readonly Init _init;
        private readonly InitOptions _initOptions;
        private readonly SyncOptions _options;

        public Sync(Globals globals, Fetch fetch, SyncCheckin syncCheckin, Clone clone, QuickClone quickClone, Init init, InitOptions initOptions)
        {
            _globals = globals;
            _fetch = fetch;
            _syncCheckin = syncCheckin;
            _clone = clone;
            _quickClone = quickClone;
            _init = init;
            _initOptions = initOptions;
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

                // Handle workspace navigation if --workspace-name is specified
                if (!string.IsNullOrEmpty(_options.WorkspaceName))
                {
                    var workspaceRoot = _options.WorkspaceRoot ?? FindWorkspaceRoot();
                    if (workspaceRoot == null)
                    {
                        Console.Error.WriteLine("❌ ERROR: Could not find workspace root directory. Use --workspace-root to specify it.");
                        return GitTfsExitCodes.InvalidArguments;
                    }

                    // Navigate to the agent workspace repo directory
                    var repoPath = Path.Combine(workspaceRoot, "_workspace", "_agents", _options.WorkspaceName, "repo");
                    if (!Directory.Exists(repoPath))
                    {
                        Console.Error.WriteLine($"❌ ERROR: Workspace repository not found: {repoPath}");
                        Console.Error.WriteLine($"   Run 'git tfs sync --init-workspace --workspace-name={_options.WorkspaceName}' first.");
                        return GitTfsExitCodes.InvalidArguments;
                    }

                    Trace.WriteLine($"Changing to workspace repository: {repoPath}");
                    Directory.SetCurrentDirectory(repoPath);

                    // CRITICAL FIX: Reinitialize the repository object after changing directories
                    // This ensures all git operations happen in the correct repository
                    _globals.GitDir = Path.Combine(repoPath, ".git");
                    _globals.Repository = _init.GitHelper.MakeRepository(_globals.GitDir);
                    Trace.WriteLine($"Repository reinitialized for workspace at: {repoPath}");
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
                    var workspaceRoot = _options.WorkspaceRoot ?? FindWorkspaceRoot() ?? Directory.GetCurrentDirectory();
                    var workspaceName = _options.WorkspaceName ?? Path.GetFileName(_globals.GitDir ?? Directory.GetCurrentDirectory());
                    var agentWorkspace = Path.Combine(workspaceRoot, "_workspace", "_agents", workspaceName);
                    lockFile = Path.Combine(agentWorkspace, $"{workspaceName}.lock");
                }

                // Acquire lock if needed
                ILockProvider lockProvider = null;
                var lockAcquired = false;
                var lockName = _options.WorkspaceName ?? "default";

                if (!_options.NoLock)
                {
                    lockProvider = new FileLockProvider(lockFile);
                    
                    // Handle --force-unlock: forcibly remove the lock before attempting to acquire it
                    if (_options.ForceUnlock)
                    {
                        Console.WriteLine($"⚠️  Force-unlocking workspace '{lockName}'...");
                        var existingLockInfo = lockProvider.GetLockInfo(lockName);
                        if (existingLockInfo != null)
                        {
                            Console.WriteLine($"   Removing lock held by: {existingLockInfo.Hostname} (PID {existingLockInfo.Pid})");
                            Console.WriteLine($"   Lock acquired at: {existingLockInfo.AcquiredAt:yyyy-MM-dd HH:mm:ss} UTC");
                            if (!string.IsNullOrEmpty(existingLockInfo.PipelineId))
                            {
                                Console.WriteLine($"   Pipeline: {existingLockInfo.AcquiredBy} #{existingLockInfo.BuildNumber}");
                            }
                        }
                        
                        try
                        {
                            lockProvider.ForceUnlock(lockName);
                            Console.WriteLine("✅ Lock removed successfully");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"❌ Failed to force unlock: {ex.Message}");
                            return GitTfsExitCodes.ExceptionThrown;
                        }
                    }
                    
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

        private string FindWorkspaceRoot()
        {
            // Search for _workspace directory by walking up the directory tree
            var currentDir = Directory.GetCurrentDirectory();
            var dir = new DirectoryInfo(currentDir);

            while (dir != null)
            {
                var workspaceDir = Path.Combine(dir.FullName, "_workspace");
                if (Directory.Exists(workspaceDir))
                {
                    Trace.WriteLine($"Found workspace root: {dir.FullName}");
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            Trace.WriteLine("Workspace root not found in directory tree");
            return null;
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

            // Step 1: Pull latest commits from Git remote (to get commits pushed by other agents/users)
            Console.WriteLine("\n📥 Pulling latest commits from Git remote...");
            var pullResult = PullFromGitRemote();
            if (pullResult != GitTfsExitCodes.OK)
            {
                Console.Error.WriteLine("❌ Failed to pull from Git remote");
                return pullResult;
            }

            // Step 2: Check in all untracked commits to TFVC using sync-checkin (preserves commit SHAs)
            Console.WriteLine("\n📝 Checking in untracked commits to TFVC...");
            
            // CRITICAL FIX: Refresh repository again before sync-checkin
            // SyncCheckin uses its own injected Globals instance, so we need to refresh it
            Trace.WriteLine("Refreshing repository before sync-checkin to ensure notes are visible...");
            _globals.Repository = _init.GitHelper.MakeRepository(_globals.GitDir);
            Trace.WriteLine("Repository refreshed for sync-checkin");
            
            // CRITICAL FIX: Skip the pre-checkin fetch in SyncCheckin since we're in a git-to-tfvc only sync
            // This prevents unnecessary fetches that could overwrite notes
            Environment.SetEnvironmentVariable("GIT_TFS_SKIP_PRECHECKIN_FETCH", "true");
            
            try
            {
                var checkinResult = _syncCheckin.Run();
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
                    Console.WriteLine("ℹ️  No new commits to sync to TFVC");
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_TFS_SKIP_PRECHECKIN_FETCH", null);
            }

            // Step 3: Push commits and git-notes back to Git remote
            Console.WriteLine("\n📤 Pushing commits and git-notes to Git remote...");
            var pushResult = PushToGitRemote();
            if (pushResult != GitTfsExitCodes.OK)
            {
                Console.Error.WriteLine("❌ Failed to push to Git remote");
                return pushResult;
            }

            Console.WriteLine("\n✅ Git → TFVC sync completed successfully");
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

            // Step 1: Fetch from TFVC
            Console.WriteLine("\n📥 Step 1: Fetching from TFVC...");
            var fetchResult = _fetch.Run(_globals.RemoteId);
            if (fetchResult != GitTfsExitCodes.OK)
            {
                Console.Error.WriteLine("❌ Fetch from TFVC failed");
                return fetchResult;
            }

            // Step 1.5: Merge fetched TFVC commits into HEAD
            Console.WriteLine("\n🔄 Step 1.5: Merging TFVC changes into working branch...");
            var tfsBranch = remote.RemoteRef;  // refs/remotes/tfs/default
            
            // Get the latest commit from the TFS remote
            var tfsCommit = _globals.Repository.GetCommit(tfsBranch);
            if (tfsCommit == null)
            {
                Console.Error.WriteLine("❌ Could not find TFS remote commit");
                return GitTfsExitCodes.InvalidArguments;
            }
            
            // Check if HEAD is behind the TFS remote
            var headCommit = _globals.Repository.GetCommit("HEAD");
            if (headCommit != null && headCommit.Sha != tfsCommit.Sha)
            {
                Trace.WriteLine($"Merging TFVC changes from {headCommit.Sha} to {tfsCommit.Sha}");
                
                // Try fast-forward first (no local commits)
                var mergeResult = RunGitCommand($"merge --ff-only {tfsBranch}");
                if (mergeResult != 0)
                {
                    // If fast-forward fails, create a merge commit to preserve commit SHAs
                    // This is KEY: merge commits don't change SHAs, so git-notes are preserved!
                    Console.WriteLine("   Fast-forward not possible, creating merge commit...");
                    mergeResult = RunGitCommand($"merge --no-ff {tfsBranch} -m \"Merge TFVC changes (C{remote.MaxChangesetId})\"");
                    if (mergeResult != 0)
                    {
                        // Check if there are conflicts
                        if (HasMergeConflicts())
                        {
                            var conflicts = GetConflictedFiles();
                            var conflictInfo = AnalyzeConflict(conflicts);
                            conflictInfo.TfvcCommitSha = tfsCommit.Sha;
                            conflictInfo.LocalCommitSha = headCommit?.Sha;
                            HandleMergeConflict(conflictInfo);
                        }
                        else
                        {
                            Console.Error.WriteLine("❌ Failed to merge TFVC changes");
                            Console.Error.WriteLine("   An unknown error occurred during merge");
                            Console.Error.WriteLine("   Run 'git status' for more details");
                        }
                        return GitTfsExitCodes.ExceptionThrown;
                    }
                }
                
                Console.WriteLine($"✅ Working branch updated to include TFVC commit {tfsCommit.Sha.Substring(0, 8)}");
            }
            else
            {
                Console.WriteLine("   Already up to date with TFVC");
            }

            // Step 2: Pull latest commits from Git remote (to get commits pushed by other agents/users)
            Console.WriteLine("\n📥 Step 2: Pulling latest commits from Git remote...");
            var pullResult = PullFromGitRemote();
            if (pullResult != GitTfsExitCodes.OK)
            {
                Console.Error.WriteLine("❌ Failed to pull from Git remote");
                return pullResult;
            }

            // CRITICAL FIX: After pulling from Git remote (which may rebase commits),
            // we need to refresh the repository to pick up the new state
            Trace.WriteLine("Refreshing repository after pull to update TFS remote tracking...");
            _globals.Repository = _init.GitHelper.MakeRepository(_globals.GitDir);
            
            // Re-read the TFS remote to get updated state
            remote = _globals.Repository.ReadTfsRemote(_globals.RemoteId);
            if (remote == null)
            {
                Console.Error.WriteLine("❌ ERROR: Lost TFS remote after pull");
                return GitTfsExitCodes.InvalidArguments;
            }
            Trace.WriteLine($"TFS remote state after pull: MaxChangesetId={remote.MaxChangesetId}, MaxCommitHash={remote.MaxCommitHash}");

            // Step 3: Check in all untracked commits to TFVC using sync-checkin (preserves commit SHAs)
            Console.WriteLine("\n📤 Step 3: Checking in untracked commits to TFVC...");
            
            // CRITICAL FIX: Refresh repository again before sync-checkin
            // SyncCheckin uses its own injected Globals instance, so we need to refresh it
            Trace.WriteLine("Refreshing repository before sync-checkin to ensure notes are visible...");
            _globals.Repository = _init.GitHelper.MakeRepository(_globals.GitDir);
            Trace.WriteLine("Repository refreshed for sync-checkin");
            
            // NOTE: With merge commits, the TFS remote commit is naturally in HEAD's ancestry
            // (it's one of the parents of the merge commit). No additional rebase needed.
            Trace.WriteLine($"TFS remote ready: MaxChangesetId={remote.MaxChangesetId}, MaxCommitHash={remote.MaxCommitHash}");
            
            // CRITICAL FIX: Skip the pre-checkin fetch in SyncCheckin since we just fetched in Step 1
            // This prevents overwriting the git-notes we pulled from remote in Step 2
            Environment.SetEnvironmentVariable("GIT_TFS_SKIP_PRECHECKIN_FETCH", "true");
            
            try
            {
                var checkinResult = _syncCheckin.Run();
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
            finally
            {
                Environment.SetEnvironmentVariable("GIT_TFS_SKIP_PRECHECKIN_FETCH", null);
            }

            // Step 4: Push commits and git-notes back to Git remote
            Console.WriteLine("\n📤 Step 4: Pushing commits and git-notes to Git remote...");
            var pushResult = PushToGitRemote();
            if (pushResult != GitTfsExitCodes.OK)
            {
                Console.Error.WriteLine("❌ Failed to push to Git remote");
                return pushResult;
            }

            Console.WriteLine("\n✅ Bidirectional sync completed successfully");
            return GitTfsExitCodes.OK;
        }

        private int InitializeWorkspace()
        {
            Console.WriteLine("🚀 Initializing workspace...");
            
            var rootDir = _options.WorkspaceRoot ?? Directory.GetCurrentDirectory();
            Console.WriteLine($"   Root directory: {rootDir}");
            
            // Create directory structure under _workspace subfolder
            var workspaceDir = Path.Combine(rootDir, "_workspace");
            var toolsDir = Path.Combine(workspaceDir, "_tools");
            var agentsDir = Path.Combine(workspaceDir, "_agents");

            // Safeguard 1: Check if base workspace structure already exists
            bool workspaceExists = Directory.Exists(workspaceDir);
            bool toolsExist = Directory.Exists(toolsDir);
            
            if (workspaceExists && toolsExist)
            {
                Console.WriteLine($"✅ Workspace structure already exists: {workspaceDir}");
                Console.WriteLine("   Skipping workspace creation (already present)");
            }
            else
            {
                // Create base workspace directory structure
                Directory.CreateDirectory(workspaceDir);
                Directory.CreateDirectory(toolsDir);
                Directory.CreateDirectory(agentsDir);
                Console.WriteLine($"✅ Created workspace directory: {workspaceDir}");
            }

            // Check for Git and optionally install (always check, even if workspace exists)
            var gitInstaller = new GitInstaller(toolsDir);
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

            // Safeguard 2: Handle workspace name - required for agent workspace creation
            if (string.IsNullOrEmpty(_options.WorkspaceName))
            {
                // No workspace name provided - just create/verify the base structure
                Console.WriteLine("\n✅ Base workspace structure ready!");
                Console.WriteLine($"   Root directory: {rootDir}");
                Console.WriteLine($"   Workspace directory: {workspaceDir}");
                Console.WriteLine($"   Tools directory: {toolsDir}");
                Console.WriteLine($"   Agents directory: {agentsDir}");
                
                Console.WriteLine("\nTo create an agent workspace:");
                Console.WriteLine("  # For folder structure only:");
                Console.WriteLine($"    .\\git-tfs.exe sync --init-workspace --init-only --workspace-name=<WorkspaceName>");
                Console.WriteLine("\n  # For full setup with clone:");
                Console.WriteLine($"    .\\git-tfs.exe sync --init-workspace --workspace-name=<WorkspaceName> \\");
                Console.WriteLine($"      --tfvc-url=<url> --tfvc-path=<path> --git-remote-url=<url>");
                
                return GitTfsExitCodes.OK;
            }

            var workspaceName = _options.WorkspaceName;
            var agentWorkspace = Path.Combine(agentsDir, workspaceName);
            var repoPath = Path.Combine(agentWorkspace, "repo");

            // Check if agent workspace already exists
            bool agentExists = Directory.Exists(agentWorkspace);
            bool repoExists = Directory.Exists(repoPath);
            
            if (agentExists && !_options.InitOnly)
            {
                // For full init, check if repo already exists (from previous init-only or manual creation)
                if (repoExists)
                {
                    Console.WriteLine($"\n⚠️  Agent workspace already exists: {agentWorkspace}");
                    Console.WriteLine("   Repository directory already exists - cannot perform full initialization.");
                    Console.WriteLine("   Options:");
                    Console.WriteLine("     1. Use --init-only to verify/update folder structure only");
                    Console.WriteLine("     2. Delete the existing workspace and try again");
                    Console.WriteLine($"     3. Choose a different workspace name");
                    return GitTfsExitCodes.InvalidArguments;
                }
                // Agent workspace exists but repo doesn't - this is OK for full init
                Console.WriteLine($"\n✅ Using existing agent workspace: {agentWorkspace}");
            }

            // Create agent workspace directories
            // Note: For full init, clone will create the repo directory
            // For init-only, we create it manually
            if (_options.InitOnly)
            {
                Directory.CreateDirectory(repoPath);
            }
            else
            {
                // For full init, ensure agent workspace exists (but not repo - clone will create it)
                Directory.CreateDirectory(agentWorkspace);
            }
            
            if (!agentExists)
            {
                Console.WriteLine($"✅ Created agent workspace: {agentWorkspace}");
            }
            
            Console.WriteLine($"   Repository directory: {repoPath}");
            Console.WriteLine($"   Lock file location: {Path.Combine(agentWorkspace, $"{workspaceName}.lock")}");

            // Branch: --init-only vs full init
            if (_options.InitOnly)
            {
                // Just create folder structure
                Console.WriteLine("\n✅ Workspace folder structure initialized (--init-only)!");
                Console.WriteLine($"   Agent workspace: {agentWorkspace}");
                Console.WriteLine($"   Repository path: {repoPath}");
                Console.WriteLine($"   Lock file location: {Path.Combine(agentWorkspace, $"{workspaceName}.lock")}");

                Console.WriteLine("\nNext steps to initialize the repository:");
                Console.WriteLine($"  cd {repoPath}");
                Console.WriteLine($"  ..\\..\\..\\..\\git-tfs.exe clone --gitignore-template=VisualStudio <tfvc-url> <tfvc-path> .");
                Console.WriteLine($"  # or use quick-clone for faster initialization");
                Console.WriteLine($"  ..\\..\\..\\..\\git-tfs.exe quick-clone --gitignore-template=VisualStudio <tfvc-url> <tfvc-path> .");
                Console.WriteLine($"  git remote add origin <git-remote-url>");
                Console.WriteLine($"  git push origin --all");
                
                return GitTfsExitCodes.OK;
            }

            // Full initialization: clone repository, configure git notes, add remote
            Console.WriteLine("\n📦 Performing full workspace initialization...");
            Console.WriteLine($"   TFVC URL: {_options.TfvcUrl}");
            Console.WriteLine($"   TFVC Path: {_options.TfvcPath}");
            Console.WriteLine($"   Git Remote: {_options.GitRemoteUrl}");
            Console.WriteLine($"   Clone method: {(_options.UseQuickClone ? "quick-clone (shallow)" : "clone (full history)") }");

            // Change to the repo directory for clone operation
            var originalDir = Directory.GetCurrentDirectory();
            try
            {
                // Navigate to parent directory of repo
                Directory.SetCurrentDirectory(agentWorkspace);
                
                // Set initial branch if specified
                if (!string.IsNullOrEmpty(_options.InitialBranch))
                {
                    _initOptions.GitInitDefaultBranch = _options.InitialBranch;
                }
                
                // Perform clone or quick-clone
                Console.WriteLine($"\n📥 Cloning TFVC repository...");
                int cloneResult;
                
                if (_options.UseQuickClone)
                {
                    cloneResult = _quickClone.Run(_options.TfvcUrl, _options.TfvcPath, "repo");
                }
                else
                {
                    cloneResult = _clone.Run(_options.TfvcUrl, _options.TfvcPath, "repo");
                }
                
                if (cloneResult != GitTfsExitCodes.OK)
                {
                    Console.Error.WriteLine("\n❌ Clone failed");
                    return cloneResult;
                }
                
                Console.WriteLine("✅ Clone completed successfully");
                
                // Reinitialize repository object to access newly cloned repo
                _globals.GitDir = Path.Combine(repoPath, ".git");
                _globals.Repository = _init.GitHelper.MakeRepository(_globals.GitDir);
                
                // Configure git user for all future commits in this repository
                // This resolves from env vars → git-tfs config → git config → defaults
                var (userName, userEmail) = _globals.Repository.ResolveGitUserSignature();
                Console.WriteLine($"\n⚙️  Configuring git user for repository...");
                Console.WriteLine($"   Name:  {userName}");
                Console.WriteLine($"   Email: {userEmail}");
                
                // Navigate into the repo directory
                Directory.SetCurrentDirectory(repoPath);
                
                // Set local config so all future commits use these values
                var configNameResult = RunGitCommand($"config user.name \"{userName}\"");
                var configEmailResult = RunGitCommand($"config user.email \"{userEmail}\"");
                
                if (configNameResult != 0 || configEmailResult != 0)
                {
                    Console.Error.WriteLine("⚠️  Warning: Failed to set git user config");
                    Console.Error.WriteLine("   Future commits may fail if git config is not set");
                }
                else
                {
                    Console.WriteLine("✅ Git user configured");
                }
                
                // Apply gitignore template if provided
                if (!string.IsNullOrEmpty(_options.GitIgnoreTemplate))
                {
                    Console.WriteLine($"\n📝 Applying .gitignore template: {_options.GitIgnoreTemplate}");
                    var gitignoreApplied = ApplyGitIgnoreTemplate(_options.GitIgnoreTemplate, repoPath);
                    if (gitignoreApplied)
                    {
                        Console.WriteLine("✅ .gitignore template applied and committed");
                    }
                    else
                    {
                        Console.WriteLine("⚠️  .gitignore template not applied (template not found or already exists)");
                    }
                }
                
                // Configure git notes (critical for sync)
                Console.WriteLine("\n⚙️  Configuring git notes for sync...");
                var configResult = RunGitCommand("config git-tfs.use-notes true");
                if (configResult != 0)
                {
                    Console.Error.WriteLine("❌ Failed to configure git notes");
                    return GitTfsExitCodes.InvalidArguments;
                }
                Console.WriteLine("✅ Git notes configured");
                
                // Add git remote
                Console.WriteLine($"\n🔗 Adding Git remote: {_options.GitRemoteUrl}");
                var remoteResult = RunGitCommand($"remote add origin {_options.GitRemoteUrl}");
                if (remoteResult != 0)
                {
                    Console.Error.WriteLine("❌ Failed to add Git remote");
                    return GitTfsExitCodes.InvalidArguments;
                }
                Console.WriteLine("✅ Git remote added");
                
                // Auto-push if requested
                if (_options.AutoPush)
                {
                    Console.WriteLine($"\n🚀 Automatically pushing to Git remote...");
                    
                    // Push all branches
                    var pushResult = RunGitCommand("push origin --all", useAuth: true);
                    if (pushResult != 0)
                    {
                        Console.Error.WriteLine("❌ Failed to push branches to Git remote");
                        Console.Error.WriteLine("   You can manually push later with: git push origin --all");
                        // Don't fail the entire init, just warn
                    }
                    else
                    {
                        Console.WriteLine("✅ Successfully pushed branches to Git remote");
                    }
                    
                    // Push git-notes (critical for sync tracking)
                    Console.WriteLine("\n📝 Pushing git-notes to Git remote...");
                    var notesPushResult = RunGitCommand($"push origin {GitTfsConstants.TfvcSyncNotesRef}", useAuth: true);
                    if (notesPushResult != 0)
                    {
                        Console.WriteLine("⚠️  Failed to push git-notes to Git remote");
                        Console.WriteLine("   Git-notes will be pushed during first sync operation");
                        // Don't fail - notes can be pushed later during sync
                    }
                    else
                    {
                        Console.WriteLine("✅ Successfully pushed git-notes to Git remote");
                    }
                }
                
                // Success!
                Console.WriteLine("\n" + new string('=', 80));
                Console.WriteLine("✅ FULL WORKSPACE INITIALIZATION COMPLETE!");
                Console.WriteLine(new string('=', 80));
                Console.WriteLine($"   Workspace: {agentWorkspace}");
                Console.WriteLine($"   Repository: {repoPath}");
                Console.WriteLine($"   TFVC Source: {_options.TfvcUrl} {_options.TfvcPath}");
                Console.WriteLine($"   Git Remote: {_options.GitRemoteUrl}");
                Console.WriteLine($"   Git notes: Enabled");
                
                if (_options.AutoPush)
                {
                    Console.WriteLine("\nRepository is ready for bidirectional sync!");
                    Console.WriteLine($"  Start sync:  cd {rootDir} && .\\git-tfs.exe sync --workspace-name={workspaceName}");
                }
                else
                {
                    Console.WriteLine("\nNext steps:");
                    Console.WriteLine($"  1. Push to Git: cd {repoPath} && git push origin --all");
                    Console.WriteLine($"  2. Start sync:  cd {rootDir} && .\\git-tfs.exe sync --workspace-name={workspaceName}");
                }
                
                Console.WriteLine("\nTo add another agent workspace:");
                Console.WriteLine("  # Folder structure only:");
                Console.WriteLine($"    .\\git-tfs.exe sync --init-workspace --init-only --workspace-name=<Name>");
                Console.WriteLine("  # Full setup:");
                Console.WriteLine($"    .\\git-tfs.exe sync --init-workspace --workspace-name=<Name> \\");
                Console.WriteLine($"      --tfvc-url=<url> --tfvc-path=<path> --git-remote-url=<url>");
                
                return GitTfsExitCodes.OK;
            }
            finally
            {
                // Restore original directory
                Directory.SetCurrentDirectory(originalDir);
            }
        }

        private bool ApplyGitIgnoreTemplate(string templateNameOrPath, string repoPath)
        {
            try
            {
                var gitignorePath = Path.Combine(repoPath, ".gitignore");
                
                // Don't overwrite existing .gitignore
                if (File.Exists(gitignorePath))
                {
                    Console.WriteLine($"   .gitignore already exists, skipping template application");
                    return false;
                }
                
                string templatePath = null;
                
                // Check if it's a file path
                if (File.Exists(templateNameOrPath))
                {
                    templatePath = templateNameOrPath;
                }
                else
                {
                    // Try to resolve as a built-in template name
                    var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    var templatesDir = Path.Combine(exeDir, "Templates", "gitignore");
                    
                    // Try with .gitignore extension
                    var builtInPath = Path.Combine(templatesDir, $"{templateNameOrPath}.gitignore");
                    if (File.Exists(builtInPath))
                    {
                        templatePath = builtInPath;
                    }
                    else
                    {
                        // Try case-insensitive search
                        if (Directory.Exists(templatesDir))
                        {
                            var files = Directory.GetFiles(templatesDir, "*.gitignore");
                            templatePath = files.FirstOrDefault(f => 
                                Path.GetFileNameWithoutExtension(f).Equals(templateNameOrPath, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }
                
                if (string.IsNullOrEmpty(templatePath) || !File.Exists(templatePath))
                {
                    Console.WriteLine($"   Template '{templateNameOrPath}' not found");
                    return false;
                }
                
                // Copy template to .gitignore
                File.Copy(templatePath, gitignorePath);
                Console.WriteLine($"   Copied template to .gitignore");
                
                // Add and commit the .gitignore file
                var addResult = RunGitCommand("add .gitignore");
                if (addResult != 0)
                {
                    Console.WriteLine($"   Failed to add .gitignore to git");
                    return false;
                }
                
                // Commit with explicit --author flag as safeguard (in case local config wasn't set)
                // Resolve signature using the same precedence as the local config we just set
                var (userName, userEmail) = _globals.Repository.ResolveGitUserSignature();
                var commitResult = RunGitCommand($"commit -m \"Add .gitignore from template\" --author=\"{userName} <{userEmail}>\"");
                if (commitResult != 0)
                {
                    Console.WriteLine($"   Failed to commit .gitignore");
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   Error applying .gitignore template: {ex.Message}");
                return false;
            }
        }

        private int RunGitCommand(string arguments, bool useAuth = false)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                // If auth token is provided and useAuth is true, add authentication header
                if (useAuth && !string.IsNullOrEmpty(_options.GitAuthToken))
                {
                    // Use RFC 6750-compliant Bearer token format
                    startInfo.Arguments = $"-c http.extraheader=\"AUTHORIZATION: Bearer {_options.GitAuthToken}\" {arguments}";
                }
                
                using (var process = Process.Start(startInfo))
                {
                    // Read stdout and stderr to capture Git's output
                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();
                    
                    process.WaitForExit();
                    
                    // Display stdout if not empty
                    if (!string.IsNullOrWhiteSpace(stdout))
                    {
                        Console.WriteLine(stdout);
                    }
                    
                    // Display stderr if not empty (Git often writes normal output to stderr)
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        // Check if this is an error (non-zero exit code) or just informational
                        if (process.ExitCode != 0)
                        {
                            Console.Error.WriteLine($"Git error: {stderr}");
                        }
                        else
                        {
                            Console.WriteLine(stderr);
                        }
                    }
                    
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to run git command: {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// Pulls latest commits from the Git remote to get commits pushed by other agents/users.
        /// Uses merge to preserve commit SHAs and git-notes (no rebase = no SHA changes).
        /// </summary>
        private int PullFromGitRemote()
        {
            try
            {
                // Check if we have a Git remote configured
                var hasRemote = RunGitCommand("remote get-url origin") == 0;
                if (!hasRemote)
                {
                    Trace.WriteLine("No Git remote 'origin' configured, skipping pull");
                    Console.WriteLine("ℹ️  No Git remote configured, skipping pull");
                    return GitTfsExitCodes.OK;
                }

                // Get current branch name
                var branchName = _globals.Repository.GetCurrentBranch();
                if (string.IsNullOrEmpty(branchName))
                {
                    Trace.WriteLine("Unable to determine current branch, skipping pull");
                    Console.WriteLine("ℹ️  Unable to determine current branch, skipping pull");
                    return GitTfsExitCodes.OK;
                }

                // Extract short branch name (e.g., "main" from "refs/heads/main")
                var shortBranchName = branchName.Replace("refs/heads/", "");

                // Fetch git-notes first to ensure they're available
                Trace.WriteLine($"Fetching git-notes from origin...");
                var fetchNotesResult = RunGitCommand($"fetch origin {GitTfsConstants.TfvcSyncNotesRef}:{GitTfsConstants.TfvcSyncNotesRef}", useAuth: true);
                if (fetchNotesResult == 0)
                {
                    Trace.WriteLine("✅ Git-notes fetched successfully");
                }
                else
                {
                    Trace.WriteLine("ℹ️  Git-notes will be synced during push");
                }

                // Pull with merge (NOT rebase) to preserve commit SHAs and git-notes
                // This is KEY: merge doesn't change SHAs, so git-notes stay attached!
                Trace.WriteLine($"Pulling from origin/{shortBranchName}...");
                var pullResult = RunGitCommand($"pull --no-rebase origin {shortBranchName}", useAuth: true);
                
                if (pullResult != 0)
                {
                    // Check if this is a merge conflict
                    if (HasMergeConflicts())
                    {
                        var conflicts = GetConflictedFiles();
                        var conflictInfo = AnalyzeConflict(conflicts);
                        
                        // Try to get the remote commit being merged
                        var mergeHeadPath = Path.Combine(_globals.GitDir, "MERGE_HEAD");
                        if (File.Exists(mergeHeadPath))
                        {
                            conflictInfo.GitRemoteCommitSha = File.ReadAllText(mergeHeadPath).Trim();
                        }
                        
                        HandleMergeConflict(conflictInfo);
                    }
                    else
                    {
                        Console.Error.WriteLine($"Failed to pull from Git remote (branch: {shortBranchName})");
                        Console.Error.WriteLine("An unknown error occurred. Run 'git status' for more details");
                    }
                    return GitTfsExitCodes.ExceptionThrown;
                }

                Console.WriteLine($"✅ Pulled latest commits from origin/{shortBranchName}");
                return GitTfsExitCodes.OK;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error pulling from Git remote: {ex.Message}");
                Trace.WriteLine($"Pull error details: {ex}");
                return GitTfsExitCodes.ExceptionThrown;
            }
        }

        /// <summary>
        /// Configures Git with settings optimized for auto-merging during sync operations.
        /// Uses the patience algorithm for better merge results and enables auto-merge features.
        /// </summary>
        private void ConfigureAutoMerge()
        {
            Trace.WriteLine("Configuring Git for auto-merge...");
            
            // Use patience diff algorithm for better merge results
            // This algorithm is better at detecting moved lines and produces cleaner merges
            RunGitCommand("config merge.conflictstyle diff3");
            
            // Configure rebase to use the recursive merge strategy with patience
            RunGitCommand("config pull.rebase true");
            RunGitCommand("config rebase.autoStash false"); // We don't want auto-stash in CI
            RunGitCommand("config rebase.autoSquash false"); // Keep all commits as-is
            
            // Enable rerere (reuse recorded resolution) for repeated conflict patterns
            // This helps if the same conflict happens multiple times
            RunGitCommand("config rerere.enabled true");
            RunGitCommand("config rerere.autoUpdate true");
            
            Trace.WriteLine("Auto-merge configuration complete");
        }

        /// <summary>
        /// Checks if there are any unresolved merge conflicts in the working directory.
        /// </summary>
        private bool HasMergeConflicts()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "diff --name-only --diff-filter=U",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    
                    // If there's output, there are unmerged files
                    return !string.IsNullOrWhiteSpace(output);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error checking for merge conflicts: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets a list of files with merge conflicts.
        /// </summary>
        private string[] GetConflictedFiles()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "diff --name-only --diff-filter=U",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    
                    if (string.IsNullOrWhiteSpace(output))
                        return new string[0];
                    
                    return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error getting conflicted files: {ex.Message}");
                return new string[0];
            }
        }

        /// <summary>
        /// Detailed conflict information for reporting to the user
        /// </summary>
        private class ConflictInfo
        {
            public string[] ConflictedFiles { get; set; }
            public string TfvcCommitSha { get; set; }
            public string GitRemoteCommitSha { get; set; }
            public string LocalCommitSha { get; set; }
            public DateTime DetectedAt { get; set; }
            public string RepoPath { get; set; }
            public string Branch { get; set; }
        }

        /// <summary>
        /// Provides comprehensive guidance for resolving merge conflicts during sync operations.
        /// Adapts guidance based on whether running in CI/CD or interactive environment.
        /// </summary>
        private void HandleMergeConflict(ConflictInfo conflict)
        {
            Console.Error.WriteLine("");
            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("❌ MERGE CONFLICT DETECTED");
            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("");

            // General info
            Console.Error.WriteLine("Git-TFS has detected a merge conflict while synchronizing changes.");
            Console.Error.WriteLine("This typically happens when the same file is modified in both Git and TFVC");
            Console.Error.WriteLine("and then synced. Git-TFS is unable to resolve these conflicts automatically.");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("IMPORTANT: Resolving merge conflicts requires careful attention.");
            Console.Error.WriteLine("DO NOT push your changes to Git or check in to TFVC until the conflict is resolved,");
            Console.Error.WriteLine("as this can cause data loss or corruption of the repository state.");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("Please follow the appropriate resolution steps based on your environment:");
            Console.Error.WriteLine("  • CI/CD automation (e.g., Azure DevOps, Jenkins)");
            Console.Error.WriteLine("  • Local development environment");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("After resolving the conflict, commit the resolution and re-run the sync command.");
            Console.Error.WriteLine("If you need to abort the merge, you can run 'git merge --abort' to cancel.");
            Console.Error.WriteLine("");

            // CI/CD specific guidance
            if (IsRunningInCiCdEnvironment())
            {
                DisplayCIConflictGuidance(conflict);
            }
            else
            {
                // Local development guidance
                DisplayLocalDevConflictGuidance(conflict);
            }
        }

        /// <summary>
        /// Determines if the sync command is running in a CI/CD environment
        /// </summary>
        private bool IsRunningInCiCdEnvironment()
        {
            // Check for common environment variables set by CI/CD systems
            var buildId = Environment.GetEnvironmentVariable("BUILD_BUILDID");
            var isRunningUnderDocker = Environment.GetEnvironmentVariable("DOCKER_CONTAINER") == "true";

            return !string.IsNullOrEmpty(buildId) || isRunningUnderDocker;
        }

        /// <summary>
        /// Displays conflict resolution guidance for CI/CD environments
        /// </summary>
        private void DisplayCIConflictGuidance(ConflictInfo conflict)
        {
            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("⚠️  CI/CD ENVIRONMENT DETECTED");
            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("");
            Console.Error.WriteLine("This sync is running in an automated CI/CD pipeline where manual conflict");
            Console.Error.WriteLine("resolution is not possible. The pipeline will fail and leave the repository");
            Console.Error.WriteLine("in a conflicted state that must be resolved manually.");
            Console.Error.WriteLine("");

            // Determine if we're in a workspace agent scenario
            var isWorkspaceAgent = !string.IsNullOrEmpty(_options.WorkspaceName);
            
            if (isWorkspaceAgent)
            {
                DisplayWorkspaceAgentConflictGuidance(conflict);
            }
            else
            {
                DisplayStandardCIConflictGuidance(conflict);
            }
        }

        /// <summary>
        /// Displays conflict resolution guidance for workspace agent scenarios (multi-agent sync)
        /// </summary>
        private void DisplayWorkspaceAgentConflictGuidance(ConflictInfo conflict)
        {
            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("🔧 HOW TO RESOLVE (Workspace Agent Scenario)");
            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("");
            Console.Error.WriteLine("⚠️  IMPORTANT: This conflict is in the git-tfs workspace agent's repository,");
            Console.Error.WriteLine("   not in your regular development repository. You need to access the BUILD");
            Console.Error.WriteLine("   SERVER or shared workspace location where the agent is running.");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 1: Access the Build Server / Workspace Location");
            Console.Error.WriteLine($"   Repository path: {conflict.RepoPath}");
            if (!string.IsNullOrEmpty(_options.WorkspaceName))
            {
                Console.Error.WriteLine($"   Workspace name:  {_options.WorkspaceName}");
            }
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Option A - Remote Desktop / SSH into the build server:");
            Console.Error.WriteLine("      1. Connect to the build server where the pipeline runs");
            Console.Error.WriteLine($"      2. Navigate to: {conflict.RepoPath}");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Option B - Network share (if workspace is on shared storage):");
            Console.Error.WriteLine("      1. Access the workspace via network share");
            Console.Error.WriteLine($"      2. Navigate to: {conflict.RepoPath}");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   ⚠️  WARNING: Do NOT clone a fresh copy! The conflict exists ONLY in");
            Console.Error.WriteLine("      this specific agent's working directory and must be resolved there.");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 2: Verify You're in the Correct Repository");
            Console.Error.WriteLine($"   cd \"{conflict.RepoPath}\"");
            Console.Error.WriteLine("   git status");
            Console.Error.WriteLine("   # You should see 'You have unmerged paths' and the conflicted files");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 3: Inspect the Conflict");
            Console.Error.WriteLine("   See what's conflicting:");
            Console.Error.WriteLine("      git diff                    # Shows conflicts with markers");
            Console.Error.WriteLine("      git diff HEAD               # Shows TFVC changes (ours)");
            Console.Error.WriteLine("      git diff MERGE_HEAD         # Shows Git remote changes (theirs)");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 4: Resolve the Conflict");
            Console.Error.WriteLine("   Choose the appropriate method:");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Method 1 - Manual Text Editing (Recommended if you have server access):");
            Console.Error.WriteLine("      1. Open each conflicted file in a text editor (notepad, vim, etc.)");
            Console.Error.WriteLine("      2. Look for conflict markers:");
            Console.Error.WriteLine("         <<<<<<< HEAD");
            Console.Error.WriteLine("         TFVC changes (fetched from TFS)");
            Console.Error.WriteLine("         =======");
            Console.Error.WriteLine("         Git remote changes (pulled from origin)");
            Console.Error.WriteLine("         >>>>>>> <commit-sha>");
            Console.Error.WriteLine("      3. Edit to keep the correct changes, remove markers");
            Console.Error.WriteLine("      4. Save the file");
            Console.Error.WriteLine("      5. Stage: git add <filename>");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Method 2 - Use Git Merge Tool (if GUI available on server):");
            Console.Error.WriteLine("      git mergetool");
            Console.Error.WriteLine("      # If configured, this opens a GUI tool");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Method 3 - Accept One Side Completely:");
            Console.Error.WriteLine("      # Keep TFVC changes (discard Git remote):");
            Console.Error.WriteLine("      git checkout --ours <filename>");
            Console.Error.WriteLine("      git add <filename>");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("      # Keep Git remote changes (discard TFVC):");
            Console.Error.WriteLine("      git checkout --theirs <filename>");
            Console.Error.WriteLine("      git add <filename>");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 5: Complete the Merge");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   EASIEST: Skip the editor and commit directly:");
            Console.Error.WriteLine("      git commit -m \"Resolved merge conflict: [describe what you did]\"");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   OR use the default editor:");
            Console.Error.WriteLine("      git commit");
            Console.Error.WriteLine("      # This opens an editor with a pre-filled commit message");
            Console.Error.WriteLine("      # An editor will open to let you edit the message");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   HOW TO SAVE AND EXIT THE EDITOR:");
            Console.Error.WriteLine("      If Vim opens:");
            Console.Error.WriteLine("        • Type ':wq' and press Enter (writes and quits)");
            Console.Error.WriteLine("        • Or type ':x' and press Enter (save and exit - shorter)");
            Console.Error.WriteLine("        • To add notes: press 'i' to edit, then Esc, then ':wq'");
            Console.Error.WriteLine("        • To abort without saving: type ':q!' and press Enter");
            Console.Error.WriteLine("      If Nano opens:");
            Console.Error.WriteLine("        • Edit the message as needed");
            Console.Error.WriteLine("        • Press 'Ctrl+O' to write (save), then Enter to confirm");
            Console.Error.WriteLine("        • Press 'Ctrl+X' to exit");
            Console.Error.WriteLine("      If VS Code or Notepad opens:");
            Console.Error.WriteLine("        • Edit the message as needed");
            Console.Error.WriteLine("        • Save the file (Ctrl+S or File → Save)");
            Console.Error.WriteLine("        • Close the editor window");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 6: Manually Complete the Sync");
            Console.Error.WriteLine("   After resolving and committing, you need to finish what the pipeline");
            Console.Error.WriteLine("   couldn't do automatically:");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   1. Check in any new commits to TFVC (if there were any):");
            Console.Error.WriteLine($"      git-tfs sync-checkin --workspace-name={_options.WorkspaceName}");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   2. Push everything back to Git remote:");
            Console.Error.WriteLine($"      cd \"{conflict.RepoPath}\"");
            Console.Error.WriteLine($"      git push origin {conflict.Branch}");
            Console.Error.WriteLine($"      git push --force origin refs/notes/tfvc-sync");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 7: Pipeline Will Succeed on Next Run");
            Console.Error.WriteLine("   Once the conflict is resolved and changes are pushed, the next pipeline");
            Console.Error.WriteLine("   run will succeed.");
            Console.Error.WriteLine("");
        }

        /// <summary>
        /// Displays conflict resolution guidance for standard CI scenarios (single repo)
        /// </summary>
        private void DisplayStandardCIConflictGuidance(ConflictInfo conflict)
        {
            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("🔧 HOW TO RESOLVE (For Developers)");
            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 1: Clone/Pull the Repository");
            Console.Error.WriteLine("   If you haven't already:");
            Console.Error.WriteLine("   git clone <repository-url>");
            Console.Error.WriteLine("   cd <repository-directory>");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   If you already have the repo:");
            Console.Error.WriteLine("   git fetch origin");
            Console.Error.WriteLine($"   git checkout {conflict.Branch}");
            Console.Error.WriteLine("   git pull origin " + conflict.Branch);
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 2: Reproduce the Conflict Locally");
            Console.Error.WriteLine("   The sync process merges TFVC changes with Git remote changes.");
            Console.Error.WriteLine("   To reproduce the same conflict:");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Option A - If TFVC changes are already in your branch:");
            Console.Error.WriteLine("      # The conflict will appear when you pull from Git remote");
            Console.Error.WriteLine("      git pull origin " + conflict.Branch);
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Option B - If you need to fetch TFVC changes first:");
            Console.Error.WriteLine("      # Run git-tfs sync locally (it will fail at the same point)");
            Console.Error.WriteLine("      git-tfs sync");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 3: Resolve Conflicts");
            Console.Error.WriteLine("   Once you see the same conflict locally, use ONE of these methods:");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Method 1 - Use Visual Studio (if available):");
            Console.Error.WriteLine("      1. Open the solution in Visual Studio");
            Console.Error.WriteLine("      2. Team Explorer → Changes → Conflicts");
            Console.Error.WriteLine("      3. Use the built-in merge tool to resolve each file");
            Console.Error.WriteLine("      4. Mark as resolved and commit");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Method 2 - Use VS Code (if available):");
            Console.Error.WriteLine("      1. Open the repository folder in VS Code");
            Console.Error.WriteLine("      2. VS Code will highlight conflicted files");
            Console.Error.WriteLine("      3. Click 'Accept Current Change', 'Accept Incoming', or 'Accept Both'");
            Console.Error.WriteLine("      4. Or manually edit the file to resolve");
            Console.Error.WriteLine("      5. Save and stage the files (Source Control tab)");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Method 3 - Use Git's Merge Tool:");
            Console.Error.WriteLine("      git mergetool");
            Console.Error.WriteLine("      # Opens configured merge tool (e.g., KDiff3, P4Merge, WinMerge)");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Method 4 - Manual Resolution:");
            Console.Error.WriteLine("      1. Open each conflicted file in a text editor");
            Console.Error.WriteLine("      2. Find conflict markers:");
            Console.Error.WriteLine("         <<<<<<< HEAD");
            Console.Error.WriteLine("         Your changes");
            Console.Error.WriteLine("         =======");
            Console.Error.WriteLine("         Their changes");
            Console.Error.WriteLine("         >>>>>>> <commit-sha>");
            Console.Error.WriteLine("      3. Edit the file to keep the correct changes, remove markers");
            Console.Error.WriteLine("      4. Save the file");
            Console.Error.WriteLine("      5. Stage: git add <filename>");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 4: Complete the Merge");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   EASIEST: Skip the editor and commit directly:");
            Console.Error.WriteLine("      git commit -m \"Resolved merge conflict: [describe what you did]\"");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   OR use the default editor:");
            Console.Error.WriteLine("      git commit");
            Console.Error.WriteLine("      # Add a descriptive message about how conflicts were resolved");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 5: Push Your Resolution");
            Console.Error.WriteLine("   git push origin " + conflict.Branch);
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 6: Pipeline Will Succeed on Next Run");
            Console.Error.WriteLine("   Once your conflict resolution is pushed, the next pipeline run will");
            Console.Error.WriteLine("   succeed because the conflicts are already resolved.");
            Console.Error.WriteLine("");
        }

        /// <summary>
        /// Displays conflict resolution guidance for interactive (local development) environments
        /// </summary>
        private void DisplayLocalDevConflictGuidance(ConflictInfo conflict)
        {
            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("🔧 HOW TO RESOLVE THIS CONFLICT");
            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 1: Navigate to the Repository");
            Console.Error.WriteLine($"   cd \"{conflict.RepoPath}\"");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 2: Inspect the Conflict");
            Console.Error.WriteLine("   Option A - View conflicted files:");
            Console.Error.WriteLine("      git status");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Option B - See what changed in each version:");
            Console.Error.WriteLine("      git diff                    # Shows all conflicts with markers");
            Console.Error.WriteLine("      git diff HEAD               # Shows your changes");
            Console.Error.WriteLine("      git diff MERGE_HEAD         # Shows their changes");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Option C - View conflict for a specific file:");
            Console.Error.WriteLine("      git show :1:<filename>      # Common ancestor version");
            Console.Error.WriteLine("      git show :2:<filename>      # Your version (HEAD)");
            Console.Error.WriteLine("      git show :3:<filename>      # Their version (MERGE_HEAD)");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 3: Choose a Resolution Strategy");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Strategy A - Resolve Manually (RECOMMENDED)");
            Console.Error.WriteLine("   -------------------------------------------");
            Console.Error.WriteLine("   Best when: Changes affect different parts or need careful review");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   1. Open each conflicted file in a text editor");
            Console.Error.WriteLine("   2. Look for conflict markers:");
            Console.Error.WriteLine("      <<<<<<< HEAD");
            Console.Error.WriteLine("      TFVC changes (fetched from TFS)");
            Console.Error.WriteLine("      =======");
            Console.Error.WriteLine("      Git remote changes (pulled from origin)");
            Console.Error.WriteLine("      >>>>>>> <commit-sha>");
            Console.Error.WriteLine("   3. Edit the file to keep the correct changes");
            Console.Error.WriteLine("   4. Remove the conflict markers (<<<<<<, =======, >>>>>>>)");
            Console.Error.WriteLine("   5. Test your changes if needed");
            Console.Error.WriteLine("   6. Stage the resolved file:");
            Console.Error.WriteLine("      git add <filename>");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("   Strategy B - Use a Merge Tool (GUI Required)");
            Console.Error.WriteLine("   ---------------------------------------------");
            Console.Error.WriteLine("   Best when: You have Visual Studio, VS Code, or another GUI tool");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   1. Launch your merge tool:");
            Console.Error.WriteLine("      git mergetool");
            Console.Error.WriteLine("   2. Resolve conflicts in the GUI (e.g., Visual Studio, VS Code, KDiff3)");
            Console.Error.WriteLine("   3. Save and close the tool");
            Console.Error.WriteLine("   4. Files are automatically staged after resolution");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Strategy C - Keep One Side Completely");
            Console.Error.WriteLine("   --------------------------------------");
            Console.Error.WriteLine("   ⚠️  Use with caution - discards one set of changes entirely!");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Option 1 - Keep TFVC changes (discard Git remote changes):");
            Console.Error.WriteLine("      git checkout --ours <filename>");
            Console.Error.WriteLine("      git add <filename>");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   Option 2 - Keep Git remote changes (discard TFVC changes):");
            Console.Error.WriteLine("      git checkout --theirs <filename>");
            Console.Error.WriteLine("      git add <filename>");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   To apply to all conflicted files:");
            Console.Error.WriteLine("      git checkout --ours .");
            Console.Error.WriteLine("      # or");
            Console.Error.WriteLine("      git checkout --theirs .");
            Console.Error.WriteLine("      git add .");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 4: Verify Your Resolution");
            Console.Error.WriteLine("   Check that all conflicts are resolved:");
            Console.Error.WriteLine("      git status                  # Should show no 'unmerged' files");
            Console.Error.WriteLine("      git diff --cached           # Review your resolution");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 5: Complete the Merge");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   EASIEST: Skip the editor and commit directly:");
            Console.Error.WriteLine("      git commit -m \"Resolved merge conflict: [describe what you did]\"");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   OR use the default editor:");
            Console.Error.WriteLine("      git commit");
            Console.Error.WriteLine("      # The commit message will be pre-filled with merge information");
            Console.Error.WriteLine("      # An editor will open to let you edit the message");
            Console.Error.WriteLine("");
            Console.Error.WriteLine("   HOW TO SAVE AND EXIT THE EDITOR:");
            Console.Error.WriteLine("      If Vim opens:");
            Console.Error.WriteLine("        • Type ':wq' and press Enter (writes and quits)");
            Console.Error.WriteLine("        • Or type ':x' and press Enter (save and exit - shorter)");
            Console.Error.WriteLine("        • To add notes: press 'i' to edit, then Esc, then ':wq'");
            Console.Error.WriteLine("        • To abort without saving: type ':q!' and press Enter");
            Console.Error.WriteLine("      If Nano opens:");
            Console.Error.WriteLine("        • Edit the message as needed");
            Console.Error.WriteLine("        • Press 'Ctrl+O' to write (save), then Enter to confirm");
            Console.Error.WriteLine("        • Press 'Ctrl+X' to exit");
            Console.Error.WriteLine("      If VS Code or Notepad opens:");
            Console.Error.WriteLine("        • Edit the message as needed");
            Console.Error.WriteLine("        • Save the file (Ctrl+S or File → Save)");
            Console.Error.WriteLine("        • Close the editor window");
            Console.Error.WriteLine("");

            Console.Error.WriteLine("STEP 6: Resume the Sync");
            Console.Error.WriteLine("   After committing, run the sync command again:");
            Console.Error.WriteLine($"   cd \"{Path.GetDirectoryName(conflict.RepoPath)}\"");
            if (!string.IsNullOrEmpty(_options.WorkspaceName))
            {
                Console.Error.WriteLine($"   git-tfs sync --workspace-name={_options.WorkspaceName}");
            }
            else
            {
                Console.Error.WriteLine("   git-tfs sync");
            }
            Console.Error.WriteLine("");

            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("📚 ADDITIONAL RESOURCES");
            Console.Error.WriteLine(new string('=', 80));
            Console.Error.WriteLine("");
            Console.Error.WriteLine("Need more help?");
            Console.Error.WriteLine("   • Git merge documentation:");
            Console.Error.WriteLine("     https://git-scm.com/docs/git-merge");
            Console.Error.WriteLine("   • Resolving conflicts:");
            Console.Error.WriteLine("     https://git-scm.com/book/en/v2/Git-Branching-Basic-Branching-and-Merging");
            Console.Error.WriteLine("   • Git-tfs documentation:");
            Console.Error.WriteLine("     https://github.com/git-tfs/git-tfs");
            Console.Error.WriteLine("");
        }

        /// <summary>
        /// Analyzes a conflict to build comprehensive conflict information
        /// </summary>
        private ConflictInfo AnalyzeConflict(string[] conflictedFiles)
        {
            var branchName = _globals.Repository.GetCurrentBranch();
            var repoPath = Directory.GetCurrentDirectory();
            
            return new ConflictInfo
            {
                ConflictedFiles = conflictedFiles,
                DetectedAt = DateTime.Now,
                RepoPath = repoPath,
                Branch = branchName?.Replace("refs/heads/", "") ?? "main"
            };
        }

        /// <summary>
        /// Pushes commits and git-notes to the Git remote.
        /// </summary>
        private int PushToGitRemote()
        {
            try
            {
                // Check if we have a Git remote configured
                var hasRemote = RunGitCommand("remote get-url origin") == 0;
                if (!hasRemote)
                {
                    Trace.WriteLine("No Git remote 'origin' configured, skipping push");
                    Console.WriteLine("ℹ️  No Git remote configured, skipping push");
                    return GitTfsExitCodes.OK;
                }

                // Get current branch name
                var branchName = _globals.Repository.GetCurrentBranch();
                if (string.IsNullOrEmpty(branchName))
                {
                    Trace.WriteLine("Unable to determine current branch, skipping push");
                    Console.WriteLine("ℹ️  Unable to determine current branch, skipping push");
                    return GitTfsExitCodes.OK;
                }

                // Extract short branch name (e.g., "main" from "refs/heads/main")
                var shortBranchName = branchName.Replace("refs/heads/", "");

                // Try regular push first (should work since merge commits don't change SHAs)
                Trace.WriteLine($"Attempting regular push to origin/{shortBranchName}...");
                var pushResult = RunGitCommand($"push origin {shortBranchName}", useAuth: true);
                
                if (pushResult != 0)
                {
                    // Push was rejected - likely a concurrent change from another agent
                    Console.WriteLine("⚠️  Push rejected. Another agent may have pushed changes concurrently.");
                    Console.WriteLine("   Pulling latest changes and retrying...");
                    
                    // Pull again to get the latest changes (using merge to preserve SHAs)
                    Trace.WriteLine($"Pulling latest changes before retry...");
                    var retryPullResult = RunGitCommand($"pull --no-rebase origin {shortBranchName}", useAuth: true);
                    if (retryPullResult != 0)
                    {
                        // Check if this is a merge conflict
                        if (HasMergeConflicts())
                        {
                            Console.Error.WriteLine("❌ Merge conflict detected during retry pull");
                            Console.Error.WriteLine("   This indicates concurrent modifications to the same files.");
                            Console.Error.WriteLine("   The conflict must be resolved manually before the next sync.");
                            
                            var conflicts = GetConflictedFiles();
                            var conflictInfo = AnalyzeConflict(conflicts);
                            
                            // Try to get the remote commit being merged
                            var mergeHeadPath = Path.Combine(_globals.GitDir, "MERGE_HEAD");
                            if (File.Exists(mergeHeadPath))
                            {
                                conflictInfo.GitRemoteCommitSha = File.ReadAllText(mergeHeadPath).Trim();
                            }
                            
                            HandleMergeConflict(conflictInfo);
                        }
                        
                        Console.Error.WriteLine("❌ Failed to pull latest changes for retry");
                        Console.Error.WriteLine("   An unknown error occurred. Run 'git status' for details.");
                        return GitTfsExitCodes.ExceptionThrown;
                    }
                    
                    // Retry the push after merging latest changes
                    Trace.WriteLine("Retrying push after pulling latest changes...");
                    pushResult = RunGitCommand($"push origin {shortBranchName}", useAuth: true);
                    
                    if (pushResult != 0)
                    {
                        Console.Error.WriteLine($"❌ Failed to push commits even after retry (branch: {shortBranchName})");
                        Console.Error.WriteLine("");
                        Console.Error.WriteLine("This indicates a serious synchronization issue. Possible causes:");
                        Console.Error.WriteLine("  • Branch was force-pushed by someone else");
                        Console.Error.WriteLine("  • Repository history was rewritten");
                        Console.Error.WriteLine("  • Repeated concurrent pushes are occurring");
                        Console.Error.WriteLine("");
                        Console.Error.WriteLine("Resolution:");
                        Console.Error.WriteLine("  1. Check repository history for unexpected changes");
                        Console.Error.WriteLine("  2. Verify no one is force-pushing to this branch");
                        Console.Error.WriteLine("  3. Consider increasing lock timeout if concurrent syncs are frequent");
                        Console.Error.WriteLine("  4. Re-run sync command (will pull and retry again)");
                        return GitTfsExitCodes.ExceptionThrown;
                    }
                    
                    Console.WriteLine("✅ Push succeeded after retry");
                }
                else
                {
                    Trace.WriteLine("✅ Regular push succeeded (no retry required)");
                }

                // Push git-notes (with force since notes ref is updated independently)
                Trace.WriteLine("Pushing git-notes to remote...");
                var notesPushResult = RunGitCommand($"push --force origin {GitTfsConstants.TfvcSyncNotesRef}", useAuth: true);
                
                if (notesPushResult != 0)
                {
                    Console.Error.WriteLine("Failed to push git-notes to Git remote");
                    Console.Error.WriteLine("Commits were pushed, but tracking metadata was not synced");
                    return GitTfsExitCodes.ExceptionThrown;
                }

                Console.WriteLine($"✅ Pushed commits and git-notes to origin/{shortBranchName}");
                return GitTfsExitCodes.OK;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error pushing to Git remote: {ex.Message}");
                Trace.WriteLine($"Push error details: {ex}");
                return GitTfsExitCodes.ExceptionThrown;
            }
        }
    }
}
