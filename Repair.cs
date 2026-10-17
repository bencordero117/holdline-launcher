// ============================================================
//  Holdline — Repair.cs
//
//  One button that checks an install and fixes what it safely can.
//
//  ------------------------------------------------------------
//  WHAT CHANGED IN THIS VERSION, AND WHY
//  ------------------------------------------------------------
//
//  Every previous version of this file was built on one sentence,
//  repeated in its own comments:
//
//      "the client bundle has never contained the two plugin
//       DLLs, so deleting them can only ever break the install"
//
//  That sentence was true, and it justified an enormous amount of
//  machinery: a backup folder in AppData, a restore-from-backup
//  path, and a drive-wide hunt that searched the Skyrim folder,
//  Downloads and the whole user profile for any file named
//  SkyrimPlatform.dll or MpClientPlugin.dll and copied the first
//  one it found into place.
//
//  That hunt is what broke a real tester. It cannot tell a good
//  DLL from a bad one. It cannot tell OUR SkyrimPlatform.dll from
//  another SkyMP server's, or from a three-year-old copy in an
//  unpacked archive in Downloads. On a machine whose files were
//  already wrong, "find a copy somewhere and put it back" simply
//  reinstalled the wrongness and then reported success.
//
//  The 2026-08-06 bundle contains those files. Verified by
//  reading the shipped zip, not by assuming:
//
//      Data/SKSE/Plugins/MpClientPlugin.dll     1,477,632 bytes
//      Data/SKSE/Plugins/SkyrimPlatform.dll       149,504 bytes
//      Data/Platform/Plugins/skymp5-client.js   1,705,744 bytes
//      Data/Platform/PluginsDev/                 (empty, present)
//      Root/d3dx9_42.dll                           88,576 bytes
//
//  So the premise is gone, and everything built on it goes with
//  it. This file no longer searches the player's disk for
//  Holdline's own files. It downloads them from the release —
//  the same known-good bundle the Join button installs — and
//  that download is the only source of a Holdline file that this
//  code will accept.
//
//  DELETED IN THIS REWRITE:
//      RestoreMissingPlugins   — scavenged before downloading
//      HuntForPluginDlls       — the drive-wide search itself
//      BackupOurClientFiles    — safety net for a fault that no
//      RestoreOurClientFiles     longer exists, and a way to put
//      BackupDir                 a stale DLL back over a fresh one
//
//  ------------------------------------------------------------
//  Two rules this file still obeys
//  ------------------------------------------------------------
//
//  1. It only ever deletes files Holdline itself installed AND
//     can re-download in full. The player's mods, saves, ESPs and
//     other people's SKSE plugins are never touched.
//
//  2. It reports what it could NOT fix as loudly as what it did.
//     A tester's night was lost to a silent failure; a repair
//     that quietly does nothing would repeat exactly that.
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;

namespace HoldlineLauncher
{
    /// <summary>
    /// The outcome of a repair run, in a form the settings window can show
    /// without knowing anything about what was checked.
    /// </summary>
    public sealed class RepairReport
    {
        /// <summary>Things that were wrong and are now fixed.</summary>
        public readonly List<string> Fixed = new();

        /// <summary>Things that are wrong and need the player to act.</summary>
        public readonly List<string> Blocking = new();

        /// <summary>Things checked and found healthy.</summary>
        public readonly List<string> Fine = new();

        public bool ReadyToPlay => Blocking.Count == 0;

        /// <summary>
        /// The whole thing as one block of text for a message box. Written
        /// for a player: no paths unless they need to act on them, no jargon.
        /// </summary>
        public string ToMessage()
        {
            var text = new StringBuilder();

            if (Fixed.Count > 0)
            {
                text.AppendLine("FIXED");

                foreach (var line in Fixed)
                {
                    text.AppendLine("   " + line);
                }

                text.AppendLine();
            }

            if (Blocking.Count > 0)
            {
                text.AppendLine("STILL NEEDS DOING");

                foreach (var line in Blocking)
                {
                    text.AppendLine("   " + line);
                }

                text.AppendLine();
            }

            if (Fixed.Count == 0 && Blocking.Count == 0)
            {
                text.AppendLine("Everything checked out. Nothing needed fixing.");
                text.AppendLine();
            }

            if (Fine.Count > 0)
            {
                text.AppendLine("CHECKED AND FINE");

                foreach (var line in Fine)
                {
                    text.AppendLine("   " + line);
                }

                text.AppendLine();
            }

            text.AppendLine(ReadyToPlay
                ? "You're good to go — close this and click Join."
                : "Sort the items above, then run Fix my game again.");

            return text.ToString().TrimEnd();
        }
    }

    public static class Repair
    {
        /// <summary>
        /// Checks an install top to bottom and fixes what it safely can.
        /// Never throws: a repair tool that crashes is a cruel joke.
        ///
        /// Async because it genuinely installs things — Visual C++, SKSE and
        /// Holdline's own game files — rather than telling the player to go
        /// and do it. Anything this code is allowed to do, it does.
        /// </summary>
        public static async Task<RepairReport> RunAsync(
            string? skyrimFolder,
            IProgress<string> progress)
        {
            var report = new RepairReport();

            try
            {
                // ---------- 0. Can this PC run the chat window at all? ----------
                //
                //  First, because it needs nothing: no Skyrim folder, no
                //  network, no files. And because if it fails, every step
                //  below is a waste of the player's time — a processor
                //  missing an instruction set cannot be repaired by a
                //  download.
                CheckProcessorSupport(report);

                // ---------- 1. Do we even know where Skyrim is? ----------
                if (!MainWindow.IsSkyrimFolder(skyrimFolder))
                {
                    var found = MainWindow.DetectSkyrimFolder();

                    if (found != null)
                    {
                        MainWindow.SetSkyrimFolder(found);
                        skyrimFolder = found;
                        report.Fixed.Add("Found Skyrim and pointed Holdline at it.");
                    }
                    else
                    {
                        report.Blocking.Add(
                            "Holdline can't find Skyrim. Use \"Change folder\" above and " +
                            "pick the folder containing SkyrimSE.exe.");

                        // Nothing below this can be checked without it.
                        return report;
                    }
                }

                else
                {
                    report.Fine.Add("Skyrim's folder is set correctly.");
                }

                if (MainWindow.RelaunchElevatedIfNeeded(skyrimFolder))
                {
                    report.Blocking.Add(
                        "Your Skyrim is under Program Files, which Windows write-protects. " +
                        "Holdline is restarting itself with administrator rights - press Fix my game again in the new window.");
                    return report;
                }

                var root = skyrimFolder!;
                var dataDir = Path.Combine(root, "Data");

                // ---------- 2. Can we write there at all? ----------
                // Checked early: the fixes below move and replace files, and
                // "permission denied" halfway through a repair is a mess.
                if (!ClientFiles.IsWritable(root))
                {
                    report.Blocking.Add(
                        "Windows won't let Holdline change files in your Skyrim folder. " +
                        "Close Holdline, right-click its icon, choose \"Run as " +
                        "administrator\", and run this again.");

                    return report;
                }

                // ---------- 3. The renamed Platform folder ----------
                RepairPlatformFolder(dataDir, report);

                // ---------- 4. Files in the wrong Plugins folder ----------
                RepairMisplacedSksePlugins(dataDir, report);

                // ---------- 5. SKSE: install it, don't lecture about it ----------
                await FixSkseAsync(root, report, progress);

                // ---------- 6b. Windows PATH leftovers ----------
                //
                //  SkyrimPlatform refuses to start if the Windows PATH
                //  variable mentions NirnLab or points into a Skyrim folder
                //  (it reads that as a Skyrim Together style conflict). Those
                //  entries get planted by other launchers and by hand-rescues,
                //  and no player should ever be sent into Windows settings to
                //  dig them out. The launcher put files on this machine; the
                //  launcher cleans up after them.
                CleanPathEnvironment(report);

                // ---------- 7. Holdline's own files: WIPE AND REDOWNLOAD ----------
                //
                //  This is the whole button now. There is no "look around the
                //  disk first" step ahead of it any more, because that step
                //  could only ever supply a file nobody had checked.
                //
                //  Deliberately unconditional. An older version asked
                //  ClientFiles.AreInstalled() first and skipped the install
                //  when it said yes — which is exactly how a tester with a
                //  crashing game got "you're good to go" instantly and no
                //  repair at all. A repair button that trusts the same check
                //  the broken machine already passed cannot fix that machine.
                await RebuildClientFilesAsync(dataDir, report, progress);

                // ---------- 7b. Browser backend ----------
                //
                //  MUST run AFTER the rebuild, because the bundle lays down
                //  SkyrimPlatform.ini and would overwrite this.
                //
                //  PROVEN, in this order, all from engine source plus real
                //  machines:
                //
                //    BrowserApi.cpp GetBackend() reads BackendName from
                //    Data/SKSE/Plugins/SkyrimPlatform.ini. Values: off /
                //    tilted / nirnlab / auto. The shipped default is "auto",
                //    and auto IS NOT IMPLEMENTED - it logs "Auto mode not
                //    implemented yet, falling back to Tilted UI (legacy)".
                //
                //    "tilted" cannot work on a Holdline install at all. It
                //    spawns a separate helper, SkyrimPlatformCEF.exe.hidden,
                //    from the Skyrim root (MyChromiumApp.h default argument).
                //    Holdline's bundle has never shipped that helper and it
                //    exists on no machine here. It also aborts outright if the
                //    substring "nirnlab" appears anywhere in the Windows PATH
                //    (MyChromiumApp.cpp PathVerifyNoIncompatibleEntries), and
                //    NirnLabUIPlugin.dll puts it there when SKSE loads it.
                //
                //    "nirnlab" runs a third-party browser we do not control.
                //    Observed on a tester's PC: CEFSubprocess.exe faulting
                //    with 0xc0000409 as the interface loaded, taking Skyrim
                //    with it. Same machine, both backends, dead at the same
                //    line of the log.
                //
                //  WHAT WORKS: "nirnlab". It is the value on the machine
                //  Holdline is developed and verified on, and it is the only
                //  value that produces a working interface. Setting it
                //  explicitly is the whole point of this step - the shipped
                //  default of "auto" silently lands on tilted, which cannot
                //  work here at all.
                //
                //  DO NOT set this to "off" to dodge a browser crash. Tested
                //  on a live server: with the browser off the world loads and
                //  the player can walk around, but pressing Enter grabs input
                //  for a chat box that never draws, and the player is frozen
                //  until they press Escape. A server without chat is not a
                //  server. Off is not a fallback; it is a different bug.
                //  NOTE: an earlier build ALSO deleted NirnLabUIPlugin.dll
                //  here. That was wrong and is deliberately gone: the nirnlab
                //  backend selected above is served BY that plugin. Removing
                //  it while naming it as the backend leaves the game with no
                //  interface at all.
                EnsureBrowserBackend(dataDir, report);

                // ---------- 7c. Turn the log up ----------
                //
                //  Runs after EnsureBrowserBackend, which is what creates
                //  SkyrimPlatform.ini on a machine that has never launched
                //  the game. VERIFIED against the bundle: it ships no
                //  SkyrimPlatform.ini of its own (Data\SKSE\Plugins in the
                //  zip holds only the versionlib bins, MpClientPlugin.dll
                //  and SkyrimPlatform.dll), so on a fresh install this file
                //  does not exist until one of these two steps writes it.
                //
                //  That ordering matters: EnsureBrowserBackend creates the
                //  file with a [Browser] section only, which leaves LogLevel
                //  absent -- and main.cpp then falls back to info. So this
                //  step appending [Debug] LogLevel = 0 is the ONLY thing that
                //  gives a fresh machine a readable log.
                EnsureTraceLogging(dataDir, report);

                // ---------- 7d. The orphaned preloader ----------
                //
                //  MUST run after the rebuild: the bundle is what installs
                //  d3dx9_42.dll, so checking before the download would look
                //  at the previous run's state and clear a file that is
                //  about to come straight back.
                RemoveOrphanedPreloader(root, dataDir, report);

                // ---------- 7d2. Engine Fixes itself ----------
                //
                //  Same timing, same rule, same reason as 7d. Added 19 Sep
                //  2026 after the first fresh-machine Join got the "did not
                //  pre-load ... Skyrim will now close" popup from the DLL the
                //  bundle had been shipping since 13 Aug. See ParkEngineFixes.
                ParkEngineFixes(dataDir, report);

                // ---------- 7e. Program Files permissions ----------
                //
                //  MUST run after the rebuild, for the same reason as 7d: the
                //  bundle is what creates Data\NirnLabUIPlatform, so granting
                //  rights before the download would be granting them on a
                //  folder that does not exist yet, and the freshly extracted
                //  files would inherit the old restrictive permissions.
                //
                //  Only does anything when Skyrim sits under Program Files.
                //  That is the machine where CEFSubprocess.exe fail-fasts with
                //  0xc0000409, named directly in the tester's Event Viewer.
                GrantBrowserFolderWriteAccess(root, dataDir, report);

                // ---------- 7f. Did that grant actually work? ----------
                //
                //  MUST run after 7e, and it is the only thing that has ever
                //  checked it. GrantBrowserFolderWriteAccess shells out to
                //  icacls and reports what it attempted; this writes a real
                //  file in each folder and reports what happened.
                CheckFolderWriteAccess(dataDir, report);

                // ---------- 8. Is the SkyrimPlatform we just laid down right? ----------
                //
                //  MOVED. This used to run BEFORE the rebuild, where it read
                //  the version off whatever DLL happened to be on the disk —
                //  a file that was about to be replaced anyway, so the number
                //  it reported was about to stop being true. Reading it after
                //  the install means the version in the report is the version
                //  the player will actually launch with.
                CheckPlatformVersion(dataDir, report);

                // ---------- 9. READ THE LOG ----------
                //
                //  Every check above tests something we already suspected.
                //  When the game drops a player to the main menu for a reason
                //  NOT on that list, all the steps pass, the report says
                //  "you're fine", and the player is still locked out with no
                //  idea why — while the actual reason sits written in plain
                //  text in skyrim-platform.log, which nothing here ever opened.
                //
                //  That gap is what turned a one-click button into "paste me
                //  your log". The log is a file on this machine. Reading it is
                //  the launcher's job, not the player's.
                ReadPlatformLog(report, progress);

                // ---------- 9.5 COLLECT THE LOGS WE CANNOT READ ----------
                //
                //  Step 9 reads skyrim-platform.log, which OUR plugin writes.
                //  On a machine that dies BEFORE our plugin wakes up, that log
                //  stops mid-startup and cannot say why. That is exactly what
                //  happened to a tester for months.
                //
                //  The layer that DOES know is SKSE's own skse64.log: when a
                //  plugin fails to load it writes the plugin's name and the
                //  reason in plain English. Nothing has ever collected it, so
                //  it has never once been read.
                //
                //  Runs BEFORE WriteDiagnosticReport so the report below
                //  mentions what this found.
                CollectExtraLogsToDesktop(root, report, progress);

                // ---------- 10. PUT IT ALL IN ONE FILE ----------
                //
                //  Dead last, so it captures what every step above found,
                //  including the log read. ReadPlatformLog has already
                //  dropped holdline-log.txt on the Desktop; this puts
                //  holdline-report.txt beside it.
                WriteDiagnosticReport(root, dataDir, report);
            }
            catch (Exception ex)
            {
                report.Blocking.Add("The check stopped early: " + ex.Message);
            }

            return report;
        }

