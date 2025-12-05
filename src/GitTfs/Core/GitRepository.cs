using System.Diagnostics;
using System.Text.RegularExpressions;
using StructureMap;
using LibGit2Sharp;
using GitTfs.Commands;
using Branch = LibGit2Sharp.Branch;
using GitTfs.Util;

namespace GitTfs.Core
{
    public class GitRepository : GitHelpers, IGitRepository
    {
        private readonly IContainer _container;
        private readonly Globals _globals;
        private IDictionary<string, IGitTfsRemote> _cachedRemotes;
        private readonly Repository _repository;
        private readonly RemoteConfigConverter _remoteConfigReader;
        private readonly GitNotesManager _notesManager;

        public GitRepository(string gitDir, IContainer container, Globals globals, RemoteConfigConverter remoteConfigReader)
            : base(container)
        {
            _container = container;
            _globals = globals;
            GitDir = gitDir;
            _repository = new Repository(GitDir);
            _remoteConfigReader = remoteConfigReader;
            _notesManager = new GitNotesManager(_repository);
        }

        ~GitRepository()
        {
            if (_repository != null)
                _repository.Dispose();
        }

        public GitCommit Commit(LogEntry logEntry)
        {
            var parents = logEntry.CommitParents.Select(sha => _repository.Lookup<Commit>(sha));
            var commit = _repository.ObjectDatabase.CreateCommit(
                new Signature(logEntry.AuthorName, logEntry.AuthorEmail, logEntry.Date.ToUniversalTime()),
                new Signature(logEntry.CommitterName, logEntry.CommitterEmail, logEntry.Date.ToUniversalTime()),
                logEntry.Log,
                logEntry.Tree,
                parents,
                false);
            changesetsCache[logEntry.ChangesetId] = commit.Sha;
            return new GitCommit(commit);
        }

        public void UpdateRef(string gitRefName, string shaCommit, string message = null)
        {
            if (message == null)
                _repository.Refs.Add(gitRefName, shaCommit, allowOverwrite: true);
            else
                _repository.Refs.Add(gitRefName, shaCommit, message, true);
        }

        public static string ShortToLocalName(string branchName) => "refs/heads/" + branchName;

        public static string ShortToTfsRemoteName(string branchName) => "refs/remotes/tfs/" + branchName;

        public string GitDir { get; }

        protected override GitProcess Start(string[] command, Action<ProcessStartInfo> initialize) => base.Start(command, initialize.And(SetUpPaths));

        private void SetUpPaths(ProcessStartInfo gitCommand)
        {
            if (GitDir != null)
                gitCommand.EnvironmentVariables["GIT_DIR"] = GitDir;
        }

        public string GetConfig(string key)
        {
            var entry = _repository.Config.Get<string>(key);
            return entry == null ? null : entry.Value;
        }

        public T GetConfig<T>(string key) => GetConfig(key, default(T));

