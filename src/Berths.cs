using System;
using System.Collections.Generic;
using Vintagestory.API.Server;

namespace SailorLocust
{
    /// <summary>
    /// Which boat has which dock. Docks of the same name are one port, each a berth, as platforms of one station. A boat
    /// nearing a port asks for a berth: it gets the free one nearest to it, or none, and then waits off the port; of
    /// the boats waiting, the one that asked first is served first. A berth is kept by being asked for again and
    /// again (the boat at the dock does it every tick): when the boat sails away, or is gone for any reason, the
    /// berth comes free by itself a few seconds later. Nothing here is saved: after a restart the boats ask again.
    /// </summary>
    public class Berths
    {
        const long ExpireMs = 8000;
        readonly ICoreServerAPI sapi;
        readonly Dictionary<long, KeyValuePair<string, long>> held = new Dictionary<long, KeyValuePair<string, long>>();   // dock > ship, last asked
        readonly Dictionary<string, Dictionary<string, long[]>> queues = new Dictionary<string, Dictionary<string, long[]>>();   // port > ship > since, last asked

        public Berths(ICoreServerAPI sapi) { this.sapi = sapi; }

        long Now { get { return sapi.World.ElapsedMilliseconds; } }

        public static long KeyOf(Marker m) { return ((long)m.X & 0x3FFFFF) | (((long)m.Z & 0x3FFFFF) << 22) | (((long)m.Y & 0xFFFF) << 44); }

        /// <summary>The ship that has that dock now, or null.</summary>
        public string Holder(Marker dock)
        {
            KeyValuePair<string, long> hold;
            if (!held.TryGetValue(KeyOf(dock), out hold)) return null;
            if (Now - hold.Value > ExpireMs) { held.Remove(KeyOf(dock)); return null; }
            return hold.Key;
        }

        /// <summary>The dock of those that this ship has, or null.</summary>
        public Marker HeldBy(List<Marker> docks, string ship)
        {
            foreach (Marker dock in docks) if (Holder(dock) == ship) return dock;
            return null;
        }

        /// <summary>Keeps the dock for the ship (or takes it, if it is free). False when another ship has it.</summary>
        public bool Keep(Marker dock, string ship)
        {
            string holder = Holder(dock);
            if (holder != null && holder != ship) return false;
            held[KeyOf(dock)] = new KeyValuePair<string, long>(ship, Now);
            return true;
        }

        /// <summary>
        /// A berth of that port for the ship: the one it has already, or the free one nearest to it. Null when there is
        /// none, or when a ship that asked earlier is still waiting; the ship is then in the queue for as long as it
        /// keeps asking. taken tells of a dock with a boat lying at it that asked for nothing (a player's).
        /// </summary>
        public Marker Ask(string port, List<Marker> docks, string ship, double x, double z, Func<Marker, bool> taken)
        {
            Marker mine = HeldBy(docks, ship);
            if (mine != null) { Keep(mine, ship); return mine; }

            string name = port.Trim().ToLowerInvariant();
            Dictionary<string, long[]> queue;
            if (!queues.TryGetValue(name, out queue)) queues[name] = queue = new Dictionary<string, long[]>();
            long now = Now;
            long[] entry;
            if (!queue.TryGetValue(ship, out entry)) queue[ship] = entry = new[] { now, now };
            entry[1] = now;
            string first = null; long firstSince = long.MaxValue; List<string> gone = null;
            foreach (var pair in queue)
            {
                if (now - pair.Value[1] > ExpireMs) { if (gone == null) gone = new List<string>(); gone.Add(pair.Key); continue; }
                if (pair.Value[0] < firstSince) { firstSince = pair.Value[0]; first = pair.Key; }
            }
            if (gone != null) foreach (string g in gone) queue.Remove(g);
            if (first != ship) return null;

            Marker best = null; double bestDist = double.MaxValue;
            foreach (Marker dock in docks)
            {
                if (Holder(dock) != null || (taken != null && taken(dock))) continue;
                double d = (dock.X - x) * (dock.X - x) + (dock.Z - z) * (dock.Z - z);
                if (d < bestDist) { bestDist = d; best = dock; }
            }
            if (best == null) return null;
            held[KeyOf(best)] = new KeyValuePair<string, long>(ship, now);
            queue.Remove(ship);
            return best;
        }

        /// <summary>The ship gives up what it has and its place in every queue (taken back, or told to go elsewhere).</summary>
        public void Leave(string ship)
        {
            var mine = new List<long>();
            foreach (var pair in held) if (pair.Value.Key == ship) mine.Add(pair.Key);
            foreach (long key in mine) held.Remove(key);
            Done(ship);
            foreach (var queue in queues.Values) queue.Remove(ship);
        }

        // ---------------------------------------------------------------- the water of the port, one boat at a time

        /// <summary>A stretch of water a boat works in as it comes into or goes out of a berth: a rectangle on the water.</summary>
        public class Lane { public double X, Z, Ax, Az, HalfLength, HalfWidth; }

        class Wait { public Lane Lane; public long Since, Last; }
        readonly Dictionary<string, KeyValuePair<Lane, long>> lanes = new Dictionary<string, KeyValuePair<Lane, long>>();
        readonly Dictionary<string, Wait> waits = new Dictionary<string, Wait>();
        const long LaneExpireMs = 3000, MaxWorkMs = 90000;
        readonly Dictionary<string, long> granted = new Dictionary<string, long>();

