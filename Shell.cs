// Shell.cs
// ---------------------------------------------------------------------------
// The four things the new layout needs that the original code-behind never had:
// background music with a mute that sticks, live search over the hold list, and
// a refresh button.
//
// WHY THIS IS A SEPARATE FILE:
//   MainWindow.xaml.cs is 31KB of join logic that was debugged the hard way on
//   2026-07-29 and is now proven end to end. Editing it to bolt on a music
//   player is how you reintroduce a crash. This is a partial class instead, so
//   the proven file is never touched.
//
// WHY EVERYTHING IS WRAPPED IN try/catch:
//   None of this is load-bearing. If the music can't play, or the mute file
//   can't be read, the launcher must still list holds and still join. Sound is
//   decoration; joining is the product. A decoration is never allowed to take
//   the product down with it.
//
// HOW THE AUDIO SHIPS:
//   The three MP3s are embedded inside the exe, then written out to
//   %AppData%\Holdline\sound\ the first time the launcher runs. WPF's
//   MediaPlayer is built on DirectShow and will not read audio straight out of
//   an embedded resource, so it needs a real file on disk. Extracting once and
//   reusing it is the standard way around that.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HoldlineLauncher;

public partial class MainWindow
{
    // --- music -------------------------------------------------------------

    private MediaPlayer? _music;
    private List<string> _tracks = new();
    private int _trackIndex;
    private bool _muted;

    // Background music, not a soundtrack. Loud enough to set a mood, quiet
    // enough that nobody reaches for the mute out of irritation.
    private const double MusicVolume = 0.34;

    private static string SoundFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Holdline", "sound");

    private static string MuteFlagPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Holdline", "sound-muted.txt");

