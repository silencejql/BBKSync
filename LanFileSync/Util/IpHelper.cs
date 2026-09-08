namespace LanFileSync;

internal static class IpHelper
{
    internal static List<string> ExpandIps(string text)
    {
        var result = new List<string>();
        var parts = text.Split(new[] { ',', '，', ';', '；', '\r', '\n', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                result.Add(part);
                continue;
            }

            string head = part[..dash].Trim();
            string tail = part[(dash + 1)..].Trim();
            if (!TrySplitIp(head, out string prefix, out int tailStart))
                throw new FormatException($"IP 范围起始无效: {part}");
            if (!TrySplitIp(tail, out string prefix2, out int tailEnd))
            {
                if (!int.TryParse(tail, out tailEnd))
                    throw new FormatException($"IP 范围结束无效: {part}");
                prefix2 = prefix;
            }
            if (!string.Equals(prefix, prefix2, StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"IP 范围起始与结束不在同一网段: {part}");
            if (tailStart > tailEnd)
                throw new FormatException($"IP 范围起始大于结束: {part}");
            for (int i = tailStart; i <= tailEnd; i++)
            {
                if (i < 1 || i > 254)
                    throw new FormatException($"IP 段数字需在 1~254 之间（出错处: {part}）");
                result.Add(prefix + i);
            }
        }
        return result;
    }

    internal static bool TrySplitIp(string s, out string prefix, out int lastOctet)
    {
        int lastDot = s.LastIndexOf('.');
        if (lastDot > 0 && int.TryParse(s[(lastDot + 1)..], out lastOctet))
        {
            prefix = s[..(lastDot + 1)];
            return true;
        }
        prefix = "";
        lastOctet = 0;
        return false;
    }
}