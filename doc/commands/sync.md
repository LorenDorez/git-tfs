## Summary

The `sync` command provides robust bidirectional synchronization between Git and TFVC with built-in locking, workspace initialization, and conflict detection.

## Synopsis

```
Usage: git-tfs sync [options]

Options:
  -h, -H, --help                Show help
  --from-tfvc                   Sync from TFVC to Git only
  --to-tfvc                     Sync from Git to TFVC only
  --dry-run                     Show what would happen without making changes
  --repair-notes                Repair broken git notes metadata
  --resolve                     Resume sync after manual conflict resolution
  --auto-merge                  Attempt automatic merge conflict resolution (default: true)
  --no-auto-merge               Disable automatic merging, always pause on conflicts
  
Workspace Initialization:
  --init-workspace              Initialize workspace structure and self-install
  --tfvc-url=VALUE              TFS collection URL (required with --init-workspace)
  --tfvc-path=VALUE             TFVC repository path (required with --init-workspace)
  --git-remote-url=VALUE        Git remote URL (required with --init-workspace)
  
Locking Options:
  --lock-provider=VALUE         Lock mechanism (currently only 'file' is supported)
  --lock-timeout=VALUE          How long to wait for lock in seconds (default: 300, max: 7200)
  --max-lock-age=VALUE          Consider locks older than this stale in seconds (default: 7200)
  --force-unlock                Forcibly remove stale lock
  --no-lock                     Skip locking (for testing only, not recommended for production)
  --lock-file=VALUE             Path to lock file (for file-based locking)
  
Workspace Options:
  --workspace-root=VALUE        Root directory for all sync workspaces
  --workspace-name=VALUE        Specific workspace name
  --tools-dir=VALUE             Directory containing tools
  --git-exe=VALUE               Path to git executable
  --gitignore-template=VALUE    Apply built-in template or custom path
  
Path Exclusion Options:
  --exclude-paths=VALUE         Exclude files/paths from sync
  --sync-pipeline-files         Explicitly allow syncing of pipeline definition files
  
Environment Options:
  --environment=VALUE           Force environment detection (auto|services|server)
```

## Prerequisites

**CRITICAL**: This command **requires git notes to be enabled** to function correctly. Git notes are used to store TFS metadata without modifying commit messages, which would change commit SHAs and break synchronization tracking.

Enable git notes:
```bash
git config git-tfs.use-notes true
```

## Examples

### Example 1: Initialize Workspace (Self-Installing)

This is the recommended first step. The `--init-workspace` flag will:
- Create the recommended directory structure
- Copy git-tfs.exe to a persistent location
- Set up the workspace for the specified TFVC path

```powershell
# Download git-tfs.exe to a temporary location
$tempDir = $env:AGENT_TEMPDIRECTORY
Invoke-WebRequest -Uri "https://github.com/LorenDorez/git-tfs/releases/latest/download/git-tfs.exe" `
  -OutFile "$tempDir\git-tfs.exe"

# Initialize workspace (git-tfs will self-install)
& "$tempDir\git-tfs.exe" sync --init-workspace `
  --workspace-root="C:\TFVC-to-Git-Migration" `
  --workspace-name="MyProject" `
  --tfvc-url="https://dev.azure.com/MyOrg" `
  --tfvc-path="$/MyProject/Main" `
  --git-remote-url="https://github.com/MyOrg/MyRepo.git"

# Use the persistent git-tfs.exe from now on
$gitTfs = "C:\TFVC-to-Git-Migration\_tools\git-tfs\git-tfs.exe"
```

This creates:
```
C:\TFVC-to-Git-Migration\
├── _tools\
│   └── git-tfs\
│       └── git-tfs.exe         # Persistent installation
├── _agents\
│   └── MyProject\              # Workspace directory
├── _locks\
│   └── MyProject.lock          # Lock file location
```

### Example 2: TFVC → Git Sync (Azure DevOps Pipeline)

After initialization, use the persistent git-tfs.exe in your pipeline:

```powershell
$workspaceRoot = "C:\TFVC-to-Git-Migration"
$workspaceName = "MyProject"
$gitTfsExe = "$workspaceRoot\_tools\git-tfs\git-tfs.exe"
$lockFile = "$workspaceRoot\_locks\$workspaceName.lock"

# Sync from TFVC to Git with file-based locking
& $gitTfsExe sync --from-tfvc `
  --workspace-root="$workspaceRoot" `
  --workspace-name="$workspaceName" `
  --lock-provider=file `
  --lock-file="$lockFile" `
  --lock-timeout=300

# Push to Git remote with notes
cd "$workspaceRoot\_agents\$workspaceName"
git push origin main --force-with-lease
git push origin refs/notes/tfvc-sync:refs/notes/tfvc-sync --force
```

### Example 3: Git → TFVC Sync

```powershell
$workspaceRoot = "C:\TFVC-to-Git-Migration"
$workspaceName = "MyProject"
$gitTfsExe = "$workspaceRoot\_tools\git-tfs\git-tfs.exe"
$lockFile = "$workspaceRoot\_locks\$workspaceName.lock"

# Sync from Git to TFVC with file-based locking
& $gitTfsExe sync --to-tfvc `
  --workspace-root="$workspaceRoot" `
  --workspace-name="$workspaceName" `
  --lock-provider=file `
  --lock-file="$lockFile" `
  --lock-timeout=300
```

