// SkympConnector.cs
// Confirmed against the actual dist build (client/Data/Platform/Plugins/
// skymp5-client-settings.txt) on 2026-07-27. The file's exact contents:
//
// {
//   "gameData": { "profileId": 1 },
//   "master": "",
//   "server-ip": "127.0.0.1",
//   "server-master-key": null,
//   "server-port": 7777
// }
//
// UPDATED 2026-07-27 (evening), after proving the master-server chain
// end to end. Two changes, both driven by what the game actually did:
//
//   1. WriteServerSettings now also writes "master" and
//      "server-master-key". Writing only ip/port is not enough: with
//      master empty the client requests /api/servers//manifest.json,
//      gets a bad response, retries four times and ends up with an
//      empty load order. Filling both fields is what turned that into
//      a real manifest fetch.
//
//   2. sourceClientDataDir is now optional. If it is null or missing,
//      the client-data copy is skipped. Re-copying on every join stacks
//      duplicate files into the player's Data folder, and on an install
//      that already has the client files it buys nothing.
//
// Also worth knowing: a Vercel PREVIEW url will not work as the master.
// Preview deployments are private and answer unauthenticated requests
// with a 302 redirect to a login page, which the client reports as
// "status code 302". Use the production domain.
//
// CRITICAL: the game must be launched via skse64_loader.exe, NOT
// Skyrim.exe. MpClientPlugin.dll and SkyrimPlatform.dll live in
// Data/SKSE/Plugins/ — they are SKSE plugins. Launching Skyrim.exe
// directly means SKSE never loads, those DLLs never load, and the player
// boots into vanilla single-player with no multiplayer layer at all.
// WorkingDirectory must be the Skyrim root, or address-library plugins
// fail to load.

