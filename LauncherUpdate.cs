// LauncherUpdate.cs
// Added 2026-07-31.
//
// The problem this solves: client files (the game side) already update
// themselves — ClientFiles.cs stamps a version into the Skyrim folder, sees a
// newer one, and pulls just the new files down. Nobody reinstalls Skyrim.
//
// The launcher EXE had no such thing. Every change to the launcher itself
// meant telling every existing player, by hand, to go and download a new exe.
// That is fine while "every existing player" is a couple of testers. It stops
// being fine the moment strangers have it, because the ones who don't see the
// message keep running old code against a moving server.
//
// So: on startup the launcher asks GitHub what the newest launcher release is,
// and if it's newer than itself, offers to update. One prompt, one download of
// a single ~70 MB exe, automatic relaunch.
//
// THREE TRAPS, all real, all handled below:
//
//   1. The repo's GitHub Releases are NOT all launcher releases. The client
//      files ship as releases too (client-v1, client-v2, client-v3...). The
//      obvious endpoint — /releases/latest — returns whichever release is
//      newest overall, which is usually a CLIENT release, whose tag is not a
//      version number at all. So we read the release LIST and keep only tags
//      shaped like v1.0.4.
//
//   2. A running .exe on Windows cannot overwrite itself. The file is locked
//      for writing for as long as the process lives. So the launcher never
//      tries: it downloads beside itself, hands the swap to a detached
//      PowerShell command that waits for this process to die first, and then
//      exits. PowerShell does the copy and starts the new exe.
//
//   3. THE STAGED FILE MUST NOT SHARE A NAME WITH ANYTHING A PLAYER MIGHT BE
//      RUNNING. This one was found the hard way, 2026-08-07. The staged
//      download used to be called "HoldlineLauncher.update.exe". Windows hides
//      known extensions, so that file shows on the desktop as
//      "HoldlineLauncher.update" — and a player whose earlier swap failed was
//      left with exactly that file sitting there, and quite reasonably started
//      opening it. From then on the updater staged its download ON TOP OF THE
//      FILE IT WAS RUNNING FROM, then asked Windows to copy that file over
//      itself. Windows refuses, nothing changes, and the update prompt comes
//      back forever: an infinite loop that no amount of clicking Yes escapes.
//      The staged name below is deliberately one no player would ever launch,
//      and the guard in DownloadAndApplyAsync refuses outright if the staged
//      path ever resolves to the running exe.
//
// Deliberately NOT forced. A failed or refused update leaves the old launcher
// working exactly as before — an update check must never be able to stop
// somebody joining a server.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace HoldlineLauncher
{
    /// <summary>
    /// A newer launcher release found on GitHub.
    /// </summary>
    public sealed class LauncherUpdateInfo
    {
        public string Version { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
        public string Notes { get; init; } = "";
    }

    public static class LauncherUpdate
    {
        // THE version of this build. Bump this in the same commit that
        // publishes a new release, and make the GitHub tag match it exactly
        // (tag "v1.0.5" <-> CurrentVersion "1.0.5"). If this is ever left
        // behind, every launcher in the wild re-downloads the build it is
        // already running, every single start.
        public const string CurrentVersion = "1.0.38";

        // Name of the file the new build is downloaded to before the swap.
        //
        // NOT "HoldlineLauncher.update.exe". See trap 3 at the top of this
        // file: that name renders as "HoldlineLauncher.update" in Explorer,
        // players launch it, and the updater then stages on top of itself and
        // loops forever. This name is inert — nothing about it invites a
        // double-click, and it never collides with the shipped exe name.
        private const string StagedFileName = "HoldlineLauncher.newversion.tmp";

        // Release list, newest first. Not /releases/latest — see trap 1 above.
        private const string ReleasesApiUrl =
            "https://api.github.com/repos/bencordero117/holdline-launcher/releases";

        // The page a player is sent to if the automatic path fails, so a
        // broken updater still leaves them able to update by hand.
        public const string ReleasesPageUrl =
            "https://github.com/bencordero117/holdline-launcher/releases";

        // Short timeout on the check: this runs at startup and must never be
        // the reason the window sits there doing nothing. The DOWNLOAD gets
        // its own, much longer, client.
        private static readonly HttpClient checkHttp =
            new() { Timeout = TimeSpan.FromSeconds(10) };

        private static readonly HttpClient downloadHttp =
            new() { Timeout = TimeSpan.FromMinutes(30) };

        static LauncherUpdate()
        {
            // GitHub's API returns 403 to any request without a User-Agent.
            // This is not optional and the failure looks like a network fault.
            checkHttp.DefaultRequestHeaders.UserAgent.ParseAdd("HoldlineLauncher");
            downloadHttp.DefaultRequestHeaders.UserAgent.ParseAdd("HoldlineLauncher");
        }

        /// <summary>
        /// Full path of the running launcher exe.
        ///
        /// Environment.ProcessPath, NOT Assembly.Location: the launcher is
        /// published as a single self-contained file, and Assembly.Location
        /// comes back EMPTY for single-file builds. Using it would silently
        /// write the update to nowhere.
        /// </summary>
        public static string? OwnExePath()
        {
            try
            {
                var path = Environment.ProcessPath;

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                return path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Deletes stale staged files left behind by a swap that never
        /// finished. Safe to call at any time: it only ever removes files this
        /// class created, and only when they are not the running exe.
        ///
        /// Also clears the OLD staged name from before the 2026-08-07 fix, so
        /// the "HoldlineLauncher.update" file that trapped a tester stops
        /// sitting on people's desktops inviting a double-click.
        /// </summary>
        public static void CleanUpStagedFiles()
        {
            try
            {
                var exePath = OwnExePath();

                if (exePath == null)
                {
                    return;
                }

                var folder = Path.GetDirectoryName(exePath);

                if (string.IsNullOrEmpty(folder))
                {
                    return;
                }

                foreach (var name in new[] { StagedFileName, "HoldlineLauncher.update.exe" })
                {
                    var stale = Path.Combine(folder, name);

                    // Never delete the file we are running from, however it
                    // came to be named that.
                    if (string.Equals(stale, exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    TryDelete(stale);
                }
            }
            catch
            {
                // Cleanup is a courtesy. It must never throw into startup.
            }
        }

        /// <summary>
        /// Asks GitHub whether a newer launcher exists. Returns null when
        /// we're current, when GitHub is unreachable, or on anything
        /// unexpected — every failure here is a non-event, never an error the
        /// player has to read.
        /// </summary>
        public static async Task<LauncherUpdateInfo?> CheckAsync()
        {
            try
            {
                // Runs here rather than needing a call from MainWindow: this
                // method is already invoked once at startup, so the stale
                // staged files from a failed swap get cleared on every launch
                // without any other file having to know about them.
                CleanUpStagedFiles();

                using var response = await checkHttp.GetAsync(ReleasesApiUrl);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var current = ParseVersion(CurrentVersion);

                if (current == null)
                {
                    return null;
                }

                LauncherUpdateInfo? best = null;
                List<int>? bestVersion = null;

                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    if (GetBool(release, "draft") || GetBool(release, "prerelease"))
                    {
                        continue;
                    }

                    var tag = GetString(release, "tag_name");

                    // Trap 1: client-v3 and friends live in this same list.
                    // ParseVersion returns null for anything not shaped like
                    // a version number, which filters them out.
                    var version = ParseVersion(tag);

                    if (version == null)
                    {
                        continue;
                    }

                    if (Compare(version, current) <= 0)
                    {
                        continue;
                    }

                    if (bestVersion != null && Compare(version, bestVersion) <= 0)
                    {
                        continue;
                    }

                    var assetUrl = FindExeAsset(release);

                    if (assetUrl == null)
                    {
                        // A launcher release with no exe attached is a
                        // publishing mistake, not an update. Skip it rather
                        // than offering an update that cannot download.
                        continue;
                    }

                    bestVersion = version;
                    best = new LauncherUpdateInfo
                    {
                        Version = tag.TrimStart('v', 'V'),
                        DownloadUrl = assetUrl,
                        Notes = GetString(release, "body")
                    };
                }

                return best;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Downloads the new exe next to the current one, then hands the swap
        /// to a detached PowerShell command and asks the caller to shut down.
        ///
        /// Returns null when the handover has been started successfully — the
        /// caller must then close the application, because the swap cannot
        /// happen while this process holds the file open. Returns a message to
        /// show the player on failure, in which case nothing has changed and
        /// the launcher keeps running normally.
        /// </summary>
        public static async Task<string?> DownloadAndApplyAsync(
            LauncherUpdateInfo info,
            IProgress<string> report)
        {
            var exePath = OwnExePath();

            if (exePath == null)
            {
                return "Holdline couldn't work out where it's running from, so it " +
                       "can't update itself. Download the new version from:\n" +
                       ReleasesPageUrl;
            }

            var folder = Path.GetDirectoryName(exePath);

            if (string.IsNullOrEmpty(folder))
            {
                return "Holdline couldn't work out where it's running from, so it " +
                       "can't update itself. Download the new version from:\n" +
                       ReleasesPageUrl;
            }

            // Staged NEXT TO the exe, not in %TEMP%. The copy at the end has
            // to land on top of the running exe, and a same-folder copy can't
            // fail halfway across a drive boundary or be swept away by a temp
            // cleaner between download and swap.
            var stagedPath = Path.Combine(folder, StagedFileName);

            // TRAP 3 GUARD. If the staged path is the file we are running
            // from, the download would overwrite the running exe and the swap
            // would ask Windows to copy a file onto itself — which fails
            // silently and re-offers the same update forever. Refuse instead,
            // and say something the player can act on.
            if (string.Equals(stagedPath, exePath, StringComparison.OrdinalIgnoreCase))
            {
                return "Holdline can't update itself while running under this filename. " +
                       "Download the newest version from:\n" + ReleasesPageUrl +
                       "\n\nSave it to your Desktop and open that copy from now on.";
            }

            // Prove we can write here BEFORE spending 70 MB of someone's
            // bandwidth. An exe sitting in Program Files can't update itself
            // without elevation, and finding that out after the download is a
            // waste of their time.
            if (!CanWriteTo(folder))
            {
                return "Holdline can't update itself where it's installed:\n" + folder +
                       "\n\nWindows protects that folder. Either move Holdline somewhere " +
                       "like your Desktop, or download the new version yourself from:\n" +
                       ReleasesPageUrl;
            }

            try
            {
                report.Report("Downloading Holdline " + info.Version + "...");

                var error = await DownloadAsync(info.DownloadUrl, stagedPath, report);

                if (error != null)
                {
                    TryDelete(stagedPath);
                    return error;
                }

                // A truncated download that still "succeeded" would replace a
                // working launcher with a broken one. A real published build
                // is tens of megabytes; anything tiny is a proxy page or an
                // error body, not our exe.
                var size = new FileInfo(stagedPath).Length;

                if (size < 1_000_000)
                {
                    TryDelete(stagedPath);
                    return "The update didn't download properly. Nothing has changed — " +
                           "Holdline will keep working, and you can try again later.";
                }

                report.Report("Restarting Holdline...");

                if (!StartSwapAndRelaunch(exePath, stagedPath))
                {
                    TryDelete(stagedPath);
                    return "Holdline couldn't start the update. Nothing has changed. You " +
                           "can download the new version from:\n" + ReleasesPageUrl;
                }

                return null;
            }
            catch (Exception ex)
            {
                TryDelete(stagedPath);
                return "The update failed:\n" + ex.Message +
                       "\n\nNothing has changed — Holdline will keep working as before.";
            }
        }

        /// <summary>
        /// Launches the detached process that does the actual swap.
        ///
        /// Why this can't be done in C#: this process has the exe open. So
        /// PowerShell waits for THIS process id to disappear, copies the
        /// staged file over the real one, starts it, and tidies up. The retry
        /// loop is there because Windows can hold the lock for a moment after
        /// the process is gone.
        /// </summary>
        private static bool StartSwapAndRelaunch(string exePath, string stagedPath)
        {
            try
            {
                var pid = Environment.ProcessId;

                var script =
                    "Wait-Process -Id " + pid + " -ErrorAction SilentlyContinue; " +
                    "$ok = $false; " +
                    "for ($i = 0; $i -lt 40; $i++) { " +
                    "  try { " +
                    "    Copy-Item -LiteralPath " + PsQuote(stagedPath) +
                    " -Destination " + PsQuote(exePath) + " -Force; " +
                    "    $ok = $true; break " +
                    "  } catch { Start-Sleep -Milliseconds 500 } " +
                    "}; " +
                    "Remove-Item -LiteralPath " + PsQuote(stagedPath) +
                    " -Force -ErrorAction SilentlyContinue; " +
                    "Start-Process -FilePath " + PsQuote(exePath);

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-WindowStyle");
                startInfo.ArgumentList.Add("Hidden");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(script);

                var process = System.Diagnostics.Process.Start(startInfo);

                return process != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Wraps a path as a PowerShell single-quoted string. Inside single
        /// quotes PowerShell treats everything literally, so only the quote
        /// itself needs escaping — by doubling it. This matters: folder names
        /// with apostrophes are common ("Ben's PC") and would otherwise end
        /// the string early and turn the rest of the path into commands.
        /// </summary>
        private static string PsQuote(string value)
        {
            return "'" + value.Replace("'", "''") + "'";
        }

        private static bool CanWriteTo(string folder)
        {
            try
            {
                var probe = Path.Combine(folder, ".holdline-update-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A leftover staged file costs one wasted download next time.
                // Never let cleanup be the thing that throws.
            }
        }

        private static async Task<string?> DownloadAsync(
            string url,
            string destPath,
            IProgress<string> report)
        {
            try
            {
                using var response = await downloadHttp.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    return "Couldn't download the update (server said " +
                           (int)response.StatusCode + "). Nothing has changed — " +
                           "Holdline will keep working as before.";
                }

                var total = response.Content.Headers.ContentLength ?? -1L;

                using (var source = await response.Content.ReadAsStreamAsync())
                using (var dest = File.Create(destPath))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    int lastPercent = -1;
                    int read;

                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await dest.WriteAsync(buffer, 0, read);
                        done += read;

                        if (total > 0)
                        {
                            var percent = (int)(done * 100 / total);

                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                report.Report("Downloading update... " + percent + "%");
                            }
                        }
                        else
                        {
                            report.Report("Downloading update... " + (done / 1048576) + " MB");
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                return "Couldn't download the update:\n" + ex.Message +
                       "\n\nNothing has changed — Holdline will keep working as before.";
            }
        }

        /// <summary>
        /// Finds the launcher exe attached to a release. Matches on extension
        /// rather than an exact filename, so renaming the asset one day
        /// doesn't quietly stop every launcher in the world from updating.
        /// </summary>
        private static string? FindExeAsset(JsonElement release)
        {
            if (!release.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");

                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = GetString(asset, "browser_download_url");

                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }

            return null;
        }

        /// <summary>
        /// Turns "v1.0.4" or "1.0.4" into [1, 0, 4]. Returns null for anything
        /// that isn't purely dotted numbers — which is what keeps client-v3
        /// and any future non-launcher tag out of the comparison entirely.
        /// </summary>
        private static List<int>? ParseVersion(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            var text = tag.Trim().TrimStart('v', 'V');

            if (text.Length == 0)
            {
                return null;
            }

            var parts = text.Split('.');
            var numbers = new List<int>();

            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var number) || number < 0)
                {
                    return null;
                }

                numbers.Add(number);
            }

            return numbers.Count == 0 ? null : numbers;
        }

        /// <summary>
        /// Compares dotted version numbers. Missing trailing parts count as
        /// zero, so "1.1" and "1.1.0" are the same version rather than the
        /// shorter one losing.
        /// </summary>
        private static int Compare(List<int> left, List<int> right)
        {
            var length = Math.Max(left.Count, right.Count);

            for (var i = 0; i < length; i++)
            {
                var a = i < left.Count ? left[i] : 0;
                var b = i < right.Count ? right[i] : 0;

                if (a != b)
                {
                    return a < b ? -1 : 1;
                }
            }

            return 0;
        }

        private static string GetString(JsonElement element, string field)
        {
            if (element.TryGetProperty(field, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }

            return "";
        }

        private static bool GetBool(JsonElement element, string field)
        {
            return element.TryGetProperty(field, out var value) &&
                   value.ValueKind == JsonValueKind.True;
        }
    }
}