        public T GetConfig<T>(string key, T defaultValue)
        {
            try
            {
                var entry = _repository.Config.Get<T>(key);
                if (entry == null)
                    return defaultValue;
                return entry.Value;
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        public void SetConfig(string key, string value) => _repository.Config.Set<string>(key, value, ConfigurationLevel.Local);

        public void SetConfig(string key, bool value) => SetConfig(key, value.ToString().ToLower());

        /// <summary>
        /// Adds a git note to a commit with TFS changeset metadata.
        /// </summary>
        public void AddTfsNote(string commitSha, string tfsUrl, string tfsRepositoryPath, int changesetId)
        {
            _notesManager.AddNote(commitSha, tfsUrl, tfsRepositoryPath, changesetId);
        }

        /// <summary>
        /// Configures the specified git remote to automatically push and fetch git notes.
        /// This ensures TFS changeset metadata stored in git notes is synced with the remote repository.
        /// </summary>
        /// <param name="remoteName">The name of the remote to configure (default: "origin")</param>
        public void ConfigureRemoteToSyncNotes(string remoteName = "origin")
        {
            // Check if git notes are enabled
            var useGitNotes = GetConfig(GitTfsConstants.UseGitNotesConfigKey, true);
            if (!useGitNotes)
            {
                Trace.WriteLine("Git notes are disabled, skipping notes sync configuration");
                return;
            }

            // Check if the remote exists
            var remoteExists = _repository.Network.Remotes[remoteName] != null;
            if (!remoteExists)
            {
                Trace.WriteLine($"Remote '{remoteName}' does not exist, skipping notes sync configuration");
                return;
            }

            var notesRefspec = $"+{GitTfsConstants.TfvcSyncNotesRef}:{GitTfsConstants.TfvcSyncNotesRef}";
            var fetchConfigKey = $"remote.{remoteName}.fetch";
            var pushConfigKey = $"remote.{remoteName}.push";

            // Check if fetch refspec already exists
            var existingFetchRefspecs = _repository.Config.Find(fetchConfigKey, ConfigurationLevel.Local);
            var fetchRefspecExists = existingFetchRefspecs.Any(rs => rs.Value == notesRefspec);

            if (!fetchRefspecExists)
            {
                // Add fetch refspec for notes
                _repository.Config.Add(fetchConfigKey, notesRefspec, ConfigurationLevel.Local);
                Trace.WriteLine($"Configured remote '{remoteName}' to fetch git notes: {notesRefspec}");
            }

            // Check if push refspec already exists
            var existingPushRefspecs = _repository.Config.Find(pushConfigKey, ConfigurationLevel.Local);
            var pushRefspecExists = existingPushRefspecs.Any(rs => rs.Value == notesRefspec);

            if (!pushRefspecExists)
            {
                // Add push refspec for notes
                _repository.Config.Add(pushConfigKey, notesRefspec, ConfigurationLevel.Local);
                Trace.WriteLine($"Configured remote '{remoteName}' to push git notes: {notesRefspec}");
            }
        }

        public IEnumerable<IGitTfsRemote> ReadAllTfsRemotes()
        {
            var remotes = GetTfsRemotes().Values;
            foreach (var remote in remotes)
                remote.EnsureTfsAuthenticated();

            return remotes;
        }

        public IGitTfsRemote ReadTfsRemote(string remoteId)
        {
            if (!HasRemote(remoteId))
                throw new GitTfsException("Unable to locate git-tfs remote with id = " + remoteId)
                    .WithRecommendation("Try using `git tfs bootstrap` to auto-init TFS remotes.");
            var remote = GetTfsRemotes()[remoteId];
            remote.EnsureTfsAuthenticated();
            return remote;
        }

        private IGitTfsRemote ReadTfsRemote(string tfsUrl, string tfsRepositoryPath)
        {
            var allRemotes = GetTfsRemotes();
            
            // First try: exact match (case-insensitive)
            var matchingRemotes =
                allRemotes.Values.Where(
                    remote => remote.MatchesUrlAndRepositoryPath(tfsUrl, tfsRepositoryPath));
            
            if (matchingRemotes.Any())
            {
                if (matchingRemotes.Count() > 1)
                {
                    Trace.WriteLine("More than one remote matched!");
                }
                return matchingRemotes.First();
            }
            
            // Second try: if no exact match, try to find a remote with matching repository path only
            // This handles cases where the TFS URL might have changed but the repository path is the same
            if (!string.IsNullOrEmpty(tfsRepositoryPath))
            {
                var repositoryPathMatch = allRemotes.Values.FirstOrDefault(
                    remote => !string.IsNullOrEmpty(remote.TfsRepositoryPath) &&
                             string.Equals(remote.TfsRepositoryPath, tfsRepositoryPath, StringComparison.OrdinalIgnoreCase));
                
                if (repositoryPathMatch != null)
                {
                    Trace.WriteLine($"No exact URL match found, but found remote '{repositoryPathMatch.Id}' with matching repository path. Using this remote.");
                    Trace.WriteLine($"  Note URL in git notes:  {tfsUrl}");
                    Trace.WriteLine($"  Configured remote URL: {repositoryPathMatch.TfsUrl}");
                    return repositoryPathMatch;
                }
            }
            
            // Third try: if we only have one TFS remote configured, use it regardless of URL/path match
            // This is a common scenario where the user has a single TFS remote but URLs might not match exactly
            if (allRemotes.Count == 1)
            {
                var singleRemote = allRemotes.Values.First();
                Trace.WriteLine($"No matching remote found, but only one TFS remote ('{singleRemote.Id}') is configured. Using it.");
                Trace.WriteLine($"  URL in git notes:           {tfsUrl}");
                Trace.WriteLine($"  Repository in git notes:    {tfsRepositoryPath}");
                Trace.WriteLine($"  Configured remote URL:      {singleRemote.TfsUrl}");
                Trace.WriteLine($"  Configured remote repository: {singleRemote.TfsRepositoryPath}");
                return singleRemote;
            }
            
            // No match found, return a derived remote (will require bootstrapping)
            Trace.WriteLine($"Unable to find a matching TFS remote for URL '{tfsUrl}' and repository '{tfsRepositoryPath}'");
            return new DerivedGitTfsRemote(tfsUrl, tfsRepositoryPath);
        }

        public IEnumerable<string> GetGitRemoteBranches(string gitRemote)
        {
            gitRemote = gitRemote + "/";
            var references = _repository.Branches.Where(b => b.IsRemote && b.FriendlyName.StartsWith(gitRemote) && !b.FriendlyName.EndsWith("/HEAD"));
            return references.Select(r => r.FriendlyName);
        }

        private IDictionary<string, IGitTfsRemote> GetTfsRemotes()
            => _cachedRemotes ?? (_cachedRemotes = ReadTfsRemotes());

        public IGitTfsRemote CreateTfsRemote(RemoteInfo remote)
        {
            foreach (var entry in _remoteConfigReader.Dump(remote))
            {
                if (entry.Value != null)
                {
                    _repository.Config.Set(entry.Key, entry.Value);
                }
                else
                {
                    _repository.Config.Unset(entry.Key);
                }
            }

            var gitTfsRemote = BuildRemote(remote);
            gitTfsRemote.EnsureTfsAuthenticated();

            return _cachedRemotes[remote.Id] = gitTfsRemote;
        }


        public void DeleteTfsRemote(IGitTfsRemote remote)
        {
            if (remote == null)
                throw new GitTfsException("error: the name of the remote to delete is invalid!");

            UnsetTfsRemoteConfig(remote.Id);
            _repository.Refs.Remove(remote.RemoteRef);
        }

        private void UnsetTfsRemoteConfig(string remoteId)
        {
            foreach (var entry in _remoteConfigReader.Delete(remoteId))
            {
                _repository.Config.Unset(entry.Key);
            }
            _cachedRemotes = null;
        }

        public void MoveRemote(string oldRemoteName, string newRemoteName)
        {
            if (!Reference.IsValidName(ShortToLocalName(oldRemoteName)))
                throw new GitTfsException("error: the name of the remote to move is invalid!");

            if (!Reference.IsValidName(ShortToLocalName(newRemoteName)))
                throw new GitTfsException("error: the new name of the remote is invalid!");

            if (HasRemote(newRemoteName))
                throw new GitTfsException($"error: this remote name \"{newRemoteName}\" is already used!");

            var oldRemote = ReadTfsRemote(oldRemoteName);
            if (oldRemote == null)
                throw new GitTfsException($"error: the remote \"{oldRemoteName}\" doesn't exist!");

            var remoteInfo = oldRemote.RemoteInfo;
            remoteInfo.Id = newRemoteName;

            CreateTfsRemote(remoteInfo);
            var newRemote = ReadTfsRemote(newRemoteName);

            _repository.Refs.Rename(oldRemote.RemoteRef, newRemote.RemoteRef);
            UnsetTfsRemoteConfig(oldRemoteName);
        }

        public Branch RenameBranch(string oldName, string newName)
        {
            var branch = _repository.Branches[oldName];

            return branch == null ? null : _repository.Branches.Rename(branch, newName);
        }

        private IDictionary<string, IGitTfsRemote> ReadTfsRemotes()
        {
            // does this need to ensuretfsauthenticated?
            _repository.Config.Set("tfs.touch", "1"); // reload configuration, because `git tfs init` and `git tfs clone` use Process.Start to update the config, so _repository's copy is out of date.
            var remotes = _remoteConfigReader.Load(_repository.Config).Select(x => BuildRemote(x)).ToDictionary(x => x.Id);

            bool shouldExport = GetConfig(GitTfsConstants.ExportMetadatasConfigKey) == "true";

            foreach(var remote in remotes.Values)
            {
                var metadataExportInitializer = new ExportMetadatasInitializer(_globals);
                metadataExportInitializer.InitializeRemote(remote, shouldExport);
            }

            return remotes;
        }

        private IGitTfsRemote BuildRemote(RemoteInfo remoteInfo)
            => _container.With(remoteInfo).With<IGitRepository>(this).GetInstance<IGitTfsRemote>();

        public bool HasRemote(string remoteId) => GetTfsRemotes().ContainsKey(remoteId);

        public bool IsInSameTeamProjectAsDefaultRepository(string tfsRepositoryPath)
        {
            IGitTfsRemote defaultRepository;
            if (!GetTfsRemotes().TryGetValue(GitTfsConstants.DefaultRepositoryId, out defaultRepository))
            {
                return true;
            }

            var teamProjectPath = defaultRepository.TfsRepositoryPath.ToTfsTeamProjectRepositoryPath();

            //add ending '/' because there can be overlapping names ($/myproject/ and $/myproject other/)
            return tfsRepositoryPath.StartsWith(teamProjectPath + "/");
        }

        public bool HasRef(string gitRef) => _repository.Refs[gitRef] != null;

        public void MoveTfsRefForwardIfNeeded(IGitTfsRemote remote) => MoveTfsRefForwardIfNeeded(remote, "HEAD");

        public void MoveTfsRefForwardIfNeeded(IGitTfsRemote remote, string @ref)
        {
            int currentMaxChangesetId = remote.MaxChangesetId;
            var untrackedTfsChangesets = from cs in GetLastParentTfsCommits(@ref)
                                         where cs.Remote.Id == remote.Id && cs.ChangesetId > currentMaxChangesetId
                                         orderby cs.ChangesetId
                                         select cs;
            foreach (var cs in untrackedTfsChangesets)
            {
                // UpdateTfsHead sets tag with TFS changeset id on each commit so we can't just update to latest
                remote.UpdateTfsHead(cs.GitCommit, cs.ChangesetId);
            }
        }

        public GitCommit GetCommit(string commitish)
        {
            var commit = _repository.Lookup<Commit>(commitish);

            return commit is null ? null : new GitCommit(commit);
        }

        public MergeResult Merge(string commitish)
        {
            var commit = _repository.Lookup<Commit>(commitish);
            if (commit == null)
                throw new GitTfsException("error: commit '" + commitish + "' can't be found and merged into!");

            var options = new MergeOptions
            {
                OnCheckoutNotify = (file, reason) =>
                {
                    if (reason == CheckoutNotifyFlags.Conflict)
                        Trace.TraceError("conflict found: " + file);
                    return true;
                },
                CheckoutNotifyFlags = CheckoutNotifyFlags.Conflict
            };
            return _repository.Merge(commit, _repository.Config.BuildSignature(new DateTimeOffset(DateTime.Now)), options);
        }

        public String GetCurrentCommit() => _repository.Head.Commits.First().Sha;

        public IEnumerable<TfsChangesetInfo> GetLastParentTfsCommits(string head)
        {
            Trace.WriteLine($"[GetLastParentTfsCommits] Called with head: {head}");
            var changesets = new List<TfsChangesetInfo>();
            var commit = _repository.Lookup<Commit>(head);
            if (commit == null)
            {
                Trace.WriteLine($"[GetLastParentTfsCommits] No commit found for {head}");
                return changesets;
            }
            Trace.WriteLine($"[GetLastParentTfsCommits] Found commit {commit.Sha}, starting search for TFS commits...");
            FindTfsParentCommits(changesets, commit);
            Trace.WriteLine($"[GetLastParentTfsCommits] Search complete. Found {changesets.Count} TFS commit(s)");
            foreach (var cs in changesets)
            {
                Trace.WriteLine($"[GetLastParentTfsCommits]   - C{cs.ChangesetId} at {cs.GitCommit}");
            }
            return changesets;
        }

        private void FindTfsParentCommits(List<TfsChangesetInfo> changesets, Commit commit)
        {
            Trace.WriteLine($"[FindTfsParentCommits] Starting search from commit {commit.Sha}");
            var commitsToFollow = new Stack<Commit>();
            commitsToFollow.Push(commit);
            var alreadyVisitedCommits = new HashSet<string>();
            int commitsChecked = 0;
            
            while (commitsToFollow.Any())
            {
                commit = commitsToFollow.Pop();
                commitsChecked++;

                alreadyVisitedCommits.Add(commit.Sha);

                Trace.WriteLine($"[FindTfsParentCommits] Checking commit #{commitsChecked}: {commit.Sha.Substring(0, 8)}...");
                var changesetInfo = TryParseChangesetInfo(commit.Message, commit.Sha);
                if (changesetInfo == null)
                {
                    Trace.WriteLine($"[FindTfsParentCommits]   Not a TFS commit, checking {commit.Parents.Count()} parent(s)");
                    // If commit was not a TFS commit, continue searching all new parents of the commit
                    // Add parents in reverse order to keep topology (main parent should be treated first!)
                    foreach (var parent in commit.Parents.Where(x => !alreadyVisitedCommits.Contains(x.Sha)).Reverse())
                        commitsToFollow.Push(parent);
                }
                else
                {
                    Trace.WriteLine($"[FindTfsParentCommits]   ? Found TFS commit! C{changesetInfo.ChangesetId}");
                    changesets.Add(changesetInfo);
                }
            }
            Trace.WriteLine($"[FindTfsParentCommits] Commits visited count: {alreadyVisitedCommits.Count}");
        }

        public TfsChangesetInfo GetTfsChangesetById(string remoteRef, int changesetId)
        {
            var commit = FindCommitByChangesetId(changesetId, remoteRef);
            if (commit == null)
                return null;
            return TryParseChangesetInfo(commit.Message, commit.Sha);
        }

        public TfsChangesetInfo GetCurrentTfsCommit()
        {
            var currentCommit = _repository.Head.Commits.First();
            return TryParseChangesetInfo(currentCommit.Message, currentCommit.Sha);
        }

        public TfsChangesetInfo GetTfsCommit(GitCommit commit)
        {
            if (commit is null) throw new ArgumentNullException(nameof(commit));

            return TryParseChangesetInfo(commit.Message, commit.Sha);
        }

        public TfsChangesetInfo GetTfsCommit(string sha)
        {
            var gitCommit = GetCommit(sha);

            return gitCommit is null ? null : GetTfsCommit(gitCommit);
        }

        private TfsChangesetInfo TryParseChangesetInfo(string gitTfsMetaInfo, string commit)
        {
            Trace.WriteLine($"[TryParseChangesetInfo] Parsing commit {commit.Substring(0, 8)}...");
            
            // First, try to get metadata from git notes (new approach)
            Trace.WriteLine($"[TryParseChangesetInfo] Step 1: Checking for git-notes...");
            var noteInfo = _notesManager.GetNote(commit);
            if (noteInfo != null)
            {
                Trace.WriteLine($"[TryParseChangesetInfo] ? Found git-note!");
                Trace.WriteLine($"[TryParseChangesetInfo]   - ChangesetId: C{noteInfo.ChangesetId}");
                Trace.WriteLine($"[TryParseChangesetInfo]   - TfsUrl: {noteInfo.TfsUrl}");
                Trace.WriteLine($"[TryParseChangesetInfo]   - TfsPath: {noteInfo.TfsRepositoryPath}");
                
                var commitInfo = _container.GetInstance<TfsChangesetInfo>();
                commitInfo.Remote = ReadTfsRemote(noteInfo.TfsUrl, noteInfo.TfsRepositoryPath);
                commitInfo.ChangesetId = noteInfo.ChangesetId;
                commitInfo.GitCommit = commit;
                Trace.WriteLine($"[TryParseChangesetInfo] ? Successfully parsed from git-notes: C{commitInfo.ChangesetId}");
                return commitInfo;
            }
            else
            {
                Trace.WriteLine($"[TryParseChangesetInfo] ? No git-note found for this commit");
            }

            // Fall back to parsing commit message (legacy approach for backward compatibility)
            Trace.WriteLine($"[TryParseChangesetInfo] Step 2: Trying legacy commit message parsing...");
            var match = GitTfsConstants.TfsCommitInfoRegex.Match(gitTfsMetaInfo);
            if (match.Success)
            {
                Trace.WriteLine($"[TryParseChangesetInfo] ? Found git-tfs-id in commit message");
                var url = match.Groups["url"].Value;
                var repo = match.Groups["repository"].Success ? match.Groups["repository"].Value : null;
                var changesetId = Convert.ToInt32(match.Groups["changeset"].Value);
                
                Trace.WriteLine($"[TryParseChangesetInfo]   - ChangesetId: C{changesetId}");
                Trace.WriteLine($"[TryParseChangesetInfo]   - TfsUrl: {url}");
                Trace.WriteLine($"[TryParseChangesetInfo]   - TfsPath: {repo ?? "(null)"}");
                
                var commitInfo = _container.GetInstance<TfsChangesetInfo>();
                commitInfo.Remote = ReadTfsRemote(url, repo);
                commitInfo.ChangesetId = changesetId;
                commitInfo.GitCommit = commit;
                Trace.WriteLine($"[TryParseChangesetInfo] ? Successfully parsed from commit message: C{commitInfo.ChangesetId}");
                return commitInfo;
            }
            else
            {
                Trace.WriteLine($"[TryParseChangesetInfo] ? No git-tfs-id found in commit message");
            }
            
            Trace.WriteLine($"[TryParseChangesetInfo] ? Not a TFS commit (no metadata found)");
            return null;
        }

        public IDictionary<string, GitObject> CreateObjectsDictionary()
            => new Dictionary<string, GitObject>(StringComparer.InvariantCultureIgnoreCase);

        public IDictionary<string, GitObject> GetObjects(string commit, IDictionary<string, GitObject> entries)
        {
            if (commit != null)
            {
                ParseEntries(entries, _repository.Lookup<Commit>(commit).Tree, commit);
            }
            return entries;
        }

        public IDictionary<string, GitObject> GetObjects(string commit)
        {
            var entries = CreateObjectsDictionary();
            return GetObjects(commit, entries);
        }

        public IGitTreeBuilder GetTreeBuilder(string commit)
            => commit == null
                ? new GitTreeBuilder(_repository.ObjectDatabase)
                : new GitTreeBuilder(_repository.ObjectDatabase, _repository.Lookup<Commit>(commit).Tree);

        public string GetCommitMessage(string head, string parentCommitish)
        {
            var message = new System.Text.StringBuilder();
            foreach (Commit comm in
                _repository.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = head, ExcludeReachableFrom = parentCommitish }))
            {
                // Normalize commit message line endings to CR+LF style, so that message
                // would be correctly shown in TFS commit dialog.
                message.AppendLine(NormalizeLineEndings(comm.Message));
            }

            return GitTfsConstants.TfsCommitInfoRegex.Replace(message.ToString(), "").Trim(' ', '\r', '\n');
        }

