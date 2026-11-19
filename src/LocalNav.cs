using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SailorLocust
{
    /// <summary>
    /// The boat's eyes: the real blocks around it, 81 x 81 at the water's level, and the way through them. One block for
    /// each cell. A cell is water when it has liquid, nothing solid in it and two blocks of room above. Cells of chunks
    /// not loaded count as closed, and are told apart so the pilot can wait for them rather than call itself stuck.
    /// </summary>
    public class LocalNav
    {
        public const int R = 40;
        public const int N = 2 * R + 1;
        const int MaxDist = 7;

        readonly ICoreServerAPI sapi;
        readonly IBlockAccessor ba;
        readonly BlockPos tmp = new BlockPos(0, 0, 0, 0);
        readonly byte[] dist = new byte[N * N];       // 0 closed, else blocks to the nearest closed cell (capped)
        readonly float[] g = new float[N * N];
        readonly int[] parent = new int[N * N];
        readonly bool[] closed = new bool[N * N];
        readonly int[] queue = new int[N * N];
        readonly PriorityQueue<int, float> open = new PriorityQueue<int, float>();
        bool[] solidKnown = new bool[0];
        bool[] solid = new bool[0];

        public int OriginX, OriginZ, WaterY;
        /// <summary>Cells of the last scan that lie in chunks not loaded.</summary>
        public int UnknownCells;
        /// <summary>Other boats for the next scan, as {x, z, axis x, axis z, half length, half width}: their water reads as closed.</summary>
        public List<double[]> Obstacles = new List<double[]>();

        public LocalNav(ICoreServerAPI sapi) { this.sapi = sapi; ba = sapi.World.BlockAccessor; }

        bool Solid(Block block)
        {
            int id = block.Id;
            if (id == 0) return false;
            if (id >= solid.Length)
            {
                int size = Math.Max(id + 1, sapi.World.Blocks.Count);
                Array.Resize(ref solid, size);
                Array.Resize(ref solidKnown, size);
            }
            if (!solidKnown[id])
            {
                Cuboidf[] boxes = block.CollisionBoxes;
                solid[id] = boxes != null && boxes.Length > 0;
                solidKnown[id] = true;
            }
            return solid[id];
        }

        public bool IsWater(int x, int y, int z)
        {
            tmp.Set(x, y, z);
            Block fluid = ba.GetBlock(tmp, BlockLayersAccess.Fluid);
            if (fluid == null || fluid.Id == 0 || !fluid.IsLiquid() || fluid.LiquidCode == "lava") return false;
            if (Solid(ba.GetBlock(tmp, BlockLayersAccess.Solid))) return false;
            tmp.Y = y + 1;
            if (Solid(ba.GetBlock(tmp, BlockLayersAccess.Solid))) return false;
            tmp.Y = y + 2;
            if (Solid(ba.GetBlock(tmp, BlockLayersAccess.Solid))) return false;
            return true;
        }

        public bool Loaded(int x, int y, int z)
        {
            tmp.Set(x, y, z);
            return ba.GetChunkAtBlockPos(tmp) != null;
        }

        /// <summary>The level of the water a boat floats on at that point: the highest block with liquid from a little above the boat downwards. -1 when none.</summary>
        public int FindWaterY(double x, double y, double z)
        {
            int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
            for (int by = (int)Math.Floor(y) + 1; by >= (int)Math.Floor(y) - 3; by--)
            {
                tmp.Set(bx, by, bz);
                Block fluid = ba.GetBlock(tmp, BlockLayersAccess.Fluid);
                if (fluid != null && fluid.Id != 0 && fluid.IsLiquid()) return by;
            }
            return -1;
        }

        /// <summary>Looks at the blocks around that point at that level.</summary>
        public void Scan(double centerX, double centerZ, int waterY)
        {
            OriginX = (int)Math.Floor(centerX) - R;
            OriginZ = (int)Math.Floor(centerZ) - R;
            WaterY = waterY;
            UnknownCells = 0;
            int head = 0, tail = 0;
            // chunk columns are told loaded or not once each
            int c0x = OriginX >> 5, c0z = OriginZ >> 5, cw = ((OriginX + N - 1) >> 5) - c0x + 1, ch = ((OriginZ + N - 1) >> 5) - c0z + 1;
            Span<bool> loaded = stackalloc bool[cw * ch];
            for (int cz = 0; cz < ch; cz++) for (int cx = 0; cx < cw; cx++) loaded[cz * cw + cx] = Loaded(((c0x + cx) << 5) + 1, waterY, ((c0z + cz) << 5) + 1);

            for (int z = 0; z < N; z++)
            {
                for (int x = 0; x < N; x++)
                {
                    int wx = OriginX + x, wz = OriginZ + z, i = z * N + x;
                    bool known = loaded[((wz >> 5) - c0z) * cw + ((wx >> 5) - c0x)];
                    if (!known) UnknownCells++;
                    if (known && IsWater(wx, waterY, wz)) dist[i] = MaxDist;
                    else { dist[i] = 0; queue[tail++] = i; }
                }
            }
            // the hulls of the other boats near are closed water too, with a little room around them
            foreach (double[] o in Obstacles)
            {
                double ox = o[0], oz = o[1], ax = o[2], az = o[3], hl = o[4], hw = o[5];
                double ext = hl + hw + 1;
                int x0 = Math.Max(0, (int)Math.Floor(ox - ext) - OriginX), x1 = Math.Min(N - 1, (int)Math.Floor(ox + ext) - OriginX);
                int z0 = Math.Max(0, (int)Math.Floor(oz - ext) - OriginZ), z1 = Math.Min(N - 1, (int)Math.Floor(oz + ext) - OriginZ);
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        double dx = OriginX + x + 0.5 - ox, dz = OriginZ + z + 0.5 - oz;
                        if (Math.Abs(dx * ax + dz * az) > hl || Math.Abs(dx * az - dz * ax) > hw) continue;
                        int i = z * N + x;
                        if (dist[i] != 0) { dist[i] = 0; queue[tail++] = i; }
                    }
                }
            }
            // how far each water cell is from the nearest closed one
            while (head < tail)
            {
                int cur = queue[head++];
                int cx = cur % N, cz = cur / N, d = dist[cur];
                if (d >= MaxDist - 1) continue;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx >= N || nz >= N) continue;
                        int ni = nz * N + nx;
                        if (dist[ni] > d + 1) { dist[ni] = (byte)(d + 1); queue[tail++] = ni; }
                    }
                }
            }
        }

        /// <summary>Blocks of clear water around that point in the last scan (0: not water; capped).</summary>
        public int Clearance(double x, double z)
        {
            int cx = (int)Math.Floor(x) - OriginX, cz = (int)Math.Floor(z) - OriginZ;
            if (cx < 0 || cz < 0 || cx >= N || cz >= N) return 0;
            return dist[cz * N + cx];
        }

        /// <summary>
        /// The way over the scanned water from one point to another, as block centres. Keeps hardRadius blocks from
        /// anything solid where it can and prefers softRadius. When the goal cannot be reached, the way ends at the
        /// reachable cell nearest to it and reached is false.
        /// </summary>
        public List<Vec2d> Plan(double fromX, double fromZ, double toX, double toZ, int hardRadius, int softRadius, out bool reached)
        {
            reached = false;
            int sx = GameMath.Clamp((int)Math.Floor(fromX) - OriginX, 0, N - 1), sz = GameMath.Clamp((int)Math.Floor(fromZ) - OriginZ, 0, N - 1);
            int gx = GameMath.Clamp((int)Math.Floor(toX) - OriginX, 1, N - 2), gz = GameMath.Clamp((int)Math.Floor(toZ) - OriginZ, 1, N - 2);
            if (dist[sz * N + sx] == 0)
            {
                // the boat's own cell reads as closed (hard against a shore): start from the nearest water
                bool any = false; int best = int.MaxValue, bx = sx, bz = sz;
                for (int dz = -3; dz <= 3; dz++) for (int dx = -3; dx <= 3; dx++)
                {
                    int nx = sx + dx, nz = sz + dz;
                    if (nx < 0 || nz < 0 || nx >= N || nz >= N || dist[nz * N + nx] == 0) continue;
                    int d = dx * dx + dz * dz;
                    if (d < best) { best = d; bx = nx; bz = nz; any = true; }
                }
                if (!any) return null;
                sx = bx; sz = bz;
            }
            Array.Fill(g, float.MaxValue);
            Array.Fill(parent, -1);
            Array.Clear(closed, 0, closed.Length);
            open.Clear();
            int start = sz * N + sx, goal = gz * N + gx;
            g[start] = 0;
            open.Enqueue(start, 0);
            int bestNode = start; float bestH = Heuristic(sx, sz, gx, gz);
            while (open.Count > 0)
            {
                int cur = open.Dequeue();
                if (closed[cur]) continue;
                closed[cur] = true;
                int cx = cur % N, cz = cur / N;
                float h = Heuristic(cx, cz, gx, gz);
                if (h < bestH) { bestH = h; bestNode = cur; }
                if (cur == goal) { reached = true; break; }
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cx + dx, nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx >= N || nz >= N) continue;
                        int ni = nz * N + nx;
                        int d = dist[ni];
                        if (d == 0 || closed[ni]) continue;
                        if (dx != 0 && dz != 0 && (dist[cz * N + nx] == 0 || dist[nz * N + cx] == 0)) continue;
                        float penalty = d <= hardRadius ? 12f : (d < softRadius ? (softRadius - d) * 0.7f : 0f);
                        float ng = g[cur] + (dx != 0 && dz != 0 ? 1.4142f : 1f) * (1f + penalty);
                        if (ng >= g[ni]) continue;
                        g[ni] = ng;
                        parent[ni] = cur;
                        open.Enqueue(ni, ng + Heuristic(nx, nz, gx, gz));
                    }
                }
            }
            int end = reached ? goal : bestNode;
            var path = new List<Vec2d>();
            for (int n = end; n != -1; n = parent[n]) path.Add(new Vec2d(OriginX + n % N + 0.5, OriginZ + n / N + 0.5));
            path.Reverse();
            return path;
        }

        static float Heuristic(int x, int z, int gx, int gz)
        {
            int dx = Math.Abs(x - gx), dz = Math.Abs(z - gz);
            return Math.Max(dx, dz) + 0.4142f * Math.Min(dx, dz);
        }
    }
}