        /// <summary>
        /// Undoes a Platform folder renamed during troubleshooting. This is
        /// the single highest-value check in the file: it is invisible from
        /// inside the game, and it makes every other fix pointless.
        /// </summary>
        private static void RepairPlatformFolder(string dataDir, RepairReport report)
        {
            try
            {
                var platform = Path.Combine(dataDir, "Platform");
                var disabled = Path.Combine(dataDir, "Platform_off");
                //  The launcher's own Single player switch used Platform-off
                //  (hyphen) until 1 Oct 2026, and people rename by hand too.
                if (!Directory.Exists(disabled) && Directory.Exists(Path.Combine(dataDir, "Platform-off")))
                {
                    disabled = Path.Combine(dataDir, "Platform-off");
                }

                //  Files the Single player switch parks by renaming in place.
                //  Put them back before anything below looks for them.
                try
                {
                    var dllOff = Path.Combine(dataDir, "SKSE", "Plugins", "MpClientPlugin.dll.off");
                    var dllOn = Path.Combine(dataDir, "SKSE", "Plugins", "MpClientPlugin.dll");
                    if (File.Exists(dllOff))
                    {
                        if (File.Exists(dllOn)) File.Delete(dllOn);
                        File.Move(dllOff, dllOn);
                        report.Fixed.Add("The multiplayer plugin had been switched off for single player. Switched it back on.");
                    }
                    //  6 Oct 2026: the Single player switch parks SkyrimPlatform.dll too.
                    //  Step 8 lays a fresh copy down anyway; this just stops the
                    //  parked one lingering beside it.
                    var spOff = Path.Combine(dataDir, "SKSE", "Plugins", "SkyrimPlatform.dll.off");
                    if (File.Exists(spOff))
                    {
                        var spOn = Path.Combine(dataDir, "SKSE", "Plugins", "SkyrimPlatform.dll");
                        if (File.Exists(spOn)) File.Delete(spOn);
                        File.Move(spOff, spOn);
                        report.Fixed.Add("Skyrim Platform had been switched off for single player. Switched it back on.");
                    }
                    //  17 Oct 2026: the Single player switch parks NirnLabUIPlugin.dll
                    //  when the Alien mod is absent. Holdline always needs the NirnLab
                    //  browser, so put it back - this makes Fix my game fully reverse
                    //  the switch rather than re-downloading the whole browser.
                    var nirnOff = Path.Combine(dataDir, "SKSE", "Plugins", "NirnLabUIPlugin.dll.off");
                    if (File.Exists(nirnOff))
                    {
                        var nirnOn = Path.Combine(dataDir, "SKSE", "Plugins", "NirnLabUIPlugin.dll");
                        if (File.Exists(nirnOn)) File.Delete(nirnOn);
                        File.Move(nirnOff, nirnOn);
                        report.Fixed.Add("The NirnLab browser had been switched off for single player. Switched it back on.");
                    }
                    //  17 Oct 2026: single player enables the Alien ESP; the
                    //  server keeps it off so "server = Alien off" stays
                    //  consistent until the multiplayer Alien work ships.
                    if (PluginListGuard.DisableAlienEsp())
                    {
                        report.Fixed.Add("The Alien mod had been switched on for single player. Switched it back off for the server.");
                    }
                    var pluginsDir = Path.Combine(platform, "Plugins");
                    if (Directory.Exists(pluginsDir))
                    {
                        var backOn = 0;
                        foreach (var parkedJs in Directory.GetFiles(pluginsDir, "*.js.off"))
                        {
                            var live = parkedJs.Substring(0, parkedJs.Length - 4);
                            if (File.Exists(live)) File.Delete(live);
                            File.Move(parkedJs, live);
                            backOn++;
                        }
                        if (backOn > 0)
                        {
                            report.Fixed.Add(backOn + " Holdline script file(s) had been switched off for single player. Switched them back on.");
                        }
                    }
                }
                catch (Exception exOff)
                {
                    report.Blocking.Add("Couldn't switch Holdline's parked files back on (" + exOff.Message + ").");
                }

                if (Directory.Exists(platform))
                {
                    report.Fine.Add("Holdline's Platform folder is in place.");

                    if (Directory.Exists(disabled))
                    {
                        // Both exist. Renaming would collide, and deleting is
                        // never this tool's job, so say so and move on.
                        report.Fine.Add(
                            "A leftover Platform_off folder is sitting there too. " +
                            "It's harmless and can be ignored.");
                    }

                    return;
                }

                if (Directory.Exists(disabled))
                {
                    Directory.Move(disabled, platform);

                    report.Fixed.Add(
                        "Your Platform folder had been renamed to Platform_off and was " +
                        "switched off. Renamed it back.");

                    return;
                }

                // Neither exists: not a fault, the client files just haven't
                // been installed yet. The install step at the end handles it.
            }
            catch (Exception ex)
            {
                report.Blocking.Add(
                    "Couldn't rename the Platform folder (" + ex.Message + "). " +
                    "Try running Holdline as administrator.");
            }
        }

        /// <summary>
        /// SKSE plugins belong in Data\SKSE\Plugins. On a tester's machine
        /// EngineFixes.dll and its debug file had ended up in
        /// Data\Platform\Plugins instead, where SKSE never looks for them.
        /// Moves them back, but only where nothing would be overwritten.
        ///
        /// NARROWED IN THIS REWRITE. This list used to include
        /// SkyrimPlatform.dll and MpClientPlugin.dll, so that a stray copy of
        /// either could be "rescued" out of the wrong folder. That rescue is
        /// now pointless at best and harmful at worst: the download a few
        /// steps below supplies both files, known-good, every single time.
        /// Moving an unknown copy into place first just risks a stale DLL
        /// surviving in a folder the install does not clean.
        ///
        /// What stays on the list is exactly what Holdline does NOT ship and
        /// therefore cannot replace: the Engine Fixes set. All five files,
        /// not just the DLL — moving the DLL alone would leave it in the
        /// right folder with its config missing, which fails just as silently
        /// as being in the wrong folder did.
        /// </summary>
        private static void RepairMisplacedSksePlugins(string dataDir, RepairReport report)
        {
            try
            {
                var wrongDir = Path.Combine(dataDir, "Platform", "Plugins");
                var rightDir = Path.Combine(dataDir, "SKSE", "Plugins");

                if (!Directory.Exists(wrongDir))
                {
                    return;
                }

                // Only these move. Data\Platform\Plugins is a real folder with
                // real contents — skymp5-client.js and its settings file live
                // there and MUST stay — so anything not matching this list is
                // left strictly alone.
                var strays = new[]
                {
                    "EngineFixes.dll",
                    "EngineFixes.pdb",
                    "EngineFixes.toml",
                    "EngineFixes_preload.txt",
                    "EngineFixes_SNCT.ini"
                };

                var moved = new List<string>();

                foreach (var name in strays)
                {
                    var source = Path.Combine(wrongDir, name);

                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(rightDir);
                    var target = Path.Combine(rightDir, name);

                    if (File.Exists(target))
                    {
                        // The right copy is already where it belongs. The stray
                        // is a duplicate. Report it rather than delete it.
                        report.Fine.Add(
                            "A spare copy of " + name + " is in the wrong folder. " +
                            "The correct one is in place, so it's doing no harm.");

                        continue;
                    }

                    File.Move(source, target);
                    moved.Add(name);
                }

                if (moved.Count > 0)
                {
                    report.Fixed.Add(
                        "Moved " + string.Join(" and ", moved) +
                        " into the folder Skyrim actually reads.");
                }
            }
            catch (Exception ex)
            {
                report.Blocking.Add(
                    "Couldn't move some files into the right folder (" + ex.Message +
                    "). Try running Holdline as administrator.");
            }
        }

        /// <summary>
        /// SKSE, installed rather than described.
        ///
        /// The old version of this method could only TELL the player their
        /// SKSE was missing or the wrong version, and then send them off to
        /// find, download and drag a .7z themselves. That is precisely the
        /// kind of "here, you do it" step that costs testers whole evenings,
        /// and the launcher already contains code that can do the whole job.
        ///
        /// Note on what this does NOT do: if SKSE is present and its version
        /// matches the game, it is left alone. Re-downloading a working SKSE
        /// on every click would risk breaking a healthy install to fix
        /// nothing. A wrong or missing one is replaced without asking.
        /// </summary>
        private static async Task FixSkseAsync(
            string root,
            RepairReport report,
            IProgress<string> progress)
        {
            try
            {
                var exe = MainWindow.SkyrimExeIn(root);
                var skse = SkseSetup.Inspect(root, exe);

                if (skse.Ok)
                {
                    report.Fine.Add("SKSE is installed and matches your Skyrim.");
                    return;
                }

                // Say what was wrong BEFORE fixing it, so the report explains
                // itself rather than announcing a download out of nowhere.
                string what;

                if (!skse.LoaderPresent)
                {
                    what = "SKSE wasn't installed";
                }
                else if (skse.InstalledRuntime == null)
                {
                    what = "your SKSE was incomplete";
                }
                else
                {
                    what = "your SKSE was the wrong version (" +
                           SkseStatus.Pretty(skse.InstalledRuntime) + ")";
                }

                progress.Report("Downloading the right SKSE for your game...");

                var error = await SkseDownload.DownloadAndInstallAsync(
                    root,
                    exe,
                    line => progress.Report(line));

                if (error != null)
                {
                    // Fell back to a human step, so this is genuinely blocking
                    // and must be reported as such -- never swallowed.
                    report.Blocking.Add(error);
                    return;
                }

                // Prove it, rather than assuming the installer worked.
                var after = SkseSetup.Inspect(root, MainWindow.SkyrimExeIn(root));

                if (after.Ok)
                {
                    report.Fixed.Add(
                        "Fixed SKSE — " + what + ", so Holdline downloaded and " +
                        "installed the version that matches your Skyrim.");
                }
                else
                {
                    report.Blocking.Add(
                        "Holdline tried to install SKSE but it still isn't right. " +
                        "Use \"Get SKSE\" above and install it manually.");
                }
            }
            catch (Exception ex)
            {
                report.Blocking.Add("Couldn't sort out SKSE: " + ex.Message);
            }
        }

