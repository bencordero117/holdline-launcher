// Cosmetics.cs
// ---------------------------------------------------------------------------
// The "Appearance mods (optional)" button that sits under Join.
//
// WHY THIS IS A SEPARATE FILE:
//   Same reason as Shell.cs. MainWindow.xaml.cs holds the join logic that was
//   debugged the hard way and now works end to end. Cosmetics are decoration.
//   Decoration does not get to live in the file that can break joining.
//
// WHY WE LINK INSTEAD OF BUNDLING:
//   Every werewolf retexture on Nexus carries the same clause: assets may not
//   be used in files that are being sold for money. Holdline takes money for
//   hold creation, so bundling any of them would need the author's written
//   permission first.
//
//   Linking needs no permission from anybody. The player clicks, Nexus serves
//   the file under Nexus's own terms, and Holdline never hosts, copies or
//   redistributes a single byte of someone else's work. That is the whole
//   design of this feature and the reason it opens pages rather than
//   downloading anything itself.
//
//   IF A MOD IS EVER BUNDLED INTO Data.zip INSTEAD, THAT PROTECTION IS GONE.
//   Get the author's yes in writing first.
//
// WHY IT OPENS THE MOD PAGE RATHER THAN FIRING nxm:// DIRECTLY:
//   An nxm:// link is not a static address. Nexus generates it per download
//   with a key and an expiry baked in, which is what the green "Mod Manager
//   Download" button on the page produces. A hand-built nxm:// link has no
//   valid key and simply fails. So the honest one-click is: open the right
//   page, let the player press the button Nexus put there.
//
// WHAT MAY GO IN THE LIST:
//   Pure texture/mesh replacers with NO .esp/.esl/.esm plugin. A plugin
//   changes form records that the server and every other player reference,
//   which desyncs or crashes. Textures are invisible to the server: each
//   player just sees their own paint on the same shared werewolf, so mixed
//   installs across a server are completely safe.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HoldlineLauncher;

public partial class MainWindow
{
    /// <summary>
    /// One optional appearance mod. Vetted by hand; see the header for the
    /// two rules an entry has to satisfy before it can be added.
    /// </summary>
    private sealed class Cosmetic
    {
        public string Name = "";
        public string Blurb = "";
        public string Url = "";
        public bool Recommended;
    }

    // Verified plugin-free. Diverse Werewolves Collection is deliberately
    // ABSENT: it ships a plugin and edits NPC records, which is exactly the
    // desync risk this list exists to avoid.
    private static readonly List<Cosmetic> AppearanceMods = new()
    {
        new Cosmetic
        {
            Name = "Warwolf",
            Blurb = "Battle-scarred werewolf. Keeps the vanilla shape, adds scars and grime.",
            Url = "https://www.nexusmods.com/skyrimspecialedition/mods/70809",
            Recommended = true,
        },
        new Cosmetic
        {
            Name = "Iconic's Werewolf and Werebear Retexture",
            Blurb = "Highest quality of the set. 4K and 2K, several eye colours.",
            Url = "https://www.nexusmods.com/skyrimspecialedition/mods/90675",
            Recommended = true,
        },
        new Cosmetic
        {
            Name = "HD Werewolf Retexture",
            Blurb = "Straight sharpness upgrade. Same werewolf, higher resolution fur.",
            Url = "https://www.nexusmods.com/skyrimspecialedition/mods/277",
            Recommended = false,
        },
    };

    /// <summary>
    /// Called once at startup. If there is no Vortex on this machine the
    /// button is removed rather than disabled: a greyed-out control invites
    /// people to wonder what they are missing, and this is optional paint on
    /// an optional feature. No Vortex, no button, no question raised.
    /// </summary>
    private void InitAppearanceButton()
    {
        try
        {
            if (AppearanceButton is null)
            {
                return;
            }

            AppearanceButton.Visibility = IsVortexInstalled()
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch
        {
            // Leave it showing. A button that opens a web page is harmless
            // even on a machine that turns out not to have Vortex.
        }
    }

    /// <summary>
    /// True when something on this machine has claimed the nxm: protocol,
    /// which in practice means Vortex (or another mod manager) is installed
    /// and its "Mod Manager Download" handoff will work.
    ///
    /// Checked so the panel can tell the player the truth up front instead of
    /// sending them to a green button that will do nothing.
    /// </summary>
    private static bool IsVortexInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("nxm");
            return key is not null;
        }
        catch
        {
            // If the registry cannot be read, assume it IS installed. A false
            // "you need Vortex" nag on a machine that already has it is more
            // annoying than the reverse.
            return true;
        }
    }

    private void AppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowAppearanceWindow();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Couldn't open the appearance list.\r\n\r\n" + ex.Message,
                "Holdline",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// Built in code rather than XAML because it is one small panel over a
    /// fixed list. A second .xaml file would mean editing two places every
    /// time a mod is added or dropped.
    /// </summary>
    private void ShowAppearanceWindow()
    {
        var stack = new StackPanel { Margin = new Thickness(22, 20, 22, 22) };

        stack.Children.Add(new TextBlock
        {
            Text = "Werewolf appearance",
            FontSize = 19,
            FontFamily = new FontFamily("Palatino Linotype, Book Antiqua, Georgia, serif"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xE2, 0xC9)),
            Margin = new Thickness(0, 0, 0, 6),
        });

        // Said plainly and first. Nobody should ever come away thinking they
        // need any of this in order to play.
        stack.Children.Add(new TextBlock
        {
            Text = "None of this is required. Your werewolf already works without it "
                 + "and everyone sees the same fight either way. These only change how "
                 + "it looks on your own screen.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xA0, 0x8A)),
            Margin = new Thickness(0, 0, 0, 14),
        });

        foreach (var mod in AppearanceMods)
        {
            stack.Children.Add(BuildModRow(mod));
        }

        stack.Children.Add(new TextBlock
        {
            Text = "Each link opens the mod's page on Nexus. Press the green "
                 + "\"Mod Manager Download\" there and Vortex takes it from there. "
                 + "These are other people's work, hosted by Nexus, under their own terms.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x7B, 0x63)),
            Margin = new Thickness(0, 8, 0, 0),
        });

        var window = new Window
        {
            Title = "Holdline - Appearance",
            Width = 470,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x13, 0x09)),
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 560,
                Content = stack,
            },
        };

        window.ShowDialog();
    }

    private Border BuildModRow(Cosmetic mod)
    {
        var inner = new StackPanel();

        var heading = new TextBlock
        {
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xE2, 0xC9)),
            Margin = new Thickness(0, 0, 0, 3),
            Text = mod.Recommended ? mod.Name + "  (recommended)" : mod.Name,
        };
        inner.Children.Add(heading);

        inner.Children.Add(new TextBlock
        {
            Text = mod.Blurb,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xA0, 0x8A)),
            Margin = new Thickness(0, 0, 0, 9),
        });

        var button = new Button
        {
            Content = "Open on Nexus",
            FontSize = 12,
            Padding = new Thickness(14, 6, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x20, 0x15)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0xA0, 0x57)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x56, 0x37)),
            BorderThickness = new Thickness(1),
        };

        var url = mod.Url;
        button.Click += (_, _) => OpenInBrowser(url);
        inner.Children.Add(button);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x1C, 0x13)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x3A, 0x26)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(15, 13, 15, 14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = inner,
        };
    }

}
