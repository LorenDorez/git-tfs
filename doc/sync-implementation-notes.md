# Git TFS Sync Command - Implementation Notes

## Overview

This document describes the implementation of the `git tfs sync` command added to support robust bidirectional synchronization between Git and TFVC.

## Implementation Summary

- **Total Lines**: ~1,378 (1,028 production code + tests, 350 documentation)
- **Files Added**: 7 (4 production, 2 test, 1 documentation)
- **Approach**: Minimal changes - reuses existing Fetch and Rcheckin commands
- **Test Coverage**: Unit tests for options validation and locking mechanism
- **Documentation**: Comprehensive with Azure DevOps pipeline examples

## Design Principles

### 1. Minimal Changes
Rather than reimplementing sync logic, this implementation:
- Delegates to existing `Fetch` command for TFVC→Git sync
- Delegates to existing `Rcheckin` command for Git→TFVC sync
- Reuses existing `GitNotesManager` for metadata tracking
- Builds on existing authentication and connection handling

### 2. Extensibility
Designed to support future enhancements:
- `ILockProvider` interface allows multiple lock providers (currently: file-based)
- Options structure supports path exclusions (implementation deferred)
- Environment detection hooks prepared (implementation deferred)

### 3. Safety First
- Git notes requirement prevents commit SHA changes
- File-based locking prevents race conditions
- Stale lock detection prevents indefinite hangs
- Comprehensive validation prevents invalid configurations
- Dry-run mode for testing

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                     Sync Command                         │
│  ┌────────────────────────────────────────────────────┐ │
│  │  Options Validation (SyncOptions)                  │ │
│  │  - Lock timeout validation                         │ │
│  │  - Required parameter checks                       │ │
│  │  - Direction validation                            │ │
│  └────────────────────────────────────────────────────┘ │
│                           │                              │
│                           ▼                              │
│  ┌────────────────────────────────────────────────────┐ │
│  │  Lock Acquisition (FileLockProvider)               │ │
│  │  - Check for stale locks                           │ │
│  │  - Acquire lock or wait                            │ │
│  │  - Store lock metadata                             │ │
│  └────────────────────────────────────────────────────┘ │
│                           │                              │
│                           ▼                              │
│  ┌────────────────────────────────────────────────────┐ │
│  │  Git Notes Verification                            │ │
│  │  - Check if git notes enabled                      │ │
│  │  - Provide clear error if disabled                 │ │
│  └────────────────────────────────────────────────────┘ │
│                           │                              │
│                           ▼                              │
│  ┌────────────────────────────────────────────────────┐ │
│  │  Sync Operations                                   │ │
│  │  ┌──────────────────┐  ┌──────────────────┐        │ │
│  │  │ TFVC → Git       │  │ Git → TFVC       │        │ │
│  │  │ (Fetch command)  │  │ (Rcheckin cmd)   │        │ │
│  │  └──────────────────┘  └──────────────────┘        │ │
│  └────────────────────────────────────────────────────┘ │
│                           │                              │
│                           ▼                              │
│  ┌────────────────────────────────────────────────────┐ │
│  │  Lock Release                                      │ │
│  │  - Delete lock file                                │ │
│  └────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────┘
```

## Component Descriptions

### 1. Sync.cs (363 lines)
Main command implementation with:
- Options validation
- Lock acquisition/release
- Git notes verification
- Sync orchestration
- Workspace initialization (`--init-workspace`)
- Error handling with actionable messages

### 2. SyncOptions.cs (136 lines)
Command-line options with:
- Named constants for magic numbers
- Comprehensive validation
- Default values
- Help text generation via OptionSet

### 3. ILockProvider.cs (63 lines)
Interface defining lock provider contract:
- `TryAcquireLock()` - Acquire with timeout
- `ReleaseLock()` - Release lock
- `IsStale()` - Check if lock is stale
- `ForceUnlock()` - Force remove lock
- `GetLockInfo()` - Get lock metadata

### 4. FileLockProvider.cs (179 lines)
File-based implementation of ILockProvider:
- Creates lock file with JSON metadata
- Detects and removes stale locks
- Waits/retries on lock contention
- Logs lock operations to Trace

### 5. SyncOptionsTest.cs (101 lines)
Unit tests covering:
- Default value initialization
- Validation constraints
- Error messages
- Valid configuration scenarios

### 6. FileLockProviderTest.cs (186 lines)
Unit tests covering:
- Lock acquisition
- Lock release
- Stale lock detection
- Force unlock
- Lock metadata retrieval
- Directory creation
- Cleanup via IDisposable

## Lock File Format

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

## Error Handling

### Git Notes Not Enabled
```
❌ ERROR: Git notes are required for 'git tfs sync' to function.

