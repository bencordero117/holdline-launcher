// AlienFiles.cs - delivers the AvP Alien mod to every player on Join.  10 Oct 2026
//
// WHAT THIS DOES
//   On every Join and every Fix my game (both go through
//   ClientFiles.EnsureInstalledAsync, which calls EnsureAsync below):
//     1. Reads a tiny text file from the "alien" release on GitHub:
//          alien-version.txt   ->   "<build> <SHA-256 of Alien.zip>"   e.g. "54 3F2A...".
//     2. Compares it with the stamp this launcher left in the player's game:
//          Data\SKSE\Plugins\AvPAlien\holdline-alien-version.txt
//        Same line AND the key files present -> nothing to do (one small request).
//     3. Otherwise downloads Alien.zip from the same release, checks its SHA-256
//        against the version file, unpacks it, and copies it into Data, overwriting.
//     4. Removes files an OLDER build installed that the new build no longer ships
//        (only files this launcher itself installed - the list is kept next to the
//        stamp), backs up a non-Alien animation cache the first time (same rule as
//        Install-Alien.bat), deletes the build 21-22 OpenAnimationReplacer folder.
//     5. Checks every file landed at the right size, then writes the stamp.
//
// UPDATING THE ALIEN FOR EVERYONE (no launcher rebuild):
//   run build-alien-zip.bat, then on the "alien" release replace BOTH assets,
//   Alien.zip and alien-version.txt. Next Join on every machine pulls the new build.
//
// NEVER BLOCKS A JOIN. Any failure here (GitHub down, antivirus, a locked file) is
// reported in the status line and the join carries on - the Alien is optional for
// playing on the server today.
//
// DOES NOT TURN THE PLUGIN ON. AvPAlien.esp is copied into Data but is not added to
// the player's plugins.txt (EnablePluginInLoadOrder = false). The Alien mod's own
// README says to disable it before joining Holdline, and nothing syncs it in
// multiplayer yet. Turning it on is part of the later multiplayer work.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace HoldlineLauncher
{
    public static class AlienFiles
    {
        private const string ReleaseBase =
            "https://github.com/bencordero117/holdline-launcher/releases/download/alien/";

        public const string VersionUrl = ReleaseBase + "alien-version.txt";
        public const string ZipUrl = ReleaseBase + "Alien.zip";

        // See the header. Flip only together with the server's load order.
        public const bool EnablePluginInLoadOrder = false;

        private static readonly HttpClient http =
            new() { Timeout = TimeSpan.FromMinutes(30) };

        // Files whose presence proves an install is really there, not just a stamp.
        private static readonly string[] KeyFiles =
        {
            "AvPAlien.esp",
            Path.Combine("SKSE", "Plugins", "AvPAlien.dll"),
            Path.Combine("meshes", "actors", "avpalien", "character assets", "skeleton.nif"),
        };

        // The two whole-game animation caches the Alien replaces.
        private static readonly string[] AnimationCaches =
        {
            "animationdatasinglefile.txt",
            "animationsetdatasinglefile.txt",
        };

        private static string StampDir(string dataDir) =>
            Path.Combine(dataDir, "SKSE", "Plugins", "AvPAlien");

        private static string StampPath(string dataDir) =>
            Path.Combine(StampDir(dataDir), "holdline-alien-version.txt");

        private static string FileListPath(string dataDir) =>
            Path.Combine(StampDir(dataDir), "holdline-alien-files.txt");

        /// <summary>
        /// Brings the Alien files up to the published build. Never throws; never
        /// fails a join. Returns a short line for the status bar, or null when
        /// nothing needed doing.
        /// </summary>
        public static async Task<string?> EnsureAsync(string dataDir, IProgress<string> report)
        {
            try
            {
                return await EnsureCoreAsync(dataDir, report);
            }
            catch (Exception ex)
            {
                var msg = "Alien mod not updated (" + ex.Message + "). Joining anyway.";
                report.Report(msg);
                return msg;
            }
        }

        private static async Task<string?> EnsureCoreAsync(string dataDir, IProgress<string> report)
        {
            // 1. What is published?
            string published;
            try
            {
                published = (await http.GetStringAsync(VersionUrl)).Trim();
            }
            catch (Exception ex)
            {
                var msg = "Couldn't check for an Alien mod update (" + ex.Message + "). Joining anyway.";
                report.Report(msg);
                return msg;
            }

            var parts = published.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1].Length != 64)
            {
                var msg = "The Alien mod's version file on GitHub doesn't look right. Joining anyway.";
                report.Report(msg);
                return msg;
            }
            var build = parts[0];
            var expectedSha = parts[1].ToUpperInvariant();
            var wantedStamp = build + " " + expectedSha;

            // 2. Already on it?
            if (ReadStamp(dataDir) == wantedStamp && KeyFiles.All(k => File.Exists(Path.Combine(dataDir, k))))
            {
                return null;
            }

            if (!ClientFiles.IsWritable(dataDir))
            {
                var msg = "Can't write the Alien mod into your Skyrim folder (Program Files?). " +
                          "Run Holdline as administrator. Joining anyway.";
                report.Report(msg);
                return msg;
            }

            var work = Path.Combine(Path.GetTempPath(), "Holdline-alien-" + Guid.NewGuid().ToString("N"));
            var zipPath = Path.Combine(work, "Alien.zip");
            var unzipDir = Path.Combine(work, "unzipped");

            try
            {
                Directory.CreateDirectory(work);

                // 3. Download + verify.
                report.Report("Downloading the Alien mod (build " + build + ", about 190 MB)...");
                using (var response = await http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var msg = "Couldn't download the Alien mod (server said " + (int)response.StatusCode + "). Joining anyway.";
                        report.Report(msg);
                        return msg;
                    }
                    var total = response.Content.Headers.ContentLength ?? -1L;
                    await using var src = await response.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(zipPath);
                    var buf = new byte[1 << 16];
                    long done = 0;
                    var lastPct = -1;
                    int n;
                    while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length))) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        done += n;
                        if (total > 0)
                        {
                            var pct = (int)(done * 100 / total);
                            if (pct != lastPct && pct % 5 == 0)
                            {
                                lastPct = pct;
                                report.Report("Downloading the Alien mod... " + pct + "%");
                            }
                        }
                    }
                }

                report.Report("Checking the Alien mod download...");
                var actualSha = await Task.Run(() => Sha256Of(zipPath));
                if (actualSha != expectedSha)
                {
                    var msg = "The Alien mod download didn't match its fingerprint, so it wasn't installed. Joining anyway.";
                    report.Report(msg);
                    return msg;
                }

                report.Report("Unpacking the Alien mod...");
                Directory.CreateDirectory(unzipDir);
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, unzipDir));

                if (!File.Exists(Path.Combine(unzipDir, "AvPAlien.esp")) ||
                    !File.Exists(Path.Combine(unzipDir, "SKSE", "Plugins", "AvPAlien.dll")))
                {
                    var msg = "The Alien mod download is missing AvPAlien.esp or AvPAlien.dll - a problem on our end. Joining anyway.";
                    report.Report(msg);
                    return msg;
                }

                // 4. Prepare Data, then copy.
                report.Report("Installing the Alien mod (build " + build + ")...");
                var newFiles = await Task.Run(() => ListRelative(unzipDir));
                await Task.Run(() =>
                {
                    RemoveFilesDroppedSinceLastBuild(dataDir, newFiles);
                    BackUpForeignAnimationCaches(dataDir);
                    RemoveOldOarFolder(dataDir);
                    CopyTreeInto(unzipDir, dataDir);
                });

                // 5. Proof, then stamp.
                var missing = await Task.Run(() => VerifyMirrored(unzipDir, dataDir));
                if (missing.Count > 0)
                {
                    var shown = string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? " and " + (missing.Count - 5) + " more" : "");
                    var msg = "The Alien mod copy didn't finish (" + shown + "). Close Skyrim and press Fix my game. Joining anyway.";
                    report.Report(msg);
                    return msg;
                }

                Directory.CreateDirectory(StampDir(dataDir));
                File.WriteAllLines(FileListPath(dataDir), newFiles);
                File.WriteAllText(StampPath(dataDir), wantedStamp);

                var done2 = "Alien mod build " + build + " installed.";
                report.Report(done2);
                return done2;
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
            }
        }

        /// <summary>The build number this machine has, or null. For reports.</summary>
        public static string? InstalledBuild(string dataDir)
        {
            var s = ReadStamp(dataDir);
            if (string.IsNullOrEmpty(s)) return null;
            var sp = s.IndexOf(' ');
            return sp > 0 ? s.Substring(0, sp) : s;
        }

        private static string? ReadStamp(string dataDir)
        {
            try
            {
                var p = StampPath(dataDir);
                return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Sha256Of(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }

        private static List<string> ListRelative(string root) =>
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                     .Select(f => Path.GetRelativePath(root, f))
                     .ToList();

        // Deletes files the PREVIOUS launcher-installed build put in Data that the new
        // build doesn't ship. Only paths from our own list; never anything else.
        private static void RemoveFilesDroppedSinceLastBuild(string dataDir, List<string> newFiles)
        {
            var listPath = FileListPath(dataDir);
            if (!File.Exists(listPath)) return;

            var keep = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
            var dataFull = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (var rel in File.ReadAllLines(listPath))
            {
                var r = rel.Trim();
                if (r.Length == 0 || keep.Contains(r)) continue;
                var full = Path.GetFullPath(Path.Combine(dataDir, r));
                if (!full.StartsWith(dataFull, StringComparison.OrdinalIgnoreCase)) continue; // never outside Data
                try { if (File.Exists(full)) File.Delete(full); } catch { }
            }
        }

        // Same rule as Install-Alien.bat :backup - a cache that isn't the Alien's own
        // (Nemesis, another mod) is copied to .avpalien-backup once, so
        // Uninstall-Alien.bat can put it back.
        private static void BackUpForeignAnimationCaches(string dataDir)
        {
            foreach (var name in AnimationCaches)
            {
                var path = Path.Combine(dataDir, "meshes", name);
                var backup = path + ".avpalien-backup";
                if (!File.Exists(path) || File.Exists(backup)) continue;
                try
                {
                    if (File.ReadAllText(path).Contains("AvPAlienProject")) continue;
                    File.Copy(path, backup, overwrite: false);
                }
                catch { }
            }
        }

        // Build 23+: executions use Skyrim's own kill moves; Install-Alien.bat deletes this.
        private static void RemoveOldOarFolder(string dataDir)
        {
            var oar = Path.Combine(dataDir, "meshes", "actors", "character", "animations",
                                   "OpenAnimationReplacer", "AvPAlien");
            try { if (Directory.Exists(oar)) Directory.Delete(oar, recursive: true); } catch { }
        }

        private static void CopyTreeInto(string sourceDir, string destDir)
        {
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static List<string> VerifyMirrored(string sourceDir, string destDir)
        {
            var missing = new List<string>();
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, file);
                var target = Path.Combine(destDir, rel);
                try
                {
                    if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(file).Length)
                        missing.Add(rel);
                }
                catch
                {
                    missing.Add(rel);
                }
            }
            return missing;
        }
    }
}
