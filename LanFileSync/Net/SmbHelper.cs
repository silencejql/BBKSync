using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LanFileSync;

public static class SmbHelper
{
    private const int RESOURCETYPE_DISK = 1;
    private const int CONNECT_TEMPORARY = 0x4;
    private const int NO_ERROR = 0;
    private const int ERROR_ALREADY_ASSIGNED = 85;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string lpLocalName;
        public string lpRemoteName;
        public string lpComment;
        public string lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetUseConnectionW(
        IntPtr hwndOwner,
        ref NetResource lpNetResource,
        string? lpPassword,
        string? lpUserID,
        int dwFlags,
        IntPtr lpAccessName,
        IntPtr lpBufferSize,
        IntPtr lpResult);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2W(string lpName, int dwFlags, bool fForce);

    public static string MakeUnc(string ip, string share)
    {
        ip = (ip ?? "").Trim().TrimStart('\\');
        share = (share ?? "").Trim().TrimStart('\\').TrimStart('/');
        if (share.Length == 0)
            share = "c$";
        return @"\\" + ip + @"\" + share;
    }

    public static void Connect(string unc, string? user, string? password)
    {
        var nr = new NetResource
        {
            dwType = RESOURCETYPE_DISK,
            lpRemoteName = unc,
        };

        int rc = WNetUseConnectionW(
            IntPtr.Zero,
            ref nr,
            string.IsNullOrEmpty(password) ? null : password,
            string.IsNullOrEmpty(user) ? null : user,
            CONNECT_TEMPORARY,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (rc != NO_ERROR && rc != ERROR_ALREADY_ASSIGNED)
            throw new Win32Exception(rc);
    }
}