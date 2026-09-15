namespace LanFileSync;

internal static class IpHelper
{
    internal static List<string> ExpandIps(string text)
    {
        var result = new List<string>();
        var parts = text.Split(new[] { ',', '，', ';', '；', '\r', '\n', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? lastPrefix = null; // 记录上一个完整 IP 的前三段前缀

        foreach (var part in parts)
        {
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                // 单个 IP 或单个数字(继承前缀)
                if (TrySplitIp(part, out string singlePrefix, out int last))
                {
                    lastPrefix = singlePrefix;
                    result.Add(singlePrefix + last);
                }
                else if (int.TryParse(part, out int num) && lastPrefix != null)
                {
                    // 纯数字，继承上一个 IP 的前缀
                    if (num < 1 || num > 254)
                        throw new FormatException($"IP 段数字需在 1~254 之间(出错处: {part})");
                    result.Add(lastPrefix + num);
                }
                else
                {
                    result.Add(part);
                }
                continue;
            }

            // 范围表达式: head-tail
            string head = part[..dash].Trim();
            string tail = part[(dash + 1)..].Trim();
            string prefix;
            int tailStart;

            if (TrySplitIp(head, out prefix, out tailStart))
            {
                lastPrefix = prefix;
            }
            else if (int.TryParse(head, out tailStart) && lastPrefix != null)
            {
                prefix = lastPrefix;
            }
            else
            {
                throw new FormatException($"IP 范围起始无效: {part}");
            }

            string prefix2;
            int tailEnd;
            if (!TrySplitIp(tail, out prefix2, out tailEnd))
            {
                if (!int.TryParse(tail, out tailEnd))
                    throw new FormatException($"IP 范围结束无效: {part}");
                prefix2 = prefix;
            }
            else
            {
                lastPrefix = prefix2;
            }

            if (!string.Equals(prefix, prefix2, StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"IP 范围起始与结束不在同一网段: {part}");
            if (tailStart > tailEnd)
                throw new FormatException($"IP 范围起始大于结束: {part}");
            for (int i = tailStart; i <= tailEnd; i++)
            {
                if (i < 1 || i > 254)
                    throw new FormatException($"IP 段数字需在 1~254 之间(出错处: {part})");
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