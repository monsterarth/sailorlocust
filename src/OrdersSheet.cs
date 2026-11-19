using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SailorLocust
{
    /// <summary>A dock or a buoy the player may add to a route: what the server sends for the sheet and the map.</summary>
    public class KnownStop
    {
        public bool Dock;
        public string Name;
        public double X, Z;
        /// <summary>Berths of the port (docks of that name).</summary>
        public int Berths = 1;

        public string KindText { get { return SailorLocustSystem.T(Dock ? "kind-dock" : "kind-buoy") + (Dock && Berths > 1 ? " x" + Berths : ""); } }

        /// <summary>"D|name|x|z|berths" lines, as the server writes them.</summary>
        public static List<KnownStop> Parse(string text)
        {
            var list = new List<KnownStop>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string line in text.Split('\n'))
            {
                string[] p = line.Split('|');
                if (p.Length < 4) continue;
                double x, z;
                if (!double.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)) continue;
                if (!double.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z)) continue;
                int berths = 1;
                if (p.Length > 4) int.TryParse(p[4], out berths);
                list.Add(new KnownStop { Dock = p[0] == "D", Name = p[1], X = x, Z = z, Berths = Math.Max(1, berths) });
            }
            return list;
        }
    }

    /// <summary>
    /// The sailing orders of one sailor locust, laid out as a timetable: on the left the stops in the order sailed, with
    /// add, remove, up and down; on the right the stop chosen, what it is and when the boat leaves it. A stop is added
    /// from the list of docks and buoys the player knows, or by clicking one on the world map. Every change is sent at
    /// once: there is nothing to save.
    /// </summary>
    public class GuiDialogSailingOrders : GuiDialog
    {
        const int MaxStops = 12;
        /// <summary>The sheet that is open, so the key that opens it also closes it, and the map knows whom to add a stop to.</summary>
        public static GuiDialogSailingOrders Current;

        readonly EntitySailorLocust locust;
        readonly List<Stop> stops;
        long listener;
        string shownStatus; bool shownRunning;
        bool composing;
        int selected = -1;

        public EntitySailorLocust Locust { get { return locust; } }

        public override string ToggleKeyCombinationCode { get { return null; } }

        public GuiDialogSailingOrders(ICoreClientAPI capi, EntitySailorLocust locust) : base(capi)
        {
            this.locust = locust;
            stops = Orders.Parse(locust.WatchedAttributes.GetString("orders", ""));
            if (stops.Count > 0) selected = 0;
            // the docks and buoys this player knows, with where they are: for the list and for the map
            capi.Network.SendEntityPacket(locust.EntityId, EntitySailorLocust.PacketStops, null);
            Compose();
        }

        KnownStop Find(string name)
        {
            foreach (KnownStop k in StopsMapLayer.Known) if (string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }

        /// <summary>A name not among the known ones is taken for a dock: it may be far away.</summary>
        bool IsBuoy(string name) { KnownStop k = Find(name); return k != null && !k.Dock; }

        void Compose()
        {
            composing = true;
            const int L = 330, R = 360, RW = 330, W = R + RW;
            var all = new List<ElementBounds>();
            var build = new List<Action<GuiComposer>>();
            System.Func<double, double, double, double, ElementBounds> at = (x, y, w, h) => { ElementBounds b = ElementBounds.Fixed(x, y, w, h); all.Add(b); return b; };
            CairoFont small = CairoFont.WhiteSmallText(), detail = CairoFont.WhiteDetailText(), big = CairoFont.WhiteSmallishText();

            ElementBounds status = at(0, 30, W, 26);
            build.Add(c => c.AddDynamicText("", big, status, "status"));

            // ---- left: the stops
            double top = 66;
            ElementBounds leftBox = at(0, top, L, 380), rightBox = at(R, top, RW, 380);
            build.Add(c => c.AddInset(leftBox, 2));
            build.Add(c => c.AddInset(rightBox, 2));
            ElementBounds routeTitle = at(10, top + 8, L - 20, 22);
            build.Add(c => c.AddStaticText(SailorLocustSystem.T("sheet-stops"), small, routeTitle));
            double y = top + 36;
            if (stops.Count == 0)
            {
                ElementBounds empty = at(10, y, L - 20, 44);
                build.Add(c => c.AddStaticText(SailorLocustSystem.T("sheet-empty"), detail, empty));
            }
            for (int i = 0; i < stops.Count && i < MaxStops; i++)
            {
                int row = i;
                Stop stop = stops[i];
                bool buoy = IsBuoy(stop.Name);
                string text = (row + 1) + ". " + stop.Name + "   " + (buoy ? SailorLocustSystem.T("sheet-passes") : SailorLocustSystem.T("sheet-waits", stop.Wait >= 0 ? stop.Wait : 60));
                ElementBounds b = at(10, y, L - 20, 24);
                bool isSel = row == selected;
                build.Add(c => c.AddToggleButton(text, small, on => Select(row), b, "stop" + row));
                y += 26;
            }
            double by = top + 380 - 40;
            ElementBounds add = at(10, by, 70, 30), del = at(88, by, 70, 30), up = at(166, by, 70, 30), down = at(244, by, 70, 30);
            build.Add(c => c.AddSmallButton("+", OnAdd, add));
            build.Add(c => c.AddSmallButton("−", OnRemove, del));
            build.Add(c => c.AddSmallButton("▲", () => Move(-1), up));
            build.Add(c => c.AddSmallButton("▼", () => Move(1), down));

            // ---- right: the stop chosen
            if (selected < 0 || selected >= stops.Count)
            {
                ElementBounds none = at(R + 12, top + 10, RW - 24, 30);
                build.Add(c => c.AddStaticText(SailorLocustSystem.T("sheet-none"), big, none));
                ElementBounds hint = at(R + 12, top + 50, RW - 24, 120);
                build.Add(c => c.AddStaticText(SailorLocustSystem.T("sheet-none-hint"), detail, hint));
            }
            else
            {
                Stop stop = stops[selected];
                KnownStop k = Find(stop.Name);
                bool buoy = k != null && !k.Dock;
                ElementBounds name = at(R + 12, top + 10, RW - 24, 30);
                build.Add(c => c.AddStaticText(stop.Name, big, name));
                string what = k == null ? SailorLocustSystem.T("sheet-unknown")
                    : buoy ? SailorLocustSystem.T("sheet-buoy")
                    : (k.Berths > 1 ? SailorLocustSystem.T("sheet-port", k.Berths) : SailorLocustSystem.T("sheet-dock"));
                if (k != null)
                {
                    Vec3d me = capi.World.Player?.Entity?.Pos?.XYZ;
                    if (me != null) what += "\n" + SailorLocustSystem.T("sheet-where", (int)Math.Sqrt((k.X - me.X) * (k.X - me.X) + (k.Z - me.Z) * (k.Z - me.Z)));
                }
                ElementBounds kind = at(R + 12, top + 46, RW - 24, 60);
                build.Add(c => c.AddStaticText(what, detail, kind));
                ElementBounds leaveTitle = at(R + 12, top + 120, RW - 24, 22);
                build.Add(c => c.AddStaticText(SailorLocustSystem.T("sheet-leave"), small, leaveTitle));
                if (buoy)
                {
                    ElementBounds passes = at(R + 12, top + 148, RW - 24, 44);
                    build.Add(c => c.AddStaticText(SailorLocustSystem.T("sheet-leave-buoy"), detail, passes));
                }
                else
                {
                    ElementBounds waitLabel = at(R + 12, top + 152, 170, 22), wait = at(R + 186, top + 148, 80, 28);
                    build.Add(c => c.AddStaticText(SailorLocustSystem.T("sheet-leave-after"), detail, waitLabel));
                    build.Add(c => c.AddNumberInput(wait, OnWait, CairoFont.WhiteDetailText(), "wait"));
                    ElementBounds note = at(R + 12, top + 186, RW - 24, 70);
                    build.Add(c => c.AddStaticText(SailorLocustSystem.T("sheet-leave-note"), detail, note));
                }
            }

            // ---- below: the map, sailing, taking it back
            double fy = top + 392;
            shownRunning = locust.WatchedAttributes.GetBool("running");
            ElementBounds map = at(0, fy, 200, 30), run = at(R, fy, 160, 30), pickup = at(W - 160, fy, 160, 30);
            build.Add(c => c.AddSmallButton(SailorLocustSystem.T("sheet-map"), OnMap, map));
            build.Add(c => c.AddSmallButton(SailorLocustSystem.T(shownRunning ? "orders-stop" : "orders-start"), OnToggle, run));
            build.Add(c => c.AddSmallButton(SailorLocustSystem.T("orders-pickup"), OnPickup, pickup));

            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bg.BothSizing = ElementSizing.FitToChildren;
            bg.WithChildren(all.ToArray());
            ElementBounds dialog = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            SingleComposer?.Dispose();
            GuiComposer composer = capi.Gui.CreateCompo("sailorlocust-orders", dialog)
                .AddShadedDialogBG(bg)
                .AddDialogTitleBar(SailorLocustSystem.T("orders-title"), () => TryClose())
                .BeginChildElements(bg);
            foreach (Action<GuiComposer> step in build) step(composer);
            SingleComposer = composer.EndChildElements().Compose();
            for (int i = 0; i < stops.Count && i < MaxStops; i++) SingleComposer.GetToggleButton("stop" + i)?.SetValue(i == selected);
            if (selected >= 0 && selected < stops.Count && !IsBuoy(stops[selected].Name))
                SingleComposer.GetNumberInput("wait")?.SetValue((stops[selected].Wait >= 0 ? stops[selected].Wait : 60).ToString());
            shownStatus = null;
            composing = false;
            Refresh(0);
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            Current = this;
            listener = capi.Event.RegisterGameTickListener(Refresh, 300);
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            if (Current == this) Current = null;
            capi.Event.UnregisterGameTickListener(listener);
        }

        /// <summary>The list of docks and buoys came from the server: the kinds and places shown may change.</summary>
        public void KnownChanged() { if (!composing) Compose(); }

        void Refresh(float dt)
        {
            if (composing) return;
            if (!locust.Alive || capi.World.GetEntityById(locust.EntityId) == null) { TryClose(); return; }
            if (locust.WatchedAttributes.GetBool("running") != shownRunning) { Compose(); return; }
            string status = locust.StatusText();
            if (status != shownStatus) { shownStatus = status; SingleComposer.GetDynamicText("status").SetNewText(status); }
        }

        void Send()
        {
            var sb = new StringBuilder();
            foreach (Stop stop in stops)
            {
                sb.Append(stop.Name);
                if (!IsBuoy(stop.Name)) sb.Append("; ").Append(stop.Wait >= 0 ? stop.Wait : 60);
                sb.Append('\n');
            }
            capi.Network.SendEntityPacket(locust.EntityId, EntitySailorLocust.PacketSaveOrders, Encoding.UTF8.GetBytes(sb.ToString()));
        }

        void Select(int row)
        {
            if (composing) return;
            selected = row;
            Compose();
        }

        /// <summary>A stop added after the one chosen (or at the end), and chosen.</summary>
        public bool Add(string name)
        {
            if (stops.Count >= MaxStops) { capi.TriggerIngameError(this, "fullroute", SailorLocustSystem.T("sheet-full", MaxStops)); return true; }
            int at = selected >= 0 && selected < stops.Count ? selected + 1 : stops.Count;
            stops.Insert(at, new Stop { Name = name, Wait = 60 });
            selected = at;
            Send(); Compose();
            return true;
        }

        bool OnAdd()
        {
            new GuiDialogAddStop(capi, this).TryOpen();
            return true;
        }

        bool OnRemove()
        {
            if (selected < 0 || selected >= stops.Count) return true;
            stops.RemoveAt(selected);
            if (selected >= stops.Count) selected = stops.Count - 1;
            Send(); Compose();
            return true;
        }

        bool Move(int by)
        {
            int to = selected + by;
            if (selected < 0 || selected >= stops.Count || to < 0 || to >= stops.Count) return true;
            Stop moved = stops[selected];
            stops[selected] = stops[to];
            stops[to] = moved;
            selected = to;
            Send(); Compose();
            return true;
        }

        void OnWait(string text)
        {
            if (composing || selected < 0 || selected >= stops.Count) return;
            int seconds;
            if (!int.TryParse((text ?? "").Trim(), out seconds)) return;
            seconds = Math.Max(0, Math.Min(36000, seconds));
            if (stops[selected].Wait == seconds) return;
            stops[selected].Wait = seconds;
            Send();
            // the line on the left shows the seconds too
            SingleComposer.GetToggleButton("stop" + selected)?.SetValue(true);
        }

        /// <summary>The world map, with the docks and buoys this player knows: a click on one adds it to this route.</summary>
        bool OnMap()
        {
            StopsMapLayer.PickFor = this;
            WorldMapManager manager = capi.ModLoader.GetModSystem<WorldMapManager>();
            if (manager == null) return true;
            if (!manager.IsOpened) manager.ToggleMap(EnumDialogType.Dialog);
            capi.ShowChatMessage(SailorLocustSystem.T("sheet-map-hint"));
            return true;
        }

        bool OnToggle()
        {
            capi.Network.SendEntityPacket(locust.EntityId, EntitySailorLocust.PacketToggle, null);
            return true;
        }

        bool OnPickup()
        {
            capi.Network.SendEntityPacket(locust.EntityId, EntitySailorLocust.PacketPickup, null);
            TryClose();
            return true;
        }
    }

    /// <summary>The docks and buoys the player knows, nearest first, to add one to the route; and the way to the map.</summary>
    public class GuiDialogAddStop : GuiDialog
    {
        const int Shown = 16;
        readonly GuiDialogSailingOrders sheet;

        public override string ToggleKeyCombinationCode { get { return null; } }

        public GuiDialogAddStop(ICoreClientAPI capi, GuiDialogSailingOrders sheet) : base(capi)
        {
            this.sheet = sheet;
            Compose();
        }

        void Compose()
        {
            const int W = 380;
            var all = new List<ElementBounds>();
            var build = new List<Action<GuiComposer>>();
            System.Func<double, double, double, double, ElementBounds> at = (x, y, w, h) => { ElementBounds b = ElementBounds.Fixed(x, y, w, h); all.Add(b); return b; };
            ElementBounds title = at(0, 30, W, 22);
            build.Add(c => c.AddStaticText(SailorLocustSystem.T("add-title"), CairoFont.WhiteSmallText(), title));
            double y = 60;
            Vec3d me = capi.World.Player?.Entity?.Pos?.XYZ ?? new Vec3d();
            var list = new List<KnownStop>(StopsMapLayer.Known);
            list.Sort((a, b) => ((a.X - me.X) * (a.X - me.X) + (a.Z - me.Z) * (a.Z - me.Z)).CompareTo((b.X - me.X) * (b.X - me.X) + (b.Z - me.Z) * (b.Z - me.Z)));
            if (list.Count == 0)
            {
                ElementBounds none = at(0, y, W, 44);
                build.Add(c => c.AddStaticText(SailorLocustSystem.T(StopsMapLayer.Received ? "orders-known-none" : "add-loading"), CairoFont.WhiteDetailText(), none));
                y += 50;
            }
            for (int i = 0; i < list.Count && i < Shown; i++)
            {
                KnownStop k = list[i];
                int d = (int)Math.Sqrt((k.X - me.X) * (k.X - me.X) + (k.Z - me.Z) * (k.Z - me.Z));
                string label = k.Name + "   (" + k.KindText + ", " + d + " m)";
                ElementBounds b = at(0, y, W, 26);
                build.Add(c => c.AddSmallButton(label, () => { sheet.Add(k.Name); TryClose(); return true; }, b));
                y += 30;
            }
            y += 6;
            ElementBounds map = at(0, y, 180, 28), close = at(W - 120, y, 120, 28);
            build.Add(c => c.AddSmallButton(SailorLocustSystem.T("sheet-map"), () => { TryClose(); StopsMapLayer.PickFor = sheet; WorldMapManager m = capi.ModLoader.GetModSystem<WorldMapManager>(); if (m != null && !m.IsOpened) m.ToggleMap(EnumDialogType.Dialog); capi.ShowChatMessage(SailorLocustSystem.T("sheet-map-hint")); return true; }, map));
            build.Add(c => c.AddSmallButton(SailorLocustSystem.T("add-close"), () => TryClose(), close));

            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bg.BothSizing = ElementSizing.FitToChildren;
            bg.WithChildren(all.ToArray());
            ElementBounds dialog = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            GuiComposer composer = capi.Gui.CreateCompo("sailorlocust-addstop", dialog)
                .AddShadedDialogBG(bg)
                .AddDialogTitleBar(SailorLocustSystem.T("add-dialog"), () => TryClose())
                .BeginChildElements(bg);
            foreach (Action<GuiComposer> step in build) step(composer);
            SingleComposer = composer.EndChildElements().Compose();
        }
    }
}
