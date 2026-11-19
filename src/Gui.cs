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

}
