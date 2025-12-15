# Git-tfs config values

Git-tfs uses git's configuration system to track most of the important
information about repositories.

## Repository-wide configuration

By default, git-tfs sets these configuration values for the repository
during `git tfs init`.

* `core.ignorecase` is set to `true`, in an attempt to deal with
  casing issues.
* `core.autocrlf` is set to `false`. This will make git preserve all
  characters (including CR and LF) in all files. The reason for doing
  this is to make the result of `git tfs clone` as nearly identical,
  byte-wise, as possible, to the version in TFS.

There is other git-tfs configuration values for the repository:

* `git-tfs.batch-size` define the number of changesets fetched in the same time
  from TFS (Could also be set with the `clone` command).
* `git-tfs.work-item-regex` could be used to define the regular expression to
  extract workitems reference from commit message.
* `git-tfs.workspace-dir` is used to define a new directory as the workspace
  used by TFS to circumvent problem with long paths.
  The path should be the shortest possible (i.e. "c:\w")
* `git-tfs.export-metadatas` is set to `true` to export all metadata in the
  commit messages.
* `git-tfs.disable-gitignore-support` define if git-tfs should use the
`.gitignore` file to filter changesets retrieved from TFVC.
* `git-tfs.use-notes` (default: `true`) controls whether TFS changeset metadata
  is stored in git notes (at `refs/notes/tfvc-sync`) instead of being embedded
  in commit messages. When enabled, commit messages remain clean and commit SHAs
  are preserved during bidirectional sync operations. Set to `false` to use the
  legacy behavior of embedding metadata in commit messages.

### Git User Configuration for Automated Commits

Git-tfs creates commits automatically in certain scenarios (e.g., initial `.gitignore` commit during `init` or `sync --init-workspace`). The author/committer identity for these commits is resolved using the following precedence order:

1. **Environment variables** (highest priority) - useful for CI/CD pipelines:
   - `GIT_TFS_USER_NAME`
   - `GIT_TFS_USER_EMAIL`
   
2. **Git-TFS config** - git-tfs-specific identity (checks local → global → system):
   - `git-tfs.user.name`
   - `git-tfs.user.email`
   
3. **Git config** - standard git identity (checks local → global → system):
   - `user.name`
   - `user.email`
   
4. **Hardcoded defaults** (lowest priority):
   - `"git-tfs"` / `"git-tfs@noreply.com"`

**Examples:**

```sh
# For CI/CD pipelines - set environment variables
export GIT_TFS_USER_NAME="Azure Pipeline"
export GIT_TFS_USER_EMAIL="pipeline@company.com"

# For agent machines - set global git-tfs config (applies to all repos)
git config --global git-tfs.user.name "Build Agent"
git config --global git-tfs.user.email "agent@example.com"

# For a specific repository - set local config
cd /path/to/repo
git config git-tfs.user.name "Custom Identity"
git config git-tfs.user.email "custom@example.com"
```

**Note:** During `sync --init-workspace`, git-tfs automatically sets the local `user.name` and `user.email` in the newly created repository using the resolved values from the precedence chain above. This ensures all future git operations in that repository use the appropriate identity.

## TFS Checkin Author Mapping

When checking in git commits to TFVC (using `checkin`, `rcheckin`, or `sync`), git-tfs determines which TFS user to check in as using the following precedence:

### Precedence Order

1. **Explicit `--author` option** (highest priority) - manually specify TFS user ID:
   ```sh
   git tfs rcheckin --author="DOMAIN\username"
   # or just username if your TFS is configured to resolve it
   git tfs rcheckin --author="username"
   ```

2. **Authors file mapping** - explicit mapping from git user to TFS user:
   ```sh
   # authors.txt format:
   DOMAIN\tfsuser = Git Name <git@email.com>
   
   git tfs rcheckin --authors="authors.txt"
   ```

3. **Automatic inference from git commit author** (NEW) - automatically infers TFS user from git commit:
   - Email `user@domain.com` → TFS user `user` (username extracted from email)
   - Name `DOMAIN\user` → TFS user `DOMAIN\user` (domain format preserved)
   - Name `username` → TFS user `username` (simple name preserved)
   - **For merge commits**: Uses the author of the **last parent** (the merged branch), not the merge commit author
     - This gives credit to the developer who did the actual work, not just who clicked "merge"
   - **TFS will resolve the username** against its configured authentication domain

4. **Authenticated user** (lowest priority) - uses the currently authenticated TFS user

### How Automatic Inference Works

The automatic inference extracts the **username** from the git commit author and lets TFS resolve it:

