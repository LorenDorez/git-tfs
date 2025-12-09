using System;
using NDesk.Options;
using GitTfs.Core;

namespace GitTfs.Commands
{
    public class SyncOptions
    {
        // Constants
        private const int DefaultLockTimeoutSeconds = 300;  // 5 minutes
        public const int MaxLockAgeSeconds = 7200;  // 2 hours
        private const string DefaultLockProvider = "file";

        public SyncOptions()
        {
            // Default values
            LockTimeout = DefaultLockTimeoutSeconds;
            MaxLockAge = MaxLockAgeSeconds;
            AutoMerge = true;
            LockProvider = DefaultLockProvider;
        }

        // Direction options
        public bool FromTfvc { get; set; }
        public bool ToTfvc { get; set; }
        public bool DryRun { get; set; }
        public bool RepairNotes { get; set; }
        public bool Resolve { get; set; }
        public bool AutoMerge { get; set; }

        // Workspace initialization
        public bool InitWorkspace { get; set; }
        public bool InitOnly { get; set; }  // Only create folder structure, no clone
        public string TfvcUrl { get; set; }
        public string TfvcPath { get; set; }
        public string GitRemoteUrl { get; set; }
        public bool AutoInstallGit { get; set; }
        public bool UseQuickClone { get; set; }  // Use quick-clone instead of clone for faster initialization
        public bool AutoPush { get; set; }  // Automatically push to origin after successful initialization

        // Locking options
        public string LockProvider { get; set; }
        public int LockTimeout { get; set; }
        public int MaxLockAge { get; set; }
        public bool ForceUnlock { get; set; }
        public bool NoLock { get; set; }
        public string LockFile { get; set; }

        // Workspace and path options
        public string WorkspaceRoot { get; set; }
        public string WorkspaceName { get; set; }
        public string ToolsDir { get; set; }
        public string GitExe { get; set; }
        public string GitIgnoreTemplate { get; set; }
        public string GitAuthToken { get; set; }  // PAT token for authenticated Git operations
        public string InitialBranch { get; set; }  // Initial branch name (passed to git-init, requires Git >= 2.28.0)

        // Path exclusion options
        public string ExcludePaths { get; set; }
        public bool SyncPipelineFiles { get; set; }

        // Environment options
        public string Environment { get; set; }

        public OptionSet GetOptionSet()
        {
            return new OptionSet
            {
                // Core sync options
                { "from-tfvc", "Sync from TFVC to Git only", v => FromTfvc = v != null },
                { "to-tfvc", "Sync from Git to TFVC only", v => ToTfvc = v != null },
                { "dry-run", "Show what would happen without making changes", v => DryRun = v != null },
                { "repair-notes", "Repair broken git notes metadata", v => RepairNotes = v != null },
                { "resolve", "Resume sync after manual conflict resolution", v => Resolve = v != null },
                { "auto-merge", "Attempt automatic merge conflict resolution (default: true)", v => AutoMerge = v != null },
                { "no-auto-merge", "Disable automatic merging, always pause on conflicts", v => AutoMerge = v == null },

                // Workspace initialization
                { "init-workspace", "Initialize workspace structure (with --init-only: folders only, default: full setup with clone)", v => InitWorkspace = v != null },
                { "init-only", "Only create workspace folder structure, skip repository clone/setup", v => InitOnly = v != null },
                { "tfvc-url=", "TFS collection URL (required for full init unless --init-only)", v => TfvcUrl = v },
                { "tfvc-path=", "TFVC repository path (required for full init unless --init-only)", v => TfvcPath = v },
                { "git-remote-url=", "Git remote URL (required for full init unless --init-only)", v => GitRemoteUrl = v },
                { "auto-install-git", "Auto-download and install Git Portable if not detected (~45MB download from GitHub)", v => AutoInstallGit = v != null },
                { "use-quick-clone", "Use quick-clone (shallow) instead of full clone for faster initialization", v => UseQuickClone = v != null },
                { "auto-push", "Automatically push to Git remote after successful initialization", v => AutoPush = v != null },

                // Locking options
                { "lock-provider=", "Lock mechanism (currently only 'file' is supported)", v => LockProvider = v },
                { "lock-timeout=", "How long to wait for lock in seconds (default: 300, max: 7200)", v => LockTimeout = int.Parse(v) },
                { "max-lock-age=", "Consider locks older than this stale in seconds (default: 7200)", v => MaxLockAge = int.Parse(v) },
                { "force-unlock", "Forcibly remove stale lock", v => ForceUnlock = v != null },
                { "no-lock", "Skip locking (for testing only)", v => NoLock = v != null },
                { "lock-file=", "Path to lock file (for file-based locking)", v => LockFile = v },

                // Workspace and path options
                { "workspace-root=", "Root directory for all sync workspaces", v => WorkspaceRoot = v },
                { "workspace-name=", "Specific workspace name", v => WorkspaceName = v },
                { "tools-dir=", "Directory containing tools", v => ToolsDir = v },
                { "git-exe=", "Path to git executable", v => GitExe = v },
                { "gitignore-template=", "Apply built-in template or custom path", v => GitIgnoreTemplate = v },
                { "git-auth-token=", "Personal Access Token (PAT) for authenticated Git operations (push/fetch)", v => GitAuthToken = v },
                { "initial-branch=", "Set initial branch name (passed to git-init, requires Git >= 2.28.0, default: main)", v => InitialBranch = v },

                // Path exclusion options
                { "exclude-paths=", "Exclude files/paths from sync", v => ExcludePaths = v },
                { "sync-pipeline-files", "Explicitly allow syncing of pipeline definition files", v => SyncPipelineFiles = v != null },

                // Environment options
                { "environment=", "Force environment detection (auto|services|server)", v => Environment = v },
            };
        }

        public void Validate()
        {
            // Validate lock timeout
            if (LockTimeout > MaxLockAgeSeconds)
            {
                throw new GitTfsException(
                    $"ERROR: --lock-timeout cannot exceed {MaxLockAgeSeconds} seconds (2 hours).\n" +
                    $"Reason: Prevents indefinite pipeline hangs and ensures stale lock detection.\n" +
                    $"Specified: {LockTimeout} seconds ({LockTimeout / 3600.0:F1} hours)");
            }

            // Validate init-workspace parameters
            // Full init requires TFVC and Git URLs when workspace name is provided
            if (InitWorkspace && !InitOnly && !string.IsNullOrEmpty(WorkspaceName))
            {
                // Full init requires TFVC and Git URLs
                if (string.IsNullOrEmpty(TfvcUrl))
                {
                    throw new GitTfsException(
                        "ERROR: --tfvc-url is required for full workspace initialization.\n" +
                        "Use --init-only if you only want to create the folder structure.");
                }
                if (string.IsNullOrEmpty(TfvcPath))
                {
                    throw new GitTfsException(
                        "ERROR: --tfvc-path is required for full workspace initialization.\n" +
                        "Use --init-only if you only want to create the folder structure.");
                }
                if (string.IsNullOrEmpty(GitRemoteUrl))
                {
                    throw new GitTfsException(
                        "ERROR: --git-remote-url is required for full workspace initialization.\n" +
                        "Use --init-only if you only want to create the folder structure.");
                }
            }

            // Validate direction options
            if (FromTfvc && ToTfvc)
            {
                throw new GitTfsException("ERROR: Cannot specify both --from-tfvc and --to-tfvc");
            }

            // Validate lock provider
            if (LockProvider != "file")
            {
                throw new GitTfsException($"ERROR: Unsupported lock provider '{LockProvider}'. Currently only 'file' is supported.");
            }
        }
    }
}