        private static string NormalizeLineEndings(string input) => string.IsNullOrEmpty(input)
                ? input
                : input.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        private void ParseEntries(IDictionary<string, GitObject> entries, Tree treeInfo, string commit)
        {
            var treesToDescend = new Queue<Tree>(new[] { treeInfo });
            while (treesToDescend.Any())
            {
                var currentTree = treesToDescend.Dequeue();
                foreach (var item in currentTree)
                {
                    if (item.TargetType == TreeEntryTargetType.Tree)
                    {
                        treesToDescend.Enqueue((Tree)item.Target);
                    }
                    var path = item.Path.Replace('\\', '/');
                    entries[path] = new GitObject
                    {
                        Mode = item.Mode,
                        Sha = item.Target.Sha,
                        ObjectType = item.TargetType,
                        Path = path,
                        Commit = commit
                    };
                }
            }
        }

        public IEnumerable<IGitChangedFile> GetChangedFiles(string from, string to)
        {
            using (var diffOutput = CommandOutputPipe("diff-tree", "-r", "-M", "-z", from, to))
            {
                var changes = GitChangeInfo.GetChangedFiles(diffOutput);
                foreach (var change in changes)
                {
                    yield return BuildGitChangedFile(change);
                }
            }
        }

        private IGitChangedFile BuildGitChangedFile(GitChangeInfo change) => change.ToGitChangedFile(_container.With((IGitRepository)this));

