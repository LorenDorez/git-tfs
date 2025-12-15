* fix: read/write of `description` in bare repos ( #1487 by bramborman )
* chore: target .net v4.8 ( #1490 by @pmiossec )
* chore: Update libgit2sharp to v0.30 ( #1492 by @pmiossec )
* Fix handling of renamed branches for clone/fetch ( #1493 by @dh2i-sam )
* feature: Configurable git user signature for automated commits (e.g., `.gitignore` commit during `init` or `sync --init-workspace`)
  - Supports environment variables: `GIT_TFS_USER_NAME` / `GIT_TFS_USER_EMAIL` (highest priority)
  - Supports git-tfs config: `git-tfs.user.name` / `git-tfs.user.email`
  - Falls back to standard git config: `user.name` / `user.email`
  - Falls back to defaults: `"git-tfs"` / `"git-tfs@noreply.com"`
  - During `sync --init-workspace`, automatically sets local git config using resolved values
  - See `doc/config.md` for configuration examples
* feature: Automatic TFS user ID inference during checkin operations
  - When checking in git commits to TFVC, git-tfs now automatically infers the TFS user ID from the git commit author
  - Extracts username from email (e.g., `john@company.com` ? `john`) and lets TFS resolve it to the correct domain
  - Preserves domain format if specified in git name (e.g., `COMPANY\john` ? `COMPANY\john`)
  - **For merge commits**: Uses the author of the last parent (merged branch) to credit the developer who did the work
  - Authors file still takes priority for explicit mappings
  - Simpler and more flexible than hardcoding domain - works with any TFS authentication setup
  - Preserves commit authorship throughout the sync cycle without requiring authors file configuration
  - Falls back to authenticated user if inference is not possible
