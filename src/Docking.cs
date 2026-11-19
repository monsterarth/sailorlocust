using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SailorLocust
{
    /// <summary>
    /// Where a boat lies at a dock. A dock post looks one of eight ways, towards its water: the berth is the stretch of
    /// water in front of it, and the boat lies there with its side to the post, across the way the post looks. A post
    /// at the head of a pier looking out makes a T with the pier; a post on the side of a pier looking sideways has
    /// the boat alongside. One rule for both.
    /// </summary>
    public static class Docking
    {
        /// <summary>Blocks from the post to the middle of the berth.</summary>
        public const double Offset = 4;
        /// <summary>The berth as drawn: blocks along the boat and across it.</summary>
        public const int Length = 15, Width = 5;

        /// <summary>The way a post of that facing looks, as a step in x and z (not of length 1 on the diagonals).</summary>
        public static void Dir(int facing, out double dx, out double dz)
        {
            double a = ((facing % 8) + 8) % 8 * Math.PI / 4;
            dx = Math.Sin(a); dz = Math.Cos(a);
        }

        /// <summary>The facing nearest to the way that entity looks.</summary>
        public static int FacingOf(Entity looker)
        {
            Vec3f view = looker.Pos.GetViewVector();
            return FacingOf(view.X, view.Z);
        }

        public static int FacingOf(double x, double z)
        {
            int i = (int)Math.Round(Math.Atan2(x, z) / (Math.PI / 4));
            return ((i % 8) + 8) % 8;
        }

        /// <summary>The middle of the berth of a post at that block.</summary>
        public static void BerthPoint(int x, int z, int facing, out double bx, out double bz)
        {
            Dir(facing, out double dx, out double dz);
            bx = x + 0.5 + dx * Offset;
            bz = z + 0.5 + dz * Offset;
        }

        /// <summary>
        /// Blocks from the post to where open water begins in front of it: a post stands back on its pier, and the boat
        /// is to lie against the pier's edge, not against the post. A cell is open water when the water in it has
        /// nothing solid in it nor in the two blocks above (a pier's planks are not).
        /// </summary>
        public static double EdgeDistance(int x, int z, int facing, System.Func<int, int, bool> openWater)
        {
            Dir(facing, out double dx, out double dz);
            for (double step = 0.75; step <= 9; step += 0.5)
            {
                int cx = (int)Math.Floor(x + 0.5 + dx * step), cz = (int)Math.Floor(z + 0.5 + dz * step);
                if (cx == x && cz == z) continue;
                if (openWater(cx, cz)) return Math.Max(0.5, step - 0.35);
            }
            return 1;
        }

        /// <summary>Half the length and half the width of a boat, from its own collision boxes (the long way is the length).</summary>
        public static void BoatSize(Entity boat, out double halfLength, out double halfWidth)
        {
            halfLength = 1.5; halfWidth = 1;
            if (boat.CollisionBox != null) { halfLength = halfWidth = Math.Max(0.5, (boat.CollisionBox.X2 - boat.CollisionBox.X1) / 2); }
            try
            {
                foreach (EntityBehavior behavior in boat.SidedProperties?.Behaviors ?? new List<EntityBehavior>())
                {
                    var field = behavior.GetType().GetField("OrigCollisionBoxes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (!(field?.GetValue(behavior) is Cuboidf[] boxes) || boxes.Length == 0) continue;
                    double maxX = 0, maxZ = 0;
                    foreach (Cuboidf box in boxes) { maxX = Math.Max(maxX, Math.Max(Math.Abs(box.X1), Math.Abs(box.X2))); maxZ = Math.Max(maxZ, Math.Max(Math.Abs(box.Z1), Math.Abs(box.Z2))); }
                    halfLength = Math.Max(maxX, maxZ); halfWidth = Math.Min(maxX, maxZ);
                    break;
                }
            }
            catch { }
            halfWidth = Math.Max(0.6, halfWidth);
            halfLength = Math.Max(halfWidth, halfLength);
        }

        /// <summary>The middle of the berth of a boat of that half width: its side half a block off the edge of the water.</summary>
        public static void BerthFor(int x, int z, int facing, double edge, double halfWidth, out double bx, out double bz)
        {
            Dir(facing, out double dx, out double dz);
            double len = Math.Sqrt(dx * dx + dz * dz);
            dx /= len; dz /= len;
            double out_ = edge + halfWidth + 0.45;
            bx = x + 0.5 + dx * out_;
            bz = z + 0.5 + dz * out_;
        }

        /// <summary>The yaw nearest to that one at which the boat lies along that line (either end forward).</summary>
        public static float YawAlong(double ax, double az, float near)
        {
            double len = Math.Sqrt(ax * ax + az * az);
            if (len < 1e-6) return near;
            ax /= len; az /= len;
            float best = near; double bestScore = double.MaxValue;
            for (int i = 0; i < 720; i++)
            {
                float yaw = (float)(i * Math.PI / 360);
                Vec3f view = EntityPos.GetViewVector(0, yaw);
                double vlen = Math.Sqrt(view.X * view.X + view.Z * view.Z);
                if (vlen < 1e-6) continue;
                double cross = Math.Abs((view.X * az - view.Z * ax) / vlen);
                if (cross > 0.02) continue;
                double turn = Math.Abs(GameMath.AngleRadDistance(near, yaw));
                if (turn < bestScore) { bestScore = turn; best = yaw; }
            }
            return best;
        }

        /// <summary>
        /// For a post that was given no way to look (placed before posts had one): the way with the most open water in
        /// front of it. -1 when its chunk is not loaded or there is no water near.
        /// </summary>
        public static int AutoFacing(LocalNav nav, Marker dock)
        {
            if (!nav.Loaded(dock.X, dock.Y, dock.Z)) return -1;
            int best = -1, bestCount = 0;
            for (int facing = 0; facing < 8; facing++)
            {
                Dir(facing, out double dx, out double dz);
                int count = 0;
                for (int step = 2; step <= 9; step++)
                {
                    for (int side = -2; side <= 2; side++)
                    {
                        int x = (int)Math.Floor(dock.X + 0.5 + dx * step - dz * side), z = (int)Math.Floor(dock.Z + 0.5 + dz * step + dx * side);
                        int waterY = nav.FindWaterY(x, dock.Y - 1, z);
                        if (waterY >= 0 && nav.IsWater(x, waterY, z)) count++;
                    }
                }
                // the straight ways before the diagonal ones when they are as good
                if (count > bestCount + (facing % 2 == 1 ? 2 : 0)) { bestCount = count; best = facing; }
            }
            return bestCount >= 12 ? best : -1;
        }
    }

    /// <summary>
    /// The help for placing a dock, in the player's game: with a dock post in hand, or looking at one that is placed, the
    /// berth is drawn on the water. Green where there is water, red where something is in the way, and all orange when
    /// another dock stands nearer than the 16 blocks two berths need.
    /// </summary>
    public class BerthPreview
    {
        const int Slot = 5231;
        readonly ICoreClientAPI capi;
        string shown = "";

        public BerthPreview(ICoreClientAPI capi)
        {
            this.capi = capi;
            capi.Event.RegisterGameTickListener(Tick, 200);
        }

        void Clear()
        {
            if (shown.Length == 0) return;
            shown = "";
            capi.World.HighlightBlocks(capi.World.Player, Slot, new List<BlockPos>(), new List<int>());
        }

        void Tick(float dt)
        {
            try { Draw(); }
            catch (Exception) { Clear(); }
        }

        void Draw()
        {
            IClientPlayer player = capi.World.Player;
            BlockSelection sel = player?.CurrentBlockSelection;
            if (player?.Entity == null || sel == null) { Clear(); return; }
            BlockPos post; int facing;
            var placed = capi.World.BlockAccessor.GetBlockEntity(sel.Position) as BlockEntitySailorMarker;
            if (placed != null && placed.IsDock)
            {
                if (placed.Facing < 0) { Clear(); return; }
                post = sel.Position; facing = placed.Facing;
            }
            else
            {
                ItemStack held = player.InventoryManager?.ActiveHotbarSlot?.Itemstack;
                Block block = held?.Block;
                if (block == null || !(block is BlockSailorMarker) || block.Attributes == null || block.Attributes["markerKind"].AsString("dock") != "dock") { Clear(); return; }
                // where the post would go, looking the way the player looks
                Block aimed = capi.World.BlockAccessor.GetBlock(sel.Position);
                post = aimed != null && aimed.IsReplacableBy(block) ? sel.Position.Copy() : sel.Position.AddCopy(sel.Face);
                facing = Docking.FacingOf(player.Entity);
            }
            string key = post.X + "," + post.Y + "," + post.Z + "," + facing;
            if (key == shown) return;
            shown = key;

            Docking.Dir(facing, out double dx, out double dz);
            double dlen = Math.Sqrt(dx * dx + dz * dz); dx /= dlen; dz /= dlen;
            double edge = Docking.EdgeDistance(post.X, post.Z, facing, (x, z) => { int wy = WaterY(x, post.Y, z); return wy >= 0 && Water(x, wy, z); });
            // drawn from the edge of the water out: boats lie against its near side
            double bx = post.X + 0.5 + dx * (edge + Docking.Width / 2.0), bz = post.Z + 0.5 + dz * (edge + Docking.Width / 2.0);
            int waterY = WaterY((int)Math.Floor(bx), post.Y, (int)Math.Floor(bz));
            bool crowded = OtherDockNear(post);
            var cells = new List<BlockPos>(); var colors = new List<int>(); var seen = new HashSet<long>();
            int green = ColorUtil.ToRgba(90, 60, 200, 80), red = ColorUtil.ToRgba(140, 60, 60, 220), orange = ColorUtil.ToRgba(120, 40, 150, 230), mid = ColorUtil.ToRgba(140, 60, 220, 230);
            for (int along = -Docking.Length / 2; along <= Docking.Length / 2; along++)
            {
                for (int across = -Docking.Width / 2; across <= Docking.Width / 2; across++)
                {
                    int x = (int)Math.Floor(bx - dz * along + dx * across + 0.0001), z = (int)Math.Floor(bz + dx * along + dz * across + 0.0001);
                    if (!seen.Add(((long)x << 32) | (uint)z)) continue;
                    int y = waterY >= 0 ? waterY : post.Y - 1;
                    bool water = waterY >= 0 && Water(x, y, z);
                    cells.Add(new BlockPos(x, y, z, post.dimension));
                    colors.Add(!water ? red : crowded ? orange : (along == 0 && across == 0 ? mid : green));
                }
            }
            // the lanes a boat comes in by, out along the line of the pier on both ends: yellow where open, red where not
            int yellow = ColorUtil.ToRgba(70, 230, 210, 60);
            foreach (int s in new[] { 1, -1 })
            {
                for (int along = Docking.Length / 2 + 1; along <= Docking.Length / 2 + 22; along++)
                {
                    for (int across = -Docking.Width / 2; across <= Docking.Width / 2; across++)
                    {
                        int x = (int)Math.Floor(bx - dz * along * s + dx * across + 0.0001), z = (int)Math.Floor(bz + dx * along * s + dz * across + 0.0001);
                        if (!seen.Add(((long)x << 32) | (uint)z)) continue;
                        int y = waterY >= 0 ? waterY : post.Y - 1;
                        bool water = waterY >= 0 && Water(x, y, z);
                        cells.Add(new BlockPos(x, y, z, post.dimension));
                        colors.Add(water ? yellow : red);
                    }
                }
            }
            capi.World.HighlightBlocks(player, Slot, cells, colors);
        }

        int WaterY(int x, int top, int z)
        {
            var pos = new BlockPos(x, top, z, 0);
            for (int y = top; y >= top - 6; y--)
            {
                pos.Y = y;
                Block fluid = capi.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
                if (fluid != null && fluid.Id != 0 && fluid.IsLiquid()) return y;
            }
            return -1;
        }

        bool Water(int x, int y, int z)
        {
            var pos = new BlockPos(x, y, z, 0);
            IBlockAccessor ba = capi.World.BlockAccessor;
            Block fluid = ba.GetBlock(pos, BlockLayersAccess.Fluid);
            if (fluid == null || fluid.Id == 0 || !fluid.IsLiquid()) return false;
            for (int up = 0; up <= 2; up++)
            {
                pos.Y = y + up;
                Block solid = ba.GetBlock(pos, BlockLayersAccess.Solid);
                if (solid != null && solid.Id != 0 && solid.CollisionBoxes != null && solid.CollisionBoxes.Length > 0) return false;
            }
            return true;
        }

        bool OtherDockNear(BlockPos post)
        {
            IBlockAccessor ba = capi.World.BlockAccessor;
            for (int cx = -1; cx <= 1; cx++)
            {
                for (int cz = -1; cz <= 1; cz++)
                {
                    for (int cy = -1; cy <= 1; cy++)
                    {
                        IWorldChunk chunk = ba.GetChunkAtBlockPos(new BlockPos(post.X + cx * 32, post.Y + cy * 32, post.Z + cz * 32, post.dimension));
                        if (chunk?.BlockEntities == null) continue;
                        foreach (var pair in chunk.BlockEntities)
                        {
                            var other = pair.Value as BlockEntitySailorMarker;
                            if (other == null || !other.IsDock || other.Pos.Equals(post)) continue;
                            double ox = other.Pos.X - post.X, oz = other.Pos.Z - post.Z;
                            if (ox * ox + oz * oz < 16 * 16) return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