- `john@company.com` → `john` (TFS resolves to `COMPANY\john` if that's the auth domain)
- `john@dev.company.com` → `john` (TFS resolves based on its config)
- `COMPANY\john` → `COMPANY\john` (domain format preserved as-is)

**For merge commits**, git-tfs uses the author of the **last (most recent) parent commit** to determine the TFS author:

```
Main:    A ← B ← M (merge by John)
               ↗
Feature: C ← D (by Jane)

When checking in M:
- Merge commit author: John (who clicked merge)
- Last parent (D) author: Jane (who did the work)
- TFS author: Jane ✓ (gives credit for the actual work)
```

This approach is **simpler and more flexible** because:
- ✅ TFS handles domain resolution automatically based on its configuration
- ✅ Works with any TFS authentication setup (AD, Azure AD, etc.)
- ✅ No need to guess which domain to use
- ✅ Falls back gracefully if TFS can't resolve the username
- ✅ Merge commits credit the developer who did the work, not just the merger

### Examples

**Example 1: Automatic inference (no configuration needed)**
```sh
# Developer commits with:
git config user.name "John Doe"
git config user.email "john@company.com"
git commit -m "Fix bug"

# When checking in to TFVC, git-tfs automatically infers:
# john@company.com → username "john"
# TFS resolves "john" to the correct domain account
git tfs sync --to-tfvc
# ✅ Checked in as COMPANY\john (or whatever domain TFS uses)
```

**Example 2: Using domain format in git name**
```sh
git config user.name "COMPANY\john"
git config user.email "john@company.com"

git tfs sync --to-tfvc
# ✅ Checked in as COMPANY\john (domain format preserved)
```

**Example 3: Using authors file for custom mappings**
```sh
# authors.txt - useful when git username doesn't match TFS username:
COMPANY\john.doe = John Doe <john@company.com>
COMPANY\jane.smith = Jane Smith <jane@example.org>

git tfs rcheckin --authors="authors.txt"
# ✅ Uses explicit mappings from authors file
```

**Example 4: Simple username**
```sh
git config user.name "john"
git config user.email "john@company.com"

git tfs sync --to-tfvc
# ✅ Checked in as "john" (TFS resolves based on auth config)
```

### TFS Permissions

To check in on behalf of other users (using inferred or authors file mappings), the authenticated TFS user needs the **"Check in other user's changes" (CheckinOther)** permission. This is typically granted to:
- Build service accounts
- Integration accounts  
- Team leads/admins

Without this permission:
- If inferred user matches authenticated user → ✅ Works
- If inferred user differs from authenticated user → ❌ TFS rejects the checkin

### Troubleshooting

If automatic inference isn't working:

1. **Check trace output** to see what username was inferred:
   ```sh
   git tfs sync --to-tfvc 2>&1 | grep "Inferred TFS user"
   ```

2. **Use explicit domain format** in your git name:
   ```sh
   git config user.name "COMPANY\john"
   ```

3. **Use authors file** for explicit control:
   ```sh
   # authors.txt:
   COMPANY\john = john <john@company.com>
   
   git tfs sync --to-tfvc --authors=authors.txt
   ```

4. **Check TFS permissions** - ensure you have CheckinOther permission

## Per-TFS remote

Git-tfs can map multiple TFS branches to git branches. Each TFS
branch is tracked as a separate "remote", and several config values
are stored for each branch.

Each git-tfs remote is assigned an ID. All of a remote's config keys
are prefixed with `tfs-remote.<id>.` So, for example, the full `url`
key for the remote `default` is `tfs-remote.default.url`.

* `url`
  is the URL of the TFS project collection.
* `legacy-urls`
  is a list, comma-separated, of previous URLs of the TFS project
  collection. For example, if you started your git-tfs clone from
  a 2005 or 2008 TFS server ('http://tfs:8080/tfs'), and the server
  migrated to 2010 or later, moving your project into a project
  collection ('http://tfs:8080/tfs/DefaultCollection'), then the
  `url` for your git-tfs remote should be the current url, and
  `legacy-urls` would be the old url.
* `repository`
  is the TFS repository path that was cloned to the root of your
  git-tfs project. Typically this is a TFS project path
  (`$/MyProject`), but it can be a subdirectory (`$/MyProject/Dir`)
  or a branch (`$/MyProject/trunk`).
* `username` and `password`
  are your TFS credentials. Normally, if you connect to a TFS
  server on your local Windows domain, you won't need to provide
  these values, because git-tfs defaults to using integrated
  authentication.
* `ignore-paths`
  is a regular expression of TFS paths to ignore when fetching.
* `autotag`
  can be set to `true` to make git-tfs create a tag for each
  TFS commit. This is disabled by default, because creating
  a lot of tags will slow down your git operations.
* `noparallel`
   can be set to `true` to make disable parallel access to the TFS.
   This can be useful in cases where the TFS has problems with 
   parallel access and reports `TF400030`. (See issue #1242)
