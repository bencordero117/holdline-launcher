// MainWindow.xaml.cs
// Holdline launcher — hold list now comes from Supabase.
//
// WHAT CHANGED FROM THE FIRST VERSION:
//   - LoadHolds() no longer invents three rows. It calls the Supabase REST
//     API and shows whatever is really in public.holds.
//   - Reads happen with the PUBLISHABLE key and no sign-in. That works
//     because of the RLS policy holds_public_read, created 2026-07-27,
//     which grants SELECT on public.holds to anon + authenticated.
//     The key opens the door; the policy decides what is visible.
//   - A hold is joinable only if it has a server_address and status is
//     online. Rows without an address are shown but refuse to launch,
//     rather than failing confusingly at the game.
//
// Join is unchanged and already proven: version check, locate SKSE,
// write ip/port/master/master-key into skymp5-client-settings.txt, then
// launch via skse64_loader.exe with the Skyrim folder as working dir.

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace HoldlineLauncher;

/// <summary>
/// One server in the browser. Maps onto a row of public.holds.
/// </summary>
public class Hold
{
    public string Name { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 7777;
    public string MasterKey { get; set; } = "";
    public int Players { get; set; }
    public int MaxPlayers { get; set; } = 32;
    public string Framework { get; set; } = "";
    public string Language { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsPrivate { get; set; }

    /// <summary>
    /// Locked by Holdline from the website admin panel. A locked hold is shut,
    /// not paused: it still appears in the list so its owner can see the state,
    /// but the launcher refuses to connect.
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// The hold's tags, as chosen on the website ("PvP", "Serious RP" and so
    /// on). Never null, so filter code can enumerate it without guarding.
    /// </summary>
    public List<string> Tags { get; set; } = new List<string>();

    /// <summary>
    /// True when the hold has not published an address, so there is nothing
    /// to connect to. Shown in the list, but the Join button says so.
    /// </summary>
    public bool IsPlaceholder => string.IsNullOrWhiteSpace(Ip);

    /// <summary>
    /// True only when the hold says outright that it is offline. A blank or
    /// unrecognised status counts as unknown, not offline, because refusing
    /// to connect on missing data would be worse than trying and failing.
    /// </summary>
    public bool IsOffline => string.Equals(Status.Trim(), "offline", StringComparison.OrdinalIgnoreCase);

    public Brush Accent { get; set; } = new SolidColorBrush(Color.FromRgb(0x6B, 0x56, 0x37));

    // --- display helpers bound by the XAML ---
    public string Address => IsLocked ? "unavailable" : (IsPlaceholder ? "not published" : Ip + ":" + Port);
    public string PlayerText => IsPlaceholder ? "--" : Players + "/" + MaxPlayers;
    public string AccessText => IsPrivate ? "Private hold" : "Open to all";

    public string MetaText
    {
        get
        {
            if (IsLocked)
            {
                return "LOCKED";
            }

            if (IsPlaceholder)
            {
                return "NO ADDRESS";
            }

            var status = Status.Trim();

            // A blank status used to render a blank badge. Say something.
            if (status.Length == 0)
            {
                return "UNKNOWN";
            }

            if (!string.Equals(status, "online", StringComparison.OrdinalIgnoreCase))
            {
                return status.ToUpperInvariant();
            }

            return string.IsNullOrWhiteSpace(Language) ? "ONLINE" : Language.ToUpperInvariant();
        }
    }
}

public partial class MainWindow : Window
{
    // The master server deployed and verified working on 2026-07-27.
    private const string MasterUrl = "https://holdline-chi.vercel.app";

    // Where Skyrim lives. This used to be a const pointing at Ben's own D:
    // drive, which meant the launcher worked on exactly one machine on Earth
    // and told everyone else "Couldn't find Skyrim". Caught by the first
    // outside tester, 2026-07-28.
    //
    // Now resolved at join time, cheapest and most reliable source first:
    //   1. A folder the user already picked by hand (remembered in AppData).
    //   2. Bethesda's own installer registry key.
    //   3. Steam's registry key, then every Steam library folder it lists,
    //      because keeping games on a second drive is normal, not exotic.
    //   4. A short list of well-known default paths.
    // If every one of those misses we ask the user once and remember it.
    private static string? _skyrimFolder;

    // Special Edition ships as SkyrimSE.exe, but check the other names too
    // rather than assuming, so a VR install doesn't produce a mystery failure.
    private static readonly string[] SkyrimExeNames =
        { "SkyrimSE.exe", "SkyrimVR.exe", "Skyrim.exe" };

    // --- Supabase ---
    private const string SupabaseUrl = "https://wjyketroeqrdcctnivnb.supabase.co";

    // PASTE THE PUBLISHABLE KEY BETWEEN THE QUOTES ON THE LINE BELOW.
    // It is the one labelled "publishable" in the Supabase dashboard, and it
    // starts with sb_publishable_. It is safe to ship inside the app.
    // Never put a key starting with sb_secret_ in here.
    private const string SupabaseKey = "sb_publishable_i-N54iK1lf8ICBdqUjD_6w_3WmsflLY";

    private const string HoldsQuery =
        "/rest/v1/holds?select=name,tagline,server_address,server_port,master_key," +
        "players,capacity,is_private,is_locked,hold_color,status,framework,language,tags&order=name.asc";

    // 15 seconds, not the 100-second default. If Holdline is unreachable the
    // launcher should say so quickly instead of sitting on "Fetching...".
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ObservableCollection<Hold> holds = new();

    public MainWindow()
    {
        InitializeComponent();

        HoldList.ItemsSource = holds;
        MasterText.Text = MasterUrl.Replace("https://", "");
        HoldCountText.Text = "loading";
        SetStatus("Fetching holds from Holdline...");

        // Fire and forget: the window shows immediately and fills in when
        // the request comes back, instead of freezing on a cold network.
        _ = LoadHoldsAsync();

        // Also fire and forget, and deliberately AFTER the hold list: if
        // GitHub is slow or down, the player still gets their server list.
        // An update check must never be able to stop somebody joining.
        _ = CheckForUpdateAsync();
    }

    /// <summary>
    /// Asks GitHub whether a newer launcher exists, and offers it.
    ///
    /// Client files already self-update (ClientFiles.cs). Before this, the
    /// launcher exe did not: every launcher change meant telling every player
    /// by hand to go and re-download it, and the ones who missed the message
    /// kept running old code against a moving server.
    ///
    /// Optional on purpose. Declining leaves the current launcher working
    /// exactly as it did, and so does every failure path.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        LauncherUpdateInfo? update;

        try
        {
            update = await LauncherUpdate.CheckAsync();
        }
        catch
        {
            // CheckAsync already swallows its own failures; this is belt and
            // braces so a surprise here can't take the window down at startup.
            return;
        }

        if (update == null)
        {
            return;
        }

        var answer = MessageBox.Show(
            "A new version of Holdline is available (" + update.Version + ").\n\n" +
            "You're running " + LauncherUpdate.CurrentVersion + ". Updating takes a " +
            "minute or two and Holdline will restart itself when it's done.\n\n" +
            "Update now?",
            "Holdline update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes)
        {
            SetStatus("Update skipped — you're running Holdline " +
                      LauncherUpdate.CurrentVersion + ".");
            return;
        }

        var report = new Progress<string>(SetStatus);

        var error = await LauncherUpdate.DownloadAndApplyAsync(update, report);

        if (error != null)
        {
            SetStatus("Update failed — carrying on with the current version.");

            MessageBox.Show(
                error,
                "Holdline couldn't update",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        // The swap is now waiting on this process to exit — it physically
        // cannot overwrite the exe while we're still holding it open. So the
        // last thing this build ever does is close itself.
        Application.Current.Shutdown();
    }

    private static SolidColorBrush Hex(byte r, byte g, byte b)
    {
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    /// <summary>
    /// Turns "#4FA9C9" into a brush. Falls back to the border colour on
    /// anything unexpected, since a bad colour must not break the list.
    /// </summary>
    private static Brush ParseAccent(string? hex)
    {
        var fallback = Hex(0x6B, 0x56, 0x37);

        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var text = hex.Trim().TrimStart('#');
        if (text.Length != 6)
        {
            return fallback;
        }

        try
        {
            var r = byte.Parse(text.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var g = byte.Parse(text.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var b = byte.Parse(text.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Hex(r, g, b);
        }
        catch
        {
            return fallback;
        }
    }

    // --- small helpers for reading JSON that may be null or missing ---

    private static string GetString(JsonElement row, string field)
    {
        if (row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }

        return "";
    }

    private static int GetInt(JsonElement row, string field, int fallback)
    {
        if (row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var number))
            {
                return number;
            }
        }

        return fallback;
    }

    private static bool GetBool(JsonElement row, string field)
    {
        return row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Reads a JSON array of strings. A hold created before tags existed, or
    /// one saved with none, comes back as null rather than an empty array, so
    /// both have to land on an empty list instead of throwing.
    /// </summary>
    private static List<string> GetStringList(JsonElement row, string field)
    {
        var list = new List<string>();

        if (row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in value.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var text = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        list.Add(text);
                    }
                }
            }
        }

        return list;
    }

    private async Task LoadHoldsAsync()
    {
        if (SupabaseKey.StartsWith("PASTE", StringComparison.Ordinal))
        {
            HoldCountText.Text = "0 listed";
            SetStatus("No Supabase key set in the launcher. Paste the publishable key into MainWindow.xaml.cs.");
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SupabaseUrl + HoldsQuery);
            request.Headers.Add("apikey", SupabaseKey);
            request.Headers.Add("Authorization", "Bearer " + SupabaseKey);

            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                HoldCountText.Text = "0 listed";
                SetStatus("Holdline returned " + (int)response.StatusCode + ": " + Trim(body));
                return;
            }

            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                HoldCountText.Text = "0 listed";
                SetStatus("Unexpected reply from Holdline. Expected a list of holds.");
                return;
            }

            holds.Clear();

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                holds.Add(new Hold
                {
                    Name = GetString(row, "name"),
                    Tagline = GetString(row, "tagline"),
                    Ip = GetString(row, "server_address"),
                    Port = GetInt(row, "server_port", 7777),
                    MasterKey = GetString(row, "master_key"),
                    Players = GetInt(row, "players", 0),
                    MaxPlayers = GetInt(row, "capacity", 32),
                    Framework = GetString(row, "framework"),
                    Language = GetString(row, "language"),
                    Status = GetString(row, "status"),
                    IsPrivate = GetBool(row, "is_private"),
                    IsLocked = GetBool(row, "is_locked"),
                    Tags = GetStringList(row, "tags"),
                    Accent = ParseAccent(GetString(row, "hold_color"))
                });
            }

            HoldCountText.Text = holds.Count + " listed";

            if (holds.Count == 0)
            {
                SetStatus("No holds are listed yet. Create one on the website and it will appear here.");
                return;
            }

            HoldList.SelectedIndex = 0;
            SetStatus("Loaded " + holds.Count + " hold" + (holds.Count == 1 ? "" : "s") + " from Holdline.");
        }
        catch (Exception ex)
        {
            HoldCountText.Text = "0 listed";
            SetStatus("Couldn't reach Holdline: " + ex.Message);
        }
    }

