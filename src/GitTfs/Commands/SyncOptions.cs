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
        public string TfvcUrl { get; set; }
        public string TfvcPath { get; set; }
        public string GitRemoteUrl { get; set; }

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
                { "init-workspace", "Initialize workspace structure and self-install", v => InitWorkspace = v != null },
                { "tfvc-url=", "TFS collection URL (required with --init-workspace)", v => TfvcUrl = v },
                { "tfvc-path=", "TFVC repository path (required with --init-workspace)", v => TfvcPath = v },
                { "git-remote-url=", "Git remote URL (required with --init-workspace)", v => GitRemoteUrl = v },

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

            // Validate init-workspace requirements
            if (InitWorkspace)
            {
                if (string.IsNullOrEmpty(TfvcUrl) || string.IsNullOrEmpty(TfvcPath) || string.IsNullOrEmpty(GitRemoteUrl))
                {
                    throw new GitTfsException(
                        "ERROR: --init-workspace requires --tfvc-url, --tfvc-path, and --git-remote-url");
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
