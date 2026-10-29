using System.Globalization;

namespace LuckyCat.Desktop.Core;

public static class LogRetention
{
    private const string Prefix = "app-";
    private const string Extension = ".log";
    private const string Format = "yyyyMMdd";

    public static int DeleteOlderThan(string logDirectory, DateTimeOffset now, int keepDays)
    {
        if (!Directory.Exists(logDirectory)) return 0;

        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-keepDays);
        var deleted = 0;

        foreach (var file in Directory.EnumerateFiles(logDirectory, "app-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (!DateOnly.TryParseExact(name[Prefix.Length..], Format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day)) continue;
            if (day >= cutoff) continue;

            File.Delete(file);
            deleted++;
        }

        return deleted;
    }
}
