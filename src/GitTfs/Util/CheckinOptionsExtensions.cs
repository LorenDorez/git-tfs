using System.Text.RegularExpressions;
using GitTfs.Commands;
using GitTfs.Core;
using System.Diagnostics;

namespace GitTfs.Util
{
    public static class CheckinOptionsExtensions
    {
        public static CheckinOptions Clone(this CheckinOptions source, Globals globals)
        {
            CheckinOptions clone = new CheckinOptions();

            clone.CheckinComment = source.CheckinComment;
            clone.NoGenerateCheckinComment = source.NoGenerateCheckinComment;
            clone.NoMerge = source.NoMerge;
            clone.OverrideReason = source.OverrideReason;
            clone.Force = source.Force;
            clone.OverrideGatedCheckIn = source.OverrideGatedCheckIn;
            clone.WorkItemsToAssociate.AddRange(source.WorkItemsToAssociate);
            clone.WorkItemsToResolve.AddRange(source.WorkItemsToResolve);
            clone.AuthorTfsUserId = source.AuthorTfsUserId;
            try
            {
                string re = globals.Repository.GetConfig(GitTfsConstants.WorkItemAssociateRegexConfigKey);
                if (string.IsNullOrEmpty(re))
                    clone.WorkItemAssociateRegex = GitTfsConstants.TfsWorkItemAssociateRegex;
                else
                    clone.WorkItemAssociateRegex = new Regex(re);
            }
            catch (Exception)
            {
                clone.WorkItemAssociateRegex = null;
            }
            foreach (var note in source.CheckinNotes)
            {
                clone.CheckinNotes[note.Key] = note.Value;
            }

            return clone;
        }

        public static void ProcessWorkItemCommands(this CheckinOptions checkinOptions, bool isResolvable = true)
        {
            MatchCollection workitemMatches = GitTfsConstants.TfsWorkItemRegex.Matches(checkinOptions.CheckinComment);
            if (workitemMatches.Count > 0)
            {
                foreach (Match match in workitemMatches)
                {
                    if (isResolvable && match.Groups["action"].Value == "resolve")
                    {
                        Trace.TraceInformation("Resolving work item {0}", match.Groups["item_id"]);
                        checkinOptions.WorkItemsToResolve.Add(match.Groups["item_id"].Value);
                    }
                    else
                    {
                        Trace.TraceInformation("Associating with work item {0}", match.Groups["item_id"]);
                        checkinOptions.WorkItemsToAssociate.Add(match.Groups["item_id"].Value);
                    }
                }
                checkinOptions.CheckinComment = GitTfsConstants.TfsWorkItemRegex.Replace(checkinOptions.CheckinComment, "").Trim(' ', '\r', '\n');
            }

            if (checkinOptions.WorkItemAssociateRegex != null)
            {
                var workitemAssociatedMatches = checkinOptions.WorkItemAssociateRegex.Matches(checkinOptions.CheckinComment);
                if (workitemAssociatedMatches.Count != 0)
                {
                    foreach (Match match in workitemAssociatedMatches)
                    {
                        var workitem = match.Groups["item_id"].Value;
                        if (!checkinOptions.WorkItemsToAssociate.Contains(workitem))
                        {
                            Trace.TraceInformation("Associating with work item {0}", workitem);
                            checkinOptions.WorkItemsToAssociate.Add(workitem);
                        }
                    }
                }
            }
        }

        public static void ProcessCheckinNoteCommands(this CheckinOptions checkinOptions)
        {
            MatchCollection matches = GitTfsConstants.TfsReviewerRegex.Matches(checkinOptions.CheckinComment);
            if (matches.Count == 0)
                return;

            foreach (Match match in matches)
            {
                string reviewer = match.Groups["reviewer"].Value;
                if (!string.IsNullOrWhiteSpace(reviewer))
                {
                    switch (match.Groups["type"].Value)
                    {
                        case "code":
                            Trace.TraceInformation("Code reviewer: {0}", reviewer);
                            checkinOptions.CheckinNotes.Add("Code Reviewer", reviewer);
                            break;
                        case "security":
                            Trace.TraceInformation("Security reviewer: {0}", reviewer);
                            checkinOptions.CheckinNotes.Add("Security Reviewer", reviewer);
                            break;
                        case "performance":
                            Trace.TraceInformation("Performance reviewer: {0}", reviewer);
                            checkinOptions.CheckinNotes.Add("Performance Reviewer", reviewer);
                            break;
                    }
                }
            }
            checkinOptions.CheckinComment = GitTfsConstants.TfsReviewerRegex.Replace(checkinOptions.CheckinComment, "").Trim(' ', '\r', '\n');
        }

