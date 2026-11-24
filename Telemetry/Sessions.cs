using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal static class Sessions
{
    private const int UserName = 5, DomainName = 7;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(nint server, uint session, int info, out nint buffer, out uint bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    // The account signed in to a session, as "domain\user". Empty for sessions without a user, such as session 0.
    public static string User(uint session)
    {
        string user = Query(session, UserName);
        if (user.Length == 0) return "";
        string domain = Query(session, DomainName);
        return domain.Length == 0 ? user : $@"{domain}\{user}";
    }

    private static string Query(uint session, int info)
    {
        if (!WTSQuerySessionInformationW(0, session, info, out nint buffer, out _) || buffer == 0) return "";
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { WTSFreeMemory(buffer); }
    }
}
