using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanFileSync;

public static class HostHelper
{
    private static readonly HashSet<string> Loopback = new(StringComparer.OrdinalIgnoreCase)
    { "127.0.0.1", "::1", "localhost" };

    /// <summary>获取本机所有 IPv4 地址(不含回环)。</summary>
    public static List<string> GetLocalIPs()
    {
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(addr.Address.ToString());
        }
        return list;
    }

    /// <summary>判断指定 IP 是否为本机地址(含回环)。</summary>
    public static bool IsLocalIP(string ip)
    {
        if (Loopback.Contains(ip)) return true;
        foreach (var local in GetLocalIPs())
            if (string.Equals(local, ip, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
                if (a.AddressFamily == AddressFamily.InterNetwork && a.ToString() == ip) return true;
        }
        catch { }
        return false;
    }

    /// <summary>从目标列表中过滤本机地址，返回 (远端列表, 被跳过的本机列表)。</summary>
    public static (List<string> remote, List<string> skipped) FilterLocalHosts(List<string> hosts)
    {
        var skipped = hosts.Where(IsLocalIP).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var remote = hosts.Where(h => !IsLocalIP(h)).ToList();
        return (remote, skipped);
    }
}
