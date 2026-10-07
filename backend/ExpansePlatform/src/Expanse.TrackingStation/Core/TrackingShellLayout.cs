using System;

namespace Expanse.TrackingStation.Core
{
    // Shared by the actual IMGUI shell and offline geometry checks; dimensions are logical pixels.
    public sealed class TrackingShellLayout
    {
        public float Scale, Width, Height, SidebarWidth, HeaderHeight, FooterHeight, Padding, Gap;
        public float FontSize, TitleHeight, SearchHeight, FilterHeight, QuickHeight, ToolbarHeight, RowHeight, CountHeight;
        public bool Compact, TwoToolbarRows, FiltersCollapsed;
        public float InnerWidth { get { return SidebarWidth - 2 * Padding; } }
        public float TreeTop { get { return HeaderHeight + Padding + TitleHeight + Gap + (FiltersCollapsed ? 0 : SearchHeight + Gap + FilterHeight + Gap + QuickHeight + Gap + ToolbarHeight * (TwoToolbarRows ? 2 : 1) + Gap); } }
        public float TreeBottom { get { return Height - FooterHeight - CountHeight; } }
        public float CardWidth { get { return Math.Min(Width - SidebarWidth - 2 * Padding, Compact ? 460 : 600); } }
        public float CardHeight { get { return Compact ? 150 : 166; } }
        public float CardLeft { get { return SidebarWidth + (Width - SidebarWidth - CardWidth) / 2; } }
        public float CardTop { get { return Height - FooterHeight - Padding - CardHeight; } }
        public static TrackingShellLayout Create(float screenWidth, float screenHeight, float gameScale, float preferenceScale)
        {
            if (float.IsNaN(gameScale) || float.IsInfinity(gameScale) || gameScale <= 0) gameScale = 1;
            if (float.IsNaN(preferenceScale) || float.IsInfinity(preferenceScale)) preferenceScale = 1;
            var scale = Math.Max(.5f, Math.Min(gameScale * Math.Max(.75f, Math.Min(2, preferenceScale)), Math.Min(screenWidth / 760f, screenHeight / 500f)));
            var w = screenWidth / scale; var h = screenHeight / scale; var compact = h < 720;
            var result = new TrackingShellLayout { Scale = scale, Width = w, Height = h, SidebarWidth = w * .28f, HeaderHeight = 78, FooterHeight = 52, Compact = compact, FontSize = compact ? 13 : 14, Padding = compact ? 14 : 18, Gap = compact ? 6 : 8, TitleHeight = compact ? 20 : 24, SearchHeight = 34, FilterHeight = 32, QuickHeight = 34, ToolbarHeight = 30, RowHeight = 32, CountHeight = 62 };
            result.TwoToolbarRows = result.InnerWidth < 500;
            return result;
        }
    }
}