        public bool WorkingCopyHasUnstagedOrUncommitedChanges
        {
            get
            {
                if (IsBare)
                    return false;
                return (from
                            entry in _repository.RetrieveStatus()
                        where
                            entry.State != FileStatus.Ignored &&
                            entry.State != FileStatus.NewInWorkdir
                        select entry).Any();
            }
        }

        public void CopyBlob(string sha, string outputFile)
        {
            Blob blob;
            var destination = new FileInfo(outputFile);
            if (!destination.Directory.Exists)
                destination.Directory.Create();
            if ((blob = _repository.Lookup<Blob>(sha)) != null)
                using (Stream stream = blob.GetContentStream(new FilteringOptions(string.Empty)))
                using (var outstream = File.Create(destination.FullName))
                    stream.CopyTo(outstream);
        }

        public string AssertValidBranchName(string gitBranchName)
        {
            if (!Reference.IsValidName(ShortToLocalName(gitBranchName)))
                throw new GitTfsException("The name specified for the new git branch is not allowed. Choose another one!");
            return gitBranchName;
        }

        private bool IsRefNameUsed(string gitBranchName)
        {
            var parts = gitBranchName.Split('/');
            var refName = parts.First();
            for (int i = 1; i <= parts.Length; i++)
            {
                if (HasRef(ShortToLocalName(refName)) || HasRef(ShortToTfsRemoteName(refName)))
                    return true;
                if (i < parts.Length)
                    refName += '/' + parts[i];
            }

            return false;
        }

