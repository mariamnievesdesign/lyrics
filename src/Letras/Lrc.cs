using System.Globalization;
using System.Text.RegularExpressions;

namespace Letras;

public sealed record LrcLine(TimeSpan Time, string Text);

/// <summary>Lee letras en formato LRC: "[01:23.45] texto de la línea".</summary>
public static class Lrc
{
    static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);

    public static List<LrcLine> Parse(string lrc)
    {
        var list = new List<LrcLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var stamps = Stamp.Matches(line);
            if (stamps.Count == 0) continue;

            var text = Stamp.Replace(line, "").Trim();
            foreach (Match m in stamps)
            {
                var min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                list.Add(new LrcLine(TimeSpan.FromSeconds(min * 60 + sec), text));
            }
        }
        return list.OrderBy(l => l.Time).ToList();
    }
}
