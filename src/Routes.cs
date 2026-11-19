using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SailorLocust
{
    /// <summary>The sea routes a player may see, as sent to that player's game.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class RoutesPacket
    {
        /// <summary>"x1,y1,z1>x2,y2,z2": the blocks of the marker a stretch leaves from and of the one it goes to.</summary>
        public List<string> Keys = new List<string>();
        /// <summary>How many numbers of Coords each stretch has.</summary>
        public List<int> Counts = new List<int>();
        /// <summary>The ways, one after the other, flat: x0, z0, x1, z1, ... in blocks.</summary>
        public List<double> Coords = new List<double>();
    }

    /// <summary>
    /// The sea routes of the boats under orders, for maps to draw (Remember It reads them; nothing here depends on it).
    /// Each stretch between two stops that follow each other in a locust's orders is one route, the way the long route
    /// finder gives it. A player is sent only the stretches whose two ends they have been near (48 blocks), so nobody's
    /// game holds the place of a port they have not found.
    ///
    /// In the player's game the routes are put in the client's object cache, made of the base library's types only:
    /// "sailorlocust:routes" is a Dictionary&lt;string, double[]&gt; (replaced whole at each change, never changed in
    /// place) and "sailorlocust:routesVersion" an int that goes up by one at each change.
    /// </summary>
    public class RouteShare
    {
        public const string Channel = "sailorlocust";
        public const string CacheRoutes = "sailorlocust:routes", CacheVersion = "sailorlocust:routesVersion";
        const string KnownKey = "sailorlocust-known", LinesKey = "sailorlocust-lines";
        public const double KnowRadius = 48;
        const int NewRoutesPerTick = 4;

        readonly SailorLocustSystem system;
        readonly ICoreServerAPI sapi;
        IServerNetworkChannel channel;

        /// <summary>The orders of every ship, with where it was last seen: also of ships whose chunk is not loaded.</summary>
        readonly Dictionary<string, Line> lines = new Dictionary<string, Line>();
        readonly Dictionary<string, HashSet<long>> known = new Dictionary<string, HashSet<long>>();
        readonly Dictionary<string, string> sent = new Dictionary<string, string>();
        readonly Dictionary<string, double[]> ways = new Dictionary<string, double[]>();
        bool linesDirty;

        public class Line
        {
            public string Orders = "";
            public double X, Z;
        }

        public class Segment
        {
            public string Key;
            public Marker From, To;
            public double[] Way;
        }

        public RouteShare(SailorLocustSystem system)
        {
            this.system = system;
            sapi = system.sapi;
        }

        public void Start()
        {
            channel = sapi.Network.RegisterChannel(Channel).RegisterMessageType<RoutesPacket>().RegisterMessageType<FleetPacket>();
            sapi.Event.PlayerNowPlaying += player => { LoadKnown(player); sent.Remove(player.PlayerUID); };
            sapi.Event.PlayerDisconnect += player => { SaveKnown(player); known.Remove(player.PlayerUID); sent.Remove(player.PlayerUID); };
            sapi.Event.RegisterGameTickListener(Tick, 3000, 1500);
        }

        public IServerNetworkChannel Net { get { return channel; } }

        public static void StartClient(ICoreClientAPI capi)
        {
            capi.Network.RegisterChannel(Channel).RegisterMessageType<RoutesPacket>().RegisterMessageType<FleetPacket>()
                .SetMessageHandler<FleetPacket>(packet => FleetMapLayer.Latest = packet)
                .SetMessageHandler<RoutesPacket>(packet =>
            {
                var routes = new Dictionary<string, double[]>();
                List<string> keys = packet.Keys ?? new List<string>();
                List<int> counts = packet.Counts ?? new List<int>();
                List<double> coords = packet.Coords ?? new List<double>();
                int at = 0;
                for (int i = 0; i < keys.Count && i < counts.Count; i++)
                {
                    int n = counts[i];
                    if (n < 4 || at + n > coords.Count) break;
                    var way = new double[n];
                    coords.CopyTo(at, way, 0, n);
                    routes[keys[i]] = way;
                    at += n;
                }
                capi.ObjectCache[CacheRoutes] = routes;
                int version = capi.ObjectCache.TryGetValue(CacheVersion, out object old) && old is int v ? v : 0;
                capi.ObjectCache[CacheVersion] = version + 1;
            });
        }

        // ---------------------------------------------------------------- ships and their orders

        /// <summary>A locust left the helm for good: its stretches go too.</summary>
        public void Forget(string shipId)
        {
            if (shipId != null && lines.Remove(shipId)) linesDirty = true;
        }

        void Note(string shipId, string orders, double x, double z)
        {
            if (shipId == null) return;
            orders = orders ?? "";
            if (!lines.TryGetValue(shipId, out Line line)) { lines[shipId] = line = new Line(); linesDirty = true; }
            if (line.Orders != orders) { line.Orders = orders; linesDirty = true; }
            line.X = x; line.Z = z;
        }

        void ReadShips()
        {
            foreach (Entity entity in sapi.World.LoadedEntities.Values)
            {
                if (!(entity is EntitySailorLocust locust) || !locust.Alive) continue;
                Note(locust.WatchedAttributes.GetString(OffscreenFleet.IdAttr, null), locust.WatchedAttributes.GetString("orders", ""), locust.Pos.X, locust.Pos.Z);
            }
            foreach (VirtualShip ship in system.Fleet.Ships) Note(ship.ShipId, ship.OrdersText, ship.X, ship.Z);
        }

        // ---------------------------------------------------------------- the stretches

        public static string KeyOf(Marker a, Marker b)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            return a.X.ToString(inv) + "," + a.Y.ToString(inv) + "," + a.Z.ToString(inv) + ">" + b.X.ToString(inv) + "," + b.Y.ToString(inv) + "," + b.Z.ToString(inv);
        }

        /// <summary>
        /// Every stretch of every ship, the same stretch of two ships once. Each stop is the marker of that name nearest
        /// to the stop before it (the first: nearest to the ship), which picks one dock of a port of several. Ways not
        /// worked out yet are worked out a few at a time; a stretch without its way yet is left out until it has one.
        /// </summary>
        public List<Segment> Segments()
        {
            var list = new List<Segment>();
            var seen = new HashSet<string>();
            int budget = NewRoutesPerTick;
            foreach (Line line in lines.Values)
            {
                List<Stop> stops = Orders.Parse(line.Orders);
                if (stops.Count < 2) continue;
                var markers = new List<Marker>();
                double px = line.X, pz = line.Z;
                foreach (Stop stop in stops)
                {
                    Marker m = system.Markers.Find(stop.Name, px, pz);
                    markers.Add(m);
                    if (m != null) { px = m.X; pz = m.Z; }
                }
                for (int i = 0; i < markers.Count; i++)
                {
                    Marker a = markers[i], b = markers[(i + 1) % markers.Count];
                    if (a == null || b == null || (a.X == b.X && a.Y == b.Y && a.Z == b.Z)) continue;
                    string key = KeyOf(a, b);
                    if (!seen.Add(key)) continue;
                    if (!ways.TryGetValue(key, out double[] way))
                    {
                        if (budget-- <= 0) continue;
                        List<Vec2d> route = system.Ocean.FindRoute(a.X + 0.5, a.Z + 0.5, b.X + 0.5, b.Z + 0.5, out _);
                        way = new double[route.Count * 2];
                        for (int k = 0; k < route.Count; k++) { way[2 * k] = route[k].X; way[2 * k + 1] = route[k].Y; }
                        ways[key] = way;
                    }
                    list.Add(new Segment { Key = key, From = a, To = b, Way = way });
                }
            }
            return list;
        }

        /// <summary>The stretches whose two ends are among those markers.</summary>
        public static List<Segment> Known(List<Segment> all, HashSet<long> markers)
        {
            var list = new List<Segment>();
            if (markers == null) return list;
            foreach (Segment s in all) if (markers.Contains(Berths.KeyOf(s.From)) && markers.Contains(Berths.KeyOf(s.To))) list.Add(s);
            return list;
        }

        // ---------------------------------------------------------------- the players

        HashSet<long> KnownBy(IServerPlayer player)
        {
            if (!known.TryGetValue(player.PlayerUID, out HashSet<long> set)) { LoadKnown(player); set = known[player.PlayerUID]; }
            return set;
        }

        void LoadKnown(IServerPlayer player)
        {
            var set = new HashSet<long>();
            try
            {
                byte[] data = player.GetModdata(KnownKey);
                if (data != null && data.Length >= 4)
                {
                    using (var reader = new BinaryReader(new MemoryStream(data)))
                    {
                        int n = reader.ReadInt32();
                        for (int i = 0; i < n; i++) set.Add(reader.ReadInt64());
                    }
                }
            }
            catch (Exception e) { sapi.Logger.Warning("[sailorlocust] the docks known to " + player.PlayerName + " could not be read: " + e.Message); }
            known[player.PlayerUID] = set;
        }

        void SaveKnown(IServerPlayer player)
        {
            if (!known.TryGetValue(player.PlayerUID, out HashSet<long> set)) return;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(set.Count);
                foreach (long key in set) writer.Write(key);
                writer.Flush();
                player.SetModdata(KnownKey, stream.ToArray());
            }
        }

        /// <summary>The markers near the player become known to them; true when one is new.</summary>
        bool LookAround(IServerPlayer player)
        {
            EntityPos pos = player.Entity?.Pos;
            if (pos == null) return false;
            HashSet<long> set = KnownBy(player);
            bool added = false;
            foreach (Marker m in system.Markers.All)
            {
                double dx = m.X + 0.5 - pos.X, dz = m.Z + 0.5 - pos.Z;
                if (dx * dx + dz * dz > KnowRadius * KnowRadius || Math.Abs(m.Y - pos.Y) > 40) continue;
                if (set.Add(Berths.KeyOf(m))) added = true;
            }
            if (added) SaveKnown(player);
            return added;
        }

        void Tick(float dt)
        {
            try
            {
                ReadShips();
                List<Segment> all = Segments();
                foreach (IPlayer p in sapi.World.AllOnlinePlayers)
                {
                    var player = p as IServerPlayer;
                    if (player?.Entity == null || player.ConnectionState != EnumClientState.Playing) continue;
                    LookAround(player);
                    List<Segment> mine = Known(all, KnownBy(player));
                    mine.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                    var sig = new StringBuilder();
                    foreach (Segment s in mine) sig.Append(s.Key).Append(':').Append(s.Way.Length).Append(';');
                    string signature = sig.ToString();
                    if (sent.TryGetValue(player.PlayerUID, out string before) && before == signature) continue;
                    var packet = new RoutesPacket();
                    foreach (Segment s in mine) { packet.Keys.Add(s.Key); packet.Counts.Add(s.Way.Length); packet.Coords.AddRange(s.Way); }
                    channel.SendPacket(packet, player);
                    sent[player.PlayerUID] = signature;
                }
            }
            catch (Exception e) { sapi.Logger.Warning("[sailorlocust] the sea routes could not be sent: " + e.Message); }
        }

        // ---------------------------------------------------------------- the savegame

        public void Load()
        {
            lines.Clear(); ways.Clear();
            byte[] data = sapi.WorldManager.SaveGame.GetData(LinesKey);
            if (data == null || data.Length < 4) return;
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(data)))
                {
                    int version = reader.ReadInt32();
                    int n = reader.ReadInt32();
                    for (int i = 0; i < n; i++) lines[reader.ReadString()] = new Line { Orders = reader.ReadString(), X = reader.ReadDouble(), Z = reader.ReadDouble() };
                }
            }
            catch (Exception e) { sapi.Logger.Warning("[sailorlocust] the saved sea routes could not be read: " + e.Message); }
        }

        public void Save()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1);
                writer.Write(lines.Count);
                foreach (var pair in lines) { writer.Write(pair.Key); writer.Write(pair.Value.Orders ?? ""); writer.Write(pair.Value.X); writer.Write(pair.Value.Z); }
                writer.Flush();
                sapi.WorldManager.SaveGame.StoreData(LinesKey, stream.ToArray());
            }
            foreach (IPlayer p in sapi.World.AllOnlinePlayers) if (p is IServerPlayer player) SaveKnown(player);
        }

        public int LineCount { get { return lines.Count; } }
    }
}