        public bool CreateBranch(string gitBranchName, string target)
        {
            Reference reference;
            try
            {
                reference = _repository.Refs.Add(gitBranchName, target);
            }
            catch (Exception)
            {
                return false;
            }
            return reference != null;
        }

        private readonly Dictionary<int, string> changesetsCache = new Dictionary<int, string>();
        private bool cacheIsFull = false;

        public string FindCommitHashByChangesetId(int changesetId)
        {
            var commit = FindCommitByChangesetId(changesetId);
            if (commit == null)
                return null;

            return commit.Sha;
        }

        private static readonly Regex tfsIdRegex = new Regex("^git-tfs-id: .*;C([0-9]+)\r?$", RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.RightToLeft);

        public static bool TryParseChangesetId(string commitMessage, out int changesetId)
        {
            var match = tfsIdRegex.Match(commitMessage);
            if (match.Success)
            {
                changesetId = int.Parse(match.Groups[1].Value);
                return true;
            }

            changesetId = 0;
            return false;
        }

        private Commit FindCommitByChangesetId(int changesetId, string remoteRef = null)
        {
            Trace.WriteLine("Looking for changeset " + changesetId + " in git repository...");

            if (remoteRef == null)
            {
                string sha;
                if (changesetsCache.TryGetValue(changesetId, out sha))
                {
                    Trace.WriteLine("Changeset " + changesetId + " found at " + sha);
                    return _repository.Lookup<Commit>(sha);
                }
                if (cacheIsFull)
                {
                    Trace.WriteLine("Looking for changeset " + changesetId + " in git repository: CacheIsFull, stopped looking.");
                    return null;
                }
            }

            var reachableFromRemoteBranches = new CommitFilter
            {
                IncludeReachableFrom = _repository.Branches.Where(p => p.IsRemote),
                SortBy = CommitSortStrategies.Time
            };

            if (remoteRef != null)
            {
                var query = _repository.Branches.Where(p => p.IsRemote && p.CanonicalName.EndsWith(remoteRef));
                Trace.WriteLine("Looking for changeset " + changesetId + " in git repository: Adding remotes:");
                foreach (var reachable in query)
                {
                    Trace.WriteLine(reachable.CanonicalName + "reachable from " + remoteRef);
                }
                reachableFromRemoteBranches.IncludeReachableFrom = query;
            }
            var commitsFromRemoteBranches = _repository.Commits.QueryBy(reachableFromRemoteBranches);

            Commit commit = null;
            foreach (var c in commitsFromRemoteBranches)
            {
                int id;
                if (TryParseChangesetId(c.Message, out id))
                {
                    changesetsCache[id] = c.Sha;
                    if (id == changesetId)
                    {
                        commit = c;
                        break;
                    }
                }
                else
                {
                    foreach (var note in c.Notes)
                    {
                        if (TryParseChangesetId(note.Message, out id))
                        {
                            changesetsCache[id] = c.Sha;
                            if (id == changesetId)
                            {
                                commit = c;
                                break;
                            }
                        }
                    }
                }
            }
            if (remoteRef == null && commit == null)
                cacheIsFull = true; // repository fully scanned
            Trace.WriteLine((commit == null) ? " => Commit " + changesetId + " not found!" : " => Commit " + changesetId + " found! hash: " + commit.Sha);
            return commit;
        }

