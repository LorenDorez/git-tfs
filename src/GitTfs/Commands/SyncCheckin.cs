using System.Diagnostics;
using NDesk.Options;
using GitTfs.Core;
using GitTfs.Util;
using StructureMap;

namespace GitTfs.Commands
{
    /// <summary>
    /// Internal command used by 'sync' to check in commits to TFVC with git-notes tracking.
    /// This is a simplified version of rcheckin that always reuses existing commits
    /// instead of fetching them back from TFVC.
    /// </summary>
    [Pluggable("sync-checkin")]
    [RequiresValidGitRepository]
    public class SyncCheckin : GitTfsCommand
    {
        private readonly CheckinOptions _checkinOptions;
        private readonly CheckinOptionsFactory _checkinOptionsFactory;
        private readonly TfsWriter _writer;
        private readonly Globals _globals;
        private readonly AuthorsFile _authors;
        private readonly DiagnosticOutputHelper _diagnosticHelper;

        private bool AutoRebase { get; set; }
        private bool ForceCheckin { get; set; }

        public SyncCheckin(CheckinOptions checkinOptions, TfsWriter writer, Globals globals, AuthorsFile authors, DiagnosticOutputHelper diagnosticHelper)
        {
            _checkinOptions = checkinOptions;
            _checkinOptionsFactory = new CheckinOptionsFactory(globals);
            _writer = writer;
            _globals = globals;
            _authors = authors;
            _diagnosticHelper = diagnosticHelper;
        }

        public OptionSet OptionSet => new OptionSet
                    {
                        {"a|autorebase", "Continue and rebase if new TFS changesets found", v => AutoRebase = v != null},
                        {"ignore-merge", "Force check in ignoring parent tfs branches in merge commits", v => ForceCheckin = v != null},
                    }.Merge(_checkinOptions.OptionSet);

        // uses rebase and works only with HEAD
        public int Run()
        {
            _globals.WarnOnGitVersion();

            if (_globals.Repository.IsBare)
                throw new GitTfsException("error: you should specify the local branch to checkin for a bare repository.");

            return _writer.Write("HEAD", PerformSyncCheckin);
        }

        // uses rebase and works only with HEAD in a none bare repository
        public int Run(string localBranch)
        {
            _globals.WarnOnGitVersion();

            if (!_globals.Repository.IsBare)
                throw new GitTfsException("error: This syntax with one parameter is only allowed in bare repository.");

            _authors.LoadAuthorsFromSavedFile(_globals.GitDir);

            return _writer.Write(GitRepository.ShortToLocalName(localBranch), PerformSyncCheckin);
        }

