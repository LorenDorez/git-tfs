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
  --init-workspace              Initialize workspace structure
                                  Creates folder structure and optionally installs Git Portable
                                  Provides instructions to download git-tfs ZIP from GitHub releases
                                  No TFVC/Git parameters needed - just sets up the environment
  --workspace-name=VALUE        Name for the agent workspace (optional - if omitted, only creates base structure)
  --auto-install-git            Auto-download and install Git Portable if not detected (~45MB download from GitHub)
  
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

**Git Requirement**: git-tfs requires Git to be installed and available. The sync command can optionally auto-install Git Portable for you.

**Git Auto-Installation**: Use the `--auto-install-git` flag with `--init-workspace` to automatically download and install Git Portable (~45MB) from official GitHub releases if Git is not detected. This is opt-in for security - you must explicitly enable it.

**CRITICAL**: This command **requires git notes to be enabled** to function correctly. Git notes are used to store TFS metadata without modifying commit messages, which would change commit SHAs and break synchronization tracking.

Enable git notes:
```bash
git config git-tfs.use-notes true
```

## Repository Initialization Methods

After creating an agent workspace with `--init-workspace`, you must initialize the Git repository to establish the baseline connection to TFVC. There are three methods available, each suited for different scenarios:

### Method 1: init + fetch (Standard Approach)

The traditional two-step approach gives you full control over the initialization process:

```powershell
cd _workspace\_agents\MyProject\repo
git-tfs init <tfvc-url> <tfvc-path>
git-tfs fetch
```

**Use when:**
- You need fine-grained control over the initialization
- You want to configure additional settings between init and fetch
- You're setting up a new repository from scratch

**Advantages:**
- Complete history from TFVC
- Full control over the process
- Can add custom git configurations between steps

### Method 2: clone (One-Step Full Clone)

Creates the Git repository and fetches the complete TFVC history in a single command:

```powershell
cd _workspace\_agents\MyProject
git-tfs clone <tfvc-url> <tfvc-path> repo
```

**Use when:**
- You want a simple one-command setup
- You need the complete TFVC history
- The repository is relatively small or you have good network bandwidth

**Advantages:**
- Simplest command - one step instead of two
- Complete TFVC history included
- Creates the repository directory if it doesn't exist

### Method 3: quick-clone (Fast Shallow Clone)

Creates a shallow clone with only recent TFVC history for faster initialization:

```powershell
cd _workspace\_agents\MyProject
git-tfs quick-clone <tfvc-url> <tfvc-path> repo
```

**Use when:**
- You need fast initialization
- The full TFVC history is not required
- The repository is large and full history would take too long
- You're setting up temporary or test environments

**Advantages:**
- Fastest initialization method
- Smaller disk footprint
- Good for CI/CD pipelines where full history isn't needed

**Trade-offs:**
- Limited history available
- Cannot fetch older changesets without re-cloning

### Adding .gitignore Files

All three initialization methods support the `--gitignore-template` option to automatically add a .gitignore file:

```powershell
# With init
git-tfs init --gitignore-template=VisualStudio <tfvc-url> <tfvc-path>

# With clone
git-tfs clone --gitignore-template=VisualStudio <tfvc-url> <tfvc-path> repo

# With quick-clone
git-tfs quick-clone --gitignore-template=VisualStudio <tfvc-url> <tfvc-path> repo
```

**Available built-in templates:**
- `VisualStudio` - C#/.NET projects (bin/, obj/, packages/, .vs/, etc.)
- `Java` - Java projects (target/, build/, .class files, IDE files)
- `Node` - Node.js/JavaScript projects (node_modules/, dist/, .env)
- `Python` - Python projects (__pycache__/, venv/, .pyc files)
- Custom path - Provide a path to your own .gitignore file template

**Example with custom template:**
```powershell
git-tfs init --gitignore-template="C:\Templates\custom.gitignore" <tfvc-url> <tfvc-path>
```

The .gitignore file helps prevent committing build artifacts, dependencies, and other files that shouldn't be in version control.

## Examples

### Example 1: Initialize Workspace

The `--init-workspace` flag creates the folder structure and optionally installs Git Portable. It does not run any git-tfs commands - those are run manually in the next steps.

```powershell
# Download and extract git-tfs ZIP from GitHub releases
$rootDir = "C:\TFVC-to-Git-Migration"
New-Item -ItemType Directory -Path $rootDir -Force | Out-Null

$zipPath = "$rootDir\git-tfs.zip"
Invoke-WebRequest -Uri "https://github.com/LorenDorez/git-tfs/releases/latest/download/git-tfs.zip" `
  -OutFile $zipPath

# Extract to root directory
Expand-Archive -Path $zipPath -DestinationPath $rootDir -Force
Remove-Item $zipPath  # Clean up ZIP file

