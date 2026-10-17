// ClientFiles.cs
// Added 2026-07-29, after the first outside tester got past the Skyrim-path
// bug and hit the next two walls in a row:
//
//   1. SKSE wasn't installed on his machine.
//   2. Even with SKSE, the SkyMP client files weren't there either — so the
//      game would have booted to the plain vanilla main menu, connected to
//      nothing.
//
// Ben had both of these on his own machine because he copied them in by hand
// on the 27th and forgot they were ever steps. Every other machine on Earth
// has neither. This file closes that gap.
//
// Two halves:
//
//   ClientFiles  — downloads the SkyMP client files (our own build, ours to
//                  ship) from GitHub Releases and installs them into the
//                  player's Skyrim Data folder, once.
//
//   SkseInstall  — we are NOT allowed to redistribute SKSE (its published
//                  permissions forbid re-uploading it and forbid its use in
//                  anything sold, and Holdline takes Patreon money). So we
//                  never host it. The player downloads it themselves from
//                  silverlock.org; all we do is put the files they already
//                  have into the right folder, which is the part that
//                  actually confuses people.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace HoldlineLauncher
{
    public static class ClientFiles
    {
        // Where the client-file zip lives. This must be a PUBLIC GitHub
        // release asset, or the download 404s for everyone but Ben.
        //
        // Bump BOTH of these together when the client files change: the
        // version string is what tells an existing install it's out of date.
        public const string ClientFilesUrl =
            "https://github.com/bencordero117/holdline-launcher/releases/download/client-2026-09-04c/Data.zip";

        // The browser, as its own download. Proven necessary 18 Aug, on
        // tester Gio's machine, by arithmetic: the v7 Data.zip is 155.8 MB
        // *total*, while the NirnLab browser runtime alone compresses to
        // 160.1 MB — so the bundle cannot contain the browser, and a fresh
        // machine that installs it has no browser at all. SkyrimPlatform
        // then falls back to the legacy Tilted/CEF engine, which is exactly
        // the engine that crashes machines like Gio's on every launch.
        //
        // This zip carries the three files that turned Gio's crashing
        // machine into a working one, watched land one at a time:
        //   NirnLabUIPlatform\            (237 files, the CEF runtime)
        //   SKSE\Plugins\NirnLabUIPlugin.dll
        //   SKSE\Plugins\SkyrimPlatform.ini   ([Browser] BackendName=nirnlab)
        // All three extract into Skyrim\Data.
        public const string BrowserZipUrl =
            "https://github.com/bencordero117/holdline-launcher/releases/download/client-2026-08-17/NirnLab-browser.zip";

        // v2 added the Holdline overlay (Data\Platform\UI) and SSE Engine Fixes.
        //
        // v3 carries the patched skymp5-client.js. Without it the client drops
        // itself into single-player about eight seconds after joining: it
        // switches whenever a save loads that it did not initiate, and on a
        // slow load its own load beats the flag that says "this was me". The
        // player then keeps playing locally while the server holds their last
        // known position, so nothing moves, doors and items do not respond,
        // and progress never saves.
        //
        // Bumping this is what makes machines that already installed v1 or v2 —
        // including Ben's and Bezni's — fetch the new files instead of
        // deciding they are already up to date.
        // v4 carries the werewolf work: Beast Form cast wired to the actual
        // transform, and the race-menu suppression from the first attempt
        // reverted so Skyrim's own character creation still opens for new
        // players.
        //
        // ORDER MATTERS. ClientBundleSha256 below is the fingerprint of the
        // v4 bundle. If this version and that hash ever disagree — say the
        // hash is updated but the URL still points at v3 — every launcher
        // downloads the old bundle, fails the fingerprint check, and
        // reinstalls 180 MB on every single join, forever. Bump the URL, the
        // version, and the hash together, and publish the release BEFORE
        // shipping a launcher that points at it.
        // v5 carries the smithing perk grant: the client walks the player's
        // perk list on connect and re-applies the smithing tree, because perks
        // live on the character inside the engine and the server has no hands
        // in there. Without this, everyone but Ben — whose machine had the
        // patched bundle dropped in by hand — smiths at vanilla-novice level.
        //
        // v6 (2026-08-06) REMOVES the patched skymp5-client.js entirely. The
        // patch that fixed the eight-second single-player drop turned out to
        // break joining outright after the 31st: the client died unpacking
        // server messages ("invalid UTF-8 byte") and never left the main menu.
        // A full night of testing proved the server, world, and character were
        // all healthy — restoring the stock 7/27 bundle fixed the join on the
        // spot. v6 ships that stock bundle, byte-identical to SkyMP's own
        // dist.zip (1,705,744 bytes). The single-player-drop fix must be
        // rebuilt cleanly on top of the stock bundle before it ships again.
        // v6 also ships what v5 forgot: Data\SKSE\Plugins (MpClientPlugin.dll
        // + SkyrimPlatform.dll + versionlib bins) and an empty
        // Data\Platform\PluginsDev — SkyrimPlatform silently kills its JS
        // engine when that folder is missing. The zip is shaped Data\ + Root\
        // so nothing but Data's contents lands in Data. Asset name is plain
        // "Data.zip"; the URL above must match it exactly or every launcher
        // 404s.
        // v7 (2026-08-17) — build stamp 17aug-host-death. NPC death/hosting
        // work. Published under tag client-2026-08-17.
        //
        // v9 (2026-09-03) — build stamp v218-three-forms. The cast hook now
        // maps three spells to three races instead of one:
        //   werewolfchange              0x92c48   -> 0xcdd84    werewolf
        //   dlc1vampirechange           0x200283b -> 0x200283a  vampire lord
        //   werewolfchangeringofhircine 0xf8306   -> 0x401e17b  werebear
        // Every id was read out of a live scan of Ben's game, not guessed.
        // Bethesda never shipped a player werebear change spell, so the Ring
        // of Hircine power stands in as its trigger - nothing else uses it.
        // Howl of Terror and the werewolf stat block stay werewolf-only, and a
        // beast race can never be recorded as the race you revert to, so
        // chaining forms cannot strand a player in animal shape.
        // Published under tag client-2026-09-03.
        //
        // v10 (2026-09-04) — build stamp v219-three-forms-menu. The admin
        // flag the menu raises now carries WHICH form to become instead of
        // just "yes". true still means werewolf, so nothing that set it
        // before changes. Without this the vampire lord and werebear buttons
        // had to go through the server appearance path, which only repaints
        // the human skeleton - wrong model, no powers. Published under tag
        // client-2026-09-04.
        //
        // v11 (2026-09-05) — build stamp v220-vampire-gear. Two fixes, both
        // the same root cause as the werewolf howl: vanilla dresses a Vampire
        // Lord and fills its hands from a Papyrus script, and scripts do not
        // run here. So the model arrived naked and the favourites menu had
        // nothing in it to click. The armor, the cape and both hand spells are
        // now put on at transform. Each form also gets its own health and
        // stamina block instead of only the werewolf having one, which left a
        // bear body running human numbers. Werewolf numbers are unchanged.
        // Published under tag client-2026-09-05.
        //
        // v12 (2026-09-06) — build stamp v221-vl-stance. A Vampire Lord has
        // two stances: hovering, where it casts, and standing, where it
        // fights with claws. Vanilla swaps the hands over from a Papyrus
        // script on the sneak key. Without it the spells stayed in both hands
        // on the ground and Skyrim will not start a clawed attack while a hand
        // holds a spell - so the claws did nothing while the powers sat there
        // visibly equipped. The hands now empty on the way down and refill on
        // the way up. Werewolf and werebear have no second stance and are not
        // touched. Published under tag client-2026-09-06.
        //
        // v13 (2026-09-07) — build stamp v222-vl-powers. The full Vampire
        // Lord power set: bats, mist form, vampiric grip, detect life,
        // seduction, night cloak and the gargoyle. They sit in the shout slot
        // rather than a hand, so they fire with the shout key and work in both
        // stances. Bats is equipped as the active one. Needs the matching
        // gamemode.js, which grants the same list server-side - a spell the
        // server has not seen the player learn is stripped out of the equip.
        // Published under tag client-2026-09-07.
        //
        // v14 (2026-09-08) - build stamp v223-vl-perks. The Vampire Lord
        // perk tree, all eleven perks, granted on transform. The powers in
        // v13 fired their visuals and did nothing else because in vanilla the
        // whole tree is perk-gated: the spell is the trigger, the perk is
        // what makes it act. Ids read out of a PERK scan of the live game.
        // Perks are local state, so no matching gamemode.js change is needed
        // for this one. Published under tag client-2026-09-08.
        //
        // v15 (2026-09-09) - build stamp v224-vl-gargoyle. The gargoyle summon
        // was sitting in the voice slot with the lesser powers. Its editor id
        // is dlc1conjuregargoylelefthand - a hand spell - so it could never
        // have fired from there. Moved to the left hand; raise dead came out
        // to make room and is still granted by the server, just not in hand.
        // Published under tag client-2026-09-09.
        //
        // v16 (2026-09-10) - build stamp v225-vl-summon. The gargoyle was
        // spawning and then being deleted: worldCleanerService picks a random
        // actor near the player every update tick and deletes anything without
        // server identity or wc protection. Summon bases are now skipped.
        // Published under tag client-2026-09-10.
        //
        // v17 (2026-09-11) - build stamp v226-vl-stance2. The grounded stance
        // watched isSneaking() only, which never went true for the Vampire
        // Lord, so the hand spells never came out and claws stayed unusable on
        // the ground. It now also reads the IsSneaking animation variable, the
        // same pair the bundle uses everywhere else, and prints both values.
        // Published under tag client-2026-09-11.
        //
        // v18 (2026-09-12) - build stamp v227-vl-powers2. Bats and mist form
        // now run from engine calls instead of waiting on Papyrus: bats fades
        // the player out and glides him forward, mist form fades him down and
        // regenerates. Both end on the poll tick. Also carries the stance fix
        // from v226. Published under tag client-2026-09-12.
        //
        // v19 (2026-09-04b) - build stamp v228-vl-live. Stance and the VL
        // powers now read the race the game actually has instead of a
        // client memo that is zero on every launch, so they work after a
        // relaunch. VL hand spells are cleared on revert so the werewolf no
        // longer inherits them. Holdline lines now also reach
        // skyrim-platform.log. Tags are real dates plus a letter from here.
        // Published under tag client-2026-09-04b.
        //
        // v20 (2026-09-04c) - build stamp v229-vl-keys. The log proved the
        // engine never reports the Vampire Lord ground stance and never
        // raises a cast event for the bats power, so stance now follows the
        // sneak key and bats/mist fire from the shout key by reading the
        // voice slot. Published under tag client-2026-09-04c.
        public const string ClientFilesVersion = "2026-09-04c";

        // A long timeout on purpose. The client files are around 180 MB and
        // the default 100 seconds would fail every download on a slow line —
        // which would look like a broken launcher, not a slow connection.
        private static readonly HttpClient http =
            new() { Timeout = TimeSpan.FromMinutes(30) };

        /// <summary>
        /// Stamp file recording which client-file version this Skyrim install
        /// has. Kept inside the Skyrim folder rather than in AppData, because
        /// the question "are these files installed?" is a property of the
        /// game folder, not of the user. Point the launcher at a different
        /// Skyrim install and it correctly decides to install again.
        /// </summary>
        private static string MarkerPath(string skyrimDataDir) =>
            Path.Combine(skyrimDataDir, "Platform", "holdline-client-version.txt");

        /// <summary>
        /// SHA-256 of the skymp5-client.js we ship in the current release.
        ///
        /// This exists because Holdline is not the only SkyMP server in the
        /// world, and the others install to exactly the same place we do:
        /// Data\Platform\Plugins. Play on somebody else's SkyMP server and
        /// their launcher drops THEIR client bundle on top of ours.
        ///
        /// A version stamp alone cannot see that. The stamp would still read
        /// "4", so we'd decide everything was fine, install nothing, and the
        /// player would run another server's client against our server — with
        /// our werewolf patch gone and no error anywhere to explain it.
        ///
        /// So we fingerprint the file itself. If the bundle sitting in the
        /// player's Skyrim folder is not byte-for-byte the one we shipped, it
        /// is not ours, and we reinstall — automatically, on the next Join,
        /// with no cleanup asked of the player. Holdline stays one click even
        /// after they have been playing somewhere else.
        ///
        /// MUST be regenerated whenever the client bundle changes, in the
        /// same edit that bumps ClientFilesVersion:
        ///     certutil -hashfile skymp5-client.js SHA256
        /// A stale hash here means every launcher reinstalls 180 MB on every
        /// single join, forever.
        /// </summary>
        private const string ClientBundleSha256 =
            "035CAC9300C9141E78BA08379CF3396D62EC5C25FC3CF39C7436A34B3E81DA9E";

        /// <summary>
        /// Every file that carries Holdline's identity, with the SHA-256 of
        /// the copy we ship. The js bundle is NOT the only file another SkyMP
        /// launcher overwrites: on 8/6 Keizaal's launcher swapped BOTH SKSE
        /// DLLs (MpClientPlugin.dll and SkyrimPlatform.dll) and left our js
        /// alone — so the old js-only fingerprint said "healthy" while the
        /// game dialed with a foreign network plugin and the server heard
        /// nothing. Cost Ben a full night. Existence checks cannot see this;
        /// only fingerprints can.
        ///
        /// MUST be regenerated together whenever the release bundle changes,
        /// in the same edit that bumps ClientFilesVersion:
        ///     certutil -hashfile FILE SHA256
        /// </summary>
        private static readonly (string[] RelPath, string Sha256)[] IdentityFiles =
        {
            (new[] { "Platform", "Plugins", "skymp5-client.js" },
             ClientBundleSha256),
            (new[] { "SKSE", "Plugins", "MpClientPlugin.dll" },
             "391ccd3ab48b527eb35c95cc104c50a46412bd2af9946e9427fce7ba110177a9"),
            (new[] { "SKSE", "Plugins", "SkyrimPlatform.dll" },
             "64be0221a95ecda5ddc9dfa98cbfc9af35cfc7cca35af96230a96568ed2c24cd"),
        };

        private static string ClientBundlePath(string skyrimDataDir) =>
            Path.Combine(skyrimDataDir, "Platform", "Plugins", "skymp5-client.js");

        /// <summary>
        /// True when the client bundle in this Skyrim folder is the exact one
        /// we ship. False if it is missing, unreadable, or somebody else's.
        /// </summary>
        private static bool BundleIsOurs(string skyrimDataDir)
        {
            // ALL identity files must be byte-for-byte ours — the js bundle
            // AND both SKSE DLLs. One foreign file is enough to break a join
            // with no error anywhere, so one foreign file is enough to
            // trigger a clean reinstall.
            foreach (var item in IdentityFiles)
            {
                try
                {
                    var parts = new string[item.RelPath.Length + 1];
                    parts[0] = skyrimDataDir;
                    item.RelPath.CopyTo(parts, 1);
                    var path = Path.Combine(parts);

                    if (!File.Exists(path))
                    {
                        return false;
                    }

                    using var stream = File.OpenRead(path);
                    using var sha = System.Security.Cryptography.SHA256.Create();

                    var hash = Convert.ToHexString(sha.ComputeHash(stream));

                    if (!string.Equals(hash, item.Sha256,
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                catch
                {
                    // Can't read it, so can't vouch for it. Reinstalling costs
                    // a download; trusting a file we couldn't check costs a
                    // player a broken session they can't diagnose.
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// True if this Skyrim install already has current client files.
        /// Checks a real client file as well as our stamp, so that deleting
        /// the Data folder and keeping a stale stamp doesn't fool us.
        /// </summary>
        public static bool AreInstalled(string skyrimDataDir)
        {
            try
            {
                // The multiplayer layer needs BOTH SKSE plugins, not one. This
                // used to check only MpClientPlugin.dll, and that single
                // missing check cost tester Gio an entire night: his
                // MpClientPlugin.dll was in place but SkyrimPlatform.dll was
                // not, so this returned true, the install was declared healthy,
                // nothing was redelivered, and the game crashed on every launch
                // because SkyrimPlatform — the half that runs our whole client
                // bundle — simply was not there.
                //
                // The list lives in one place, shared with the post-install
                // verify, so the two can never disagree about what a complete
                // install means.
                if (MissingRequiredFiles(skyrimDataDir).Count > 0)
                {
                    return false;
                }

                // Checked BEFORE the stamp, deliberately. Another SkyMP
                // server's launcher overwrites the bundle without touching
                // our stamp file, so the stamp is the one thing that cannot
                // be trusted to notice this.
                if (!BundleIsOurs(skyrimDataDir))
                {
                    return false;
                }

                var marker = MarkerPath(skyrimDataDir);

                if (!File.Exists(marker))
                {
                    // Plugin present, bundle verified as ours, but no stamp:
                    // these files were put here by hand, which is exactly the
                    // state Ben's own machine has been in since 27 July.
                    // Adopt them and write the stamp rather than downloading
                    // 180 MB on top of files that are already working.
                    TryWriteMarker(skyrimDataDir);
                    return true;
                }

                return File.ReadAllText(marker).Trim() == ClientFilesVersion;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The files a working Holdline install cannot do without, and which
        /// of them are not on disk right now. Returns friendly names, because
        /// the result is shown to a player.
        ///
        /// SkyrimPlatform.dll is the reason this exists. It is the half that
        /// loads and runs our whole client bundle, and until now nothing in
        /// the launcher ever checked whether it was there. Its Platform folder
        /// is listed too: SkyrimPlatform without Data\Platform loads, then
        /// dies looking for the files it needs, which reads to a player as a
        /// plain crash with no cause.
        /// </summary>
        private static List<string> MissingRequiredFiles(string skyrimDataDir)
        {
            var missing = new List<string>();

            try
            {
                var pluginDir = Path.Combine(skyrimDataDir, "SKSE", "Plugins");

                // Field is FilePath, not Path: naming it Path next to calls to
                // Path.Combine is a trap for whoever reads this next.
                var required = new (string FilePath, string Name)[]
                {
                    (Path.Combine(pluginDir, "MpClientPlugin.dll"), "the multiplayer plugin"),
                    (Path.Combine(pluginDir, "SkyrimPlatform.dll"), "the scripting plugin"),

                    // The browser. Checked here because until 8/7 nothing
                    // checked it at all: an install could be missing the whole
                    // browser runtime and this method still reported healthy,
                    // and the game then died on load with no explanation.
                    //
                    // Two files, on purpose. The DLL is the SKSE plugin that
                    // serves the nirnlab backend Repair selects. CEFSubprocess
                    // is the separate process that backend spawns, and it lives
                    // in a DIFFERENT folder (Data\NirnLabUIPlatform, beside
                    // Platform rather than inside it). Either one alone can be
                    // present while the other is missing.
                    (Path.Combine(pluginDir, "NirnLabUIPlugin.dll"), "the browser plugin"),
                    (Path.Combine(skyrimDataDir, "NirnLabUIPlatform", "CEFSubprocess.exe"),
                     "the browser runtime"),

                    // The switch that PICKS the working browser. Without this
                    // file SkyrimPlatform defaults to "auto", and auto in the
                    // current engine always means the legacy Tilted/CEF path —
                    // the one that crashes machines like Gio's on every
                    // launch. Proven from his own logs, 18 Aug: config "auto"
                    // = crash, config "nirnlab" = his log matched Ben's
                    // working machine line for line.
                    (Path.Combine(pluginDir, "SkyrimPlatform.ini"),
                     "the browser settings file")
                };

                foreach (var item in required)
                {
                    if (!File.Exists(item.FilePath))
                    {
                        missing.Add(item.Name);
                    }
                }

                // The bundle and the folder around it. SkyrimPlatform is
                // useless without both.
                var platformDir = Path.Combine(skyrimDataDir, "Platform");

                if (!Directory.Exists(platformDir))
                {
                    missing.Add("the Platform folder");
                }
                else if (!File.Exists(Path.Combine(platformDir, "Plugins", "skymp5-client.js")))
                {
                    missing.Add("the Holdline game code");
                }
            }
            catch
            {
                // If we cannot even look, do not claim files are missing —
                // that would send a player chasing a fault we did not see.
            }

            return missing;
        }

        private static void TryWriteMarker(string skyrimDataDir)
        {
            try
            {
                var marker = MarkerPath(skyrimDataDir);
                var dir = Path.GetDirectoryName(marker);

                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(marker, ClientFilesVersion);
            }
            catch
            {
                // Failing to write the stamp costs a re-check next time, not
                // a broken join. Never let it throw out of AreInstalled.
            }
        }

        /// <summary>
        /// Can we actually write here? Steam installs frequently land in
        /// C:\Program Files (x86)\, which a normal user process cannot write
        /// to. Finding that out by way of a half-finished copy and an
        /// access-denied exception is much worse than asking first.
        /// </summary>
        public static bool IsWritable(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, ".holdline-write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Makes sure Data\SKSE\Plugins\SkyrimPlatform.ini selects the
        /// NirnLab browser. Written by the launcher itself, every join,
        /// because no bundle has ever shipped this file: Ben's machine only
        /// works because his copy was set by hand months ago, and every
        /// fresh machine defaulted onto the crashing legacy engine.
        ///
        /// Patches surgically: an existing file keeps every other line it
        /// has. Only the BackendName value is corrected, or the [Browser]
        /// section appended if absent. Returns null on success, or a message
        /// for the player.
        /// </summary>
        public static string? EnsureBrowserConfig(string skyrimDataDir)
        {
            try
            {
                var iniPath = Path.Combine(
                    skyrimDataDir, "SKSE", "Plugins", "SkyrimPlatform.ini");

                var iniDir = Path.GetDirectoryName(iniPath);

                if (!string.IsNullOrEmpty(iniDir))
                {
                    Directory.CreateDirectory(iniDir);
                }

                if (!File.Exists(iniPath))
                {
                    File.WriteAllText(iniPath,
                        "[Browser]\r\nBackendName=nirnlab\r\n");
                    return null;
                }

                var lines = new List<string>(File.ReadAllLines(iniPath));
                var browserSection = -1;
                var backendLine = -1;
                var currentSection = "";

                for (var i = 0; i < lines.Count; i++)
                {
                    var trimmed = lines[i].Trim();

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.ToLowerInvariant();

                        if (currentSection == "[browser]")
                        {
                            browserSection = i;
                        }

                        continue;
                    }

                    if (currentSection == "[browser]" &&
                        trimmed.ToLowerInvariant().StartsWith("backendname") &&
                        trimmed.Contains('='))
                    {
                        backendLine = i;
                    }
                }

                if (backendLine >= 0)
                {
                    var value = lines[backendLine]
                        .Substring(lines[backendLine].IndexOf('=') + 1)
                        .Trim();

                    if (string.Equals(value, "nirnlab",
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    lines[backendLine] = "BackendName=nirnlab";
                }
                else if (browserSection >= 0)
                {
                    lines.Insert(browserSection + 1, "BackendName=nirnlab");
                }
                else
                {
                    lines.Add("[Browser]");
                    lines.Add("BackendName=nirnlab");
                }

                File.WriteAllLines(iniPath, lines);
                return null;
            }
            catch (Exception)
            {
                return "Holdline couldn't set up the browser settings file in your " +
                       "Skyrim folder. Close Holdline, right-click its icon, choose " +
                       "\"Run as administrator\", and join again.";
            }
        }

        /// <summary>
        /// Downloads and installs the NirnLab browser if it's missing.
        /// Separate from the main client bundle because the browser is
        /// bigger than the whole v7 bundle and lives in its own release
        /// asset. Returns null when the browser is present (or was just
        /// installed and verified), or a message for the player.
        /// </summary>
        public static async Task<string?> EnsureBrowserFilesAsync(
            string skyrimDataDir,
            IProgress<string> report)
        {
            var runtimeExe = Path.Combine(
                skyrimDataDir, "NirnLabUIPlatform", "CEFSubprocess.exe");
            var pluginDll = Path.Combine(
                skyrimDataDir, "SKSE", "Plugins", "NirnLabUIPlugin.dll");

            if (File.Exists(runtimeExe) && File.Exists(pluginDll))
            {
                return null;
            }

            if (!IsWritable(skyrimDataDir))
            {
                return "Holdline can't write into your Skyrim folder:\n" + skyrimDataDir +
                       "\n\nThis usually means Skyrim is installed under Program Files, " +
                       "which Windows protects. Close Holdline, right-click the Holdline " +
                       "icon, choose \"Run as administrator\", and join again.";
            }

            var work = Path.Combine(
                Path.GetTempPath(), "Holdline-browser-" + Guid.NewGuid().ToString("N"));
            var zipPath = Path.Combine(work, "browser.zip");
            var unzipDir = Path.Combine(work, "unzipped");

            try
            {
                Directory.CreateDirectory(work);

                var downloadError = await DownloadAsync(
                    BrowserZipUrl, zipPath, report, "browser files (about 160 MB)");

                if (downloadError != null)
                {
                    return downloadError;
                }

                report.Report("Unpacking browser files...");
                Directory.CreateDirectory(unzipDir);

                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, unzipDir));

                if (!File.Exists(Path.Combine(
                        unzipDir, "NirnLabUIPlatform", "CEFSubprocess.exe")))
                {
                    return "The downloaded browser files don't look right — the " +
                           "runtime isn't inside them. This is a problem on our " +
                           "end, not yours.";
                }

                report.Report("Installing browser files into Skyrim...");

                await Task.Run(() => CopyTreeInto(unzipDir, skyrimDataDir));

                // Per-file proof, not hope: every file that came out of the
                // zip must now exist in Skyrim's Data at the same size.
                var missing = await Task.Run(
                    () => VerifyMirroredFiles(unzipDir, skyrimDataDir));

                if (missing.Count > 0)
                {
                    return "Holdline installed the browser, but " +
                           DescribeMissing(missing) +
                           " didn't end up in your Skyrim folder.\n\n" +
                           "That's usually Windows or antivirus blocking the copy. " +
                           "Close Holdline, right-click its icon, choose " +
                           "\"Run as administrator\", and try again.";
                }

                report.Report("Browser files installed.");
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "Windows blocked Holdline from writing into your Skyrim folder. " +
                       "Right-click the Holdline icon, choose \"Run as administrator\", " +
                       "and join again.";
            }
            catch (Exception ex)
            {
                return "Installing the browser files failed:\n" + ex.Message;
            }
            finally
            {
                TryDeleteDirectory(work);
            }
        }

        /// <summary>
        /// Plain recursive copy of everything under sourceDir into destDir,
        /// overwriting. Same shape as SkympConnector.CopyClientData, local
        /// so the browser install doesn't reach across classes.
        /// </summary>
        private static void CopyTreeInto(string sourceDir, string destDir)
        {
            foreach (var dir in Directory.GetDirectories(
                         sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, dir);
                Directory.CreateDirectory(Path.Combine(destDir, relative));
            }

            foreach (var file in Directory.GetFiles(
                         sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var target = Path.Combine(destDir, relative);
                var targetDir = Path.GetDirectoryName(target);

                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(file, target, overwrite: true);
            }
        }

        /// <summary>
        /// The evidence half of every copy in this file. Walks every file
        /// under sourceDir and returns the relative path of each one that is
        /// not now sitting under destDir at the same size.
        ///
        /// Exists because of 18 Aug: tester Gio's launcher reported a
        /// completed download and his machine was still missing the files
        /// that mattered. A copy loop that doesn't look back cannot tell
        /// the difference between done and dropped.
        /// </summary>
        private static List<string> VerifyMirroredFiles(
            string sourceDir, string destDir)
        {
            var missing = new List<string>();

            foreach (var file in Directory.GetFiles(
                         sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var target = Path.Combine(destDir, relative);

                try
                {
                    if (!File.Exists(target) ||
                        new FileInfo(target).Length != new FileInfo(file).Length)
                    {
                        missing.Add(relative);
                    }
                }
                catch
                {
                    missing.Add(relative);
                }
            }

            return missing;
        }

        /// <summary>
        /// Turns a list of missing relative paths into something a player
        /// can read without scrolling. Full list up to eight, then a count.
        /// </summary>
        private static string DescribeMissing(List<string> missing)
        {
            if (missing.Count <= 8)
            {
                return string.Join(", ", missing);
            }

            return string.Join(", ", missing.GetRange(0, 8)) +
                   " and " + (missing.Count - 8) + " more files";
        }

        /// <summary>
        /// Downloads and installs the client files if they aren't already
        /// there. Returns null on success, or a message to show the player.
        /// Safe to call on every join: it returns immediately once installed.
        /// </summary>
        public static async Task<string?> EnsureInstalledAsync(
            string skyrimDataDir,
            IProgress<string> report)
        {
            // Visual C++ first, and deliberately BEFORE the "already
            // installed" shortcut below. Engine Fixes silently fails to load
            // without it, and a machine can lose the runtime long after the
            // client files were installed. The check is a registry read, so
            // on a machine that already has it this costs nothing.
            var vcError = await Dependencies.EnsureVcRedistAsync(report);
            if (vcError != null)
            {
                return vcError;
            }

            // The browser, before anything else — and before the "already
            // installed" shortcut, because these two fixes are what turn a
            // machine like Gio's from crashing to working, and neither is
            // carried by the v7 bundle. The config write is a few
            // milliseconds when already correct; the download only happens
            // when the browser is actually missing.
            var configError = EnsureBrowserConfig(skyrimDataDir);
            if (configError != null)
            {
                return configError;
            }

            var browserError = await EnsureBrowserFilesAsync(skyrimDataDir, report);
            if (browserError != null)
            {
                return browserError;
            }

            // The AvP Alien mod (10 Oct 2026). Checked on EVERY join and every
            // Fix my game, before the "already installed" shortcut, so a newer
            // Alien build published on the "alien" release reaches everyone
            // without a launcher rebuild. Never fails the join - see AlienFiles.cs.
            await AlienFiles.EnsureAsync(skyrimDataDir, report);

            if (AreInstalled(skyrimDataDir))
            {
                return null;
            }

            if (!IsWritable(skyrimDataDir))
            {
                return "Holdline can't write into your Skyrim folder:\n" + skyrimDataDir +
                       "\n\nThis usually means Skyrim is installed under Program Files, " +
                       "which Windows protects. Close Holdline, right-click the Holdline " +
                       "icon, choose \"Run as administrator\", and join again.";
            }

            var work = Path.Combine(Path.GetTempPath(), "Holdline-client-" + Guid.NewGuid().ToString("N"));
            var zipPath = Path.Combine(work, "client-files.zip");
            var unzipDir = Path.Combine(work, "unzipped");

            try
            {
                Directory.CreateDirectory(work);

                report.Report("Downloading multiplayer files (about 180 MB)...");

                var downloadError = await DownloadAsync(ClientFilesUrl, zipPath, report);
                if (downloadError != null)
                {
                    return downloadError;
                }

                report.Report("Unpacking multiplayer files...");
                Directory.CreateDirectory(unzipDir);

                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, unzipDir));

                var sourceDir = FindDataRoot(unzipDir);
                if (sourceDir == null)
                {
                    return "The downloaded multiplayer files don't look right — Holdline " +
                           "couldn't find the Platform folder inside them. This is a problem " +
                           "on our end, not yours.";
                }

                report.Report("Installing multiplayer files into Skyrim...");

                // CopyClientData backs up the vanilla script files it's about
                // to overwrite before it overwrites them. Off the UI thread:
                // this is roughly 180 MB of file copying and would otherwise
                // freeze the window solid.
                await Task.Run(() => SkympConnector.CopyClientData(sourceDir, skyrimDataDir));

                // Look back at what just landed. CopyClientData is a plain
                // mirror copy, so every file under sourceDir must now exist
                // in Data at the same size — anything else is a dropped file
                // the player would otherwise discover as an unexplained
                // crash.
                var dataMissing = await Task.Run(
                    () => VerifyMirroredFiles(sourceDir, skyrimDataDir));

                if (dataMissing.Count > 0)
                {
                    return "Holdline installed its files, but " +
                           DescribeMissing(dataMissing) +
                           " didn't end up in your Skyrim folder.\n\n" +
                           "That's usually Windows or antivirus blocking the copy. Close " +
                           "Holdline, right-click its icon, choose \"Run as administrator\", " +
                           "and try again.";
                }

                // A few files have to sit NEXT TO SkyrimSE.exe rather than in
                // Data — the SKSE preloader that Engine Fixes needs is loaded
                // by the game before Data means anything to it. The zip keeps
                // those in a top-level "Root" folder, beside Data.
                var rootSource = FindRootFolder(unzipDir, sourceDir);

                if (rootSource == null)
                {
                    return "The downloaded multiplayer files are missing their Root " +
                           "folder, so the engine files can't be installed. This is a " +
                           "problem on our end, not yours.";
                }

                var skyrimRoot = Path.GetDirectoryName(
                    skyrimDataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (string.IsNullOrEmpty(skyrimRoot))
                {
                    return "Holdline couldn't work out where your main Skyrim folder is, " +
                           "starting from:\n" + skyrimDataDir;
                }

                report.Report("Installing engine files...");
                await Task.Run(() => CopyRootFiles(rootSource, skyrimRoot));

                var rootMissing = await Task.Run(
                    () => VerifyMirroredFiles(rootSource, skyrimRoot));

                if (rootMissing.Count > 0)
                {
                    return "Holdline installed its engine files, but " +
                           DescribeMissing(rootMissing) +
                           " didn't end up in your Skyrim folder.\n\n" +
                           "That's usually Windows or antivirus blocking the copy. Close " +
                           "Holdline, right-click its icon, choose \"Run as administrator\", " +
                           "and try again.";
                }

                // Prove the copy actually landed BEFORE stamping success.
                //
                // Tester Gio's night was lost to exactly this gap: a required
                // file was not on disk, the launcher reported "installed"
                // anyway, and the stamp written below then told every later
                // check that all was well. A stamp is a claim; this is the
                // evidence. If a required file is missing right now, say so
                // plainly and write NO stamp, so the next run reinstalls
                // instead of trusting a lie.
                var missing = MissingRequiredFiles(skyrimDataDir);

                if (missing.Count > 0)
                {
                    return "Holdline installed its files, but " +
                           string.Join(" and ", missing) +
                           " didn't end up in your Skyrim folder.\n\n" +
                           "That's usually Windows or antivirus blocking the copy. Close " +
                           "Holdline, right-click its icon, choose \"Run as administrator\", " +
                           "and try again.";
                }

                TryWriteCredits(skyrimDataDir);
                TryWriteMarker(skyrimDataDir);

                report.Report("Multiplayer files installed.");
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "Windows blocked Holdline from writing into your Skyrim folder. " +
                       "Right-click the Holdline icon, choose \"Run as administrator\", " +
                       "and join again.";
            }
            catch (Exception ex)
            {
                return "Installing the multiplayer files failed:\n" + ex.Message;
            }
            finally
            {
                TryDeleteDirectory(work);
            }
        }

        /// <summary>
        /// Streams the download to disk rather than buffering it in memory,
        /// and reports percentage as it goes, because a silent five-minute
        /// pause reads as a hang.
        /// </summary>
        private static async Task<string?> DownloadAsync(
            string url,
            string destPath,
            IProgress<string> report,
            string label = "multiplayer files")
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    return "Couldn't download the " + label + " (server said " +
                           (int)response.StatusCode + "). This is a problem on our end, " +
                           "not yours — try again in a few minutes.";
                }

                var total = response.Content.Headers.ContentLength ?? -1L;

                using var source = await response.Content.ReadAsStreamAsync();
                using var dest = File.Create(destPath);

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
                            report.Report("Downloading " + label + "... " + percent + "%");
                        }
                    }
                    else
                    {
                        report.Report("Downloading " + label + "... " + (done / 1048576) + " MB");
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                return "Couldn't download the " + label + ":\n" + ex.Message +
                       "\n\nCheck your internet connection and try again.";
            }
        }

        /// <summary>
        /// Finds the folder to copy from. Accepts a zip made from the Data
        /// folder itself, a zip made from its contents, or one with a stray
        /// wrapper folder around either — because the zip gets made by hand
        /// and guessing wrong should not be a silent failure.
        /// </summary>
        /// <summary>
        /// Locates the zip's "Root" folder — the files that belong beside
        /// SkyrimSE.exe rather than in Data. Anything sitting INSIDE the Data
        /// payload is ignored on purpose: that has already been copied into
        /// Skyrim\Data by the step before this one, and copying it to the game
        /// root as well would put the same files in two places.
        /// </summary>
        private static string? FindRootFolder(string extractRoot, string dataDir)
        {
            var direct = Path.Combine(extractRoot, "Root");

            if (Directory.Exists(direct))
            {
                return direct;
            }

            // Compare with a trailing separator so a sibling folder that merely
            // starts with the same letters (Data vs DataFiles) isn't mistaken
            // for something inside the Data payload.
            var dataPrefix = dataDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            foreach (var dir in Directory.GetDirectories(extractRoot, "Root", SearchOption.AllDirectories))
            {
                if (dir.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return dir;
            }

            return null;
        }

        /// <summary>
        /// Copies the Root folder's contents into the Skyrim folder that holds
        /// SkyrimSE.exe. Currently that means the SKSE preloader
        /// (d3dx9_42.dll) that SSE Engine Fixes requires — the filename looks
        /// wrong and is not: it is a proxy DLL that loads early on purpose.
        /// </summary>
        private static void CopyRootFiles(string rootSource, string skyrimRoot)
        {
            foreach (var file in Directory.GetFiles(rootSource, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(rootSource, file);
                var target = Path.Combine(skyrimRoot, relative);
                var targetDir = Path.GetDirectoryName(target);

                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(file, target, overwrite: true);
            }
        }

        /// <summary>
        /// Writes the credit file for the third-party components Holdline
        /// ships. SSE Engine Fixes is GPL software: redistributing it is
        /// allowed, and crediting its authors is the condition. This is not
        /// optional politeness — it is the terms.
        /// </summary>
        private static void TryWriteCredits(string skyrimDataDir)
        {
            try
            {
                var path = Path.Combine(skyrimDataDir, "Platform", "Holdline-Credits.txt");
                var dir = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path,
                    "Holdline — third-party components\r\n" +
                    "=================================\r\n\r\n" +
                    "Includes SSE Engine Fixes by aers, Nukem and Ryan,\r\n" +
                    "distributed under the GNU General Public License.\r\n" +
                    "https://www.nexusmods.com/skyrimspecialedition/mods/17230\r\n\r\n" +
                    "Includes the Microsoft Visual C++ Redistributable,\r\n" +
                    "installed from Microsoft.\r\n\r\n" +
                    "Skyrim Script Extender (SKSE64) is NOT redistributed by\r\n" +
                    "Holdline. It is downloaded by you from skse.silverlock.org.\r\n\r\n" +
                    "Werewolf textures by IconicDeath, used with the author's\r\n" +
                    "written permission.\r\n\r\n" +
                    "Built on SkyMP (skyrim-multiplayer/skymp).\r\n\r\n" +
                    "Skyrim and all related assets are property of Bethesda\r\n" +
                    "Softworks. Holdline is not affiliated with or endorsed by\r\n" +
                    "Bethesda. You must own and supply your own copy of the\r\n" +
                    "game.\r\n");
            }
            catch
            {
                // Never let a credits file stop someone joining.
            }
        }

        private static string? FindDataRoot(string extractRoot)
        {
            if (Directory.Exists(Path.Combine(extractRoot, "Platform")))
            {
                return extractRoot;
            }

            foreach (var dir in Directory.GetDirectories(extractRoot, "*", SearchOption.AllDirectories))
            {
                if (Directory.Exists(Path.Combine(dir, "Platform")))
                {
                    return dir;
                }
            }

            return null;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Leftovers in Temp are Windows' problem, not the player's.
                // Never let cleanup turn a successful install into an error.
            }
        }
    }

    /// <summary>
    /// Helps the player install SKSE from a copy THEY downloaded. We do not
    /// host, bundle, or redistribute SKSE — its permissions forbid it. All
    /// this does is move files the player already has into the right folder,
    /// which is the step that actually trips people up, since most people
    /// have no idea where Steam put Skyrim. The launcher does.
    /// </summary>
    public static class SkseInstall
    {
        private const string SkseLoaderName = "skse64_loader.exe";

        public const string DownloadPage = "https://skse.silverlock.org/";

        /// <summary>
        /// Installs SKSE from a .zip the player downloaded, or from a folder
        /// they already extracted. Returns null on success, or a message.
        /// </summary>
        public static string? InstallFrom(string sourcePath, string skyrimRoot)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return "No SKSE download was chosen.";
            }

            if (!ClientFiles.IsWritable(skyrimRoot))
            {
                return "Holdline can't write into your Skyrim folder:\n" + skyrimRoot +
                       "\n\nRight-click the Holdline icon, choose \"Run as administrator\", " +
                       "and try again.";
            }

            var temp = Path.Combine(Path.GetTempPath(), "Holdline-skse-" + Guid.NewGuid().ToString("N"));

            try
            {
                string searchRoot;

                if (Directory.Exists(sourcePath))
                {
                    searchRoot = sourcePath;
                }
                else if (File.Exists(sourcePath))
                {
                    var ext = Path.GetExtension(sourcePath).ToLowerInvariant();

                    if (ext == ".7z" || ext == ".rar")
                    {
                        // Recent Windows 11 can unpack .7z from the right-click
                        // menu, older Windows cannot. Give the built-in route
                        // first and the fallback second, rather than leading
                        // with "go install some other program".
                        return "That file is still zipped up, so Holdline can't read it yet.\n\n" +
                               "Here's the fix:\n\n" +
                               "1.  Find the file you downloaded (check your Downloads folder).\n" +
                               "2.  Right-click it and choose \"Extract All\", then click Extract.\n" +
                               "3.  A new folder appears next to it.\n" +
                               "4.  Click Join again, choose YES, and pick that new folder.\n\n" +
                               "If \"Extract All\" isn't in the right-click menu, your Windows " +
                               "is a little older — installing the free program 7-Zip will add it.";
                    }

                    if (ext != ".zip")
                    {
                        return "That doesn't look like the SKSE download.\n\n" +
                               "Pick the file you downloaded from silverlock.org, or the " +
                               "folder you unzipped it into. It should have a file called " +
                               "skse64_loader.exe somewhere inside.";
                    }

                    Directory.CreateDirectory(temp);
                    ZipFile.ExtractToDirectory(sourcePath, temp);
                    searchRoot = temp;
                }
                else
                {
                    return "Holdline couldn't find:\n" + sourcePath;
                }

                var skseRoot = FindSkseRoot(searchRoot);

                if (skseRoot == null)
                {
                    return "Holdline looked inside what you picked and couldn't find " +
                           "the SKSE files.\n\n" +
                           "Two things worth checking:\n\n" +
                           "1.  Did you pick the right folder? Look for one containing a " +
                           "file called " + SkseLoaderName + ".\n" +
                           "2.  Did you download the Anniversary Edition build (2.2.6)? " +
                           "The \"SE build\" is a different one and won't work here.";
                }

                CopyTree(skseRoot, skyrimRoot);

                if (!File.Exists(Path.Combine(skyrimRoot, SkseLoaderName)))
                {
                    return "The SKSE files were copied but " + SkseLoaderName +
                           " still isn't in your Skyrim folder. Something's off — try " +
                           "extracting it into the folder by hand.";
                }

                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "Windows blocked Holdline from writing into your Skyrim folder. " +
                       "Right-click the Holdline icon, choose \"Run as administrator\", " +
                       "and try again.";
            }
            catch (Exception ex)
            {
                return "Installing SKSE failed:\n" + ex.Message;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(temp))
                    {
                        Directory.Delete(temp, recursive: true);
                    }
                }
                catch
                {
                    // Cleanup failure must never fail the install.
                }
            }
        }

        /// <summary>
        /// SKSE archives contain a versioned wrapper folder such as
        /// skse64_2_02_06\. Find whichever folder actually holds the loader
        /// instead of assuming the archive's shape.
        /// </summary>
        private static string? FindSkseRoot(string root)
        {
            if (File.Exists(Path.Combine(root, SkseLoaderName)))
            {
                return root;
            }

            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (File.Exists(Path.Combine(dir, SkseLoaderName)))
                {
                    return dir;
                }
            }

            return null;
        }

        private static void CopyTree(string sourceDir, string destDir)
        {
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, dir);
                Directory.CreateDirectory(Path.Combine(destDir, relative));
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var target = Path.Combine(destDir, relative);
                var targetDir = Path.GetDirectoryName(target);

                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(file, target, overwrite: true);
            }
        }
    }
}
