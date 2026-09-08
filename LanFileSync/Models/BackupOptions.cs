using System.Text.RegularExpressions;

namespace LanFileSync;

public sealed class BackupOptions
{
    public bool ApplyLogRule { get; init; } = true;
    public int LogRetentionDays { get; init; } = 2;
    public IReadOnlyList<string> IgnoreRegexes { get; init; } = Array.Empty<string>();

    private Regex[]? _compiled;

    public bool IsIgnored(string relPath)
    {
        if (IgnoreRegexes.Count == 0)
            return false;

        if (_compiled == null)
        {
            _compiled = IgnoreRegexes
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s =>
                {
                    try { return new Regex(s, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)); }
                    catch (Exception) { return null; }
                })
                .Where(r => r != null)
                .Cast<Regex>()
                .ToArray();
        }

        foreach (var rx in _compiled)
        {
            try
            {
                if (rx.IsMatch(relPath))
                    return true;
            }
            catch (Exception) { }
        }
        return false;
    }
}