# Initialize workspace (creates _workspace subfolder with all workspace content)
cd $rootDir
.\git-tfs.exe sync --init-workspace `
  --workspace-name="MyProject" `
  --auto-install-git

# This creates the following structure:
#   C:\TFVC-to-Git-Migration\           (root folder)
#   ├── git-tfs.exe                      (from extracted ZIP - run from here)
#   ├── GitTfs.dll                       (dependencies from ZIP)
#   └── _workspace\                      (created by --init-workspace)
#       ├── _tools\                      (shared tools)
#       │   └── git\                     (Git Portable auto-installed)
#       └── _agents\                     (agent workspaces)
#           └── MyProject\
#               ├── repo\                (Git repository)
#               └── locks\               (lock files)

# Use the git-tfs.exe from the root directory (where it was extracted)
$gitTfs = "$rootDir\git-tfs.exe"

# Now manually initialize the Git repository using one of the methods below
cd C:\TFVC-to-Git-Migration\_workspace\_agents\MyProject\repo

# Method 1: init + fetch (standard approach)
& $gitTfs init https://dev.azure.com/MyOrg $/MyProject/Main
& $gitTfs fetch

# Method 2: clone (creates git repo + fetches history in one command)
# & $gitTfs clone https://dev.azure.com/MyOrg $/MyProject/Main .

# Method 3: quick-clone (faster, creates shallow clone with recent history)
# & $gitTfs quick-clone https://dev.azure.com/MyOrg $/MyProject/Main .

# Optional: Add .gitignore file using built-in template
& $gitTfs init --gitignore-template=VisualStudio  # Or use with clone/quick-clone

# Configure git remote and push
git remote add origin https://github.com/MyOrg/MyRepo.git
git push origin main
```

### Example 2: Initialize Base Workspace Structure Only

If you want to create just the base workspace structure without a specific agent workspace:

```powershell
# Download and extract git-tfs ZIP from GitHub releases (see Example 1)
$rootDir = "C:\TFVC-to-Git-Migration"
cd $rootDir

# Initialize base structure only (no --workspace-name)
.\git-tfs.exe sync --init-workspace --auto-install-git

# This creates:
#   C:\TFVC-to-Git-Migration\
#   ├── git-tfs.exe
#   └── _workspace\
#       ├── _tools\git\              # Git Portable (auto-installed)
#       └── _agents\                 # Empty - ready for agent workspaces

# Later, create agent workspaces as needed
.\git-tfs.exe sync --init-workspace --workspace-name="MyProject"
.\git-tfs.exe sync --init-workspace --workspace-name="AnotherProject"
```

### Example 3: Add Additional Agent Workspaces

After initial setup, you can add more agent workspaces. Tools are reused automatically:

```powershell
# Add a new agent workspace (reuses existing tools in _workspace\_tools)
$rootDir = "C:\TFVC-to-Git-Migration"
cd $rootDir

.\git-tfs.exe sync --init-workspace --workspace-name="AnotherProject"

# Then manually initialize git-tfs in the new workspace using one of these methods:
cd _workspace\_agents\AnotherProject\repo

# Option A: init + fetch
..\..\..\..\git-tfs.exe init https://dev.azure.com/MyOrg $/AnotherProject/Main
..\..\..\..\git-tfs.exe fetch

# Option B: clone (faster, single command)
# cd _workspace\_agents\AnotherProject
# ..\..\..\..\git-tfs.exe clone https://dev.azure.com/MyOrg $/AnotherProject/Main repo

# Option C: quick-clone (fastest, shallow history)
# cd _workspace\_agents\AnotherProject
# ..\..\..\..\git-tfs.exe quick-clone https://dev.azure.com/MyOrg $/AnotherProject/Main repo
```

This creates:
```
C:\TFVC-to-Git-Migration\
├── git-tfs.exe                      # From extracted ZIP - run from here
├── GitTfs.dll                       # Dependencies from ZIP
└── _workspace\                      # Created by --init-workspace
    ├── _tools\
    │   └── git\                     # Git Portable (auto-installed)
    │       └── bin\git.exe
    └── _agents\
        ├── MyProject\               # First agent workspace
        │   ├── repo\                # Git repository
        │   └── locks\               # Lock files
        │       └── MyProject.lock
        └── AnotherProject\          # Second agent workspace
            ├── repo\                # Git repository
            └── locks\               # Lock files
                └── AnotherProject.lock
```

### Example 4: TFVC → Git Sync (Azure DevOps Pipeline)

After initialization, use the git-tfs.exe from the root directory in your pipeline:

```powershell
$rootDir = "C:\TFVC-to-Git-Migration"
$workspaceName = "MyProject"
$gitTfsExe = "$rootDir\git-tfs.exe"
$agentWorkspace = "$rootDir\_workspace\_agents\$workspaceName"
$lockFile = "$agentWorkspace\locks\$workspaceName.lock"

