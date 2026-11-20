using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.LiveChannels.Configuration;
using Jellyfin.Plugin.LiveChannels.Services;
using JPKribs.Jellyfin.Base;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Every key the pages use has a string, in Localization/en-us.json or as a declared Jellyfin string, and the
/// server's messages read in US English.
/// </summary>
public class TranslationKeysTests
{
    private static readonly Regex KeyUse = new(
        @"data-str(?:-title|-label|-placeholder)?=""([^""]+)""|\.(?:tr|format)\('([^']+)'",
        RegexOptions.Compiled);

    [Fact]
    public void EveryKeyInWeb_HasAString()
    {
        var project = ProjectDirectory();
        var known = LocalKeys(project);
        known.UnionWith(JellyfinKeys(project));

        var missing = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(project, "Web")).Where(f => f.EndsWith(".html", StringComparison.Ordinal) || f.EndsWith(".js", StringComparison.Ordinal)))
        {
            foreach (Match match in KeyUse.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (!known.Contains(key))
                {
                    missing.Add(Path.GetFileName(file) + ": " + key);
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Web_UsesKeys()
    {
        var project = ProjectDirectory();
        var uses = Directory.GetFiles(Path.Combine(project, "Web"))
            .SelectMany(f => KeyUse.Matches(File.ReadAllText(f)))
            .Count();

        Assert.True(uses > 100, "The pages use " + uses + " keys.");
    }

    [Fact]
    public void ConfigurationRefusal_ReadsInUsEnglish()
    {
        var config = new PluginConfiguration { TranscodeWidth = 100 };

        var error = Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(config, null, "en-us"));

        Assert.Equal("Resolution width must be between 320 and 3840 pixels.", error.Message);
    }

    [Fact]
    public void UnknownLanguage_FallsBackToUsEnglish()
    {
        Assert.Equal("Generate Buffer Cards", BaseTranslations.Get(TranslationKeys.TaskGenerateBufferCards, "xx"));
        Assert.Equal("Save", BaseTranslations.Get(TranslationKeys.Save, "xx"));
    }

    private static HashSet<string> LocalKeys(string project)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "Localization", "en-us.json")));
        return json.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> JellyfinKeys(string project)
        => XDocument.Load(Path.Combine(project, "Jellyfin.Plugin.LiveChannels.csproj"))
            .Descendants("JellyfinString")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string ProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Jellyfin.Plugin.LiveChannels");
            if (File.Exists(Path.Combine(candidate, "Jellyfin.Plugin.LiveChannels.csproj")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the plugin project above " + AppContext.BaseDirectory);
    }
}
