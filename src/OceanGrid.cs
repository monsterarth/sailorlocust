using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace SailorLocust
{
    /// <summary>
    /// The long route. World generation's ocean map tells, for any point of the world and without a chunk generated, how
    /// much sea there is: one value for each 32 blocks. A* over that grid gives the way around the continents. It does
    /// not see lakes, rivers or islets: the boat's own look at the real blocks (LocalNav) and the buoys cover those.
    /// </summary>
    public class OceanGrid
    {
        const int Tile = 64;
        readonly ICoreServerAPI sapi;
        MapLayerBase gen;
        bool tried;
        readonly Dictionary<long, int[]> tiles = new Dictionary<long, int[]>();

        /// <summary>Blocks for each value of the ocean map.</summary>
        public int Cell { get; private set; }
        public int CellsX { get; private set; }
        public int CellsZ { get; private set; }
        public bool Ready { get { return gen != null; } }

        public OceanGrid(ICoreServerAPI sapi) { this.sapi = sapi; }

        public void Init()
        {
            if (gen != null || tried) return;
            try
            {
                GenMaps maps = sapi.ModLoader.GetModSystem<GenMaps>();
                if (maps == null || maps.oceanGen == null || maps.noiseSizeOcean <= 0) return;   // world generation not started yet: asked again later
                tried = true;
                // a copy of its own: the generator keeps a working state and world generation runs on another thread
                gen = (MapLayerBase)DeepClone(maps.oceanGen, new Dictionary<object, object>(new RefComparer()), 0);
                Cell = sapi.WorldManager.RegionSize / maps.noiseSizeOcean;
                CellsX = sapi.WorldManager.MapSizeX / Cell;
                CellsZ = sapi.WorldManager.MapSizeZ / Cell;
                sapi.Logger.Notification("[sailorlocust] ocean map ready: " + CellsX + " x " + CellsZ + " values, one for each " + Cell + " blocks");
            }
            catch (Exception e)
            {
                gen = null;
                sapi.Logger.Warning("[sailorlocust] no ocean map (" + e.Message + "): boats sail straight from marker to marker, around what they see");
            }
        }

        sealed class RefComparer : IEqualityComparer<object>
        {
            bool IEqualityComparer<object>.Equals(object a, object b) { return ReferenceEquals(a, b); }
            int IEqualityComparer<object>.GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        static object DeepClone(object source, Dictionary<object, object> seen, int depth)
        {
            if (source == null) return null;
            Type type = source.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return source;
            if (typeof(Delegate).IsAssignableFrom(type) || typeof(Type).IsAssignableFrom(type) || type.FullName.StartsWith("System.Reflection")) return source;
            if (depth > 60) throw new InvalidOperationException("generator too deep to copy");
            if (!type.IsValueType && seen.TryGetValue(source, out object done)) return done;
            if (type.IsArray)
            {
                Array from = (Array)source;
                Array to = (Array)from.Clone();
                seen[source] = to;
                Type element = type.GetElementType();
                if (!(element.IsPrimitive || element.IsEnum || element == typeof(string)))
                {
                    if (from.Rank != 1) throw new InvalidOperationException("array of rank " + from.Rank);
                    for (int i = 0; i < from.Length; i++) to.SetValue(DeepClone(from.GetValue(i), seen, depth + 1), i);
                }
                return to;
            }
            // only the game's own types and plain collections are copied field by field; anything else is shared as it is
            string ns = type.Namespace ?? "";
            bool own = ns.StartsWith("Vintagestory") || ns.StartsWith("System.Collections.Generic");
            if (!own) return source;
            object copy = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            if (!type.IsValueType) seen[source] = copy;
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    field.SetValue(copy, DeepClone(field.GetValue(source), seen, depth + 1));
                }
            }
            return copy;
        }

        /// <summary>The ocean map's value at that grid point (the point stands at block x * Cell, z * Cell). Outside the world: 0, land.</summary>
        public int Value(int cx, int cz)
        {
            if (gen == null || cx < 0 || cz < 0 || cx >= CellsX || cz >= CellsZ) return 0;
            int tx = cx / Tile, tz = cz / Tile;
            long key = ((long)tx << 32) | (uint)tz;
            if (!tiles.TryGetValue(key, out int[] tile))
            {
                tile = gen.GenLayer(tx * Tile, tz * Tile, Tile, Tile);
                tiles[key] = tile;
            }
            return tile[(cz - tz * Tile) * Tile + (cx - tx * Tile)] & 0xFF;
        }

        public bool Sea(int cx, int cz) { return Value(cx, cz) >= SailorLocustSystem.Config.OceanThreshold; }

        bool Coast(int cx, int cz)
        {
            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) if (!Sea(cx + dx, cz + dz)) return true;
            return false;
        }

        public static double Length(List<Vec2d> route)
        {
            double sum = 0;
            for (int i = 1; i < route.Count; i++) sum += Math.Sqrt((route[i].X - route[i - 1].X) * (route[i].X - route[i - 1].X) + (route[i].Y - route[i - 1].Y) * (route[i].Y - route[i - 1].Y));
            return sum;
        }

        bool NearestSea(int cx, int cz, int radius, out int sx, out int sz)
        {
            sx = cx; sz = cz;
            double best = double.MaxValue; bool found = false;
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (!Sea(cx + dx, cz + dz)) continue;
                    // open water first, a coast point only when nothing else is near
                    double d = dx * dx + dz * dz + (Coast(cx + dx, cz + dz) ? 4 : 0);
                    if (d < best) { best = d; sx = cx + dx; sz = cz + dz; found = true; }
                }
            }
            return found;
        }

        bool Clear(int ax, int az, int bx, int bz, bool offshore)
        {
            int steps = Math.Max(Math.Abs(bx - ax), Math.Abs(bz - az)) * 3;
            if (steps == 0) return true;
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps;
                int x = (int)Math.Round(ax + (bx - ax) * t), z = (int)Math.Round(az + (bz - az) * t);
                if (!Sea(x, z)) return false;
                if (offshore && i > 2 && i < steps - 2 && Coast(x, z)) return false;
            }
            return true;
        }

        /// <summary>
        /// The way from a to b in blocks, first and last points included. Over the open sea when the ocean map knows one;
        /// otherwise the straight line, left to the boat's own eyes and to the buoys.
        /// </summary>
        public List<Vec2d> FindRoute(double ax, double az, double bx, double bz, out string how)
        {
            var direct = new List<Vec2d> { new Vec2d(ax, az), new Vec2d(bx, bz) };
            Init();
            if (gen == null) { how = "straight line (no ocean map)"; return direct; }
            int sx0 = (int)Math.Round(ax / Cell), sz0 = (int)Math.Round(az / Cell), gx0 = (int)Math.Round(bx / Cell), gz0 = (int)Math.Round(bz / Cell);
            if (Math.Abs(sx0 - gx0) <= 2 && Math.Abs(sz0 - gz0) <= 2) { how = "straight line (near)"; return direct; }
            if (!NearestSea(sx0, sz0, 6, out int sx, out int sz) || !NearestSea(gx0, gz0, 6, out int gx, out int gz))
            {
                how = "straight line (no open sea near one end)";
                return direct;
            }

            int span = Math.Max(Math.Abs(sx - gx), Math.Abs(sz - gz));
            int margin = Math.Max(64, span);
            int minX = Math.Max(0, Math.Min(sx, gx) - margin), maxX = Math.Min(CellsX - 1, Math.Max(sx, gx) + margin);
            int minZ = Math.Max(0, Math.Min(sz, gz) - margin), maxZ = Math.Min(CellsZ - 1, Math.Max(sz, gz) + margin);
            int w = maxX - minX + 1, h = maxZ - minZ + 1;
            if ((long)w * h > 2500000) { how = "straight line (too far for one route: place buoys between)"; return direct; }

            var g = new float[w * h];
            var parent = new int[w * h];
            var closed = new BitArray(w * h);
            for (int i = 0; i < g.Length; i++) { g[i] = float.MaxValue; parent[i] = -1; }
            var open = new PriorityQueue<int, float>();
            int start = (sz - minZ) * w + (sx - minX), goal = (gz - minZ) * w + (gx - minX);
            g[start] = 0;
            open.Enqueue(start, 0);
            bool found = false;
            while (open.Count > 0)
            {
                int cur = open.Dequeue();
                if (closed[cur]) continue;
                closed[cur] = true;
                if (cur == goal) { found = true; break; }
                int cx = cur % w + minX, cz = cur / w + minZ;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cx + dx, nz = cz + dz;
                        if (nx < minX || nx > maxX || nz < minZ || nz > maxZ) continue;
                        int ni = (nz - minZ) * w + (nx - minX);
                        if (closed[ni] || !Sea(nx, nz)) continue;
                        if (dx != 0 && dz != 0 && (!Sea(cx + dx, cz) || !Sea(cx, cz + dz))) continue;   // no cutting of corners
                        float step = (dx != 0 && dz != 0 ? 1.4142f : 1f) * (Coast(nx, nz) ? 3f : 1f);   // keeps off the shore where it can
                        float ng = g[cur] + step;
                        if (ng >= g[ni]) continue;
                        g[ni] = ng;
                        parent[ni] = cur;
                        int ddx = Math.Abs(nx - gx), ddz = Math.Abs(nz - gz);
                        float hEst = Math.Max(ddx, ddz) + 0.4142f * Math.Min(ddx, ddz);
                        open.Enqueue(ni, ng + hEst);
                    }
                }
            }
            if (!found) { how = "straight line (the ocean map shows no sea route)"; return direct; }

            var nodes = new List<int>();
            for (int n = goal; n != -1; n = parent[n]) nodes.Add(n);
            nodes.Reverse();
            // fewer points: from each kept point, the farthest one still in plain sight over open water
            var kept = new List<int> { nodes[0] };
            int at = 0;
            while (at < nodes.Count - 1)
            {
                int next = at + 1;
                for (int j = nodes.Count - 1; j > at + 1; j--)
                {
                    if (Clear(nodes[at] % w + minX, nodes[at] / w + minZ, nodes[j] % w + minX, nodes[j] / w + minZ, true)) { next = j; break; }
                }
                kept.Add(nodes[next]);
                at = next;
            }
            var route = new List<Vec2d> { new Vec2d(ax, az) };
            foreach (int n in kept) route.Add(new Vec2d((n % w + minX) * (double)Cell, (n / w + minZ) * (double)Cell));
            route.Add(new Vec2d(bx, bz));
            how = "sea route";
            return route;
        }
    }
}
