using System;
using System.Collections.Generic;
using System.Text;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SailorLocust
{
    /// <summary>Every boat under orders, for the admins' map: where it is and a line about it.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class FleetPacket
    {
        public List<double> Xs = new List<double>();
        public List<double> Zs = new List<double>();
        public List<string> Titles = new List<string>();
        public List<string> Details = new List<string>();
        /// <summary>Sailing out of sight (by time, not in the world) rather than on the water.</summary>
        public List<bool> Unseen = new List<bool>();
    }

    /// <summary>One boat under orders, on the water or out of sight, as the admin tools show it.</summary>
    public class ShipView
    {
        public double X, Z;
        public string Boat, Owner, Next, Status;
        public bool Unseen;

        public string Title { get { return Boat + (string.IsNullOrEmpty(Owner) ? "" : " (" + Owner + ")"); } }
        public string Detail { get { return (Unseen ? "out of sight" : "on the water") + ", to " + (Next ?? "?") + (string.IsNullOrEmpty(Status) ? "" : ": " + Status); } }
    }

    /// <summary>
    /// Where the boats under orders are, for the server's admins (those who may run /sailor): the list for the
    /// command, and a packet every few seconds to their game, which draws the boats on its world map.
    /// </summary>
    public class FleetWatch
    {
        readonly SailorLocustSystem system;
        readonly ICoreServerAPI sapi;
        IServerNetworkChannel channel;
        public const double EverySeconds = 4;

        public FleetWatch(SailorLocustSystem system) { this.system = system; sapi = system.sapi; }

        public void Start(IServerNetworkChannel channel)
        {
            this.channel = channel;
            sapi.Event.RegisterGameTickListener(_ => Send(), (int)(EverySeconds * 1000), 2000);
        }

        public static bool IsAdmin(IServerPlayer player) { return player != null && player.HasPrivilege(Privilege.controlserver); }

        public List<ShipView> All()
        {
            var list = new List<ShipView>();
            foreach (Entity e in sapi.World.LoadedEntities.Values)
            {
                if (!(e is EntitySailorLocust locust) || !e.Alive || locust.Pilot == null) continue;
                Entity boat = locust.Boat;
                if (boat == null || !locust.Pilot.Running || locust.Pilot.Stops.Count == 0) continue;
                list.Add(new ShipView
                {
                    X = boat.Pos.X, Z = boat.Pos.Z, Boat = boat.GetName(),
                    Owner = OwnerName(locust.WatchedAttributes.GetString("ownerUid", "")),
                    Next = locust.Pilot.CurrentStop?.Name,
                    Status = StatusText(locust.WatchedAttributes.GetString("status", ""))
                });
            }
            foreach (VirtualShip ship in system.Fleet.Ships)
            {
                List<Stop> stops = Orders.Parse(ship.OrdersText);
                string next = stops.Count == 0 ? null : stops[((ship.StopIndex % stops.Count) + stops.Count) % stops.Count].Name;
                list.Add(new ShipView { X = ship.X, Z = ship.Z, Boat = ship.BoatCode.Substring(ship.BoatCode.IndexOf(':') + 1), Owner = OwnerName(ship.OwnerUid), Next = next, Status = ship.Status, Unseen = true });
            }
            return list;
        }

        string OwnerName(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            return sapi.PlayerData.GetPlayerDataByUid(uid)?.LastKnownPlayername ?? uid;
        }

        static string StatusText(string status)
        {
            if (string.IsNullOrEmpty(status)) return "";
            string[] parts = status.Split('|');
            var args = new object[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++) args[i - 1] = parts[i];
            return SailorLocustSystem.T(parts[0], args);
        }

        void Send()
        {
            if (channel == null) return;
            List<IServerPlayer> admins = null;
            foreach (IPlayer p in sapi.World.AllOnlinePlayers) if (p is IServerPlayer sp && IsAdmin(sp)) (admins ?? (admins = new List<IServerPlayer>())).Add(sp);
            if (admins == null) return;
            var packet = new FleetPacket();
            foreach (ShipView v in All())
            {
                packet.Xs.Add(v.X); packet.Zs.Add(v.Z); packet.Titles.Add(v.Title); packet.Details.Add(v.Detail); packet.Unseen.Add(v.Unseen);
            }
            channel.SendPacket(packet, admins.ToArray());
        }
    }

    /// <summary>The boats under orders on the game's world map, for an admin: the server sends them, this draws them.</summary>
    public class FleetMapLayer : MapLayer
    {
        public const string Code = "sailorlocust-fleet";
        const string Icon = "sailorlocust:textures/icons/boat.svg";
        static readonly int OnWater = unchecked((int)0xFFE08A2A), OutOfSight = unchecked((int)0xFF8FA6B8);
        /// <summary>The last list from the server (the client keeps one).</summary>
        public static FleetPacket Latest;

        readonly ICoreClientAPI capi;
        readonly Dictionary<string, LoadedTexture> icons = new Dictionary<string, LoadedTexture>();
        readonly Vec3d world = new Vec3d();
        Vec2f view = new Vec2f();
        bool failed;

        public FleetMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
        {
            capi = api as ICoreClientAPI;
            ZIndex = 3;
        }

        public override string Title { get { return "Sailor Locust: boats"; } }
        public override string LayerGroupCode { get { return Code; } }
        public override EnumMapAppSide DataSide { get { return EnumMapAppSide.Client; } }

        public static void Register(ICoreClientAPI capi)
        {
            try
            {
                WorldMapManager manager = capi.ModLoader.GetModSystem<WorldMapManager>();
                manager?.RegisterMapLayer<FleetMapLayer>(Code, 0.8);
            }
            catch (Exception e) { capi.Logger.Warning("[sailorlocust] the boats could not be put on the world map: " + e.Message); }
        }

        static float Px() { return (float)Math.Ceiling(16 * RuntimeEnv.GUIScale); }

        LoadedTexture Texture(int color, int px)
        {
            string key = color + "|" + px;
            if (icons.TryGetValue(key, out LoadedTexture t)) return t;
            try { t = capi.Gui.LoadSvgWithPadding(new AssetLocation(Icon), px, px, 1, color); }
            catch (Exception e) { capi.Logger.Warning("[sailorlocust] boat icon not read: " + e.Message); t = null; }
            icons[key] = t;
            return t;
        }

        public override void Render(GuiElementMap mapElem, float dt)
        {
            FleetPacket p = Latest;
            if (failed || !Active || capi == null || p == null || p.Xs.Count == 0) return;
            try
            {
                float px = Px();
                double left = mapElem.Bounds.renderX, top = mapElem.Bounds.renderY;
                capi.Render.PushScissor(mapElem.Bounds, true);
                try
                {
                    for (int i = 0; i < p.Xs.Count && i < p.Zs.Count; i++)
                    {
                        bool unseen = i < p.Unseen.Count && p.Unseen[i];
                        LoadedTexture t = Texture(unseen ? OutOfSight : OnWater, (int)px);
                        if (t == null) continue;
                        world.Set(p.Xs[i], 0, p.Zs[i]);
                        mapElem.TranslateWorldPosToViewPos(world, ref view);
                        capi.Render.Render2DTexturePremultipliedAlpha(t.TextureId, (float)(left + view.X - px / 2), (float)(top + view.Y - px / 2), px, px, 60, null);
                    }
                }
                finally { capi.Render.PopScissor(); }
            }
            catch (Exception e)
            {
                failed = true;
                capi.Logger.Error("[sailorlocust] drawing the boats on the world map failed, the layer stops: " + e);
            }
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            FleetPacket p = Latest;
            if (failed || !Active || p == null) return;
            float half = Px() / 2 + 2;
            double mx = args.X - mapElem.Bounds.renderX, my = args.Y - mapElem.Bounds.renderY;
            for (int i = 0; i < p.Xs.Count && i < p.Zs.Count; i++)
            {
                world.Set(p.Xs[i], 0, p.Zs[i]);
                mapElem.TranslateWorldPosToViewPos(world, ref view);
                if (Math.Abs(view.X - mx) > half || Math.Abs(view.Y - my) > half) continue;
                if (i < p.Titles.Count) hoverText.AppendLine(p.Titles[i]);
                if (i < p.Details.Count) hoverText.AppendLine(p.Details[i]);
            }
        }

        public override void Dispose()
        {
            foreach (LoadedTexture t in icons.Values) t?.Dispose();
            icons.Clear();
            base.Dispose();
        }
    }
}