        /// <summary>
        /// The ship may work in that lane now: no other ship works in water that crosses it. A ship keeps its lane by
        /// asking again (every tick while it is at it) and lets it go by Done, or by no longer asking. Of two ships that
        /// wait for crossing water, the one that asked first gets it first, so arriving and leaving boats take turns.
        /// </summary>
        public bool Manoeuvre(string ship, Lane lane)
        {
            long now = Now;
            Purge(now);
            if (lanes.ContainsKey(ship))
            {
                bool overdue = granted.TryGetValue(ship, out long since) && now - since > MaxWorkMs;
                if (!overdue) { lanes[ship] = new KeyValuePair<Lane, long>(lane, now); return true; }
                // a minute and a half at it (stuck, or slow): a ship waiting for crossing water goes first; this one queues again
                bool someone = false;
                foreach (var pair in waits) if (pair.Key != ship && Cross(pair.Value.Lane, lane)) someone = true;
                if (!someone) { lanes[ship] = new KeyValuePair<Lane, long>(lane, now); return true; }
                lanes.Remove(ship); granted.Remove(ship);
                waits[ship] = new Wait { Lane = lane, Since = now, Last = now };
                return false;
            }
            if (!waits.TryGetValue(ship, out Wait w)) waits[ship] = w = new Wait { Since = now };
            w.Lane = lane; w.Last = now;
            foreach (var pair in lanes) if (pair.Key != ship && Cross(pair.Value.Key, lane)) return false;
            foreach (var pair in waits) if (pair.Key != ship && pair.Value.Since < w.Since && Cross(pair.Value.Lane, lane)) return false;
            lanes[ship] = new KeyValuePair<Lane, long>(lane, now);
            granted[ship] = now;
            waits.Remove(ship);
            return true;
        }

        void Purge(long now)
        {
            List<string> gone = null;
            foreach (var pair in lanes) if (now - pair.Value.Value > LaneExpireMs) (gone ?? (gone = new List<string>())).Add(pair.Key);
            if (gone != null) { foreach (string g in gone) lanes.Remove(g); gone.Clear(); }
            foreach (var pair in waits) if (now - pair.Value.Last > LaneExpireMs) (gone ?? (gone = new List<string>())).Add(pair.Key);
            if (gone != null) foreach (string g in gone) waits.Remove(g);
        }

        readonly Dictionary<string, KeyValuePair<Lane, long>> berthRects = new Dictionary<string, KeyValuePair<Lane, long>>();

        /// <summary>Where the hull of the ship lies, or will, at the berth it has (told every tick while it has one).</summary>
        readonly Dictionary<string, Lane> berthWays = new Dictionary<string, Lane>();

        public void SetBerth(string ship, Lane rect, Lane way = null)
        {
            berthRects[ship] = new KeyValuePair<Lane, long>(rect, Now);
            if (way != null) berthWays[ship] = way; else berthWays.Remove(ship);
        }

        /// <summary>The ways in and out of the berths other ships have now.</summary>
        public List<Lane> OtherWays(string ship)
        {
            var list = new List<Lane>(); long now = Now;
            foreach (var pair in berthRects) if (pair.Key != ship && now - pair.Value.Value <= ExpireMs && berthWays.TryGetValue(pair.Key, out Lane w)) list.Add(w);
            return list;
        }

        /// <summary>The berths other ships have now, as the water their hulls take there.</summary>
        public List<Lane> OtherBerths(string ship)
        {
            var list = new List<Lane>(); long now = Now;
            foreach (var pair in berthRects) if (pair.Key != ship && now - pair.Value.Value <= ExpireMs) list.Add(pair.Value.Key);
            return list;
        }

        /// <summary>The ship is through: its water is free for the next.</summary>
        public void Done(string ship) { lanes.Remove(ship); waits.Remove(ship); granted.Remove(ship); }

        public bool Working(string ship) { return lanes.TryGetValue(ship, out var l) && Now - l.Value <= LaneExpireMs; }

        /// <summary>Two rectangles on the water overlap (the test of separating axes).</summary>
        public static bool Cross(Lane a, Lane b)
        {
            double dx = b.X - a.X, dz = b.Z - a.Z;
            foreach (var axis in new[] { (a.Ax, a.Az), (-a.Az, a.Ax), (b.Ax, b.Az), (-b.Az, b.Ax) })
            {
                double ra = a.HalfLength * Math.Abs(a.Ax * axis.Item1 + a.Az * axis.Item2) + a.HalfWidth * Math.Abs(-a.Az * axis.Item1 + a.Ax * axis.Item2);
                double rb = b.HalfLength * Math.Abs(b.Ax * axis.Item1 + b.Az * axis.Item2) + b.HalfWidth * Math.Abs(-b.Az * axis.Item1 + b.Ax * axis.Item2);
                if (Math.Abs(dx * axis.Item1 + dz * axis.Item2) > ra + rb) return false;
            }
            return true;
        }

        public int HeldCount { get { int n = 0; long now = Now; foreach (var pair in held) if (now - pair.Value.Value <= ExpireMs) n++; return n; } }
    }
}
