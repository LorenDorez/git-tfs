# Git Notes for TFS Changeset Metadata

## Overview

Starting with this version, git-tfs can store TFS changeset metadata in **git notes** instead of embedding it directly in commit messages. This approach preserves commit SHAs and prevents divergence during bidirectional sync operations.

## What Are Git Notes?

Git notes are a mechanism to attach arbitrary metadata to commits without modifying the commit itself. They are stored in a separate reference (`refs/notes/tfvc-sync` for git-tfs) and do not affect commit SHAs.

## Benefits

### Preserved Commit SHAs
With git notes, commit SHAs remain unchanged even after syncing with TFS:
```bash
# Before rcheckin
git log --oneline
abc123 Fix bug in user login

# After rcheckin (with git notes)
git log --oneline
abc123 Fix bug in user login  # SHA unchanged!

# View the TFS metadata in notes
git notes --ref=refs/notes/tfvc-sync show abc123
changeset=12345
tfs_url=https://tfs.example.com/tfs
tfs_path=$/MyProject/trunk
synced_at=2025-12-04T10:30:00Z
```

### Clean Commit History
Commit messages are no longer polluted with git-tfs-id metadata:
```bash
# Legacy approach (commit message modified)
Fix bug in user login

git-tfs-id: [https://tfs.example.com/tfs]$/MyProject/trunk;C12345

# New approach (clean commit message)
Fix bug in user login

# Metadata stored in git notes instead
```

### No Force Push Required
After rcheckin, the remote and local repositories don't diverge:
```bash
# Legacy approach
git tfs rcheckin
# Remote has different SHAs, must force push
git push origin main --force  # Dangerous!

# New approach
git tfs rcheckin
# SHAs unchanged, normal push works
git push origin main  # Safe!
```

### Pull Requests Remain Valid
PRs reference specific commit SHAs which don't change, so:
- PR history remains valid
- Comments stay attached to the right commits
- Approvals don't get lost

## Configuration

### Enable Git Notes (Default)
Git notes are enabled by default. No configuration needed!

### Disable Git Notes (Use Legacy Behavior)
To use the old behavior (embed metadata in commit messages):
```bash
git config git-tfs.use-notes false
```

### Re-enable Git Notes
```bash
git config git-tfs.use-notes true
# or remove the config to use default
git config --unset git-tfs.use-notes
```

## Backward Compatibility

Git-tfs automatically supports both approaches:
- **Reading**: Checks git notes first, then falls back to commit messages
- **Writing**: Uses git notes when enabled, commit messages when disabled

This means:
- Existing repositories with git-tfs-id in commit messages continue to work
- You can switch between approaches at any time
- Mixed repositories (some commits with notes, some with commit message metadata) work correctly

## Working with Git Notes

### View Notes for a Commit
```bash
git notes --ref=refs/notes/tfvc-sync show <commit-sha>
```

### View All Notes
```bash
git log --show-notes=refs/notes/tfvc-sync
```

### Sync Notes with Remote
Git notes need to be explicitly pushed and fetched:

```bash
# Push notes
git push origin refs/notes/tfvc-sync

# Fetch notes
git fetch origin refs/notes/tfvc-sync:refs/notes/tfvc-sync
```

To automatically push/fetch notes, configure your remote:
```bash
# Add to .git/config or run:
git config remote.origin.push '+refs/notes/tfvc-sync:refs/notes/tfvc-sync'
git config remote.origin.fetch '+refs/notes/tfvc-sync:refs/notes/tfvc-sync'
```

## Migration from Legacy Approach

If you have an existing repository with git-tfs-id in commit messages, you don't need to migrate. The new code reads from both sources.

However, if you want to clean up your commit history, future commits will use git notes automatically (if enabled), while old commits will continue to be read from their commit messages.

## Troubleshooting

### Notes Not Showing Up
Make sure you've fetched the notes:
```bash
git fetch origin refs/notes/tfvc-sync:refs/notes/tfvc-sync
```

### Want to See Both Notes and Commit Messages
```bash
git log --show-notes=refs/notes/tfvc-sync --all
```

### Notes Not Pushing to Remote
Explicitly push the notes reference:
```bash
git push origin refs/notes/tfvc-sync
```

Or configure automatic push (see "Sync Notes with Remote" above).

## Technical Details

### Note Storage Format
Notes are stored in simple key=value format:
```
changeset=12345
tfs_url=https://tfs.example.com/tfs
tfs_path=$/MyProject/trunk
synced_at=2025-12-04T10:30:00Z
```

### Notes Reference
Git-tfs uses a custom notes reference: `refs/notes/tfvc-sync`

This keeps TFS metadata separate from other notes in your repository.

## See Also

- [Git Notes Documentation](https://git-scm.com/docs/git-notes)
- [git-tfs Configuration](config.md)