        private int PerformSyncCheckin(TfsChangesetInfo parentChangeset, string refToCheckin)
        {
            _diagnosticHelper.OutputDiagnosticInformation("sync-checkin", parentChangeset.Remote);
            
            if (_globals.Repository.IsBare)
                AutoRebase = false;

            if (_globals.Repository.WorkingCopyHasUnstagedOrUncommitedChanges)
            {
                throw new GitTfsException("error: You have local changes; sync checkin only possible with clean working directory.")
                    .WithRecommendation("Try 'git stash' to stash your local changes and checkin again.");
            }

            // get latest changes from TFS to minimize possibility of late conflict
            // CRITICAL FIX: Skip pre-checkin fetch if we just fetched (to avoid overwriting git-notes)
            var skipPrecheckinFetch = Environment.GetEnvironmentVariable("GIT_TFS_SKIP_PRECHECKIN_FETCH") == "true";
            if (!skipPrecheckinFetch)
            {
                Trace.TraceInformation("Fetching changes from TFS to minimize possibility of late conflict...");
                parentChangeset.Remote.Fetch();
                if (parentChangeset.ChangesetId != parentChangeset.Remote.MaxChangesetId)
                {
                    if (AutoRebase)
                    {
                        _globals.Repository.CommandNoisy("rebase", "--rebase-merges", parentChangeset.Remote.RemoteRef);
                        parentChangeset = _globals.Repository.GetTfsCommit(parentChangeset.Remote.MaxCommitHash);
                    }
                    else
                    {
                        if (_globals.Repository.IsBare)
                            _globals.Repository.UpdateRef(refToCheckin, parentChangeset.Remote.MaxCommitHash);

                        throw new GitTfsException("error: New TFS changesets were found.")
                            .WithRecommendation("Try to rebase HEAD onto latest TFS checkin and repeat sync or alternatively use manual checkin");
                    }
                }
            }
            else
            {
                Trace.TraceInformation("Skipping pre-checkin fetch (already fetched by parent command)");
            }

            IEnumerable<GitCommit> commitsToCheckin = _globals.Repository.FindParentCommits(refToCheckin, parentChangeset.Remote.MaxCommitHash);
            Trace.WriteLine("Commits to checkin count: " + commitsToCheckin.Count());
            if (!commitsToCheckin.Any())
                throw new GitTfsException("error: latest TFS commit should be parent of commits being checked in");

            SetupMetadataExport(parentChangeset.Remote);

            return PerformSyncCheckinQuick(parentChangeset, refToCheckin, commitsToCheckin);
        }

        private void SetupMetadataExport(IGitTfsRemote remote)
        {
            var exportInitializer = new ExportMetadatasInitializer(_globals);
            var shouldExport = _globals.Repository.GetConfig(GitTfsConstants.ExportMetadatasConfigKey) == "true";
            exportInitializer.InitializeRemote(remote, shouldExport);
        }

        private int PerformSyncCheckinQuick(TfsChangesetInfo parentChangeset, string refToCheckin, IEnumerable<GitCommit> commitsToCheckin)
        {
            var tfsRemote = parentChangeset.Remote;
            string currentParent = parentChangeset.Remote.MaxCommitHash;
            int newChangesetId = 0;

            foreach (var commit in commitsToCheckin)
            {
                // CRITICAL FAILSAFE: Skip commits that already have tracking metadata
                // This prevents duplicate checkins if a commit was already synced in a previous run
                var existingChangesetInfo = _globals.Repository.GetTfsCommit(commit.Sha);
                if (existingChangesetInfo != null)
                {
                    Trace.TraceInformation("Commit {0} already tracked as C{1}, skipping checkin", commit.Sha.Substring(0, 8), existingChangesetInfo.ChangesetId);
                    Console.WriteLine($"??  Commit {commit.Sha.Substring(0, 8)} already synced as C{existingChangesetInfo.ChangesetId}, skipping");
                    
                    // Update parent and continue to next commit
                    currentParent = commit.Sha;
                    parentChangeset = new TfsChangesetInfo { ChangesetId = existingChangesetInfo.ChangesetId, GitCommit = commit.Sha, Remote = tfsRemote };
                    continue;
                }
                
                var message = BuildCommitMessage(commit, !_checkinOptions.NoGenerateCheckinComment, currentParent);
                string target = commit.Sha;
                var parents = commit.Parents.Where(c => c.Sha != currentParent).ToArray();
                string tfsRepositoryPathOfMergedBranch = _checkinOptions.NoMerge
                                                         ? null
                                                         : FindTfsRepositoryPathOfMergedBranch(tfsRemote, parents, target);

                var commitSpecificCheckinOptions = _checkinOptionsFactory.BuildCommitSpecificCheckinOptions(_checkinOptions, message, commit, _authors);

                Trace.TraceInformation("Starting sync-checkin of {0} '{1}'", target.Substring(0, 8), commitSpecificCheckinOptions.CheckinComment);
                try
                {
                    // Check in to TFVC
                    newChangesetId = tfsRemote.Checkin(target, currentParent, parentChangeset, commitSpecificCheckinOptions, tfsRepositoryPathOfMergedBranch);
                    
                    // Always reuse the existing commit that was just checked in
                    // This commit already has the correct content - we just need to add tracking metadata
                    Trace.TraceInformation("Adding git-note to commit {0} for changeset C{1}", target.Substring(0, 8), newChangesetId);
                    
                    // Add git-note to the existing commit to track the changeset
                    _globals.Repository.AddTfsNote(target, tfsRemote.TfsUrl, tfsRemote.TfsRepositoryPath, newChangesetId);
                    
                    // Update TFS head to point to the commit that was checked in
                    tfsRemote.UpdateTfsHead(target, newChangesetId);
                    
                    // Update parent for next iteration
                    currentParent = target;
                    parentChangeset = new TfsChangesetInfo { ChangesetId = newChangesetId, GitCommit = target, Remote = tfsRemote };
                    Trace.TraceInformation("Done with {0}.", target);
                }
                catch (Exception)
                {
                    if (newChangesetId != 0)
                    {
                        var lastCommit = _globals.Repository.FindCommitHashByChangesetId(newChangesetId);
                        if (lastCommit != null)
                            RebaseOnto(lastCommit, currentParent);
                    }
                    throw;
                }
            }

            if (_globals.Repository.IsBare)
                _globals.Repository.UpdateRef(refToCheckin, tfsRemote.MaxCommitHash);
            else
                _globals.Repository.ResetHard(tfsRemote.MaxCommitHash);
            Trace.TraceInformation("No more to sync-checkin.");

            Trace.WriteLine("Cleaning...");
            tfsRemote.CleanupWorkspaceDirectory();

            return GitTfsExitCodes.OK;
        }

