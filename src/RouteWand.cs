using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SailorLocust
{
    /// <summary>
    /// A staff tool, found only in the creative inventory: used on a boat with a sailor locust (or on the locust), it
    /// shows on the water, for half a minute, what the locust means to do. The whole round of its orders (the long
    /// routes between its stops), the way through the water it follows now, and the points of its coming in: the
    /// point out on the line of the pier, the head of the berth and the berth itself. Used on nothing, it takes it away.
    /// </summary>
    public class ItemSailorWand : Item
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            handling = EnumHandHandling.PreventDefault;
            if (!firstEvent) return;
            Entity target = entitySel?.Entity;
            if (api is ICoreClientAPI capi)
            {
                if (target == null) RouteView.Clear(capi);
                return;
            }
            var player = (byEntity as EntityPlayer)?.Player as IServerPlayer;
            if (player == null || target == null) return;
            var locust = target as EntitySailorLocust;
            if (locust == null) locust = api.World.GetEntityById(target.WatchedAttributes.GetLong(EntitySailorLocust.LocustAttr)) as EntitySailorLocust;
            if (locust == null || !locust.Alive) { player.SendIngameError("nolocust", SailorLocustSystem.T("err-nolocust")); return; }
            byte[] data = RouteView.Build(api.ModLoader.GetModSystem<SailorLocustSystem>(), locust);
            ((ICoreServerAPI)api).Network.SendEntityPacket(player, locust.EntityId, EntitySailorLocust.PacketRoute, data);
        }
    }

    public static class RouteView
    {
        public const byte Round = 0, Now = 1, Points = 2;
        const int Slot = 5232;
        static long clearAt;

        /// <summary>Server: what to show, as kind, then the points x, z of each line.</summary>
        public static byte[] Build(SailorLocustSystem system, EntitySailorLocust locust)
        {
            var lines = new List<KeyValuePair<byte, List<Vec2d>>>();
            Entity boat = locust.Boat;
            double px = locust.Pos.X, pz = locust.Pos.Z;
            List<Stop> stops = Orders.Parse(locust.WatchedAttributes.GetString("orders", ""));
            var markers = new List<Marker>();
            foreach (Stop stop in stops)
            {
                Marker m = system.Markers.Find(stop.Name, px, pz);
                markers.Add(m);
                if (m != null) { px = m.X; pz = m.Z; }
            }
            for (int i = 0; markers.Count > 1 && i < markers.Count; i++)
            {
                Marker a = markers[i], b = markers[(i + 1) % markers.Count];
                if (a == null || b == null || (a.X == b.X && a.Z == b.Z)) continue;
                lines.Add(new KeyValuePair<byte, List<Vec2d>>(Round, system.Ocean.FindRoute(a.X + 0.5, a.Z + 0.5, b.X + 0.5, b.Z + 0.5, out _)));
            }
            Pilot pilot = locust.Pilot;
            if (pilot != null && boat != null)
            {
                List<Vec2d> now = pilot.CurrentWay(boat);
                if (now != null && now.Count > 1) lines.Add(new KeyValuePair<byte, List<Vec2d>>(Now, now));
                List<Vec2d> points = pilot.BerthPoints();
                if (points != null && points.Count > 0) lines.Add(new KeyValuePair<byte, List<Vec2d>>(Points, points));
            }
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(boat != null ? boat.Pos.Y : locust.Pos.Y);
                writer.Write(lines.Count);
                foreach (var line in lines)
                {
                    writer.Write(line.Key);
                    writer.Write(line.Value.Count);
                    foreach (Vec2d p in line.Value) { writer.Write(p.X); writer.Write(p.Y); }
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>Client: draws it on the water.</summary>
        public static void Show(ICoreClientAPI capi, byte[] data)
        {
            var cells = new List<BlockPos>();
            var colors = new List<int>();
            var seen = new Dictionary<long, int>();
            int round = ColorUtil.ToRgba(110, 230, 200, 60), now = ColorUtil.ToRgba(140, 70, 220, 90), point = ColorUtil.ToRgba(170, 40, 210, 240), berth = ColorUtil.ToRgba(200, 255, 255, 255);
            using (var reader = new BinaryReader(new MemoryStream(data)))
            {
                double y = reader.ReadDouble();
                int level = FindWater(capi, (int)Math.Floor(y));
                int count = reader.ReadInt32();
                for (int l = 0; l < count; l++)
                {
                    byte kind = reader.ReadByte();
                    int n = reader.ReadInt32();
                    var pts = new List<Vec2d>();
                    for (int i = 0; i < n; i++) pts.Add(new Vec2d(reader.ReadDouble(), reader.ReadDouble()));
                    if (kind == Points)
                    {
                        for (int i = 0; i < pts.Count; i++) Mark(cells, colors, seen, (int)Math.Floor(pts[i].X), level, (int)Math.Floor(pts[i].Y), i == pts.Count - 1 ? berth : point, 3, true);
                        continue;
                    }
                    int color = kind == Now ? now : round;
                    for (int i = 1; i < pts.Count; i++)
                    {
                        double dx = pts[i].X - pts[i - 1].X, dz = pts[i].Y - pts[i - 1].Y, len = Math.Sqrt(dx * dx + dz * dz);
                        int steps = Math.Max(1, (int)Math.Ceiling(len));
                        for (int s = 0; s <= steps; s++)
                        {
                            double t = s / (double)steps;
                            Mark(cells, colors, seen, (int)Math.Floor(pts[i - 1].X + dx * t), level, (int)Math.Floor(pts[i - 1].Y + dz * t), color, kind == Now ? 3 : 1, false);
                        }
                    }
                    if (cells.Count > 20000) break;
                }
            }
            capi.World.HighlightBlocks(capi.World.Player, Slot, cells, colors);
            clearAt = capi.World.ElapsedMilliseconds + 30000;
            capi.Event.RegisterCallback(dt => { if (capi.World.ElapsedMilliseconds >= clearAt) Clear(capi); }, 30500);
        }

        static void Mark(List<BlockPos> cells, List<int> colors, Dictionary<long, int> seen, int x, int y, int z, int color, int rank, bool big)
        {
            for (int ox = big ? -1 : 0; ox <= (big ? 1 : 0); ox++)
            {
                for (int oz = big ? -1 : 0; oz <= (big ? 1 : 0); oz++)
                {
                    long key = ((long)(x + ox) << 32) ^ (uint)(z + oz);
                    if (seen.TryGetValue(key, out int at))
                    {
                        // what matters more shows over what matters less: the points over the way now over the round
                        if (rank >= 3) colors[at] = color;
                        continue;
                    }
                    seen[key] = cells.Count;
                    cells.Add(new BlockPos(x + ox, y, z + oz, 0));
                    colors.Add(color);
                }
            }
        }

        static int FindWater(ICoreClientAPI capi, int y)
        {
            BlockPos pos = capi.World.Player.Entity.Pos.AsBlockPos;
            for (int dy = 1; dy >= -3; dy--)
            {
                pos.Y = y + dy;
                Block fluid = capi.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
                if (fluid != null && fluid.IsLiquid()) return y + dy;
            }
            return y;
        }

        public static void Clear(ICoreClientAPI capi)
        {
            capi.World.HighlightBlocks(capi.World.Player, Slot, new List<BlockPos>(), new List<int>());
        }
    }
}