        /// <summary>
        /// Program Files installs: hand the browser's folders back to the player.
        ///
        /// PROVEN from a tester's Windows Event Viewer, Event ID 1000:
        ///
        ///     Faulting application: CEFSubprocess.exe
        ///     Faulting module:      CEFSubprocess.exe
        ///     Exception code:       0xc0000409
        ///     Path: c:\program files (x86)\steam\steamapps\common\
        ///           Skyrim Special Edition\Data\NirnLabUIPlatform\
        ///           CEFSubprocess.exe
        ///
        /// 0xc0000409 is a fail-fast: the process killed itself on purpose
        /// rather than continue. That machine's skyrim-platform.log ends dead
        /// at the same second with no error line, which is what a hard process
        /// death looks like from the inside. Everything before that point in
        /// the log is healthy - hooks attached, thousands of Papyrus binds,
        /// MpClientPlugin registering its own functions - so nothing Holdline
        /// loads is failing. The browser subprocess is.
        ///
        /// The one structural difference between that machine and a working
        /// one is the folder. Program Files is write-protected by Windows, and
        /// CEF - the Chromium runtime NirnLab uses to draw Holdline's
        /// interface and chat - writes cache and log files beside itself when
        /// it spawns its subprocess. Denied write access at that moment is
        /// exactly the class of failure that fail-fasts.
        ///
        /// Note the asymmetry this explains. The LAUNCHER can write there,
        /// because the player is told to run it as administrator and because
        /// Fix My Game demonstrably extracted 300-odd MB into that same
        /// folder. The GAME is launched by Steam, unelevated, and gets no such
        /// help. So "the launcher could write it" is not evidence the game
        /// can, and that is the gap this step closes: a granted ACL persists
        /// after the launcher exits, so the unelevated game inherits it.
        ///
        /// Rather than move fifteen gigabytes of someone else's install, this
        /// grants modify rights on the folders Holdline owns. Nothing outside
        /// those folders is touched, nothing is deleted, and the change is the
        /// same one Windows makes when a user takes ownership by hand.
        ///
        /// icacls is used rather than the .NET ACL API because it ships with
        /// every supported Windows, and the well-known SID *S-1-5-32-545
        /// (BUILTIN\Users) is spelled identically on every language of
        /// Windows, which the word "Users" is not.
        ///
        /// ELEVATION. Editing an ACL in Program Files needs administrator
        /// rights. An earlier draft of this step simply told the player to
        /// relaunch as administrator, which is the manual step Holdline
        /// exists to delete. So: if this process is already elevated the
        /// grants run in-process. If it is not, they are written to one batch
        /// file and run once through ShellExecute with the runas verb - a
        /// single UAC prompt for all folders, not one per folder. Success is
        /// confirmed by a flag file the batch writes only after every grant
        /// returned zero, because a shell-executed process cannot have its
        /// output redirected and its exit code alone is not worth trusting.
        ///
        /// /C is deliberately NOT passed. It continues past per-file errors
        /// and can still exit zero, which would let this report a fix that
        /// half happened. Without it icacls stops and returns non-zero on the
        /// first failure, which is the answer we actually want.
        /// </summary>
        internal static void GrantBrowserFolderWriteAccess(string root, string dataDir,
                                                          RepairReport report)
        {
            try
            {
                if (root.IndexOf("Program Files", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // Nothing to do. A Skyrim on a plain drive path already has
                    // the rights CEF needs, which is why those installs have
                    // never hit this crash.
                    return;
                }

                // Recursive on the folders Holdline installs, because every
                // file inside them was extracted under the old restrictive
                // permissions and needs the new ones.
                //
                // Non-recursive on Skyrim's own root and Data, so new files
                // created there inherit modify rights without walking a
                // fifteen gigabyte install, which would take minutes.
                var targets = new List<(string Path, bool Recurse)>
                {
                    (Path.Combine(dataDir, "NirnLabUIPlatform"), true),
                    (Path.Combine(dataDir, "Platform"), true),
                    (Path.Combine(dataDir, "SKSE", "Plugins"), true),
                    (dataDir, false),
                    (root, false)
                };

                var present = targets.Where(t => Directory.Exists(t.Path)).ToList();

                if (present.Count == 0)
                {
                    report.Fine.Add(
                        "Skyrim is in Program Files, but Holdline's folders aren't there " +
                        "yet, so there was nothing to unlock.");

                    return;
                }

                var elevated = IsRunningElevated();

                string? failure = null;

                var ok = elevated
                    ? GrantInProcess(present, out failure)
                    : GrantViaElevatedHelper(present, out failure);

                if (ok)
                {
                    report.Fixed.Add(
                        "Your Skyrim is installed in Program Files, which Windows " +
                        "write-protects. That blocks Holdline's interface from starting " +
                        "and crashes the game the moment you join. Unlocked Holdline's " +
                        "folders inside Skyrim - nothing was moved or deleted.");
                }
                else
                {
                    report.Blocking.Add(
                        "Your Skyrim is installed in Program Files, which Windows " +
                        "write-protects - on its own that can crash the game as you " +
                        "join. Holdline couldn't change those permissions. Close " +
                        "Holdline, right-click its icon, choose \"Run as administrator\" " +
                        "and run this again. (" + (failure ?? "no detail") + ")");
                }
            }
            catch (Exception ex)
            {
                report.Fine.Add("Couldn't check the Program Files permissions: " + ex.Message);
            }
        }

        /// <summary>
        /// Is this process running with administrator rights? Fully qualified
        /// rather than adding a using, to keep this file's header untouched.
        /// </summary>
        private static bool IsRunningElevated()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

                var principal = new System.Security.Principal.WindowsPrincipal(identity);

                return principal.IsInRole(
                    System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                // If we cannot tell, assume not elevated. Guessing "yes" would
                // run the grants in-process, fail quietly, and report a fix
                // that never happened. Guessing "no" costs a UAC prompt.
                return false;
            }
        }

        /// <summary>
        /// The grant, run directly. Only valid when already elevated.
        /// </summary>
        private static bool GrantInProcess(
            List<(string Path, bool Recurse)> targets, out string? failure)
        {
            failure = null;

            foreach (var target in targets)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "icacls",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    psi.ArgumentList.Add(target.Path);
                    psi.ArgumentList.Add("/grant");
                    psi.ArgumentList.Add("*S-1-5-32-545:(OI)(CI)M");

                    if (target.Recurse)
                    {
                        psi.ArgumentList.Add("/T");
                    }

                    using var proc = Process.Start(psi);

                    if (proc == null)
                    {
                        failure = "Windows wouldn't start icacls.";
                        return false;
                    }

                    // Read before waiting. icacls output is a few lines, well
                    // under any pipe buffer, so this cannot deadlock, and
                    // ReadToEnd returns when the process closes the stream.
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();

                    if (!proc.WaitForExit(600000))
                    {
                        failure = "Permission change timed out on " +
                                  Path.GetFileName(target.Path) + ".";
                        return false;
                    }

                    if (proc.ExitCode != 0)
                    {
                        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

                        failure = string.IsNullOrWhiteSpace(detail)
                            ? ("icacls exit code " + proc.ExitCode)
                            : detail.Trim();

                        return false;
                    }
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The grant, run once through UAC. One prompt covers every folder.
        ///
        /// Output cannot be captured through ShellExecute, so the batch writes
        /// a flag file as its last act and this reads that file back. A missing
        /// flag means a real failure, whatever the exit code said.
        /// </summary>
        private static bool GrantViaElevatedHelper(
            List<(string Path, bool Recurse)> targets, out string? failure)
        {
            failure = null;

            var work = Path.Combine(Path.GetTempPath(),
                                    "Holdline-perms-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(work);

                var batPath = Path.Combine(work, "unlock.bat");
                var flagPath = Path.Combine(work, "done.txt");

                var script = new StringBuilder();

                script.AppendLine("@echo off");

                foreach (var target in targets)
                {
                    // The grant string is quoted so cmd treats its brackets as
                    // literal text rather than trying to parse them.
                    script.AppendLine("icacls \"" + target.Path +
                                      "\" /grant \"*S-1-5-32-545:(OI)(CI)M\"" +
                                      (target.Recurse ? " /T" : ""));

                    script.AppendLine("if errorlevel 1 exit /b 1");
                }

                script.AppendLine("echo ok>\"" + flagPath + "\"");
                script.AppendLine("exit /b 0");

                // No byte order mark: cmd can choke on one at the top of a
                // batch file.
                File.WriteAllText(batPath, script.ToString(),
                                  new UTF8Encoding(false));

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"" + batPath + "\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);

                if (proc == null)
                {
                    failure = "Windows wouldn't start the permission helper.";
                    return false;
                }

                if (!proc.WaitForExit(600000))
                {
                    failure = "The permission change timed out.";
                    return false;
                }

                if (!File.Exists(flagPath))
                {
                    failure = "The permission change didn't finish (exit code " +
                              proc.ExitCode + ").";

                    return false;
                }

                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Thrown when the player closes the UAC prompt or clicks No.
                failure = "You closed the Windows permission prompt. Run Fix My Game " +
                          "again and choose Yes.";

                return false;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(work))
                    {
                        Directory.Delete(work, true);
                    }
                }
                catch
                {
                    // A leftover temp folder is not worth failing a repair over.
                }
            }
        }


        /// <summary>
        /// Removes d3dx9_42.dll when SSE Engine Fixes is not installed.
        ///
        /// THIS IS THE ORPHANED-PRELOADER BUG, and it shipped to every clean
        /// machine while being invisible on the development one.
        ///
        ///   The client bundle installs Root\d3dx9_42.dll into the Skyrim
        ///   folder. That file is not a DirectX library despite the name - it
        ///   is a PROXY DLL, the SKSE preloader that brings SSE Engine Fixes
        ///   up before the game's own startup.
        ///
        ///   The bundle does NOT ship Engine Fixes itself. So on any machine
        ///   that does not already own Engine Fixes, Holdline installs a
        ///   loader for a mod that is not there: the proxy is loaded early by
        ///   the game, finds no EngineFixes.dll and no EngineFixes_preload.txt
        ///   behind it, and forwards to a real d3dx9_42.dll that a modern
        ///   Windows install has no reason to have - Skyrim SE is DirectX 11
        ///   and the DX9 end-user runtime is not present by default.
        ///
        ///   A development machine with Engine Fixes already installed never
        ///   sees any of this. Every fresh machine does. That asymmetry is
        ///   exactly why "it works here" and "it crashes there" persisted
        ///   through a full wipe and reinstall.
        ///
        /// Renamed rather than deleted, so installing Engine Fixes later is a
        /// one-step undo, and so a machine that genuinely needs the proxy for
        /// something else can get it back.
        /// </summary>
        private static void RemoveOrphanedPreloader(string root, string dataDir,
                                                    RepairReport report)
        {
            try
            {
                var proxy = Path.Combine(root, "d3dx9_42.dll");
                var parked = Path.Combine(root, "d3dx9_42.dll.holdline-off");

                // THE REVERSE GEAR IS GONE. DO NOT PUT IT BACK. 13 Aug 2026.
                //
                //  This step used to restore d3dx9_42.dll whenever
                //  EngineFixes.dll was present, on the reasoning that the
                //  proxy "belongs" where it was in that case.
                //
                //  That reasoning held only while Engine Fixes was never
                //  shipped. On 13 Aug Engine Fixes was added to Data.zip, so
                //  EngineFixes.dll became present on EVERY machine after a
                //  fresh download -- and this branch started re-arming the
                //  banned preloader for everybody. Tester Gio pressed Fix my
                //  game to escape a crash and the button rebuilt the crash,
                //  every single time, which is why nothing anyone deleted by
                //  hand ever stuck.
                //
                //  The 10 Aug finding is absolute and outranks any local
                //  reasoning here: d3dx9_42.dll is a real Windows DirectX file
                //  that Engine Fixes replaces with a proxy. On a machine that
                //  does not already have Engine Fixes installed BY HAND, that
                //  proxy leads nowhere and Skyrim dies ~4 seconds into startup
                //  with no popup and no log. Skyrim SE is DX11 and does not
                //  need this file at all; SkyrimPlatform.dll and
                //  MpClientPlugin.dll load through SKSE normally.
                //
                //  So the rule has no exception and no Engine Fixes clause:
                //  the proxy NEVER lives in Root. If it is there, park it.
                //  A player who genuinely wants full Engine Fixes can rename
                //  the .holdline-off file back themselves; that is one rename
                //  for one person, versus a dead game for every fresh tester.
                if (!File.Exists(proxy))
                {
                    return;
                }

                if (File.Exists(parked))
                {
                    File.Delete(parked);
                }

                File.Move(proxy, parked);

                report.Fixed.Add(
                    "Moved aside a startup file (d3dx9_42.dll). It replaces a real " +
                    "Windows graphics file and can stop Skyrim starting at all - Holdline " +
                    "does not need it. Renamed, not deleted.");
            }
            catch (Exception ex)
            {
                report.Fine.Add("Couldn't check the Engine Fixes startup file: " + ex.Message);
            }
        }