        public string BuildCommitMessage(GitCommit commit, bool generateCheckinComment, string latest) => generateCheckinComment
                               ? _globals.Repository.GetCommitMessage(commit.Sha, latest)
                               : _globals.Repository.GetCommit(commit.Sha).Message;

        private string FindTfsRepositoryPathOfMergedBranch(IGitTfsRemote remoteToCheckin, GitCommit[] gitParents, string target)
        {
            if (gitParents.Length != 0)
            {
                Trace.TraceInformation("Working on the merge commit: " + target);
                if (gitParents.Length > 1)
                    Trace.TraceWarning("warning: only 1 parent is supported by TFS for a merge changeset. The other parents won't be materialized in the TFS merge!");
                foreach (var gitParent in gitParents)
                {
                    var tfsCommit = _globals.Repository.GetTfsCommit(gitParent);
                    if (tfsCommit != null)
                        return tfsCommit.Remote.TfsRepositoryPath;
                    var lastCheckinCommit = _globals.Repository.GetLastParentTfsCommits(gitParent.Sha).FirstOrDefault();
                    if (lastCheckinCommit != null)
                    {
                        if (!ForceCheckin && lastCheckinCommit.Remote.Id != remoteToCheckin.Id)
                            throw new GitTfsException("error: the merged branch '" + lastCheckinCommit.Remote.Id
                                + "' is a TFS tracked branch (" + lastCheckinCommit.Remote.TfsRepositoryPath
                                + ") with some commits not checked in.\nIn this case, the local merge won't be materialized as a merge in tfs...")
                                .WithRecommendation("check in all the commits of the tfs merged branch in TFS before trying to check in a merge commit",
                                "use --ignore-merge option to ignore merged TFS branch and check in commit as a normal changeset (not a merge).");
                    }
                    else
                    {
                        Trace.TraceWarning("warning: the parent " + gitParent + " does not belong to a TFS tracked branch (not checked in TFS) and will be ignored!");
                    }
                }
            }
            return null;
        }

        public void RebaseOnto(string newBaseCommit, string oldBaseCommit) => _globals.Repository.CommandNoisy("rebase", "--rebase-merges", "--onto", newBaseCommit, oldBaseCommit);
    }
}
