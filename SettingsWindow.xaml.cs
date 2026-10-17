// ============================================================
//  Holdline — SettingsWindow.xaml.cs
//
//  Everything here exists because of one tester's evening. He
//  pointed the launcher at the wrong thing, there was no screen
//  to look at what it had picked, and no way to change it. Then
//  he was asked to install SKSE by hand and the launcher accepted
//  an ancient copy without complaint.
//
//  So: show the state, let it be changed, and check what you're
//  given before saying yes.
// ============================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.Win32;

namespace HoldlineLauncher
{
    public partial class SettingsWindow : Window
    {
        private static readonly Brush GoodBrush =
            new SolidColorBrush(Color.FromRgb(0x7F, 0xB0, 0x69));

        private static readonly Brush BadBrush =
            new SolidColorBrush(Color.FromRgb(0xC9, 0x6A, 0x4F));

        private static readonly Brush MutedBrush =
            new SolidColorBrush(Color.FromRgb(0xB0, 0xA0, 0x8A));

        private static readonly Brush LineSoftBrush =
            new SolidColorBrush(Color.FromRgb(0x6B, 0x56, 0x37));

        private static readonly Brush AccentBrush =
            new SolidColorBrush(Color.FromRgb(0xD9, 0x8A, 0x3D));

        public SettingsWindow()
        {
            InitializeComponent();
            Refresh();
        }

        // ------------------------------------------------------------
        //  Drawing the current state
        // ------------------------------------------------------------

        /// <summary>
        /// Re-reads everything from disk and redraws. Called on open, after
        /// any change, and by the Re-check button — because the player may
        /// well go fix something in Explorer with this window still open.
        /// </summary>
        private void Refresh()
        {
            var folder = MainWindow.CurrentSkyrimFolder;

            SkyrimPathText.Text = string.IsNullOrWhiteSpace(folder)
                ? "(not set — Holdline hasn't found Skyrim yet)"
                : folder;

            var folderOk = MainWindow.IsSkyrimFolder(folder);

            SkyrimStatusText.Text = folderOk
                ? "Skyrim found here."
                : "No SkyrimSE.exe in this folder, so this isn't a Skyrim install.";

            SkyrimStatusText.Foreground = folderOk ? GoodBrush : BadBrush;

            OpenFolderButton.IsEnabled = !string.IsNullOrWhiteSpace(folder)
                                         && Directory.Exists(folder);

            // ---- SKSE ----
            var exe = folderOk ? MainWindow.SkyrimExeIn(folder) : null;
            var skse = SkseSetup.Inspect(folder, exe);

            SkseStatusText.Text = skse.Summary;
            SkseStatusText.Foreground = skse.Ok
                ? GoodBrush
                : (skse.LoaderPresent ? BadBrush : MutedBrush);

            // The drop zone stays ENABLED even with no Skyrim folder set.
            // A disabled element in WPF never receives drag events at all, so
            // disabling it would make a drop do precisely nothing with no
            // explanation — the exact silent-failure pattern that cost the
            // tester his evening. Dimmed, but it still answers when used.
            DropZone.Opacity = folderOk ? 1.0 : 0.55;
            ChooseSkseButton.IsEnabled = folderOk;
            ChooseSkseFolderButton.IsEnabled = folderOk;

            // ---- Holdline's own files ----
            try
            {
                if (!folderOk)
                {
                    ClientStatusText.Text = "Waiting until Skyrim's folder is set.";
                    ClientStatusText.Foreground = MutedBrush;
                }
                else
                {
                    var dataDir = Path.Combine(folder!, "Data");
                    var clientJs = Path.Combine(dataDir, "Platform", "Plugins", "skymp5-client.js");

                    if (File.Exists(clientJs))
                    {
                        ClientStatusText.Text = "Installed.";
                        ClientStatusText.Foreground = GoodBrush;
                    }
                    else
                    {
                        ClientStatusText.Text =
                            "Not installed yet — click Fix my game, or they arrive " +
                            "on your first join.";
                        ClientStatusText.Foreground = MutedBrush;
                    }
                }
            }
            catch (Exception ex)
            {
                ClientStatusText.Text = "Couldn't check: " + ex.Message;
                ClientStatusText.Foreground = MutedBrush;
            }
        }

        // ------------------------------------------------------------
        //  Skyrim folder
        // ------------------------------------------------------------

        private void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new OpenFolderDialog
            {
                Title = "Select your Skyrim Special Edition folder"
            };

            var current = MainWindow.CurrentSkyrimFolder;

            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            {
                picker.InitialDirectory = current;
            }

