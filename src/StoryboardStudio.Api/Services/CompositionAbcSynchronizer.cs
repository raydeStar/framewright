using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Keeps the approachable composition form connected to the exact ABC that
/// YuE2 performs. It preserves existing melody bars, applies structural order
/// and bar counts, and rewrites harmony and global musical settings. A user who
/// edits raw ABC explicitly remains authoritative over that score.
/// </summary>
public static partial class CompositionAbcSynchronizer
{
    public static string Synchronize(
        MusicCompositionDocument? previous,
        MusicCompositionDocument current,
        string sourceAbc)
    {
        var normalized = sourceAbc.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var voices = ReadVoices(normalized);
        var vocal = voices.Vocal.Count > 0 ? voices.Vocal : ["Z"];
        var instrumental = voices.Instrumental.Count > 0 ? voices.Instrumental : ["Z"];
        var previousRanges = BuildRanges(previous);
        var output = new StringBuilder();
        var reference = HeaderValue(normalized, "X", "1", allowEmpty: false);
        output.Append("X:").Append(reference).Append('\n');
        output.Append("T:").Append(SanitizeHeader(current.Title)).Append('\n');
        output.Append("M:").Append(current.Meter).Append('\n');
        output.Append("L:").Append(HeaderValue(normalized, "L", "1/8", allowEmpty: false)).Append('\n');
        output.Append("Q:1/4=").Append(current.Tempo.ToString(CultureInfo.InvariantCulture)).Append('\n');
        output.AppendLine("V: Vocal clef=treble name=\"Vocal Melody\" snm=\"Vocal\"");
        output.AppendLine("V: Ins clef=treble name=\"Ins Melody\" snm=\"Inst.\"");
        output.Append("K:").Append(FormatKey(current.Key)).Append('\n');

        for (var index = 0; index < current.Sections.Count; index++)
        {
            var section = current.Sections[index];
            var fallbackStart = current.Sections.Take(index).Sum(item => item.Bars);
            var range = FindRange(section, index, previous, previousRanges, vocal.Count, fallbackStart);
            var vocalBars = Resize(vocal, range.Start, range.Count, section.Bars);
            var instrumentalBars = Resize(instrumental, range.Start, range.Count, section.Bars);
            if (section.Chords.Count > 0)
            {
                vocalBars = vocalBars.Select((bar, barIndex) => ApplyChord(bar, section.Chords[barIndex % section.Chords.Count])).ToArray();
            }

            output.Append("% ").Append(SanitizeComment(section.Type)).Append(" [fw:").Append(SanitizeComment(section.Id)).Append("]\n");
            output.AppendLine("V: Vocal");
            WriteBars(output, vocalBars);
            output.AppendLine("V: Ins");
            WriteBars(output, instrumentalBars);
        }

        return output.ToString();
    }

    private static (List<string> Vocal, List<string> Instrumental) ReadVoices(string abc)
    {
        var vocal = new List<string>();
        var instrumental = new List<string>();
        List<string>? active = null;
        var bodyStarted = false;
        foreach (var rawLine in abc.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!bodyStarted)
            {
                if (line.StartsWith("K:", StringComparison.OrdinalIgnoreCase)) bodyStarted = true;
                continue;
            }
            if (line.StartsWith('%') || line.Length == 0) continue;

            var inline = InlineVoice().Match(line);
            if (inline.Success)
            {
                active = inline.Groups[1].Value.Equals("Vocal", StringComparison.OrdinalIgnoreCase) ? vocal : instrumental;
                AddBars(active, line[inline.Length..]);
                continue;
            }

            var voice = VoiceLine().Match(line);
            if (voice.Success)
            {
                active = voice.Groups[1].Value.Equals("Vocal", StringComparison.OrdinalIgnoreCase) ? vocal : instrumental;
                continue;
            }
            if (active is not null) AddBars(active, line);
        }
        return (vocal, instrumental);
    }

    private static void AddBars(List<string> destination, string music)
    {
        destination.AddRange(music.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<(string Id, string Type, int Start, int Count)> BuildRanges(MusicCompositionDocument? previous)
    {
        if (previous is null) return [];
        var start = 0;
        var ranges = new List<(string, string, int, int)>(previous.Sections.Count);
        foreach (var section in previous.Sections)
        {
            ranges.Add((section.Id, section.Type, start, section.Bars));
            start += section.Bars;
        }
        return ranges;
    }

    private static (int Start, int Count) FindRange(
        MusicSection section,
        int index,
        MusicCompositionDocument? previous,
        List<(string Id, string Type, int Start, int Count)> ranges,
        int availableBars,
        int fallbackStart)
    {
        if (previous is not null)
        {
            var exact = ranges.FirstOrDefault(item => item.Id.Equals(section.Id, StringComparison.OrdinalIgnoreCase));
            if (exact.Count > 0) return (exact.Start, exact.Count);
            var matchingType = ranges.FirstOrDefault(item => item.Type.Equals(section.Type, StringComparison.OrdinalIgnoreCase));
            if (matchingType.Count > 0) return (matchingType.Start, matchingType.Count);
            if (index < ranges.Count) return (ranges[index].Start, ranges[index].Count);
        }

        return (Math.Min(fallbackStart, Math.Max(0, availableBars - 1)), Math.Max(1, section.Bars));
    }

    private static string[] Resize(List<string> source, int start, int sourceCount, int desiredCount)
    {
        var safeCount = Math.Max(1, Math.Min(sourceCount, source.Count));
        var result = new string[desiredCount];
        for (var index = 0; index < desiredCount; index++)
        {
            var sourceIndex = (Math.Max(0, start) + index % safeCount) % source.Count;
            result[index] = source[sourceIndex];
        }
        return result;
    }

    private static string ApplyChord(string bar, string chord)
    {
        var melody = ChordSymbol().Replace(bar, string.Empty).Trim();
        return $"\"{chord}\"{(melody.Length == 0 ? "Z" : melody)}";
    }

    private static void WriteBars(StringBuilder output, string[] bars)
    {
        for (var index = 0; index < bars.Length; index += 4)
        {
            output.Append(string.Join('|', bars.Skip(index).Take(4)));
            output.AppendLine("|");
        }
    }

    private static string HeaderValue(string abc, string name, string fallback, bool allowEmpty)
    {
        var match = Regex.Match(abc, $"(?im)^{Regex.Escape(name)}:(.*)$");
        var value = match.Success ? match.Groups[1].Value.Trim() : fallback;
        return !allowEmpty && value.Length == 0 ? fallback : value;
    }

    private static string SanitizeHeader(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string FormatKey(string value)
    {
        var compact = Regex.Replace(value.Trim(), "\\s+", " ");
        if (compact.EndsWith(" minor", StringComparison.OrdinalIgnoreCase)) return compact[..^6].TrimEnd() + "m";
        if (compact.EndsWith(" major", StringComparison.OrdinalIgnoreCase)) return compact[..^6].TrimEnd();
        return compact;
    }

    private static string SanitizeComment(string value) => CommentUnsafe().Replace(value, "-").Trim('-');

    [GeneratedRegex("^\\[V:\\s*(Vocal|Ins)\\]", RegexOptions.IgnoreCase)]
    private static partial Regex InlineVoice();

    [GeneratedRegex("^V:\\s*(Vocal|Ins)(?:\\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex VoiceLine();

    [GeneratedRegex("\"[^\"\\r\\n]*\"")]
    private static partial Regex ChordSymbol();

    [GeneratedRegex("[^A-Za-z0-9 _-]+")]
    private static partial Regex CommentUnsafe();
}
