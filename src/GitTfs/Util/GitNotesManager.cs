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
        private const string NotesNamespace = "tfvc-sync"; // Short name for comparison

        public GitNotesManager(Repository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>
        /// Stores TFS changeset metadata as a git note on the specified commit.
        /// </summary>
        public void AddNote(string commitSha, string tfsUrl, string tfsRepositoryPath, int changesetId)
        {
            Trace.WriteLine($"[GitNotes] AddNote called: commitSha={commitSha}, changesetId={changesetId}");
            
            if (string.IsNullOrWhiteSpace(commitSha))
            {
                Trace.WriteLine("[GitNotes] ERROR: commitSha is null or empty");
                throw new ArgumentNullException(nameof(commitSha));
            }

            var commit = _repository.Lookup<Commit>(commitSha);
            if (commit == null)
            {
                Trace.WriteLine($"[GitNotes] ERROR: Commit {commitSha} not found in repository");
                throw new ArgumentException($"Commit {commitSha} not found", nameof(commitSha));
            }

            Trace.WriteLine($"[GitNotes] Commit found: {commitSha}");
            Trace.WriteLine($"[GitNotes] Creating note content for: URL={tfsUrl}, Path={tfsRepositoryPath}, C{changesetId}");

            // Create note content in simple text format
            var noteContent = CreateNoteContent(tfsUrl, tfsRepositoryPath, changesetId);
            Trace.WriteLine($"[GitNotes] Note content created: {noteContent}");

            // Add the note
            var signature = commit.Committer;
            Trace.WriteLine($"[GitNotes] Using signature: {signature.Name} <{signature.Email}>");
            Trace.WriteLine($"[GitNotes] Notes ref: {NotesRef}");
            
            try
            {
                Trace.WriteLine($"[GitNotes] Attempting to add note to commit {commitSha}...");
                _repository.Notes.Add(commit.Id, noteContent, signature, signature, NotesRef);
                Trace.WriteLine($"[GitNotes] ? Successfully added git note for commit {commitSha} with changeset C{changesetId}");
            }
            catch (LibGit2Sharp.NameConflictException ex)
            {
                // Note already exists, update it
                Trace.WriteLine($"[GitNotes] Note already exists, updating... ({ex.Message})");
                _repository.Notes.Remove(commit.Id, signature, signature, NotesRef);
                _repository.Notes.Add(commit.Id, noteContent, signature, signature, NotesRef);
                Trace.WriteLine($"[GitNotes] ? Successfully updated git note for commit {commitSha} with changeset C{changesetId}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[GitNotes] ? ERROR adding note: {ex.GetType().Name}: {ex.Message}");
                Trace.WriteLine($"[GitNotes] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Retrieves TFS changeset metadata from git notes for the specified commit.
        /// </summary>
        public TfsNoteInfo GetNote(string commitSha)
        {
            Trace.WriteLine($"[GitNotesManager.GetNote] Called for commit: {commitSha}");
            
            if (string.IsNullOrWhiteSpace(commitSha))
            {
                Trace.WriteLine($"[GitNotesManager.GetNote] ? commitSha is null or whitespace");
                return null;
            }

            var commit = _repository.Lookup<Commit>(commitSha);
            if (commit == null)
            {
                Trace.WriteLine($"[GitNotesManager.GetNote] ? Commit not found in repository");
                return null;
            }

            Trace.WriteLine($"[GitNotesManager.GetNote] Commit found, checking for notes...");
            Trace.WriteLine($"[GitNotesManager.GetNote] Looking for notes in ref: {NotesRef}");
            
            try
            {
                // Check if the commit has notes in our custom ref
                var notesCount = 0;
                foreach (var note in commit.Notes)
                {
                    notesCount++;
                    Trace.WriteLine($"[GitNotesManager.GetNote] Found note #{notesCount}:");
                    Trace.WriteLine($"[GitNotesManager.GetNote]   - Namespace: {note.Namespace}");
                    Trace.WriteLine($"[GitNotesManager.GetNote]   - Message preview: {(note.Message.Length > 100 ? note.Message.Substring(0, 100) + "..." : note.Message)}");
                    
                    // LibGit2Sharp stores the namespace as just "tfvc-sync" (without "refs/notes/" prefix)
                    // So we need to compare against the short name, not the full ref
                    if (note.Namespace == NotesNamespace)
                    {
                        Trace.WriteLine($"[GitNotesManager.GetNote] ? Found matching note in {NotesNamespace}!");
                        Trace.WriteLine($"[GitNotesManager.GetNote] Full note content:\n{note.Message}");
                        var parsed = ParseNoteContent(note.Message);
                        if (parsed != null)
                        {
                            Trace.WriteLine($"[GitNotesManager.GetNote] ? Successfully parsed: C{parsed.ChangesetId}");
                        }
                        else
                        {
                            Trace.WriteLine($"[GitNotesManager.GetNote] ? Failed to parse note content");
                        }
                        return parsed;
                    }
                }
                
                if (notesCount == 0)
                {
                    Trace.WriteLine($"[GitNotesManager.GetNote] ? No notes found on this commit");
                }
                else
                {
                    Trace.WriteLine($"[GitNotesManager.GetNote] ? Found {notesCount} note(s), but none matching namespace '{NotesNamespace}'");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[GitNotesManager.GetNote] ? Exception while checking notes: {ex.GetType().Name}: {ex.Message}");
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
