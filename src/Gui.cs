using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SailorLocust
{
    /// <summary>The name of a dock or a buoy.</summary>
    public class GuiDialogMarkerName : GuiDialog
    {
        readonly BlockPos pos;

        public override string ToggleKeyCombinationCode { get { return null; } }

        public GuiDialogMarkerName(ICoreClientAPI capi, BlockPos pos, string name, bool dock, bool smallOnly = false, int role = 0) : base(capi)
        {
            this.pos = pos.Copy();
            ElementBounds hint = ElementBounds.Fixed(0, 30, 340, 22);
            ElementBounds input = ElementBounds.Fixed(0, 58, 340, 30);
            ElementBounds save = ElementBounds.Fixed(240, 100, 100, 28);
            ElementBounds turn = ElementBounds.Fixed(0, 100, 150, 28);
            ElementBounds small = ElementBounds.Fixed(0, 136, 220, 28);
            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bg.BothSizing = ElementSizing.FitToChildren;
            ElementBounds roleb = ElementBounds.Fixed(0, 136, 340, 28);
            if (dock) bg.WithChildren(hint, input, save, turn, small); else bg.WithChildren(hint, input, save, roleb);
            ElementBounds dialog = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            SingleComposer = capi.Gui.CreateCompo("sailorlocust-marker", dialog)
                .AddShadedDialogBG(bg)
                .AddDialogTitleBar(SailorLocustSystem.T(dock ? "dock-title" : "buoy-title"), () => TryClose())
                .BeginChildElements(bg)
                    .AddStaticText(SailorLocustSystem.T("marker-hint"), CairoFont.WhiteSmallText(), hint)
                    .AddTextInput(input, null, CairoFont.TextInput(), "name")
                    .AddSmallButton(SailorLocustSystem.T("save"), OnSave, save)
                    .AddIf(dock)
                        .AddSmallButton(SailorLocustSystem.T("dock-turn"), OnTurn, turn)
                        .AddSmallButton(SailorLocustSystem.T(smallOnly ? "dock-small-on" : "dock-small-off"), OnSmall, small)
                    .EndIf()
                    .AddIf(!dock)
                        .AddSmallButton(SailorLocustSystem.T("buoy-role-" + Math.Max(0, Math.Min(3, role))), OnRole, roleb)
                    .EndIf()
                .EndChildElements()
                .Compose();
            SingleComposer.GetTextInput("name").SetValue(name ?? "");
            given = name ?? "";
        }

        string given;
        bool sent;

        /// <summary>A name typed is kept however the sheet is closed (Escape, the cross, clicking away), not only by Save.</summary>
        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            if (sent) return;
            string value = BlockEntitySailorMarker.CleanName(SingleComposer?.GetTextInput("name")?.GetText());
            if (!string.IsNullOrEmpty(value) && value != given) { sent = true; capi.Network.SendBlockEntityPacket(pos, BlockEntitySailorMarker.PacketRename, Encoding.UTF8.GetBytes(value)); }
        }

        public override void OnKeyDown(KeyEvent args)
        {
            if (args.KeyCode == (int)GlKeys.Enter || args.KeyCode == (int)GlKeys.KeypadEnter) { OnSave(); args.Handled = true; return; }
            base.OnKeyDown(args);
        }

        /// <summary>Turns the berth one step round the post; the outline on the water follows.</summary>
        bool OnTurn()
        {
            capi.Network.SendBlockEntityPacket(pos, BlockEntitySailorMarker.PacketTurn, null);
            return true;
        }

        /// <summary>Keeps the dock for small boats, or opens it to all; the sheet closes so the next look shows the new state.</summary>
        /// <summary>A buoy of the route, the waiting area of the port of its name, or that port's way out; the sheet closes to show the new state.</summary>
        bool OnRole()
        {
            capi.Network.SendBlockEntityPacket(pos, BlockEntitySailorMarker.PacketRole, null);
            TryClose();
            return true;
        }

        bool OnSmall()
        {
            capi.Network.SendBlockEntityPacket(pos, BlockEntitySailorMarker.PacketSmall, null);
            TryClose();
            return true;
        }

        bool OnSave()
        {
            string value = BlockEntitySailorMarker.CleanName(SingleComposer.GetTextInput("name").GetText());
            if (value.Length > 0 && value != given) capi.Network.SendBlockEntityPacket(pos, BlockEntitySailorMarker.PacketRename, Encoding.UTF8.GetBytes(value));
            sent = true;
            TryClose();
            return true;
        }
    }

    /// <summary>
    /// The sailing orders of one sailor locust, made by clicking: the docks and buoys it knows are buttons that add a
    /// stop; each stop of the route can go up, go down or be taken out, and a dock has its seconds of waiting. Every
    /// change is sent at once: there is nothing to type but the seconds, and nothing to save.
    /// </summary>
    public class GuiDialogSailingOrders : GuiDialog
    {
        const int MaxStops = 12, MaxKnown = 12;
        /// <summary>The sheet that is open, so the key that opens it also closes it.</summary>
        public static GuiDialogSailingOrders Current;

        readonly EntitySailorLocust locust;
        readonly List<Stop> stops;
        readonly List<string[]> known = new List<string[]>();
        long listener;
        string shownStatus, shownKnown; bool shownRunning;
        bool composing;

        public override string ToggleKeyCombinationCode { get { return null; } }

        public GuiDialogSailingOrders(ICoreClientAPI capi, EntitySailorLocust locust) : base(capi)
        {
            this.locust = locust;
            stops = Orders.Parse(locust.WatchedAttributes.GetString("orders", ""));
            ReadKnown();
            Compose();
        }

        void ReadKnown()
        {
            shownKnown = locust.WatchedAttributes.GetString("known", "");
            known.Clear();
            foreach (string line in shownKnown.Split('\n'))
            {
                string[] parts = line.Split('|');
                if (parts.Length >= 3) known.Add(parts);
            }
        }

        /// <summary>A name the locust does not know is shown as a dock: it may be far away.</summary>
        bool IsBuoy(string name)
        {
            foreach (string[] k in known) if (string.Equals(k[1], name, StringComparison.OrdinalIgnoreCase)) return k[0] != "D";
            return false;
        }

        void Compose()
        {
            composing = true;
            const int W = 470;
            var all = new List<ElementBounds>();
            var build = new List<Action<GuiComposer>>();
            System.Func<double, double, double, double, ElementBounds> at = (x, y, w, h) => { ElementBounds b = ElementBounds.Fixed(x, y, w, h); all.Add(b); return b; };
            CairoFont small = CairoFont.WhiteSmallText(), detail = CairoFont.WhiteDetailText();

            ElementBounds status = at(0, 30, W, 26);
            build.Add(c => c.AddDynamicText("", CairoFont.WhiteSmallishText(), status, "status"));
            double y = 64;
            ElementBounds routeTitle = at(0, y, W, 20);
            build.Add(c => c.AddStaticText(SailorLocustSystem.T("orders-route"), small, routeTitle));
            y += 26;
            if (stops.Count == 0)
            {
                ElementBounds empty = at(0, y, W, 22);
                build.Add(c => c.AddStaticText(SailorLocustSystem.T("orders-empty"), detail, empty));
                y += 28;
            }
            for (int i = 0; i < stops.Count && i < MaxStops; i++)
            {
                int row = i;
                Stop stop = stops[i];
                bool buoy = IsBuoy(stop.Name);
                ElementBounds name = at(0, y + 4, 196, 22);
                build.Add(c => c.AddStaticText((row + 1) + ". " + stop.Name, small, name));
                if (buoy)
                {
                    ElementBounds passes = at(200, y + 4, 120, 22);
                    build.Add(c => c.AddStaticText(SailorLocustSystem.T("orders-passes"), detail, passes));
                }
                else
                {
                    ElementBounds waitLabel = at(200, y + 4, 62, 22);
                    ElementBounds wait = at(262, y, 58, 26);
                    build.Add(c => c.AddStaticText(SailorLocustSystem.T("orders-wait"), detail, waitLabel));
                    build.Add(c => c.AddNumberInput(wait, text => OnWait(row, text), CairoFont.WhiteDetailText(), "wait" + row));
                }
                ElementBounds up = at(330, y, 44, 26), down = at(378, y, 44, 26), remove = at(426, y, 44, 26);
                build.Add(c => c.AddSmallButton(SailorLocustSystem.T("orders-up"), () => Move(row, -1), up));
                build.Add(c => c.AddSmallButton(SailorLocustSystem.T("orders-down"), () => Move(row, 1), down));
                build.Add(c => c.AddSmallButton("X", () => Remove(row), remove));
                y += 30;
            }

            y += 10;
            ElementBounds addTitle = at(0, y, W, 20);
            build.Add(c => c.AddStaticText(SailorLocustSystem.T(known.Count == 0 ? "orders-known-none" : "orders-add"), small, addTitle));
            y += 26;
            for (int i = 0; i < known.Count && i < MaxKnown; i++)
            {
                string[] k = known[i];
                ElementBounds button = at(i % 2 == 0 ? 0 : W / 2 + 4, y, W / 2 - 4, 26);
                string label = k[1] + "  (" + SailorLocustSystem.T(k[0] == "D" ? "kind-dock" : "kind-buoy") + (k[0] == "D" && k.Length > 3 && k[3] != "1" ? " x" + k[3] : "") + ", " + k[2] + " m)";
                build.Add(c => c.AddSmallButton(label, () => Add(k[1]), button));
                if (i % 2 == 1 || i == Math.Min(known.Count, MaxKnown) - 1) y += 30;
            }

            y += 12;
            shownRunning = locust.WatchedAttributes.GetBool("running");
            ElementBounds run = at(0, y, 170, 28), pickup = at(W - 150, y, 150, 28);
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
            for (int i = 0; i < stops.Count && i < MaxStops; i++)
            {
                if (IsBuoy(stops[i].Name)) continue;
                int seconds = stops[i].Wait >= 0 ? stops[i].Wait : 60;
                SingleComposer.GetNumberInput("wait" + i)?.SetValue(seconds.ToString());
            }
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

        void Refresh(float dt)
        {
            if (composing) return;
            if (!locust.Alive || capi.World.GetEntityById(locust.EntityId) == null) { TryClose(); return; }
            // the list of docks and buoys comes a moment after the sheet opens; the button's words change with the state
            if (locust.WatchedAttributes.GetString("known", "") != shownKnown) { ReadKnown(); Compose(); return; }
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

        bool Add(string name)
        {
            if (stops.Count >= MaxStops) return true;
            stops.Add(new Stop { Name = name, Wait = 60 });
            Send(); Compose();
            return true;
        }

        bool Remove(int row)
        {
            if (row < 0 || row >= stops.Count) return true;
            stops.RemoveAt(row);
            Send(); Compose();
            return true;
        }

        bool Move(int row, int by)
        {
            int to = row + by;
            if (row < 0 || row >= stops.Count || to < 0 || to >= stops.Count) return true;
            Stop moved = stops[row];
            stops[row] = stops[to];
            stops[to] = moved;
            Send(); Compose();
            return true;
        }

        void OnWait(int row, string text)
        {
            if (composing || row < 0 || row >= stops.Count) return;
            int seconds;
            if (!int.TryParse((text ?? "").Trim(), out seconds)) return;
            seconds = Math.Max(0, Math.Min(36000, seconds));
            if (stops[row].Wait == seconds) return;
            stops[row].Wait = seconds;
            Send();
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
}