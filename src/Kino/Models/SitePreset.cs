namespace Kino.Models
{
    // Optional public connection details. Presets never contain credentials or select themselves.
    public sealed class SitePreset
    {
        public string Name { get; }
        public string BaseUrl { get; }
        public ProviderKind Provider { get; }

        public SitePreset(string name, string baseUrl, ProviderKind provider)
        { Name = name; BaseUrl = baseUrl; Provider = provider; }

        public static IReadOnlyList<SitePreset> Available { get; } = Array.AsReadOnly(new[]
        {
            new SitePreset("余时 · Yushi", "https://aixyushi.com", ProviderKind.NewApi)
        });
    }
}
