using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SailorLocust
{
    /// <summary>A boat under orders that is not in the world: what it was, where it is by now and what it does next.</summary>
    public class VirtualShip
    {
        public string ShipId;
        public string BoatCode;
        public byte[] BoatBytes;
        public string OrdersText = "";
        public int StopIndex;
        public bool Waiting;
        public double WaitLeft;
        public string OwnerUid = "";
        public double X, Y, Z;
        public float Yaw;
        /// <summary>Blocks a second, as measured while it really sailed.</summary>
        public double Speed = 2.5;

        // not saved
        public List<Stop> Stops;
        public List<Vec2d> Route;
        public int RouteIdx;
        public string RouteFor;
        public double RetryIn;
        public string Status = "";
    }

    /// <summary>
    /// Sailing out of sight, after the way the trains of Yang's Transport Tycoon go on off screen (the idea, not the
    /// code). An entity is only moved while its chunk is loaded, so a boat under orders whose chunk unloads, or that
    /// reaches the edge of the loaded world, leaves the world: a copy of the boat with all it carries goes into the
    /// savegame, and from then on it advances by time along its route. When the place where it is by now is loaded,
    /// the boat is put back on the water there with its locust at the helm.
    ///
    /// The chunk that unloaded may still hold the boat as it was. Every ship has an id and a generation number that
    /// goes up each time it leaves or enters the world; a boat that loads with an older number is that left-over and
    /// is removed, so nothing is doubled.
    /// </summary>
    public class OffscreenFleet
    {
        public const string IdAttr = "sailorShipId", GenAttr = "sailorShipGen";
        const string SaveKey = "sailorlocust-fleet";
        const double SpeedFactor = 0.85;

        readonly SailorLocustSystem system;
        ICoreServerAPI sapi;
        readonly Dictionary<string, int> gens = new Dictionary<string, int>();
        readonly Dictionary<string, VirtualShip> ships = new Dictionary<string, VirtualShip>();
        readonly List<long> removals = new List<long>();
        long lastMs = -1;

        /// <summary>Told when a ship out of sight reaches a dock (the self test listens).</summary>
        public static Action<VirtualShip, Marker> VirtualArrived;
        public static Action<VirtualShip, Entity, EntitySailorLocust> Materialized;

        public OffscreenFleet(SailorLocustSystem system) { this.system = system; }

        public int Count { get { return ships.Count; } }
        public IEnumerable<VirtualShip> Ships { get { return ships.Values; } }
        public bool IsVirtual(string shipId) { return shipId != null && ships.ContainsKey(shipId); }

        public void Start()
        {
            sapi = system.sapi;
            sapi.Event.OnEntityLoaded += OnEntityLoaded;
            sapi.Event.OnEntityDespawn += OnEntityDespawn;
            sapi.Event.RegisterGameTickListener(Tick, 500);
        }

        // ---------------------------------------------------------------- ids

        /// <summary>Gives the boat and its locust the ship's id (a new one unless given) and its current generation.</summary>
        public void Enlist(Entity boat, EntitySailorLocust locust, string shipId)
        {
            string old = boat.WatchedAttributes.GetString(IdAttr, null);
            if (shipId == null)
            {
                // a boat that sailed under another locust before: what that ship left behind in far chunks stays void
                if (old != null) gens[old] = -1;
                shipId = Guid.NewGuid().ToString("N");
            }
            if (!gens.TryGetValue(shipId, out int gen) || gen < 1) gen = 1;
            gens[shipId] = gen;
            Stamp(boat, shipId, gen);
            Stamp(locust, shipId, gen);
        }

        static void Stamp(Entity entity, string shipId, int gen)
        {
            entity.WatchedAttributes.SetString(IdAttr, shipId);
            entity.WatchedAttributes.SetInt(GenAttr, gen);
        }

        /// <summary>The locust left the helm for good: the boat is a plain boat again and every left-over of the ship is void.</summary>
        public void Retire(EntitySailorLocust locust, Entity boat)
        {
            string shipId = locust.WatchedAttributes.GetString(IdAttr, null);
            system.Routes?.Forget(shipId);
            if (shipId == null || boat == null) return;   // the boat is not here to be cleared: its id stays good
            boat.WatchedAttributes.RemoveAttribute(IdAttr);
            boat.WatchedAttributes.RemoveAttribute(GenAttr);
            gens[shipId] = -1;
            ships.Remove(shipId);
        }

        void OnEntityLoaded(Entity entity)
        {
            string shipId = entity.WatchedAttributes?.GetString(IdAttr, null);
            if (shipId == null) return;
            int gen = entity.WatchedAttributes.GetInt(GenAttr);
            if (!gens.TryGetValue(shipId, out int known)) { gens[shipId] = gen; return; }   // a list that lost it: the boat in the world is the truth
            if (ships.ContainsKey(shipId) || gen != known)
            {
                removals.Add(entity.EntityId);
                if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] left-over of ship " + Short(shipId) + " (generation " + gen + ", now " + known + ") removed: " + entity.Code);
            }
        }

        static string Short(string id) { return id == null ? "?" : id.Substring(0, Math.Min(6, id.Length)); }

        // ---------------------------------------------------------------- leaving the world

        void OnEntityDespawn(Entity entity, EntityDespawnData reason)
        {
            if (!SailorLocustSystem.Config.OffscreenSailing || reason == null || reason.Reason != EnumDespawnReason.Unload) return;
            string shipId = entity.WatchedAttributes?.GetString(IdAttr, null);
            if (shipId == null || ships.ContainsKey(shipId)) return;
            EntitySailorLocust locust = entity as EntitySailorLocust;
            Entity boat = locust != null ? (locust.Boat ?? sapi.World.GetEntityById(locust.WatchedAttributes.GetLong(EntitySailorLocust.BoatAttr))) : entity;
            if (locust == null) locust = LocustOf(boat);
            if (boat == null || locust == null) return;
            TryVirtualize(boat, locust, "its chunk unloaded");
        }

        public static EntitySailorLocust LocustOf(Entity boat)
        {
            if (boat == null) return null;
            return boat.World.GetEntityById(boat.WatchedAttributes.GetLong(EntitySailorLocust.LocustAttr)) as EntitySailorLocust;
        }

        /// <summary>Takes a boat under orders out of the world to sail on by time. False when it is not under orders or carries a player.</summary>
        public bool TryVirtualize(Entity boat, EntitySailorLocust locust, string why)
        {
            if (!SailorLocustSystem.Config.OffscreenSailing || locust.Pilot == null || !locust.Pilot.Running || locust.Pilot.Stops.Count == 0) return false;
            string shipId = boat.WatchedAttributes.GetString(IdAttr, null);
            if (shipId == null || ships.ContainsKey(shipId)) return false;
            IMountableSeat[] seats = boat.GetBehavior<EntityBehaviorSeatable>()?.Seats;
            if (seats != null) foreach (IMountableSeat seat in seats) if (seat.Passenger is EntityPlayer) return false;

            var ship = new VirtualShip
            {
                ShipId = shipId,
                BoatCode = boat.Code.ToString(),
                OrdersText = locust.WatchedAttributes.GetString("orders", ""),
                StopIndex = locust.Pilot.StopIndex,
                Waiting = locust.Pilot.Waiting,
                WaitLeft = locust.Pilot.WaitLeft,
                OwnerUid = locust.WatchedAttributes.GetString("ownerUid", ""),
                X = boat.Pos.X, Y = boat.Pos.Y, Z = boat.Pos.Z, Yaw = boat.Pos.Yaw,
                Speed = locust.Pilot.CruiseSpeed > 0.5 ? locust.Pilot.CruiseSpeed : 2.5
            };
            try
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    boat.ToBytes(writer, false);
                    writer.Flush();
                    ship.BoatBytes = stream.ToArray();
                }
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[sailorlocust] the boat " + boat.Code + " could not be copied to sail out of sight, it stays where it is: " + e.Message);
                return false;
            }
            gens[shipId] = (gens.TryGetValue(shipId, out int gen) && gen > 0 ? gen : 1) + 1;
            ships[shipId] = ship;
            locust.Pilot.Release(boat);
            removals.Add(boat.EntityId);
            removals.Add(locust.EntityId);
            if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] ship " + Short(shipId) + " (" + boat.Code + ") sails on out of sight from " + (int)ship.X + " " + (int)ship.Z + ": " + why);
            return true;
        }

        // ---------------------------------------------------------------- out of sight

        void Tick(float dtGame)
        {
            // by the clock of the server, so a slow tick does not slow the ships
            long now = sapi.World.ElapsedMilliseconds;
            double dt = lastMs < 0 ? 0 : Math.Min(5.0, (now - lastMs) / 1000.0);
            lastMs = now;

            if (removals.Count > 0)
            {
                foreach (long id in removals)
                {
                    Entity entity = sapi.World.GetEntityById(id);
                    if (entity == null || !entity.Alive) continue;
                    (entity as EntityAgent)?.TryUnmount();
                    entity.Die(EnumDespawnReason.Removed);
                }
                removals.Clear();
            }
            if (ships.Count == 0) return;
            List<VirtualShip> ready = null;
            foreach (VirtualShip ship in ships.Values)
            {
                try { Advance(ship, dt); }
                catch (Exception e) { ship.Status = "error: " + e.Message; }
                ship.RetryIn -= dt;
                if (ship.RetryIn <= 0 && LoadedAround(ship.X, ship.Y, ship.Z, 40) && (!SailorLocustSystem.Config.HideUnseen || Pilot.PlayerWithin(sapi, ship.X, ship.Z, SailorLocustSystem.Config.UnseenDistance - 40)))
                {
                    if (ready == null) ready = new List<VirtualShip>();
                    ready.Add(ship);
                }
            }
            if (ready == null) return;
            foreach (VirtualShip ship in ready)
            {
                try { if (!Materialize(ship)) ship.RetryIn = 4; }
                catch (Exception e)
                {
                    ship.RetryIn = 30;
                    sapi.Logger.Warning("[sailorlocust] ship " + Short(ship.ShipId) + " could not be put back on the water, tried again later: " + e);
                }
            }
        }

        bool LoadedAround(double x, double y, double z, int reach)
        {
            LocalNav nav = system.SharedNav;
            int bx = (int)x, by = (int)y, bz = (int)z;
            return nav.Loaded(bx, by, bz) && nav.Loaded(bx - reach, by, bz - reach) && nav.Loaded(bx + reach, by, bz - reach) && nav.Loaded(bx - reach, by, bz + reach) && nav.Loaded(bx + reach, by, bz + reach);
        }

        void Advance(VirtualShip ship, double dt)
        {
            if (ship.Stops == null) ship.Stops = Orders.Parse(ship.OrdersText);
            if (ship.Stops.Count == 0) { ship.Status = "no orders"; return; }
            ship.StopIndex = ((ship.StopIndex % ship.Stops.Count) + ship.Stops.Count) % ship.Stops.Count;
            Stop stop = ship.Stops[ship.StopIndex];
            Marker marker = system.Markers.Find(stop.Name, ship.X, ship.Z);
            if (marker == null) { ship.Status = "unknown stop " + stop.Name; return; }
            // the berths of a port are shared with the boats in the world: one table for both
            if (marker.Dock)
            {
                List<Marker> docks = system.Markers.FindAll(stop.Name);
                docks.RemoveAll(m => !m.Dock);
                Marker mine = system.Berths.HeldBy(docks, ship.ShipId);
                if (mine != null) { marker = mine; system.Berths.Keep(mine, ship.ShipId); }
                else if (ship.Waiting) system.Berths.Keep(marker, ship.ShipId);
                else if (Math.Sqrt((marker.X + 0.5 - ship.X) * (marker.X + 0.5 - ship.X) + (marker.Z + 0.5 - ship.Z) * (marker.Z + 0.5 - ship.Z)) <= SailorLocustSystem.Config.BerthAskDistance)
                {
                    Marker berth = system.Berths.Ask(stop.Name, docks, ship.ShipId, ship.X, ship.Z, null);
                    if (berth == null) { ship.Status = "waiting for a berth at " + stop.Name; return; }
                    marker = berth;
                }
            }
            if (ship.Waiting)
            {
                ship.WaitLeft -= dt;
                ship.Status = "at " + marker.Name;
                if (ship.WaitLeft <= 0) { ship.Waiting = false; ship.WaitLeft = 0; Next(ship); }
                return;
            }
            double tx = marker.X + 0.5, tz = marker.Z + 0.5;
            string key = marker.X + "," + marker.Z + "," + ship.StopIndex;
            if (ship.Route == null || ship.RouteFor != key)
            {
                ship.Route = system.Ocean.FindRoute(ship.X, ship.Z, tx, tz, out _);
                ship.RouteIdx = 1;
                ship.RouteFor = key;
            }
            double step = ship.Speed * SpeedFactor * dt;
            double radius = marker.Dock ? SailorLocustSystem.Config.ArrivalRadius : SailorLocustSystem.Config.BuoyPassRadius;
            int guard = 0;
            while (step > 0)
            {
                double toEnd = Math.Sqrt((tx - ship.X) * (tx - ship.X) + (tz - ship.Z) * (tz - ship.Z));
                if (toEnd <= radius || ship.RouteIdx >= ship.Route.Count)
                {
                    if (marker.Dock)
                    {
                        ship.Waiting = true;
                        ship.WaitLeft = stop.Wait >= 0 ? stop.Wait : SailorLocustSystem.Config.DefaultWaitSeconds;
                        ship.Status = "at " + marker.Name;
                        VirtualArrived?.Invoke(ship, marker);
                    }
                    else Next(ship);
                    return;
                }
                Vec2d p = ship.Route[ship.RouteIdx];
                double dx = p.X - ship.X, dz = p.Y - ship.Z, d = Math.Sqrt(dx * dx + dz * dz);
                if (d < 1e-6) { ship.RouteIdx++; continue; }
                ship.Yaw = (float)Math.Atan2(dx, dz);
                // on the last stretch it stops short of the marker, as the real boat does
                double room = ship.RouteIdx == ship.Route.Count - 1 ? Math.Max(0, d - radius + 0.5) : d;
                double go = Math.Min(step, room);
                ship.X += dx / d * go; ship.Z += dz / d * go;
                step -= go;
                if (go >= d - 1e-6) ship.RouteIdx++;
                if (++guard > 64) break;
            }
            ship.Status = "to " + marker.Name;
        }

        static void Next(VirtualShip ship)
        {
            ship.StopIndex = (ship.StopIndex + 1) % ship.Stops.Count;
            ship.Route = null;
        }

        // ---------------------------------------------------------------- back into the world

        bool Materialize(VirtualShip ship)
        {
            LocalNav nav = system.SharedNav;
            // waiting at a dock: it comes back tied in its berth (the pilot plans the berth for its own hull, below);
            // the dock it holds, or the nearest of the port
            Marker tieAt = null;
            if (ship.Waiting && ship.Stops != null && ship.Stops.Count > 0)
            {
                string port = ship.Stops[((ship.StopIndex % ship.Stops.Count) + ship.Stops.Count) % ship.Stops.Count].Name;
                List<Marker> docks = system.Markers.FindAll(port);
                docks.RemoveAll(m => !m.Dock);
                tieAt = system.Berths.HeldBy(docks, ship.ShipId) ?? system.Markers.Find(port, ship.X, ship.Z);
                if (tieAt == null || !tieAt.Dock || tieAt.Facing < 0) tieAt = null;
                else { ship.X = tieAt.X + 0.5; ship.Z = tieAt.Z + 0.5; }
            }
            int waterY = nav.FindWaterY(ship.X, ship.Y, ship.Z);
            if (waterY < 0) waterY = nav.FindWaterY(ship.X, sapi.World.SeaLevel - 1, ship.Z);
            if (waterY < 0) waterY = sapi.World.SeaLevel - 1;
            nav.Scan(ship.X, ship.Z, waterY);
            // the open water nearest to where the route puts it: the long route may run over a shore the ocean map does not show
            int best = -1; double bestScore = double.MaxValue;
            for (int dz = -LocalNav.R + 4; dz <= LocalNav.R - 4; dz++)
            {
                for (int dx = -LocalNav.R + 4; dx <= LocalNav.R - 4; dx++)
                {
                    double wx = Math.Floor(ship.X) + dx + 0.5, wz = Math.Floor(ship.Z) + dz + 0.5;
                    int clear = nav.Clearance(wx, wz);
                    if (clear < 3) continue;
                    double score = dx * dx + dz * dz - Math.Min(clear, 5) * 6;
                    if (score < bestScore) { bestScore = score; best = (dz + LocalNav.R) * LocalNav.N + dx + LocalNav.R; }
                }
            }
            if (best < 0) return false;
            double px = Math.Floor(ship.X) + best % LocalNav.N - LocalNav.R + 0.5, pz = Math.Floor(ship.Z) + best / LocalNav.N - LocalNav.R + 0.5;

            EntityProperties type = sapi.World.GetEntityType(new AssetLocation(ship.BoatCode));
            if (type == null)
            {
                sapi.Logger.Warning("[sailorlocust] ship " + Short(ship.ShipId) + ": the boat " + ship.BoatCode + " is not in this server's mods any more; it stays out of sight");
                ship.RetryIn = 600;
                return true;
            }
            Entity boat = sapi.World.ClassRegistry.CreateEntity(type);
            using (var reader = new BinaryReader(new MemoryStream(ship.BoatBytes))) boat.FromBytes(reader, false);
            boat.EntityId = 0;
            boat.WatchedAttributes.RemoveAttribute("seatdata");
            boat.WatchedAttributes.RemoveAttribute(EntitySailorLocust.LocustAttr);
            double y = Math.Abs(ship.Y - (waterY + 1)) < 1.2 ? ship.Y : waterY + 1.0;
            boat.Pos.SetPos(px, y, pz);
            boat.Pos.Yaw = ship.Yaw;
            boat.Pos.Motion.Set(0, 0, 0);
            int gen = (gens.TryGetValue(ship.ShipId, out int known) && known > 0 ? known : 1) + 1;
            gens[ship.ShipId] = gen;
            Stamp(boat, ship.ShipId, gen);
            sapi.World.SpawnEntity(boat);
            ships.Remove(ship.ShipId);

            string error = system.Install(boat, ship.OwnerUid, out EntitySailorLocust locust, ship.ShipId);
            if (error != null)
            {
                sapi.Logger.Warning("[sailorlocust] ship " + Short(ship.ShipId) + " is back on the water at " + (int)px + " " + (int)pz + " but its locust could not take the helm (" + error + "); a locust is left on board as an item");
                Item item = sapi.World.GetItem(new AssetLocation(SailorLocustSystem.Domain, "sailorlocust"));
                if (item != null) sapi.World.SpawnItemEntity(new ItemStack(item), new Vec3d(px, y + 1, pz));
                return true;
            }
            locust.WatchedAttributes.SetString("orders", ship.OrdersText);
            locust.Pilot.ReloadOrders();
            locust.Pilot.StopIndex = ship.StopIndex;
            locust.Pilot.Waiting = ship.Waiting;
            locust.Pilot.WaitLeft = ship.WaitLeft;
            locust.Pilot.CruiseSpeed = ship.Speed;
            locust.SetRunning(true);
            if (tieAt != null) locust.Pilot.TieUpAt(tieAt);
            if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] ship " + Short(ship.ShipId) + " (" + ship.BoatCode + ") is back on the water at " + (int)boat.Pos.X + " " + (int)boat.Pos.Z + (tieAt != null ? ", tied at " + tieAt.Name : "") + ", generation " + gen);
            Materialized?.Invoke(ship, boat, locust);
            return true;
        }

        // ---------------------------------------------------------------- the savegame

        public void Load()
        {
            gens.Clear(); ships.Clear();
            byte[] data = sapi.WorldManager.SaveGame.GetData(SaveKey);
            if (data == null || data.Length < 4) return;
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(data)))
                {
                    int version = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++) { string id = reader.ReadString(); gens[id] = reader.ReadInt32(); }
                    count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var ship = new VirtualShip
                        {
                            ShipId = reader.ReadString(), BoatCode = reader.ReadString(), OrdersText = reader.ReadString(), StopIndex = reader.ReadInt32(),
                            Waiting = reader.ReadBoolean(), WaitLeft = reader.ReadDouble(), OwnerUid = reader.ReadString(),
                            X = reader.ReadDouble(), Y = reader.ReadDouble(), Z = reader.ReadDouble(), Yaw = reader.ReadSingle(), Speed = reader.ReadDouble()
                        };
                        ship.BoatBytes = reader.ReadBytes(reader.ReadInt32());
                        ships[ship.ShipId] = ship;
                    }
                }
                sapi.Logger.Notification("[sailorlocust] " + ships.Count + " ship(s) sailing out of sight, " + gens.Count + " known");
            }
            catch (Exception e) { sapi.Logger.Error("[sailorlocust] the ships sailing out of sight could not be read from the savegame: " + e); }
        }

        public void Save()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1);
                writer.Write(gens.Count);
                foreach (var pair in gens) { writer.Write(pair.Key); writer.Write(pair.Value); }
                writer.Write(ships.Count);
                foreach (VirtualShip ship in ships.Values)
                {
                    writer.Write(ship.ShipId); writer.Write(ship.BoatCode); writer.Write(ship.OrdersText ?? ""); writer.Write(ship.StopIndex);
                    writer.Write(ship.Waiting); writer.Write(ship.WaitLeft); writer.Write(ship.OwnerUid ?? "");
                    writer.Write(ship.X); writer.Write(ship.Y); writer.Write(ship.Z); writer.Write(ship.Yaw); writer.Write(ship.Speed);
                    writer.Write(ship.BoatBytes.Length); writer.Write(ship.BoatBytes);
                }
                writer.Flush();
                sapi.WorldManager.SaveGame.StoreData(SaveKey, stream.ToArray());
            }
        }

        public string Describe()
        {
            if (!SailorLocustSystem.Config.OffscreenSailing) return "Sailing out of sight is off in the config.";
            var sb = new StringBuilder();
            sb.AppendLine(ships.Count + " ship(s) sailing out of sight:");
            foreach (VirtualShip ship in ships.Values) sb.AppendLine(Short(ship.ShipId) + " " + ship.BoatCode + " at " + (int)ship.X + " " + (int)ship.Z + ", " + ship.Status + ", " + ship.Speed.ToString("0.0") + " b/s");
            return sb.ToString();
        }
    }
}
