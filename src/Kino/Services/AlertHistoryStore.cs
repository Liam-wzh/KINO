using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Kino.Services
{
    public sealed class AlertHistoryStore
    {
        public string FilePath { get; }
        public AlertHistoryStore(string path = null) => FilePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KINO", "alert-history.json");
        public Dictionary<string, DateTimeOffset> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new Dictionary<string, DateTimeOffset>();
                var history = JsonConvert.DeserializeObject<Dictionary<string, DateTimeOffset>>(File.ReadAllText(FilePath, Encoding.UTF8));
                var latest = DateTimeOffset.UtcNow.AddMinutes(5);
                return (history ?? new Dictionary<string, DateTimeOffset>()).Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value != default && p.Value <= latest).ToDictionary(p => p.Key, p => p.Value.ToUniversalTime());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            { return new Dictionary<string, DateTimeOffset>(); }
        }
        public void Save(IDictionary<string, DateTimeOffset> history)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(history, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
        }
    }
}
