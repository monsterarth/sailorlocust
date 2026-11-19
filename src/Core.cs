// Sailor Locust: a locust that takes the helm of a boat and sails it between named docks.
// Inspired by the Conductor Locust of Yang's Transport Tycoon and by Boat Autopilot (Majke): the idea only, no code and
// no assets of either.
using System;
using System.Collections.Generic;
using System.IO;
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
    public class SailorConfig
    {
        /// <summary>False removes the mod's grid recipes: the items then come only from creative or from staff.</summary>
        public bool EnableRecipes = true;
        /// <summary>World generation's ocean map value from which a 32 block cell counts as open sea for the long route.</summary>
        public int OceanThreshold = 40;
        /// <summary>Blocks from a dock at which the boat counts as arrived.</summary>
        public double ArrivalRadius = 7;
        /// <summary>Blocks from a buoy at which it counts as passed.</summary>
        public double BuoyPassRadius = 9;
        /// <summary>Blocks from a port at which a boat asks for a berth (docks of the same name are the berths of one port).</summary>
        public double BerthAskDistance = 96;
        /// <summary>Seconds waited at a dock when the orders give none.</summary>
        public int DefaultWaitSeconds = 60;
        /// <summary>Sail setting on open water for boats with sails (0 furled, 1 half, 2 full).</summary>
        public int CruiseSail = 2;
        /// <summary>True: only whoever placed a sailor locust may change its orders or take it back.</summary>
        public bool OwnerOnly = false;
        /// <summary>Etapa 2: sail on by time along the route while nobody is near (the boat leaves the world and comes back).</summary>
        public bool OffscreenSailing = true;
        /// <summary>Logs the pilot's decisions.</summary>
        public bool Debug = false;
        /// <summary>A boat under orders with no player within UnseenDistance blocks leaves the world and sails on by time; it is put back when one comes within UnseenDistance - 40.</summary>
        public bool HideUnseen = true;
        public int UnseenDistance = 160;
    }

    public class Marker
    {
        public string Name;
        public int X, Y, Z;
        public bool Dock;
        /// <summary>A dock post looks one of eight ways, towards its water (Docking); -1: not said yet.</summary>
        public int Facing = -1;
        /// <summary>A dock the player keeps for small boats: a boat longer than Pilot.SmallLength is never given it.</summary>
        public bool SmallOnly;
        /// <summary>For a buoy: 0 a buoy of the route; 1 the waiting area of the port of its name; 2 the way out of that port; 3 the way in (every boat bound for the port passes it).</summary>
        public int Role;
        public const int RoleRoute = 0, RoleWait = 1, RoleExit = 2, RoleEntry = 3;
    }

    /// <summary>The docks and buoys of the world by name, kept in the savegame so a route can be worked out to a dock whose chunk is not loaded.</summary>
    public class MarkerRegistry
    {
        const string SaveKey = "sailorlocust-markers";
        readonly ICoreServerAPI sapi;
        readonly Dictionary<long, Marker> byPos = new Dictionary<long, Marker>();

        public MarkerRegistry(ICoreServerAPI sapi) { this.sapi = sapi; }

        static long Key(int x, int y, int z) { return ((long)x & 0x3FFFFF) | (((long)z & 0x3FFFFF) << 22) | (((long)y & 0xFFFF) << 44); }

        public int Count { get { return byPos.Count; } }
        public IEnumerable<Marker> All { get { return byPos.Values; } }

        public void Set(BlockPos pos, string name, bool dock, int facing = -1, bool smallOnly = false, int role = 0)
        {
            byPos[Key(pos.X, pos.Y, pos.Z)] = new Marker { Name = name, X = pos.X, Y = pos.Y, Z = pos.Z, Dock = dock, Facing = facing, SmallOnly = smallOnly, Role = dock ? 0 : role };
        }

        public void Remove(BlockPos pos) { byPos.Remove(Key(pos.X, pos.Y, pos.Z)); }

        /// <summary>The marker of that name (case and outer blanks ignored); of several, the nearest to the given point.</summary>
        public Marker Find(string name, double nearX, double nearZ)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string wanted = name.Trim();
            Marker best = null; double bestDist = double.MaxValue;
            // a name that has docks is a port: its stop is a dock, never a buoy that happens to share the name
            bool port = false;
            foreach (Marker m in byPos.Values) if (m.Dock && string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase)) { port = true; break; }
            foreach (Marker m in byPos.Values)
            {
                if (m.Role != Marker.RoleRoute || (port && !m.Dock) || !string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                double dx = m.X - nearX, dz = m.Z - nearZ, d = dx * dx + dz * dz;
                if (d < bestDist) { bestDist = d; best = m; }
            }
            return best;
        }

        /// <summary>The buoy of that port with that role (waiting area, way out) nearest to the point, or null.</summary>
        public Marker FindRole(string port, int role, double nearX, double nearZ)
        {
            if (string.IsNullOrWhiteSpace(port)) return null;
            string wanted = port.Trim();
            Marker best = null; double bestDist = double.MaxValue;
            foreach (Marker m in byPos.Values)
            {
                if (m.Dock || m.Role != role || !string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                double d = Sq(m.X - nearX) + Sq(m.Z - nearZ);
                if (d < bestDist) { bestDist = d; best = m; }
            }
            return best;
        }

        /// <summary>Every marker of that name: docks of one name are the berths of one port.</summary>
        public List<Marker> FindAll(string name)
        {
            var list = new List<Marker>();
            if (string.IsNullOrWhiteSpace(name)) return list;
            string wanted = name.Trim();
            foreach (Marker m in byPos.Values) if (string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase)) list.Add(m);
            return list;
        }

        public List<Marker> Nearest(double x, double z, int max)
        {
            var list = new List<Marker>(byPos.Values);
            list.Sort((a, b) => (Sq(a.X - x) + Sq(a.Z - z)).CompareTo(Sq(b.X - x) + Sq(b.Z - z)));
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        static double Sq(double v) { return v * v; }

        public void Load()
        {
            byPos.Clear();
            byte[] data = sapi.WorldManager.SaveGame.GetData(SaveKey);
            if (data == null || data.Length < 4) return;
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(data)))
                {
                    int version = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var m = new Marker { Name = reader.ReadString(), X = reader.ReadInt32(), Y = reader.ReadInt32(), Z = reader.ReadInt32(), Dock = reader.ReadBoolean() };
                        if (version >= 2) m.Facing = reader.ReadInt32();
                        if (version >= 3) { m.SmallOnly = reader.ReadBoolean(); m.Role = reader.ReadInt32(); }
                        byPos[Key(m.X, m.Y, m.Z)] = m;
                    }
                }
            }
            catch (Exception e) { sapi.Logger.Warning("[sailorlocust] could not read the saved docks and buoys: " + e.Message); }
        }

        public void Save()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(3);
                writer.Write(byPos.Count);
                foreach (Marker m in byPos.Values) { writer.Write(m.Name ?? ""); writer.Write(m.X); writer.Write(m.Y); writer.Write(m.Z); writer.Write(m.Dock); writer.Write(m.Facing); writer.Write(m.SmallOnly); writer.Write(m.Role); }
                writer.Flush();
                sapi.WorldManager.SaveGame.StoreData(SaveKey, stream.ToArray());
            }
        }
    }

    public class SailorLocustSystem : ModSystem
    {
        public const string Domain = "sailorlocust";
        public static SailorConfig Config = new SailorConfig();

        public ICoreServerAPI sapi;
        public MarkerRegistry Markers;
        public OceanGrid Ocean;
        public OffscreenFleet Fleet;
        public Berths Berths;
        public RouteShare Routes;
        public FleetWatch Watch;
        LocalNav sharedNav;
        /// <summary>One look at the blocks at a time is enough: the server's game thread runs the pilots one after another.</summary>
        public LocalNav SharedNav { get { return sharedNav ?? (sharedNav = new LocalNav(sapi)); } }

        public override void Start(ICoreAPI api)
        {
            api.RegisterEntity("EntitySailorLocust", typeof(EntitySailorLocust));
            api.RegisterItemClass("ItemSailorLocust", typeof(ItemSailorLocust));
            api.RegisterItemClass("ItemSailorWand", typeof(ItemSailorWand));
            api.RegisterBlockClass("BlockSailorMarker", typeof(BlockSailorMarker));
            api.RegisterBlockEntityClass("SailorMarker", typeof(BlockEntitySailorMarker));
        }

        public const string HotkeyOrders = "sailorlocust-orders";

        public override void StartClientSide(ICoreClientAPI api)
        {
            // out at sea the locust's seat is out of reach from another seat, and from the water: a key opens its orders
            new BerthPreview(api);
            RouteShare.StartClient(api);
            FleetMapLayer.Register(api);
            StopsMapLayer.Register(api);
            api.Input.RegisterHotKey(HotkeyOrders, "Sailor Locust: sailing orders", GlKeys.O, HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler(HotkeyOrders, combo =>
            {
                if (GuiDialogSailingOrders.Current != null) { GuiDialogSailingOrders.Current.TryClose(); return true; }
                EntityPlayer me = api.World.Player?.Entity;
                if (me == null) return true;
                EntitySailorLocust pick = null;
                Entity boat = me.MountedOn?.MountSupplier?.OnEntity;
                if (boat != null) pick = api.World.GetEntityById(boat.WatchedAttributes.GetLong(EntitySailorLocust.LocustAttr)) as EntitySailorLocust;
                if (pick == null)
                {
                    double best = 14 * 14;
                    foreach (Entity e in api.World.LoadedEntities.Values)
                    {
                        if (!(e is EntitySailorLocust locust) || !e.Alive) continue;
                        double d = e.Pos.SquareDistanceTo(me.Pos.XYZ);
                        if (d < best) { best = d; pick = locust; }
                    }
                }
                if (pick == null) { api.TriggerIngameError(this, "nolocust", T("err-nolocust")); return true; }
                api.Network.SendEntityPacket(pick.EntityId, EntitySailorLocust.PacketRefresh, null);
                new GuiDialogSailingOrders(api, pick).TryOpen();
                return true;
            });
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            try
            {
                SailorConfig loaded = api.LoadModConfig<SailorConfig>("sailorlocust.json");
                if (loaded != null) Config = loaded;
                api.StoreModConfig(Config, "sailorlocust.json");
            }
            catch (Exception e) { api.Logger.Warning("[sailorlocust] config not read, defaults used: " + e.Message); }

            Markers = new MarkerRegistry(api);
            Ocean = new OceanGrid(api);
            Fleet = new OffscreenFleet(this);
            Berths = new Berths(api);
            Routes = new RouteShare(this);
            api.Event.SaveGameLoaded += () => { Markers.Load(); Fleet.Load(); Routes.Load(); };
            api.Event.GameWorldSave += () => { Markers.Save(); Fleet.Save(); Routes.Save(); };
            api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, () =>
            {
                if (!Config.EnableRecipes)
                {
                    int removed = api.World.GridRecipes.RemoveAll(r => r?.Name != null && r.Name.Domain == Domain);
                    api.Logger.Notification("[sailorlocust] recipes off by config: " + removed + " removed");
                }
                Ocean.Init();
            });
            Fleet.Start();
            Routes.Start();
            Watch = new FleetWatch(this);
            Watch.Start(Routes.Net);
            api.Event.OnPlayerInteractEntity += OnInteractBoat;
            SailorCommands.Register(this);
            SelfTest.MaybeStart(this);
        }

        /// <summary>
        /// The locust sits inside the boat, behind the box of its seat: a click meant for it lands on that box. So a
        /// right click on the seat the locust sits in opens its sailing orders, and with sneak takes the locust back.
        /// </summary>
        void OnInteractBoat(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
        {
            if (mode != (int)EnumInteractMode.Interact || entity == null || entity is EntityAgent) return;
            long locustId = entity.WatchedAttributes.GetLong(EntitySailorLocust.LocustAttr);
            if (locustId == 0) return;
            var locust = sapi.World.GetEntityById(locustId) as EntitySailorLocust;
            var player = byPlayer as IServerPlayer;
            if (locust == null || !locust.Alive || player?.Entity == null) return;
            IMountableSeat seat = locust.PerchSeat(entity);
            if (seat?.Config == null) return;
            var boxes = entity.GetBehavior<EntityBehaviorSelectionBoxes>();
            int index = player.Entity.EntitySelection?.SelectionBoxIndex ?? 0;
            if (boxes?.selectionBoxes == null || index <= 0 || index - 1 >= boxes.selectionBoxes.Length) return;
            if (boxes.selectionBoxes[index - 1]?.AttachPoint?.Code != seat.Config.APName) return;
            handling = EnumHandling.PreventDefault;
            if (!locust.MayCommand(player)) return;
            if (player.Entity.Controls.Sneak) { locust.DropAsItem(player); return; }
            locust.UpdateKnown();
            sapi.Network.SendEntityPacket(player, locust.EntityId, EntitySailorLocust.PacketOpen, null);
        }

        public static string T(string key, params object[] args) { return Lang.Get(Domain + ":" + key, args); }

        /// <summary>Puts a new sailor locust at the helm of that boat. Returns the reason it could not, as a lang key, or null.</summary>
        public string Install(Entity boat, string ownerUid, out EntitySailorLocust locust, string shipId = null)
        {
            locust = null;
            if (boat == null || boat is EntityAgent) return "err-notvessel";
            EntityBehaviorSeatable seatable = boat.GetBehavior<EntityBehaviorSeatable>();
            if (seatable == null || seatable.Seats == null) return "err-notvessel";
            if (BoatDrive.For(boat) == null) return "err-notvessel";
            if (sapi.World.GetEntityById(boat.WatchedAttributes.GetLong(EntitySailorLocust.LocustAttr)) is EntitySailorLocust there && there.Alive) return "err-hasone";
            IMountableSeat free = null;
            foreach (IMountableSeat seat in seatable.Seats)
            {
                if (seat.Config == null || !seat.Config.Controllable) continue;
                if (free == null || (free.Passenger != null && seat.Passenger == null)) free = seat;
            }
            if (free == null) return "err-notvessel";

            EntityProperties type = sapi.World.GetEntityType(new AssetLocation(Domain, "sailorlocust"));
            if (type == null) return "err-notvessel";
            var e = (EntitySailorLocust)sapi.World.ClassRegistry.CreateEntity(type);
            e.Pos.SetPos(boat.Pos.X, boat.Pos.Y + 0.5, boat.Pos.Z);
            e.Pos.Yaw = boat.Pos.Yaw;
            e.WatchedAttributes.SetString("ownerUid", ownerUid ?? "");
            sapi.World.SpawnEntity(e);
            e.Attach(boat, free);
            Fleet.Enlist(boat, e, shipId);
            locust = e;
            return null;
        }
    }

    public class ItemSailorLocust : Item
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            Entity target = entitySel?.Entity;
            if (target == null || target is EntityAgent || target.GetBehavior<EntityBehaviorSeatable>() == null)
            {
                base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
                return;
            }
            // handled here on both sides, so the player is not seated in the boat instead
            handling = EnumHandHandling.PreventDefault;
            if (api.Side != EnumAppSide.Server || !firstEvent) return;
            var player = (byEntity as EntityPlayer)?.Player as IServerPlayer;
            var system = api.ModLoader.GetModSystem<SailorLocustSystem>();
            string error = system.Install(target, player?.PlayerUID, out _);
            if (error != null)
            {
                player?.SendIngameError(error, SailorLocustSystem.T(error));
                return;
            }
            if (player == null || player.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                slot.TakeOut(1);
                slot.MarkDirty();
            }
            player?.SendMessage(GlobalConstants.InfoLogChatGroup, SailorLocustSystem.T("msg-installed"), EnumChatType.Notification);
        }
    }

    public class BlockSailorMarker : Block
    {
        public override bool DoPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ItemStack byItemStack)
        {
            if (!base.DoPlaceBlock(world, byPlayer, blockSel, byItemStack)) return false;
            // a dock post looks the way of whoever places it: that is where its berth is
            if (world.Side == EnumAppSide.Server && byPlayer?.Entity != null)
            {
                var be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntitySailorMarker;
                if (be != null && be.IsDock) be.SetFacing(Docking.FacingOf(byPlayer.Entity));
            }
            return true;
        }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world.Side == EnumAppSide.Client)
            {
                (world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntitySailorMarker)?.OpenDialog();
            }
            return true;
        }
    }

    public class BlockEntitySailorMarker : BlockEntity
    {
        public const int PacketRename = 1001, PacketTurn = 1002, PacketSmall = 1003, PacketRole = 1004;
        /// <summary>For a buoy: Marker.Role.</summary>
        public int Role;
        public string MarkerName = "";
        /// <summary>Kept for small boats only (Marker.SmallOnly).</summary>
        public bool SmallOnly;
        /// <summary>The way the dock looks, 0 to 7 (Docking); -1 until said.</summary>
        public int Facing = -1;

        public void SetFacing(int facing)
        {
            Facing = facing;
            Registry?.Set(Pos, MarkerName, IsDock, Facing, SmallOnly, Role);
            MarkDirty(true);
        }

        /// <summary>Told by the server: a dock has this buoy's name (shown so the player knows the buoy is not a stop).</summary>
        public bool SharesPortName;

        public bool IsDock { get { return Block?.Attributes == null || Block.Attributes["markerKind"].AsString("dock") == "dock"; } }

        MarkerRegistry Registry { get { return (Api as ICoreServerAPI)?.ModLoader.GetModSystem<SailorLocustSystem>()?.Markers; } }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            // the savegame's list is the reference; a loaded block puts itself back if the list lost it
            if (api.Side == EnumAppSide.Server && !string.IsNullOrEmpty(MarkerName))
            {
                // a way to look found for it while its chunk was away is kept
                Marker known = Registry?.Find(MarkerName, Pos.X, Pos.Z);
                if (Facing < 0 && known != null && known.X == Pos.X && known.Y == Pos.Y && known.Z == Pos.Z) Facing = known.Facing;
                Registry?.Set(Pos, MarkerName, IsDock, Facing, SmallOnly, Role);
            }
        }

        public override void OnBlockPlaced(ItemStack byItemStack = null)
        {
            base.OnBlockPlaced(byItemStack);
            if (Api.Side != EnumAppSide.Server) return;
            if (string.IsNullOrEmpty(MarkerName))
            {
                // a name of its own from the start, so two unnamed docks are never the same stop
                string tag = (Math.Abs(Pos.X * 31 + Pos.Z * 17 + Pos.Y) % 9000 + 1000).ToString();
                MarkerName = IsDock ? "Dock " + tag : "Buoy " + tag;
            }
            Registry?.Set(Pos, MarkerName, IsDock, Facing, SmallOnly, Role);
            MarkDirty(true);
        }

        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();
            if (Api?.Side == EnumAppSide.Server) Registry?.Remove(Pos);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetString("markerName", MarkerName ?? "");
            tree.SetInt("facing", Facing);
            tree.SetBool("smallOnly", SmallOnly);
            tree.SetInt("role", Role);
            if (Api is ICoreServerAPI sapi2 && !IsDock) { Marker port = Registry?.Find(MarkerName, Pos.X, Pos.Z); tree.SetBool("sharesPort", port != null && port.Dock); }
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            MarkerName = tree.GetString("markerName", "");
            Facing = tree.GetInt("facing", -1);
            SmallOnly = tree.GetBool("smallOnly", false);
            Role = tree.GetInt("role", 0);
            SharesPortName = tree.GetBool("sharesPort", false);
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            dsc.AppendLine(SailorLocustSystem.T(IsDock ? "marker-info-dock" : "marker-info-buoy", MarkerName));
            if (IsDock && SmallOnly) dsc.AppendLine(SailorLocustSystem.T("marker-info-small", (int)Pilot.SmallLength));
            if (!IsDock && Role == Marker.RoleWait) dsc.AppendLine(SailorLocustSystem.T("marker-info-wait", MarkerName));
            if (!IsDock && Role == Marker.RoleExit) dsc.AppendLine(SailorLocustSystem.T("marker-info-exit", MarkerName));
            if (!IsDock && Role == Marker.RoleEntry) dsc.AppendLine(SailorLocustSystem.T("marker-info-entry", MarkerName));
            if (!IsDock && Role == Marker.RoleRoute && Api is ICoreClientAPI && !string.IsNullOrEmpty(MarkerName) && SharesPortName) dsc.AppendLine(SailorLocustSystem.T("marker-info-portname"));
        }

        public static string CleanName(string raw)
        {
            if (raw == null) return "";
            var sb = new StringBuilder();
            foreach (char c in raw)
            {
                if (c == ';' || c == '|' || c == '\n' || c == '\r' || c == '\t') continue;
                sb.Append(c);
                if (sb.Length >= 40) break;
            }
            return sb.ToString().Trim();
        }

        public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
        {
            base.OnReceivedClientPacket(fromPlayer, packetid, data);
            if (packetid == PacketRole)
            {
                if (IsDock || !Api.World.Claims.TryAccess(fromPlayer, Pos, EnumBlockAccessFlags.Use)) return;
                Role = (Role + 1) % 4;
                Registry?.Set(Pos, MarkerName, IsDock, Facing, SmallOnly, Role);
                // the look follows the role: a plain buoy, or a tall beacon, blue for the waiting area, green for the way out
                string code = Role == Marker.RoleWait ? "sailorbeacon-wait" : Role == Marker.RoleExit ? "sailorbeacon-exit" : Role == Marker.RoleEntry ? "sailorbeacon-entry" : "sailorbuoy";
                Block look = Api.World.GetBlock(new AssetLocation(SailorLocustSystem.Domain, code));
                if (look != null && look.Id != 0 && look.Id != Block.Id) Api.World.BlockAccessor.ExchangeBlock(look.Id, Pos);
                MarkDirty(true);
                return;
            }
            if (packetid == PacketSmall)
            {
                if (!IsDock || !Api.World.Claims.TryAccess(fromPlayer, Pos, EnumBlockAccessFlags.Use)) return;
                SmallOnly = !SmallOnly;
                Registry?.Set(Pos, MarkerName, IsDock, Facing, SmallOnly, Role);
                MarkDirty(true);
                return;
            }
            if (packetid == PacketTurn)
            {
                if (!IsDock || !Api.World.Claims.TryAccess(fromPlayer, Pos, EnumBlockAccessFlags.Use)) return;
                SetFacing((Math.Max(Facing, -1) + 1) % 8);
                return;
            }
            if (packetid != PacketRename || data == null) return;
            if (!Api.World.Claims.TryAccess(fromPlayer, Pos, EnumBlockAccessFlags.Use)) return;
            string name = CleanName(Encoding.UTF8.GetString(data));
            if (name.Length == 0) return;
            MarkerName = name;
            Registry?.Set(Pos, MarkerName, IsDock, Facing, SmallOnly, Role);
            MarkDirty(true);
        }

        public void OpenDialog()
        {
            if (Api is ICoreClientAPI capi) new GuiDialogMarkerName(capi, Pos, MarkerName, IsDock, SmallOnly, Role).TryOpen();
        }
    }

    public static class SailorCommands
    {
        public static void Register(SailorLocustSystem system)
        {
            ICoreServerAPI api = system.sapi;
            api.ChatCommands.Create("sailor")
                .WithDescription("Sailor Locust: docks, buoys and boats under way")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("list")
                    .WithDescription("Lists the docks and buoys of the world")
                    .HandleWith(args =>
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine(system.Markers.Count + " docks and buoys:");
                        foreach (Marker m in system.Markers.All) sb.AppendLine((m.Dock ? "dock " : "buoy ") + "\"" + m.Name + "\" at " + m.X + " " + m.Y + " " + m.Z);
                        return TextCommandResult.Success(sb.ToString());
                    })
                .EndSubCommand()
                .BeginSubCommand("fleet")
                    .WithDescription("Lists every boat under orders: where it is, whose it is, where it goes (also on the world map, layer \"Sailor Locust: boats\")")
                    .HandleWith(args =>
                    {
                        List<ShipView> all = system.Watch.All();
                        var sb = new StringBuilder();
                        sb.AppendLine(all.Count + " boat(s) under orders:");
                        foreach (ShipView v in all) sb.AppendLine(v.Title + " at " + (int)v.X + " " + (int)v.Z + ", " + v.Detail);
                        return TextCommandResult.Success(sb.ToString());
                    })
                .EndSubCommand()
                .BeginSubCommand("route")
                    .WithDescription("Works out the long route between two docks or buoys and tells its length")
                    .WithArgs(api.ChatCommands.Parsers.Word("from"), api.ChatCommands.Parsers.Word("to"))
                    .HandleWith(args =>
                    {
                        Marker a = system.Markers.Find((string)args[0], 0, 0), b = system.Markers.Find((string)args[1], 0, 0);
                        if (a == null || b == null) return TextCommandResult.Error("Unknown dock or buoy.");
                        List<Vec2d> route = system.Ocean.FindRoute(a.X, a.Z, b.X, b.Z, out string how);
                        return TextCommandResult.Success(how + ": " + route.Count + " points, " + (int)OceanGrid.Length(route) + " blocks (straight line " + (int)Math.Sqrt((a.X - b.X) * (double)(a.X - b.X) + (a.Z - b.Z) * (double)(a.Z - b.Z)) + ")");
                    })
                .EndSubCommand();
        }
    }
}
