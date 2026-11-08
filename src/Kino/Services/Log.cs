using System.IO;
using System.Text;

namespace Kino.Services
{
    internal static class Log
    {
        private static readonly object Gate = new object();
        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(SettingsStore.DataDirectory);
                    var file = Path.Combine(SettingsStore.DataDirectory, "kino.log");
                    if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024)
                        File.WriteAllText(file, "", new UTF8Encoding(false));
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
