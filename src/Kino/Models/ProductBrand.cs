namespace Kino.Models
{
    public static class ProductBrand
    {
        public const string Name = "KINO";
        public static string Version => typeof(ProductBrand).Assembly.GetName().Version.ToString(3);
    }
}
