param(
    [string]$MasterPath = (Join-Path $PSScriptRoot "..\assets\brand\icon-master.png"),
    [string]$NormalizedPath = (Join-Path $PSScriptRoot "..\assets\brand\icon-normalized.png"),
    [string]$IconPath = (Join-Path $PSScriptRoot "..\assets\brand\winput-lan.ico")
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSEdition -ne "Desktop") {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -MasterPath $MasterPath -NormalizedPath $NormalizedPath -IconPath $IconPath
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.Drawing

Add-Type -ReferencedAssemblies "System.Drawing" -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;

public static class WinputLanIconNormalizer
{
    private const double TargetGlyphCoverage = 0.80;

    private static Rectangle AlphaBounds(Bitmap bitmap, byte minimumAlpha)
    {
        int left = bitmap.Width;
        int top = bitmap.Height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A < minimumAlpha) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        if (right < left || bottom < top) throw new InvalidOperationException("The icon master has no opaque pixels.");
        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        Bitmap result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(result))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }
        return result;
    }

    private static bool HasMultipleOpaqueColors(Bitmap bitmap)
    {
        int first = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                if (color.A != 255) continue;
                int rgb = (color.R << 16) | (color.G << 8) | color.B;
                if (first < 0) first = rgb;
                else if (first != rgb) return true;
            }
        }
        return false;
    }

    private static string BoundsText(Rectangle bounds)
    {
        return String.Format("{0},{1} {2}x{3}", bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static void WriteIcon(string path, IList<byte[]> frames, int[] sizes)
    {
        using (FileStream output = File.Create(path))
        using (BinaryWriter writer = new BinaryWriter(output))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);
            int offset = 6 + (16 * sizes.Length);
            for (int index = 0; index < sizes.Length; index++)
            {
                int size = sizes[index];
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)frames[index].Length);
                writer.Write((uint)offset);
                offset += frames[index].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
    }

    public static string Normalize(string masterPath, string normalizedPath, string iconPath)
    {
        int[] iconSizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
        using (Bitmap master = new Bitmap(masterPath))
        {
            if (master.Width != master.Height) throw new InvalidOperationException("The icon master must be square.");
            // The master contains single-alpha-pixel export noise well outside the visible mark.
            // Keeping alpha >= 16 crops the visible glyph while retaining antialiased edges in output.
            Rectangle sourceBounds = AlphaBounds(master, 16);
            int cropSide = (int)Math.Ceiling(Math.Max(sourceBounds.Width, sourceBounds.Height) / TargetGlyphCoverage);
            int cropX = (int)Math.Round(sourceBounds.X + (sourceBounds.Width - cropSide) / 2.0, MidpointRounding.AwayFromZero);
            int cropY = (int)Math.Round(sourceBounds.Y + (sourceBounds.Height - cropSide) / 2.0, MidpointRounding.AwayFromZero);
            cropX = Math.Max(0, Math.Min(master.Width - cropSide, cropX));
            cropY = Math.Max(0, Math.Min(master.Height - cropSide, cropY));
            Rectangle crop = new Rectangle(cropX, cropY, cropSide, cropSide);

            using (Bitmap cropped = master.Clone(crop, PixelFormat.Format32bppArgb))
            using (Bitmap normalized = Resize(cropped, master.Width, master.Height))
            {
                Rectangle normalizedBounds = AlphaBounds(normalized, 16);
                double coverage = Math.Max(normalizedBounds.Width, normalizedBounds.Height) / (double)normalized.Width;
                if (coverage < 0.78 || coverage > 0.82) throw new InvalidOperationException("Normalized glyph coverage is outside 78-82 percent.");
                if (!HasMultipleOpaqueColors(normalized))
                    throw new InvalidOperationException("The normalized icon did not preserve the master’s two-tone palette.");

                Directory.CreateDirectory(Path.GetDirectoryName(normalizedPath));
                normalized.Save(normalizedPath, ImageFormat.Png);
                List<byte[]> frames = new List<byte[]>();
                List<string> frameBounds = new List<string>();
                foreach (int size in iconSizes)
                {
                    using (Bitmap frame = Resize(normalized, size, size))
                    {
                        Rectangle bounds = AlphaBounds(frame, 16);
                        double frameCoverage = Math.Max(bounds.Width, bounds.Height) / (double)size;
                        if (frameCoverage < 0.68 || frameCoverage > 0.94) throw new InvalidOperationException("Icon frame coverage is not visually useful at " + size + "px (" + BoundsText(bounds) + ", " + frameCoverage.ToString("P1") + ").");
                        using (MemoryStream stream = new MemoryStream())
                        {
                            frame.Save(stream, ImageFormat.Png);
                            frames.Add(stream.ToArray());
                        }
                        frameBounds.Add(size + "px=" + BoundsText(bounds));
                    }
                }
                using (Bitmap titleBarPreview = Resize(normalized, 28, 28))
                {
                    Rectangle bounds = AlphaBounds(titleBarPreview, 16);
                    double frameCoverage = Math.Max(bounds.Width, bounds.Height) / 28.0;
                    if (frameCoverage < 0.75 || frameCoverage > 0.90) throw new InvalidOperationException("The 28px title-bar preview is not visually useful.");
                    frameBounds.Add("28px=" + BoundsText(bounds));
                }
                WriteIcon(iconPath, frames, iconSizes);
                return "source=" + BoundsText(sourceBounds) + "; crop=" + BoundsText(crop) + "; normalized=" + BoundsText(normalizedBounds) + "; coverage=" + coverage.ToString("P1") + "; frames=" + String.Join(", ", frameBounds.ToArray());
            }
        }
    }
}
'@

$result = [WinputLanIconNormalizer]::Normalize(
    (Resolve-Path -LiteralPath $MasterPath),
    [System.IO.Path]::GetFullPath($NormalizedPath),
    [System.IO.Path]::GetFullPath($IconPath))
Write-Output "Normalized Winput LAN icon: $result"
