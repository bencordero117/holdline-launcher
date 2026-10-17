// PlayerIdentity.cs
//
// Gives every Holdline install its own player id, automatically, once.
//
// WHY THIS EXISTS
// ---------------
// The server runs in offline mode (server-settings.json: "offlineMode": true).
// In that mode there is no account system at all — the server identifies a
// player purely by the "profileId" number sitting in their local
// skymp5-client-settings.txt. Confirmed in the engine source:
//
//   skymp5-server/ts/systems/login.ts:233
//     offlineMode === true && typeof gameData.profileId === "number"
//       -> emit("spawnAllowed", userId, profileId, ...)
//
//   skymp5-server/ts/systems/spawn.ts:20
//     let actorId = ctx.svr.getActorsByProfileId(userProfileId)[0];
//     if (actorId) { ...load that existing character... }
//     else         { ...create a new one, then setRaceMenuOpen(actorId, true) }
//
// So profileId IS the character. Two installs carrying the same number are
// the same person as far as the server is concerned: the second player to
// join loads the first player's character. That is exactly what happened
// when Besnik joined — every copy of the client ships with profileId 1, so
// he spawned as Ben's character. Hand-editing a text file per player does
// not scale past two people, which is why this moved into the launcher.
//
// WHERE THE ID LIVES
// ------------------
// %AppData%\Holdline\player-id.txt — a dedicated one-line file, on purpose.
// It is NOT stored in launcher-settings.json, because SaveSkyrimFolder()
// rewrites that file wholesale and would wipe the id. And it is NOT trusted
// to live only in skymp5-client-settings.txt, because ClientFiles copies the
// whole client Data folder over the top on every version bump, restoring the
// shipped profileId 1. This file is the source of truth; the launcher
// re-stamps it into the client settings on every single join.
//
// VALID RANGE
// -----------
// profileId is int32_t and must be >= 0 (MpObjectReference.cpp:917 throws on
// negative). It must also be strictly > 0: RegisterProfileId only inserts
// into the actorIdByProfileId index when profileId > 0
// (MpObjectReference.cpp:932), so a player on id 0 is never found again and
// gets a brand-new character on every join. Generated ids therefore start
// well above zero, and also well above the low numbers already handed out by
// hand, so a new player can never land on an existing character.

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace HoldlineLauncher
{
    public static class PlayerIdentity
    {
        /// <summary>
        /// The profileId every copy of the client ships with. Treated as
        /// "nobody has claimed this install yet" rather than as a real id,
        /// because it is the shared default that caused the collision.
        /// </summary>
        private const int ShippedDefaultProfileId = 1;

        // Generated ids sit far above any hand-assigned number. int32 max is
        // 2,147,483,647; staying one below it avoids any off-by-one worry at
        // the top of the range.
        private const int GeneratedMin = 1_000_000;
        private const int GeneratedMaxExclusive = 2_147_483_646;

        private static readonly object Gate = new object();

        // 0 means "not resolved yet this session".
        private static int _cached;

        /// <summary>
        /// Same folder the launcher already uses for its settings, so an
        /// uninstall that clears %AppData%\Holdline clears both together.
        /// </summary>
        public static string IdFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Holdline",
            "player-id.txt");

        /// <summary>
        /// Returns this install's permanent player id, creating it on first
        /// run. Always returns a usable id (>= 1) even if the disk is not
        /// writable — a player with an unwritable AppData still gets to play,
        /// they just get a fresh character each launch, which is far better
        /// than being dropped into somebody else's.
        /// </summary>
        /// <param name="existingSettingsFilePath">
        /// Optional path to the player's skymp5-client-settings.txt. Used
        /// once, on first run, to adopt an id that was already set by hand so
        /// an existing character is not orphaned. Pass it BEFORE anything can
        /// overwrite that file.
        /// </param>
        public static int GetOrCreateProfileId(string? existingSettingsFilePath = null)
        {
            lock (Gate)
            {
                if (_cached >= 1)
                {
                    return _cached;
                }

                // 1. Already claimed on a previous run. This is the normal path.
                var stored = ReadStoredId();
                if (stored >= 1)
                {
                    _cached = stored;
                    return _cached;
                }

                // 2. First run, but this install was hand-fixed to a real id
                //    before the launcher started managing them (Besnik's 2).
                //    Adopt it so the character they already built survives.
                //    The shipped default of 1 is deliberately NOT adopted —
                //    that is the collision, not an identity.
                var adopted = ReadProfileIdFromSettings(existingSettingsFilePath);
                if (adopted > ShippedDefaultProfileId)
                {
                    _cached = adopted;
                    WriteStoredId(_cached);
                    return _cached;
                }

                // 3. Genuinely new install. Claim a number nobody else has.
                _cached = RandomNumberGenerator.GetInt32(GeneratedMin, GeneratedMaxExclusive);
                WriteStoredId(_cached);
                return _cached;
            }
        }

        /// <summary>
        /// Reads the saved id. Returns 0 for missing, unreadable, or junk
        /// contents, which sends the caller down the create path.
        /// </summary>
        private static int ReadStoredId()
        {
            try
            {
                var path = IdFilePath;

                if (!File.Exists(path))
                {
                    return 0;
                }

                var text = File.ReadAllText(path);

                // A file saved by Notepad can carry a byte-order mark, and a
                // hand-edited one usually carries a trailing newline. Neither
                // should cost the player their character.
                text = text.Trim().TrimStart('\uFEFF').Trim();

                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    && value >= 1)
                {
                    return value;
                }
            }
            catch
            {
                // Fall through to create a new id.
            }

            return 0;
        }

        private static void WriteStoredId(int profileId)
        {
            try
            {
                var path = IdFilePath;
                var dir = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, profileId.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                // Not fatal. The id still holds for this session; the player
                // gets a new one next launch rather than a crash now.
            }
        }

        /// <summary>
        /// Pulls gameData.profileId out of an existing client settings file.
        /// Returns 0 if the file is absent, unparseable, or has no id.
        /// </summary>
        private static int ReadProfileIdFromSettings(string? settingsFilePath)
        {
            if (string.IsNullOrEmpty(settingsFilePath))
            {
                return 0;
            }

            try
            {
                if (!File.Exists(settingsFilePath))
                {
                    return 0;
                }

                var root = JsonNode.Parse(File.ReadAllText(settingsFilePath)) as JsonObject;

                if (root?["gameData"] is JsonObject gameData
                    && gameData["profileId"] is JsonValue idValue
                    && idValue.TryGetValue<int>(out var parsed)
                    && parsed >= 1)
                {
                    return parsed;
                }
            }
            catch
            {
                // A corrupt settings file just means we generate a fresh id.
            }

            return 0;
        }
    }
}
