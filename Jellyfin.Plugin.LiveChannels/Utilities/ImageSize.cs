using System;
using System.Buffers.Binary;
using System.IO;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Reads an image's pixel size from its header, without decoding it or starting a process: enough to choose an Up
/// Next card's artwork frame. Understands PNG, JPEG, GIF and WebP (lossy, lossless and extended), the formats
/// Jellyfin stores artwork in.
/// </summary>
internal static class ImageSize
{
    // JPEG keeps its size in the start-of-frame segment, which can follow large metadata (EXIF, ICC profiles).
    private const int MaxHeaderBytes = 512 * 1024;

    /// <summary>The image's width and height, or <c>null</c> when the file is missing, unreadable, or not one of the formats read.</summary>
    /// <param name="path">The image file.</param>
    /// <returns>The size, or <c>null</c>.</returns>
    public static (int Width, int Height)? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(MaxHeaderBytes, file.Length)];
            var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Parse(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The size an image's leading bytes declare, or <c>null</c>.</summary>
    /// <param name="data">The file's leading bytes.</param>
    /// <returns>The size, or <c>null</c>.</returns>
    public static (int Width, int Height)? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
        {
            return Valid((int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
        }

        if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
        {
            return Valid(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
        }

        if (data.Length >= 30 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
        {
            return WebP(data);
        }

        return data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 ? Jpeg(data) : null;
    }

    private static (int, int)? WebP(ReadOnlySpan<byte> data)
    {
        var chunk = data.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8) && data.Length >= 30)
        {
            // Lossy: the frame header after the 3-byte start code holds 14-bit dimensions.
            return Valid(BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF);
        }

        if (chunk.SequenceEqual("VP8L"u8) && data.Length >= 25)
        {
            // Lossless: 14 bits each of width-1 and height-1, after the signature byte.
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            return Valid((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8X"u8) && data.Length >= 30)
        {
            // Extended: 24-bit canvas width-1 and height-1.
            var width = data[24] | (data[25] << 8) | (data[26] << 16);
            var height = data[27] | (data[28] << 8) | (data[29] << 16);
            return Valid(width + 1, height + 1);
        }

        return null;
    }

    private static (int, int)? Jpeg(ReadOnlySpan<byte> data)
    {
        var at = 2;
        while (at + 9 < data.Length)
        {
            if (data[at] != 0xFF)
            {
                return null;
            }

            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            // Start-of-frame markers (baseline, progressive, and the rest) other than DHT, JPG and DAC.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return Valid(BinaryPrimitives.ReadUInt16BigEndian(data[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(at + 5)..]));
            }

            // Markers without a length, then every other segment by its length.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD9))
            {
                at += 2;
                continue;
            }

            at += 2 + BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
        }

        return null;
    }

    private static (int, int)? Valid(int width, int height) => width > 0 && height > 0 ? (width, height) : null;
}