using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HoldlineLauncher
{
    public static class SkympConnector
    {
        // Skyrim versions confirmed present as versionlib-*.bin files in the
        // dist build. If the player's Skyrim.exe doesn't match one of these,
        // refuse early with a clear message instead of letting SKSE crash.
        private static readonly string[] SupportedVersions = new[]
        {
            "1.6.317.0",
            "1.6.318.0",
            "1.6.323.0",
            "1.6.342.0",
            "1.6.353.0",
            "1.6.640.0",
            "1.6.1170.0",
        };

        // SKSE's loader executable, expected in the Skyrim root folder
        // (same directory as Skyrim.exe). SKSE is NOT shipped in the dist
        // build — the player installs it themselves — so we detect it and
        // give a clear message rather than silently launching vanilla.
        private const string SkseLoaderName = "skse64_loader.exe";

        // Files in client/Data that are MODIFIED VANILLA game files, not
        // new SkyMP-only files (confirmed against the dist.zip listing).
        // Overwriting these with no backup permanently alters the player's
        // single-player install with no way back, so we back each up once
        // before the first overwrite.
        private static readonly string[] VanillaFilesToBackup = new[]
        {
            Path.Combine("Scripts", "uimenubase.pex"),
            Path.Combine("Scripts", "QF_MQ201_00035D5F.pex"),
            Path.Combine("Scripts", "playerbookshelfcontainerscript.pex"),
            Path.Combine("Scripts", "DLC2SummonDremoraMerchantScript.pex"),
            Path.Combine("Scripts", "TIF__000361DF.pex"),
        };

        // Skyrim 1.7.99 — the Steam update Bethesda pushed on 20 Aug 2026.
        // It is NOT a normal "unsupported version": it is the version every
        // player gets automatically, including anyone who buys Skyrim today,
        // and it breaks EVERY SKSE plugin (not just ours) because the address
        // library switch in CommonLibSSE-NG reads the minor version and has
        // no case for 7. So it gets its own message and its own repair path
        // instead of being lumped in with the generic rejection.
        //
        // The changelog for 1.7.99 is console mod storage and localised UI
        // strings — no PC gameplay content — so going back to 1.6.1170 costs
        // the player nothing.
        //
        // MATCHED ON THE MINOR NUMBER, NOT A VERSION STRING. The failure is in
        // CommonLibSSE-NG's load_version(), which switches on the MINOR part:
        // case 4 -> VR, case 6 -> AE, default -> SE. Minor 7 hits default, the
        // library decides the game is SE 1.5.97, and every relocated address
        // is then read from the wrong table. So the thing that identifies a
        // broken build is minor >= 7 — not the literal "1.7.99.0". Written
        // this way it also catches 1.7.100 and anything after it, and it does
        // not depend on a version string nobody here has been able to read off
        // a real 1.7.99 exe.
        public const int BrokenMinorVersion = 7;

        // The version we put people back on. Newest entry in SupportedVersions.
        public const string TargetVersion = "1.6.1170.0";

        /// <summary>
        /// Reads the four-part file version off SkyrimSE.exe. Returns an empty
        /// string if the file isn't there — callers that care about a missing
        /// exe must check for that themselves, because "missing" and "wrong
        /// version" are different problems with different fixes.
        /// </summary>
        public static string GetInstalledVersion(string skyrimExePath)
        {
            if (!File.Exists(skyrimExePath))
            {
                return string.Empty;
            }

            var info = FileVersionInfo.GetVersionInfo(skyrimExePath);
            return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
        }

        /// <summary>
        /// True when the player is on the broken Steam update and we know how
        /// to put them right. The UI uses this to decide whether to offer the
        /// repair instead of a dead end.
        /// </summary>
        public static bool NeedsDowngrade(string skyrimExePath)
        {
            if (!File.Exists(skyrimExePath))
            {
                return false;
            }

            var info = FileVersionInfo.GetVersionInfo(skyrimExePath);
            return info.FileMajorPart == 1 && info.FileMinorPart >= BrokenMinorVersion;
        }

        public static string? CheckGameVersion(string skyrimExePath)
        {
            if (!File.Exists(skyrimExePath))
            {
                return $"Couldn't find Skyrim at: {skyrimExePath}";
            }

            string installedVersion = GetInstalledVersion(skyrimExePath);

            // The exe existed a moment ago and doesn't now, or it carries no
            // version resource at all. Without this the player falls through to
            // the generic message and reads "your Skyrim version ()" — empty
            // brackets and no idea what to do next.
            if (string.IsNullOrEmpty(installedVersion))
            {
                return $"Couldn't read the version from Skyrim at: {skyrimExePath}";
            }

            if (NeedsDowngrade(skyrimExePath))
            {
                return $"Steam updated Skyrim to {installedVersion} and that update breaks " +
                       "every script-extender mod, Holdline included. Nothing is wrong with " +
                       "your game or your install — this happened to everyone at once.\n\n" +
                       $"Holdline needs Skyrim {TargetVersion}. The update added no PC " +
                       "gameplay content, so going back costs you nothing.\n\n" +
                       "Putting Skyrim back is a supported thing Steam can do, and the next " +
                       "Holdline update will do it for you. Until then, ask in Discord and " +
                       "we'll walk you through it.";
            }

            if (Array.IndexOf(SupportedVersions, installedVersion) == -1)
            {
                return $"This hold's SkyMP build doesn't support your Skyrim version ({installedVersion}). " +
                       $"Supported versions: {string.Join(", ", SupportedVersions)}";
            }

            return null; // version OK
        }

        /// <summary>
        /// Locates skse64_loader.exe in the Skyrim root folder (alongside
        /// Skyrim.exe). Returns the full path, or null if SKSE isn't
        /// installed.
        /// </summary>
        public static string? FindSkseLoader(string skyrimExePath)
        {
            var skyrimRoot = Path.GetDirectoryName(skyrimExePath);
            if (string.IsNullOrEmpty(skyrimRoot))
            {
                return null;
            }

            var loaderPath = Path.Combine(skyrimRoot, SkseLoaderName);
            return File.Exists(loaderPath) ? loaderPath : null;
        }

        /// <summary>
        /// Backs up any vanilla files we're about to overwrite, but only the
        /// first time (won't clobber an existing backup with an already-
        /// modified copy on a later run).
        /// </summary>
        private static void BackupVanillaFilesIfNeeded(string skyrimDataDir)
        {
            foreach (var relPath in VanillaFilesToBackup)
            {
                var liveFile = Path.Combine(skyrimDataDir, relPath);
                var backupFile = liveFile + ".holdline-backup";

                if (File.Exists(liveFile) && !File.Exists(backupFile))
                {
                    File.Copy(liveFile, backupFile);
                }
            }
        }

        /// <summary>
        /// Restores the original vanilla files, if a backup exists.
        /// Returns the NUMBER of files actually restored, so the caller can
        /// tell the player the truth. Returning void here was a real bug:
        /// backups are only created by CopyClientData, so on an install
        /// where the client files were copied in by hand there are no
        /// .holdline-backup files at all, and a void method let the UI
        /// report success after doing nothing.
        /// </summary>
        public static int RestoreVanillaFiles(string skyrimDataDir)
        {
            int restored = 0;

            foreach (var relPath in VanillaFilesToBackup)
            {
                var liveFile = Path.Combine(skyrimDataDir, relPath);
                var backupFile = liveFile + ".holdline-backup";

                if (File.Exists(backupFile))
                {
                    File.Copy(backupFile, liveFile, overwrite: true);
                    restored++;
                }
            }

            return restored;
        }

        /// <summary>
        /// Writes the target hold's connection details into
        /// skymp5-client-settings.txt, preserving any other fields already
        /// in the file.
        ///
        /// masterUrl and masterKey are optional only so existing callers
        /// keep compiling. In practice both should be supplied — without
        /// them the client cannot fetch the hold's mod manifest.
        /// </summary>
        /// <param name="settingsFilePath">
        /// Must be the DESTINATION path inside the player's real Skyrim Data
        /// folder, not the dist source copy. Call this AFTER CopyClientData
        /// so it overwrites the freshly-copied file.
        /// </param>
        /// <param name="profileId">
        /// This install's unique player id, from PlayerIdentity. Pass 0 to
        /// leave whatever is already in the file alone. Anything >= 1 is
        /// stamped into gameData.profileId, overwriting the shipped default —
        /// which must happen on EVERY join, because a client-files update
        /// copies the dist settings file (profileId 1) over the top and would
        /// otherwise drop the player back onto the shared default character.
        /// </param>
        public static void WriteServerSettings(
            string settingsFilePath,
            string ip,
            int port,
            string? masterUrl = null,
            string? masterKey = null,
            int profileId = 0)
        {
            JsonObject root;

            if (File.Exists(settingsFilePath))
            {
                // JsonNode.Parse THROWS on malformed input -- it does not
                // return null -- so the "?? new JsonObject()" that used to sit
                // on the line below could never run. One stray byte in this
                // file (a UTF-16 marker, a truncated write, a half-finished
                // hand edit) took down the whole join with "Invalid UTF-8 by
                // index zero" and no way forward.
                //
                // There is nothing in this file we cannot rewrite from scratch:
                // ip, port and profile id all arrive as arguments. So a file we
                // cannot read is simply replaced rather than mourned.
                try
                {
                    var existing = File.ReadAllText(settingsFilePath);
                    root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
                }
                catch
                {
                    root = new JsonObject();
                }
            }
            else
            {
                root = new JsonObject();
            }

            root["server-ip"] = ip;
            root["server-port"] = port;

            // Holdline servers run in offline mode on a direct IP, so they are
            // not registered with the public SkyMP master directory. The client
            // still asks it for connection details on every join, gets a 404,
            // burns a five-second timeout, and then falls back to the ip/port
            // written directly above -- the values it already had.
            //
            // Conditional on purpose. If a hold ever DOES have a master server,
            // that lookup is the thing that finds it, and hard-coding "ignore"
            // would quietly break it. So: skip the request only when there is
            // nothing on the other end of it.
            root["server-info-ignore"] = masterUrl == null;

            if (masterUrl != null)
            {
                // A trailing slash produces a double slash in the request
                // path, because the client appends "/api/servers/..."
                // itself. Strip it rather than trusting the caller.
                root["master"] = masterUrl.TrimEnd('/');
            }

            if (masterKey != null)
            {
                root["server-master-key"] = masterKey;
            }

            if (profileId >= 1)
            {
                // gameData is a nested object: { "gameData": { "profileId": 1 } }.
                // Replace only the id inside it, so any other field the client
                // keeps in there survives.
                var gameData = root["gameData"] as JsonObject;

                if (gameData == null)
                {
                    gameData = new JsonObject();
                    root["gameData"] = gameData;
                }

                gameData["profileId"] = profileId;
            }

            var dir = Path.GetDirectoryName(settingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };

            // Encoding stated outright rather than left to the default. The
            // platform reads this file as raw bytes and hands them to the
            // JavaScript engine, which rejects anything that is not clean
            // UTF-8 -- a byte-order mark at the front is enough to kill the
            // client before it connects. "false" here means no mark.
            File.WriteAllText(
                settingsFilePath,
                root.ToJsonString(options),
                new UTF8Encoding(false));
        }

        /// <summary>
        /// Copies the hold's client/Data folder (from the dist build) into
        /// the player's real Skyrim Data folder. Backs up known vanilla
        /// files first. Uses Path.GetRelativePath rather than string
        /// replacement, since blind .Replace() on paths breaks on case
        /// differences, trailing slashes, or repeated substrings.
        ///
        /// NOTE: if Skyrim is installed under Program Files, this needs to
        /// run elevated (admin) or it will fail with access-denied —
        /// Program Files isn't writable by a normal user process.
        /// </summary>
        public static void CopyClientData(string sourceClientDataDir, string skyrimDataDir)
        {
            BackupVanillaFilesIfNeeded(skyrimDataDir);

            foreach (var dirPath in Directory.GetDirectories(sourceClientDataDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceClientDataDir, dirPath);
                Directory.CreateDirectory(Path.Combine(skyrimDataDir, relative));
            }

            foreach (var filePath in Directory.GetFiles(sourceClientDataDir, "*.*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceClientDataDir, filePath);
                var destPath = Path.Combine(skyrimDataDir, relative);
                CopyWithRetry(filePath, destPath);
            }
        }

        //  A file can stay locked for a moment even after Skyrim is CLOSED - the
        //  OS, antivirus, or a lingering skse64_loader can hold a handle briefly
        //  (MpClientPlugin.dll is the one that bites). Retry a few times with a
        //  short wait so a transient lock never fails the whole install.
        private static void CopyWithRetry(string from, string to)
        {
            Exception? last = null;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try { File.Copy(from, to, overwrite: true); return; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(400);
                }
            }
            throw last ?? new IOException("Could not copy " + Path.GetFileName(to));
        }

        /// <summary>
        /// Full connect flow for a hold: version check -> locate SKSE ->
        /// (optionally) copy client data -> write settings -> launch via
        /// SKSE. Returns null on success, or an error message to show the
        /// player.
        ///
        /// Pass null for sourceClientDataDir to skip the copy when the
        /// player's Data folder already contains the client files.
        /// Vortex mod-collection install is not wired in yet.
        /// </summary>
        public static string? ConnectToHold(
            string skyrimExePath,
            string skyrimDataDir,
            string? sourceClientDataDir,
            string ip,
            int port,
            string? masterUrl = null,
            string? masterKey = null)
        {
            var versionError = CheckGameVersion(skyrimExePath);
            if (versionError != null)
            {
                return versionError;
            }

            // Check for SKSE BEFORE copying anything. If it's missing there's
            // no point modifying the player's game folder — bail out clean.
            var skseLoaderPath = FindSkseLoader(skyrimExePath);
            if (skseLoaderPath == null)
            {
                return "SKSE (Skyrim Script Extender) isn't installed. Holdline needs it to run — " +
                       $"it should be at:\n{Path.Combine(Path.GetDirectoryName(skyrimExePath) ?? "", SkseLoaderName)}\n\n" +
                       "Download the Special Edition build from skse.silverlock.org and extract it into " +
                       "your Skyrim folder, then try again.";
            }

            if (!string.IsNullOrEmpty(sourceClientDataDir))
            {
                if (!Directory.Exists(sourceClientDataDir))
                {
                    return $"This hold's client files are missing at:\n{sourceClientDataDir}";
                }

                CopyClientData(sourceClientDataDir, skyrimDataDir);
            }

            var settingsFilePath = Path.Combine(skyrimDataDir, "Platform", "Plugins", "skymp5-client-settings.txt");

            // Claim this install's own player id before writing. In offline
            // mode the server has no accounts — profileId alone decides which
            // character you load — so two installs sharing the shipped
            // default of 1 means the second player to join spawns as the
            // first one's character.
            var profileId = PlayerIdentity.GetOrCreateProfileId(settingsFilePath);

            WriteServerSettings(settingsFilePath, ip, port, masterUrl, masterKey, profileId);

            // Launch through SKSE, not Skyrim.exe. The loader injects SKSE
            // and then starts the game itself; WorkingDirectory must be the
            // Skyrim root or the loader won't find the game.
            Process.Start(new ProcessStartInfo
            {
                FileName = skseLoaderPath,
                WorkingDirectory = Path.GetDirectoryName(skseLoaderPath),
                UseShellExecute = true,
            });

            return null; // success
        }

        /// <summary>
        /// Watches a launch that has already been started and reports, in plain
        /// language, if Skyrim never appears or appears and then dies.
        ///
        /// WHY THIS EXISTS: ConnectToHold returning null only proves the SKSE
        /// LOADER was started. skse64_loader.exe injects and then exits on
        /// purpose, so the loader is useless as a health signal - watching it
        /// would report "closed" on every successful launch. We watch the game
        /// process by name instead.
        ///
        /// Returns null when everything looks fine. Never throws.
        /// </summary>
        public static async Task<string?> WatchLaunchAsync(
            string skyrimExePath,
            IProgress<string>? progress = null)
        {
            string gameName;
            try
            {
                gameName = Path.GetFileNameWithoutExtension(skyrimExePath);
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(gameName)) return null;

            const int PollMs = 500;
            const int AppearTimeoutMs = 45000;
            const int StayAliveMs = 15000;

            // Phase 1 - wait for the game window process to show up.
            int waited = 0;
            Process? game = null;
            while (waited < AppearTimeoutMs)
            {
                game = FindProcessByName(gameName);
                if (game != null) break;
                await Task.Delay(PollMs);
                waited += PollMs;
                if (waited % 5000 == 0)
                {
                    progress?.Report("Waiting for Skyrim to open (" + (waited / 1000) + "s)...");
                }
            }

            if (game == null)
            {
                return "SKSE started, but Skyrim never opened.\n\n"
                     + "That usually means an SKSE plugin refused to load, or a "
                     + "mod is fighting the game before it reaches the menu.";
            }

            progress?.Report("Skyrim is opening...");

            // Phase 2 - a crash on startup normally happens within seconds.
            int watched = 0;
            while (watched < StayAliveMs)
            {
                await Task.Delay(PollMs);
                watched += PollMs;
                try
                {
                    if (game.HasExited)
                    {
                        return "Skyrim opened and then closed straight away.\n\n"
                             + "That is a startup crash. The cause is almost always "
                             + "an SKSE plugin built for a different Skyrim version, "
                             + "or a missing file the game expects.";
                    }
                }
                catch
                {
                    // Lost the handle (permissions, or it was replaced). Do not
                    // cry wolf - an unreadable process is not a proven failure.
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// First running process with this name, or null. Never throws.
        /// </summary>
        private static Process? FindProcessByName(string name)
        {
            try
            {
                var found = Process.GetProcessesByName(name);
                if (found.Length == 0) return null;
                for (int i = 1; i < found.Length; i++)
                {
                    try { found[i].Dispose(); } catch { }
                }
                return found[0];
            }
            catch
            {
                return null;
            }
        }
    }
}
