using System.Diagnostics;

namespace Kino.Services
{
    internal static class BrowserLauncher
    {
        public static bool Open(Uri uri)
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); return true; }
            catch { return false; }
        }
    }
}
