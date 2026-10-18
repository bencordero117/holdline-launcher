// PluginListGuard.cs
// Added 2026-08-13.
//
// WHY THIS FILE EXISTS
// --------------------
// Tester Gio's game closed itself at the main menu every single time, with
// no crash report, no popup, and a skyrim-platform.log that stopped at the
// exact same line. Diffing his log against a working machine's proved the
// death happens BEFORE Holdline's code ever runs: after the Papyrus binds,
// before SKSE's data-loaded message boots the JS engine.
//
// The thing the game does in that window is load its plugin list. An
// enabled entry in plugins.txt whose .esp/.esm/.esl file is not actually in
// Data kills the game exactly there: a clean self-close at the menu, no
// fault event, nothing in any log we ship. It is invisible unless someone
// opens plugins.txt by hand - which is why it survived an entire night of
// deleting files inside the game folder. The game folder was never the
// problem.
//
// The launcher has never looked at this file. Players come to Holdline with
// years of leftover mod lists, and any entry pointing at a file they since
// moved or deleted is a landmine we inherit. FiveM-style "press Join and it
// works" means the launcher owns this check.
//
// WHAT IT DOES, AND DELIBERATELY NOTHING MORE
// -------------------------------------------
// For every ENABLED entry (a line starting with '*') it checks the file
// exists in Data. If it does not, the '*' is removed, so the game skips it
// exactly as if the player had unticked it in the vanilla menu. The line
// itself stays, and a one-time backup of the original file is written
// beside it, so nothing is ever lost.
//
// It does NOT install anything, does NOT reorder anything, does NOT touch
// entries whose files exist, and does NOT touch disabled lines. A player's
// working mod setup passes through untouched. Only entries that would kill
// the game are defused.
//
// Never throws and never blocks a join, same contract as
// ClientPermissions: worst case the player is exactly where they were
// before this existed.

using System;
using System.Collections.Generic;
using System.IO;

namespace HoldlineLauncher
{
    public static class PluginListGuard
    {
        /// <summary>
        /// Disables load-order entries whose plugin files are missing from
        /// Data. Call on the Join path, before the game starts, right next
        /// to ClientPermissions.EnsureBrowserPermissions.
        /// </summary>
        public static void EnsurePluginListSane(string skyrimDataDir,
                                                IProgress<string> report)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(skyrimDataDir) ||
                    !Directory.Exists(skyrimDataDir))
                {
                    return;
                }

                var pluginsTxt = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "Skyrim Special Edition",
                    "plugins.txt");

                if (!File.Exists(pluginsTxt))
                {
                    // Vanilla install that has never run a mod manager.
                    // Nothing to check - the game builds its own list.
                    return;
                }

                var lines = File.ReadAllLines(pluginsTxt);

                var fixedLines = new List<string>(lines.Length);
                var disabled = new List<string>();

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    // Only enabled entries can kill the load. Comments,
                    // blanks and already-disabled lines pass through as-is.
                    if (!trimmed.StartsWith("*", StringComparison.Ordinal))
                    {
                        fixedLines.Add(line);
                        continue;
                    }

                    var name = trimmed.Substring(1).Trim();

                    if (name.Length == 0 ||
                        File.Exists(Path.Combine(skyrimDataDir, name)))
                    {
                        fixedLines.Add(line);
                        continue;
                    }

                    // Enabled, and the file is not there. This is the
                    // landmine. Untick it; keep the line.
                    fixedLines.Add(name);
                    disabled.Add(name);
                }

                if (disabled.Count == 0)
                {
                    return;
                }

                // One-time backup of the untouched original. Never
                // overwritten on later runs, so the pre-Holdline state is
                // always recoverable.
                var backup = pluginsTxt + ".holdline-bak";

                if (!File.Exists(backup))
                {
                    File.Copy(pluginsTxt, backup);
                }

                File.WriteAllLines(pluginsTxt, fixedLines);

                report.Report(
                    "Switched off " + disabled.Count +
                    " mod entr" + (disabled.Count == 1 ? "y" : "ies") +
                    " pointing at files that aren't installed (" +
                    string.Join(", ", disabled) +
                    "). Files like that stop Skyrim at the main menu. " +
                    "Nothing was deleted - your original list is saved as " +
                    "plugins.txt.holdline-bak.");
            }
            catch
            {
                // A load-order check must never be the reason a player
                // cannot join.
            }
        }

        private const string AlienEspName = "AvPAlien.esp";

        private static string PluginsTxtPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Skyrim Special Edition", "plugins.txt");

        /// <summary>
        /// Keeps the Alien mod ON: ensures AvPAlien.esp is enabled (*) in
        /// plugins.txt so it loads in multiplayer and single player alike. Only
        /// enables it when the file is actually in Data (an enabled entry whose
        /// file is missing kills the game at the menu). Ticks the existing line
        /// IN PLACE so its load-order position - and therefore the Alien's form
        /// IDs - stay put. Returns true if it changed something. Never throws.
        /// Shared by the Single player switch, Join, and Fix my game.
        /// </summary>
        public static bool EnableAlienEsp(string skyrimDataDir)
        {
            try
            {
                if (string.IsNullOrEmpty(skyrimDataDir)) return false;
                if (!File.Exists(Path.Combine(skyrimDataDir, AlienEspName))) return false;

                var txt = PluginsTxtPath();
                var lines = File.Exists(txt)
                    ? new List<string>(File.ReadAllLines(txt))
                    : new List<string>();

                for (var i = 0; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    if (string.Equals(t, "*" + AlienEspName, StringComparison.OrdinalIgnoreCase))
                        return false; // already on
                    if (string.Equals(t, AlienEspName, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = "*" + AlienEspName; // tick in place - keeps its slot/form IDs
                        WriteLines(txt, lines);
                        return true;
                    }
                }
                lines.Add("*" + AlienEspName); // not listed yet - add it
                WriteLines(txt, lines);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteLines(string path, List<string> lines)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(path, lines);
        }
    }
}
