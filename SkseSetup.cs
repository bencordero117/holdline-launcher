// ============================================================
//  Holdline — SkseSetup.cs
//
//  Replaces the old SkseInstall helper. Three things the old one
//  got wrong, all of them found the hard way on a tester's machine
//  on 2026-07-31:
//
//   1. It accepted ANY folder containing skse64_loader.exe with no
//      version check at all. The tester had an old skse64_2_02_06
//      folder sitting in Downloads from years ago, pointed at it,
//      and the launcher said yes. Skyrim then died on startup with
//      a plugin error nobody ever saw.
//
//   2. It refused .7z outright — which is the ONLY format SKSE
//      actually ships in. So the happy path was, by construction,
//      impossible. Every single player was told to go install a
//      separate extraction program first.
//
//   3. Its file picker was filtered to *.zip, so the real download
//      was invisible in the dialog.
//
//  This version checks the SKSE runtime DLL against the actual
//  Skyrim executable's version, unpacks .7z using tar.exe (shipped
//  with Windows 10 1803 and later), and reports what it found in
//  plain words.
//
//  We still do NOT host, bundle or redistribute SKSE. Its
//  permissions forbid both redistribution and use in anything sold,
//  and Skyrim Together got publicly burned for exactly that. The
//  player downloads it from silverlock.org themselves. All we do is
//  put files they already have into the right folder.
// ============================================================

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace HoldlineLauncher
{
    /// <summary>
    /// What SKSE looks like on this machine right now. Everything is
    /// nullable because "we don't know" is a real answer that the
    /// settings screen has to be able to show.
    /// </summary>
    public sealed class SkseStatus
    {
        public bool LoaderPresent;
        public string? LoaderPath;

        /// <summary>Runtime tag found in the Skyrim folder, e.g. "1_6_1170".</summary>
        public string? InstalledRuntime;

        /// <summary>Runtime tag the game itself needs, e.g. "1_6_1170".</summary>
        public string? RequiredRuntime;

        /// <summary>True only when loader and matching runtime are both there.</summary>
        public bool Ok => LoaderPresent
                          && InstalledRuntime != null
                          && RequiredRuntime != null
                          && string.Equals(InstalledRuntime, RequiredRuntime,
                                           StringComparison.OrdinalIgnoreCase);

        /// <summary>One line, written for a player, never a developer.</summary>
        public string Summary
        {
            get
            {
                if (!LoaderPresent)
                {
                    return "Not installed yet";
                }

                if (InstalledRuntime == null)
                {
                    return "Installed, but incomplete — missing its main file";
                }

                if (RequiredRuntime == null)
                {
                    return "Installed (version " + Pretty(InstalledRuntime) + ")";
                }

                if (!Ok)
                {
                    return "Wrong version — you have " + Pretty(InstalledRuntime) +
                           ", Skyrim needs " + Pretty(RequiredRuntime);
                }

                return "Installed and correct (for Skyrim " + Pretty(InstalledRuntime) + ")";
            }
        }

        internal static string Pretty(string tag)
        {
            return tag.Replace('_', '.');
        }
    }

    public static class SkseSetup
    {
        public const string DownloadPage = "https://skse.silverlock.org/";

        public const string LoaderName = "skse64_loader.exe";

        // SKSE ships one DLL named for the exact Skyrim build it was made
        // for: skse64_1_6_1170.dll for Anniversary Edition, skse64_1_5_97.dll
        // for the older Special Edition, and so on. That name is the most
        // reliable version marker in the whole archive, far better than the
        // folder name, which the player can rename and often has.
        //
        // skse64_steam_loader.dll also starts with skse64_ but has no digits,
        // so the digit groups below exclude it without needing a special case.
        private static readonly Regex RuntimeDllPattern =
            new(@"^skse64_(\d+)_(\d+)_(\d+)\.dll$", RegexOptions.IgnoreCase);

        // ------------------------------------------------------------
        //  Reading the current state
        // ------------------------------------------------------------

        /// <summary>
        /// The runtime tag this Skyrim needs, read from the executable's own
        /// version resource. Returns null if the version can't be read, which
        /// is treated everywhere below as "don't block the player" rather
        /// than "fail" — a missing version resource is not their fault.
        /// </summary>
        public static string? RequiredRuntimeTag(string? skyrimExePath)
        {
            if (string.IsNullOrWhiteSpace(skyrimExePath) || !File.Exists(skyrimExePath))
            {
                return null;
            }

            try
            {
                var info = FileVersionInfo.GetVersionInfo(skyrimExePath);

                // A real Skyrim reports 1.6.1170.0. A zeroed version resource
                // means we learned nothing, so say so instead of inventing
                // "0_0_0" and then telling the player their SKSE is wrong.
                if (info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0)
                {
                    return null;
                }

                return info.FileMajorPart + "_" + info.FileMinorPart + "_" + info.FileBuildPart;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Finds the runtime tag of whatever SKSE is sitting in a folder.
        /// Top level only: SKSE's DLLs live beside the loader, and searching
        /// deeper would happily pick up a copy buried in some mod's folder.
        /// </summary>
        public static string? RuntimeTagIn(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return null;
            }

            try
            {
                foreach (var file in Directory.GetFiles(folder, "skse64_*.dll", SearchOption.TopDirectoryOnly))
                {
                    var match = RuntimeDllPattern.Match(Path.GetFileName(file));

                    if (match.Success)
                    {
                        return match.Groups[1].Value + "_" +
                               match.Groups[2].Value + "_" +
                               match.Groups[3].Value;
                    }
                }
            }
            catch
            {
                // An unreadable folder is reported as "not found", never a crash.
            }

            return null;
        }

        /// <summary>
        /// Everything the settings screen needs to draw its SKSE row.
        /// Never throws.
        /// </summary>
        public static SkseStatus Inspect(string? skyrimRoot, string? skyrimExePath)
        {
            var status = new SkseStatus
            {
                RequiredRuntime = RequiredRuntimeTag(skyrimExePath)
            };

            if (string.IsNullOrWhiteSpace(skyrimRoot) || !Directory.Exists(skyrimRoot))
            {
                return status;
            }

            var loader = Path.Combine(skyrimRoot, LoaderName);

            if (File.Exists(loader))
            {
                status.LoaderPresent = true;
                status.LoaderPath = loader;
            }

            status.InstalledRuntime = RuntimeTagIn(skyrimRoot);

            return status;
        }

        // ------------------------------------------------------------
        //  Installing
        // ------------------------------------------------------------

        /// <summary>
        /// Installs SKSE from whatever the player points at: the .7z straight
        /// from silverlock.org, a .zip, or a folder they already extracted.
        /// Returns null on success, or a message written for a player.
        ///
        /// skyrimExePath is used only for the version check. Pass null and the
        /// check is skipped rather than failed.
        /// </summary>
        public static string? InstallFrom(string sourcePath, string skyrimRoot, string? skyrimExePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return "No SKSE download was chosen.";
            }

            if (string.IsNullOrWhiteSpace(skyrimRoot) || !Directory.Exists(skyrimRoot))
            {
                return "Holdline doesn't know where Skyrim is yet, so there's nowhere " +
                       "to put SKSE.\n\nSet your Skyrim folder at the top of this window first.";
            }

            if (!ClientFiles.IsWritable(skyrimRoot))
            {
                return "Holdline can't write into your Skyrim folder:\n" + skyrimRoot +
                       "\n\nClose Holdline, right-click its icon, choose \"Run as " +
                       "administrator\", and try again.";
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

                    Directory.CreateDirectory(temp);

                    if (ext == ".zip")
                    {
                        ZipFile.ExtractToDirectory(sourcePath, temp);
                    }
                    else if (ext == ".7z")
                    {
                        var unpackError = Extract7z(sourcePath, temp);

                        if (unpackError != null)
                        {
                            return unpackError;
                        }
                    }
                    else
                    {
                        return "That doesn't look like the SKSE download.\n\n" +
                               "Pick the file you downloaded from silverlock.org — it ends " +
                               "in .7z — or the folder you already extracted it into.";
                    }

                    searchRoot = temp;
                }
                else
                {
                    return "Holdline couldn't find:\n" + sourcePath;
                }

                var skseRoot = FindSkseRoot(searchRoot);

                if (skseRoot == null)
                {
                    return "Holdline looked inside what you picked and couldn't find SKSE.\n\n" +
                           "It should contain a file called " + LoaderName + ".\n\n" +
                           "If you picked a folder, try picking the .7z file you downloaded " +
                           "instead — Holdline can open those itself now.";
                }

                // ---- the check that would have saved the tester two hours ----
                var required = RequiredRuntimeTag(skyrimExePath);
                var offered = RuntimeTagIn(skseRoot);

                if (offered == null)
                {
                    return "That copy of SKSE is incomplete — it has " + LoaderName +
                           " but not the main file that goes with it.\n\n" +
                           "This usually means it's an old or half-extracted copy. " +
                           "Download a fresh one from silverlock.org and pick the .7z file " +
                           "without extracting it.";
                }

                if (required != null &&
                    !string.Equals(required, offered, StringComparison.OrdinalIgnoreCase))
                {
                    return "That's the wrong version of SKSE.\n\n" +
                           "You picked:      SKSE for Skyrim " + SkseStatus.Pretty(offered) + "\n" +
                           "Your Skyrim is:  " + SkseStatus.Pretty(required) + "\n\n" +
                           "This is the single most common mix-up, and it's an easy one to " +
                           "make — the download page lists several builds.\n\n" +
                           "Go to silverlock.org and take the one whose line mentions " +
                           "game version " + SkseStatus.Pretty(required) + ".\n\n" +
                           "If this folder was already sitting on your PC from a while back, " +
                           "ignore it and download a fresh copy.";
                }

                CopyTree(skseRoot, skyrimRoot);

                if (!File.Exists(Path.Combine(skyrimRoot, LoaderName)))
                {
                    return "The SKSE files were copied but " + LoaderName +
                           " still isn't in your Skyrim folder.\n\n" +
                           "Something blocked the copy — antivirus is the usual culprit. " +
                           "Try running Holdline as administrator.";
                }

                if (RuntimeTagIn(skyrimRoot) == null)
                {
                    return "SKSE's loader copied across but its main file didn't.\n\n" +
                           "Try running Holdline as administrator, or check whether your " +
                           "antivirus quarantined something.";
                }

                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "Windows blocked Holdline from writing into your Skyrim folder.\n\n" +
                       "Close Holdline, right-click its icon, choose \"Run as administrator\", " +
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
                    // A leftover temp folder is harmless. Never throw here.
                }
            }
        }

        /// <summary>
        /// Unpacks a .7z using tar.exe, which Windows has shipped built in
        /// since Windows 10 build 1803 (April 2018). No extra program for the
        /// player to install, and no NuGet package for us to add.
        ///
        /// Returns null on success, or a message. If tar is missing or too
        /// old, the player gets the old manual route rather than a dead end.
        /// </summary>
        private static string? Extract7z(string archivePath, string targetDir)
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = "tar.exe",
                    UseShellExecute = false,

                    // CreateNoWindow hides tar's console. The output streams
                    // are deliberately NOT redirected: nothing here reads them,
                    // and an unread redirected stream can fill its buffer and
                    // wedge the child process until the timeout below fires.
                    // With no console attached the output simply goes nowhere.
                    CreateNoWindow = true
                };

                // ArgumentList quotes each item properly, which matters because
                // Skyrim installs live under "Program Files (x86)" and temp
                // paths contain the user's name — both can hold spaces and
                // brackets that a hand-built command line gets wrong.
                start.ArgumentList.Add("-xf");
                start.ArgumentList.Add(archivePath);
                start.ArgumentList.Add("-C");
                start.ArgumentList.Add(targetDir);

                using var process = Process.Start(start);

                if (process == null)
                {
                    return ManualExtractMessage();
                }

                // Bounded so a wedged unpacker can't hang the launcher forever.
                // SKSE is about 15 MB, so two minutes is enormously generous.
                if (!process.WaitForExit(120_000))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Already gone. Nothing to do.
                    }

                    return "Unpacking the SKSE download took too long and was stopped.\n\n" +
                           ManualExtractMessage();
                }

                if (process.ExitCode != 0)
                {
                    return ManualExtractMessage();
                }

                // tar can report success having written nothing useful, so
                // trust the folder rather than the exit code.
                if (Directory.GetFileSystemEntries(targetDir).Length == 0)
                {
                    return ManualExtractMessage();
                }

                return null;
            }
            catch
            {
                return ManualExtractMessage();
            }
        }

        private static string ManualExtractMessage()
        {
            return "Holdline couldn't open that .7z file automatically.\n\n" +
                   "Here's the manual way — it takes about a minute:\n\n" +
                   "1.  Find the file you downloaded (check your Downloads folder).\n" +
                   "2.  Right-click it and choose \"Extract All\", then click Extract.\n" +
                   "3.  A new folder appears next to it.\n" +
                   "4.  Come back here and pick that new folder.\n\n" +
                   "If \"Extract All\" isn't in the right-click menu, your Windows is a " +
                   "little older — installing the free program 7-Zip adds it.";
        }

        /// <summary>
        /// SKSE archives contain a versioned wrapper folder such as
        /// skse64_2_02_06\. Find whichever folder actually holds the loader
        /// instead of assuming the archive's shape.
        /// </summary>
        private static string? FindSkseRoot(string root)
        {
            if (File.Exists(Path.Combine(root, LoaderName)))
            {
                return root;
            }

            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (File.Exists(Path.Combine(dir, LoaderName)))
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
