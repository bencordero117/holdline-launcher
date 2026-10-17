// MumbleLink.cs
// ---------------------------------------------------------------------------
// PROXIMITY VOICE - the bridge half that runs in the launcher.
//
// WHAT THIS DOES:
//   Mumble does not read the game. It reads a 5460-byte block of shared memory
//   called "MumbleLink" that Mumble itself creates. Anything that writes the
//   local player's position into that block gets positional audio for free:
//   Mumble ships the position alongside the voice packet, the server forwards
//   it, and every listener's own Mumble does the distance falloff and stereo
//   panning locally. No audio ever touches the game server.
//
// WHY IT LIVES IN THE LAUNCHER:
//   The launcher is already running while the player plays, it is C#, and C#
//   can map shared memory directly. The alternative - reading Skyrim's memory
//   from outside - is what the Among Us bridge does, and it trips antivirus
//   and anti-cheat. We never touch the game's memory. The SkyMP client plugin
//   publishes the position it ALREADY knows, and this reads that.
//
// THE CONTRACT WITH THE GAME SIDE:
//   The client plugin writes one small JSON file, overwritten a few times a
//   second:
//       %AppData%\Holdline\voice-position.json
//       { "x":.., "y":.., "z":.., "yaw":.., "world":"..", "hold":"..", "name":".." }
//   x/y/z are raw Skyrim units. yaw is the player's Z rotation in degrees.
//   "world" is the worldspace or cell, "hold" is the hold's master key, "name"
//   is the callsign.
//
//   A file rather than a socket on purpose: it is the one form of IPC that
//   works no matter what the plugin sandbox allows, it survives either side
//   restarting, and a half-written file just fails one tick and is retried.
//
// WHAT IS CONFIRMED AND WHAT IS NOT:
//   CONFIRMED - the struct layout, the 5460-byte size and the "MumbleLink"
//   map name, from Mumble's own link-plugin documentation and three separate
//   working implementations.
//   NOT YET CONFIRMED - that the SkyMP client plugin can write that file.
//   The server plainly knows position (pos / locationalData / worldOrCellDesc),
//   so the data exists; how it reaches disk is the open question. If the
//   plugin cannot write files, the same JSON can arrive over a local socket
//   and only ReadFeed below changes.
// ---------------------------------------------------------------------------

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HoldlineLauncher
{
    /// <summary>
    /// One position sample, as published by the game side.
    /// </summary>
    public sealed class VoicePosition
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        /// <summary>Player facing, degrees, Skyrim's Z rotation.</summary>
        public float Yaw { get; set; }

        public string World { get; set; } = "";
        public string Hold { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// Writes the local player's position into Mumble's shared memory block.
    /// </summary>
    public sealed class MumbleLink : IDisposable
    {
        // Mumble creates this mapping. We only ever open it, never create it,
        // so if Mumble is not running Connect simply fails and we retry later.
        private const string MapName = "MumbleLink";

        // sizeof(LinkedMem). Fixed by the spec; do not compute it.
        private const int StructSize = 5460;

        // Byte offsets into LinkedMem. Taken from the published struct:
        //   uint32 uiVersion; uint32 uiTick;
        //   float  fAvatarPosition[3], fAvatarFront[3], fAvatarTop[3];
        //   wchar  name[256];
        //   float  fCameraPosition[3], fCameraFront[3], fCameraTop[3];
        //   wchar  identity[256];
        //   uint32 context_len; byte context[256]; wchar description[2048];
        private const int OffVersion = 0;
        private const int OffTick = 4;
        private const int OffAvatarPos = 8;
        private const int OffAvatarFront = 20;
        private const int OffAvatarTop = 32;
        private const int OffName = 44;
        private const int OffCameraPos = 556;
        private const int OffCameraFront = 568;
        private const int OffCameraTop = 580;
        private const int OffIdentity = 592;
        private const int OffContextLen = 1104;
        private const int OffContext = 1108;
        private const int OffDescription = 1364;

        private const int MaxWideChars = 256;
        private const int MaxContextBytes = 256;

        private MemoryMappedFile? map;
        private MemoryMappedViewAccessor? view;
        private uint tick;

        public bool IsConnected => view is not null;

        /// <summary>
        /// Opens Mumble's shared memory. Returns false when Mumble is not
        /// running, which is a normal state, not an error - the caller just
        /// tries again on the next tick.
        /// </summary>
        public bool Connect()
        {
            if (view is not null)
            {
                return true;
            }

            try
            {
                map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
                view = map.CreateViewAccessor(0, StructSize, MemoryMappedFileAccess.ReadWrite);
                return true;
            }
            catch
            {
                Disconnect();
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                view?.Dispose();
                map?.Dispose();
            }
            catch
            {
            }

            view = null;
            map = null;
        }

        /// <summary>
        /// Publishes one frame. Mumble treats the link as dead if uiTick stops
        /// changing for five seconds, so this must be called steadily - a few
        /// times a second is plenty.
        /// </summary>
        /// <param name="context">
        /// Two players only hear each other positionally when their context
        /// strings match exactly. Anything else is delivered flat. So this must
        /// encode game + hold + worldspace: same hold but different worlds
        /// means they are nowhere near each other and must not be positional.
        /// </param>
        public bool Write(
            float[] avatarPos,
            float[] avatarFront,
            float[] avatarTop,
            string playerName,
            string identity,
            string context)
        {
            if (view is null && !Connect())
            {
                return false;
            }

            try
            {
                unchecked
                {
                    tick++;
                }

                view!.Write(OffVersion, (uint)2);
                view.Write(OffTick, tick);

                WriteVec(OffAvatarPos, avatarPos);
                WriteVec(OffAvatarFront, avatarFront);
                WriteVec(OffAvatarTop, avatarTop);

                // The camera is reported at the avatar. Skyrim's third-person
                // camera orbits the player, so the difference is under a metre
                // and well inside any sane falloff curve. Reporting them
                // separately would need real camera data we do not have.
                WriteVec(OffCameraPos, avatarPos);
                WriteVec(OffCameraFront, avatarFront);
                WriteVec(OffCameraTop, avatarTop);

                WriteWide(OffName, playerName);
                WriteWide(OffIdentity, identity);
                WriteContext(context);

                return true;
            }
            catch
            {
                // Mumble closed underneath us. Drop the handles so the next
                // tick reconnects cleanly instead of throwing forever.
                Disconnect();
                return false;
            }
        }

        private void WriteVec(int offset, float[] v)
        {
            view!.Write(offset, v.Length > 0 ? v[0] : 0f);
            view.Write(offset + 4, v.Length > 1 ? v[1] : 0f);
            view.Write(offset + 8, v.Length > 2 ? v[2] : 0f);
        }

        /// <summary>
        /// Writes a null-terminated UTF-16 string, truncated to fit. wchar_t on
        /// Windows is two bytes, which is what C# char already is.
        /// </summary>
        private void WriteWide(int offset, string? text)
        {
            text ??= "";

            if (text.Length > MaxWideChars - 1)
            {
                text = text.Substring(0, MaxWideChars - 1);
            }

            var i = 0;
            for (; i < text.Length; i++)
            {
                view!.Write(offset + (i * 2), (ushort)text[i]);
            }

            view!.Write(offset + (i * 2), (ushort)0);
        }

        /// <summary>
        /// Context is raw bytes with an explicit length, not a string, so it is
        /// written as UTF-8 with the length alongside it.
        /// </summary>
        private void WriteContext(string? context)
        {
            var bytes = Encoding.UTF8.GetBytes(context ?? "");
            var length = Math.Min(bytes.Length, MaxContextBytes);

            for (var i = 0; i < length; i++)
            {
                view!.Write(OffContext + i, bytes[i]);
            }

            view!.Write(OffContextLen, (uint)length);
        }

        public void Dispose()
        {
            Disconnect();
        }
    }

    /// <summary>
    /// Reads the game side's position feed, converts it into Mumble's world,
    /// and pumps it into the link. Start it when the game launches, stop it
    /// when the launcher closes. Nothing here is load-bearing: if voice cannot
    /// start, the player still plays, they just do not hear anyone.
    /// </summary>
    public sealed class VoiceBridge : IDisposable
    {
        /// <summary>
        /// Skyrim measures in units, Mumble in metres. The usual figure is
        /// about seventy units to the metre. This is the single knob that
        /// decides how far a voice carries, so it is deliberately a constant
        /// with a name rather than a number buried in the maths - expect to
        /// tune it by ear once two people are stood in a field.
        /// </summary>
        private const float UnitsPerMetre = 70f;

        private static readonly string FeedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Holdline",
            "voice-position.json");

        private readonly MumbleLink link = new MumbleLink();
        private CancellationTokenSource? cancel;

        /// <summary>Last error, for the launcher to show if it wants to.</summary>
        public string Status { get; private set; } = "Not started";

        public void Start()
        {
            Stop();

            cancel = new CancellationTokenSource();
            var token = cancel.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        Pump();
                    }
                    catch
                    {
                        // Never allowed to take the launcher down.
                    }

                    try
                    {
                        // 20 a second. Mumble needs well under five seconds
                        // between ticks; this is smooth without being greedy.
                        await Task.Delay(50, token);
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                }
            }, token);
        }

        private void Pump()
        {
            var sample = ReadFeed();
            if (sample is null)
            {
                Status = "Waiting for the game";
                return;
            }

            if (!link.Connect())
            {
                Status = "Waiting for Mumble";
                return;
            }

            // --- coordinates -------------------------------------------------
            // Skyrim is Z-up: X east, Y north, Z up.
            // Mumble is Y-up:  X right, Y up, Z front.
            // So Skyrim Z becomes Mumble Y, and Skyrim Y becomes Mumble Z.
            var pos = new[]
            {
                sample.X / UnitsPerMetre,
                sample.Z / UnitsPerMetre,
                sample.Y / UnitsPerMetre,
            };

            // Facing, flattened to the horizontal plane. Pitch is ignored on
            // purpose: looking at your boots should not move your voice.
            var radians = sample.Yaw * (float)(Math.PI / 180.0);
            var front = new[]
            {
                (float)Math.Sin(radians),
                0f,
                (float)Math.Cos(radians),
            };

            var top = new[] { 0f, 1f, 0f };

            // Context decides who can hear whom positionally. Hold AND world
            // both matter - two players in the same hold but different
            // worldspaces are not near each other in any meaningful sense.
            var context = "holdline|" + sample.Hold + "|" + sample.World;

            var identity = sample.Name;

            if (link.Write(pos, front, top, sample.Name, identity, context))
            {
                Status = "Linked";
            }
            else
            {
                Status = "Link lost";
            }
        }

        /// <summary>
        /// Reads the feed file. A torn read - the game writing while we read -
        /// just fails to parse and is retried 50ms later, so it is swallowed
        /// rather than reported.
        /// </summary>
        private static VoicePosition? ReadFeed()
        {
            try
            {
                if (!File.Exists(FeedPath))
                {
                    return null;
                }

                // Stale feed means the game has stopped publishing. Better to
                // go silent than to pin everyone at the last known spot.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(FeedPath) > TimeSpan.FromSeconds(3))
                {
                    return null;
                }

                using var stream = new FileStream(
                    FeedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                var json = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                return new VoicePosition
                {
                    X = Num(root, "x"),
                    Y = Num(root, "y"),
                    Z = Num(root, "z"),
                    Yaw = Num(root, "yaw"),
                    World = Str(root, "world"),
                    Hold = Str(root, "hold"),
                    Name = Str(root, "name"),
                };
            }
            catch
            {
                return null;
            }
        }

        private static float Num(JsonElement root, string field)
        {
            if (root.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var number))
            {
                return (float)number;
            }

            return 0f;
        }

        private static string Str(JsonElement root, string field)
        {
            if (root.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }

            return "";
        }

        public void Stop()
        {
            try
            {
                cancel?.Cancel();
                cancel?.Dispose();
            }
            catch
            {
            }

            cancel = null;
            link.Disconnect();
            Status = "Stopped";
        }

        public void Dispose()
        {
            Stop();
            link.Dispose();
        }
    }
}