Git notes allow tracking TFS metadata without modifying commit messages,
which would change commit SHAs and break synchronization tracking.

To enable git notes:
  git config git-tfs.use-notes true

After enabling, you may need to:
  1. Re-clone/fetch to populate notes
  2. Push notes to remote: git push origin refs/notes/tfvc-sync
```

### Lock Timeout
```
❌ Failed to acquire lock within 300 seconds
   Lock held by: BUILD-AGENT-02 (PID 5432)
   Acquired at: 2025-01-15 10:30:00 UTC
```

### Invalid Configuration
```
ERROR: --lock-timeout cannot exceed 7200 seconds (2 hours).
Reason: Prevents indefinite pipeline hangs and ensures stale lock detection.
Specified: 10800 seconds (3.0 hours)
```

## Testing Strategy

### Unit Tests
Run on all platforms via CI:
```bash
dotnet test src/GitTfsTest/GitTfsTest.csproj --filter "FullyQualifiedName~Sync"
```

### Integration Tests (Windows)
Requires Windows environment with Visual Studio:
1. Build git-tfs
2. Test workspace initialization
3. Test TFVC→Git sync
4. Test Git→TFVC sync
5. Test bidirectional sync
6. Test lock contention scenarios

### Manual Testing
See `doc/commands/sync.md` for Azure DevOps pipeline examples

## Future Enhancements

### Path Exclusions (Deferred)
```csharp
// In SyncOptions.cs
public string ExcludePaths { get; set; }
public bool SyncPipelineFiles { get; set; }

// Implementation would use Minimatch or Regex
```

### Auto-Merge (Deferred)
```csharp
// In Sync.cs
private int AttemptAutoMerge(IGitTfsRemote remote)
{
    // Check for conflicts
    // If non-overlapping, auto-merge
    // If overlapping, pause for manual resolution
}
```

### Multiple Lock Providers (Deferred)
```csharp
// Azure Blob Lock Provider
public class AzureBlobLockProvider : ILockProvider
{
    // Uses Azure Blob Lease for distributed locking
}

// Redis Lock Provider
public class RedisLockProvider : ILockProvider
{
    // Uses RedLock algorithm
}
```

## Troubleshooting

### Build Issues
If build fails with "mono not found":
- This project requires Windows with Visual Studio
- Use `build.ps1` on Windows
- Or build via AppVeyor CI

### Test Failures
If tests fail on Linux:
- Unit tests should pass on all platforms
- Integration tests require Windows
- Use `[FactExceptOnUnix]` for Windows-only tests

### Lock File Issues
If lock file is not released:
- Check process didn't crash
- Use `--force-unlock` to remove stale lock
- Verify lock directory is writable

## Contributing

When extending this feature:
1. Maintain separation of concerns (no coupling)
2. Extract magic numbers to constants
3. Add unit tests for new functionality
4. Update documentation
5. Follow existing patterns (see Fetch.cs, Rcheckin.cs)

## References

- Original Issue: [GitHub Issue URL]
- Documentation: `doc/commands/sync.md`
- Example Tests: `src/GitTfsTest/Commands/SyncOptionsTest.cs`
- Example Command: `src/GitTfs/Commands/Fetch.cs`