    /// <summary>
    /// Keeps a long error body from overflowing the one-line status bar.
    /// </summary>
    private static string Trim(string text)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length > 160 ? flat.Substring(0, 160) + "..." : flat;
    }
internal static string? CurrentSkyrimFolder
    {
        get
        {
            if (LooksLikeSkyrim(_skyrimFolder))
            {
                return _skyrimFolder;
            }

            return LoadSavedSkyrimFolder() ?? AutoDetectSkyrimFolder();
        }
    }

    internal static bool IsSkyrimFolder(string? folder)
    {
        return LooksLikeSkyrim(folder);
    }

    internal static void SetSkyrimFolder(string folder)
    {
        _skyrimFolder = folder;
        SaveSkyrimFolder(folder);
    }

    internal static string? DetectSkyrimFolder()
    {
        return AutoDetectSkyrimFolder();
    }

    internal static string? SkyrimExeIn(string? folder)
    {
        if (!LooksLikeSkyrim(folder))
        {
            return null;
        }

        foreach (var name in SkyrimExeNames)
        {
            var full = Path.Combine(folder!, name);

            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new SettingsWindow { Owner = this };
            AddSinglePlayerButton(window);
            window.ShowDialog();
            RefreshSinglePlayerLabel();

            SetStatus("Settings closed.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Holdline — settings failed",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    

private void HoldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HoldList.SelectedItem is not Hold hold)
        {
            JoinButton.IsEnabled = false;
            return;
        }

        DetailName.Text = hold.Name;
        DetailTagline.Text = hold.Tagline;
        DetailAddress.Text = hold.Address;
        DetailKey.Text = string.IsNullOrEmpty(hold.MasterKey) ? "none" : hold.MasterKey;
        DetailRegion.Text = string.IsNullOrWhiteSpace(hold.Framework) ? "unknown" : hold.Framework;
        DetailAccess.Text = hold.AccessText;

        JoinButton.IsEnabled = true;

        if (hold.IsLocked)
        {
            JoinButton.Content = "Locked by Holdline";
            JoinButton.IsEnabled = false;
        }
        else if (hold.IsPlaceholder)
        {
            JoinButton.Content = "No address published";
        }
        else if (hold.IsOffline)
        {
            JoinButton.Content = "Hold is offline";
        }
        else
        {
            JoinButton.Content = "Join hold";
        }
    }

    /// <summary>
    /// True if this folder actually holds a Skyrim install. Checking for the
    /// executable rather than the folder name means a renamed or relocated
    /// install still passes, and a merely similar-looking folder doesn't.
    /// </summary>
    private static bool LooksLikeSkyrim(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        foreach (var name in SkyrimExeNames)
        {
            if (File.Exists(Path.Combine(folder, name)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindSkyrimExe()
    {
        if (!LooksLikeSkyrim(_skyrimFolder))
        {
            return null;
        }

        foreach (var name in SkyrimExeNames)
        {
            var full = Path.Combine(_skyrimFolder!, name);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>
    /// Where we remember the user's answer. AppData rather than next to the
    /// exe, because the exe may live in Downloads and get replaced on every
    /// update, and a setting that vanishes on update is worse than no setting.
    /// </summary>
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Holdline",
        "launcher-settings.json");

    private static string? LoadSavedSkyrimFolder()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));

            if (doc.RootElement.TryGetProperty("skyrimFolder", out var value))
            {
                var saved = value.GetString();
                return LooksLikeSkyrim(saved) ? saved : null;
            }
        }
        catch
        {
            // A corrupt settings file should cost the user a re-pick, not a
            // crash on startup.
        }

        return null;
    }

    private static void SaveSkyrimFolder(string folder)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);

            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(new { skyrimFolder = folder }));
        }
        catch
        {
            // Failing to save just means we ask again next time. Not fatal.
        }
    }

    private static string? ReadRegistryString(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Every Steam library on this machine: the main install plus whatever
    /// extra drives the user has added.
    /// </summary>
    private static IEnumerable<string> SteamLibraryFolders(string? steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
        {
            yield break;
        }

        yield return steamPath;

        // libraryfolders.vdf is Valve's own text format, not JSON. A real
        // parser is overkill: every library line looks like
        //     "path"    "D:\\SteamLibrary"
        // so taking the second quoted string off any line mentioning path is
        // enough, and degrades quietly if Valve rearranges the file.
        string[] vdfCandidates =
        {
            Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamPath, "config", "libraryfolders.vdf"),
        };

        foreach (var vdf in vdfCandidates)
        {
            string[] lines;

            try
            {
                if (!File.Exists(vdf))
                {
                    continue;
                }

                lines = File.ReadAllLines(vdf);
            }
            catch
            {
                continue;
            }

            foreach (var line in lines)
            {
                if (line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var parts = line.Split('"');

                if (parts.Length >= 4)
                {
                    var candidate = parts[3].Replace(@"\\", @"\");

                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> CandidateSkyrimFolders()
    {
        // 1. Bethesda's installer records the install path directly, so try
        //    that before going hunting through Steam.
        string[] bethesdaKeys =
        {
            @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition",
            @"SOFTWARE\Bethesda Softworks\Skyrim Special Edition",
        };

        foreach (var key in bethesdaKeys)
        {
            var path = ReadRegistryString(Registry.LocalMachine, key, "Installed Path")
                    ?? ReadRegistryString(Registry.LocalMachine, key, "installed path");

            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path.Trim();
            }
        }

        // 2. Steam itself, then every library folder it knows about.
        var steam = ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                 ?? ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                 ?? ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");

        foreach (var library in SteamLibraryFolders(steam))
        {
            yield return Path.Combine(library, "steamapps", "common", "Skyrim Special Edition");
        }

        // 3. Last-ditch guesses. Cheap to check, and they cover the common
        //    layouts including Ben's own, so his machine still works even if
        //    every registry lookup above comes back empty.
        string[] drives = { "C", "D", "E", "F" };

        foreach (var drive in drives)
        {
            yield return drive + @":\SteamLibrary\steamapps\common\Skyrim Special Edition";
            yield return drive + @":\Steam\steamapps\common\Skyrim Special Edition";
            yield return drive + @":\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition";
            yield return drive + @":\Games\Steam\steamapps\common\Skyrim Special Edition";
        }
    }

    /// <summary>
    /// Where Steam itself is installed - the folder download_depot writes
    /// under (steamapps\content). From the registry, the same keys the
    /// Skyrim hunt uses; null when none of them answer.
    /// </summary>
    private static string? SteamInstallFolder()
    {
        var steam = ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                 ?? ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                 ?? ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (string.IsNullOrWhiteSpace(steam)) return null;
        var norm = steam.Trim().Replace('/', '\\');
        return Directory.Exists(norm) ? norm : null;
    }

    /// <summary>
    /// The app_489830 folder Steam's console just filled, if all three
    /// depot folders are in it. Checks Steam's install folder first, then
    /// the usual places. Null means ask the player.
    /// </summary>
    private static string? FindDownloadedDepotRoot()
    {
        var roots = new List<string>();
        var steam = SteamInstallFolder();
        if (steam != null) roots.Add(Path.Combine(steam, "steamapps", "content", "app_" + DowngradeAppId));
        string[] drives = { "C", "D", "E", "F" };
        foreach (var drive in drives)
        {
            roots.Add(drive + @":\Program Files (x86)\Steam\steamapps\content\app_" + DowngradeAppId);
            roots.Add(drive + @":\Steam\steamapps\content\app_" + DowngradeAppId);
            roots.Add(drive + @":\SteamLibrary\steamapps\content\app_" + DowngradeAppId);
        }
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var complete = true;
            foreach (var step in DowngradeSteps)
            {
                if (!Directory.Exists(Path.Combine(root, step.FolderName))) { complete = false; break; }
            }
            if (complete) return root;
        }
        return null;
    }

    private static string? AutoDetectSkyrimFolder()
    {
        foreach (var candidate in CandidateSkyrimFolders())
        {
            if (LooksLikeSkyrim(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Makes sure _skyrimFolder points at a real install, searching silently
    /// first and asking the user only once everything automatic has failed.
    /// Returns false if we still don't know where Skyrim is.
    /// </summary>
    private bool EnsureSkyrimFolder()
    {
        if (LooksLikeSkyrim(_skyrimFolder))
        {
            return true;
        }

        _skyrimFolder = LoadSavedSkyrimFolder() ?? AutoDetectSkyrimFolder();

        if (LooksLikeSkyrim(_skyrimFolder))
        {
            SaveSkyrimFolder(_skyrimFolder!);
            return true;
        }

        MessageBox.Show(
            "Holdline couldn't find your Skyrim Special Edition folder automatically.\n\n" +
            "Click OK, then point Holdline at it. It's the folder that contains " +
            "SkyrimSE.exe, usually inside steamapps\\common\\Skyrim Special Edition.\n\n" +
            "Holdline only asks once and then remembers.",
            "Holdline — where is Skyrim?",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        var picker = new OpenFolderDialog
        {
            Title = "Select your Skyrim Special Edition folder",
        };

        if (picker.ShowDialog() != true)
        {
            return false;
        }

        if (!LooksLikeSkyrim(picker.FolderName))
        {
            MessageBox.Show(
                "That folder doesn't contain SkyrimSE.exe, so it isn't the Skyrim folder.\n\n" +
                "Picked:\n" + picker.FolderName,
                "Holdline",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return false;
        }

        _skyrimFolder = picker.FolderName;
        SaveSkyrimFolder(_skyrimFolder);
        return true;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    /// <summary>
    /// Puts a failure in front of the player instead of leaving it in a log.
    /// The first half is the proven cause; the second is what to do about it.
    /// </summary>
    private void ShowProblem(string problem, string guidance)
    {
        SetStatus(problem.Replace("\n", " "));
        MessageBox.Show(
            problem + "\n\n" + guidance,
            "Holdline",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async void JoinButton_Click(object sender, RoutedEventArgs e)
    {
        if (HoldList.SelectedItem is not Hold hold)
        {
            SetStatus("Pick a hold first.");
            return;
        }

        if (hold.IsLocked)
        {
            SetStatus(hold.Name + " has been locked by Holdline, so it can't be joined right now.");
            return;
        }

        if (hold.IsPlaceholder)
        {
            SetStatus("This hold hasn't published a server address, so there's nothing to connect to yet.");
            return;
        }

        if (hold.IsOffline)
        {
            SetStatus(hold.Name + " reports itself offline, so the game would sit there failing to connect.");
            return;
        }

        if (string.IsNullOrWhiteSpace(hold.MasterKey))
        {
            SetStatus("This hold has no master key, so the game can't fetch its mod list.");
            return;
        }

        if (!EnsureSkyrimFolder())
        {
            SetStatus("Holdline still doesn't know where Skyrim is, so there's nothing to launch.");
            return;
        }

        var exePath = FindSkyrimExe();
        if (exePath == null)
        {
            var msg = "Couldn't find Skyrim in:\n" + _skyrimFolder;
            SetStatus(msg.Replace("\n", " "));
            MessageBox.Show(msg, "Holdline", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var skyrimRoot = _skyrimFolder!;
        var dataDir = Path.Combine(skyrimRoot, "Data");

        if (RelaunchElevatedIfNeeded(skyrimRoot))
        {
            SetStatus("Your Skyrim is under Program Files - restarting Holdline with administrator rights.");
            return;
        }

        JoinButton.IsEnabled = false;

        try
        {
            //  If the Single player switch parked Holdline's files, put them
            //  back first. Otherwise step 2 sees MpClientPlugin.dll missing
            //  and downloads the whole bundle over the parked copies.
            try
            {
                if (HoldlineIsOff())
                {
                    var restored = TurnHoldlineOn(skyrimRoot);
                    SetStatus("Turned Holdline back on. " + SummariseOn(restored));
                    RefreshSinglePlayerLabel();
                }
            }
            catch (Exception exOn)
            {
                SetStatus("Couldn't turn Holdline back on: " + exOn.Message);
                return;
            }

            // Step 0: the game's own version. Checked before SKSE because
            // SKSE built for one version can't load into another — putting
            // it in first would just be a longer walk to the same wall.
            if (SkympConnector.NeedsDowngrade(exePath))
            {
                // Read once. If the exe went missing in the gap, don't put
                // "Steam updated Skyrim to ," in front of the player — say
                // something true instead.
                var found = SkympConnector.GetInstalledVersion(exePath);
                await OfferDowngrade(
                    skyrimRoot,
                    string.IsNullOrEmpty(found) ? "a version Holdline can't use" : found);
                return;
            }

            // Step 1: SKSE. Checked here rather than left to ConnectToHold so
            // that a missing copy becomes an offer of help instead of a dead
            // end. We can't ship SKSE ourselves — its permissions forbid
            // redistribution, and forbid its use in anything sold — so the
            // player downloads it and we place it.
            if (SkympConnector.FindSkseLoader(exePath) == null)
            {
                if (!await OfferSkseInstall(skyrimRoot))
                {
                    SetStatus("Holdline needs SKSE installed before it can launch Skyrim.");
                    return;
                }
            }

            // Step 1b: claim this install's player id NOW, before step 2 can
            // overwrite skymp5-client-settings.txt with the shipped copy. On
            // an install that was hand-fixed to a real id, that file is the
            // only record of it, and a client-files update would erase it.
            // Cheap and idempotent: after the first run this is a file read.
            PlayerIdentity.GetOrCreateProfileId(
                Path.Combine(dataDir, "Platform", "Plugins", "skymp5-client-settings.txt"));

            // Step 2: the SkyMP client files. Without these the game launches
            // straight into the ordinary Skyrim main menu, connected to
            // nothing — which is exactly what the first outside tester would
            // have seen. Returns immediately once installed, so this costs
            // nothing on later joins.
            var installError = await ClientFiles.EnsureInstalledAsync(
                dataDir,
                new Progress<string>(SetStatus));

            if (installError != null)
            {
                SetStatus(installError.Replace("\n", " "));
                MessageBox.Show(installError, "Holdline", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetStatus("Connecting to " + hold.Name + " at " + hold.Address + "...");

            // sourceClientDataDir stays null: step 2 above already put the
            // client files in place, and handing ConnectToHold a folder here
            // would copy the same 180 MB again on every single join.
            var error = SkympConnector.ConnectToHold(
                skyrimExePath: exePath,
                skyrimDataDir: dataDir,
                sourceClientDataDir: null,
                ip: hold.Ip,
                port: hold.Port,
                masterUrl: MasterUrl,
                masterKey: hold.MasterKey);

            if (error != null)
            {
                ShowProblem(error,
                    "Fix the problem above, then click Join again. If you are not sure "
                    + "what it means, send this message to us - it names the exact cause.");
            }
            else
            {
                SetStatus("Launching Skyrim through SKSE. The game window takes a moment to appear.");

                // The launcher used to stop right here and call it a success. It
                // had only proven the SKSE LOADER started - not that Skyrim lived.
                // So every failure past this line reached the player as silence:
                // the status line said "launching", the game never came up, and
                // nothing ever said why. That is the night Gio lost. Now we watch.
                var launchProblem = await SkympConnector.WatchLaunchAsync(
                    exePath,
                    new Progress<string>(SetStatus));

                if (launchProblem != null)
                {
                    ShowProblem(launchProblem,
                        "Try Fix My Game, which reinstalls the Holdline files and checks "
                        + "your SKSE, then click Join again. If Skyrim still will not start, "
                        + "launch it normally from Steam - if it fails there too, the problem "
                        + "is Skyrim itself, not Holdline.");
                }
                else
                {
                    SetStatus("Skyrim is running. Look for " + hold.Name
                              + " once you are past the main menu.");
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus("Connect failed: " + ex.Message);
            MessageBox.Show(ex.ToString(), "Holdline — connect failed",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            JoinButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------
    // SKYRIM DOWNGRADE
    //
    // Steam's 1.7.99 update breaks every script-extender mod. Steam can
    // hand back an older build through its own console — download_depot —
    // which is Valve's own feature serving Valve's own files to someone
    // who already owns the game. Nothing here is cracked, patched or
    // redistributed: Holdline never touches a game file it didn't get
    // from Steam a minute earlier.
    //
    // The manifest IDs below were read off SteamDB and cross-checked
    // three ways: all three carry the same 17 Jan 2024 16:01:14 UTC
    // timestamp (one Bethesda build lands on every depot at once), and
    // three other known trios on the same list matched their public
    // release dates exactly.
    //
    // TAKE CARE EDITING: the first two manifests both begin with 8 and
    // look almost identical. 8442... belongs to 489831, 8042... to
    // 489832. Swapping them gives the player a mismatched install.
    // ---------------------------------------------------------------
    private const string DowngradeAppId = "489830";

    private sealed class DepotStep
    {
        public string DepotId = "";
        public string ManifestId = "";
        public string What = "";

        public string Command =>
            "download_depot " + DowngradeAppId + " " + DepotId + " " + ManifestId;

        public string FolderName => "depot_" + DepotId;
    }

    private static readonly DepotStep[] DowngradeSteps = new[]
    {
        new DepotStep
        {
            DepotId = "489831",
            ManifestId = "8442952117333549665",
            What = "the main game files — about 7 GB, so this is the long one",
        },
        new DepotStep
        {
            DepotId = "489832",
            ManifestId = "8042843504692938467",
            What = "the rest of the game files — about 8 GB",
        },
        new DepotStep
        {
            DepotId = "489833",
            ManifestId = "1914580699073641964",
            What = "the game program itself — small and quick",
        },
    };

    /// <summary>
    /// Walks the player back to Skyrim 1.6.1170.
    ///
    /// Steam only accepts download_depot through its own console window,
    /// and no URL can run a command in it, so the player pastes three
    /// lines. Holdline opens the console, puts each line on the clipboard
    /// in turn, and does every part after that — finding the files,
    /// copying them into place, and stopping Steam from undoing it.
    ///
    /// Returns false always: even on success the player has to relaunch,
    /// because the exe that's running is the one we just replaced.
    /// </summary>
    private async Task<bool> OfferDowngrade(string skyrimRoot, string installedVersion)
    {
        var start = MessageBox.Show(
            "Steam updated Skyrim to " + installedVersion + ", and that update stops every " +
            "script-extender mod from loading — Holdline included. Nothing is wrong with " +
            "your game or your PC. It happened to everyone at once.\n\n" +
            "Holdline can put Skyrim back to the version that works. Steam does the actual " +
            "work; these are Steam's own files, and you already own them.\n\n" +
            "Two things to know before you start:\n\n" +
            "  •  It downloads about 15 GB, so give it time and make sure you have the room.\n" +
            "  •  You'll paste three short lines into a Steam window. Holdline copies each " +
            "one for you — you just click, paste, and press Enter.\n\n" +
            "The update added nothing to the PC game, so you lose nothing by going back. " +
            "Your saves and mods are untouched.\n\n" +
            "Start now?",
            "Holdline — put Skyrim back",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (start != MessageBoxResult.Yes)
        {
            SetStatus("Skyrim is still on " + installedVersion + ", so Holdline can't launch it yet.");
            return false;
        }

        // Open Steam's console. This is a real Steam URL, but if Steam is
        // shut or the handler isn't registered it simply won't appear —
        // so the player is told what they're looking for either way,
        // rather than being left staring at a desktop.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "steam://open/console",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't open the Steam console: " + ex.Message);
        }

        MessageBox.Show(
            "Steam should now be showing a CONSOLE tab, with a box you can type in.\n\n" +
            "If you can't see it: click on Steam, and look along the top row where " +
            "STORE and LIBRARY are. CONSOLE sits on that row.\n\n" +
            "Leave that window open and come back here.",
            "Holdline — find the Steam console",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        for (int i = 0; i < DowngradeSteps.Length; i++)
        {
            var step = DowngradeSteps[i];
            var human = (i + 1) + " of " + DowngradeSteps.Length;

            // Clipboard access can fail if another program is holding it.
            // Worth catching, because the player can still type the line
            // by hand from the dialog if it does.
            bool copied = true;
            try
            {
                Clipboard.SetText(step.Command);
            }
            catch (Exception)
            {
                copied = false;
            }

            SetStatus("Downgrade step " + human + " — waiting for Steam to finish downloading.");

            var paste = MessageBox.Show(
                "Step " + human + ": " + step.What + ".\n\n" +
                (copied
                    ? "The line is already copied. Click into the Steam console, press " +
                      "Ctrl+V, then press Enter.\n\n"
                    : "Holdline couldn't reach your clipboard, so type this line into the " +
                      "Steam console exactly and press Enter:\n\n") +
                step.Command + "\n\n" +
                "Steam will look like it's doing nothing for a while. That's normal — it " +
                "shows no progress bar for this. Wait until it prints a line ending in " +
                "\"Depot download complete\".\n\n" +
                "Click OK once that line appears. Click Cancel to stop and do this later.",
                "Holdline — step " + human,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (paste != MessageBoxResult.OK)
            {
                SetStatus("Downgrade stopped at step " + human + ". Nothing on your PC was changed.");
                return false;
            }
        }

        // FIND IT FIRST - 22 Sep 2026. The note below was right that
        // download_depot writes into the Steam INSTALL folder and not the
        // library Skyrim lives in - but that folder is not a guess: Steam
        // records it in the registry (SteamPath), the same key the Skyrim
        // hunt above already reads. So look there for app_489830 holding all
        // three depot folders, and only if that fails fall back to asking.
        // The first outside tester spent ten minutes hunting for this folder.
        var found = FindDownloadedDepotRoot();
        string sourceRoot;
        if (found != null)
        {
            sourceRoot = found;
            SetStatus("Found Steam's download folder: " + sourceRoot);
        }
        else
        {
        MessageBox.Show(
            "Downloads done. Last thing.\n\n" +
            "In the Steam console, find the line that says \"Depot download complete\". " +
            "Just before it, Steam printed the folder it saved everything into. It ends " +
            "with:\n\n" +
            "    app_" + DowngradeAppId + "\\depot_" + DowngradeSteps[0].DepotId + "\n\n" +
            "Click OK, then point Holdline at the app_" + DowngradeAppId + " folder — " +
            "the one ABOVE the depot folders, not a depot folder itself.",
            "Holdline — where did Steam put it?",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        var picker = new OpenFolderDialog
        {
            Title = "Select the app_" + DowngradeAppId + " folder Steam just created",
        };
        var steamGuess = SteamInstallFolder();
        if (steamGuess != null)
        {
            var contentGuess = Path.Combine(steamGuess, "steamapps", "content");
            if (Directory.Exists(contentGuess)) picker.InitialDirectory = contentGuess;
        }

        if (picker.ShowDialog() != true)
        {
            SetStatus("Downgrade paused: the downloaded files are on your PC, but Holdline " +
                      "wasn't shown where. Press Join again to pick up from here.");
            return false;
        }

        sourceRoot = picker.FolderName;
        }

        // Verify all three before copying ANY of them. A half-applied
        // downgrade is a worse place to be than the broken version we
        // started from — mismatched data against a mismatched exe.
        var missing = new List<string>();
        foreach (var step in DowngradeSteps)
        {
            if (!Directory.Exists(Path.Combine(sourceRoot, step.FolderName)))
            {
                missing.Add(step.FolderName);
            }
        }

        if (missing.Count > 0)
        {
            var msg = "That folder is missing " + missing.Count + " of the three downloads:\n\n" +
                      "    " + string.Join("\n    ", missing) + "\n\n" +
                      "Picked:\n" + sourceRoot + "\n\n" +
                      "Holdline hasn't changed anything. Either a download didn't finish, or " +
                      "this is the wrong folder — it should be the app_" + DowngradeAppId +
                      " folder that holds all three depot folders.";

            SetStatus("Downgrade stopped: " + missing.Count + " of 3 downloads weren't in that folder.");
            MessageBox.Show(msg, "Holdline", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Free space, measured — not assumed. A copy that runs out of room
        // halfway is the single worst outcome this whole feature can produce:
        // old data files sitting under a new exe, with Steam's manifest
        // saying everything is fine. Better to stop before touching anything.
        //
        // The size is summed off the folders Steam actually downloaded rather
        // than hardcoded at "about 15 GB", so it stays true if Bethesda ever
        // changes what a depot contains.
        try
        {
            long needed = 0;
            foreach (var step in DowngradeSteps)
            {
                needed += FolderSize(Path.Combine(sourceRoot, step.FolderName));
            }

            var driveRoot = Path.GetPathRoot(Path.GetFullPath(skyrimRoot));
            if (!string.IsNullOrEmpty(driveRoot))
            {
                var free = new DriveInfo(driveRoot).AvailableFreeSpace;

                if (free < needed)
                {
                    const double GB = 1024.0 * 1024.0 * 1024.0;
                    var msg = "There isn't enough room on the drive Skyrim is installed on.\n\n" +
                              "Needed: " + (needed / GB).ToString("0.0") + " GB\n" +
                              "Free: " + (free / GB).ToString("0.0") + " GB\n" +
                              "Short by about " + ((needed - free) / GB).ToString("0.0") + " GB\n\n" +
                              "Holdline hasn't changed anything, and your downloads are still " +
                              "on your PC. Free up some space and press Join again — you won't " +
                              "have to download it a second time.";

                    SetStatus("Not enough free space to put Skyrim back. Nothing was changed.");
                    MessageBox.Show(msg, "Holdline", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
        }
        catch (Exception)
        {
            // Couldn't measure. Carry on rather than block the player over a
            // safeguard — the copy below has its own error handling if the
            // drive does run dry.
        }

        try
        {
            // Off the UI thread: this is 15 GB and would freeze the window
            // solid, which every player reads as a crash.
            await Task.Run(() =>
            {
                foreach (var step in DowngradeSteps)
                {
                    var from = Path.Combine(sourceRoot, step.FolderName);
                    Dispatcher.Invoke(() =>
                        SetStatus("Putting Skyrim back — copying " + step.FolderName + "..."));
                    CopyFolderOver(from, skyrimRoot);
                }
            });
        }
        catch (Exception ex)
        {
            SetStatus("Copy failed: " + ex.Message);
            MessageBox.Show(
                "Holdline couldn't finish copying the files into Skyrim.\n\n" + ex.Message +
                "\n\nIf Steam or Skyrim is running, close both and press Join again.",
                "Holdline — copy failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        var locked = LockSteamManifest(skyrimRoot);

        // SKSE is built against ONE exact game version. Anyone who set up
        // SKSE while they were on the broken build has the wrong one now,
        // and FindSkseLoader only checks that the file exists — it doesn't
        // read its version. Left alone, the next Join would sail past the
        // SKSE step, launch, and fail with nothing on screen explaining why.
        //
        // Renaming the loader (not deleting it) makes FindSkseLoader return
        // null, which sends the player through OfferSkseInstall — the path
        // that reads SKSE's own download page and picks the build matching
        // THIS game version. Nothing is destroyed: the old file is still
        // there under a new name if it's ever wanted.
        var skseRetired = RetireMismatchedSkse(skyrimRoot);

        SetStatus("Skyrim is back on " + SkympConnector.TargetVersion + ". Press Join again to play.");

        MessageBox.Show(
            "Done — Skyrim is back on " + SkympConnector.TargetVersion + ".\n\n" +
            (locked
                ? "Holdline also told Steam to leave this version alone, so it won't quietly " +
                  "update you again. If you ever want Steam updating Skyrim normally: find " +
                  "appmanifest_" + DowngradeAppId + ".acf in your steamapps folder, " +
                  "right-click it, Properties, and untick Read-only.\n\n"
                : "One thing Holdline couldn't do: stop Steam updating you again. In Steam, " +
                  "right-click Skyrim, Properties, Updates, and set it to \"Only update this " +
                  "game when I launch it\". Then always start the game from Holdline.\n\n") +
            (skseRetired
                ? "The add-on Skyrim needs was built for the old version, so Holdline set it " +
                  "aside. Press Join and it'll fetch the right one for you — takes a moment.\n\n"
                : "") +
            "Press Join and you're in.",
            "Holdline — Skyrim restored",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        return false;
    }

    /// <summary>
    /// Adds up every byte under a folder. Used to check free space against
    /// what's really there rather than against a guess.
    /// </summary>
    private static long FolderSize(string path)
    {
        long total = 0;

        foreach (var file in Directory.GetFiles(path))
        {
            total += new FileInfo(file).Length;
        }

        foreach (var dir in Directory.GetDirectories(path))
        {
            total += FolderSize(dir);
        }

        return total;
    }

    /// <summary>
    /// Moves the SKSE loader aside after a version change so the installer
    /// fetches a matching build. Returns true only if a loader was actually
    /// renamed, so the player isn't told about a step that didn't happen.
    /// </summary>
    private static bool RetireMismatchedSkse(string skyrimRoot)
    {
        try
        {
            var exePath = Path.Combine(skyrimRoot, "SkyrimSE.exe");
            var loader = SkympConnector.FindSkseLoader(exePath);

            if (loader == null)
            {
                return false;
            }

            var parked = loader + ".pre-downgrade";

            // A second run would otherwise throw on an existing target.
            if (File.Exists(parked))
            {
                File.Delete(parked);
            }

            File.Move(loader, parked);
            return true;
        }
        catch (Exception)
        {
            // Not fatal. Worst case the player hits the old SKSE failure,
            // which is where they'd have been anyway without this.
            return false;
        }
    }

    /// <summary>
    /// Copies everything from one folder over another, replacing files
    /// that are already there and keeping anything that isn't part of the
    /// copy. Used to lay Steam's depot contents over the live install.
    /// </summary>
    private static void CopyFolderOver(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.GetFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));

            // A read-only file already sitting there — which is exactly
            // what a previous downgrade leaves behind — would throw.
            if (File.Exists(target))
            {
                var attrs = File.GetAttributes(target);
                if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    File.SetAttributes(target, attrs & ~FileAttributes.ReadOnly);
                }
            }

            File.Copy(file, target, true);
        }

        foreach (var dir in Directory.GetDirectories(from))
        {
            CopyFolderOver(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    /// <summary>
    /// Marks Steam's record of the Skyrim install read-only. Steam reads
    /// that file to decide whether an update is due; if it can't rewrite
    /// it, it leaves the install alone. Best-effort — returns false if
    /// the file isn't where expected, so the player gets told to set it
    /// by hand instead of being told it's handled when it isn't.
    /// </summary>
    private static bool LockSteamManifest(string skyrimRoot)
    {
        try
        {
            // ...\steamapps\common\Skyrim Special Edition
            //  -> ...\steamapps\appmanifest_489830.acf
            //
            // TrimEnd first. Path.GetDirectoryName("D:\a\b\") returns
            // "D:\a\b", not "D:\a" — so a saved folder path that happens
            // to carry a trailing slash would walk up one level short and
            // look for the manifest inside common\, where it never is.
            // The player would then be told to set it by hand for no
            // reason, on exactly the machines that stored the path that way.
            var root = skyrimRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var common = Path.GetDirectoryName(root);
            var steamapps = common == null ? null : Path.GetDirectoryName(common);

            if (steamapps == null)
            {
                return false;
            }

            var manifest = Path.Combine(steamapps, "appmanifest_" + DowngradeAppId + ".acf");

            if (!File.Exists(manifest))
            {
                return false;
            }

            File.SetAttributes(manifest, File.GetAttributes(manifest) | FileAttributes.ReadOnly);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Offers to install SKSE from a copy the player downloads themselves.
    /// Returns true only if SKSE is genuinely in place afterwards — never on
    /// the strength of the player having clicked something.
    /// </summary>
    private async Task<bool> OfferSkseInstall(string skyrimRoot)
    {
        // Automatic route first. Holdline reads SKSE's own download page,
        // picks the build matching THIS game's version, and installs it.
        // We host nothing: the player's PC downloads from silverlock
        // directly, which their terms allow. Only if that fails do we fall
        // back to the manual path below, which is proven and still works.
        SetStatus("Getting SKSE. This is a one-time setup step.");

        var autoError = await SkseDownload.DownloadAndInstallAsync(
            skyrimRoot,
            FindSkyrimExe(),
            line => Dispatcher.Invoke(() => SetStatus(line)));

        if (autoError == null)
        {
            SetStatus("SKSE is installed. Carrying on with the join.");
            return true;
        }

        SetStatus("Couldn't fetch SKSE automatically. Falling back to manual setup.");

        var answer = MessageBox.Show(
            "There's one free add-on Skyrim needs before you can play online. " +
            "It's called SKSE.\n\n" +
            "It's made by a different team, and their rules don't let us include it " +
            "in the Holdline download. So you'll grab it yourself — once. Takes about " +
            "a minute, and you'll never have to do it again.\n\n" +
            "Once you've got it, Holdline puts it in the right place for you. You " +
            "don't have to find any folders.\n\n" +
            "YES  —  I already downloaded it. Set it up for me.\n" +
            "NO  —  Show me where to get it.\n" +
            "CANCEL  —  I'll do this another time.",
            "Holdline — one quick thing",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Information);

        if (answer == MessageBoxResult.No)
        {
            OpenUrl(SkseSetup.DownloadPage);

            MessageBox.Show(
                "The download page just opened in your web browser. It's a plain-looking " +
                "page — that's normal, it's the real one.\n\n" +
                "Step 1.  Find the line that says:\n" +
                "             Current Anniversary Edition build 2.2.6\n\n" +
                "Step 2.  Click the download link on THAT line.\n\n" +
                "             Careful — there's another link nearby that says\n" +
                "             \"Current SE build\". That one is for an older\n" +
                "             version of Skyrim and won't work. You want the\n" +
                "             Anniversary Edition one.\n\n" +
                "Step 3.  When it finishes downloading, find the file (it'll be in your\n" +
                "             Downloads folder). Right-click it and choose \"Extract All\",\n" +
                "             then click Extract. A new folder appears.\n\n" +
                "Step 4.  Come back here, click Join, and this time choose YES.\n" +
                "             Pick the folder you just made.\n\n" +
                "That's everything. Holdline takes it from there.",
                "Holdline — getting SKSE",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return false;
        }

        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        string? chosen = null;

        var filePicker = new OpenFileDialog
        {
            Title = "Find the SKSE file you downloaded — or press Cancel to choose a folder instead",
           //  THE WILDCARDS WERE MISSING. This read
           //    "SKSE download (.7z;.zip)|.7z;.zip|All files (.)|."
           //  and a Win32 filter pattern without a wildcard matches a file
           //  called literally ".7z" - so the picker opened showing NOTHING,
           //  whatever the player had downloaded. That is the trap Gio hit,
           //  and it is not a wrong filter, it is an empty one.
           Filter = "SKSE download (*.7z;*.zip)|*.7z;*.zip|All files (*.*)|*.*",
        };

        //  START WHERE THE FILE IS - 22 Sep 2026. Windows reopens a picker
        //  wherever it was last used, and the last thing this player did was
        //  pick the app_489830 depot folder - so the SKSE picker opened on
        //  the depot folders, which hold no .7z at all. The browser put the
        //  download in Downloads, so start there; if the newest SKSE archive
        //  is sitting in it, select that file outright and he only presses
        //  Open. Falls back to plain Downloads, then to whatever Windows had.
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            if (Directory.Exists(downloads))
            {
                filePicker.InitialDirectory = downloads;

                FileInfo? newest = null;

                foreach (var pattern in new[] { "skse*.7z", "skse*.zip" })
                {
                    foreach (var path in Directory.GetFiles(downloads, pattern))
                    {
                        var info = new FileInfo(path);
                        if (newest == null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                        {
                            newest = info;
                        }
                    }
                }

                if (newest != null)
                {
                    filePicker.FileName = newest.FullName;
                }
            }
        }
        catch
        {
            // Any trouble here just means the picker opens where it always did.
        }

        if (filePicker.ShowDialog() == true)
        {
            chosen = filePicker.FileName;
        }
        else
        {
            var folderPicker = new OpenFolderDialog
            {
                Title = "Choose the SKSE folder you unzipped",
            };

            try
            {
                var dl = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(dl)) folderPicker.InitialDirectory = dl;
            }
            catch
            {
            }

            if (folderPicker.ShowDialog() == true)
            {
                chosen = folderPicker.FolderName;
            }
        }

        if (string.IsNullOrEmpty(chosen))
        {
            return false;
        }

        SetStatus("Installing SKSE...");

        var error = SkseSetup.InstallFrom(chosen, skyrimRoot, FindSkyrimExe());

        if (error != null)
        {
            SetStatus(error.Replace("\n", " "));
            MessageBox.Show(error, "Holdline", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        MessageBox.Show(
            "All set — SKSE is installed. You won't have to do that again.\n\n" +
            "Holdline will carry on joining now.",
            "Holdline",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        return true;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // A browser that refuses to open is not worth crashing the
            // launcher over; the dialog already showed the address.
        }
    }

    // ------------------------------------------------------------------
    //  SINGLE PLAYER SWITCH - 1 Oct 2026. Ben: "we need an unlink button
    //  that makes your game go back to single player with one click." The
    //  Holdline client loads whenever SKSE starts the game; in a single-
    //  player save it empties the world and shuts the game. Turning it off
    //  is two renames - Data\Platform and the SkyMP client DLL - and turning
    //  it back on is the reverse. The button is made in code so it needs no
    //  change to the window layout: it sits beside Join.
    // ------------------------------------------------------------------
    private Button? _singleButton;

    private void AddSinglePlayerButton(Window host)
    {
        try
        {
            //  Find the first button the settings window already has and sit
            //  beside it, wearing its exact style, colours, size and margin.
            Button? sample = null;
            Panel? panel = null;
            var queue = new Queue<DependencyObject>();
            if (host.Content is DependencyObject rootObj) queue.Enqueue(rootObj);
            while (queue.Count > 0 && sample == null)
            {
                var node = queue.Dequeue();
                if (node is Button b && b.Parent is Panel p) { sample = b; panel = p; break; }
                foreach (var child in LogicalTreeHelper.GetChildren(node))
                {
                    if (child is DependencyObject d) queue.Enqueue(d);
                }
            }
            if (sample == null || panel == null) return;

            _singleButton = new Button
            {
                Style = sample.Style,
                Background = sample.Background,
                Foreground = sample.Foreground,
                BorderBrush = sample.BorderBrush,
                BorderThickness = sample.BorderThickness,
                FontFamily = sample.FontFamily,
                FontSize = sample.FontSize,
                FontWeight = sample.FontWeight,
                Padding = sample.Padding,
                Margin = sample.Margin,
                MinWidth = sample.MinWidth,
                Height = double.IsNaN(sample.Height) ? double.NaN : sample.Height,
                HorizontalAlignment = sample.HorizontalAlignment,
                ToolTip = "Turn Holdline off so Skyrim runs as a normal single-player game. Press again (or Fix my game) to turn it back on."
            };
            _singleButton.Click += SinglePlayerButton_Click;
            var at = panel.Children.IndexOf(sample);
            panel.Children.Insert(at + 1, _singleButton);
            RefreshSinglePlayerLabel();
        }
        catch
        {
            // No button is better than a crash on an unexpected layout.
        }
    }

    //  PROGRAM FILES - 3 Oct 2026. A Skyrim under Program Files cannot be
    //  written by a normal program; Besnik's Fix my game "succeeded" while his
    //  old files stayed. When the chosen Skyrim folder is under Program Files
    //  and this process is not elevated, Holdline restarts itself elevated
    //  before Join or Fix my game touch anything. Returns true when it has
    //  handed over to the elevated copy (the caller must stop).
    public static bool IsUnderProgramFiles(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(root) && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static bool RelaunchElevatedIfNeeded(string? skyrimFolder)
    {
        if (!IsUnderProgramFiles(skyrimFolder) || IsElevated()) return false;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
            return true;
        }
        catch
        {
            //  the person declined the prompt, or it could not be shown
            return false;
        }
    }

    private static string PlatformPath(string skyrim) => Path.Combine(skyrim, "Data", "Platform");
    private static string PlatformOffPath(string skyrim) => Path.Combine(skyrim, "Data", "Platform-off");
    private static string PluginsPath(string skyrim) => Path.Combine(skyrim, "Data", "Platform", "Plugins");
    private static string ClientDllPath(string skyrim) => Path.Combine(skyrim, "Data", "SKSE", "Plugins", "MpClientPlugin.dll");
    private static string PlatformDllPath(string skyrim) => Path.Combine(skyrim, "Data", "SKSE", "Plugins", "SkyrimPlatform.dll");
    private static string EngineFixesDllPath(string skyrim) => Path.Combine(skyrim, "Data", "SKSE", "Plugins", "EngineFixes.dll");
    private static string NirnLabDllPath(string skyrim) => Path.Combine(skyrim, "Data", "SKSE", "Plugins", "NirnLabUIPlugin.dll");
    //  The AvP Alien mod's SKSE plugin and its ESP. Their presence means the
    //  Alien mod is installed, so single player must keep NirnLab (the F10 3D
    //  editor needs it).
    private static string AlienDllPath(string skyrim) => Path.Combine(skyrim, "Data", "SKSE", "Plugins", "AvPAlien.dll");
    private static string AlienEspPath(string skyrim) => Path.Combine(skyrim, "Data", AlienEspName);
    //  The SKSE64 Preloader half of SSE Engine Fixes lives beside SkyrimSE.exe,
    //  NOT in Data. It is the proxy d3dx9_42.dll.
    private static string PreloaderPath(string skyrim) => Path.Combine(skyrim, "d3dx9_42.dll");
    private const string EngineFixesParkSuffix = ".holdline-off";

    //  Stage 1 (single player only): the AvP Alien mod. Its own ESP grants the
    //  native transform powers (Alien / Praetorian / Predalien / Facehugger /
    //  Chestburster Form) + an MCM, which run in plain Skyrim with no Holdline
    //  client. So single player just needs the ESP ENABLED in plugins.txt; the
    //  server keeps it OFF until Stage 2 wires true multiplayer load-order
    //  matching + sync. (The Holdline admin menu can't do this in single player
    //  - it rides NirnLab, which single player parks to let the game save.)
    private const string AlienEspName = "AvPAlien.esp";
    private static string PluginsTxtPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Skyrim Special Edition", "plugins.txt");

    //  THE SWITCH NEVER RENAMES A FOLDER - 1 Oct 2026.
    //
    //  The first version renamed Data\Platform to Platform-off. Windows refuses
    //  to rename a folder while ANY program holds it open - a cmd window sitting
    //  inside it, File Explorer open on Plugins, Vortex watching Data - and it
    //  says "Access to the path is denied", which this button then blamed on
    //  Skyrim running. It failed on the build machine with Skyrim closed.
    //
    //  Files renamed in place do not care who has the folder open. So OFF is:
    //    Data\Platform\Plugins\*.js            ->  *.js.off   (Platform loads .js only)
    //    Data\SKSE\Plugins\MpClientPlugin.dll  ->  .dll.off   (SKSE loads .dll only)
    //    Data\SKSE\Plugins\SkyrimPlatform.dll  ->  .dll.off   (6 Oct 2026: Ben's
    //         single-player crash log showed SkyrimPlatform v2.9 loaded with
    //         nothing to run; he wants single player to be plain Skyrim)
    //  and ON is the reverse. OFF also puts SSE Engine Fixes back
    //  (EngineFixes.dll.holdline-off -> EngineFixes.dll) because Fix my game
    //  parks it for multiplayer and single player wants it; ON parks it again.
    //
    //  A Platform-off folder left by the old switch, or by a hand rename, is
    //  still put back by the ON path, with retries, so nobody is stranded.

    private bool HoldlineIsOff()
    {
        if (string.IsNullOrEmpty(_skyrimFolder)) return false;
        var root = _skyrimFolder!;
        if (Directory.Exists(PlatformOffPath(root)) && !Directory.Exists(PlatformPath(root))) return true;
        if (File.Exists(ClientDllPath(root) + ".off") && !File.Exists(ClientDllPath(root))) return true;
        if (File.Exists(PlatformDllPath(root) + ".off") && !File.Exists(PlatformDllPath(root))) return true;
        var client = Path.Combine(PluginsPath(root), "skymp5-client.js");
        return File.Exists(client + ".off") && !File.Exists(client);
    }

    private static void MoveWithRetry(string from, string to, bool isFolder)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (isFolder) Directory.Move(from, to);
                else
                {
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                System.Threading.Thread.Sleep(250);
            }
        }
        throw new IOException("Couldn't rename " + from + " -> " + Path.GetFileName(to) + ": " + (last?.Message ?? "unknown"));
    }

    //  What a switch changed, so the status line can name it instead of just
    //  counting files.
    private sealed class SwitchResult
    {
        public readonly List<string> Off = new();    // parked (will not load)
        public readonly List<string> On = new();     // put back / restored
        public readonly List<string> Notes = new();  // anything the player should know
        public int Count => Off.Count + On.Count;
    }

    //  SSE Engine Fixes is all-or-nothing. It is two files that must share one
    //  on/off state:
    //    - Main File : Data\SKSE\Plugins\EngineFixes.dll
    //    - Preloader : d3dx9_42.dll beside SkyrimSE.exe (a proxy DLL)
    //  (Address Library 1.6.X is the third piece; Holdline ships the versionlib
    //  bins in the bundle and the switch never parks them, so they are always
    //  present.) EngineFixes.dll loaded WITHOUT its preloader shows "Engine
    //  Fixes did not pre-load" and closes the game, so we never leave one half
    //  on: if we cannot turn BOTH on, we turn BOTH off.
    private static void SetEngineFixes(string root, bool enable, SwitchResult result)
    {
        var main = EngineFixesDllPath(root);
        var mainOff = main + EngineFixesParkSuffix;
        var pre = PreloaderPath(root);
        var preOff = pre + EngineFixesParkSuffix;

        var mainAvailable = File.Exists(main) || File.Exists(mainOff);
        var preAvailable = File.Exists(pre) || File.Exists(preOff);

        if (enable && mainAvailable && preAvailable)
        {
            var changed = false;
            if (!File.Exists(main) && File.Exists(mainOff)) { MoveWithRetry(mainOff, main, isFolder: false); changed = true; }
            if (!File.Exists(pre) && File.Exists(preOff)) { MoveWithRetry(preOff, pre, isFolder: false); changed = true; }
            if (changed) result.On.Add("SSE Engine Fixes (+ preloader)");
            return;
        }

        //  Either we are turning Engine Fixes off (multiplayer), or we cannot
        //  turn both halves on. Park whichever half is live so neither loads
        //  alone and crashes the game.
        var parkedMain = false;
        var parkedPre = false;
        if (File.Exists(main)) { MoveWithRetry(main, mainOff, isFolder: false); parkedMain = true; }
        if (File.Exists(pre)) { MoveWithRetry(pre, preOff, isFolder: false); parkedPre = true; }
        if (parkedMain || parkedPre) result.Off.Add("SSE Engine Fixes (+ preloader)");

        if (enable && !(mainAvailable && preAvailable))
        {
            var missingHalf = !mainAvailable ? "EngineFixes.dll" : "the d3dx9_42.dll preloader";
            result.Notes.Add("Engine Fixes left off (" + missingHalf + " is missing; half of it would crash Skyrim)");
        }
    }

    private static string SummariseOff(SwitchResult r)
    {
        var parts = new List<string>();
        if (r.Off.Count > 0) parts.Add("Switched off: " + string.Join(", ", r.Off));
        if (r.On.Count > 0) parts.Add("on for single player: " + string.Join(", ", r.On));
        parts.AddRange(r.Notes);
        return parts.Count > 0 ? string.Join(". ", parts) + "." : "Nothing needed changing for single player.";
    }

    private static string SummariseOn(SwitchResult r)
    {
        var parts = new List<string>();
        if (r.On.Count > 0) parts.Add("Turned back on: " + string.Join(", ", r.On));
        if (r.Off.Count > 0) parts.Add("parked: " + string.Join(", ", r.Off));
        parts.AddRange(r.Notes);
        return parts.Count > 0 ? string.Join(". ", parts) + "." : "Holdline was already on.";
    }

    /// <summary>
    /// Single player only: enables AvPAlien.esp in plugins.txt so the Alien
    /// mod's native powers and MCM work in plain Skyrim. Returns true if it
    /// flipped it on. Only ever enables a file that is actually in Data - an
    /// enabled entry whose file is missing kills the game at the main menu
    /// (the exact landmine PluginListGuard exists to defuse).
    /// </summary>
    private static bool EnableAlienEsp(string root)
    {
        try
        {
            var esp = Path.Combine(root, "Data", AlienEspName);
            if (!File.Exists(esp)) return false;

            var txt = PluginsTxtPath();
            var lines = File.Exists(txt)
                ? new List<string>(File.ReadAllLines(txt))
                : new List<string>();

            for (var i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (string.Equals(t, "*" + AlienEspName, StringComparison.OrdinalIgnoreCase))
                {
                    return false; // already enabled, nothing to do
                }
                if (string.Equals(t, AlienEspName, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = "*" + AlienEspName; // was disabled - tick it on
                    WritePluginsTxt(txt, lines);
                    return true;
                }
            }

            lines.Add("*" + AlienEspName);
            WritePluginsTxt(txt, lines);
            return true;
        }
        catch
        {
            return false; // never block the switch over a load-order edit
        }
    }

    //  The Alien mod is kept ON everywhere; Fix my game uses the matching
    //  PluginListGuard.EnableAlienEsp so the behaviour is consistent.

    private static void WritePluginsTxt(string path, List<string> lines)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllLines(path, lines);
    }

    /// <summary>Puts every Holdline file the switch parks back where SKSE and Platform load it, and re-parks Engine Fixes (both halves) for multiplayer.</summary>
    private static SwitchResult TurnHoldlineOn(string root)
    {
        var result = new SwitchResult();
        var platform = PlatformPath(root);
        var platformOff = PlatformOffPath(root);
        if (Directory.Exists(platformOff) && !Directory.Exists(platform))
        {
            MoveWithRetry(platformOff, platform, isFolder: true);
            result.On.Add("Holdline Platform folder");
        }
        var dll = ClientDllPath(root);
        if (File.Exists(dll + ".off"))
        {
            MoveWithRetry(dll + ".off", dll, isFolder: false);
            result.On.Add("MpClientPlugin.dll");
        }
        var spDll = PlatformDllPath(root);
        if (File.Exists(spDll + ".off"))
        {
            MoveWithRetry(spDll + ".off", spDll, isFolder: false);
            result.On.Add("SkyrimPlatform.dll");
        }
        //  Restore NirnLab if single player parked it (it only does so when the
        //  Alien mod is absent). Holdline always needs the NirnLab browser, so
        //  this is unconditional.
        var nirn = NirnLabDllPath(root);
        if (File.Exists(nirn + ".off"))
        {
            MoveWithRetry(nirn + ".off", nirn, isFolder: false);
            result.On.Add("NirnLab UI");
        }
        var plugins = PluginsPath(root);
        if (Directory.Exists(plugins))
        {
            var js = 0;
            foreach (var parked in Directory.GetFiles(plugins, "*.js.off"))
            {
                MoveWithRetry(parked, parked.Substring(0, parked.Length - 4), isFolder: false);
                js++;
            }
            if (js > 0) result.On.Add(js + " Holdline script(s)");
        }
        //  Multiplayer wants Engine Fixes OFF - both halves parked together.
        SetEngineFixes(root, enable: false, result);

        //  The Alien mod is ON everywhere now - keep it enabled on the way to
        //  the server too (same enable used by the switch and Fix my game).
        if (EnableAlienEsp(root)) result.On.Add("Alien mod");
        return result;
    }

    /// <summary>Parks Holdline's files so plain single-player Skyrim can save, and turns Engine Fixes (both halves) ON. Reversed by the button again, Join, or Fix my game.</summary>
    private static SwitchResult TurnHoldlineOff(string root)
    {
        var result = new SwitchResult();
        var plugins = PluginsPath(root);
        if (Directory.Exists(plugins))
        {
            var js = 0;
            foreach (var f in Directory.GetFiles(plugins, "*.js"))
            {
                MoveWithRetry(f, f + ".off", isFolder: false);
                js++;
            }
            if (js > 0) result.Off.Add(js + " Holdline script(s)");
        }
        var dll = ClientDllPath(root);
        if (File.Exists(dll))
        {
            MoveWithRetry(dll, dll + ".off", isFolder: false);
            result.Off.Add("MpClientPlugin.dll");
        }
        //  6 Oct 2026: SkyrimPlatform.dll parked too, so single player is plain
        //  Skyrim rather than SkyrimPlatform loaded with nothing to run.
        var spDll = PlatformDllPath(root);
        if (File.Exists(spDll))
        {
            MoveWithRetry(spDll, spDll + ".off", isFolder: false);
            result.Off.Add("SkyrimPlatform.dll");
        }
        //  NirnLab UI and single-player saving - corrected 18 Oct 2026.
        //
        //  The save bug (Save greys out) is NOT caused by NirnLab merely being
        //  loaded. It is caused by a NirnLab PAGE being OPEN - which only the
        //  Holdline admin menu (a SkyrimPlatform plugin) does. SkyrimPlatform is
        //  parked just above, so in single player nothing opens a NirnLab page,
        //  NirnLab sits idle, and saving works with it still loaded. Tested on
        //  Ben's machine: NirnLab present + SkyrimPlatform parked -> F10 editor
        //  opens AND saving works.
        //
        //  And the AvP Alien mod's F10 3D editor REQUIRES NirnLab. Parking it
        //  broke the Alien editor in single player. So: when the Alien mod is
        //  installed, KEEP NirnLab (and restore it if an older switch parked it).
        //  Only park NirnLab when the Alien mod is absent - a harmless guard for
        //  a setup that has no use for NirnLab in single player anyway.
        var nirn = NirnLabDllPath(root);
        var alienInstalled = File.Exists(AlienDllPath(root)) || File.Exists(AlienEspPath(root));
        if (alienInstalled)
        {
            if (File.Exists(nirn + ".off") && !File.Exists(nirn))
            {
                MoveWithRetry(nirn + ".off", nirn, isFolder: false);
                result.On.Add("NirnLab UI (Alien editor)");
            }
        }
        else if (File.Exists(nirn))
        {
            MoveWithRetry(nirn, nirn + ".off", isFolder: false);
            result.Off.Add("NirnLab UI");
        }
        //  Single player wants Engine Fixes ON - both halves together, never one.
        SetEngineFixes(root, enable: true, result);

        //  Keep the Alien mod on (its transform powers + MCM) for single player.
        if (EnableAlienEsp(root)) result.On.Add("Alien mod");
        return result;
    }

    private void RefreshSinglePlayerLabel()
    {
        if (_singleButton == null) return;
        try
        {
            if (string.IsNullOrEmpty(_skyrimFolder)) _skyrimFolder = LoadSavedSkyrimFolder() ?? AutoDetectSkyrimFolder();
            _singleButton.Content = HoldlineIsOff() ? "Holdline: OFF - turn on" : "Single player";
        }
        catch
        {
        }
    }

    private static string? RunningBlocker()
    {
        foreach (var name in new[] { "SkyrimSE", "skse64_loader" })
        {
            try { if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0) return "Skyrim"; } catch { }
        }
        return null;
    }

    private void SinglePlayerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureSkyrimFolder())
            {
                SetStatus("Holdline doesn't know where Skyrim is yet.");
                return;
            }
            var root = _skyrimFolder!;
            var blocker = RunningBlocker();
            if (blocker != null)
            {
                SetStatus(blocker + " is still running. Close it, then press this again.");
                return;
            }

            if (HoldlineIsOff())
            {
                var r = TurnHoldlineOn(root);
                SetStatus("Holdline is back on. " + SummariseOn(r) + " Press Join to play on a server.");
            }
            else
            {
                if (!Directory.Exists(PlatformPath(root)) && !File.Exists(ClientDllPath(root)))
                {
                    SetStatus("Holdline isn't installed in this Skyrim folder, so there's nothing to turn off.");
                    return;
                }
                var r = TurnHoldlineOff(root);
                var summary = SummariseOff(r);

                //  Single player still needs SKSE: SkyUI and most mods throw
                //  "SKSE64 is not running" when Skyrim is started from Steam.
                //  So start it the right way ourselves instead of telling the
                //  player to go find the loader.
                var exe = Path.Combine(root, "SkyrimSE.exe");
                var loader = SkympConnector.FindSkseLoader(exe);
                if (loader != null && File.Exists(loader))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = loader,
                        WorkingDirectory = Path.GetDirectoryName(loader) ?? root,
                        UseShellExecute = true
                    });
                    SetStatus("Single player ready. " + summary + " Starting Skyrim through SKSE. Press this button again, Join, or Fix my game to turn Holdline back on.");
                }
                else
                {
                    SetStatus("Single player ready. " + summary + " Start Skyrim through skse64_loader.exe - starting it from Steam gives the SkyUI 'SKSE64 is not running' error.");
                }
            }
            RefreshSinglePlayerLabel();
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't switch: " + ex.Message);
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Put back the original copies of the five Skyrim script files " +
            "Holdline replaces?\n\n" +
            "This only restores files Holdline itself backed up. It does not " +
            "remove mods, and it does not undo client files copied in by hand " +
            "or by another server's launcher.",
            "Holdline",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (!EnsureSkyrimFolder())
            {
                SetStatus("Holdline doesn't know where Skyrim is, so there's nothing to restore.");
                return;
            }

            var restored = SkympConnector.RestoreVanillaFiles(Path.Combine(_skyrimFolder!, "Data"));

            if (restored == 0)
            {
                SetStatus("Nothing to restore: Holdline has no backups in this Data folder.");
            }
            else
            {
                SetStatus("Restored " + restored + " of 5 script files from Holdline backups.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Restore failed: " + ex.Message);
            MessageBox.Show(ex.ToString(), "Holdline — restore failed",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
