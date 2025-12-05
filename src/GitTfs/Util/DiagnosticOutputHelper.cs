using System.Diagnostics;
using GitTfs.Core;

namespace GitTfs.Util
{
    [StructureMapSingleton]
    public class DiagnosticOutputHelper
    {
        private readonly Globals _globals;

        public DiagnosticOutputHelper(Globals globals)
        {
            _globals = globals;
        }

        public void OutputDiagnosticInformation(string commandName, IGitTfsRemote remote)
        {
            if (!_globals.DebugOutput)
                return;

            var separator = new string('=', 50);
            Trace.WriteLine(separator);
            Trace.WriteLine("=== Git-TFS Debug Information ===");
            Trace.WriteLine(separator);

            // Operation Context
            Trace.WriteLine($"Command: {commandName}");
            if (!string.IsNullOrEmpty(_globals.CommandLineRun))
                Trace.WriteLine($"Full Command: {_globals.CommandLineRun}");

            // TFVC Information
            if (remote != null)
            {
                Trace.WriteLine("");
                Trace.WriteLine("--- TFVC Information ---");
                Trace.WriteLine($"TFS Server URL: {remote.TfsUrl}");
                Trace.WriteLine($"TFS Repository Path: {remote.TfsRepositoryPath ?? "(subtree owner)"}");
                
                if (remote.MaxChangesetId > 0)
                    Trace.WriteLine($"Latest Changeset (local): C{remote.MaxChangesetId}");
                else
                    Trace.WriteLine("Latest Changeset (local): (none)");

                if (!string.IsNullOrEmpty(remote.TfsUsername))
                    Trace.WriteLine($"Authentication: Username specified ({remote.TfsUsername})");
                else
                    Trace.WriteLine("Authentication: Default credentials");
            }

            // Git Information
            if (_globals.Repository != null)
            {
                Trace.WriteLine("");
                Trace.WriteLine("--- Git Information ---");
                Trace.WriteLine($"Git Repository: {_globals.Repository.GitDir}");
                
                try
                {
                    var currentBranch = _globals.Repository.GetCurrentBranch();
                    if (!string.IsNullOrEmpty(currentBranch))
                        Trace.WriteLine($"Current Branch: {currentBranch}");
                }
                catch
                {
                    // Ignore errors getting current branch (may not be available in all contexts)
                }

                try
                {
                    var currentCommit = _globals.Repository.GetCurrentCommit();
                    if (!string.IsNullOrEmpty(currentCommit))
                        Trace.WriteLine($"Latest Git Commit: {currentCommit.Substring(0, Math.Min(8, currentCommit.Length))}");
                }
                catch
                {
                    // Ignore errors getting current commit
                }
            }

            // Remote Configuration
            if (remote != null)
            {
                Trace.WriteLine("");
                Trace.WriteLine("--- Remote Configuration ---");
                Trace.WriteLine($"Remote ID: {remote.Id}");
                Trace.WriteLine($"Remote Reference: {remote.RemoteRef}");
                
                if (!string.IsNullOrEmpty(remote.MaxCommitHash))
                    Trace.WriteLine($"Max Commit Hash: {remote.MaxCommitHash.Substring(0, Math.Min(8, remote.MaxCommitHash.Length))}");
            }

            // Feature Status
            if (_globals.Repository != null)
            {
                Trace.WriteLine("");
                Trace.WriteLine("--- Feature Status ---");
                
                // Check if export metadata is enabled
                var exportMetadatasEnabled = _globals.Repository.GetConfig(GitTfsConstants.ExportMetadatasConfigKey) == "true";
                Trace.WriteLine($"Export Metadata: {(exportMetadatasEnabled ? "Enabled" : "Disabled")}");

                // Git-Notes are always supported but we can check if they've been used
                var hasGitNotes = false;
                try
                {
                    // Check if git-notes exist in the repository
                    hasGitNotes = _globals.Repository.HasRef("refs/notes/commits");
                }
                catch
                {
                    // Ignore errors checking for git-notes
                }
                Trace.WriteLine($"Git-Notes: {(hasGitNotes ? "In use" : "Available (not yet used)")}");

                // Check if git-ignore support is disabled
                var gitIgnoreDisabled = _globals.Repository.GetConfig(GitTfsConstants.DisableGitignoreSupport) == "true";
                Trace.WriteLine($"Git Ignore Support: {(gitIgnoreDisabled ? "Disabled" : "Enabled")}");

                // Check if branches are ignored
                var ignoreBranches = _globals.Repository.GetConfig(GitTfsConstants.IgnoreBranches) == "true";
                if (ignoreBranches)
                    Trace.WriteLine("Branch Strategy: Branches ignored");
            }

            Trace.WriteLine(separator);
            Trace.WriteLine("");
        }
    }
}