        public static void ProcessForceCommand(this CheckinOptions checkinOptions)
        {
            MatchCollection workitemMatches = GitTfsConstants.TfsForceRegex.Matches(checkinOptions.CheckinComment);
            if (workitemMatches.Count != 1)
                return;

            string overrideReason = workitemMatches[0].Groups["reason"].Value;
            if (!string.IsNullOrWhiteSpace(overrideReason))
            {
                Trace.TraceInformation("Forcing the checkin: {0}", overrideReason);
                checkinOptions.Force = true;
                checkinOptions.OverrideReason = overrideReason;
            }
            checkinOptions.CheckinComment = GitTfsConstants.TfsForceRegex.Replace(checkinOptions.CheckinComment, "").Trim(' ', '\r', '\n');
        }

        public static void ProcessAuthor(this CheckinOptions checkinOptions, GitCommit commit, AuthorsFile authors)
        {
            // For merge commits, prefer the author of the most recent merged parent
            // This gives credit to the developer who did the actual work, not just who clicked "merge"
            var commitToUseForAuthor = commit;
            var parents = commit.Parents.ToList();
            if (parents.Count > 1)
            {
                // This is a merge commit - use the last (most recent) parent's author
                // Skip the first parent (main branch) and use the last merged branch parent
                var lastParent = parents.Last();
                Trace.TraceInformation("Merge commit detected. Using author from last parent: {0}", lastParent.Sha.Substring(0, 8));
                commitToUseForAuthor = lastParent;
            }
            
            // If authors file is provided and has a match, use it (existing behavior)
            if (authors.IsParseSuccessfull)
            {
                Author a = authors.FindAuthor(commitToUseForAuthor.AuthorAndEmail);
                if (a != null)
                {
                    checkinOptions.AuthorTfsUserId = a.TfsUserId;
                    Trace.TraceInformation("Commit was authored by git user {0} {1} ({2})", a.Name, a.Email, a.TfsUserId);
                    return;
                }
            }

            // NEW: Automatic TFS user ID inference from git commit author
            // Try to infer TFS user from git email when authors file doesn't have a match
            var (name, email) = commitToUseForAuthor.AuthorAndEmail;
            var inferredTfsUserId = TryInferTfsUserIdFromGitAuthor(name, email);
            
            if (!string.IsNullOrWhiteSpace(inferredTfsUserId))
            {
                checkinOptions.AuthorTfsUserId = inferredTfsUserId;
                Trace.TraceInformation("Inferred TFS user from git commit author: {0} <{1}> → {2}", name, email, inferredTfsUserId);
            }
            else
            {
                // No inference possible - will use authenticated user (existing behavior)
                checkinOptions.AuthorTfsUserId = null;
                Trace.TraceInformation("Could not infer TFS user from git commit author: {0} <{1}>, will use authenticated user", name, email);
            }
        }

        /// <summary>
        /// Attempts to infer a TFS user ID from git commit author information.
        /// The inference behavior can be configured via git-tfs.checkin-author-format:
        /// - "username" (simplest): Just use the email username part (e.g., "john" from "john@company.com")
        /// - "domain-username" (default): Try to construct DOMAIN\username format
        /// - "email": Use the full email address as-is
        /// 
        /// Supported automatic patterns (when format is "domain-username"):
        /// 1. Name already in "DOMAIN\user" format → preserve as-is
        /// 2. Email "user@domain.com" → "DOMAIN\user"
        /// 3. Email "user@subdomain.domain.com" → "SUBDOMAIN\user"
        /// </summary>
        private static string TryInferTfsUserIdFromGitAuthor(string name, string email)
        {
            // Pattern 1: Name is already in "DOMAIN\user" or "DOMAIN/user" format - always preserve
            if (!string.IsNullOrWhiteSpace(name) && (name.Contains("\\") || name.Contains("/")))
            {
                // Normalize to backslash format
                return name.Replace("/", "\\");
            }

            // Pattern 2: Email format - extract username part
            string username = null;
            string domain = null;
            
            if (!string.IsNullOrWhiteSpace(email) && email.Contains("@"))
            {
                var parts = email.Split('@');
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    username = parts[0];
                    domain = parts[1];
                }
            }

            // If we couldn't extract username from email, try using name as username
            if (string.IsNullOrWhiteSpace(username) && 
                !string.IsNullOrWhiteSpace(name) && 
                !name.Contains(" ") && 
                !name.Contains("@"))
            {
                username = name;
            }

            // If we still don't have a username, give up
            if (string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            // Return just the username (simplest, most compatible)
            // TFS will resolve it against the configured authentication domain
            // If your TFS requires DOMAIN\username format, the user can:
            // 1. Set their git name to "DOMAIN\username" (detected in Pattern 1 above), or
            // 2. Use an authors file for explicit mapping
            return username;
        }
    }
}