        public void CreateTag(string name, string sha, string comment, string Owner, string emailOwner, DateTime creationDate)
        {
            if (_repository.Tags[name] == null)
                _repository.ApplyTag(name, sha, new Signature(Owner, emailOwner, new DateTimeOffset(creationDate)), comment);
        }

        public void CreateNote(string sha, string content, string owner, string emailOwner, DateTime creationDate)
        {
            Signature author = new Signature(owner, emailOwner, creationDate);
            _repository.Notes.Add(new ObjectId(sha), content, author, author, "commits");
        }

        public void ResetHard(string sha) => _repository.Reset(ResetMode.Hard, sha);

        public bool IsBare => _repository.Info.IsBare;

        /// <summary>
        /// Gets all configured "subtree" remotes which point to the same Tfs URL as the given remote.
        /// If the given remote is itself a subtree, an empty enumerable is returned.
        /// </summary>
        public IEnumerable<IGitTfsRemote> GetSubtrees(IGitTfsRemote owner)
        {
            //a subtree remote cannot have subtrees itself.
            if (owner.IsSubtree)
                return Enumerable.Empty<IGitTfsRemote>();

            return ReadAllTfsRemotes().Where(x => x.IsSubtree && string.Equals(x.OwningRemoteId, owner.Id, StringComparison.InvariantCultureIgnoreCase));
        }