# Sync from TFVC to Git with file-based locking
& $gitTfsExe sync --from-tfvc `
  --workspace-name="$workspaceName" `
  --lock-provider=file `
  --lock-file="$lockFile" `
  --lock-timeout=300

# Push to Git remote with notes
cd "$agentWorkspace\repo"
git push origin main --force-with-lease
git push origin refs/notes/tfvc-sync:refs/notes/tfvc-sync --force
```

### Example 5: Git → TFVC Sync

```powershell
$rootDir = "C:\TFVC-to-Git-Migration"
$workspaceName = "MyProject"
$gitTfsExe = "$rootDir\git-tfs.exe"
$agentWorkspace = "$rootDir\_workspace\_agents\$workspaceName"
$lockFile = "$agentWorkspace\locks\$workspaceName.lock"

# Sync from Git to TFVC with file-based locking
& $gitTfsExe sync --to-tfvc `
  --workspace-name="$workspaceName" `
  --lock-provider=file `
  --lock-file="$lockFile" `
  --lock-timeout=300
```

### Example 6: Bidirectional Sync

```powershell
$rootDir = "C:\TFVC-to-Git-Migration"
$workspaceName = "MyProject"
$gitTfsExe = "$rootDir\git-tfs.exe"
$agentWorkspace = "$rootDir\_workspace\_agents\$workspaceName"
$lockFile = "$agentWorkspace\locks\$workspaceName.lock"

# Bidirectional sync (TFVC → Git, then Git → TFVC)
& $gitTfsExe sync `
  --workspace-name="$workspaceName" `
  --lock-provider=file `
  --lock-file="$lockFile" `
  --lock-timeout=300
```

### Example 6: Dry Run

See what would happen without making changes:

```bash
git tfs sync --from-tfvc --dry-run
```

### Example 7: Force Unlock Stale Lock

If a pipeline crashed and left a stale lock:

```bash
git tfs sync --force-unlock --lock-file="C:\TFVC-to-Git-Migration\MyProject\locks\MyProject.lock"
```

## Git Auto-Installation

The `--auto-install-git` flag enables automatic download and installation of Git Portable if Git is not detected on the system.

### How It Works

1. **Detection**: Checks for Git in PATH and in the tools directory
2. **Download**: Downloads latest Git Portable from official GitHub releases (git-for-windows/git)
3. **Verification**: Validates download integrity using SHA256 checksums
4. **Extraction**: Extracts to `{WorkspaceRoot}/_tools/git-portable/`
5. **Validation**: Verifies installation by checking for git.exe

### Safety Features

- **Opt-in only**: Must explicitly specify `--auto-install-git` flag
- **Official source**: Downloads only from GitHub official releases
- **Checksum verification**: Validates SHA256 checksum before extraction
- **Version check**: Verifies minimum Git version (2.34.0+)
- **Progress indicators**: Shows download and extraction progress
- **Graceful fallback**: Provides manual installation instructions if auto-install fails

### Download Details

- **Source**: https://github.com/git-for-windows/git/releases
- **Size**: ~45MB (PortableGit)
- **Format**: Self-extracting 7-Zip archive (.7z.exe)
- **Installation time**: 1-3 minutes depending on network speed

### Example Output

```
🚀 Initializing workspace structure...

⚠️  Git not detected on this system

📦 Auto-installing Git Portable...
   Source: GitHub official releases (git-for-windows/git)
   Size: ~45MB download
   Version: 2.43.0
   Downloading: PortableGit-2.43.0-64-bit.7z.exe...
   Progress: 100% (45.2MB / 45.2MB) ✅

🔐 Verifying download integrity...
✅ Checksum verified

📂 Extracting Git Portable...
   Extracting... ✅

✅ Git Portable installed successfully: C:\TFVC-to-Git-Migration\_tools\git-portable
   Git executable: C:\TFVC-to-Git-Migration\_tools\git-portable\bin\git.exe
```

### Troubleshooting

**Network Issues**: If download fails, retry or install Git manually from https://git-scm.com/download/win

**Antivirus Blocking**: Some antivirus software may flag downloads. Use official source and checksum verification to ensure safety.

**Firewall Restrictions**: In environments that block external downloads, install Git manually and skip `--auto-install-git`.

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
$agentWorkspace = "$(WorkspaceRoot)\$(WorkspaceName)"
$lockFile = "$agentWorkspace\locks\$(WorkspaceName).lock"

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
      $agentWorkspace = "$(WorkspaceRoot)\$(WorkspaceName)"
      $lockFile = "$agentWorkspace\locks\$(WorkspaceName).lock"
      
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
