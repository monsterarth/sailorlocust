using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SailorLocust
{
    /// <summary>
    /// The docks and buoys the player knows, on the game's world map, while a sailing orders sheet is open: a click on
    /// one adds it to that route. The list is what the server sent for the sheet (docks and buoys the player has been
    /// near, and those near the boat).
    /// </summary>
    public class StopsMapLayer : MapLayer
    {
        public const string Code = "sailorlocust-stops";
        const string DockIcon = "sailorlocust:textures/icons/dock.svg", BuoyIcon = "sailorlocust:textures/icons/buoy.svg";
        static readonly int DockColor = unchecked((int)0xFF3F7FD0), BuoyColor = unchecked((int)0xFFE0702A), InRoute = unchecked((int)0xFF3FC060);

        public static List<KnownStop> Known = new List<KnownStop>();
        public static bool Received;
        /// <summary>The sheet a click on the map adds a stop to; null when none is open.</summary>
        public static GuiDialogSailingOrders PickFor;

        readonly ICoreClientAPI capi;
        readonly Dictionary<string, LoadedTexture> icons = new Dictionary<string, LoadedTexture>();
        readonly Vec3d world = new Vec3d();
        Vec2f view = new Vec2f();
        bool failed;

        public StopsMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
        {
            capi = api as ICoreClientAPI;
            ZIndex = 4;
        }

        public override string Title { get { return "Sailor Locust: docks and buoys"; } }
        public override string LayerGroupCode { get { return Code; } }
        public override EnumMapAppSide DataSide { get { return EnumMapAppSide.Client; } }

        public static void Register(ICoreClientAPI capi)
        {
            try { capi.ModLoader.GetModSystem<WorldMapManager>()?.RegisterMapLayer<StopsMapLayer>(Code, 0.85); }
            catch (Exception e) { capi.Logger.Warning("[sailorlocust] the docks and buoys could not be put on the world map: " + e.Message); }
        }

        /// <summary>The list from the server ("D|name|x|z|berths" lines).</summary>
        public static void SetKnown(string text)
        {
            Known = KnownStop.Parse(text);
            Received = true;
            GuiDialogSailingOrders.Current?.KnownChanged();
        }

        bool Showing { get { return PickFor != null && GuiDialogSailingOrders.Current == PickFor && PickFor.IsOpened(); } }

        static float Px() { return (float)Math.Ceiling(20 * RuntimeEnv.GUIScale); }

        LoadedTexture Texture(string icon, int color, int px)
        {
            string key = icon + "|" + color + "|" + px;
            if (icons.TryGetValue(key, out LoadedTexture t)) return t;
            try { t = capi.Gui.LoadSvgWithPadding(new AssetLocation(icon), px, px, 1, color); }
            catch (Exception e) { capi.Logger.Warning("[sailorlocust] map icon " + icon + " not read: " + e.Message); t = null; }
            icons[key] = t;
            return t;
        }

        bool InRouteNow(string name)
        {
            string orders = PickFor?.Locust?.WatchedAttributes.GetString("orders", "") ?? "";
            foreach (Stop s in Orders.Parse(orders)) if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public override void Render(GuiElementMap mapElem, float dt)
        {
            if (failed || !Active || capi == null || !Showing || Known.Count == 0) return;
            try
            {
                float px = Px();
                double left = mapElem.Bounds.renderX, top = mapElem.Bounds.renderY;
                capi.Render.PushScissor(mapElem.Bounds, true);
                try
                {
                    foreach (KnownStop k in Known)
                    {
                        int color = InRouteNow(k.Name) ? InRoute : k.Dock ? DockColor : BuoyColor;
                        LoadedTexture t = Texture(k.Dock ? DockIcon : BuoyIcon, color, (int)px);
                        if (t == null) continue;
                        world.Set(k.X + 0.5, 0, k.Z + 0.5);
                        mapElem.TranslateWorldPosToViewPos(world, ref view);
                        capi.Render.Render2DTexturePremultipliedAlpha(t.TextureId, (float)(left + view.X - px / 2), (float)(top + view.Y - px / 2), px, px, 62, null);
                    }
                }
                finally { capi.Render.PopScissor(); }
            }
            catch (Exception e)
            {
                failed = true;
                capi.Logger.Error("[sailorlocust] drawing the docks and buoys on the world map failed, the layer stops: " + e);
            }
        }

        KnownStop Under(MouseEvent args, GuiElementMap mapElem)
        {
            float half = Px() / 2 + 3;
            double mx = args.X - mapElem.Bounds.renderX, my = args.Y - mapElem.Bounds.renderY;
            KnownStop best = null; double bestD = double.MaxValue;
            foreach (KnownStop k in Known)
            {
                world.Set(k.X + 0.5, 0, k.Z + 0.5);
                mapElem.TranslateWorldPosToViewPos(world, ref view);
                double dx = view.X - mx, dy = view.Y - my;
                if (Math.Abs(dx) > half || Math.Abs(dy) > half) continue;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = k; }
            }
            return best;
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            if (failed || !Active || !Showing) return;
            KnownStop k = Under(args, mapElem);
            if (k == null) return;
            hoverText.AppendLine(k.Name + " (" + k.KindText + ")");
            hoverText.AppendLine(SailorLocustSystem.T("map-click-add"));
        }

        public override void OnMouseUpClient(MouseEvent args, GuiElementMap mapElem)
        {
            if (failed || !Active || !Showing || args.Button != EnumMouseButton.Left) return;
            KnownStop k = Under(args, mapElem);
            if (k == null) return;
            PickFor.Add(k.Name);
            capi.ShowChatMessage(SailorLocustSystem.T("map-added", k.Name));
            args.Handled = true;
        }

        public override void Dispose()
        {
            foreach (LoadedTexture t in icons.Values) t?.Dispose();
            icons.Clear();
            base.Dispose();
        }
    }
}
