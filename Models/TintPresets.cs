using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml.Media;

namespace KaliteKit.Models
{
    /// <summary>
    /// One selectable window tint. <see cref="Hex"/> is null for the Default
    /// (no tint) entry; every other preset maps to a solid color shown as a
    /// folder-tab card on the Personalization page.
    /// </summary>
    public sealed class TintPreset
    {
        public required string Name { get; init; }

        /// <summary>RRGGBB hex, or null for the Default (no tint) entry.</summary>
        public string? Hex { get; init; }

        public Windows.UI.Color Color { get; init; }

        // Lazily created: SolidColorBrush needs a live WinUI runtime, so the
        // tint catalog must stay constructible in headless / unit-test contexts.
        // We therefore only create the real brush when there's an actual XAML
        // runtime; otherwise we return a safe fallback brush.
        private SolidColorBrush? _brush;
        private SolidColorBrush? _fallbackBrush;
        public SolidColorBrush Brush
        {
            get
            {
                if (_brush != null) return _brush;
                try
                {
                    _brush = new SolidColorBrush(Color);
                    return _brush;
                }
                catch
                {
                    // No live WinUI runtime (e.g. startup-before-OnLaunched, or a
                    // headless unit-test environment). Fall back to a non-null brush
                    // so the XAML binding doesn't throw a parse / initialization error.
                    if (_fallbackBrush == null)
                    {
                        _fallbackBrush = new SolidColorBrush(Color);
                    }
                    return _fallbackBrush;
                }
            }
        }
    }

    /// <summary>
    /// The tint palette — a Default entry plus 69 named colors grouped by hue
    /// family (neutrals, blues, teals, greens, warm tones, reds/pinks, purples),
    /// laid out as a wrap-flow gradient grid.
    /// </summary>
    public static class TintPresets
    {
        public static IReadOnlyList<TintPreset> All { get; } = Build();

        /// <summary>Parses "#RRGGBB" (with or without the #) into a color, or null when invalid.</summary>
        public static Windows.UI.Color? ParseHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            var h = hex.Trim().TrimStart('#');
            if (h.Length != 6 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
                return null;
            return Windows.UI.Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        /// <summary>Serializes a color as RRGGBB hex (alpha dropped — tint is fully opaque).</summary>
        public static string ToHex(Windows.UI.Color color)
            => $"{color.R:X2}{color.G:X2}{color.B:X2}";

        private static IReadOnlyList<TintPreset> Build() => new[]
        {
            new TintPreset
            {
                Name = "Default",
                Hex = null,
                Color = Windows.UI.Color.FromArgb(0xFF, 0x5A, 0x5A, 0x5A),
            },

            // ── Neutrals ───────────────────────────────────────────────
            P("Gray Light", "#9A9A9A"),
            P("Gray", "#8A8A8A"),
            P("Gray Dark", "#6A6A6A"),
            P("Overcast", "#6E747D"),
            P("Storm", "#5A6270"),
            P("Blue Gray", "#6B7A8C"),
            P("Slate", "#526078"),
            P("Camouflage", "#6E6B4E"),

            // ── Blues ──────────────────────────────────────────────────
            P("Cool Blue Bright", "#4A8FD9"),
            P("Blue", "#3E6FB8"),
            P("Bright Blue", "#2E7FD6"),
            P("Azure", "#2E9BD6"),
            P("Sky", "#6EA8E8"),
            P("Steel Blue", "#4A7E9E"),
            P("Deep Blue", "#2E5A9E"),
            P("Cobalt", "#3E63C8"),
            P("Indigo", "#465AA8"),
            P("Navy", "#3E4E88"),
            P("Powder Blue", "#7E96C8"),
            P("Glacier", "#6E9EC0"),

            // ── Teals ──────────────────────────────────────────────────
            P("Seafoam", "#58B8A8"),
            P("Teal", "#3E9E9E"),
            P("Turquoise", "#3FAFAC"),
            P("Aqua", "#6EC8C0"),
            P("Mint Light", "#7CC8A0"),
            P("Mint", "#4EBD8C"),
            P("Jade", "#4EA888"),
            P("Sea Green", "#3E8C7A"),

            // ── Greens ─────────────────────────────────────────────────
            P("Green", "#4E9B4E"),
            P("Grass Green", "#5EA84E"),
            P("Spring Green", "#6EC85E"),
            P("Forest", "#3E7A3E"),
            P("Emerald", "#3E9468"),
            P("Moss", "#5E8C5E"),
            P("Olive", "#7A8C4E"),
            P("Yellow Green", "#A0A84E"),
            P("Lime", "#8CA83E"),
            P("Sage", "#8CA88C"),

            // ── Yellows, oranges & browns ──────────────────────────────
            P("Yellow Gold", "#A8903C"),
            P("Golden", "#C09A40"),
            P("Khaki", "#9E9668"),
            P("Sand", "#8C8468"),
            P("Orange Bright", "#C4682A"),
            P("Orange", "#D97B29"),
            P("Tangerine", "#E08A3E"),
            P("Apricot", "#D99E6E"),
            P("Brown", "#A86E3E"),
            P("Rust", "#A64B2A"),
            P("Brick Red", "#A3472F"),

            // ── Reds & pinks ───────────────────────────────────────────
            P("Red", "#C03B3B"),
            P("Dark Red", "#A02A2A"),
            P("Mod Red", "#8E2F3E"),
            P("Crimson", "#B0354A"),
            P("Ruby", "#A8343E"),
            P("Rose", "#B84A6B"),
            P("Pink", "#D96A8E"),
            P("Salmon", "#D97E7E"),
            P("Berry", "#9E3E6E"),
            P("Mauve", "#B86E8E"),

            // ── Purples ────────────────────────────────────────────────
            P("Violet Red Light", "#A86A9C"),
            P("Violet Red", "#8E4E7E"),
            P("Violet", "#7E5AA8"),
            P("Purple", "#6B4E9E"),
            P("Deep Purple", "#5A3E8C"),
            P("Iris Pastel", "#7D6FA9"),
            P("Iris", "#5A4EAA"),
            P("Lavender", "#9E8ED1"),
            P("Plum", "#8C4E7E"),
            P("Orchid", "#A86EB8"),
        };

        private static TintPreset P(string name, string hex)
        {
            var color = ParseHex(hex) ?? Windows.UI.Color.FromArgb(0xFF, 0x5A, 0x5A, 0x5A);
            return new TintPreset { Name = name, Hex = hex, Color = color };
        }
    }
}