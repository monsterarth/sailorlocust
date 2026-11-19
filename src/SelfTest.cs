using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SailorLocust
{
    /// <summary>
    /// The mod tried in the game itself, on a server with nobody on it (tests\ingame\run.ps1 sets SAILORLOCUST_SELFTEST).
    /// It picks two points of sea with land between them, loads the chunks, places two docks and a buoy, launches
    /// boats with a sailor locust each and watches them sail the circuit. Does nothing unless the variable is set.
    /// </summary>
    public static class SelfTest
    {
        public static void MaybeStart(SailorLocustSystem system)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAILORLOCUST_SELFTEST"))) return;
            new Runner(system).Begin();
        }

        class Ship
        {
            public string Label, Orders;
            public string[] Expect;
            public Entity Boat;
            public EntitySailorLocust Locust;
            public List<string> Arrivals = new List<string>();
            public double WorstSide = -1, WorstOff = -1, WorstSnap, WorstTurn; public int Berthings;
            public int SailChanges, LastSail;
            public double HarborBumps; public int MaxOverlap;
            public double Contact, ApproachNow, ApproachMax, InWaitArea, StillFor, LongestStill; public Vec3d StillFrom; public int ExitsPassed; public bool LeftCapital;
            /// <summary>How the boat lies as it arrives: degrees off lying side-on to the post, and blocks from the middle of the berth.</summary>
            public void NoteBerth(Marker dock)
            {
                if (dock.Facing < 0 || Boat == null || Locust?.Pilot == null) return;
                Docking.Dir(dock.Facing, out double dx, out double dz);
                double px = Locust.Pilot.LastBerthX, pz = Locust.Pilot.LastBerthZ;
                WorstSnap = Math.Max(WorstSnap, Locust.Pilot.LastSnapDist); WorstTurn = Math.Max(WorstTurn, Locust.Pilot.LastSnapTurn);
                Vec3f view = Boat.Pos.GetViewVector();
                double len = Math.Sqrt(view.X * view.X + view.Z * view.Z);
                double along = Math.Abs((view.X * -dz + view.Z * dx) / Math.Max(len, 1e-6));
                double degrees = Math.Acos(Math.Min(1, along)) * 180 / Math.PI;
                double off = Math.Sqrt((Boat.Pos.X - px) * (Boat.Pos.X - px) + (Boat.Pos.Z - pz) * (Boat.Pos.Z - pz));
                Berthings++;
                if (degrees > WorstSide) WorstSide = degrees;
                if (off > WorstOff) WorstOff = off;
            }
            public List<double> ArrivalTimes = new List<double>(); public List<long> ArrivalKeys = new List<long>(); public double BerthWaitSince = -1;
            public int BuoysPassed;
            public double CollidedSeconds, Travelled, MaxSpeed;
            public int MaxStuck;
            public Vec3d Last;
            public bool Done;
            public double DoneAt;
            // sailing out of sight
            public string ShipId;
            public long OldBoatId;
            public double Speed, LeftX, LeftZ, BackX, BackZ; public string BackTag, BackState; public bool BackHelm;
            public Marker VirtualAt;
            public bool Reloaded, Back;
            public int ArrivalsAtBack;
        }

        class Runner
        {
            readonly SailorLocustSystem system;
            readonly ICoreServerAPI sapi;
            OceanGrid ocean;
            LocalNav nav;
            int ax, az, bx, bz;            // the two ends, in blocks
            List<Vec2d> route;
            int cx0, cz0, cx1, cz1, stripZ;
            int waterY;
            readonly List<Ship> ships = new List<Ship>();
            double elapsed, nextReport;
            double limit = 900;
            int passed, failed;
            bool finished;
            Marker alpha, bravo, mid;
            readonly bool offscreen = Environment.GetEnvironmentVariable("SAILORLOCUST_SELFTEST_MODE") == "offscreen"; readonly bool berths = Environment.GetEnvironmentVariable("SAILORLOCUST_SELFTEST_MODE") == "berths"; readonly bool docking = Environment.GetEnvironmentVariable("SAILORLOCUST_SELFTEST_MODE") == "docking"; readonly bool port = Environment.GetEnvironmentVariable("SAILORLOCUST_SELFTEST_MODE") == "port" || Environment.GetEnvironmentVariable("SAILORLOCUST_SELFTEST_MODE") == "portbuoys"; readonly bool portBuoys = Environment.GetEnvironmentVariable("SAILORLOCUST_SELFTEST_MODE") == "portbuoys"; Marker capital, waitArea, wayOut; int harborX, harborZ; Marker bravo2; int midX, midZ;

            public Runner(SailorLocustSystem system) { this.system = system; sapi = system.sapi; }

            void Log(string s) { sapi.Logger.Notification("[sailorlocust-selftest] " + s); }
            void Pass(string what, string detail = "") { passed++; Log("ok   " + what + (detail.Length > 0 ? " (" + detail + ")" : "")); }
            void Fail(string what, string detail = "") { failed++; Log("FAIL " + what + (detail.Length > 0 ? " (" + detail + ")" : "")); }
            void Check(bool ok, string what, string detail = "") { if (ok) Pass(what, detail); else Fail(what, detail); }

            public void Begin()
            {
                string seconds = Environment.GetEnvironmentVariable("SAILORLOCUST_SELFTEST_SECONDS");
                if (!string.IsNullOrEmpty(seconds)) double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out limit);
                SailorLocustSystem.Config.Debug = true;
                // a server with nobody on it: every boat would leave the world at once. The tests sail them in it
                SailorLocustSystem.Config.HideUnseen = false;
                sapi.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => sapi.Event.RegisterCallback(_ => Guard(PickSite), 3000));
            }

            void Guard(Action step)
            {
                try { step(); }
                catch (Exception e) { Fail("the self test ran", e.ToString()); Finish(); }
            }

            void PickSite()
            {
                ocean = system.Ocean;
                ocean.Init();
                nav = system.SharedNav;
                Log("world " + sapi.WorldManager.MapSizeX + " x " + sapi.WorldManager.MapSizeZ + ", seed " + sapi.World.Seed + ", sea level " + sapi.World.SeaLevel);
                if (!ocean.Ready) { Fail("the ocean map is there"); Finish(); return; }
                Pass("the ocean map is there", ocean.CellsX + " x " + ocean.CellsZ + " values of " + ocean.Cell + " blocks");

                // how the values are spread, to judge the threshold
                var hist = new int[13]; int sea = 0, all = 0;
                for (int z = 0; z < ocean.CellsZ; z += 2) for (int x = 0; x < ocean.CellsX; x += 2) { int v = ocean.Value(x, z); hist[Math.Min(12, v / 20)]++; all++; if (ocean.Sea(x, z)) sea++; }
                var sb = new StringBuilder("ocean values by 20s:");
                for (int i = 0; i < hist.Length; i++) sb.Append(" " + (i * 20) + "+:" + hist[i]);
                Log(sb.ToString());
                Log("sea at the threshold of " + SailorLocustSystem.Config.OceanThreshold + ": " + (100.0 * sea / all).ToString("0.0", CultureInfo.InvariantCulture) + "% of the world");

                // two points of open sea 400 to 600 blocks apart with land on the straight line between them
                int midX = ocean.CellsX / 2, midZ = ocean.CellsZ / 2;
                int[] reach = { 14, 16, 18, 12 };
                bool found = false;
                for (int ring = 0; ring < Math.Max(ocean.CellsX, ocean.CellsZ) / 2 && !found; ring += 2)
                {
                    for (int side = 0; side < 4 && !found; side++)
                    {
                        for (int t = -ring; t <= ring && !found; t += 2)
                        {
                            int x = midX + (side == 0 ? t : side == 1 ? t : side == 2 ? -ring : ring), z = midZ + (side == 0 ? -ring : side == 1 ? ring : t);
                            if (!OpenSea(x, z)) continue;
                            for (int dir = 0; dir < 8 && !found; dir++)
                            {
                                foreach (int far in reach)
                                {
                                    double ang = dir * Math.PI / 4;
                                    int x2 = x + (int)Math.Round(Math.Cos(ang) * far), z2 = z + (int)Math.Round(Math.Sin(ang) * far);
                                    if (!OpenSea(x2, z2) || LandBetween(x, z, x2, z2) < 3) continue;
                                    List<Vec2d> r = ocean.FindRoute(x * ocean.Cell, z * ocean.Cell, x2 * ocean.Cell, z2 * ocean.Cell, out string how);
                                    double straight = Math.Sqrt((x - x2) * (x - x2) + (z - z2) * (z - z2)) * ocean.Cell, len = OceanGrid.Length(r);
                                    if (how != "sea route" || len > straight * 2.2 || len < straight * 1.15) continue;
                                    ax = x * ocean.Cell; az = z * ocean.Cell; bx = x2 * ocean.Cell; bz = z2 * ocean.Cell; route = r;
                                    Log("site: " + ax + "," + az + " to " + bx + "," + bz + ", straight " + (int)straight + " blocks over " + LandBetween(x, z, x2, z2) + " land values, sea route " + (int)len + " blocks in " + r.Count + " points");
                                    found = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                if (!found) { Fail("a place to test was found", "no two points of sea with land between them"); Finish(); return; }

                double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
                foreach (Vec2d p in route) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minZ = Math.Min(minZ, p.Y); maxZ = Math.Max(maxZ, p.Y); }
                cx0 = ((int)minX - 96) >> 5; cx1 = ((int)maxX + 96) >> 5; cz0 = ((int)minZ - 96) >> 5; cz1 = ((int)maxZ + 96) >> 5;
                Log("loading " + (cx1 - cx0 + 1) * (cz1 - cz0 + 1) + " chunk columns");
                stripZ = cz0;
                NextStrip();
            }

            bool OpenSea(int x, int z)
            {
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) if (!ocean.Sea(x + dx, z + dz)) return false;
                return true;
            }

            int LandBetween(int x, int z, int x2, int z2)
            {
                int steps = Math.Max(Math.Abs(x2 - x), Math.Abs(z2 - z)) * 2, land = 0;
                for (int i = 0; i <= steps; i++)
                {
                    double t = i / (double)steps;
                    if (!ocean.Sea((int)Math.Round(x + (x2 - x) * t), (int)Math.Round(z + (z2 - z) * t))) land++;
                }
                return land / 2;
            }

            void NextStrip()
            {
                if (stripZ > cz1) { sapi.Event.RegisterCallback(_ => Guard(Launch), 2000); return; }
                int z0 = stripZ, z1 = Math.Min(stripZ + 5, cz1);
                stripZ = z1 + 1;
                sapi.WorldManager.LoadChunkColumnPriority(cx0, z0, cx1, z1, new ChunkLoadOptions { KeepLoaded = true, OnLoaded = () => sapi.Event.RegisterCallback(_ => NextStrip(), 20) });
            }

            bool WaterAround(int x, int z, int radius)
            {
                for (int dz = -radius; dz <= radius; dz++) for (int dx = -radius; dx <= radius; dx++) if (!nav.IsWater(x + dx, waterY, z + dz)) return false;
                return true;
            }

            bool NearestWater(int x, int z, int radius, int clear, out int wx, out int wz)
            {
                wx = x; wz = z;
                for (int r = 0; r <= radius; r += 2)
                {
                    for (int dz = -r; dz <= r; dz += 2)
                    {
                        for (int dx = -r; dx <= r; dx += 2)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                            if (WaterAround(x + dx, z + dz, clear)) { wx = x + dx; wz = z + dz; return true; }
                        }
                    }
                }
                return false;
            }

            Marker Place(string block, string name, int x, int z)
            {
                Block b = sapi.World.GetBlock(new AssetLocation(SailorLocustSystem.Domain, block));
                if (b == null) { Fail("the block " + block + " is there"); return null; }
                var pos = new BlockPos(x, waterY + 1, z, 0);
                sapi.World.BlockAccessor.SetBlock(b.BlockId, pos);
                var be = sapi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntitySailorMarker;
                if (be == null) { Fail("the block " + block + " has its block entity"); return null; }
                if (string.IsNullOrEmpty(be.MarkerName)) be.OnBlockPlaced(null);
                string given = be.MarkerName;
                // named as a player would, through the packet's handler
                be.MarkerName = name;
                system.Markers.Set(pos, name, be.IsDock);
                be.MarkDirty(true);
                Marker m = system.Markers.Find(name, x, z);
                Check(m != null && m.Dock == (block == "sailordock"), "the " + block + " \"" + name + "\" is in the registry", "first named \"" + given + "\", at " + x + " " + (waterY + 1) + " " + z);
                return m;
            }

            EntityProperties BoatType(string cls, string part)
            {
                EntityProperties any = null;
                foreach (EntityProperties type in sapi.World.EntityTypes)
                {
                    if (type.Class != cls) continue;
                    if (type.Code.Path.Contains(part)) { if (type.Code.Path.EndsWith("-oak")) return type; if (any == null) any = type; }
                }
                return any;
            }

            void AddShip(string label, EntityProperties type, int nearX, int nearZ, int offset, string orders, string[] expect)
            {
                if (type == null) { Log("skip " + label + ": no such boat in this server's mods"); return; }
                if (!NearestWater(nearX + offset, nearZ + offset, 40, 5, out int wx, out int wz)) { Fail(label + ": water to launch it on"); return; }
                Entity boat = sapi.World.ClassRegistry.CreateEntity(type);
                boat.Pos.SetPos(wx + 0.5, waterY + 1.0, wz + 0.5);
                sapi.World.SpawnEntity(boat);
                string error = system.Install(boat, null, out EntitySailorLocust locust);
                if (error != null) { Fail(label + ": the sailor locust takes the helm of " + type.Code, error); return; }
                Pass(label + ": the sailor locust takes the helm of " + type.Code, "stands by seat " + locust.WatchedAttributes.GetString("perchSeat", ""));
                locust.SetOrders(orders);
                locust.SetRunning(true);
                ships.Add(new Ship { Label = label, Orders = orders, Expect = expect, Boat = boat, Locust = locust, Last = boat.Pos.XYZ });
            }

            void Launch()
            {
                waterY = sapi.World.SeaLevel - 1;
                // what the ocean map says against the blocks really there
                var water = new int[13]; var total = new int[13];
                for (int gz = (cz0 << 5) / ocean.Cell + 1; gz * ocean.Cell < (cz1 << 5); gz++)
                {
                    for (int gx = (cx0 << 5) / ocean.Cell + 1; gx * ocean.Cell < (cx1 << 5); gx++)
                    {
                        int bucket = Math.Min(12, ocean.Value(gx, gz) / 20);
                        total[bucket]++;
                        if (nav.Loaded(gx * ocean.Cell, waterY, gz * ocean.Cell) && nav.IsWater(gx * ocean.Cell, waterY, gz * ocean.Cell)) water[bucket]++;
                    }
                }
                var sb = new StringBuilder("ocean value against real water at sea level (water/points):");
                for (int i = 0; i < total.Length; i++) if (total[i] > 0) sb.Append(" " + (i * 20) + "+:" + water[i] + "/" + total[i]);
                Log(sb.ToString());

                if (!NearestWater(ax, az, 48, 4, out int ax2, out int az2) || !NearestWater(bx, bz, 48, 4, out int bx2, out int bz2)) { Fail("real water at both ends"); Finish(); return; }
                Pass("real water at both ends", ax2 + "," + az2 + " and " + bx2 + "," + bz2);
                alpha = Place("sailordock", "Alpha", ax2, az2);
                bravo = Place("sailordock", "Bravo", bx2, bz2);
                // the buoy about half way along the sea route
                double half = OceanGrid.Length(route) / 2, run = 0; Vec2d midPoint = route[route.Count / 2];
                for (int i = 1; i < route.Count; i++)
                {
                    double seg = Math.Sqrt((route[i].X - route[i - 1].X) * (route[i].X - route[i - 1].X) + (route[i].Y - route[i - 1].Y) * (route[i].Y - route[i - 1].Y));
                    if (run + seg >= half) { double t = (half - run) / seg; midPoint = new Vec2d(route[i - 1].X + (route[i].X - route[i - 1].X) * t, route[i - 1].Y + (route[i].Y - route[i - 1].Y) * t); break; }
                    run += seg;
                }
                if (NearestWater((int)midPoint.X, (int)midPoint.Y, 64, 4, out int mx, out int mz)) { mid = Place("sailorbuoy", "Mid", mx, mz); midX = mx; midZ = mz; }
                else Fail("real water for the buoy half way");
                if (alpha == null || bravo == null) { Finish(); return; }

                int recipes = 0;
                foreach (var recipe in sapi.World.GridRecipes) if (recipe?.Name != null && recipe.Name.Domain == SailorLocustSystem.Domain) recipes++;
                Check(recipes == 3, "the three recipes are loaded", recipes + " found");

                Pilot.Arrived += (locust, marker) =>
                {
                    foreach (Ship s in ships) if (s.Locust == locust) { s.Arrivals.Add(marker.Name); s.ArrivalKeys.Add(Berths.KeyOf(marker)); s.ArrivalTimes.Add(elapsed); s.NoteBerth(marker); Log(s.Label + " arrives at " + marker.Name + " after " + (int)elapsed + " s, " + (int)s.Travelled + " blocks sailed"); }
                };
                Pilot.Passed += (locust, marker) =>
                {
                    foreach (Ship s in ships) if (s.Locust == locust) { s.BuoysPassed++; Log(s.Label + " passes the buoy " + marker.Name + " after " + (int)elapsed + " s"); }
                };

                string withBuoy = mid != null ? "Alpha; 3\nMid\nBravo; 3" : "Alpha; 3\nBravo; 3";
                if (docking) { Harbor(); return; }
                if (port) { Port(); return; }
                if (berths)
                {
                    // a port of two docks and three boats: two berth, the third waits off the port until one sails away
                    if (!NearestWater(bx2 + 24, bz2 + 6, 40, 4, out int b2x, out int b2z) || Math.Abs(b2x - bx2) + Math.Abs(b2z - bz2) < 16) { Fail("water for a second dock at Bravo"); Finish(); return; }
                    bravo2 = Place("sailordock", "Bravo", b2x, b2z);
                    Check(system.Markers.FindAll("Bravo").Count == 2, "two docks share the name Bravo", "the second " + (int)Math.Sqrt((b2x - bx2) * (b2x - bx2) + (b2z - bz2) * (b2z - bz2)) + " blocks from the first");
                    AddShip("Shipwright catboat", BoatType("EntityShipwrightBoat", "catboat"), midX, midZ, 10, "Bravo; 3600", new[] { "Bravo" });
                    AddShip("vanilla sailboat", BoatType("EntityBoat", "sailed"), midX, midZ, -12, "Bravo; 3600", new[] { "Bravo" });
                    AddShip("vanilla raft", BoatType("EntityBoat", "raft"), midX, midZ, 24, "Bravo; 3600", new[] { "Bravo" });
                    if (ships.Count < 3) { Fail("three boats were launched"); Finish(); return; }
                    sapi.Event.RegisterGameTickListener(dt => Guard(() => WatchBerths(dt)), 250);
                    return;
                }
                if (offscreen)
                {
                    // out of sight: each boat first reaches the dock it starts by, then its chunks are unloaded under it
                    AddShip("vanilla sailboat", BoatType("EntityBoat", "sailed"), ax2, az2, 12, "Alpha; 3\nBravo; 20", new[] { "Alpha" });
                    AddShip("Shipwright catboat", BoatType("EntityShipwrightBoat", "catboat"), bx2, bz2, 12, "Bravo; 3\nAlpha; 20", new[] { "Bravo" });
                    OffscreenFleet.VirtualArrived += (ship, marker) =>
                    {
                        foreach (Ship s in ships) if (s.ShipId == ship.ShipId && s.VirtualAt == null) { s.VirtualAt = marker; Log(s.Label + " reaches " + marker.Name + " out of sight after " + (int)elapsed + " s, at " + (int)ship.X + "," + (int)ship.Z); }
                    };
                    OffscreenFleet.Materialized += (ship, boat, locust) =>
                    {
                        foreach (Ship s in ships) if (s.ShipId == ship.ShipId) { s.Boat = boat; s.Locust = locust; s.Last = boat.Pos.XYZ; if (!s.Back) { s.BackX = boat.Pos.X; s.BackZ = boat.Pos.Z; s.BackTag = boat.WatchedAttributes.GetString("selftestTag", ""); s.BackHelm = locust.Boat == boat && locust.Pilot.Running; s.BackState = "stop " + locust.Pilot.StopIndex + ", waiting " + locust.Pilot.Waiting; } s.Back = true; s.ArrivalsAtBack = s.Arrivals.Count; Log(s.Label + " is back on the water at " + (int)boat.Pos.X + "," + (int)boat.Pos.Z + " after " + (int)elapsed + " s"); }
                    };
                }
                else
                {
                    AddShip("vanilla sailboat", BoatType("EntityBoat", "sailed"), ax2, az2, 12, withBuoy, new[] { "Alpha", "Bravo", "Alpha" });
                    AddShip("Shipwright catboat", BoatType("EntityShipwrightBoat", "catboat"), bx2, bz2, 12, "Bravo; 3\nAlpha; 3", new[] { "Bravo", "Alpha", "Bravo" });
                    AddShip("vanilla raft", BoatType("EntityBoat", "raft"), ax2, az2, -14, "Bravo; 3\nMid", new[] { "Bravo" });
                }
                if (ships.Count == 0) { Fail("a boat was launched"); Finish(); return; }
                foreach (Ship s in ships) s.ShipId = s.Boat.WatchedAttributes.GetString(OffscreenFleet.IdAttr, null);
                if (offscreen) sapi.Event.RegisterGameTickListener(dt => Guard(() => WatchOffscreen(dt)), 250);
                else sapi.Event.RegisterGameTickListener(dt => Guard(() => Watch(dt)), 250);
            }

            // ---------------------------------------------------------------- a harbour with piers

            Block solidBlock;

            void Fill(int x0, int y0, int z0, int x1, int y1, int z1)
            {
                var pos = new BlockPos(0, 0, 0, 0);
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++) { pos.Set(x, y, z); sapi.World.BlockAccessor.SetBlock(solidBlock.BlockId, pos); }
            }

            Marker PlaceDock(string name, int x, int y, int z, int facing)
            {
                Block b = sapi.World.GetBlock(new AssetLocation(SailorLocustSystem.Domain, "sailordock"));
                var pos = new BlockPos(x, y, z, 0);
                sapi.World.BlockAccessor.SetBlock(b.BlockId, pos);
                var be = sapi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntitySailorMarker;
                if (be == null) { Fail("the dock " + name + " has its block entity"); return null; }
                be.MarkerName = name;
                be.SetFacing(facing);
                system.Markers.Set(pos, name, true, facing);
                return system.Markers.Find(name, x, z);
            }

            /// <summary>
            /// A harbour built on open water: two piers of 18 blocks with a slip of 15 blocks between them, closed by a
            /// wall at the back. One dock at the head of the north pier, looking out to sea (boats lie across it), one
            /// on the side of the south pier, looking into the slip (boats lie alongside, between the two piers). Boats
            /// come in from open water to the east and go back out, in turns, two of them to the same dock.
            /// </summary>
            void Harbor()
            {
                foreach (string code in new[] { "game:planks-oak-ud", "game:planks-oak-ns", "game:cobblestone-granite", "game:rock-granite", "game:soil-medium-none" })
                {
                    solidBlock = sapi.World.GetBlock(new AssetLocation(code));
                    if (solidBlock != null && solidBlock.Id != 0) break;
                }
                if (solidBlock == null) { Fail("a block to build piers with"); Finish(); return; }
                if (!NearestWater(midX, midZ, 70, 26, out harborX, out harborZ)) { Fail("open water for a harbour", "none of 53 x 53 near the buoy"); Finish(); return; }
                int x0 = harborX - 12, z0 = harborZ - 12;
                Fill(x0 - 2, waterY - 3, z0 - 2, x0 - 1, waterY + 2, z0 + 26);          // the back wall
                Fill(x0, waterY, z0, x0 + 17, waterY + 1, z0 + 4);                       // north pier
                Fill(x0, waterY, z0 + 20, x0 + 17, waterY + 1, z0 + 24);                 // south pier
                Marker head = PlaceDock("Head", x0 + 17, waterY + 2, z0 + 2, 2);          // at the head, looking east
                Marker side = PlaceDock("Side", x0 + 9, waterY + 2, z0 + 20, 4);          // on the side, looking north into the slip
                Marker far = null;
                if (NearestWater(harborX + 75, harborZ, 40, 4, out int fx, out int fz)) far = Place("sailorbuoy", "Far", fx, fz);
                if (head == null || side == null || far == null) { Fail("the harbour was built"); Finish(); return; }
                Pass("the harbour was built", "piers of " + solidBlock.Code + " at " + x0 + "," + z0 + "; docks Head and Side; buoy Far at " + fx + "," + fz);

                Pilot.Arrived += (locust, marker) =>
                {
                    foreach (Ship s in ships)
                    {
                        if (s.Locust != locust) continue;
                        // the arrival itself is counted by the handler every test has; this one only looks at the hull
                        int over = Overlap(s.Boat);
                        s.MaxOverlap = Math.Max(s.MaxOverlap, over);
                        Log(s.Label + " berths at " + marker.Name + " after " + (int)elapsed + " s: moved " + locust.Pilot.LastSnapDist.ToString("0.0", CultureInfo.InvariantCulture) + " and turned " + locust.Pilot.LastSnapTurn.ToString("0", CultureInfo.InvariantCulture) + " degrees when tied, " + over + " block(s) of pier under its hull, " + s.HarborBumps.ToString("0.0", CultureInfo.InvariantCulture) + " s against something so far");
                    }
                };
                Pilot.Passed += (locust, marker) => { foreach (Ship s in ships) if (s.Locust == locust) s.BuoysPassed++; };

                AddShip("Shipwright drakkar", BoatType("EntityShipwrightBoat", "drakkar"), fx, fz, 14, "Head; 5\nFar\nSide; 5\nFar", new[] { "Head", "Side" });
                AddShip("vanilla sailboat", BoatType("EntityBoat", "sailed"), fx, fz, -14, "Side; 5\nFar\nHead; 5\nFar", new[] { "Side", "Head" });
                AddShip("Shipwright catboat", BoatType("EntityShipwrightBoat", "catboat"), fx, fz, 24, "Head; 5\nFar", new[] { "Head", "Head" });
                AddShip("vanilla raft", BoatType("EntityBoat", "raft"), fx, fz, -24, "Side; 5\nFar", new[] { "Side", "Side" });
                if (ships.Count < 3) { Fail("the boats were launched"); Finish(); return; }
                sapi.Event.RegisterGameTickListener(dt => Guard(() => WatchHarbor(dt)), 250);
            }

            /// <summary>
            /// A port as players build them: three piers side by side, each a narrow walk ending in a wider T-head, and a
            /// single dock at the head of the middle one, looking out to sea. Three boats share it, two of them long
            /// drakkars, each going out round a buoy and back: they meet, queue for the berth and pass one another.
            /// </summary>
            void Port()
            {
                foreach (string code in new[] { "game:planks-oak-ud", "game:planks-oak-ns", "game:cobblestone-granite", "game:rock-granite", "game:soil-medium-none" })
                {
                    solidBlock = sapi.World.GetBlock(new AssetLocation(code));
                    if (solidBlock != null && solidBlock.Id != 0) break;
                }
                if (solidBlock == null) { Fail("a block to build piers with"); Finish(); return; }
                if (!NearestWater(midX, midZ, 70, 26, out harborX, out harborZ)) { Fail("open water for a port", "none of 53 x 53 near the buoy"); Finish(); return; }
                int x0 = harborX - 16, z0 = harborZ - 14;
                Fill(x0 - 3, waterY - 3, z0 - 4, x0 - 1, waterY + 2, z0 + 30);                      // the shore
                for (int i = 0; i < 3; i++)
                {
                    int zc = z0 + i * 12;
                    Fill(x0, waterY, zc - 1, x0 + 27, waterY + 1, zc + 1);                             // the walk, 3 wide
                    Fill(x0 + 28, waterY, zc - 3, x0 + 30, waterY + 1, zc + 3);                        // the T-head, 7 wide
                }
                capital = PlaceDock("Capital", x0 + 30, waterY + 2, z0 + 12, 2);                       // middle head, looking east
                // the other two heads are berths of the same port: three boats can lie at Capital at once
                if (PlaceDock("Capital", x0 + 30, waterY + 2, z0, 2) == null || PlaceDock("Capital", x0 + 30, waterY + 2, z0 + 24, 2) == null) { Fail("the port was built", "no second and third berth at Capital"); Finish(); return; }
                // and one on the side of the north pier, looking into the slip between it and the middle one: in through
                // the gap of 5 blocks between the two heads, bow first, and out astern
                Marker slip = PlaceDock("Slip", x0 + 14, waterY + 2, z0 + 1, 0);
                if (slip == null) { Fail("the port was built", "no dock Slip"); Finish(); return; }
                Marker far = null;
                if (NearestWater(harborX + 80, harborZ, 40, 4, out int fx, out int fz)) far = Place("sailorbuoy", "Far", fx, fz);
                if (capital == null || far == null) { Fail("the port was built"); Finish(); return; }
                if (portBuoys)
                {
                    // the port's own buoys: a waiting area off to one side, and a way out to the other
                    waitArea = PlaceRole("Capital", Marker.RoleWait, x0 + 60, z0 + 50);
                    wayOut = PlaceRole("Capital", Marker.RoleExit, x0 + 60, z0 - 30);
                    if (waitArea == null || wayOut == null) { Fail("the port was built", "no buoys of the port"); Finish(); return; }
                    Check(system.Markers.Find("Capital", waitArea.X, waitArea.Z)?.Dock == true, "a stop named after the port is its docks, not its buoys");
                }
                Pass("the port was built", "three T-piers of " + solidBlock.Code + " at " + x0 + "," + z0 + "; Capital with a berth at each of the three heads, Slip on the side of a pier in the slip; buoy Far at " + fx + "," + fz);

                Pilot.Arrived += (locust, marker) =>
                {
                    foreach (Ship s in ships)
                    {
                        if (s.Locust != locust) continue;
                        int over = Overlap(s.Boat);
                        s.MaxOverlap = Math.Max(s.MaxOverlap, over);
                        s.ApproachMax = Math.Max(s.ApproachMax, s.ApproachNow);
                        Log(s.Label + " berths at " + marker.Name + " after " + (int)elapsed + " s, " + s.ApproachNow.ToString("0", CultureInfo.InvariantCulture) + " s coming in: moved " + locust.Pilot.LastSnapDist.ToString("0.0", CultureInfo.InvariantCulture) + " and turned " + locust.Pilot.LastSnapTurn.ToString("0", CultureInfo.InvariantCulture) + " degrees when tied, " + over + " block(s) of pier under its hull, " + s.Contact.ToString("0.0", CultureInfo.InvariantCulture) + " s hull against hull so far");
                        s.ApproachNow = 0;
                    }
                };
                AddShip("Shipwright drakkar A", BoatType("EntityShipwrightBoat", "drakkar"), fx, fz, 14, "Capital; 15\nFar\nSlip; 15\nFar", new[] { "Capital", "Slip", "Capital" });
                AddShip("vanilla sailboat", BoatType("EntityBoat", "sailed"), fx, fz, -14, "Capital; 15\nFar", new[] { "Capital", "Capital", "Capital" });
                AddShip("Shipwright drakkar B", BoatType("EntityShipwrightBoat", "drakkar"), fx, fz, 28, "Slip; 15\nFar\nCapital; 15\nFar", new[] { "Slip", "Capital", "Slip" });
                AddShip("Shipwright catboat", BoatType("EntityShipwrightBoat", "catboat"), fx, fz, -28, "Capital; 15\nFar", new[] { "Capital", "Capital", "Capital" });
                if (ships.Count < 4) { Fail("the boats were launched"); Finish(); return; }
                foreach (Ship s in ships) { Docking.BoatSize(s.Boat, out double hl, out double hw); Log(s.Label + ": hull " + (2 * hl).ToString("0.0", CultureInfo.InvariantCulture) + " x " + (2 * hw).ToString("0.0", CultureInfo.InvariantCulture)); }
                sapi.Event.RegisterGameTickListener(dt => Guard(() => WatchPort(dt)), 250);
            }

            Marker PlaceRole(string name, int role, int x, int z)
            {
                if (!NearestWater(x, z, 30, 3, out int wx, out int wz)) return null;
                Block b = sapi.World.GetBlock(new AssetLocation(SailorLocustSystem.Domain, role == Marker.RoleWait ? "sailorbeacon-wait" : "sailorbeacon-exit"));
                if (b == null) { Fail("the beacon of role " + role + " is there"); return null; }
                var pos = new BlockPos(wx, waterY + 1, wz, 0);
                sapi.World.BlockAccessor.SetBlock(b.BlockId, pos);
                var be = sapi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntitySailorMarker;
                if (be == null) return null;
                be.MarkerName = name; be.Role = role;
                system.Markers.Set(pos, name, false, -1, false, role);
                be.MarkDirty(true);
                return system.Markers.FindRole(name, role, wx, wz);
            }

            /// <summary>The hull of one boat runs into the hull of the other: points of each, every half block, inside the other.</summary>
            static bool HullsTouch(Entity a, Entity b)
            {
                return PointsIn(a, b) || PointsIn(b, a);
            }

            static bool PointsIn(Entity a, Entity b)
            {
                Docking.BoatSize(a, out double al, out double aw);
                Docking.BoatSize(b, out double bl, out double bw);
                Vec3f va = a.Pos.GetViewVector(), vb = b.Pos.GetViewVector();
                double la = Math.Sqrt(va.X * va.X + va.Z * va.Z), lb = Math.Sqrt(vb.X * vb.X + vb.Z * vb.Z);
                if (la < 1e-6 || lb < 1e-6) return false;
                double ax = va.X / la, az = va.Z / la, bx = vb.X / lb, bz = vb.Z / lb;
                for (double s = -al + 0.3; s <= al - 0.3; s += 0.5)
                {
                    for (double c = -aw + 0.3; c <= aw - 0.29; c += 0.5)
                    {
                        double px = a.Pos.X + ax * s + az * c - b.Pos.X, pz = a.Pos.Z + az * s - ax * c - b.Pos.Z;
                        if (Math.Abs(px * bx + pz * bz) < bl - 0.3 && Math.Abs(px * bz - pz * bx) < bw - 0.3) return true;
                    }
                }
                return false;
            }

            void WatchPort(float dt)
            {
                if (finished) return;
                elapsed += dt;
                bool allDone = true;
                foreach (Ship s in ships)
                {
                    Track(s, dt);
                    if (s.Boat == null || !s.Boat.Alive) continue;
                    double dx = s.Boat.Pos.X - capital.X, dz = s.Boat.Pos.Z - capital.Z;
                    string status = s.Locust.WatchedAttributes.GetString("status", "");
                    if (dx * dx + dz * dz < 45 * 45 && s.Boat.CollidedHorizontally) s.HarborBumps += dt;
                    // coming in: under way to the dock and near it, not counting the time it waits off the port or gives way
                    if (dx * dx + dz * dz < 50 * 50 && status.StartsWith("status-sailing|") && !status.StartsWith("status-sailing|Far")) s.ApproachNow += dt;
                    foreach (Ship o in ships) if (o != s && o.Boat != null && o.Boat.Alive && HullsTouch(s.Boat, o.Boat)) s.Contact += dt;
                    // stuck: not tied up at a dock and not 3 blocks from where it was, for how long (waiting off the port counts too: a jammed port is the worst outcome)
                    if (status.StartsWith("status-waiting") || status.StartsWith("status-waitturn")) { s.StillFor = 0; s.StillFrom = null; }
                    else
                    {
                        if (s.StillFrom == null || s.Boat.Pos.XYZ.SquareDistanceTo(s.StillFrom) > 9) { s.StillFrom = s.Boat.Pos.XYZ; s.StillFor = 0; }
                        else { s.StillFor += dt; s.LongestStill = Math.Max(s.LongestStill, s.StillFor); }
                    }
                    if (portBuoys)
                    {
                        if (status.StartsWith("status-waitarea")) s.InWaitArea += dt;
                        if (status.StartsWith("status-waiting|Capital")) s.LeftCapital = true;
                        if (s.LeftCapital && Math.Sqrt((s.Boat.Pos.X - wayOut.X) * (s.Boat.Pos.X - wayOut.X) + (s.Boat.Pos.Z - wayOut.Z) * (s.Boat.Pos.Z - wayOut.Z)) < SailorLocustSystem.Config.BuoyPassRadius + 2) { s.ExitsPassed++; s.LeftCapital = false; }
                    }
                    if (s.Arrivals.Count < s.Expect.Length) allDone = false;
                }
                if (elapsed >= nextReport)
                {
                    nextReport = elapsed + 30;
                    foreach (Ship s in ships) Log((int)elapsed + " s  " + s.Label + " at " + (int)(s.Boat.Pos.X - capital.X) + "," + (int)(s.Boat.Pos.Z - capital.Z) + " from the dock  [" + s.Locust.WatchedAttributes.GetString("status", "") + "]  arrivals " + s.Arrivals.Count + ", " + s.Contact.ToString("0.0", CultureInfo.InvariantCulture) + " s against another boat, " + s.HarborBumps.ToString("0.0", CultureInfo.InvariantCulture) + " s against something");
                }
                if (!allDone && elapsed < limit) return;
                if (portBuoys)
                {
                    double waited = 0; int exits = 0, capitalStays = 0;
                    foreach (Ship s in ships) { waited += s.InWaitArea; exits += s.ExitsPassed; foreach (string a in s.Arrivals) if (a == "Capital") capitalStays++; }
                    Check(waited > 0, "boats with no berth free wait in the port's waiting area", waited.ToString("0", CultureInfo.InvariantCulture) + " s in it, all boats together");
                    Check(exits >= capitalStays - ships.Count, "boats leaving Capital go out by the way out of the port", exits + " pass(es) of it for " + capitalStays + " stay(s) at Capital");
                }
                foreach (Ship s in ships)
                {
                    Check(s.Arrivals.Count >= s.Expect.Length, s.Label + ": berths at " + string.Join(" > ", s.Expect), s.Arrivals.Count + " time(s) in " + (int)elapsed + " s");
                    Check(s.ApproachMax < 120, s.Label + ": comes in without circling", "at worst " + s.ApproachMax.ToString("0", CultureInfo.InvariantCulture) + " s under way within 40 blocks of the dock before it was tied");
                    Check(s.Locust.Pilot.Retries <= 1, s.Label + ": gets into the berth at the first try", s.Locust.Pilot.Retries + " time(s) round again or backed out over " + s.Arrivals.Count + " berthing(s)");
                    // a bump now and then is tolerated; a boat stuck is not
                    Check(s.Contact < 15, s.Label + ": seldom touches another boat", s.Contact.ToString("0.0", CultureInfo.InvariantCulture) + " s hull against hull");
                    Check(s.LongestStill < 180, s.Label + ": never stuck in or off the port", "at worst " + s.LongestStill.ToString("0", CultureInfo.InvariantCulture) + " s without moving 3 blocks while not tied up");
                    if (s.Berthings > 0)
                    {
                        Check(s.WorstSide < 3 && s.WorstOff < 0.5, s.Label + ": lies tied in the berth along the pier", "at worst " + s.WorstSide.ToString("0", CultureInfo.InvariantCulture) + " degrees off and " + s.WorstOff.ToString("0.0", CultureInfo.InvariantCulture) + " blocks from it");
                        Check(s.WorstSnap < 2.5 && s.WorstTurn < 30, s.Label + ": comes in alongside on its own", "at worst moved " + s.WorstSnap.ToString("0.0", CultureInfo.InvariantCulture) + " and turned " + s.WorstTurn.ToString("0", CultureInfo.InvariantCulture) + " degrees when tied");
                    }
                    Check(s.MaxOverlap == 0, s.Label + ": lies clear of the pier", s.MaxOverlap + " block(s) of pier under its hull at worst");
                    Check(s.HarborBumps < 5, s.Label + ": does not run into the piers", s.HarborBumps.ToString("0.0", CultureInfo.InvariantCulture) + " s against something within 45 blocks of the dock");
                }
                Finish();
            }

            /// <summary>Blocks of pier under the boat's hull where it lies: its length and width sampled every half block.</summary>
            int Overlap(Entity boat)
            {
                if (boat == null) return 0;
                Docking.BoatSize(boat, out double hl, out double hw);
                Vec3f view = boat.Pos.GetViewVector();
                double len = Math.Sqrt(view.X * view.X + view.Z * view.Z);
                double fx = view.X / len, fz = view.Z / len;
                var hit = new HashSet<long>();
                var pos = new BlockPos(0, 0, 0, 0);
                for (double a = -hl + 0.2; a <= hl - 0.2; a += 0.5)
                {
                    for (double c = -hw + 0.2; c <= hw - 0.2; c += 0.5)
                    {
                        int x = (int)Math.Floor(boat.Pos.X + fx * a - fz * c), z = (int)Math.Floor(boat.Pos.Z + fz * a + fx * c);
                        for (int y = waterY; y <= waterY + 1; y++)
                        {
                            pos.Set(x, y, z);
                            if (sapi.World.BlockAccessor.GetBlock(pos).Id == solidBlock.Id) hit.Add(((long)x << 32) ^ (uint)z);
                        }
                    }
                }
                return hit.Count;
            }

            void WatchHarbor(float dt)
            {
                if (finished) return;
                elapsed += dt;
                bool allDone = true;
                foreach (Ship s in ships)
                {
                    Track(s, dt);
                    if (s.Boat == null || !s.Boat.Alive) continue;
                    double dx = s.Boat.Pos.X - harborX, dz = s.Boat.Pos.Z - harborZ;
                    if (dx * dx + dz * dz < 45 * 45 && s.Boat.CollidedHorizontally) s.HarborBumps += dt;
                    if (s.Arrivals.Count < s.Expect.Length) allDone = false;
                }
                if (elapsed >= nextReport)
                {
                    nextReport = elapsed + 30;
                    foreach (Ship s in ships) Log((int)elapsed + " s  " + s.Label + " at " + (int)(s.Boat.Pos.X - harborX) + "," + (int)(s.Boat.Pos.Z - harborZ) + " from the harbour  [" + s.Locust.WatchedAttributes.GetString("status", "") + "]  " + s.HarborBumps.ToString("0.0", CultureInfo.InvariantCulture) + " s against something");
                }
                if (!allDone && elapsed < limit) return;
                foreach (Ship s in ships)
                {
                    bool order = s.Arrivals.Count >= s.Expect.Length;
                    for (int i = 0; order && i < s.Expect.Length; i++) if (s.Arrivals[i] != s.Expect[i]) order = false;
                    Check(order, s.Label + ": berths at " + string.Join(" > ", s.Expect), "berthed at: " + (s.Arrivals.Count == 0 ? "nowhere" : string.Join(" > ", s.Arrivals)) + " in " + (int)elapsed + " s");
                    if (s.Berthings == 0) continue;
                    Check(s.WorstSide < 3 && s.WorstOff < 0.5, s.Label + ": lies tied in the berth along the pier", "at worst " + s.WorstSide.ToString("0", CultureInfo.InvariantCulture) + " degrees off and " + s.WorstOff.ToString("0.0", CultureInfo.InvariantCulture) + " blocks from it");
                    Check(s.WorstSnap < 2.5 && s.WorstTurn < 30, s.Label + ": comes in alongside on its own", "at worst moved " + s.WorstSnap.ToString("0.0", CultureInfo.InvariantCulture) + " and turned " + s.WorstTurn.ToString("0", CultureInfo.InvariantCulture) + " degrees when tied");
                    Check(s.MaxOverlap == 0, s.Label + ": lies clear of the pier", s.MaxOverlap + " block(s) of pier under its hull at worst");
                    Check(s.HarborBumps < 3, s.Label + ": comes in without running into the piers or the other boats", s.HarborBumps.ToString("0.0", CultureInfo.InvariantCulture) + " s against something within 45 blocks of the harbour");
                }
                Finish();
            }

            // ---------------------------------------------------------------- a port with several berths

            Ship freed; long freedKey;

            void WatchBerths(float dt)
            {
                if (finished) return;
                elapsed += dt;
                foreach (Ship s in ships) Track(s, dt);
                if (elapsed >= nextReport)
                {
                    nextReport = elapsed + 20;
                    foreach (Ship s in ships) Log((int)elapsed + " s  " + s.Label + " at " + (int)s.Boat.Pos.X + "," + (int)s.Boat.Pos.Z + "  sailed " + (int)s.Travelled + "  [" + s.Locust.WatchedAttributes.GetString("status", "") + "]");
                }
                if (elapsed >= limit) { Fail("the test of the berths finished in time", "stopped in phase " + phase + " after " + (int)elapsed + " s"); Finish(); return; }
                var docked = new List<Ship>(); Ship outside = null;
                foreach (Ship s in ships) { if (s.Arrivals.Count > 0) docked.Add(s); else outside = s; }
                switch (phase)
                {
                    case 0:
                        if (docked.Count < 2 || outside == null) { if (docked.Count == 3) { Fail("the third boat waits off the port", "all three berthed at two docks"); Finish(); } return; }
                        Check(docked[0].ArrivalKeys[0] != docked[1].ArrivalKeys[0], "the first two boats take a dock each", docked[0].Label + " and " + docked[1].Label + ", after " + (int)elapsed + " s");
                        phase = 1;
                        break;
                    case 1:
                        if (outside == null) { Fail("the third boat waits off the port", "it berthed while both docks were taken"); Finish(); return; }
                        bool holding = outside.Locust.WatchedAttributes.GetString("status", "").StartsWith("status-berth");
                        if (!holding) { outside.BerthWaitSince = -1; return; }
                        if (outside.BerthWaitSince < 0) outside.BerthWaitSince = elapsed;
                        if (elapsed - outside.BerthWaitSince < 15) return;
                        double away = double.MaxValue;
                        foreach (Marker dock in system.Markers.FindAll("Bravo")) away = Math.Min(away, Math.Sqrt((dock.X - outside.Boat.Pos.X) * (dock.X - outside.Boat.Pos.X) + (dock.Z - outside.Boat.Pos.Z) * (dock.Z - outside.Boat.Pos.Z)));
                        Check(away > SailorLocustSystem.Config.ArrivalRadius + 3, outside.Label + ": with both docks taken, waits off the port", (int)away + " blocks from the nearest dock, 15 s without moving in, " + system.Berths.HeldCount + " berths held");
                        // one of the two is sent away: its dock must go to the boat that waits
                        freed = docked[0]; freedKey = freed.ArrivalKeys[0];
                        freed.Locust.SetOrders("Alpha; 5");
                        freed.Locust.Pilot.Depart();
                        Log(freed.Label + " is sent to Alpha, " + (int)elapsed + " s in");
                        phase = 2;
                        break;
                    case 2:
                        if (outside != null) return;
                        Ship last = null;
                        foreach (Ship s in ships) if (s != freed && s.ArrivalTimes[0] > elapsed - 2) last = s;
                        foreach (Ship s in ships) if (s != freed && (last == null || s.ArrivalTimes[0] > last.ArrivalTimes[0])) last = s;
                        Check(last.ArrivalKeys[0] == freedKey, last.Label + ": berths at the dock that came free", "after " + (int)elapsed + " s");
                        Finish();
                        break;
                }
            }

            // ---------------------------------------------------------------- sailing out of sight

            bool diagnosed;
            int phase;
            double phaseAt;
            int pendingStrips;

            void Track(Ship s, float dt)
            {
                if (s.Boat == null || !s.Boat.Alive) return;
                Vec3d now = s.Boat.Pos.XYZ;
                s.Travelled += Math.Sqrt((now.X - s.Last.X) * (now.X - s.Last.X) + (now.Z - s.Last.Z) * (now.Z - s.Last.Z));
                s.Last = now;
            }

            int CountOf(string shipId)
            {
                int n = 0;
                foreach (Entity e in sapi.World.LoadedEntities.Values) if (e.Alive && e.WatchedAttributes.GetString(OffscreenFleet.IdAttr, null) == shipId) n++;
                return n;
            }

            void LoadColumns(int x0, int z0, int x1, int z1, Action then)
            {
                pendingStrips++;
                sapi.WorldManager.LoadChunkColumnPriority(x0, z0, x1, z1, new ChunkLoadOptions { KeepLoaded = true, OnLoaded = () => { pendingStrips--; then?.Invoke(); } });
            }

            void WatchOffscreen(float dt)
            {
                if (finished) return;
                elapsed += dt;
                foreach (Ship s in ships) Track(s, dt);
                if (elapsed >= nextReport)
                {
                    nextReport = elapsed + 20;
                    foreach (Ship s in ships)
                    {
                        if (system.Fleet.IsVirtual(s.ShipId)) { foreach (VirtualShip v in system.Fleet.Ships) if (v.ShipId == s.ShipId) Log((int)elapsed + " s  " + s.Label + " out of sight at " + (int)v.X + "," + (int)v.Z + "  " + v.Status + "  " + v.Speed.ToString("0.0", CultureInfo.InvariantCulture) + " b/s measured"); }
                        else if (s.Boat != null) Log((int)elapsed + " s  " + s.Label + " at " + (int)s.Boat.Pos.X + "," + (int)s.Boat.Pos.Z + "  sailed " + (int)s.Travelled + "  [" + s.Locust.WatchedAttributes.GetString("status", "") + "]");
                    }
                }
                if (elapsed >= limit) { Fail("the test of sailing out of sight finished in time", "stopped in phase " + phase + " after " + (int)elapsed + " s"); Finish(); return; }

                switch (phase)
                {
                    case 0:
                        // under way for real, with a speed measured
                        foreach (Ship s in ships) if (s.Arrivals.Count < 1 || s.Locust.Pilot.CruiseSpeed <= 0 || s.Travelled < 160) return;
                        foreach (Ship s in ships) { s.Boat.WatchedAttributes.SetString("selftestTag", s.Label); s.OldBoatId = s.Boat.EntityId; s.Speed = s.Locust.Pilot.CruiseSpeed; s.LeftX = s.Boat.Pos.X; s.LeftZ = s.Boat.Pos.Z; }
                        Log("unloading every chunk under the boats, " + (int)elapsed + " s in");
                        for (int cz = cz0; cz <= cz1; cz++) for (int cx = cx0; cx <= cx1; cx++) sapi.WorldManager.UnloadChunkColumn(cx, cz);
                        phase = 1; phaseAt = elapsed;
                        break;
                    case 1:
                        bool allOut = true;
                        foreach (Ship s in ships) if (!system.Fleet.IsVirtual(s.ShipId)) allOut = false;
                        if (!allOut && elapsed - phaseAt < 25) return;
                        foreach (Ship s in ships)
                        {
                            Check(system.Fleet.IsVirtual(s.ShipId), s.Label + ": leaves the world when its chunks unload", "speed carried over: " + s.Speed.ToString("0.0", CultureInfo.InvariantCulture) + " b/s");
                            Check(sapi.World.GetEntityById(s.OldBoatId) == null, s.Label + ": the boat is no longer an entity of the world");
                            s.Boat = null;
                        }
                        if (!allOut) { Finish(); return; }
                        phase = 2;
                        break;
                    case 2:
                        // each ship reaches its next dock by time alone; then the dock's chunks are loaded and the boat must be there
                        foreach (Ship s in ships)
                        {
                            if (s.VirtualAt != null && !s.Reloaded)
                            {
                                s.Reloaded = true;
                                int mx = s.VirtualAt.X >> 5, mz = s.VirtualAt.Z >> 5;
                                Log("loading the chunks around " + s.VirtualAt.Name + " for " + s.Label);
                                LoadColumns(mx - 4, mz - 4, mx + 4, mz + 4, null);
                            }
                        }
                        foreach (Ship s in ships) if (!s.Back) return;
                        foreach (Ship s in ships)
                        {
                            double gone = Math.Sqrt((s.BackX - s.LeftX) * (s.BackX - s.LeftX) + (s.BackZ - s.LeftZ) * (s.BackZ - s.LeftZ));
                            if (s.VirtualAt != null)
                            {
                                double off = Math.Sqrt((s.BackX - s.VirtualAt.X) * (s.BackX - s.VirtualAt.X) + (s.BackZ - s.VirtualAt.Z) * (s.BackZ - s.VirtualAt.Z));
                                Check(off < 60, s.Label + ": sails to " + s.VirtualAt.Name + " with no chunk loaded and is put back on the water there", (int)off + " blocks from the dock, " + (int)gone + " from where it left the world");
                            }
                            else Check(gone > 40, s.Label + ": sails on with no chunk loaded and is put back on the water when the place it has reached is loaded", (int)gone + " blocks from where it left the world, before its dock");
                            Check(s.BackTag == s.Label, s.Label + ": the boat that comes back is the one that left", "its own mark is on it");
                            Check(s.BackHelm, s.Label + ": the locust is back at the helm, under the same orders", s.BackState);
                        }
                        Log("loading every chunk again, the ones the boats left from included");
                        for (int z = cz0; z <= cz1; z += 6) LoadColumns(cx0, z, cx1, Math.Min(z + 5, cz1), null);
                        phase = 3; phaseAt = elapsed;
                        break;
                    case 3:
                        if (pendingStrips > 0) { phaseAt = elapsed; return; }
                        if (elapsed - phaseAt < 8) return;
                        foreach (Ship s in ships) Check(CountOf(s.ShipId) == 2, s.Label + ": nothing doubled where it left from", CountOf(s.ShipId) + " entities carry the ship's mark (the boat and its locust make 2)");
                        phase = 4;
                        break;
                    case 4:
                        // and it goes on for real: the next dock of its orders
                        foreach (Ship s in ships) if (s.Arrivals.Count <= s.ArrivalsAtBack) return;
                        foreach (Ship s in ships) Pass(s.Label + ": sails on for real to " + s.Arrivals[s.Arrivals.Count - 1] + " after coming back", (int)s.Travelled + " blocks sailed in the world in all");
                        Finish();
                        break;
                }
            }

            void Watch(float dt)
            {
                if (finished) return;
                elapsed += dt;
                bool allDone = true;
                foreach (Ship s in ships)
                {
                    if (s.Boat == null || !s.Boat.Alive) { if (!s.Done) { s.Done = true; s.DoneAt = elapsed; Fail(s.Label + ": the boat is still there"); } continue; }
                    Vec3d now = s.Boat.Pos.XYZ;
                    double moved = Math.Sqrt((now.X - s.Last.X) * (now.X - s.Last.X) + (now.Z - s.Last.Z) * (now.Z - s.Last.Z));
                    s.Travelled += moved; s.Last = now;
                    int sailNow = s.Boat.WatchedAttributes.GetInt("sailPosition");
                    if (sailNow != s.LastSail) { s.SailChanges++; s.LastSail = sailNow; }
                    if (s.Boat.CollidedHorizontally) s.CollidedSeconds += dt;
                    if (s.Locust.Pilot != null)
                    {
                        s.MaxSpeed = Math.Max(s.MaxSpeed, s.Locust.Pilot.Speed);
                        s.MaxStuck = Math.Max(s.MaxStuck, s.Locust.Pilot.StuckCount);
                    }
                    if (!s.Done && s.Arrivals.Count >= s.Expect.Length) { s.Done = true; s.DoneAt = elapsed; }
                    if (!s.Done) allDone = false;
                }
                if (elapsed > 6 && !diagnosed && Environment.GetEnvironmentVariable("SAILORLOCUST_SELFTEST_MODE") == "physics")
                {
                    diagnosed = true;
                    foreach (Ship s in ships)
                    {
                        if (s.Boat == null) continue;
                        var sb = new StringBuilder("physics of " + s.Label + ": state " + s.Boat.State + ", always active " + s.Boat.AlwaysActive + ", tracked " + s.Boat.IsTracked + ", on ground " + s.Boat.OnGround + ", motion y " + s.Boat.Pos.Motion.Y.ToString("0.0000", CultureInfo.InvariantCulture));
                        foreach (EntityBehavior b in s.Boat.SidedProperties.Behaviors)
                        {
                            if (!(b is IPhysicsTickable t)) continue;
                            sb.Append("; " + b.GetType().Name + " ticking " + t.Ticking);
                            double y0 = s.Boat.Pos.Y;
                            for (int i = 0; i < 30; i++) t.OnPhysicsTick(1 / 30f);
                            sb.Append(", 30 ticks by hand move y " + y0.ToString("0.00", CultureInfo.InvariantCulture) + " > " + s.Boat.Pos.Y.ToString("0.00", CultureInfo.InvariantCulture) + ", swimming " + s.Boat.Swimming);
                        }
                        Log(sb.ToString());
                    }
                }
                if (elapsed >= nextReport)
                {
                    nextReport = elapsed + 20;
                    foreach (Ship s in ships)
                    {
                        if (s.Boat == null) continue;
                        Pilot p = s.Locust.Pilot;
                        Log((int)elapsed + " s  " + s.Label + " at " + (int)s.Boat.Pos.X + "," + (int)s.Boat.Pos.Z + " y " + s.Boat.Pos.Y.ToString("0.00", CultureInfo.InvariantCulture) + "  " + (p == null ? 0 : p.Speed).ToString("0.0", CultureInfo.InvariantCulture) + " b/s  sailed " + (int)s.Travelled + "  stuck " + (p == null ? 0 : p.StuckCount) + "  swimming " + s.Boat.Swimming + "  boat " + (s.Locust.Boat != null) + "  [" + s.Locust.WatchedAttributes.GetString("status", "") + "] " + (p == null ? "" : p.RouteHow));
                    }
                }
                if (allDone || elapsed >= limit) Summary();
            }

            void Summary()
            {
                RouteChecks();
                foreach (Ship s in ships)
                {
                    string got = string.Join(" > ", s.Arrivals), want = string.Join(" > ", s.Expect);
                    bool order = s.Arrivals.Count >= s.Expect.Length;
                    for (int i = 0; order && i < s.Expect.Length; i++) if (s.Arrivals[i] != s.Expect[i]) order = false;
                    Check(order, s.Label + ": sails " + want + " with nobody at the helm", "arrived at: " + (got.Length == 0 ? "nowhere" : got) + "; " + (int)s.Travelled + " blocks in " + (int)(s.Done ? s.DoneAt : elapsed) + " s, top speed " + s.MaxSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " b/s, cruise " + (s.Locust.Pilot?.CruiseSpeed ?? 0).ToString("0.0", CultureInfo.InvariantCulture) + " b/s; last state " + s.Locust.WatchedAttributes.GetString("status", ""));
                    if (s.Orders.StartsWith("Alpha; 3\nMid")) Check(s.BuoysPassed >= 1, s.Label + ": passes the buoy without stopping", s.BuoysPassed + " time(s)");
                    if (s.Berthings > 0)
                    {
                        Check(s.WorstSide < 3 && s.WorstOff < 0.5, s.Label + ": lies moored in the berth, alongside", s.Berthings + " berthing(s), at worst " + s.WorstSide.ToString("0", CultureInfo.InvariantCulture) + " degrees off and " + s.WorstOff.ToString("0.0", CultureInfo.InvariantCulture) + " blocks from the middle of the berth");
                        Check(s.WorstSnap < 2.5 && s.WorstTurn < 30, s.Label + ": comes in alongside on its own (the mooring is a small last pull)", "at worst moved " + s.WorstSnap.ToString("0.0", CultureInfo.InvariantCulture) + " blocks and turned " + s.WorstTurn.ToString("0", CultureInfo.InvariantCulture) + " degrees when tied");
                    }
                    if (s.SailChanges > 0) Check(s.SailChanges <= 6 + 8 * Math.Max(1, s.Arrivals.Count), s.Label + ": does not fuss with the sail", s.SailChanges + " change(s) of sail over " + s.Arrivals.Count + " arrival(s) and " + (int)s.Travelled + " blocks");
                    Check(s.CollidedSeconds < 15, s.Label + ": keeps off the land", s.CollidedSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s pushed against something solid, backed off " + s.MaxStuck + " time(s) at most in a row");
                }
                Finish();
            }

            /// <summary>The sea routes for maps: the stretches of the orders, their ways, and the filter by what a player knows.</summary>
            void RouteChecks()
            {
                if (alpha == null || bravo == null) return;
                List<RouteShare.Segment> all = null;
                for (int i = 0; i < 4; i++) all = system.Routes.Segments();
                var keys = new List<string>();
                foreach (RouteShare.Segment seg in all) keys.Add(seg.Key);
                string ab = RouteShare.KeyOf(alpha, bravo), ba = RouteShare.KeyOf(bravo, alpha);
                int expect = mid != null ? 5 : 2;
                Check(all.Count == expect && keys.Contains(ab) && keys.Contains(ba), "the sea routes are the stretches of the orders, each once", all.Count + " stretch(es) from " + system.Routes.LineCount + " ship(s): " + string.Join(" | ", keys));
                RouteShare.Segment one = all.Find(seg => seg.Key == ab);
                if (one != null)
                {
                    int n = one.Way.Length;
                    bool ends = Math.Abs(one.Way[0] - (alpha.X + 0.5)) < 0.01 && Math.Abs(one.Way[1] - (alpha.Z + 0.5)) < 0.01 && Math.Abs(one.Way[n - 2] - (bravo.X + 0.5)) < 0.01 && Math.Abs(one.Way[n - 1] - (bravo.Z + 0.5)) < 0.01;
                    double len = 0; for (int k = 2; k < n; k += 2) len += Math.Sqrt((one.Way[k] - one.Way[k - 2]) * (one.Way[k] - one.Way[k - 2]) + (one.Way[k + 1] - one.Way[k - 1]) * (one.Way[k + 1] - one.Way[k - 1]));
                    Check(ends && n >= 6, "the way of Alpha > Bravo runs from one dock to the other around the land", (n / 2) + " points, " + (int)len + " blocks");
                }
                var both = new HashSet<long> { Berths.KeyOf(alpha), Berths.KeyOf(bravo) };
                var onlyAlpha = new HashSet<long> { Berths.KeyOf(alpha) };
                int seenBoth = RouteShare.Known(all, both).Count, seenOne = RouteShare.Known(all, onlyAlpha).Count;
                Check(seenBoth == 2 && seenOne == 0, "a player is sent only the stretches whose two ends they know", "knowing Alpha and Bravo: " + seenBoth + "; knowing only Alpha: " + seenOne);
                var packet = new RoutesPacket();
                foreach (RouteShare.Segment seg in all) { packet.Keys.Add(seg.Key); packet.Counts.Add(seg.Way.Length); packet.Coords.AddRange(seg.Way); }
                using (var stream = new System.IO.MemoryStream())
                {
                    ProtoBuf.Serializer.Serialize(stream, packet);
                    stream.Position = 0;
                    RoutesPacket back = ProtoBuf.Serializer.Deserialize<RoutesPacket>(stream);
                    Check(back.Keys.Count == packet.Keys.Count && back.Coords.Count == packet.Coords.Count, "the routes packet goes over the network whole", stream.Length + " bytes for " + back.Keys.Count + " stretch(es)");
                }
            }

            void Finish()
            {
                if (finished) return;
                finished = true;
                if (Pilot.CostTicks > 0) Log("cost: " + Pilot.CostTicks + " pilot ticks, " + (Pilot.CostMs / Pilot.CostTicks).ToString("0.000", CultureInfo.InvariantCulture) + " ms each on average, " + Pilot.CostMaxMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms the longest; " + (Pilot.CostMs / Math.Max(1, elapsed) / 10).ToString("0.00", CultureInfo.InvariantCulture) + "% of one core over " + (int)elapsed + " s, all boats together");
                Log("DONE: " + passed + " passed, " + failed + " failed");
                sapi.Event.RegisterCallback(_ => sapi.Server.ShutDown(), 1500);
            }
        }
    }
}
