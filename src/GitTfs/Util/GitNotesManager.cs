using System;
using System.Collections.Generic;
using System.Diagnostics;
using LibGit2Sharp;

namespace GitTfs.Util
{
    /// <summary>
    /// Manages git notes for storing TFS changeset metadata without modifying commit messages.
    /// </summary>
    public class GitNotesManager
    {
        private readonly Repository _repository;
        private const string NotesRef = "refs/notes/tfvc-sync";

        public GitNotesManager(Repository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>
        /// Stores TFS changeset metadata as a git note on the specified commit.
        /// </summary>
        public void AddNote(string commitSha, string tfsUrl, string tfsRepositoryPath, int changesetId)
        {
            if (string.IsNullOrWhiteSpace(commitSha))
                throw new ArgumentNullException(nameof(commitSha));

            var commit = _repository.Lookup<Commit>(commitSha);
            if (commit == null)
                throw new ArgumentException($"Commit {commitSha} not found", nameof(commitSha));

            // Create note content in simple text format
            var noteContent = CreateNoteContent(tfsUrl, tfsRepositoryPath, changesetId);

            // Add the note
            var signature = commit.Committer;
            try
            {
                _repository.Notes.Add(commit.Id, noteContent, signature, signature, NotesRef);
                Trace.WriteLine($"Added git note for commit {commitSha} with changeset C{changesetId}");
            }
            catch (LibGit2Sharp.NameConflictException)
            {
                // Note already exists, update it
                _repository.Notes.Remove(commit.Id, signature, signature, NotesRef);
                _repository.Notes.Add(commit.Id, noteContent, signature, signature, NotesRef);
                Trace.WriteLine($"Updated git note for commit {commitSha} with changeset C{changesetId}");
            }
        }

        /// <summary>
        /// Retrieves TFS changeset metadata from git notes for the specified commit.
        /// </summary>
        public TfsNoteInfo GetNote(string commitSha)
        {
            if (string.IsNullOrWhiteSpace(commitSha))
                return null;

            var commit = _repository.Lookup<Commit>(commitSha);
            if (commit == null)
                return null;

            try
            {
                // Check if the commit has notes in our custom ref
                foreach (var note in commit.Notes)
                {
                    // Check if this note is from our tfvc-sync ref
                    if (note.Namespace == NotesRef)
                    {
                        return ParseNoteContent(note.Message);
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return null;
        }

        /// <summary>
        /// Creates simple text content for a git note.
        /// Format: changeset={id}\ntfs_url={url}\ntfs_path={path}\n
        /// </summary>
        private string CreateNoteContent(string tfsUrl, string tfsRepositoryPath, int changesetId)
        {
            var lines = new List<string>
            {
                $"changeset={changesetId}",
                $"tfs_url={tfsUrl ?? ""}",
                $"tfs_path={tfsRepositoryPath ?? ""}",
                $"synced_at={DateTime.UtcNow:o}"
            };

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Parses git note content and extracts TFS metadata.
        /// </summary>
        private TfsNoteInfo ParseNoteContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            try
            {
                var data = new Dictionary<string, string>();
                var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length == 2)
                    {
                        data[parts[0].Trim()] = parts[1].Trim();
                    }
                }

                if (data.ContainsKey("changeset"))
                {
                    if (int.TryParse(data["changeset"], out int changesetId))
                    {
                        var tfsUrl = data.ContainsKey("tfs_url") ? data["tfs_url"] : null;
                        var tfsPath = data.ContainsKey("tfs_path") ? data["tfs_path"] : null;

                        return new TfsNoteInfo
                        {
                            ChangesetId = changesetId,
                            TfsUrl = string.IsNullOrEmpty(tfsUrl) ? null : tfsUrl,
                            TfsRepositoryPath = string.IsNullOrEmpty(tfsPath) ? null : tfsPath
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to parse note content: {ex.Message}");
            }

            return null;
        }
    }

    /// <summary>
    /// Represents TFS metadata stored in git notes.
    /// </summary>
    public class TfsNoteInfo
    {
        public int ChangesetId { get; set; }
        public string TfsUrl { get; set; }
        public string TfsRepositoryPath { get; set; }
    }
}