        /// <summary>
        /// Drops a copy of the SkyrimPlatform log on the player's Desktop.
        ///
        /// Getting one log out of one tester took most of an evening and four
        /// wrong paths: it is not in the Skyrim folder, not in Data\Platform,
        /// not in a Logs subfolder, but under
        /// Documents\My Games\Skyrim Special Edition\SKSE - and Documents may
        /// be redirected into OneDrive, so even the right path is machine
        /// specific.
        ///
        /// The player should never have to find it. Copy it somewhere they
        /// cannot miss and name it so it is obvious what to send.
        /// </summary>
        private static void SaveLogCopyToDesktop(string? logPath, RepairReport report)
        {
            try
            {
                if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
                {
                    return;
                }

                var desktop = Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory);

                if (string.IsNullOrEmpty(desktop))
                {
                    return;
                }

                var target = Path.Combine(desktop, "holdline-log.txt");

                File.Copy(logPath, target, true);

                report.Fine.Add(
                    "Put a copy of Skyrim's log on your Desktop as holdline-log.txt. " +
                    "If you need help, send that file.");
            }
            catch
            {
                // A convenience copy is never worth failing a repair over.
            }
        }


        /// <summary>
        /// Parks SSE Engine Fixes. Holdline does not ship it and does not
        /// need it, and on a machine without its preloader the DLL is fatal.
        ///
        /// PROVEN 19 Sep 2026, first true fresh-machine Join: Data.zip of
        /// 13 Aug-19 Sep carried EngineFixes.dll (Part 1 only, no
        /// EngineFixes_preload.txt, on the belief that it then loads quietly
        /// as an ordinary plugin). It does not. The DLL put up "Engine Fixes
        /// did not pre-load and fixes are not active ... d3dx9_42.dll ...
        /// Skyrim will now close" and closed the game before the main menu.
        ///
        /// The preloader it demands, d3dx9_42.dll, is parked unconditionally
        /// by RemoveOrphanedPreloader just above (it kills fresh machines a
        /// different way - see there). With the preloader gone the DLL can
        /// only ever produce that popup, so it goes the same way, by the
        /// same rule, with no exception and no reverse gear. The developer
        /// and one tester have both files hand-placed and never see either
        /// fault; every fresh machine sees one or the other.
        ///
        /// This REPLACES the old CheckEngineFixes / RestoreDisabledEngineFixes
        /// pair, which did the opposite - switched a parked
        /// EngineFixes.dll.disabled back on - and the tbbmalloc (Part 2)
        /// search, which only existed to feed Engine Fixes.
        ///
        /// Runs AFTER the rebuild, for the same reason as the preloader
        /// step: an older Data.zip re-installs the DLL, so parking it before
        /// the download would clear a file that comes straight back.
        ///
        /// The .toml and _SNCT.ini beside it are inert config and are left
        /// alone. Renamed, not deleted: one rename for the one player who
        /// installs the full mod by hand and wants it, versus a closed game
        /// for everyone else.
        /// </summary>
        private static void ParkEngineFixes(string dataDir, RepairReport report)
        {
            try
            {
                var sksePlugins = Path.Combine(dataDir, "SKSE", "Plugins");
                var dll = Path.Combine(sksePlugins, "EngineFixes.dll");
                var parked = Path.Combine(sksePlugins, "EngineFixes.dll.holdline-off");
                var oldParked = Path.Combine(sksePlugins, "EngineFixes.dll.disabled");

                // A copy parked by a build before 9 Aug is already off. Bring
                // it under the one name this file uses, and nothing else.
                if (!File.Exists(dll) && File.Exists(oldParked))
                {
                    if (File.Exists(parked))
                    {
                        File.Delete(parked);
                    }

                    File.Move(oldParked, parked);
                    return;
                }

                if (!File.Exists(dll))
                {
                    return;
                }

                if (File.Exists(parked))
                {
                    File.Delete(parked);
                }

                File.Move(dll, parked);

                report.Fixed.Add(
                    "Moved aside SSE Engine Fixes (EngineFixes.dll). Holdline doesn't " +
                    "need it, and without its companion file it stops Skyrim with an " +
                    "\"Engine Fixes did not pre-load\" message before the menu. Renamed, " +
                    "not deleted.");
            }
            catch (Exception ex)
            {
                report.Fine.Add("Couldn't check Engine Fixes: " + ex.Message);
            }
        }

        /// <summary>
        /// Pulls the value out of an ini line, dropping any trailing comment.
        /// "BackendName = auto ; off/tilted/nirnlab/auto" gives back "auto".
        /// Returns an empty string when there is no value at all.
        /// </summary>
        private static string ReadIniValue(string line)
        {
            var equals = line.IndexOf('=');

            if (equals < 0 || equals == line.Length - 1)
            {
                return "";
            }

            var value = line.Substring(equals + 1);

            // Either comment marker ends the value. Whichever comes first.
            var cut = value.IndexOfAny(new[] { ';', '#' });

            if (cut >= 0)
            {
                value = value.Substring(0, cut);
            }

            return value.Trim();
        }

        /// <summary>
        /// Forces SkyrimPlatform's log level to trace (0).
        ///
        /// WHY THIS EXISTS, and it cost a whole night of debugging:
        ///
        ///   Settings.cpp writes [Debug] LogLevel = 2 by default, and
        ///   main.cpp does:
        ///
        ///     settings->GetInteger("Debug", "LogLevel", info)
        ///     log->set_level(logLevel)
        ///
        ///   Level 2 is "info". At info level the log prints the plugin
        ///   loading, the NirnLab version check, the chosen browser backend
        ///   and the font list -- and then goes SILENT, because everything
        ///   after that point is logged at debug or trace:
        ///
        ///     "Attached hook: ..."            logger::debug
        ///     "Custom events initialized."    logger::debug
        ///     "VirtualMachine::Bind called"   spdlog::trace
        ///
        ///   So a tester's log ends a few lines in and LOOKS like the game
        ///   died right after loading fonts, on every single machine that
        ///   never had its level lowered by hand. A developer machine set to
        ///   0 shows thousands more lines. Comparing the two logs invents a
        ///   difference that is not there and hides the real crash point.
        ///
        ///   Never ship a build that debugs a player at level 2. The whole
        ///   value of the log is the part above info.
        ///
        /// Edits in place and leaves every other line alone, same as the
        /// browser backend step. Failing to set this is NOT blocking - a
        /// quiet log is worse for us, not broken for the player - so this
        /// only ever reports into Fine.
        /// </summary>
        private static void EnsureTraceLogging(string dataDir, RepairReport report)
        {
            try
            {
                var pluginsDir = Path.Combine(dataDir, "SKSE", "Plugins");
                var iniPath = Path.Combine(pluginsDir, "SkyrimPlatform.ini");

                Directory.CreateDirectory(pluginsDir);

                if (!File.Exists(iniPath))
                {
                    // EnsureBrowserBackend runs first and creates this file, so
                    // reaching here means it failed and already said so. Write
                    // what we can and stay quiet.
                    File.WriteAllText(iniPath, "[Debug]\r\nLogLevel = 0\r\n");
                    return;
                }

                var lines = new List<string>(File.ReadAllLines(iniPath));

                var debugSection = -1;
                var levelLine = -1;
                var inDebug = false;

                for (var i = 0; i < lines.Count; i++)
                {
                    var trimmed = lines[i].Trim();

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        inDebug = string.Equals(
                            trimmed, "[Debug]", StringComparison.OrdinalIgnoreCase);

                        if (inDebug)
                        {
                            debugSection = i;
                        }

                        continue;
                    }

                    if (inDebug &&
                        trimmed.StartsWith("LogLevel", StringComparison.OrdinalIgnoreCase))
                    {
                        levelLine = i;
                    }
                }

                if (levelLine >= 0)
                {
                    // Same trap as BackendName: SkyrimPlatform writes this
                    // setting with its own comment attached --
                    //
                    //   LogLevel = 2 ; 0 - trace, 1 - debug, 2 - info, ...
                    //
                    // so a naive search of the LINE for "0" finds it inside
                    // that comment and concludes the level is already trace.
                    // Read the value only.
                    if (ReadIniValue(lines[levelLine]) == "0")
                    {
                        report.Fine.Add("Skyrim's log is already set to full detail.");
                        return;
                    }

                    lines[levelLine] = "LogLevel = 0";
                }
                else if (debugSection >= 0)
                {
                    lines.Insert(debugSection + 1, "LogLevel = 0");
                }
                else
                {
                    lines.Add("");
                    lines.Add("[Debug]");
                    lines.Add("LogLevel = 0");
                }

                File.WriteAllLines(iniPath, lines);

                report.Fine.Add(
                    "Turned Skyrim's log up to full detail, so if anything goes wrong " +
                    "the log actually says where.");
            }
            catch
            {
                // A log we cannot turn up is never a reason to fail a repair.
            }
        }


        /// <summary>
        /// Makes sure SkyrimPlatform.ini turns the in-game browser off.
        ///
        /// Engine facts this rests on (BrowserApi.cpp, MyChromiumApp.cpp):
        ///   - BackendName defaults to "auto"
        ///   - "auto" is not implemented; it falls back to the legacy tilted
        ///     backend
        ///   - the tilted backend aborts if the substring "nirnlab" appears
        ///     anywhere in the Windows PATH
        ///   - NirnLabUIPlugin.dll adds its own folder to PATH when loaded
        ///
        /// So a default install plus the NirnLab files we ship equals a game
        /// that will not start. Naming the backend outright avoids the broken
        /// auto path entirely.
        ///
        /// Edits in place, keeping every other line of the file untouched -
        /// the ini also carries log level and plugin folders, and rewriting it
        /// wholesale would throw away settings we do not own.
        /// </summary>
        private static void EnsureBrowserBackend(string dataDir, RepairReport report)
        {
            try
            {
                var pluginsDir = Path.Combine(dataDir, "SKSE", "Plugins");
                var iniPath = Path.Combine(pluginsDir, "SkyrimPlatform.ini");

                Directory.CreateDirectory(pluginsDir);

                if (!File.Exists(iniPath))
                {
                    File.WriteAllText(
                        iniPath,
                        "[Browser]\r\nBackendName = nirnlab\r\n");

                    report.Fixed.Add(
                        "Set Skyrim's browser setting to the one Holdline needs. " +
                        "Skyrim draws chat and menus with it, and the default value " +
                        "doesn't work.");

                    return;
                }

                var lines = new List<string>(File.ReadAllLines(iniPath));

                var browserSection = -1;
                var backendLine = -1;
                var sectionEnd = -1;
                var inBrowser = false;

                for (var i = 0; i < lines.Count; i++)
                {
                    var trimmed = lines[i].Trim();

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        var wasInBrowser = inBrowser;
                        inBrowser = string.Equals(
                            trimmed, "[Browser]", StringComparison.OrdinalIgnoreCase);

                        if (inBrowser)
                        {
                            browserSection = i;
                        }
                        else if (wasInBrowser && sectionEnd < 0)
                        {
                            sectionEnd = i;
                        }

                        continue;
                    }

                    if (inBrowser &&
                        trimmed.StartsWith("BackendName", StringComparison.OrdinalIgnoreCase))
                    {
                        backendLine = i;
                    }
                }

                if (backendLine >= 0)
                {
                    // Read the VALUE, not the line. SkyrimPlatform writes this
                    // setting with its own comment attached:
                    //
                    //   BackendName = auto ; Browser backend name: off/tilted/nirnlab/auto
                    //
                    // A naive search of the line for "off" finds it inside that
                    // comment and concludes the setting is already correct,
                    // leaving "auto" in place - which is the broken value this
                    // whole step exists to replace. Split on '=', drop any
                    // trailing comment, then compare exactly.
                    if (string.Equals(
                            ReadIniValue(lines[backendLine]),
                            "nirnlab",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        report.Fine.Add("Skyrim's browser setting is already correct.");
                        return;
                    }

                    lines[backendLine] = "BackendName = nirnlab";
                }
                else if (browserSection >= 0)
                {
                    lines.Insert(browserSection + 1, "BackendName = nirnlab");
                }
                else
                {
                    lines.Add("");
                    lines.Add("[Browser]");
                    lines.Add("BackendName = nirnlab");
                }

                File.WriteAllLines(iniPath, lines);

                report.Fixed.Add(
                    "Corrected Skyrim's browser setting. Chat and menus are drawn with it, " +
                    "and the old value stops the game starting.");
            }
            catch (Exception ex)
            {
                report.Blocking.Add(
                    "Holdline couldn't write Skyrim's browser setting " +
                    "(Data\\SKSE\\Plugins\\SkyrimPlatform.ini): " + ex.Message +
                    "\n\nClose Holdline, right-click its icon, choose \"Run as " +
                    "administrator\", and press Fix my game once more.");
            }
        }

        /// <summary>
        /// Removes Skyrim-related leftovers from the Windows PATH variable.
        ///
        /// SkyrimPlatform scans PATH at startup and refuses to run if any
        /// entry mentions NirnLab or points inside a Skyrim install — it
        /// treats that as a Skyrim Together style conflict and quits before
        /// the game appears. Those entries are planted by other launchers'
        /// installers and by hand-rescues; no legitimate program needs a
        /// Skyrim game folder on PATH, so removing them is always safe.
        ///
        /// The user-level PATH can be edited without administrator rights.
        /// The machine-level PATH cannot; if bad entries live there and the
        /// write is refused, that is reported as the one thing left to do
        /// (run Holdline as administrator once) instead of failing silently.
        /// </summary>
        private static void CleanPathEnvironment(RepairReport report)
        {
            var removedAny = false;

            // User PATH — always editable.
            try
            {
                var removed = ScrubPathTarget(EnvironmentVariableTarget.User);

                foreach (var entry in removed)
                {
                    report.Fixed.Add(
                        "Removed a leftover Skyrim entry from your Windows PATH " +
                        "settings that was stopping the game from starting:\n" + entry);

                    removedAny = true;
                }
            }
            catch (Exception ex)
            {
                report.Fine.Add("Couldn't check your user PATH settings: " + ex.Message);
            }

            // Machine PATH — editable only when elevated.
            try
            {
                var machineBad = FindBadPathEntries(EnvironmentVariableTarget.Machine);

                if (machineBad.Count > 0)
                {
                    try
                    {
                        var removed = ScrubPathTarget(EnvironmentVariableTarget.Machine);

                        foreach (var entry in removed)
                        {
                            report.Fixed.Add(
                                "Removed a leftover Skyrim entry from the system-wide " +
                                "PATH settings:\n" + entry);

                            removedAny = true;
                        }
                    }
                    catch
                    {
                        report.Blocking.Add(
                            "A leftover Skyrim entry in the system-wide PATH settings is " +
                            "stopping the game, and Windows won't let Holdline remove it " +
                            "without permission. Close Holdline, right-click its icon, " +
                            "choose \"Run as administrator\", and press Fix my game once more.");
                    }
                }
            }
            catch
            {
                // Reading machine PATH failed entirely — nothing useful to say.
            }

            if (!removedAny)
            {
                report.Fine.Add("Windows PATH settings are clean of Skyrim leftovers.");
            }
        }

        /// <summary>
        /// Lists the PATH entries at one target that SkyrimPlatform would
        /// refuse to start over.
        /// </summary>
        private static List<string> FindBadPathEntries(EnvironmentVariableTarget target)
        {
            var bad = new List<string>();
            var raw = Environment.GetEnvironmentVariable("Path", target);

            if (string.IsNullOrEmpty(raw))
            {
                return bad;
            }

            foreach (var entry in raw.Split(';'))
            {
                if (IsSkyrimPathEntry(entry))
                {
                    bad.Add(entry.Trim());
                }
            }

            return bad;
        }

        /// <summary>
        /// Rewrites PATH at one target with the Skyrim leftovers removed and
        /// returns the removed entries. Everything else is preserved in its
        /// original order, byte for byte.
        /// </summary>
        private static List<string> ScrubPathTarget(EnvironmentVariableTarget target)
        {
            var removed = new List<string>();
            var raw = Environment.GetEnvironmentVariable("Path", target);

            if (string.IsNullOrEmpty(raw))
            {
                return removed;
            }

            var kept = new List<string>();

            foreach (var entry in raw.Split(';'))
            {
                if (IsSkyrimPathEntry(entry))
                {
                    if (entry.Trim().Length > 0)
                    {
                        removed.Add(entry.Trim());
                    }
                }
                else
                {
                    kept.Add(entry);
                }
            }

            if (removed.Count > 0)
            {
                Environment.SetEnvironmentVariable("Path", string.Join(";", kept), target);
            }

            return removed;
        }

        /// <summary>
        /// The tripwires SkyrimPlatform checks PATH for. "nirnlab" is the one
        /// its own error popup names; a path inside any Skyrim install or a
        /// Skyrim Together folder triggers the same refusal. Nothing
        /// legitimate ever needs these on PATH.
        /// </summary>
        private static bool IsSkyrimPathEntry(string entry)
        {
            return entry.IndexOf("nirnlab", StringComparison.OrdinalIgnoreCase) >= 0
                || entry.IndexOf("skyrim special edition", StringComparison.OrdinalIgnoreCase) >= 0
                || entry.IndexOf("skyrim together", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Deletes Holdline's game files and downloads them again from the
        /// release. Unconditional: no "are they already there?" shortcut, no
        /// fingerprint check, no version stamp consulted. The point of this
        /// button is to stop trusting whatever is on the disk.
        ///
        /// WHAT THIS DELETES, and nothing else, ever:
        ///
        ///     Data\Platform                              (our folder, in full)
        ///     Data\Platform_off                          (our leftover from testing)
        ///     Data\SKSE\Plugins\SkyrimPlatform.dll
        ///     Data\SKSE\Plugins\MpClientPlugin.dll
        ///
        /// The two DLLs are NEW to this list, and that is the heart of the
        /// rewrite. They were excluded before for a good reason — the bundle
        /// did not contain them, so deleting them was a one-way door. The
        /// 2026-08-06 bundle contains both, so deleting them is now the only
        /// way to guarantee the player ends up with the versions we shipped
        /// rather than whatever was sitting there. Every one of these four is
        /// a file Holdline itself installed and can re-download in full.
        ///
        /// The player's own mods, saves, ESPs and any other SKSE plugin are
        /// never touched. That line is the whole safety story of this button:
        /// a repair tool that wipes things it did not install is a tool that
        /// destroys strangers' setups.
        ///
        /// ORDERING NOTE, and it matters: this runs AFTER
        /// RepairMisplacedSksePlugins. That method rescues EngineFixes files
        /// which the player may have sitting inside Data\Platform\Plugins by
        /// mistake, moving them to the folder they belong in. Delete the
        /// Platform folder first and those files go with it — taking a mod we
        /// do not own and cannot reinstall. Do not reorder these.
        /// </summary>
        private static async Task RebuildClientFilesAsync(
            string dataDir,
            RepairReport report,
            IProgress<string> progress)
        {
            // Declared out here on purpose: the catch at the bottom needs to
            // be able to put the player's game back too. An unexpected
            // exception is exactly the case where leaving a wiped install
            // behind would be worst.
            var parked = new List<string>();
            var parkedFiles = new List<string>();

            try
            {
                // PICK UP AFTER AN INTERRUPTED RUN, FIRST.
                //
                //  If a previous repair was killed mid-flight -- power cut,
                //  Task Manager, a crash -- the player's real game can be
                //  sitting on the disk as Platform.holdline-bak with no
                //  Platform folder at all. Nothing below would have looked at
                //  it: the wipe skips a folder that is already gone, so the
                //  backup would sit there dead forever while every future run
                //  downloaded 300 MB over the top of nothing.
                //
                //  It runs BEFORE GetOrCreateProfileId on purpose. The only
                //  copy of an old install's profileId can be inside
                //  Platform\Plugins\skymp5-client-settings.txt, and on this
                //  machine that file is inside the backup. Restoring first is
                //  what lets the next few lines find it.
                AdoptOrphanedParkedFolders(dataDir, report);

                // CLAIM THE PLAYER'S CHARACTER BEFORE DELETING ANYTHING.
                //
                //  profileId IS the character on an offline-mode server. It
                //  normally lives in %AppData%\Holdline\player-id.txt, which
                //  this wipe cannot touch -- but on an install that predates
                //  that file, the only surviving copy of the number is inside
                //  Data\Platform\Plugins\skymp5-client-settings.txt, which
                //  this wipe deletes.
                //
                //  GetOrCreateProfileId reads that settings file and writes
                //  the id out to player-id.txt permanently. Calling it here,
                //  one line before the delete, is the difference between a
                //  repair and a player losing the character they built.
                //  PlayerIdentity's own docs say to pass this path BEFORE
                //  anything can overwrite the file. This is that moment.
                try
                {
                    PlayerIdentity.GetOrCreateProfileId(
                        Path.Combine(dataDir, "Platform", "Plugins", "skymp5-client-settings.txt"));
                }
                catch
                {
                    // Never let identity bookkeeping abort a repair. Worst
                    // case the player gets a fresh id, which is what would
                    // have happened without this call anyway.
                }

                progress.Report("Clearing out Holdline's old game files...");

                var removed = WipeOurClientFiles(dataDir, parked);

                // THE ROLLBACK WAS INCOMPLETE UNTIL THIS.
                //
                //  Three files the bundle installs do NOT live inside any of
                //  the folders parked above -- they sit in Data\SKSE\Plugins,
                //  and the install overwrites them where they are:
                //
                //    SkyrimPlatform.dll   MpClientPlugin.dll   NirnLabUIPlugin.dll
                //
                //  So a rollback that only put the folders back left the
                //  player with their OLD skymp5-client.js beside the NEW DLLs.
                //  That is precisely the half-refreshed state this file warns
                //  about everywhere else, and it would be the launcher that
                //  created it. It did not bite tonight only because the bundle
                //  had been built from this very disk and the DLLs were
                //  byte-identical -- luck, not design.
                //
                //  Copied rather than moved: the install expects to overwrite
                //  them in place, and a DLL missing mid-install is a different
                //  failure. The copies cost a few megabytes for a few seconds.
                ParkFiles(parkedFiles, new[]
                {
                    Path.Combine(dataDir, "SKSE", "Plugins", "SkyrimPlatform.dll"),
                    Path.Combine(dataDir, "SKSE", "Plugins", "MpClientPlugin.dll"),
                    Path.Combine(dataDir, "SKSE", "Plugins", "NirnLabUIPlugin.dll")
                });

                progress.Report("Downloading Holdline's game files...");

                // With our files gone, EnsureInstalledAsync can no longer take
                // its "already installed" shortcut, so this is a genuine full
                // download and unpack every time.
                //
                //  Wiping first also settles a question this code cannot
                //  otherwise answer: whether the install step overwrites files
                //  that are already present. If nothing is present, it does not
                //  matter. The player gets the shipped bytes either way.
                if (Directory.Exists(Path.Combine(dataDir, "Platform")))
                {
                    //  Could not move the old folder aside. Force the installer to
                    //  do a full copy over the top of it (file-by-file overwrite
                    //  works where a folder rename did not).
                    //
                    //  4 Oct 2026: this used to DELETE the version marker, which
                    //  does the opposite - ClientFiles.AreInstalled treats a
                    //  missing marker as "installed", writes a fresh one and
                    //  skips the download (that is how Besnik stayed on v358).
                    //  A marker that does not match ClientFilesVersion is what
                    //  makes AreInstalled return false; a real install writes the
                    //  right one back at the end (TryWriteMarker), so Join does
                    //  not re-download afterwards.
                    var forced = false;
                    try
                    {
                        var marker = Path.Combine(dataDir, "Platform", "holdline-client-version.txt");
                        File.WriteAllText(marker, "reinstall-pending");
                        forced = true;
                    }
                    catch (Exception exMk)
                    {
                        report.Blocking.Add(
                            "Holdline could not mark its old game files for replacement (" + exMk.Message + "). " +
                            "Close Skyrim and any File Explorer window open inside the Skyrim folder, then press Fix my game again.");
                    }
                    if (forced)
                    {
                        report.Fixed.Add(
                            "Holdline's old Platform folder could not be moved aside (" +
                            string.Join("; ", WipeFailures) + "), so the fresh files were written over it instead.");
                    }
                }

                var error = await ClientFiles.EnsureInstalledAsync(dataDir, progress);

                if (error != null)
                {
                    // The old version of this message told the player their
                    // game was now broken until the download worked. That was
                    // true, and it was the bug. The files are parked, so put
                    // them back and say so.
                    RestoreParkedFolders(dataDir, parked);
                    RestoreParkedFiles(parkedFiles);

                    report.Blocking.Add(
                        error +
                        "\n\nHoldline put your old game files back, so you're no worse off " +
                        "than before you clicked. Check your internet connection and run " +
                        "Fix my game again.");

                    return;
                }

                // THE EMPTY-FOLDER TRAP. SkyrimPlatform watches TWO folders,
                // hardcoded: Data\Platform\Plugins and Data\Platform\PluginsDev.
                // If either is missing its DirectoryMonitor throws "failed with
                // code 2" (folder not found), the JavaScript engine dies during
                // startup, and the client never reaches the point of connecting
                // -- the player lands on the main menu with no error on screen.
                //
                // The current bundle does carry a PluginsDev entry, so this is
                // belt and braces rather than the load-bearing fix it once was.
                // It stays because creating a folder that already exists costs
                // nothing, and because a future rebuild of the zip could drop
                // the empty folder again without anyone noticing.
                EnsurePlatformWatchFolders(dataDir);

                //  Never claim success on a file that did not change: read the
                //  version stamp of the plugin that is actually in the game folder
                //  and put it in the report, so a stale install is visible at once.
                try
                {
                    var adminJs = Path.Combine(dataDir, "Platform", "Plugins", "z-holdline-admin.js");
                    if (File.Exists(adminJs))
                    {
                        var stamp = System.Text.RegularExpressions.Regex.Match(
                            File.ReadAllText(adminJs), "var VERSION = \"(\\d+)\"");
                        report.Fixed.Add("Holdline plugin now in the game folder: v" +
                            (stamp.Success ? stamp.Groups[1].Value : "unknown") +
                            " (" + new FileInfo(adminJs).Length + " bytes).");
                        //  10 Oct 2026: the Alien mod rides along on every install.
                        var alienBuild = AlienFiles.InstalledBuild(dataDir);
                        report.Fine.Add(alienBuild != null
                            ? "AvP Alien mod in the game folder: build " + alienBuild + "."
                            : "AvP Alien mod: not installed by Holdline (couldn't reach GitHub, or not published yet).");
                    }
                    else
                    {
                        report.Blocking.Add(
                            "After installing, the Holdline plugin is still missing from Platform\\Plugins. " +
                            "Windows is blocking the copy - right-click Holdline, choose \"Run as administrator\", " +
                            "and press Fix my game again.");
                    }
                }
                catch
                {
                }

                // Never claim success on the strength of having tried. Ask
                // afterwards whether the files are actually there.
                //
                //  There is no rescue path below this any more. There is
                //  nothing left to rescue FROM: the download is the only
                //  approved source of these files, so if it did not put them
                //  in place, the honest answer is to say so. The old code
                //  reached for a backup or a disk search at this point and
                //  could report "your install is exactly as it was" while
                //  quietly restoring the broken files the player clicked the
                //  button to get rid of.
                if (!ClientFiles.AreInstalled(dataDir))
                {
                    RestoreParkedFolders(dataDir, parked);
                    RestoreParkedFiles(parkedFiles);

                    report.Blocking.Add(
                        "Holdline downloaded and installed its game files, but they aren't " +
                        "all in place afterwards.\n\n" +
                        "That's almost always Windows, OneDrive or antivirus blocking the " +
                        "copy. Close Holdline, right-click its icon, choose \"Run as " +
                        "administrator\", and run Fix my game again.");

                    return;
                }

                // THE BROWSER, CHECKED SEPARATELY AND ON PURPOSE.
                //
                //  ClientFiles.AreInstalled above does not look at the browser
                //  at all. It checks MpClientPlugin.dll, SkyrimPlatform.dll and
                //  skymp5-client.js -- and until 8/7 nothing anywhere checked
                //  the browser, so an install could be missing the entire thing
                //  and every check still said healthy. The game then died on
                //  load with nothing on screen to explain it.
                //
                //  Two files, because they live in two different places and
                //  either can be present without the other:
                //    - NirnLabUIPlugin.dll  -> Data\SKSE\Plugins
                //    - CEFSubprocess.exe    -> Data\NirnLabUIPlatform
                //  That second folder sits BESIDE Data\Platform rather than
                //  inside it, which is exactly why the wipe used to miss it.
                //
                //  This is a report, not a rescue. The download is the only
                //  approved source for these files, so if it did not put them
                //  there the honest answer is to say so.
                var missingBrowser = new List<string>();
                var missingBrowserPaths = new List<string>();

                try
                {
                    var browserPlugin = Path.Combine(
                        dataDir, "SKSE", "Plugins", "NirnLabUIPlugin.dll");

                    var browserRuntime = Path.Combine(
                        dataDir, "NirnLabUIPlatform", "CEFSubprocess.exe");

                    if (!File.Exists(browserPlugin))
                    {
                        missingBrowser.Add("the browser plugin");
                        missingBrowserPaths.Add("Data\\SKSE\\Plugins\\NirnLabUIPlugin.dll");
                    }

                    if (!File.Exists(browserRuntime))
                    {
                        missingBrowser.Add("the browser runtime");
                        missingBrowserPaths.Add("Data\\NirnLabUIPlatform\\CEFSubprocess.exe");
                    }
                }
                catch
                {
                    // If we cannot even look, say nothing. Claiming a file is
                    // missing on the strength of a failed check would send a
                    // player chasing a fault nobody has seen.
                }

                if (missingBrowser.Count > 0)
                {
                    RestoreParkedFolders(dataDir, parked);
                    RestoreParkedFiles(parkedFiles);

                    report.Blocking.Add(
                        "Holdline's game files installed, but " +
                        string.Join(" and ", missingBrowser) +
                        (missingBrowser.Count == 1 ? " isn't" : " aren't") +
                        " there afterwards.\n\n" +
                        "Missing: " + string.Join(", ", missingBrowserPaths) + "\n\n" +
                        "Skyrim draws chat and menus with these, and the game can't " +
                        "run without them, so your old game files have been put back " +
                        "and nothing is broken.\n\n" +
                        "The usual cause is antivirus: one of those files is a .exe " +
                        "that gets unpacked from a download, which is exactly what " +
                        "antivirus removes without asking. Check your antivirus " +
                        "history for CEFSubprocess.exe, allow it, then run Fix my " +
                        "game again. If that isn't it, close Holdline, right-click " +
                        "its icon, choose \"Run as administrator\", and try once more.");

                    return;
                }

                // VERIFIED PAST THIS LINE, AND ONLY HERE. The install has
                // its required files and its browser. Carry the player's own
                // scripts across, then let the old game go.
                RestorePlayerScripts(dataDir, parked, report);

                DiscardParkedFolders(dataDir, parked);

                DiscardParkedFiles(parkedFiles);

                // EMPTY THE LIST. Without this, an exception thrown anywhere
                // below would send the catch into RestoreParkedFolders, and
                // if one backup had failed to delete while the others
                // succeeded, that surviving backup would be moved back OVER
                // a freshly installed, verified folder -- undoing a repair
                // that had already worked.
                parked.Clear();
                parkedFiles.Clear();

                report.Fixed.Add(removed
                    ? "Replaced Holdline's game files with a fresh download."
                    : "Downloaded and installed Holdline's game files from scratch.");
            }
            catch (Exception ex)
            {
                RestoreParkedFolders(dataDir, parked);
                RestoreParkedFiles(parkedFiles);

                report.Blocking.Add(
                    "Couldn't rebuild Holdline's game files: " + ex.Message +
                    "\n\nYour old game files were put back, so you're no worse off than " +
                    "before you clicked.");
            }
        }

        /// <summary>
        /// Reads the file version off SkyrimPlatform.dll and says whether it
        /// is the one the client demands. Deliberately never blocks the
        /// repair: an unreadable version resource is not proof of a bad DLL,
        /// so an unknown reads as unknown rather than as broken.
        /// </summary>
        private static void CheckPlatformVersion(string dataDir, RepairReport report)
        {
            const string wanted = "2.9.0";

            try
            {
                var dll = Path.Combine(
                    dataDir, "SKSE", "Plugins", "SkyrimPlatform.dll");

                if (!File.Exists(dll))
                {
                    return;
                }

                var info = FileVersionInfo.GetVersionInfo(dll);

                var found = string.IsNullOrWhiteSpace(info.FileVersion)
                    ? null
                    : info.FileVersion.Trim();

                if (found == null)
                {
                    report.Fine.Add(
                        "The scripting plugin doesn't say what version it is. " +
                        "That's not necessarily wrong, but if the game drops you " +
                        "to the main menu it's the first thing to replace.");

                    return;
                }

                if (found.StartsWith(wanted, StringComparison.Ordinal))
                {
                    report.Fine.Add(
                        "The scripting plugin is version " + found + ", which is what Holdline needs.");

                    return;
                }

                // Deliberately a note, not a blocking problem. Plenty of DLLs
                // carry a placeholder file version like 1.0.0.0 that has
                // nothing to do with the version the code reports at runtime,
                // and crying wolf here would send a player replacing a file
                // that was never wrong. Say what was read, say what it means
                // if the symptom matches, and leave the judgement to the
                // symptom.
                report.Fine.Add(
                    "The scripting plugin's file says version " + found + ", while " +
                    "Holdline expects " + wanted + ". File labels are often just " +
                    "wrong, so this on its own is not a problem. It only matters if " +
                    "the game drops you to the main menu right after Join, and in " +
                    "that case Skyrim shows you a message box naming the version.");
            }
            catch
            {
                // Version resources are optional metadata. Failing to read one
                // tells us nothing, so it says nothing.
            }
        }

        /// <summary>
        /// Opens skyrim-platform.log from the last session, finds the reason
        /// the client died, fixes it where it can be fixed, and says it in
        /// plain words where it cannot.
        ///
        /// The log is written by SkyrimPlatform itself and lives under the
        /// player's Documents folder -- which on a machine with OneDrive is
        /// redirected into OneDrive, so both locations are tried.
        ///
        /// Skyrim may still hold the file open, hence FileShare on everything.
        /// A log we cannot read is never a reason to fail the repair.
        /// </summary>
        private static void ReadPlatformLog(RepairReport report, IProgress<string> progress)
        {
            try
            {
                progress.Report("Reading the log from your last launch...");

                var candidates = new List<string>();

                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                if (!string.IsNullOrEmpty(docs))
                {
                    candidates.Add(Path.Combine(docs, "My Games", "Skyrim Special Edition",
                                                "SKSE", "skyrim-platform.log"));
                }

                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                if (!string.IsNullOrEmpty(profile))
                {
                    // The plain Documents folder, for a machine where OneDrive
                    // redirection is off, and the OneDrive one for a machine
                    // where GetFolderPath somehow returns the plain path.
                    candidates.Add(Path.Combine(profile, "Documents", "My Games",
                                                "Skyrim Special Edition", "SKSE",
                                                "skyrim-platform.log"));
                    candidates.Add(Path.Combine(profile, "OneDrive", "Documents", "My Games",
                                                "Skyrim Special Edition", "SKSE",
                                                "skyrim-platform.log"));
                }

                string? logPath = null;

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        logPath = candidate;
                        break;
                    }
                }

                if (logPath == null)
                {
                    // No log at all means SkyrimPlatform never ran -- which is
                    // itself worth knowing, because it points at SKSE rather
                    // than at anything Holdline installed.
                    report.Fine.Add(
                        "No scripting log was found. That usually means the game was " +
                        "launched without SKSE, so Holdline never loaded at all.");

                    return;
                }

                SaveLogCopyToDesktop(logPath, report);

                string text;

                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    text = reader.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                var lines = text.Replace("\r\n", "\n").Split('\n');

                // Only the last session is interesting. 400 lines is well past
                // the startup sequence where all the known killers land.
                var start = Math.Max(0, lines.Length - 400);

                var tail = new List<string>();

                for (var i = start; i < lines.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(lines[i]))
                    {
                        tail.Add(lines[i].Trim());
                    }
                }

                var found = false;

                // ---- KILLER 1: a missing watch folder ----
                //
                //  SkyrimPlatform monitors Data\Platform\Plugins and
                //  Data\Platform\PluginsDev, both hardcoded. A missing one
                //  throws "failed with code 2", the JavaScript engine dies
                //  during startup, and the player reaches the main menu with
                //  nothing on screen. The rebuild already recreates both, so by
                //  the time this runs the fix is in -- say so, because
                //  otherwise the player reads a fatal error in their log and
                //  assumes they are still broken.
                foreach (var line in tail)
                {
                    if (line.IndexOf("DirectoryMonitor", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        line.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        report.Fixed.Add(
                            "Your last launch died because a folder Holdline needs was " +
                            "missing. It has been recreated.");

                        found = true;
                        break;
                    }
                }

                // ---- KILLER 2: the client dying on server messages ----
                //
                //  "invalid UTF-8 byte" out of the JSON layer is what a
                //  mismatched client bundle looks like from the log: the
                //  client cannot unpack what the server sends it, throws on
                //  every tick, and never leaves the main menu. This cost a
                //  full week of nights before it was traced to a patched
                //  skymp5-client.js that no longer matched the server.
                //
                //  The rebuild above replaces that bundle with the shipped
                //  one, so by the time this line is read the cause is already
                //  gone. Name it anyway: a player who saw the symptom deserves
                //  to know what it was.
                if (!found)
                {
                    foreach (var line in tail)
                    {
                        if (line.IndexOf("invalid UTF-8", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            line.IndexOf("Invalid UTF-8", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            report.Fixed.Add(
                                "Your last launch couldn't read what the server was sending, " +
                                "which is why the game sat on the main menu. Holdline's game " +
                                "files have been replaced with the current ones, which fixes it.");

                            found = true;
                            break;
                        }
                    }
                }

                // ---- KILLER 3: the version check ----
                //
                //  The client demands SkyrimPlatform 2.9.0 exactly and quits to
                //  the main menu on anything else. Repeat the log's own line so
                //  the number is visible rather than described.
                if (!found)
                {
                    foreach (var line in tail)
                    {
                        if (line.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (line.IndexOf("mismatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             line.IndexOf("expected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             line.IndexOf("incompatible", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            report.Blocking.Add(
                                "Your last launch was stopped by a version check:\n\n" + line);

                            found = true;
                            break;
                        }
                    }
                }

                // ---- ANYTHING ELSE ----
                //
                //  The three above are the failures we have seen. There will be
                //  others. Rather than reporting a clean bill of health on a
                //  machine whose log is full of errors, hand the player the
                //  last error line verbatim. A line they can read beats a
                //  reassurance that does not match what happened to them.
                if (!found)
                {
                    string? lastError = null;

                    foreach (var line in tail)
                    {
                        if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            line.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            line.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            line.IndexOf("fatal", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            lastError = line;
                        }
                    }

                    if (lastError != null)
                    {
                        report.Blocking.Add(
                            "Your last launch logged this error, and it is most likely why " +
                            "the game did not connect:\n\n" + lastError);
                    }
                }
            }
            catch (Exception ex)
            {
                report.Fine.Add("Couldn't read the scripting log: " + ex.Message);
            }
        }

        /// <summary>
        /// Creates the two folders SkyrimPlatform monitors, if they are not
        /// already there. Both names are hardcoded in the platform itself
        /// (Settings.h: "Data/Platform/Plugins;Data/Platform/PluginsDev"),
        /// and a missing one is fatal at startup rather than degraded.
        /// Creating a folder that already exists is a no-op, so this is safe
        /// to run on every repair.
        /// </summary>
        private static void EnsurePlatformWatchFolders(string dataDir)
        {
            foreach (var folderName in new[] { "Plugins", "PluginsDev" })
            {
                try
                {
                    Directory.CreateDirectory(
                        Path.Combine(dataDir, "Platform", folderName));
                }
                catch
                {
                    // If we cannot create it, the install verify below will
                    // fail loudly anyway. Nothing useful to say here.
                }
            }
        }

        /// <summary>
        /// Removes the files Holdline installed and the download can put back
        /// in full. Returns true if anything was actually there to remove.
        ///
        /// Failures are swallowed on purpose: if a file is locked we would
        /// rather let the install overwrite it than abandon the whole repair.
        ///
        /// Only the Platform folders are removed. The two plugin DLLs are
        /// left in place and overwritten by the install instead — see the
        /// note at the bottom of this method for why.
        ///
        /// Wiping Platform still matters even though the copy overwrites:
        /// overwriting only replaces files the bundle contains, so anything
        /// left behind by an older or a different SkyMP client would survive
        /// forever. Deleting the folder outright is what makes this a genuine
        /// from-scratch install rather than a merge.
        /// </summary>
        private const string ParkedSuffix = ".holdline-bak";

        //  3 Oct 2026, from Besnik's report: the rename of Data\Platform threw
        //  (his Skyrim lives under Program Files), the exception was swallowed,
        //  the old folder stayed, and the install step then saw its version
        //  marker and skipped - while the report said "installed from scratch".
        //  The wipe now records every folder it could not move.
        private static readonly List<string> WipeFailures = new List<string>();

        private static bool WipeOurClientFiles(string dataDir, List<string> parked)
        {
            var removedSomething = false;
            WipeFailures.Clear();

            // NirnLabUIPlatform is the browser runtime (CEFSubprocess.exe,
            // libcef.dll, and its .pak/.bin resources). It sits BESIDE
            // Data\Platform, not inside it, which is why it survived every
            // repair until now while the copy of libcef.dll inside
            // Data\Platform\Distribution\RuntimeDependencies got replaced
            // clean on the same run. Half the runtime refreshed and half of it
            // left over from an older or hand-copied install is exactly what
            // makes CEF fail-fast with 0xc0000409 as the game loads.
            //
            // The bundle ships this folder in full, so deleting it is safe
            // for the same reason deleting Platform is: the download puts it
            // back seconds later.
            // NOTHING IS DELETED HERE ANY MORE. IT IS RENAMED ASIDE.
            //
            // 9 August 2026: this function, as a Directory.Delete, destroyed
            // a known-good install on the developer's own machine. The
            // download that was supposed to replace the files put back an
            // older bundle, the game loaded to a blank white screen, and the
            // only thing that saved the install was a personal backup folder
            // that happened to be sitting on the Desktop. A player would
            // have had nothing, and the button that broke their game would
            // have told them it was fixed.
            //
            // A rename costs the same as a delete and takes the same amount
            // of time (both are a directory-table operation, no bytes move),
            // and it makes that outcome impossible: every failure path below
            // now has somewhere to put the player's game back from.
            //
            // If the rename cannot happen -- locked file, open handle -- the
            // folder is LEFT ALONE rather than force-deleted. The install
            // overwrites what it can, which is strictly better than a
            // half-emptied folder with no way back.
            foreach (var folderName in new[] { "Platform", "Platform_off", "Platform-off", "NirnLabUIPlatform" })
            {
                try
                {
                    var folder = Path.Combine(dataDir, folderName);

                    if (!Directory.Exists(folder))
                    {
                        continue;
                    }

                    var backup = folder + ParkedSuffix;

                    // A backup left behind by an earlier interrupted run is
                    // stale by definition -- the folder it was protecting is
                    // right here. Clear it so the rename has room.
                    if (Directory.Exists(backup))
                    {
                        Directory.Delete(backup, true);
                    }

                    Directory.Move(folder, backup);

                    parked.Add(folderName);
                    removedSomething = true;
                }
                catch (Exception ex)
                {
                    // Locked, in use, or Program Files. Recorded; the rebuild
                    // below removes the version marker so the install cannot
                    // skip, and overwrites file by file instead.
                    WipeFailures.Add(folderName + " (" + ex.Message + ")");
                }
            }

            // THE TWO PLUGIN DLLs ARE DELIBERATELY NOT DELETED, and the
            // reason changed with this rewrite even though the behaviour did
            // not.
            //
            // The old reason was that the bundle did not contain them, so
            // deleting them was a one-way door. That is no longer true — the
            // 2026-08-06 bundle ships both.
            //
            // The reason now is that deleting them buys nothing.
            // SkympConnector.CopyClientData copies every file out of the
            // bundle with overwrite: true, so a stale SkyrimPlatform.dll or
            // MpClientPlugin.dll is replaced by the shipped one whether it
            // was deleted first or not. Deleting would produce an identical
            // result when the download succeeds, and a Skyrim folder missing
            // its multiplayer plugins when the download fails.
            //
            // Same outcome on the good path, strictly worse on the bad one.
            // So they stay, and the install overwrites them.

            return removedSomething;
        }

        /// <summary>
        /// Recovers from a repair that never finished. For each folder this
        /// tool parks, if the backup exists and the real folder does not, the
        /// backup IS the player's game -- move it back.
        ///
        /// Deliberately does nothing when both exist: that is the normal
        /// mid-repair state on a run that is about to verify, and the live
        /// folder is the newer one.
        /// </summary>
        private static void AdoptOrphanedParkedFolders(
            string dataDir, RepairReport report)
        {
            var adoptedAny = false;

            foreach (var folderName in new[] { "Platform", "Platform_off", "Platform-off", "NirnLabUIPlatform" })
            {
                try
                {
                    var folder = Path.Combine(dataDir, folderName);
                    var backup = folder + ParkedSuffix;

                    if (!Directory.Exists(backup) || Directory.Exists(folder))
                    {
                        continue;
                    }

                    Directory.Move(backup, folder);

                    adoptedAny = true;
                }
                catch
                {
                    // The rebuild below downloads everything anyway. This is a
                    // rescue, not a requirement.
                }
            }

            // Said once, however many folders were involved.
            if (adoptedAny)
            {
                report.Fixed.Add(
                    "An earlier repair was interrupted and left your game files set " +
                    "aside. Put them back before starting.");
            }
        }

        /// <summary>
        /// Puts the player's game back exactly as it was. Called on every
        /// failure path after the wipe: a failed download, a download that
        /// installed nothing, an install with no browser in it.
        ///
        /// This is the half of the rename-aside change that actually does
        /// the saving. Parking the folders without this would only move the
        /// brick somewhere tidier.
        /// </summary>
        private static void RestoreParkedFolders(string dataDir, List<string> parked)
        {
            foreach (var folderName in parked)
            {
                try
                {
                    var folder = Path.Combine(dataDir, folderName);
                    var backup = folder + ParkedSuffix;

                    if (!Directory.Exists(backup))
                    {
                        continue;
                    }

                    // Whatever the failed install managed to lay down is
                    // partial by definition. The parked copy is a whole
                    // working folder; keep that one.
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                    }

                    Directory.Move(backup, folder);
                }
                catch
                {
                    // Best effort. A folder that will not move back is left
                    // named .holdline-bak, which is at least recoverable by
                    // hand and is named plainly enough to say what it is.
                }
            }
        }

        /// <summary>
        /// Removes the parked copies. Runs ONLY after the install has been
        /// verified -- files present, browser present. Until this is called
        /// the player's original game is still on the disk.
        /// </summary>
        private static void DiscardParkedFolders(string dataDir, List<string> parked)
        {
            foreach (var folderName in parked)
            {
                try
                {
                    var backup = Path.Combine(dataDir, folderName) + ParkedSuffix;

                    if (Directory.Exists(backup))
                    {
                        Directory.Delete(backup, true);
                    }
                }
                catch
                {
                    // Leftover disk usage is not worth a failed repair.
                }
            }
        }

        /// <summary>
        /// Takes a safety copy of individual files the install will overwrite
        /// in place, and remembers which ones it managed to copy.
        ///
        /// Copies, not moves: the install is entitled to find these files
        /// where they are. The copy exists only so a failed install can be
        /// undone completely rather than nearly.
        /// </summary>
        private static void ParkFiles(List<string> parkedFiles, string[] paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    File.Copy(path, path + ParkedSuffix, true);

                    parkedFiles.Add(path);
                }
                catch
                {
                    // A file that cannot be copied aside is simply not
                    // rolled back. Recording it anyway would promise a
                    // restore that cannot happen.
                }
            }
        }

        /// <summary>
        /// Puts the safety copies back over whatever the failed install left.
        /// </summary>
        private static void RestoreParkedFiles(List<string> parkedFiles)
        {
            foreach (var path in parkedFiles)
            {
                try
                {
                    var backup = path + ParkedSuffix;

                    if (!File.Exists(backup))
                    {
                        continue;
                    }

                    File.Copy(backup, path, true);
                    File.Delete(backup);
                }
                catch
                {
                    // Left as a .holdline-bak file beside the original, which
                    // is recoverable by hand and named plainly.
                }
            }
        }

        /// <summary>
        /// Removes the safety copies. Runs only once the install is verified.
        /// </summary>
        private static void DiscardParkedFiles(List<string> parkedFiles)
        {
            foreach (var path in parkedFiles)
            {
                try
                {
                    var backup = path + ParkedSuffix;

                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }
                }
                catch
                {
                    // Leftover disk usage is not worth a failed repair.
                }
            }
        }

        /// <summary>
        /// Carries the player's own scripts back over from the parked copy.
        ///
        /// Data\Platform\Plugins is not only ours. Anything a player or a
        /// hold owner drops in there -- the Holdline admin plugin included --
        /// was deleted on EVERY repair run before this, silently, with no
        /// mention of it in the report and no way to get it back. The admin
        /// plugin IS the admin credential, so on a server owner's machine
        /// that was a repair button that quietly removed their access.
        ///
        /// Only .js files, only ones the fresh install does not already
        /// have, and never overwriting a shipped file.
        /// </summary>
        private static void RestorePlayerScripts(
            string dataDir, List<string> parked, RepairReport report)
        {
            if (!parked.Contains("Platform"))
            {
                return;
            }

            try
            {
                var oldPlugins = Path.Combine(
                    dataDir, "Platform" + ParkedSuffix, "Plugins");

                var newPlugins = Path.Combine(dataDir, "Platform", "Plugins");

                if (!Directory.Exists(oldPlugins) || !Directory.Exists(newPlugins))
                {
                    return;
                }

                var carried = new List<string>();

                foreach (var file in Directory.GetFiles(oldPlugins, "*.js"))
                {
                    var name = Path.GetFileName(file);
                    var target = Path.Combine(newPlugins, name);

                    if (File.Exists(target))
                    {
                        continue;
                    }

                    File.Copy(file, target, false);
                    carried.Add(name);
                }

                if (carried.Count > 0)
                {
                    report.Fixed.Add(
                        "Kept your own script" + (carried.Count == 1 ? "" : "s") +
                        " through the repair: " + string.Join(", ", carried) + ".");
                }
            }
            catch
            {
                // Never fail a repair over this.
            }
        }

        /// <summary>
        /// Asks the processor, in code, whether it can run the chat browser
        /// at all.
        ///
        /// WHY THIS EXISTS: a tester's game dies at CEFSubprocess.exe with
        /// exception 0xc0000409. That code is __fastfail -- a program
        /// deliberately killing itself the instant it finds something
        /// impossible -- and one of the things that produces it is machine
        /// code using a CPU instruction the CPU does not have. Chromium,
        /// which the chat browser is built on, needs SSE3 as an absolute
        /// floor and recent builds lean on SSE4.2 and POPCNT.
        ///
        /// Three days were spent trying to get a processor model out of a
        /// tester over chat. .NET can answer it in four lines. If something
        /// here comes back missing, that is the crash, and no amount of
        /// permissions or reinstalling will ever fix it.
        ///
        /// Stated as a plain fact, not a claim about an exact Chromium
        /// version: what is certain is that a missing instruction set makes
        /// this browser impossible, so knowing costs nothing and settles it.
        /// </summary>
        private static void CheckProcessorSupport(RepairReport report)
        {
            try
            {
                var missing = new List<string>();

                if (!Sse3.IsSupported) { missing.Add("SSE3"); }
                if (!Ssse3.IsSupported) { missing.Add("SSSE3"); }
                if (!Sse41.IsSupported) { missing.Add("SSE4.1"); }
                if (!Sse42.IsSupported) { missing.Add("SSE4.2"); }
                if (!Popcnt.IsSupported) { missing.Add("POPCNT"); }

                if (missing.Count == 0)
                {
                    report.Fine.Add(
                        "Your processor supports everything Skyrim's chat window needs.");

                    return;
                }

                report.Blocking.Add(
                    "Your processor is missing " + string.Join(", ", missing) +
                    ", which the chat window in Skyrim needs to run.\n\n" +
                    "This is the one fault Holdline cannot fix from here — it's the " +
                    "processor itself, not a setting or a missing file. It shows up as " +
                    "Skyrim closing itself as the game loads. Send this report to " +
                    "whoever runs your hold so they know.");
            }
            catch
            {
                // If the check itself fails, say nothing rather than accuse a
                // player's PC of something on the strength of a failed test.
            }
        }

        /// <summary>
        /// Tries to actually write a file in each folder the game needs to
        /// write in, and reports what happened.
        ///
        /// WHY THIS EXISTS: GrantBrowserFolderWriteAccess runs icacls and
        /// nobody ever checked whether it worked. On the machine this was
        /// written for -- Skyrim installed under Program Files, which Windows
        /// protects -- that is the entire question, and the answer has been
        /// assumed for a week.
        ///
        /// Creating and deleting one small file is the only test that
        /// actually answers it, on the real machine, with the real
        /// permissions the game will run under.
        /// </summary>
        private static void CheckFolderWriteAccess(string dataDir, RepairReport report)
        {
            var blocked = new List<string>();
            var checkedAny = false;

            foreach (var relative in new[]
            {
                "NirnLabUIPlatform",
                "Platform",
                Path.Combine("SKSE", "Plugins")
            })
            {
                var folder = Path.Combine(dataDir, relative);

                if (!Directory.Exists(folder))
                {
                    continue;
                }

                checkedAny = true;

                try
                {
                    var probe = Path.Combine(folder, "holdline-write-test.tmp");

                    File.WriteAllText(probe, "holdline");
                    File.Delete(probe);
                }
                catch
                {
                    blocked.Add(relative);
                }
            }

            if (!checkedAny)
            {
                return;
            }

            if (blocked.Count == 0)
            {
                report.Fine.Add(
                    "Skyrim can write to all the folders Holdline needs it to.");

                return;
            }

            report.Blocking.Add(
                "Windows is blocking writes to " + string.Join(" and ", blocked) +
                " inside Skyrim's Data folder.\n\n" +
                "The chat window writes its own working files there as the game " +
                "loads, and Skyrim closes itself when it can't. Close Holdline, " +
                "right-click its icon, choose \"Run as administrator\", and run " +
                "Fix my game again. If it still says this, Skyrim needs to be " +
                "installed somewhere outside Program Files.");
        }

        /// <summary>
        /// Writes everything this run learned to holdline-report.txt on the
        /// Desktop.
        ///
        /// WHY THIS EXISTS: helping a tester has meant three days of asking
        /// one question at a time over chat and getting answers that
        /// contradicted each other. Every fact needed is on their machine and
        /// this launcher is already standing on it. One file they can send in
        /// one message replaces the whole interrogation.
        ///
        /// Never let this fail a repair -- it is a convenience, and the
        /// report on screen is the real output.
        /// </summary>
        private static void WriteDiagnosticReport(
            string root, string dataDir, RepairReport report)
        {
            try
            {
                var desktop = Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory);

                if (string.IsNullOrEmpty(desktop))
                {
                    return;
                }

                var text = new StringBuilder();

                text.AppendLine("HOLDLINE REPORT");
                text.AppendLine("Send this whole file to whoever runs your hold.");
                text.AppendLine();
                text.AppendLine("Written: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                text.AppendLine("Launcher: v" + LauncherUpdate.CurrentVersion);
                text.AppendLine("Game files version wanted: " + ClientFiles.ClientFilesVersion);
                text.AppendLine("Skyrim folder: " + root);
                text.AppendLine();

                text.AppendLine("WINDOWS AND PROCESSOR");
                text.AppendLine("  Windows: " + Environment.OSVersion.VersionString);
                text.AppendLine("  64-bit Windows: " + Environment.Is64BitOperatingSystem);
                text.AppendLine("  Processor: " +
                    (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown"));
                text.AppendLine("  Cores: " + Environment.ProcessorCount);
                text.AppendLine("  SSE3: " + Sse3.IsSupported);
                text.AppendLine("  SSSE3: " + Ssse3.IsSupported);
                text.AppendLine("  SSE4.1: " + Sse41.IsSupported);
                text.AppendLine("  SSE4.2: " + Sse42.IsSupported);
                text.AppendLine("  POPCNT: " + Popcnt.IsSupported);
                text.AppendLine("  AVX: " + Avx.IsSupported);

                // FREE SPACE, because parking the old files instead of
                // deleting them means the old and new copies coexist for the
                // length of the install. Roughly a gigabyte more headroom is
                // needed than the old delete-first version wanted, and a
                // download that dies on a full disk looks identical to a
                // download that dies on a bad connection.
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(root)!);

                    text.AppendLine("  Free space on " + drive.Name + ": " +
                        (drive.AvailableFreeSpace / 1024 / 1024) + " MB");
                }
                catch
                {
                    text.AppendLine("  Free space: could not read");
                }

                text.AppendLine();

                text.AppendLine("KEY FILES");

                foreach (var relative in new[]
                {
                    Path.Combine("Platform", "Plugins", "skymp5-client.js"),
                    Path.Combine("SKSE", "Plugins", "SkyrimPlatform.dll"),
                    Path.Combine("SKSE", "Plugins", "MpClientPlugin.dll"),
                    Path.Combine("SKSE", "Plugins", "NirnLabUIPlugin.dll"),
                    Path.Combine("SKSE", "Plugins", "EngineFixes.dll"),
                    Path.Combine("NirnLabUIPlatform", "CEFSubprocess.exe"),
                    Path.Combine("Platform", "holdline-client-version.txt")
                })
                {
                    var full = Path.Combine(dataDir, relative);

                    try
                    {
                        if (File.Exists(full))
                        {
                            var info = new FileInfo(full);

                            text.AppendLine("  PRESENT  " + relative +
                                "  (" + info.Length + " bytes, " +
                                info.LastWriteTime.ToString("yyyy-MM-dd") + ")");
                        }
                        else
                        {
                            text.AppendLine("  MISSING  " + relative);
                        }
                    }
                    catch
                    {
                        text.AppendLine("  UNREADABLE  " + relative);
                    }
                }

                text.AppendLine();
                text.AppendLine("LEFTOVERS FROM AN INTERRUPTED REPAIR");

                // Any of these existing means a previous run did not finish.
                // Named here because they are invisible from inside the game
                // and they are the first thing to look at when an install
                // behaves like two different versions at once.
                var leftovers = new List<string>();

                foreach (var relative in new[]
                {
                    "Platform" + ParkedSuffix,
                    "Platform_off" + ParkedSuffix,
                    "NirnLabUIPlatform" + ParkedSuffix,
                    Path.Combine("SKSE", "Plugins", "SkyrimPlatform.dll" + ParkedSuffix),
                    Path.Combine("SKSE", "Plugins", "MpClientPlugin.dll" + ParkedSuffix),
                    Path.Combine("SKSE", "Plugins", "NirnLabUIPlugin.dll" + ParkedSuffix),
                    Path.Combine("SKSE", "Plugins", "EngineFixes.dll.disabled"),
                    Path.Combine("SKSE", "Plugins", "EngineFixes.dll.holdline-off")
                })
                {
                    var full = Path.Combine(dataDir, relative);

                    try
                    {
                        if (File.Exists(full) || Directory.Exists(full))
                        {
                            leftovers.Add(relative);
                        }
                    }
                    catch
                    {
                        // Skip anything unreadable rather than claim it.
                    }
                }

                try
                {
                    if (File.Exists(Path.Combine(root, "d3dx9_42.dll.holdline-off")))
                    {
                        leftovers.Add("d3dx9_42.dll.holdline-off (Skyrim folder)");
                    }
                }
                catch
                {
                    // Same.
                }

                if (leftovers.Count == 0)
                {
                    text.AppendLine("  none");
                }
                else
                {
                    foreach (var item in leftovers)
                    {
                        text.AppendLine("  " + item);
                    }
                }

                text.AppendLine();
                text.AppendLine("WHAT THE CHECK FOUND");

                foreach (var line in report.Fixed)
                {
                    text.AppendLine("  FIXED: " + line.Replace("\n", " "));
                }

                foreach (var line in report.Blocking)
                {
                    text.AppendLine("  PROBLEM: " + line.Replace("\n", " "));
                }

                foreach (var line in report.Fine)
                {
                    text.AppendLine("  OK: " + line.Replace("\n", " "));
                }

                text.AppendLine();

                // Only say the log is there if it is there. ReadPlatformLog
                // writes holdline-log.txt only when it actually found a log,
                // and telling someone to send a file that does not exist is
                // how a rescue turns back into an interrogation.
                var logCopy = Path.Combine(desktop, "holdline-log.txt");

                text.AppendLine(File.Exists(logCopy)
                    ? "Skyrim's own log was saved beside this file as holdline-log.txt. " +
                      "Send both."
                    : "Skyrim has not written a log on this PC yet, so there is no " +
                      "holdline-log.txt to send. This file is the whole picture.");

                File.WriteAllText(
                    Path.Combine(desktop, "holdline-report.txt"), text.ToString());

                report.Fine.Add(
                    "Put a report on your Desktop as holdline-report.txt. If you need " +
                    "help, send that file — it has everything in it.");
            }
            catch
            {
                // A convenience file is never worth failing a repair over.
            }
        }

        // ============================================================
        //  EXTRA LOG COLLECTION
        //
        //  Everything here COPIES. Nothing is moved, nothing is
        //  deleted. These are the player's files and some of them
        //  are still open by a running game.
        // ============================================================

        /// <summary>
        /// The logs that live in Documents\My Games\...\SKSE.
        /// skyrim-platform.log is ours and ReadPlatformLog already handles it;
        /// it is collected again here so one folder holds the whole picture.
        /// The other two are SKSE's own, they name the plugin that fails and
        /// the reason, and nothing in this launcher has ever collected them.
        /// </summary>
        private static readonly string[] ExtraSkseLogNames =
        {
            "skyrim-platform.log",
            "skse64.log",
            "SKSEPlugins.log",
        };

        /// <summary>
        /// Copies every log worth having into one folder on the Desktop, plus
        /// a manifest listing what was found and what was not.
        ///
        /// The manifest is the point. Without it, a file that was never
        /// written and a file we never looked for are indistinguishable, and
        /// the next hour goes to hunting something that does not exist.
        /// </summary>
        private static void CollectExtraLogsToDesktop(
            string root,
            RepairReport report,
            IProgress<string> progress)
        {
            try
            {
                progress.Report("Collecting your logs...");

                var desktop = ResolveDesktopFolder();

                if (desktop == null)
                {
                    report.Fine.Add(
                        "Couldn't find your Desktop folder, so the log folder wasn't " +
                        "created.");

                    return;
                }

                var outDir = Path.Combine(desktop, "Holdline-Logs");

                // Anything already in there is from an earlier run. Noted
                // BEFORE we copy, so a file we fail to find today is never
                // confused with a stale copy of the same name.
                var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (Directory.Exists(outDir))
                {
                    try
                    {
                        foreach (var existing in Directory.GetFiles(outDir))
                        {
                            stale.Add(Path.GetFileName(existing));
                        }
                    }
                    catch
                    {
                        // An unreadable folder is not a reason to skip the copy.
                    }
                }

                Directory.CreateDirectory(outDir);

                var gameFolders = FindMyGamesFolders();
                var localFolders = FindLocalSkyrimFolders();

                var targets = new List<KeyValuePair<string, string>>();

                foreach (var myGames in gameFolders)
                {
                    var skse = Path.Combine(myGames, "SKSE");

                    foreach (var name in ExtraSkseLogNames)
                    {
                        targets.Add(new KeyValuePair<string, string>(
                            Path.Combine(skse, name), name));
                    }

                    // The two INIs: a setting pointing the game at something
                    // broken shows up here and nowhere else.
                    targets.Add(new KeyValuePair<string, string>(
                        Path.Combine(myGames, "Skyrim.ini"), "Skyrim.ini"));

                    targets.Add(new KeyValuePair<string, string>(
                        Path.Combine(myGames, "SkyrimPrefs.ini"), "SkyrimPrefs.ini"));

                    // Crash Logger drops its reports in a subfolder. Newest
                    // three only -- older ones are unrelated sessions.
                    AddNewestFiles(targets, Path.Combine(skse, "Crashlogs"), 3);
                }

                // The crash we are chasing is inside Skyrim's own code, so
                // NetScriptFramework's crash folder is worth a look too. It
                // lives in the Skyrim folder rather than in Documents.
                AddNewestFiles(
                    targets, Path.Combine(root, "NetScriptFramework", "Crash"), 3);

                foreach (var localFolder in localFolders)
                {
                    // The load order names every mod the game tries to load
                    // during the exact stage where the crash lands.
                    targets.Add(new KeyValuePair<string, string>(
                        Path.Combine(localFolder, "plugins.txt"), "plugins.txt"));

                    targets.Add(new KeyValuePair<string, string>(
                        Path.Combine(localFolder, "loadorder.txt"), "loadorder.txt"));
                }

                var manifest = new StringBuilder();

                manifest.AppendLine("HOLDLINE LOG COLLECTION");
                manifest.AppendLine(
                    "Run at: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                manifest.AppendLine("Skyrim folder: " + root);
                manifest.AppendLine();
                manifest.AppendLine("FOLDERS SEARCHED");

                if (gameFolders.Count == 0 && localFolders.Count == 0)
                {
                    manifest.AppendLine(
                        "   none - no Skyrim settings folders were found on this PC");
                }
                else
                {
                    foreach (var folder in gameFolders)
                    {
                        manifest.AppendLine("   " + folder);
                    }

                    foreach (var folder in localFolders)
                    {
                        manifest.AppendLine("   " + folder);
                    }
                }

                manifest.AppendLine();
                manifest.AppendLine("FILES");

                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var copied = 0;
                var missing = 0;

                foreach (var target in targets)
                {
                    var source = target.Key;
                    var destName = target.Value;

                    if (!File.Exists(source))
                    {
                        missing++;

                        manifest.AppendLine("   NOT FOUND        " + destName);
                        manifest.AppendLine("                    looked in " + source);

                        if (stale.Contains(destName))
                        {
                            manifest.AppendLine(
                                "                    WARNING: a file of this name from an " +
                                "earlier run is still in this folder - it is NOT from today");
                        }

                        continue;
                    }

                    // Two candidate folders can both hold a file of the same
                    // name. Keep both rather than letting the second silently
                    // overwrite the first.
                    var finalName = destName;
                    var attempt = 2;

                    while (used.Contains(finalName))
                    {
                        finalName = Path.GetFileNameWithoutExtension(destName) +
                                    "-" + attempt +
                                    Path.GetExtension(destName);

                        attempt++;
                    }

                    try
                    {
                        var info = new FileInfo(source);

                        CopyEvenIfOpen(source, Path.Combine(outDir, finalName));

                        used.Add(finalName);
                        copied++;

                        manifest.AppendLine(
                            "   COPIED           " + finalName +
                            "   " + info.Length.ToString("N0") + " bytes" +
                            "   last written " +
                            info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));

                        manifest.AppendLine("                    from " + source);
                    }
                    catch (Exception ex)
                    {
                        missing++;

                        manifest.AppendLine(
                            "   COULD NOT COPY   " + destName + "   (" + ex.Message + ")");

                        manifest.AppendLine("                    from " + source);
                    }
                }

                manifest.AppendLine();
                manifest.AppendLine(
                    "SUMMARY: " + copied + " copied, " + missing + " not found.");
                manifest.AppendLine();
                manifest.AppendLine(
                    "A file listed as NOT FOUND was never written on this PC. That is a " +
                    "real answer, not a gap - do not go looking for it.");

                try
                {
                    File.WriteAllText(
                        Path.Combine(outDir, "holdline-collected.txt"),
                        manifest.ToString());
                }
                catch
                {
                    // The copies are what matter. A missing manifest is a
                    // nuisance, not a failure.
                }

                report.Fine.Add(
                    "Copied your logs into a folder called Holdline-Logs on your Desktop " +
                    "(" + copied + " found, " + missing + " not on this PC). Right-click " +
                    "that folder, choose \"Send to\" then \"Compressed (zipped) folder\", " +
                    "and send the zip.");
            }
            catch (Exception ex)
            {
                report.Fine.Add("Couldn't collect your logs: " + ex.Message);
            }
        }

        /// <summary>
        /// Adds the newest few files from a folder, if the folder exists at
        /// all. Used for crash reports, where the folder is usually absent and
        /// its absence is itself informative.
        /// </summary>
        private static void AddNewestFiles(
            List<KeyValuePair<string, string>> targets,
            string folder,
            int howMany)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                var files = new List<FileInfo>();

                foreach (var path in Directory.GetFiles(folder))
                {
                    files.Add(new FileInfo(path));
                }

                files.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));

                for (var i = 0; i < files.Count && i < howMany; i++)
                {
                    targets.Add(new KeyValuePair<string, string>(
                        files[i].FullName, files[i].Name));
                }
            }
            catch
            {
                // A crash folder we cannot read is not worth failing over.
            }
        }

        /// <summary>
        /// Copies a file that may still be open by a running game. File.Copy
        /// throws a sharing violation on an open log, which would have made
        /// this whole step fail on exactly the machine it exists to help.
        /// </summary>
        private static void CopyEvenIfOpen(string source, string destination)
        {
            using var input = new FileStream(
                source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var output = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None);

            input.CopyTo(output);
        }

        /// <summary>
        /// The player's Desktop. Tried three ways because OneDrive redirection
        /// means the obvious answer is wrong on some machines and right on
        /// others -- the same trap ReadPlatformLog already works around.
        /// </summary>
        private static string? ResolveDesktopFolder()
        {
            var candidates = new List<string>();

            var desktop = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);

            if (!string.IsNullOrEmpty(desktop))
            {
                candidates.Add(desktop);
            }

            var profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrEmpty(profile))
            {
                candidates.Add(Path.Combine(profile, "OneDrive", "Desktop"));
                candidates.Add(Path.Combine(profile, "Desktop"));
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    if (Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Try the next one.
                }
            }

            return null;
        }

        /// <summary>
        /// Every Documents\My Games\Skyrim Special Edition folder on this PC.
        /// More than one can be real: OneDrive redirection leaves a stale copy
        /// behind, and the game can be launching against the folder we are NOT
        /// reading.
        /// </summary>
        private static List<string> FindMyGamesFolders()
        {
            var roots = new List<string>();

            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (!string.IsNullOrEmpty(docs))
            {
                roots.Add(docs);
            }

            var profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrEmpty(profile))
            {
                roots.Add(Path.Combine(profile, "Documents"));
                roots.Add(Path.Combine(profile, "OneDrive", "Documents"));
            }

            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var docsRoot in roots)
            {
                foreach (var name in new[]
                         {
                             "Skyrim Special Edition",
                             "Skyrim Special Edition GOG",
                         })
                {
                    try
                    {
                        var folder = Path.Combine(docsRoot, "My Games", name);

                        if (Directory.Exists(folder) && seen.Add(folder))
                        {
                            found.Add(folder);
                        }
                    }
                    catch
                    {
                        // Bad path. Next.
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Where Skyrim keeps the load order: AppData\Local\Skyrim Special
        /// Edition. Enumerated rather than hardcoded, so the GOG and AE folder
        /// variants are picked up without guessing their exact names.
        /// </summary>
        private static List<string> FindLocalSkyrimFolders()
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var roots = new List<string>();

            var local = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrEmpty(local))
            {
                roots.Add(local);
            }

            var profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrEmpty(profile))
            {
                roots.Add(Path.Combine(profile, "AppData", "Local"));
            }

            foreach (var localRoot in roots)
            {
                try
                {
                    if (!Directory.Exists(localRoot))
                    {
                        continue;
                    }

                    foreach (var folder in Directory.GetDirectories(localRoot))
                    {
                        var name = Path.GetFileName(folder);

                        if (name.StartsWith("Skyrim Special Edition",
                                            StringComparison.OrdinalIgnoreCase) &&
                            seen.Add(folder))
                        {
                            found.Add(folder);
                        }
                    }
                }
                catch
                {
                    // Unreadable AppData is not fatal to a repair.
                }
            }

            return found;
        }
    }
}