        public void ResetRemote(IGitTfsRemote remoteToReset, string target) => _repository.Refs.UpdateTarget(remoteToReset.RemoteRef, target);

        public string GetCurrentBranch() => _repository.Head.CanonicalName;

        public void GarbageCollect(bool auto, string additionalMessage)
        {
            if (Globals.DisableGarbageCollect)
                return;
            try
            {
                if (auto)
                    _globals.Repository.CommandNoisy("gc", "--auto");
                else
                    _globals.Repository.CommandNoisy("gc");
            }
            catch (Exception e)
            {
                Trace.WriteLine(e);
                Trace.TraceWarning("Warning: `git gc` failed! " + additionalMessage);
            }
        }

        public bool Checkout(string commitish)
        {
            try
            {
                LibGit2Sharp.Commands.Checkout(_repository, commitish);
                return true;
            }
            catch (CheckoutConflictException)
            {
                return false;
            }
        }

        public IEnumerable<GitCommit> FindParentCommits(string @from, string to)
        {
            var commits = _repository.Commits.QueryBy(
                new CommitFilter() { IncludeReachableFrom = @from, ExcludeReachableFrom = to, SortBy = CommitSortStrategies.Reverse, FirstParentOnly = true })
                .Select(c => new GitCommit(c));
            var parent = to;
            foreach (var gitCommit in commits)
            {
                if (gitCommit.Parents.All(c => c.Sha != parent))
                    return new List<GitCommit>();
                parent = gitCommit.Sha;
            }
            return commits;
        }

