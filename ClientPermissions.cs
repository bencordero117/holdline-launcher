// ClientPermissions.cs
// Added 2026-08-09.
//
// WHY THIS FILE EXISTS AT ALL
// ---------------------------
// The Program Files permission fix lives in Repair.cs, which only runs when a
// player presses "Fix My Game". That is a button most players will never
// press, because nothing tells them to: they install Holdline, press Join,
// and the game dies.
//
// Tester Gio's crash, proven from his Windows Event Viewer (Event ID 1000):
//
//     Faulting application: CEFSubprocess.exe
//     Faulting module:      CEFSubprocess.exe
//     Exception code:       0xc0000409          (fail-fast)
//     Path: c:\program files (x86)\steam\steamapps\common\
//           Skyrim Special Edition\Data\NirnLabUIPlatform\CEFSubprocess.exe
//
// CEF is the Chromium runtime NirnLab uses to draw Holdline's interface and
// chat. It writes cache and log files beside itself as it spawns its
// subprocess, and Program Files is write-protected by Windows. The launcher
// gets away with writing there because the player is told to run it as
// administrator; the GAME is launched by Steam, unelevated, and gets no such
// help. Granting the ACL from the launcher persists after it exits, so the
// unelevated game inherits it.
//
// So the fix has to be on the Join path too, not only behind a button.
//
// WHY IT IS A SEPARATE FILE
// -------------------------
// This is one call and one stamp. Putting it here rather than inside
// ClientFiles.cs means the only change to that file is a single inserted
// line, which is the whole point: this project has already lost a night to a
// stale copy of a large file being pasted over a newer one. Small surface,
// small risk.
//
// WHY THERE IS A STAMP
// --------------------
// Without elevation the grant needs one UAC prompt. Prompting on every
// single Join would be worse than the bug. The stamp records that the grant
// already succeeded on this install, so the prompt happens once. It lives in
// Data\Platform, so anything that wipes and rebuilds that folder — including
// Fix My Game — correctly causes the grant to run again against the fresh
// files.
//
// Fix My Game deliberately does NOT consult this stamp. It is the player
// explicitly asking for everything to be re-checked, and it already runs
// elevated in the cases that matter.

using System;
using System.IO;

namespace HoldlineLauncher
{
    public static class ClientPermissions
    {
        /// <summary>
        /// Bump this to force every install to be re-granted, the same way
        /// ClientFilesVersion forces a re-download.
        /// </summary>
        private const string PermissionsVersion = "1";

        private static string MarkerPath(string skyrimDataDir) =>
            Path.Combine(skyrimDataDir, "Platform", "holdline-permissions.txt");

        /// <summary>
        /// Grants the player modify rights on Holdline's folders when Skyrim
        /// sits under Program Files. Does nothing anywhere else.
        ///
        /// Never throws and never blocks a join. A failed grant leaves the
        /// player exactly where they were before this existed — the game may
        /// still crash — whereas refusing to join would break every player
        /// whose install happens to work despite the folder.
        /// </summary>
        public static void EnsureBrowserPermissions(string skyrimDataDir,
                                                    IProgress<string> report)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(skyrimDataDir))
                {
                    return;
                }

                if (skyrimDataDir.IndexOf("Program Files",
                                          StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return;
                }

                var marker = MarkerPath(skyrimDataDir);

                if (File.Exists(marker) &&
                    File.ReadAllText(marker).Trim() == PermissionsVersion)
                {
                    // Already done on this install. No prompt, no work.
                    return;
                }

                var skyrimRoot = Path.GetDirectoryName(
                    skyrimDataDir.TrimEnd(Path.DirectorySeparatorChar,
                                          Path.AltDirectorySeparatorChar));

                if (string.IsNullOrEmpty(skyrimRoot))
                {
                    return;
                }

                report.Report("Unlocking Holdline's folders in Skyrim " +
                              "(Windows may ask for permission - choose Yes)...");

                // Same code Fix My Game runs. One implementation, so the two
                // paths can never drift apart.
                var result = new RepairReport();

                Repair.GrantBrowserFolderWriteAccess(skyrimRoot, skyrimDataDir, result);

                if (result.Blocking.Count > 0)
                {
                    // Say what happened and carry on. No stamp is written, so
                    // the next Join tries again.
                    report.Report(result.Blocking[0]);
                    return;
                }

                TryWriteMarker(marker);
            }
            catch
            {
                // A permission fix must never be the reason a player cannot
                // join. Worst case they are where they were before it existed.
            }
        }

        /// <summary>
        /// Writes the stamp, but ONLY into a Platform folder that already
        /// exists.
        ///
        /// Creating it would be a real regression. Fix My Game's
        /// RepairPlatformFolder renames a Platform_off folder back to Platform
        /// only when no Platform folder is present - if both exist it refuses,
        /// calls the leftover harmless, and moves on. An empty Platform folder
        /// created here just to hold a stamp would therefore permanently block
        /// the single highest-value repair in the launcher on any machine
        /// where a tester had renamed that folder.
        ///
        /// On a fresh install Platform does not exist yet, so no stamp is
        /// written and the grant runs again on the next Join. That costs one
        /// extra prompt until the client files land, and it is the safe
        /// direction to be wrong in.
        /// </summary>
        private static void TryWriteMarker(string marker)
        {
            try
            {
                var dir = Path.GetDirectoryName(marker);

                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    return;
                }

                File.WriteAllText(marker, PermissionsVersion);
            }
            catch
            {
                // Failing to write the stamp costs one extra prompt next
                // time, not a broken join.
            }
        }
    }
}
