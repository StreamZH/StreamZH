// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using StreamZH.Tray.Core;

namespace StreamZH.Tray;

/// <summary>The StreamZH monogram (streamzh.ico, made by tools/brand/tray-icon.py) with a status dot at the bottom right.</summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<StatusDot, Icon> _icons = [];

    public TrayIcons()
    {
        App = LoadIcon(new Size(256, 256));
    }

    /// <summary>The plain icon (windows, balloon notifications).</summary>
    public Icon App { get; }

    public Icon For(StatusDot dot)
    {
        if (!_icons.TryGetValue(dot, out var icon))
        {
            icon = Draw(dot);
            _icons[dot] = icon;
        }

        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        App.Dispose();
    }

    private static Icon LoadIcon(Size size)
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("streamzh.ico")
            ?? throw new InvalidOperationException("streamzh.ico is missing from the assembly.");
        return new Icon(stream, size);
    }

    private Icon Draw(StatusDot dot)
    {
        // The small icon size follows the display scaling (16 px at 100 %, 24 px at 150 %, 32 px at 200 %).
        var size = SystemInformation.SmallIconSize;
        using var source = LoadIcon(size);
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawIcon(source, new Rectangle(Point.Empty, size));

            // Dot a bit under half the icon, with a ring in the tile colour so it stands out from the monogram.
            var diameter = Math.Max(6f, size.Width * 0.45f);
            var ring = Math.Max(1f, size.Width / 16f);
            var x = size.Width - diameter;
            var y = size.Height - diameter;
            using (var outline = new SolidBrush(Color.FromArgb(0x1F, 0x1E, 0x1D)))
            {
                g.FillEllipse(outline, x - ring, y - ring, diameter + ring, diameter + ring);
            }

            using var fill = new SolidBrush(Colour(dot));
            g.FillEllipse(fill, x, y, diameter - ring, diameter - ring);
        }

        // FromHandle does not own the handle; the clone gets its own copy, so the GDI handle can go at once.
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Color Colour(StatusDot dot) => dot switch
    {
        StatusDot.Green => Color.FromArgb(0x2F, 0xB3, 0x5E),
        StatusDot.Orange => Color.FromArgb(0xF5, 0x9E, 0x0B),
        _ => Color.FromArgb(0xE5, 0x48, 0x4D),
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
