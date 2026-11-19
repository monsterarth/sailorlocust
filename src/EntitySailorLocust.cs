using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SailorLocust
{
    /// <summary>
    /// Put on a boat, in the player's game, while a sailor locust sails it: pointing at the boat tells what the locust
    /// is doing, and pointing at the locust's seat tells what a click there does.
    /// </summary>
    public class EntityBehaviorSailorHelm : EntityBehavior
    {
        public EntityBehaviorSailorHelm(Entity entity) : base(entity) { }

        public override string PropertyName() { return "sailorhelm"; }

        EntitySailorLocust Locust
        {
            get
            {
                var locust = entity.World.GetEntityById(entity.WatchedAttributes.GetLong(EntitySailorLocust.LocustAttr)) as EntitySailorLocust;
                return locust != null && locust.Alive ? locust : null;
            }
        }

        public override void GetInfoText(StringBuilder infotext)
        {
            EntitySailorLocust locust = Locust;
            if (locust == null) return;
            infotext.AppendLine(SailorLocustSystem.T("helm-info", locust.StatusText()));
            infotext.AppendLine(SailorLocustSystem.T("helm-hint"));
            infotext.AppendLine(SailorLocustSystem.T("helm-key"));
        }

        public override WorldInteraction[] GetInteractionHelp(IClientWorldAccessor world, EntitySelection es, IClientPlayer player, ref EnumHandling handled)
        {
            EntitySailorLocust locust = Locust;
            IMountableSeat seat = locust?.PerchSeat(entity);
            var boxes = entity.GetBehavior<EntityBehaviorSelectionBoxes>();
            int index = es?.SelectionBoxIndex ?? 0;
            if (seat?.Config == null || boxes?.selectionBoxes == null || index <= 0 || index - 1 >= boxes.selectionBoxes.Length) return null;
            if (boxes.selectionBoxes[index - 1]?.AttachPoint?.Code != seat.Config.APName) return null;
            return new[]
            {
                new WorldInteraction { ActionLangCode = SailorLocustSystem.Domain + ":help-orders", MouseButton = EnumMouseButton.Right },
                new WorldInteraction { ActionLangCode = SailorLocustSystem.Domain + ":help-takeback", MouseButton = EnumMouseButton.Right, HotKeyCode = "sneak" }
            };
        }
    }

    /// <summary>
    /// The locust at the helm. It sits in the boat's commanding seat as a player would, and the boat reads its keys.
    /// Boat seats take players only; this entity seats itself by the same steps the game uses when it puts a rider
    /// back after a chunk loads.
    /// </summary>
    public class EntitySailorLocust : EntityAgent
    {
        public const int PacketSaveOrders = 2001, PacketToggle = 2002, PacketPickup = 2003, PacketRefresh = 2004, PacketOpen = 2005, PacketRoute = 2006, PacketStops = 2007, PacketStopsList = 2008;

        public Pilot Pilot;
        double unmountedFor;
        static Shape hatShape;
        static bool hatTried;

        public override bool IsInteractable { get { return true; } }

        public const string BoatAttr = "boatId", LocustAttr = "sailorLocustId";
        Entity boatCache;

        /// <summary>The boat it sails, when that boat is loaded.</summary>
        public Entity Boat
        {
            get
            {
                long id = WatchedAttributes.GetLong(BoatAttr);
                if (id == 0) return null;
                if (boatCache == null || boatCache.EntityId != id || !boatCache.Alive) boatCache = World.GetEntityById(id);
                return boatCache != null && boatCache.Alive ? boatCache : null;
            }
        }

        public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
        {
            base.Initialize(properties, api, InChunkIndex3d);
            if (api.Side != EnumAppSide.Server) return;
            Pilot = new Pilot(this, api.ModLoader.GetModSystem<SailorLocustSystem>());
        }

        public override bool ShouldReceiveDamage(DamageSource damageSource, float damage) { return false; }

        public override string GetName() { return SailorLocustSystem.T("item-sailorlocust"); }

        IMountableSeat heldSeat;

        /// <summary>
        /// Takes that boat: sits in that seat of its helm. With a rider in a commanding seat the server leaves the boat's
        /// movement to that rider's client, and a locust has none: so while the locust sits there, that seat does not
        /// count as commanding. The seat is taken (nobody sits on the locust, and the game does not pick up a boat with
        /// a passenger), the server keeps moving the boat, and the locust is drawn fixed to its seat.
        /// </summary>
        public void Attach(Entity boat, IMountableSeat perch)
        {
            WatchedAttributes.SetLong(BoatAttr, boat.EntityId);
            WatchedAttributes.SetString("perchSeat", perch?.SeatId ?? "");
            boat.WatchedAttributes.SetLong(LocustAttr, EntityId);
            boatCache = boat;
            if (perch != null) MountSeat(perch);
            FollowBoat(boat);
        }

        /// <summary>Boat seats take players only: the locust seats itself by the steps the game uses to put a rider back after a chunk loads.</summary>
        public override bool TryMount(IMountableSeat onmount) { return MountSeat(onmount); }

        public bool MountSeat(IMountableSeat seat)
        {
            if (seat == null) return false;
            if (seat.Passenger != null && seat.Passenger != this) return false;
            if (MountedOn != null && MountedOn != seat) { GiveSeatBack(); TryUnmount(); }
            doMount(seat);
            var tree = new TreeAttribute();
            seat.MountableToTreeAttributes(tree);
            WatchedAttributes["mountedOn"] = tree;
            if (World.Side == EnumAppSide.Server)
            {
                Entity boat = seat.MountSupplier?.OnEntity;
                if (boat != null)
                {
                    boat.GetBehavior<EntityBehaviorSeatable>()?.ToBytes(true);
                    boat.WatchedAttributes.MarkPathDirty("seatdata");
                }
                WatchedAttributes.MarkPathDirty("mountedOn");
            }
            return true;
        }

        protected override void doMount(IMountableSeat mountable)
        {
            // on both sides, and before the boat is told of its new rider
            if (mountable?.Config != null && mountable.Config.Controllable)
            {
                mountable.Config.Controllable = false;
                heldSeat = mountable;
            }
            base.doMount(mountable);
        }

        /// <summary>The seats count as commanding again.</summary>
        void GiveSeatBack()
        {
            if (heldSeat?.Config != null) heldSeat.Config.Controllable = true;
            heldSeat = null;
            FreeOtherSeats();
        }

        readonly List<IMountableSeat> passengerSeats = new List<IMountableSeat>();

        void FreeOtherSeats()
        {
            foreach (IMountableSeat seat in passengerSeats) if (seat?.Config != null) seat.Config.Controllable = true;
            passengerSeats.Clear();
        }

        /// <summary>
        /// While the locust is under way, whoever sits in the boat is a passenger: no seat commands (a boat with two
        /// helms, as the raft, would otherwise be taken over by the first player to sit down, and the server would
        /// leave its movement to that player's game). At anchor the other seats command again and a player can sail.
        /// Done on both sides, each for its own copy of the boat.
        /// </summary>
        void KeepPassengers(Entity boat)
        {
            bool underWay = WatchedAttributes.GetBool("running") && WatchedAttributes.GetString("orders", "").Trim().Length > 0;
            if (!underWay) { if (passengerSeats.Count > 0) FreeOtherSeats(); return; }
            IMountableSeat[] seats = boat.GetBehavior<EntityBehaviorSeatable>()?.Seats;
            if (seats == null) return;
            foreach (IMountableSeat seat in seats)
            {
                if (seat?.Config == null || !seat.Config.Controllable || seat == heldSeat) continue;
                seat.Config.Controllable = false;
                passengerSeats.Add(seat);
            }
        }

        public override void OnEntityDespawn(EntityDespawnData despawn)
        {
            GiveSeatBack();
            base.OnEntityDespawn(despawn);
        }

        public IMountableSeat PerchSeat(Entity boat)
        {
            string perch = WatchedAttributes.GetString("perchSeat", "");
            IMountableSeat[] seats = boat?.GetBehavior<EntityBehaviorSeatable>()?.Seats;
            if (seats == null) return null;
            foreach (IMountableSeat seat in seats) if (seat.SeatId == perch) return seat;
            return null;
        }

        void FollowBoat(Entity boat)
        {
            double x = boat.Pos.X, y = boat.Pos.Y + 0.5, z = boat.Pos.Z;
            try
            {
                EntityPos at = (MountedOn ?? PerchSeat(boat))?.SeatPosition;
                if (at != null) { x = at.X; y = at.Y; z = at.Z; }
            }
            catch { }
            Pos.SetPos(x, y, z);
            Pos.Yaw = boat.Pos.Yaw;
            Pos.Motion.Set(boat.Pos.Motion);
        }

        public override void OnGameTick(float dt)
        {
            base.OnGameTick(dt);
            if (MountedOn == null && heldSeat != null) GiveSeatBack();
            if (Api.Side != EnumAppSide.Server)
            {
                // what the player reads when pointing at the locust's seat is the boat's: the boat is given the words
                Entity mine = Boat;
                if (mine != null && mine.GetBehavior<EntityBehaviorSailorHelm>() == null) mine.AddBehavior(new EntityBehaviorSailorHelm(mine));
                if (mine != null) KeepPassengers(mine);
                return;
            }
            Entity boat = Boat;
            if (boat == null)
            {
                // a boat that is not loaded is waited for a good while (its chunk may come later); one that is gone is not
                unmountedFor += dt;
                if (unmountedFor > 300) DropAsItem(null);
                return;
            }
            unmountedFor = 0;
            if (MountedOn == null)
            {
                // after a chunk loads the boat may come a moment later than the locust
                IMountableSeat seat = PerchSeat(boat);
                if (seat != null && (seat.Passenger == null || seat.Passenger == this)) MountSeat(seat);
            }
            KeepPassengers(boat);
            FollowBoat(boat);
            if (Pilot != null)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Pilot.Tick(dt);
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Pilot.CostTicks++; Pilot.CostMs += ms; if (ms > Pilot.CostMaxMs) Pilot.CostMaxMs = ms;
            }
        }
        /// <summary>Leaves the helm and becomes the item again: in that player's hands, or on the spot.</summary>
        public void DropAsItem(IServerPlayer toPlayer)
        {
            Entity boat = Boat;
            Pilot?.Release(boat);
            string shipId = WatchedAttributes.GetString(OffscreenFleet.IdAttr, null);
            if (shipId != null) Api.ModLoader.GetModSystem<SailorLocustSystem>()?.Berths?.Leave(shipId);
            GiveSeatBack();
            if (MountedOn != null) TryUnmount();
            Api.ModLoader.GetModSystem<SailorLocustSystem>()?.Fleet?.Retire(this, boat);
            boat?.WatchedAttributes.RemoveAttribute(LocustAttr);
            Item item = World.GetItem(new AssetLocation(SailorLocustSystem.Domain, "sailorlocust"));
            if (item != null)
            {
                var stack = new ItemStack(item);
                if (toPlayer == null || !toPlayer.InventoryManager.TryGiveItemstack(stack, true)) World.SpawnItemEntity(stack, Pos.XYZ.Add(0, 0.5, 0));
            }
            Die(EnumDespawnReason.Removed);
        }

        public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode)
        {
            if (mode != EnumInteractMode.Interact) return;
            if (Api is ICoreClientAPI capi && byEntity == capi.World.Player.Entity)
            {
                if (byEntity.Controls.Sneak) capi.Network.SendEntityPacket(EntityId, PacketPickup, null);
                else
                {
                    capi.Network.SendEntityPacket(EntityId, PacketRefresh, null);
                    new GuiDialogSailingOrders(capi, this).TryOpen();
                }
            }
        }

        public override WorldInteraction[] GetInteractionHelp(IClientWorldAccessor world, EntitySelection es, IClientPlayer player)
        {
            return new[]
            {
                new WorldInteraction { ActionLangCode = SailorLocustSystem.Domain + ":orders-title", MouseButton = EnumMouseButton.Right },
                new WorldInteraction { ActionLangCode = SailorLocustSystem.Domain + ":orders-pickup", MouseButton = EnumMouseButton.Right, HotKeyCode = "sneak" }
            };
        }

        public bool MayCommand(IServerPlayer player)
        {
            if (player?.Entity == null || player.Entity.Pos.DistanceTo(Pos) > 14) return false;
            if (!SailorLocustSystem.Config.OwnerOnly) return true;
            string owner = WatchedAttributes.GetString("ownerUid", "");
            if (owner.Length == 0 || owner == player.PlayerUID || player.HasPrivilege(Privilege.controlserver)) return true;
            player.SendIngameError("err-notowner", SailorLocustSystem.T("err-notowner"));
            return false;
        }

        public override void OnReceivedClientPacket(IServerPlayer player, int packetId, byte[] data)
        {
            base.OnReceivedClientPacket(player, packetId, data);
            if (packetId == PacketStops) { SendStops(player); return; }
            if (packetId < PacketSaveOrders || packetId > PacketRefresh) return;
            if (packetId == PacketRefresh) { UpdateKnown(); return; }
            if (!MayCommand(player)) return;
            switch (packetId)
            {
                case PacketSaveOrders:
                    string text = data == null ? "" : Encoding.UTF8.GetString(data);
                    if (text.Length > 3000) text = text.Substring(0, 3000);
                    SetOrders(text.Replace("\r", ""));
                    break;
                case PacketToggle:
                    SetRunning(!WatchedAttributes.GetBool("running"));
                    break;
                case PacketPickup:
                    DropAsItem(player);
                    break;
            }
        }

        public void SetOrders(string text)
        {
            // the stop it is on stays the one it is on (the sheet sends every click): only a route now shorter starts over
            WatchedAttributes.SetString("orders", text ?? "");
            Pilot?.ReloadOrders();
        }

        public void SetRunning(bool running)
        {
            WatchedAttributes.SetBool("running", running);
            if (!running) Pilot?.Release(Boat);
        }

        /// <summary>The docks and buoys nearest to here, for the orders sheet: "D|name|blocks" on each line.</summary>
        /// <summary>
        /// The docks and buoys this player may add to a route, with where they are, for the sheet and the map: those the
        /// player has been near (as for the sea routes on maps) and those near the boat. A port is one line, at its
        /// nearest dock, with its number of berths; a port's own buoys are left out.
        /// </summary>
        public void SendStops(IServerPlayer player)
        {
            if (!(Api is ICoreServerAPI sapi) || player == null) return;
            var system = sapi.ModLoader.GetModSystem<SailorLocustSystem>();
            HashSet<long> knows = system.Routes.KnownBy(player);
            var near = new HashSet<long>();
            foreach (Marker m in system.Markers.Nearest(Pos.X, Pos.Z, 24)) near.Add(Berths.KeyOf(m));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (Marker any in system.Markers.All)
            {
                if (any.Role != Marker.RoleRoute) continue;
                long key = Berths.KeyOf(any);
                if (!knows.Contains(key) && !near.Contains(key)) continue;
                Marker m = system.Markers.Find(any.Name, player.Entity?.Pos.X ?? Pos.X, player.Entity?.Pos.Z ?? Pos.Z) ?? any;
                if (!seen.Add(m.Name)) continue;
                int berths = 0;
                foreach (Marker other in system.Markers.FindAll(m.Name)) if (other.Dock == m.Dock && other.Role == Marker.RoleRoute) berths++;
                sb.Append(m.Dock ? "D|" : "B|").Append(m.Name).Append('|').Append(m.X.ToString(inv)).Append('|').Append(m.Z.ToString(inv)).Append('|').Append(Math.Max(1, berths)).Append('\n');
                if (seen.Count >= 200) break;
            }
            sapi.Network.SendEntityPacket(player, EntityId, PacketStopsList, Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public void UpdateKnown()
        {
            var system = Api.ModLoader.GetModSystem<SailorLocustSystem>();
            var sb = new StringBuilder();
            // a port of several docks is one line, the nearest of them, with how many berths it has
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Marker near in system.Markers.Nearest(Pos.X, Pos.Z, 64))
            {
                // a port's own buoys (waiting area, way out) are not stops; a name that has docks is the port, whatever
                // buoys share it
                if (near.Role != Marker.RoleRoute) continue;
                Marker m = system.Markers.Find(near.Name, Pos.X, Pos.Z) ?? near;
                if (!seen.Add(m.Name)) continue;
                int berths = 0;
                foreach (Marker other in system.Markers.FindAll(m.Name)) if (other.Dock == m.Dock && other.Role == Marker.RoleRoute) berths++;
                int d = (int)Math.Sqrt((m.X - Pos.X) * (m.X - Pos.X) + (m.Z - Pos.Z) * (m.Z - Pos.Z));
                sb.Append(m.Dock ? "D|" : "B|").Append(m.Name).Append('|').Append(d).Append('|').Append(berths).Append('\n');
                if (seen.Count >= 24) break;
            }
            WatchedAttributes.SetString("known", sb.ToString());
        }

/// <summary>The server tells this player's game to open the orders sheet (they clicked the locust's seat).</summary>
        public override void OnReceivedServerPacket(int packetid, byte[] data)
        {
            base.OnReceivedServerPacket(packetid, data);
            if (packetid == PacketOpen && Api is ICoreClientAPI capi) new GuiDialogSailingOrders(capi, this).TryOpen();
            if (packetid == PacketRoute && Api is ICoreClientAPI capi2 && data != null) RouteView.Show(capi2, data);
            if (packetid == PacketStopsList && data != null) StopsMapLayer.SetKnown(Encoding.UTF8.GetString(data));
        }

        public string StatusText()
        {
            string[] parts = WatchedAttributes.GetString("status", "status-idle").Split('|');
            object[] args = new object[parts.Length - 1];
            Array.Copy(parts, 1, args, 0, args.Length);
            return SailorLocustSystem.T(parts[0], args);
        }

        public override string GetInfoText()
        {
            return StatusText();
        }

        public override void OnTesselation(ref Shape entityShape, string shapePathForLogging)
        {
            base.OnTesselation(ref entityShape, shapePathForLogging);
            try
            {
                if (!hatTried)
                {
                    hatTried = true;
                    hatShape = Shape.TryGet(Api, new AssetLocation(SailorLocustSystem.Domain, "shapes/entity/sailorhat.json"));
                    if (hatShape == null) Api.Logger.Warning("[sailorlocust] the hat's shape was not found");
                }
                if (hatShape == null) return;
                // the game's locust shape is shared: the hat goes on a copy
                Shape withHat = entityShape.Clone();
                withHat.StepParentShape(hatShape.Clone(), "sailorlocust:shapes/entity/sailorhat.json", shapePathForLogging, Api.Logger, (code, location) => { });
                entityShape = withHat;
            }
            catch (Exception e)
            {
                Api.Logger.Warning("[sailorlocust] the hat could not be put on: " + e.Message);
                hatShape = null;
            }
        }
    }
}
