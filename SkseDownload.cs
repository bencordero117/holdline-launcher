// ============================================================
//  Holdline — SkseDownload.cs
//
//  The missing half of SKSE setup.
//
//  SkseSetup.cs already does everything AFTER a player hands us an
//  archive: unpack the .7z (via Windows' built-in tar.exe), check the
//  version inside actually matches their Skyrim, and copy the files
//  into the game folder. What it never did was GET the archive. That
//  job was left to the human: open a browser, find the right line on
//  silverlock, download a .7z, come back, pick the file. That step
//  broke two testers.
//
//  WHY THIS IS A SEPARATE FILE:
//    SkseSetup.cs is proven and installs correctly. This adds a new
//    entry point beside it rather than editing it, so the working
//    path stays untouched and the manual picker still exists as a
//    fallback when anything here fails.
//
//  WHY WE DO NOT SHIP SKSE OURSELVES:
//    SKSE's terms forbid re-hosting it, and forbid its use in
//    anything sold for money. Holdline charges to create a hold, so
//    bundling their file is off the table twice over. This does not
//    bundle anything. The player's own machine downloads the file
//    from silverlock's own server — we press the buttons for them.
//    Nothing is stored, mirrored, or redistributed by us.
//
//  WHY WE READ THEIR PAGE INSTEAD OF HARDCODING A URL:
//    silverlock asks people not to link directly to archived builds,
//    and the "current" build changes whenever Bethesda patches the
//    game. So we load the same front page a human would, find the
//    link whose text names their exact game version, and follow it.
//    That respects the request, and it never goes stale.
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HoldlineLauncher
{
    /// <summary>
    /// Downloads the correct SKSE build for this machine's Skyrim and
    /// installs it, with no file picking by the player.
    /// </summary>
    public static class SkseDownload
    {
        // One shared client. Creating an HttpClient per call is the
        // classic way to exhaust sockets on a machine that retries.
        private static readonly HttpClient Http = BuildClient();

        private static HttpClient BuildClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(3)
            };

            // Identify ourselves honestly. A blank user agent looks like
            // a scraper; this tells them exactly who is asking.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "HoldlineLauncher/1.0 (+https://holdline-chi.vercel.app)");

            return client;
        }

        // Anchor tags pointing at a .7z, capturing both the href and the
        // visible text, because the version we need is named in the text
        // around the link ("game version 1.6.1170"), not in the filename.
        private static readonly Regex ArchiveLinkPattern =
            new(@"<a\b[^>]*?href\s*=\s*[""']([^""']+?\.7z)[""'][^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex TagStripper =
            new(@"<[^>]+>", RegexOptions.Singleline);

        private static readonly Regex WhitespaceRun =
            new(@"\s+", RegexOptions.Singleline);

        // How far back to read for the version that labels a link. Their
        // entries are one short line each; 300 characters covers a line
        // comfortably without reaching into the previous build's entry.
        private const int ContextWindow = 300;

        /// <summary>
        /// Fetch the right SKSE for this Skyrim and install it.
        ///
        /// Returns null on success, or a human-readable message to show
        /// the player. Every failure message ends by pointing at the
        /// manual route, so a bad network is an inconvenience and never
        /// a dead end.
        ///
        /// <paramref name="report"/> is called with short status lines
        /// so the caller can show progress. It may be null.
        /// </summary>
        public static async Task<string?> DownloadAndInstallAsync(
            string? skyrimRoot,
            string? skyrimExePath,
            Action<string>? report = null,
            CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(skyrimRoot) || !Directory.Exists(skyrimRoot))
            {
                return "Holdline couldn't find your Skyrim folder, so it can't install SKSE there yet.";
            }

            // The whole point is installing the build that matches THIS
            // game. Without a version we'd be guessing, and guessing wrong
            // here is exactly what crashes people on startup.
            var required = SkseSetup.RequiredRuntimeTag(skyrimExePath);

            if (string.IsNullOrWhiteSpace(required))
            {
                return "Holdline couldn't read your Skyrim version, so it can't tell which " +
                       "SKSE you need.\n\n" + ManualRoute();
            }

            report?.Invoke("Checking silverlock.org for the right SKSE...");

            string page;

            try
            {
                page = await Http.GetStringAsync(SkseSetup.DownloadPage, cancel)
                                 .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return "Holdline couldn't reach silverlock.org to download SKSE.\n\n" +
                       "Check your internet connection, or install it yourself:\n\n" + ManualRoute();
            }

            var archiveUrl = FindArchiveFor(page, required, SkseSetup.DownloadPage);

            if (archiveUrl == null)
            {
                return "Holdline couldn't find an SKSE build for Skyrim " +
                       SkseStatus.Pretty(required) + " on silverlock.org.\n\n" +
                       "That usually means Skyrim updated very recently and SKSE hasn't " +
                       "caught up yet.\n\n" + ManualRoute();
            }

            // Download into a private temp folder, never the player's
            // Downloads, so we can't collide with a file they already have
            // and can't leave litter behind if this fails.
            var workDir = Path.Combine(Path.GetTempPath(),
                                       "holdline-skse-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(workDir);

                var archivePath = Path.Combine(workDir, SafeFileName(archiveUrl));

                report?.Invoke("Downloading SKSE " + SkseStatus.Pretty(required) + "...");

                try
                {
                    using var response = await Http
                        .GetAsync(archiveUrl, HttpCompletionOption.ResponseHeadersRead, cancel)
                        .ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();

                    using var source = await response.Content.ReadAsStreamAsync(cancel)
                                                             .ConfigureAwait(false);

                    using var target = new FileStream(
                        archivePath, FileMode.Create, FileAccess.Write, FileShare.None);

                    await source.CopyToAsync(target, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return "The SKSE download didn't finish.\n\n" +
                           "Check your internet connection, or install it yourself:\n\n" +
                           ManualRoute();
                }

                // A truncated or blocked download can land as a tiny file
                // that unpacks to nothing. SKSE is several hundred KB, so
                // anything under 100 KB is not the real thing.
                var landed = new FileInfo(archivePath);

                if (!landed.Exists || landed.Length < 100_000)
                {
                    return "The SKSE download came through incomplete.\n\n" +
                           "This is usually antivirus or a firewall blocking it. " +
                           "You can install it yourself instead:\n\n" + ManualRoute();
                }

                report?.Invoke("Installing SKSE...");

                // Hand off to the proven installer. It unpacks the .7z,
                // confirms the version inside really matches this Skyrim,
                // and copies the files into the game folder. Running that
                // check again here would only risk the two disagreeing.
                return SkseSetup.InstallFrom(archivePath, skyrimRoot!, skyrimExePath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return "Something went wrong installing SKSE: " + ex.Message + "\n\n" + ManualRoute();
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workDir))
                    {
                        Directory.Delete(workDir, recursive: true);
                    }
                }
                catch
                {
                    // Temp cleanup is a courtesy. Windows clears it eventually.
                }
            }
        }

        /// <summary>
        /// Picks the .7z on silverlock's page that belongs to a given game
        /// version tag such as 1_6_1170.
        ///
        /// Their page names the game version in the link text, like
        /// "Current Anniversary Edition build 2.2.6 (game version 1.6.1170)".
        /// We match on that rather than on the filename, because the
        /// filename carries SKSE's OWN version (skse64_2_02_06.7z), which
        /// is a different number and can't be derived from the game's.
        /// </summary>
        internal static string? FindArchiveFor(string? html, string? requiredTag, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(requiredTag))
            {
                return null;
            }

            // 1_6_1170 -> 1.6.1170, which is how the page writes it.
            var dotted = SkseStatus.Pretty(requiredTag!);

            foreach (Match match in ArchiveLinkPattern.Matches(html!))
            {
                var href = match.Groups[1].Value.Trim();

                // Skip source archives — they hold code, not the loader.
                if (href.Contains("_src", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The game version is NOT inside the link. Their page reads
                // "Current Anniversary Edition build 2.2.6 (game version
                // 1.6.1170): <a>7z archive</a>" — so the number we need sits
                // in the text LEADING UP TO the link. Read a window of that.
                var from = Math.Max(0, match.Index - ContextWindow);
                var context = WhitespaceRun.Replace(
                    TagStripper.Replace(html!.Substring(from, match.Index - from + match.Length), " "),
                    " ");

                // Each build on their page begins with the word "Current",
                // so trimming to the last one stops a link being credited
                // with the version from the entry above it.
                var marker = context.LastIndexOf("current", StringComparison.OrdinalIgnoreCase);

                if (marker > 0)
                {
                    context = context.Substring(marker);
                }

                // Matching on the game version is what makes the GOG and
                // Steam builds distinguishable: they share an SKSE version
                // number but report different game versions.
                if (!context.Contains(dotted, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Uri.TryCreate(new Uri(baseUrl), href, out var resolved))
                {
                    // Only ever follow a link back to silverlock. If their
                    // page were ever tampered with, this stops us fetching
                    // an executable from somewhere else entirely.
                    if (resolved.Host.EndsWith("silverlock.org", StringComparison.OrdinalIgnoreCase))
                    {
                        return resolved.ToString();
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// A filename we're willing to write, derived from the URL.
        /// Never trusts the remote string to be a safe path.
        /// </summary>
        private static string SafeFileName(string url)
        {
            var name = "skse.7z";

            try
            {
                var candidate = Path.GetFileName(new Uri(url).LocalPath);

                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    var cleaned = new List<char>();

                    foreach (var c in candidate)
                    {
                        cleaned.Add(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
                    }

                    var rebuilt = new string(cleaned.ToArray());

                    if (rebuilt.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                    {
                        name = rebuilt;
                    }
                }
            }
            catch
            {
                // Fall through to the default name.
            }

            return name;
        }

        private static string ManualRoute()
        {
            return "1.  Go to https://skse.silverlock.org/\n" +
                   "2.  Find the line naming your Skyrim version and click its 7z link.\n" +
                   "3.  Come back to Holdline and use \"Install SKSE from a file\".";
        }
    }
}
