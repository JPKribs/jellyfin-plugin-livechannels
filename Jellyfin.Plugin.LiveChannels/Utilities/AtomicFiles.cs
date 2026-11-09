using System;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Files a reader (an ffmpeg drawtext reading its text file) may open at any moment, written so it never sees one
/// part-written.
/// </summary>
internal static class AtomicFiles
{
    /// <summary>
    /// Writes a file named after its content once: the text goes to a file of its own first and is moved into place,
    /// so a reader sees all of it or none. A copy already there (the same text, by its name) is left as it is, and so
    /// is one another writer moves into place first.
    /// </summary>
    /// <param name="file">The file, named after its content.</param>
    /// <param name="text">The text.</param>
    /// <returns>Whether the file was already there.</returns>
    public static bool WriteOnce(string file, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        if (File.Exists(file))
        {
            return true;
        }

        var temp = file + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, file, overwrite: false);
            return false;
        }
        catch (IOException) when (File.Exists(file))
        {
            // Another writer moved the same text into place first.
            return true;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