    /// <summary>
    /// Runs once, after the window and every control in it exists. Kept
    /// deliberately dull: set up search, then start music, each in its own
    /// guard so one failing does not cancel the other.
    /// </summary>
    private void Shell_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetUpSearch();
        }
        catch
        {
            // Search is a convenience. Losing it leaves the full list, which
            // is exactly what the launcher showed before this file existed.
        }

        try
        {
            // Fire and forget. The strip appears if and when Supabase answers;
            // nothing else waits on it.
            _ = LoadPromosAsync();
        }
        catch
        {
        }

        try
        {
            InitAppearanceButton();
        }
        catch
        {
            // Cosmetics are optional twice over. Never load-bearing.
        }

        try
        {
            StartMusic();
        }
        catch
        {
            // Silence is an acceptable outcome. A crash is not.
            SafeMuteLabel("Sound off");
        }
    }

    // -----------------------------------------------------------------------
    // MUSIC
    // -----------------------------------------------------------------------

    private void StartMusic()
    {
        _muted = ReadMuteFlag();
        SafeMuteLabel(_muted ? "Sound off" : "Sound on");

        _tracks = ExtractTracks();
        if (_tracks.Count == 0)
        {
            // No audio was embedded in this build. Say so plainly and switch
            // the button off, rather than leaving a mute control that toggles
            // between two kinds of silence.
            SafeMuteLabel("No sound");
            try
            {
                MuteButton.IsEnabled = false;
            }
            catch
            {
            }

            return;
        }

        _music = new MediaPlayer();
        // Mute is driven by IsMuted (a hard output mute), not by dropping Volume
        // to 0. Volume-0 does not reliably hold across Open(): a just-opened
        // track could play at full volume for about a second before the old
        // Volume took hold, which is the blip heard when a muted song changed.
        // IsMuted holds across Open and is re-applied on every track below.
        _music.Volume = MusicVolume;
        _music.IsMuted = _muted;
        // Open() is async: the track is not actually ready until MediaOpened.
        // Properties set before then can be missed, which is how a muted track
        // change still blipped. Re-assert the mute the instant the track opens,
        // just before it becomes audible, so a muted change is silent for good.
        _music.MediaOpened += (_, _) => { if (_music is not null) { _music.Volume = MusicVolume; _music.IsMuted = _muted; } };
        _music.MediaEnded += (_, _) => NextTrack();

        // MediaFailed fires for a corrupt or half-written file. Skipping to
        // the next track is better than sitting in silence for the rest of
        // the session.
        _music.MediaFailed += (_, _) => NextTrack();

        _trackIndex = 0;
        PlayCurrent();
    }

    private void PlayCurrent()
    {
        if (_music is null || _tracks.Count == 0)
        {
            return;
        }

        try
        {
            var path = _tracks[_trackIndex % _tracks.Count];
            _music.Open(new Uri(path, UriKind.Absolute));
            // Re-apply the mute to the newly opened track BEFORE Play, so a muted
            // song change starts silent instead of blipping for a second.
            _music.Volume = MusicVolume;
            _music.IsMuted = _muted;
            _music.Play();
        }
        catch
        {
            // Leave it alone rather than retrying in a loop, which would spin
            // the CPU on a genuinely broken file.
        }
    }

    private void NextTrack()
    {
        if (_tracks.Count == 0)
        {
            return;
        }

        _trackIndex = (_trackIndex + 1) % _tracks.Count;
        PlayCurrent();
    }

    /// <summary>
    /// Writes the embedded MP3s to disk and returns their paths in play order.
    /// Files already on disk at the right size are left alone, so this costs
    /// nothing after the first launch.
    /// </summary>
    private static List<string> ExtractTracks()
    {
        var paths = new List<string>();

        try
        {
            Directory.CreateDirectory(SoundFolder);
        }
        catch
        {
            return paths;
        }

        var asm = Assembly.GetExecutingAssembly();

        // Sorted by name, which is why the files are called track1, track2,
        // track3. The order in the folder is the order they play, and
        // track1 is the one Ben picked to open with.
        var names = asm.GetManifestResourceNames()
                       .Where(n => n.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                       .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                       .ToList();

        foreach (var name in names)
        {
            try
            {
                // Resource names can carry a namespace prefix depending on how
                // the project was set up. Only the tail is wanted on disk.
                var leaf = name.Split('.').Length > 2
                    ? string.Join(".", name.Split('.').Reverse().Take(2).Reverse())
                    : name;

                var target = Path.Combine(SoundFolder, leaf);

                using var src = asm.GetManifestResourceStream(name);
                if (src is null)
                {
                    continue;
                }

                var needsWrite = true;
                if (File.Exists(target))
                {
                    try
                    {
                        needsWrite = new FileInfo(target).Length != src.Length;
                    }
                    catch
                    {
                        needsWrite = true;
                    }
                }

                if (needsWrite)
                {
                    using var dst = File.Create(target);
                    src.CopyTo(dst);
                }

                paths.Add(target);
            }
            catch
            {
                // Skip the one that failed, keep the rest.
            }
        }

        return paths;
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _muted = !_muted;

            if (_music is not null)
            {
                // IsMuted rather than Pause, so unmuting drops you back into the
                // track where it got to instead of restarting it. IsMuted also
                // holds across the next track change (see PlayCurrent), so the
                // mute stays put when a song ends and the next one starts.
                _music.IsMuted = _muted;
            }

            SafeMuteLabel(_muted ? "Sound off" : "Sound on");
            WriteMuteFlag(_muted);
        }
        catch
        {
            // Worst case the label and the audio disagree until next launch.
        }
    }

    private static bool ReadMuteFlag()
    {
        try
        {
            return File.Exists(MuteFlagPath);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteMuteFlag(bool muted)
    {
        try
        {
            var dir = Path.GetDirectoryName(MuteFlagPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (muted)
            {
                File.WriteAllText(MuteFlagPath, "muted");
            }
            else if (File.Exists(MuteFlagPath))
            {
                File.Delete(MuteFlagPath);
            }
        }
        catch
        {
            // Not worth a dialog. The setting just won't survive a restart.
        }
    }

    private void SafeMuteLabel(string text)
    {
        try
        {
            if (MuteGlyph is not null)
            {
                MuteGlyph.Text = text;
            }
        }
        catch
        {
        }
    }

    // -----------------------------------------------------------------------
    // SEARCH
    // -----------------------------------------------------------------------

    /// <summary>
    /// Attaches a filter to the view over the existing holds collection. The
    /// collection itself is never modified, so nothing the join logic does
    /// with it changes.
    /// </summary>
    private void SetUpSearch()
    {
        var view = CollectionViewSource.GetDefaultView(holds);
        if (view is null)
        {
            return;
        }

        view.Filter = item =>
        {
            if (item is not Hold hold)
            {
                return false;
            }

            // --- typed search -------------------------------------------------
            var term = SearchBox?.Text;
            if (!string.IsNullOrWhiteSpace(term))
            {
                term = term.Trim();

                var hit = Contains(hold.Name, term)
                       || Contains(hold.Tagline, term)
                       || Contains(hold.Framework, term)
                       || Contains(hold.Language, term)
                       || hold.Tags.Exists(t => Contains(t, term));

                if (!hit)
                {
                    return false;
                }
            }

            // --- tags ---------------------------------------------------------
            // Every selected tag must be present, matching the website. Picking
            // two tags narrows the list rather than widening it.
            if (activeTags.Count > 0)
            {
                foreach (var wanted in activeTags)
                {
                    if (!hold.Tags.Contains(wanted, StringComparer.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            // --- language -----------------------------------------------------
            var language = SelectedFilter(LanguageFilterBox);
            if (language is not null
                && !string.Equals(hold.Language, language, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // --- framework ----------------------------------------------------
            var framework = SelectedFilter(FrameworkFilterBox);
            if (framework is not null
                && !string.Equals(hold.Framework, framework, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // --- population ---------------------------------------------------
            // Only applied to holds that are actually reporting. Without this
            // guard, "hide empty" wipes the entire list before any hold has
            // ever phoned home, which reads as a broken launcher.
            if (IsReporting(hold))
            {
                if (HideEmptyCheck?.IsChecked == true && hold.Players <= 0)
                {
                    return false;
                }

                if (HideFullCheck?.IsChecked == true
                    && hold.MaxPlayers > 0
                    && hold.Players >= hold.MaxPlayers)
                {
                    return false;
                }
            }

            return true;
        };

        PopulateFilterBoxes();
    }

    /// <summary>
    /// A hold is only reporting once it has told the master server something.
    /// Anything else is treated as unknown rather than empty.
    /// </summary>
    private static bool IsReporting(Hold hold)
    {
        return string.Equals(hold.Status, "online", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hold.Status, "offline", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns null when the box is on its "Any" row, which is index 0.
    /// </summary>
    private static string? SelectedFilter(ComboBox? box)
    {
        if (box is null || box.SelectedIndex <= 0)
        {
            return null;
        }

        return box.SelectedItem as string;
    }

    /// <summary>
    /// Tags the user has switched on. Kept here rather than read off the chips
    /// so the filter never has to walk the visual tree.
    /// </summary>
    private readonly List<string> activeTags = new List<string>();

    private static readonly string[] AllTags =
    {
        "Economy", "Casual", "Chill", "PvP", "No PvP", "Special Events",
        "RP", "Serious RP", "Semi-Serious RP", "Supernatural",
        "Modded / Wabbajack", "Survival / Hardcore", "Beginner-Friendly",
        "Heavy Magic", "Moderate Magic", "Heavy Mods",
    };

    /// <summary>
    /// The lists are fixed rather than read off the holds, so a filter never
    /// disappears just because no hold happens to use it right now. These
    /// match the lists the website offers when a hold is created.
    /// </summary>
    private void PopulateFilterBoxes()
    {
        try
        {
            if (TagFilterPanel is not null && TagFilterPanel.Children.Count == 0)
            {
                foreach (var tag in AllTags)
                {
                    TagFilterPanel.Children.Add(BuildTagChip(tag));
                }
            }
            if (LanguageFilterBox is not null && LanguageFilterBox.Items.Count == 0)
            {
                LanguageFilterBox.Items.Add("Any language");
                foreach (var language in new[]
                {
                    "English", "German", "French", "Spanish", "Portuguese",
                    "Russian", "Polish", "Arabic", "Other",
                })
                {
                    LanguageFilterBox.Items.Add(language);
                }

                LanguageFilterBox.SelectedIndex = 0;
            }

            if (FrameworkFilterBox is not null && FrameworkFilterBox.Items.Count == 0)
            {
                FrameworkFilterBox.Items.Add("Any framework");
                foreach (var framework in new[]
                {
                    "Skyrim Together Reborn", "SkyMP", "Other / Custom",
                })
                {
                    FrameworkFilterBox.Items.Add(framework);
                }

                FrameworkFilterBox.SelectedIndex = 0;
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// A tag chip is a ToggleButton wearing a border. Built in code because the
    /// tag list is data, not layout, and duplicating sixteen of these in XAML
    /// would mean editing two files every time a tag is added.
    /// </summary>
    private ToggleButton BuildTagChip(string tag)
    {
        var chip = new ToggleButton
        {
            Content = tag,
            Tag = tag,
            FontSize = 12,
            Margin = new Thickness(0, 0, 7, 7),
            Padding = new Thickness(11, 5, 11, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
        };

        // A plain template, otherwise the chip inherits the default WPF button
        // chrome and looks like a Windows 98 escapee next to everything else.
        var template = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(999));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        template.VisualTree = border;
        chip.Template = template;

        PaintChip(chip, false);
        chip.Checked += TagChip_Toggled;
        chip.Unchecked += TagChip_Toggled;

        return chip;
    }

    private static void PaintChip(ToggleButton chip, bool on)
    {
        chip.Background = new SolidColorBrush(on
            ? Color.FromRgb(0x3A, 0x2A, 0x18)
            : Color.FromRgb(0x25, 0x1C, 0x13));

        chip.BorderBrush = new SolidColorBrush(on
            ? Color.FromRgb(0xD9, 0x8A, 0x3D)
            : Color.FromRgb(0x4A, 0x3A, 0x26));

        chip.Foreground = new SolidColorBrush(on
            ? Color.FromRgb(0xE9, 0xA0, 0x57)
            : Color.FromRgb(0xB0, 0xA0, 0x8A));
    }

    private void TagChip_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not ToggleButton chip || chip.Tag is not string tag)
            {
                return;
            }

            var on = chip.IsChecked == true;
            PaintChip(chip, on);

            if (on)
            {
                if (!activeTags.Contains(tag))
                {
                    activeTags.Add(tag);
                }
            }
            else
            {
                activeTags.Remove(tag);
            }

            ApplyFilters();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Shows and hides the filter panel. The panel starts collapsed so anyone
    /// who never wants filters never sees them.
    /// </summary>
    private void FiltersButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FilterPanel is null)
            {
                return;
            }

            FilterPanel.Visibility = FilterPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch
        {
        }
    }

    /// <summary>
    /// One handler behind every dropdown and checkbox. They all do the same
    /// thing: re-run the filter.
    /// </summary>
    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        ApplyFilters();
    }

    private void FilterBoxChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            activeTags.Clear();

            if (TagFilterPanel is not null)
            {
                foreach (var child in TagFilterPanel.Children)
                {
                    if (child is ToggleButton chip)
                    {
                        // Unhook first, or sixteen chips each fire a refresh.
                        chip.Checked -= TagChip_Toggled;
                        chip.Unchecked -= TagChip_Toggled;
                        chip.IsChecked = false;
                        PaintChip(chip, false);
                        chip.Checked += TagChip_Toggled;
                        chip.Unchecked += TagChip_Toggled;
                    }
                }
            }

            if (LanguageFilterBox is not null)
            {
                LanguageFilterBox.SelectedIndex = 0;
            }

            if (FrameworkFilterBox is not null)
            {
                FrameworkFilterBox.SelectedIndex = 0;
            }

            if (HideEmptyCheck is not null)
            {
                HideEmptyCheck.IsChecked = false;
            }

            if (HideFullCheck is not null)
            {
                HideFullCheck.IsChecked = false;
            }

            ApplyFilters();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Re-runs the filter and updates the two bits of chrome that tell the user
    /// the list is being narrowed. Without those, a filtered list is
    /// indistinguishable from a broken one.
    /// </summary>
    private void ApplyFilters()
    {
        try
        {
            var view = CollectionViewSource.GetDefaultView(holds);
            view?.Refresh();

            var count = activeTags.Count;
            if (SelectedFilter(LanguageFilterBox) is not null) count++;
            if (SelectedFilter(FrameworkFilterBox) is not null) count++;
            if (HideEmptyCheck?.IsChecked == true) count++;
            if (HideFullCheck?.IsChecked == true) count++;

            if (FiltersButton is not null)
            {
                FiltersButton.Content = count > 0 ? "Filters (" + count + ")" : "Filters";
            }

            if (FilterResultText is not null)
            {
                var shown = 0;
                if (view is not null)
                {
                    foreach (var _ in view) shown++;
                }

                FilterResultText.Text = count > 0
                    ? shown + " of " + holds.Count + " holds match"
                    : holds.Count + " holds";
            }
        }
        catch
        {
        }
    }

    private static bool Contains(string? haystack, string needle)
    {
        return !string.IsNullOrEmpty(haystack)
            && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            // Fires once while the window is still being built, before the
            // holds collection has a view. Nothing to refresh yet.
            ApplyFilters();
        }
        catch
        {
        }
    }

    // -----------------------------------------------------------------------
    // PROMOTED HOLDS (the featured strip)
    // -----------------------------------------------------------------------
    //
    // Deliberately NOT wired into the main hold fetch. That query and the Hold
    // class both live in MainWindow.xaml.cs, which is the file that took a
    // night to get right and is now proven. This asks Supabase its own small
    // question instead, so the join path cannot be affected by anything here.
    //
    // REQUIRES a column that does not exist yet: holds.is_promoted (boolean).
    // Until the SQL is run, the request comes back an error, the catch below
    // swallows it, and the strip simply stays hidden. Nothing breaks; the
    // feature just waits.

    private sealed class Promo
    {
        public string Name = "";
        public string Tagline = "";
        public string Color = "";
    }

    private readonly List<Promo> _promos = new();
    private int _promoIndex;
    private DispatcherTimer? _promoTimer;

    // Six seconds. Long enough to read a hold name and tagline without
    // hurrying, short enough that a viewer sees several in one visit.
    private static readonly TimeSpan PromoDwell = TimeSpan.FromSeconds(6);

    private const string PromoQuery =
        "/rest/v1/holds?select=name,tagline,hold_color" +
        "&is_promoted=is.true&status=eq.online&order=name.asc";

    private async Task LoadPromosAsync()
    {
        // Reset first. Without this, a second call - which Refresh now makes -
        // leaves the previous slide on screen when there are no longer any
        // promoted holds, and starts a SECOND rotation timer alongside the
        // first, so the strip flips twice as fast and then four times as fast.
        HidePromoStrip();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, SupabaseUrl + PromoQuery);
            req.Headers.Add("apikey", SupabaseKey);
            req.Headers.Add("Authorization", "Bearer " + SupabaseKey);

            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                // Almost certainly the is_promoted column not existing yet.
                // Not an error worth showing anybody.
                return;
            }

            var body = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            _promos.Clear();

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var p = new Promo
                {
                    Name = Str(row, "name"),
                    Tagline = Str(row, "tagline"),
                    Color = Str(row, "hold_color"),
                };

                if (p.Name.Length > 0)
                {
                    _promos.Add(p);
                }
            }

            if (_promos.Count == 0)
            {
                return;
            }

            BuildPromoDots();
            _promoIndex = 0;
            ShowPromo(0, fade: false);

            PromoStrip.Visibility = Visibility.Visible;

            // Only rotate when there is more than one. A single promoted hold
            // should sit still rather than fade in and out of itself.
            if (_promos.Count > 1)
            {
                _promoTimer = new DispatcherTimer { Interval = PromoDwell };
                _promoTimer.Tick += (_, _) => ShowPromo(_promoIndex + 1, fade: true);
                _promoTimer.Start();
            }
        }
        catch
        {
            // Strip stays hidden. The launcher is unaffected.
        }
    }

    /// <summary>
    /// Puts the strip back to how it looks on a fresh launch: hidden, empty,
    /// and with no timer running. Safe to call when it was never shown.
    /// </summary>
    private void HidePromoStrip()
    {
        try
        {
            if (_promoTimer is not null)
            {
                _promoTimer.Stop();
                _promoTimer = null;
            }

            _promos.Clear();
            _promoIndex = 0;

            PromoDots.Children.Clear();
            PromoName.Text = "";
            PromoTagline.Text = "";
            PromoStrip.Visibility = Visibility.Collapsed;
        }
        catch
        {
        }
    }

    private static string Str(JsonElement row, string field)
    {
        try
        {
            if (row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString() ?? "";
            }
        }
        catch
        {
        }

        return "";
    }

    private void ShowPromo(int index, bool fade)
    {
        try
        {
            if (_promos.Count == 0)
            {
                return;
            }

            _promoIndex = ((index % _promos.Count) + _promos.Count) % _promos.Count;
            var p = _promos[_promoIndex];

            PromoName.Text = p.Name;
            PromoTagline.Text = p.Tagline;

            var accent = ParseBrush(p.Color);
            if (accent is not null)
            {
                PromoAccent.Background = accent;
            }

            MarkPromoDots();

            if (fade)
            {
                // A short fade rather than a hard cut, so the strip reads as
                // one thing changing instead of two things flickering.
                var anim = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(420)));
                PromoContent.BeginAnimation(OpacityProperty, anim);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Turns a stored colour like #7A9B52 into a brush. Returns null on
    /// anything unexpected so the caller keeps the default gold rather than
    /// throwing over a typo in one row of the database.
    /// </summary>
    private static SolidColorBrush? ParseBrush(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal))
            {
                text = "#" + text;
            }

            if (text.Length != 7 && text.Length != 9)
            {
                return null;
            }

            var converted = ColorConverter.ConvertFromString(text);
            if (converted is Color c)
            {
                return new SolidColorBrush(c);
            }
        }
        catch
        {
        }

        return null;
    }

    private void BuildPromoDots()
    {
        try
        {
            PromoDots.Children.Clear();

            for (var i = 0; i < _promos.Count; i++)
            {
                PromoDots.Children.Add(new Border
                {
                    Width = 16,
                    Height = 3,
                    Margin = new Thickness(0, 0, 4, 0),
                    CornerRadius = new CornerRadius(1),
                    Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x3A, 0x26)),
                });
            }
        }
        catch
        {
        }
    }

    private void MarkPromoDots()
    {
        try
        {
            for (var i = 0; i < PromoDots.Children.Count; i++)
            {
                if (PromoDots.Children[i] is Border b)
                {
                    b.Background = i == _promoIndex
                        ? new SolidColorBrush(Color.FromRgb(0xE9, 0xA0, 0x57))
                        : new SolidColorBrush(Color.FromRgb(0x4A, 0x3A, 0x26));
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Clicking the strip takes you to the browser with that hold selected, so
    /// the advertisement leads somewhere instead of just being looked at. If
    /// the hold is not in the list, it still opens the browser rather than
    /// doing nothing visible.
    /// </summary>
    private void PromoButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LandingView.Visibility = Visibility.Collapsed;
            BrowserView.Visibility = Visibility.Visible;

            if (_promos.Count == 0)
            {
                return;
            }

            var wanted = _promos[_promoIndex].Name;

            // Clear any search first, otherwise the target may be filtered out
            // and the selection would look like it failed.
            SearchBox.Text = "";

            foreach (var item in holds)
            {
                if (string.Equals(item.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    HoldList.SelectedItem = item;
                    HoldList.ScrollIntoView(item);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    // -----------------------------------------------------------------------
    // SCREENS
    // -----------------------------------------------------------------------

    /// <summary>
    /// Landing to browser. Focus lands in the search box, because someone who
    /// has just chosen to browse is very often about to type a name.
    /// </summary>
    private void RideInButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LandingView.Visibility = Visibility.Collapsed;
            BrowserView.Visibility = Visibility.Visible;
            SearchBox.Focus();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Browser back to landing. The search term is cleared on the way out, so
    /// coming back later never shows a mysteriously short list.
    /// </summary>
    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SearchBox.Text = "";
            BrowserView.Visibility = Visibility.Collapsed;
            LandingView.Visibility = Visibility.Visible;
        }
        catch
        {
        }
    }

    // -----------------------------------------------------------------------
    // WEBSITE
    // -----------------------------------------------------------------------

    private const string WebsiteUrl = "https://holdline-chi.vercel.app";

    private void WebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        OpenInBrowser(WebsiteUrl);
    }

    // -----------------------------------------------------------------------
    // PATREON
    // -----------------------------------------------------------------------

    // ==== CHANGE THIS ONE LINE IF A DIRECT PATREON PAGE IS WANTED ====
    //
    // Pointed at the Holdline site rather than at patreon.com on purpose.
    //
    // The membership check is not a link, it is an OAuth handshake: the site
    // sends the player to Patreon, Patreon sends back a code, the site swaps
    // it for the membership status and writes is_patron. Dropping someone on a
    // bare Patreon page skips all of that, so they could pay and still not be
    // able to found a hold, which is the worst outcome available.
    //
    // If a plain "here is where to give money" link is wanted later, put the
    // patreon.com address between the quotes below. Nothing else changes.
    private const string PatreonUrl = "https://www.patreon.com/cw/ggfgh/membership";

    private void PatreonButton_Click(object sender, RoutedEventArgs e)
    {
        OpenInBrowser(PatreonUrl);
    }

    /// <summary>
    /// One place for both buttons, so a fix to either fixes both.
    /// </summary>
    private static void OpenInBrowser(string url)
    {
        try
        {
            // UseShellExecute is what makes Windows hand the address to
            // whatever browser the person actually uses. Without it, .NET
            // tries to run the URL as if it were a program, and throws.
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // A machine with no browser association is rare but possible, and
            // an unexplained dead button is worse than a sentence of text.
            MessageBox.Show(
                "Couldn't open your web browser.\r\n\r\n" +
                "You can get there by typing this address in yourself:\r\n\r\n" +
                url,
                "Holdline",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    // -----------------------------------------------------------------------
    // REFRESH
    // -----------------------------------------------------------------------

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshButton.IsEnabled = false;
            await LoadHoldsAsync();

            // Also re-check promotions. Without this, switching is_promoted on
            // in Supabase does nothing until the launcher is closed and
            // reopened, which is a miserable way to test an advert.
            await LoadPromosAsync();

            // The filter still applies to the newly loaded rows, so a search
            // typed before refreshing is not silently forgotten.
            CollectionViewSource.GetDefaultView(holds)?.Refresh();
        }
        catch
        {
            // LoadHoldsAsync reports its own trouble through the status line.
        }
        finally
        {
            try
            {
                RefreshButton.IsEnabled = true;
            }
            catch
            {
            }
        }
    }
}
