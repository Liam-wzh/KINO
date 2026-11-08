using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Kino.Models;

namespace Kino.Services
{
    public sealed class SettingsStore
    {
        public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KINO");
        public string FilePath { get; }
        public string LoadWarning { get; private set; }
        public SettingsStore(string path = null) { FilePath = path ?? Path.Combine(DataDirectory, "settings.json"); }

        public AppSettings Load()
        {
            LoadWarning = null;
            if (!File.Exists(FilePath)) return new AppSettings();
            try
            {
                var settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new AppSettings();
                if (settings.Sites != null && settings.Sites.Any(s => s != null))
                {
                    settings.Sites = settings.Sites.Where(s => s != null).ToList();
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var site in settings.Sites)
                    {
                        if (string.IsNullOrWhiteSpace(site.Id) || !ids.Add(site.Id))
                        { site.Id = Guid.NewGuid().ToString("N"); ids.Add(site.Id); }
                        site.Token = Decode(site.ProtectedToken);
                    }
                    if (!settings.SelectSite(settings.ActiveSiteId)) settings.SelectSite(settings.Sites[0].Id);
                }
                else
                {
                    settings.Token = Decode(settings.ProtectedToken);
                    settings.CaptureActiveSite();
                }
                settings.Normalize();
                return settings;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                LoadWarning = "设置文件无法读取，已使用演示模式；原文件已保留。";
                return AppSettings.DemoSettings();
            }
        }

        public void Save(AppSettings settings)
        {
            var copy = settings.Clone();
            copy.CaptureActiveSite();
            foreach (var site in copy.Sites)
                if (!string.IsNullOrEmpty(site.Token)) site.ProtectedToken = Protect(site.Token);
            // Preserve unavailable ciphertext on inactive profiles until their
            // credentials are explicitly edited. The active fields mirror the catalog.
            if (copy.Sites.Count > 0) copy.SelectSite(copy.ActiveSiteId);
            var directory = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(directory);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(copy, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }

        private string Decode(string ciphertext)
        {
            if (string.IsNullOrEmpty(ciphertext)) return "";
            try { return Unprotect(ciphertext); }
            catch (CryptographicException) { LoadWarning = "部分站点令牌无法由此 Windows 用户解密，请在站点管理中重新填写。"; }
            catch (FormatException) { LoadWarning = "部分站点令牌数据已损坏，请在站点管理中重新填写。"; }
            return "";
        }

        public static string Protect(string token) => Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
        public static string Unprotect(string token) => Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(token), null, DataProtectionScope.CurrentUser));
    }
}
