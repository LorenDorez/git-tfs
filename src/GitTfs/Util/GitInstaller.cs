using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GitTfs.Util
{
    /// <summary>
    /// Handles detection and auto-installation of Git Portable.
    /// </summary>
    public class GitInstaller
    {
        private const string GitForWindowsReleasesApi = "https://api.github.com/repos/git-for-windows/git/releases/latest";
        private const string MinimumGitVersion = "2.34.0";
        private readonly string _toolsDir;

        public GitInstaller(string toolsDir)
        {
            _toolsDir = toolsDir ?? throw new ArgumentNullException(nameof(toolsDir));
        }

        /// <summary>
        /// Checks if Git is available in PATH or in the tools directory.
        /// </summary>
        public bool IsGitAvailable(out string gitPath)
        {
            gitPath = null;

            // Check in tools directory first
            var portableGitPath = Path.Combine(_toolsDir, "git", "bin", "git.exe");
            if (File.Exists(portableGitPath))
            {
                Trace.WriteLine($"[GitInstaller] Found Git in tools directory: {portableGitPath}");
                gitPath = portableGitPath;
                return true;
            }

            // Check PATH
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    Trace.WriteLine($"[GitInstaller] Found Git in PATH: {output.Trim()}");
                    gitPath = "git"; // Available in PATH
                    
                    // Verify minimum version
                    var versionMatch = Regex.Match(output, @"git version (\d+\.\d+\.\d+)");
                    if (versionMatch.Success)
                    {
                        var version = new Version(versionMatch.Groups[1].Value);
                        var minVersion = new Version(MinimumGitVersion);
                        if (version >= minVersion)
                        {
                            return true;
                        }
                        else
                        {
                            Console.WriteLine($"⚠️  Warning: Git version {version} found but {MinimumGitVersion}+ recommended");
                            return true; // Still return true but warn
                        }
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[GitInstaller] Git not found in PATH: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Downloads and installs Git Portable.
        /// </summary>
        public bool InstallGitPortable()
        {
            try
            {
                Console.WriteLine("\n📦 Auto-installing Git Portable...");
                Console.WriteLine("   Source: GitHub official releases (git-for-windows/git)");
                Console.WriteLine("   Size: ~45MB download");

                // Get latest release info
                var (downloadUrl, checksumUrl, version) = GetLatestPortableGitUrls();
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    Console.WriteLine("❌ Failed to retrieve Git Portable download URL");
                    return false;
                }

                Console.WriteLine($"   Version: {version}");
                Console.WriteLine($"   URL: {downloadUrl}");

                var gitPortableDir = Path.Combine(_toolsDir, "git");
                var downloadPath = Path.Combine(Path.GetTempPath(), $"PortableGit-{version}.7z.exe");
                var checksumPath = downloadPath + ".sha256";

                // Download Git Portable
                if (!DownloadFile(downloadUrl, downloadPath))
                {
                    Console.WriteLine("❌ Failed to download Git Portable");
                    return false;
                }

                // Download and verify checksum
                if (!string.IsNullOrEmpty(checksumUrl))
                {
                    Console.WriteLine("\n🔐 Verifying download integrity...");
                    if (DownloadFile(checksumUrl, checksumPath))
                    {
                        if (!VerifyChecksum(downloadPath, checksumPath))
                        {
                            Console.WriteLine("❌ Checksum verification failed - download may be corrupted");
                            File.Delete(downloadPath);
                            return false;
                        }
                        Console.WriteLine("✅ Checksum verified");
                    }
                    else
                    {
                        Console.WriteLine("⚠️  Warning: Could not download checksum file, skipping verification");
                    }
                }

                // Extract Git Portable
                Console.WriteLine("\n📂 Extracting Git Portable...");
                if (!ExtractGitPortable(downloadPath, gitPortableDir))
                {
                    Console.WriteLine("❌ Failed to extract Git Portable");
                    return false;
                }

                // Cleanup
                try
                {
                    File.Delete(downloadPath);
                    if (File.Exists(checksumPath))
                        File.Delete(checksumPath);
                }
                catch { /* Ignore cleanup errors */ }

                // Verify installation
                var gitExePath = Path.Combine(gitPortableDir, "bin", "git.exe");
                if (File.Exists(gitExePath))
                {
                    Console.WriteLine($"✅ Git Portable installed successfully: {gitPortableDir}");
                    Console.WriteLine($"   Git executable: {gitExePath}");
                    return true;
                }
                else
                {
                    Console.WriteLine("❌ Git executable not found after extraction");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error installing Git Portable: {ex.Message}");
                Trace.WriteLine($"[GitInstaller] Installation error: {ex}");
                return false;
            }
        }

        private (string downloadUrl, string checksumUrl, string version) GetLatestPortableGitUrls()
        {
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "git-tfs");
                    var json = client.DownloadString(GitForWindowsReleasesApi);

                    // Parse JSON to find PortableGit download URL
                    // Look for pattern: "browser_download_url":"https://...PortableGit-...-64-bit.7z.exe"
                    var urlMatch = Regex.Match(json, @"""browser_download_url"":""([^\""]+PortableGit-[^\""]+\-64-bit\.7z\.exe)""");
                    var checksumMatch = Regex.Match(json, @"""browser_download_url"":""([^\""]+PortableGit-[^\""]+\-64-bit\.7z\.exe\.sha256)""");
                    var versionMatch = Regex.Match(json, @"""tag_name"":""v([^\""]+)""");

                    if (urlMatch.Success)
                    {
                        var downloadUrl = urlMatch.Groups[1].Value;
                        var checksumUrl = checksumMatch.Success ? checksumMatch.Groups[1].Value : null;
                        var version = versionMatch.Success ? versionMatch.Groups[1].Value : "latest";
                        return (downloadUrl, checksumUrl, version);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[GitInstaller] Error fetching release info: {ex.Message}");
            }

            return (null, null, null);
        }

        private bool DownloadFile(string url, string outputPath)
        {
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "git-tfs");

                    var lastProgressUpdate = DateTime.Now;
                    client.DownloadProgressChanged += (sender, e) =>
                    {
                        // Update progress every 500ms to avoid too much output
                        if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds > 500)
                        {
                            var mbReceived = e.BytesReceived / 1024.0 / 1024.0;
                            var mbTotal = e.TotalBytesToReceive / 1024.0 / 1024.0;
                            Console.Write($"\r   Progress: {e.ProgressPercentage}% ({mbReceived:F1}MB / {mbTotal:F1}MB)");
                            lastProgressUpdate = DateTime.Now;
                        }
                    };

                    Console.Write($"   Downloading: {Path.GetFileName(url)}...");
                    client.DownloadFile(url, outputPath);
                    Console.WriteLine(" ✅");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" ❌ Error: {ex.Message}");
                Trace.WriteLine($"[GitInstaller] Download error: {ex}");
                return false;
            }
        }

        private bool VerifyChecksum(string filePath, string checksumPath)
        {
            try
            {
                // Read expected checksum from file
                var checksumContent = File.ReadAllText(checksumPath);
                var expectedChecksum = checksumContent.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();

                // Calculate actual checksum
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = sha256.ComputeHash(stream);
                    var actualChecksum = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

                    Trace.WriteLine($"[GitInstaller] Expected: {expectedChecksum}");
                    Trace.WriteLine($"[GitInstaller] Actual: {actualChecksum}");

                    return actualChecksum.Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[GitInstaller] Checksum verification error: {ex.Message}");
                return false;
            }
        }

        private bool ExtractGitPortable(string archivePath, string targetDir)
        {
            try
            {
                // PortableGit .7z.exe is a self-extracting archive
                // We can run it with -y (yes to all) and -o{output directory}
                Directory.CreateDirectory(targetDir);

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = archivePath,
                        Arguments = $"-y -o\"{targetDir}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                
                // Show some progress
                var progressChars = new[] { '|', '/', '-', '\\' };
                var progressIndex = 0;
                while (!process.HasExited)
                {
                    Console.Write($"\r   Extracting... {progressChars[progressIndex++ % progressChars.Length]}");
                    System.Threading.Thread.Sleep(100);
                }

                process.WaitForExit();
                Console.WriteLine("\r   Extracting... ✅");

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[GitInstaller] Extraction error: {ex}");
                return false;
            }
        }
    }
}
