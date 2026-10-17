// ============================================================
//  Holdline — Dependencies.cs
//
//  Third-party runtimes a vanilla Skyrim does not have, and
//  that Holdline cannot work without.
//
//  Right now that means one thing: the Microsoft Visual C++
//  Redistributable. SSE Engine Fixes will not load without it,
//  and without Engine Fixes the Chromium process that draws the
//  Holdline overlay hangs. So on a clean machine the chain is:
//
//      Visual C++  ->  Engine Fixes  ->  overlay  ->  playable
//
//  Engine Fixes itself ships inside the client-files zip, since
//  it is just files. Visual C++ can't work that way — it is an
//  installer that has to be RUN, once, and only if it is missing.
//  That's what this file is for.
// ============================================================

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace HoldlineLauncher
{
    public static class Dependencies
    {
        // Microsoft's permanent redirect to the current x64 redistributable.
        // Deliberately NOT bundled in the client zip: this link always serves
        // the latest patched build, and it keeps 25 MB of Microsoft installer
        // out of a zip that every player downloads.
        public const string VcRedistUrl =
            "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        // Where the redistributable records itself. Both views are checked
        // because which one exists depends on how it was originally installed.
        private static readonly string[] VcRegistryKeys =
        {
            @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
            @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
        };

        /// <summary>
        /// True if the x64 Visual C++ runtime is already on this machine.
        /// This is a registry read and costs nothing, so it is safe to call
        /// on every single join rather than trusting a stamp file.
        /// </summary>
        public static bool IsVcRedistInstalled()
        {
            foreach (var keyPath in VcRegistryKeys)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                    if (key == null)
                    {
                        continue;
                    }

                    // "Installed" is a DWORD set to 1 by the redistributable.
                    if (key.GetValue("Installed") is int installed && installed == 1)
                    {
                        return true;
                    }
                }
                catch
                {
                    // A locked-down or unreadable registry hive should not
                    // stop a join. Fall through and check the next key.
                }
            }

            return false;
        }

        /// <summary>
        /// Makes sure Visual C++ is present, installing it silently if not.
        /// Returns null on success, or a message to show the player.
        ///
        /// This runs at most once per machine in practice: after a successful
        /// install the registry check above short-circuits every later join.
        /// </summary>
        public static async Task<string?> EnsureVcRedistAsync(IProgress<string> report)
        {
            if (IsVcRedistInstalled())
            {
                return null;
            }

            var work = Path.Combine(
                Path.GetTempPath(),
                "Holdline-vcredist-" + Guid.NewGuid().ToString("N"));

            var installer = Path.Combine(work, "vc_redist.x64.exe");

            try
            {
                Directory.CreateDirectory(work);

                report.Report("Getting a small Microsoft component Skyrim needs (about 25 MB)...");

                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                using (var response = await http.GetAsync(VcRedistUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return "Holdline couldn't download a small Microsoft component " +
                               "that Skyrim needs (error " + (int)response.StatusCode + ").\n\n" +
                               "Check your internet connection and try joining again.\n\n" +
                               "If it keeps failing, you can get it straight from Microsoft here:\n" +
                               VcRedistUrl;
                    }

                    await using var source = await response.Content.ReadAsStreamAsync();
                    await using var target = File.Create(installer);
                    await source.CopyToAsync(target);
                }

                report.Report("Setting up that Microsoft component...");

                // /quiet means no Microsoft window appears. /norestart means it
                // cannot reboot the machine out from under someone who is about
                // to play. UseShellExecute is required: this installer asks for
                // administrator rights, and only the shell can raise that
                // prompt. Windows will show one permission box, once.
                var start = new ProcessStartInfo
                {
                    FileName = installer,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = true
                };

                using var process = Process.Start(start);

                if (process == null)
                {
                    return "Holdline couldn't start the Microsoft installer.\n\n" +
                           "You can install it yourself from Microsoft here:\n" + VcRedistUrl;
                }

                await process.WaitForExitAsync();

                switch (process.ExitCode)
                {
                    case 0:      // installed
                    case 1638:   // this or a newer version is already present
                    case 5100:   // a newer version is already present
                        report.Report("Microsoft component ready.");
                        return null;

                    case 3010:   // installed, wants a reboot eventually
                        report.Report("Microsoft component ready.");
                        return null;

                    case 1602:   // user dismissed it
                    case 1223:   // user said No to the UAC prompt
                        return "That got cancelled, so it didn't install.\n\n" +
                               "Skyrim needs this small Microsoft component to run online — " +
                               "there's no way around it, sorry.\n\n" +
                               "Click Join again, and when Windows asks \"Do you want to allow " +
                               "this app to make changes?\", choose Yes.";

                    default:
                        return "That Microsoft component didn't finish installing " +
                               "(code " + process.ExitCode + ").\n\n" +
                               "Try joining again. If it still won't go, you can install it " +
                               "yourself from Microsoft here:\n" + VcRedistUrl;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // Thrown when Process.Start's UAC prompt is refused.
                return "Windows asked for permission and got a No, so that didn't " +
                       "install.\n\nSkyrim needs it to run online. Click Join again, and " +
                       "choose Yes when the Windows box appears — it's just Microsoft's " +
                       "own installer.";
            }
            catch (Exception ex)
            {
                return "Setting up a Microsoft component didn't work:\n" + ex.Message +
                       "\n\nYou can install it yourself from Microsoft here:\n" + VcRedistUrl;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(work))
                    {
                        Directory.Delete(work, recursive: true);
                    }
                }
                catch
                {
                    // A leftover temp folder is harmless. Never throw here.
                }
            }
        }
    }
}