### Example 4: Bidirectional Sync

```powershell
$workspaceRoot = "C:\TFVC-to-Git-Migration"
$workspaceName = "MyProject"
$gitTfsExe = "$workspaceRoot\_tools\git-tfs\git-tfs.exe"
$lockFile = "$workspaceRoot\_locks\$workspaceName.lock"

# Bidirectional sync (TFVC → Git, then Git → TFVC)
& $gitTfsExe sync `
  --workspace-root="$workspaceRoot" `
  --workspace-name="$workspaceName" `
  --lock-provider=file `
  --lock-file="$lockFile" `
  --lock-timeout=300
```

### Example 5: Dry Run

See what would happen without making changes:

```bash
git tfs sync --from-tfvc --dry-run
```

### Example 6: Force Unlock Stale Lock

If a pipeline crashed and left a stale lock:

```bash
git tfs sync --force-unlock --lock-file="C:\TFVC-to-Git-Migration\_locks\MyProject.lock"
```

## Locking Mechanism

The sync command uses file-based locking to prevent race conditions when multiple pipelines try to sync simultaneously.

### Lock Behavior

1. **Acquire**: Checks if lock file exists and is not stale
2. **Wait**: If locked, waits up to `--lock-timeout` seconds (max 7200 = 2 hours)
3. **Stale Detection**: If lock file is older than `--max-lock-age` (default 2 hours), auto-removes it
4. **Release**: Deletes lock file on successful completion
5. **Crash Recovery**: If process crashes, lock becomes stale after 2 hours

### Lock File Format

```json
{
  "version": "1.0",
  "pid": 12345,
  "hostname": "BUILD-AGENT-01",
  "workspace": "MyProject",
  "acquired_at": "2025-01-15T10:30:00Z",
  "acquired_by": "TFVC-to-Git Pipeline",
  "pipeline_id": "123",
  "build_number": "20250115.1",
  "direction": "tfvc-to-git"
}
```

## Workflow

### Bidirectional Sync Flow

1. **Acquire lock** (if not `--no-lock`)
2. **Verify git notes** are enabled
3. **Fetch from TFVC** (using existing `git tfs fetch` command)
4. **Check in to TFVC** (using existing `git tfs rcheckin` command)
5. **Release lock**

### Error Handling

The sync command provides actionable error messages:

```
❌ ERROR: Git notes are required for 'git tfs sync' to function.

Git notes allow tracking TFS metadata without modifying commit messages,
which would change commit SHAs and break synchronization tracking.

To enable git notes:
  git config git-tfs.use-notes true
```

```
❌ Failed to acquire lock within 300 seconds
   Lock held by: BUILD-AGENT-02 (PID 5432)
   Acquired at: 2025-01-15 10:30:00 UTC
```

## Azure DevOps Pipeline Integration

### Classic Pipeline (TFVC Repository)

**Get Sources:**
- Repository type: `Team Foundation Version Control`
- TFVC path: `$/MyProject/Main`
- Clean: `false` (for persistent workspace)

**Variables:**
- `WorkspaceRoot`: `C:\TFVC-to-Git-Migration`
- `WorkspaceName`: `MyProject`

**PowerShell Task:**
```powershell
$gitTfsExe = "$(WorkspaceRoot)\_tools\git-tfs\git-tfs.exe"
$lockFile = "$(WorkspaceRoot)\_locks\$(WorkspaceName).lock"

& $gitTfsExe sync --from-tfvc `
  --workspace-root="$(WorkspaceRoot)" `
  --workspace-name="$(WorkspaceName)" `
  --lock-provider=file `
  --lock-file="$lockFile"
```

### YAML Pipeline (Git Repository)

**Note**: YAML pipelines work with Git repositories. For TFVC → Git sync, use a Classic pipeline.

```yaml
trigger:
  branches:
    include:
      - main

pool:
  name: 'Self-Hosted-Windows'

variables:
  WorkspaceRoot: 'C:\TFVC-to-Git-Migration'
  WorkspaceName: 'MyProject'

steps:
- task: PowerShell@2
  displayName: 'Sync Git to TFVC'
  inputs:
    targetType: 'inline'
    script: |
      $gitTfsExe = "$(WorkspaceRoot)\_tools\git-tfs\git-tfs.exe"
      $lockFile = "$(WorkspaceRoot)\_locks\$(WorkspaceName).lock"
      
      & $gitTfsExe sync --to-tfvc `
        --workspace-root="$(WorkspaceRoot)" `
        --workspace-name="$(WorkspaceName)" `
        --lock-provider=file `
        --lock-file="$lockFile"
```

## Limitations

- **Single-branch sync only**: MVP supports one TFVC path ↔ one Git repository
- **File-based locking only**: Future versions may support Azure Blob, Redis, etc.
- **Windows only**: Requires Windows environment with Visual Studio Team Explorer
- **Requires git notes**: Cannot function without git notes enabled

## See Also

- [fetch](fetch.md) - Fetch changesets from TFVC
- [rcheckin](rcheckin.md) - Check in commits to TFVC
- [Git Notes Documentation](https://git-scm.com/docs/git-notes)