            if (picker.ShowDialog(this) != true)
            {
                return;
            }

            if (!MainWindow.IsSkyrimFolder(picker.FolderName))
            {
                MessageBox.Show(this,
                    "That folder doesn't contain SkyrimSE.exe, so it isn't the Skyrim folder.\n\n" +
                    "You picked:\n" + picker.FolderName + "\n\n" +
                    "The one you want usually sits at:\n" +
                    @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition" +
                    "\n\nIf Skyrim is on another drive, search your PC for SkyrimSE.exe " +
                    "to find where it really lives.",
                    "Holdline", MessageBoxButton.OK, MessageBoxImage.Warning);

                return;
            }

            MainWindow.SetSkyrimFolder(picker.FolderName);
            Refresh();
        }

        private void DetectButton_Click(object sender, RoutedEventArgs e)
        {
            var found = MainWindow.DetectSkyrimFolder();

            if (found == null)
            {
                MessageBox.Show(this,
                    "Holdline searched the usual places and couldn't find Skyrim.\n\n" +
                    "Use \"Change folder\" and point it at the folder containing " +
                    "SkyrimSE.exe.",
                    "Holdline", MessageBoxButton.OK, MessageBoxImage.Information);

                return;
            }

            MainWindow.SetSkyrimFolder(found);
            Refresh();

            MessageBox.Show(this,
                "Found it:\n\n" + found,
                "Holdline", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folder = MainWindow.CurrentSkyrimFolder;

            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't open that folder:\n" + ex.Message,
                    "Holdline", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ------------------------------------------------------------
        //  SKSE
        // ------------------------------------------------------------

        private async void GetSkseButton_Click(object sender, RoutedEventArgs e)
        {
            // HOLDLINE: try the automatic route first. The player's own machine
            // fetches SKSE straight from silverlock and unpacks it. Nothing is
            // mirrored or redistributed by us. Only if that fails do we fall
            // back to the old manual path below, which is unchanged.
            var root = MainWindow.CurrentSkyrimFolder;
            var exePath = MainWindow.SkyrimExeIn(root);

            Cursor = Cursors.Wait;
            IsEnabled = false;
            SkseStatusText.Text = "Getting SKSE...";

            string? failure;

            try
            {
                failure = await SkseDownload.DownloadAndInstallAsync(
                    root,
                    exePath,
                    line => Dispatcher.Invoke(() => SkseStatusText.Text = line));
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
            finally
            {
                IsEnabled = true;
                Cursor = null;
            }

            if (failure == null)
            {
                // Installed. Re-read the folder so the screen shows the truth.
                Refresh();

                MessageBox.Show(this,
                    "SKSE is installed. You're good to go \u2014 close this and click Join.",
                    "Holdline \u2014 SKSE ready",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                return;
            }

            Refresh();

            // Automatic route failed. Say why, then hand over to the manual path.
            MessageBox.Show(this,
                "Holdline couldn't fetch SKSE automatically:\n\n" + failure +
                "\n\nNo problem \u2014 we'll do it the manual way instead.",
                "Holdline \u2014 getting SKSE",
                MessageBoxButton.OK, MessageBoxImage.Warning);

            var exe = MainWindow.SkyrimExeIn(MainWindow.CurrentSkyrimFolder);
            var required = SkseSetup.RequiredRuntimeTag(exe);

            var which = required == null
                ? "Take the Anniversary Edition build."
                : "Take the one whose line mentions game version " +
                  SkseStatus.Pretty(required) + ".";

            OpenUrl(SkseSetup.DownloadPage);

            MessageBox.Show(this,
                "The SKSE download page just opened in your browser. It's a plain-looking " +
                "page — that's the real one.\n\n" +
                which + "\n\n" +
                "There are several builds listed and they look almost identical, so " +
                "check the version before you click.\n\n" +
                "When it finishes downloading, drag the file straight onto this window. " +
                "You do NOT need to unzip it.",
                "Holdline — getting SKSE",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ChooseSkseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new OpenFileDialog
            {
                Title = "Find the SKSE file you downloaded",

                // .7z FIRST, because that is what silverlock.org actually
                // serves. The old picker filtered to *.zip, which made the
                // real download invisible in the dialog and sent at least one
                // tester hunting through Downloads for a file that was
                // sitting right there.
                Filter = "SKSE download (*.7z;*.zip)|*.7z;*.zip|All files (*.*)|*.*"
            };

            if (picker.ShowDialog(this) == true)
            {
                InstallSkseFrom(picker.FileName);
            }
        }

        private void ChooseSkseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new OpenFolderDialog
            {
                Title = "Choose the SKSE folder you extracted"
            };

            if (picker.ShowDialog(this) == true)
            {
                InstallSkseFrom(picker.FolderName);
            }
        }

        // ---- drag and drop ----

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;

            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;

                DropZone.BorderBrush = AccentBrush;
                DropZoneText.Text = "Let go to install";
            }

            // Without this the window's default handling can swallow the drop
            // and nothing happens, with no error to explain why.
            e.Handled = true;
        }

        private void DropZone_DragLeave(object sender, DragEventArgs e)
        {
            ResetDropZone();
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            ResetDropZone();
            e.Handled = true;

            if (e.Data == null ||
                e.Data.GetData(DataFormats.FileDrop) is not string[] paths ||
                paths.Length == 0)
            {
                return;
            }

            if (paths.Length > 1)
            {
                MessageBox.Show(this,
                    "Drop just one thing — the SKSE file you downloaded, or the folder " +
                    "you extracted it into.",
                    "Holdline", MessageBoxButton.OK, MessageBoxImage.Information);

                return;
            }

            InstallSkseFrom(paths[0]);
        }

        private void ResetDropZone()
        {
            DropZone.BorderBrush = LineSoftBrush;
            DropZoneText.Text = "Drag the SKSE download here";
        }

        /// <summary>
        /// The one path every route above funnels into, so the checking and
        /// the messages can't drift apart between them.
        /// </summary>
        private void InstallSkseFrom(string path)
        {
            var folder = MainWindow.CurrentSkyrimFolder;

            if (!MainWindow.IsSkyrimFolder(folder))
            {
                MessageBox.Show(this,
                    "Set your Skyrim folder first — SKSE has to go inside it.",
                    "Holdline", MessageBoxButton.OK, MessageBoxImage.Warning);

                return;
            }

            var exe = MainWindow.SkyrimExeIn(folder);

            Cursor = Cursors.Wait;
            IsEnabled = false;

            string? error;

            try
            {
                error = SkseSetup.InstallFrom(path, folder!, exe);
            }
            finally
            {
                // In a finally block so a thrown exception can't leave the
                // window permanently frozen and un-closeable.
                IsEnabled = true;
                Cursor = Cursors.Arrow;
            }

            Refresh();

            if (error != null)
            {
                MessageBox.Show(this, error, "Holdline — SKSE",
                    MessageBoxButton.OK, MessageBoxImage.Warning);

                return;
            }

            MessageBox.Show(this,
                "SKSE is installed and matches your version of Skyrim.\n\n" +
                "You won't have to do that again.",
                "Holdline", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ------------------------------------------------------------
        //  One-click repair
        // ------------------------------------------------------------

        private async void FixButton_Click(object sender, RoutedEventArgs e)
        {
            RepairReport? report = null;
            string? crash = null;

            Cursor = Cursors.Wait;
            IsEnabled = false;
            RepairProgressText.Text = "Checking..."; var done = false;

            // Progress arrives from a background download, so every update is
            // marshalled back onto the UI thread before touching a control.
            var progress = new Progress<string>(line =>
            {
                if (!done) RepairProgressText.Text = line;
            });

            try
            {
                report = await Task.Run(() => Repair.RunAsync(MainWindow.CurrentSkyrimFolder, progress));
            }
            catch (Exception ex)
            {
                // Repair.RunAsync is written not to throw, but a repair button
                // that can itself crash the window would be the worst possible
                // version of this feature.
                crash = ex.Message;
            }
            finally
            {
                IsEnabled = true;
                Cursor = Cursors.Arrow;
                done = true; RepairProgressText.Text = "";
            }

            // Every message box below is shown AFTER the window is enabled
            // again, so the player is never looking at a dialog owned by a
            // frozen window.
            if (crash != null || report == null)
            {
                MessageBox.Show(this,
                    "The check couldn't finish:\n\n" + (crash ?? "unknown problem"),
                    "Holdline", MessageBoxButton.OK, MessageBoxImage.Warning);

                return;
            }

            // Redraw first: the panels below should already agree with the
            // report before the player reads it.
            Refresh();

            MessageBox.Show(this,
                report.ToMessage(),
                report.ReadyToPlay ? "Holdline — all good" : "Holdline — what's wrong",
                MessageBoxButton.OK,
                report.ReadyToPlay ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        // ------------------------------------------------------------
        //  Window chrome
        // ------------------------------------------------------------

        private void RecheckButton_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch
            {
                // A browser that refuses to open is not worth crashing over.
            }
        }
    }
}