        public bool IsPathIgnored(string relativePath) => _repository.Ignore.IsPathIgnored(relativePath);

        public string CommitGitIgnore(string pathToGitIgnoreFile)
        {
            if (!File.Exists(pathToGitIgnoreFile))
            {
                Trace.TraceWarning("warning: the .gitignore file specified '{0}' does not exist!", pathToGitIgnoreFile);
            }
            var gitTreeBuilder = new GitTreeBuilder(_repository.ObjectDatabase);
            gitTreeBuilder.Add(".gitignore", pathToGitIgnoreFile, LibGit2Sharp.Mode.NonExecutableFile);
            var tree = gitTreeBuilder.GetTree();
            var signature = new Signature("git-tfs", "git-tfs@noreply.com", new DateTimeOffset(2000, 1, 1, 0, 0, 0, new TimeSpan(0)));
            var sha = _repository.ObjectDatabase.CreateCommit(signature, signature, ".gitignore", tree, new Commit[0], false).Sha;
            Trace.WriteLine(".gitignore commit created: " + sha);

            // Point our tfs remote branch to the .gitignore commit
            var defaultRef = ShortToTfsRemoteName("default");
            _repository.Refs.Add(defaultRef, new ObjectId(sha));

            // Also point HEAD to the .gitignore commit, if it isn't already. This
            // ensures a common initial commit for the git-tfs init --gitignore case.
            if (_repository.Head.CanonicalName != defaultRef)
                _repository.Refs.Add(_repository.Head.CanonicalName, new ObjectId(sha));

            return sha;
        }

        public void UseGitIgnore(string pathToGitIgnoreFile) =>
            //Should add ourself the rules to the temporary rules because committing directly to the git database
            //prevent libgit2sharp to detect the new .gitignore file
            _repository.Ignore.AddTemporaryRules(File.ReadLines(pathToGitIgnoreFile));

        public IDictionary<int, string> GetCommitChangeSetPairs()
        {
            var allCommits = _repository.Commits.QueryBy(new CommitFilter());
            var pairs = new Dictionary<int, string>() ;
            foreach (var c in allCommits)
            {
                int changesetId;
                if (TryParseChangesetId(c.Message, out changesetId))
                {
                    if (pairs.TryGetValue(changesetId, out var commitSha))
                        Trace.TraceWarning("warning: Git commit {0} couldn't be assigned to TFS changeset '{1}' as git commit '{2}' was assigned already. Please correct the export file accordingly if needed", c.Sha, changesetId, commitSha);
                    else
                        pairs.Add(changesetId, c.Sha);
                }
                else
                {
                    foreach (var note in c.Notes)
                    {
                        if (TryParseChangesetId(note.Message, out changesetId))
                        {
                            pairs.Add(changesetId, c.Sha);
                        }
                    }
                }
            }

            return pairs;
        }
    }
}
