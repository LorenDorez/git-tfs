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
        private readonly Rcheckin _rcheckin;
        private readonly Clone _clone;
        private readonly QuickClone _quickClone;
        private readonly Init _init;
        private readonly InitOptions _initOptions;
        private readonly SyncOptions _options;

        public Sync(Globals globals, Fetch fetch, Rcheckin rcheckin, Clone clone, QuickClone quickClone, Init init, InitOptions initOptions)
        {
            _globals = globals;
            _fetch = fetch;
            _rcheckin = rcheckin;
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
            var locksDir = Path.Combine(agentWorkspace, "locks");

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
            Directory.CreateDirectory(locksDir);
            
            if (!agentExists)
            {
                Console.WriteLine($"✅ Created agent workspace: {agentWorkspace}");
            }
            
            Console.WriteLine($"   Repository directory: {repoPath}");
            Console.WriteLine($"   Lock file location: {Path.Combine(locksDir, $"{workspaceName}.lock")}");

            // Branch: --init-only vs full init
            if (_options.InitOnly)
            {
                // Just create folder structure
                Console.WriteLine("\n✅ Workspace folder structure initialized (--init-only)!");
                Console.WriteLine($"   Agent workspace: {agentWorkspace}");
                Console.WriteLine($"   Repository path: {repoPath}");
                Console.WriteLine($"   Lock directory: {locksDir}");
                
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
            Console.WriteLine($"   Clone method: {(_options.UseQuickClone ? "quick-clone (shallow)" : "clone (full history)")}");

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
                
                // Navigate into the repo directory
                Directory.SetCurrentDirectory(repoPath);
                
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
                    var pushResult = RunGitCommand("push origin --all", useAuth: true);
                    if (pushResult != 0)
                    {
                        Console.Error.WriteLine("❌ Failed to push to Git remote");
                        Console.Error.WriteLine("   You can manually push later with: git push origin --all");
                        // Don't fail the entire init, just warn
                    }
                    else
                    {
                        Console.WriteLine("✅ Successfully pushed to Git remote");
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
                
                var commitResult = RunGitCommand("commit -m \"Add .gitignore from template\"");
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
    }
}
