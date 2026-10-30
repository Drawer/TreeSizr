using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using TreeSizr.Controls;
using TreeSizr.ViewModels;

namespace TreeSizr.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1,023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    [InlineData(long.MaxValue, "8,192.0 PB")]
    public void Bytes_UsesBinaryUnits(long bytes, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, Format.Bytes(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

public class TreemapLayoutTests
{
    [Fact]
    public void Squarify_FillsTheAreaWithProportionalRectangles()
    {
        var bounds = new Rect(10, 20, 400, 300);
        double[] sizes = [50, 25, 12, 8, 5];
        double total = sizes.Sum();
        var areas = sizes.Select(s => s / total * bounds.Width * bounds.Height).ToArray();

        var rects = TreemapControl.Squarify(areas, bounds);

        for (int i = 0; i < rects.Length; i++)
        {
            Assert.Equal(areas[i], rects[i].Width * rects[i].Height, precision: 6);
            Assert.True(bounds.Contains(rects[i].TopLeft) && bounds.Contains(rects[i].BottomRight), $"rect {i} is outside the bounds");
        }
        for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
            {
                var overlap = Rect.Intersect(rects[i], rects[j]);
                Assert.True(overlap.IsEmpty || overlap.Width * overlap.Height < 1e-6, $"rects {i} and {j} overlap");
            }
    }
}

public class MainViewModelTests
{
    [Theory]
    [InlineData("C:", @"C:\")]
    [InlineData(@"  ""C:\Program Files""  ", @"C:\Program Files")]
    [InlineData(@"C:\Windows\..\Users", @"C:\Users")]
    public void NormalizePath_CleansUpTypedPaths(string input, string expected) =>
        Assert.Equal(expected, MainViewModel.NormalizePath(input));
}

/// <summary>Every text must exist in every language, with the same {n} placeholders.</summary>
public class TranslationTests
{
    private static readonly string ResourceDir = Path.Combine(FindRepoRoot(), "src", "TreeSizr", "Resources");

    public static TheoryData<string> Languages => new() { "nl" };

    [Theory]
    [MemberData(nameof(Languages))]
    public void TranslationHasTheSameKeysAndPlaceholders(string language)
    {
        var english = Load("Strings.resx");
        var translated = Load($"Strings.{language}.resx");

        Assert.Equal(english.Keys.Order(), translated.Keys.Order());
        foreach (var (key, text) in english)
            Assert.True(Placeholders(text).SetEquals(Placeholders(translated[key])), $"placeholders differ for '{key}'");
    }

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(ResourceDir, file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)").Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>The folder with TreeSizr.sln, above the test output or the working directory.</summary>
    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "TreeSizr.sln")))
                    return dir.FullName;
        }
        throw new DirectoryNotFoundException("TreeSizr.sln not found above the test output folder or the working directory.");
    }
}
