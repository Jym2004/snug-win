using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnugWin.Core.PhoneScreen;

public sealed record PhoneScreenSpecData(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("points")] int? Points = null)
{
    [JsonIgnore]
    public bool IsValid => Width > 0 && Height > 0 && Width <= Height && (Points ?? 1) > 0;

    public static PhoneScreenSpecData? Custom(int? a, int? b)
    {
        if (a is null || b is null) return null;
        if (a < 1 || a > 20000 || b < 1 || b > 20000) return null;
        return new PhoneScreenSpecData(Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value));
    }
}

public sealed record PhoneModel(string Brand, string Name, PhoneScreenSpecData Screen)
{
    public string Id => $"{Brand} {Name}";
}

public sealed record PhoneChoice(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("screen")] PhoneScreenSpecData Screen)
{
    public static readonly PhoneChoice Standard = new(
        "Apple iPhone 17 Pro Max",
        new PhoneScreenSpecData(1320, 2868, 440));

    public static PhoneChoice FromModel(PhoneModel model) => new(model.Id, model.Screen);
}

public sealed class PhoneCatalogException : Exception
{
    public PhoneCatalogException(string message) : base($"机型库：{message}") { }
}

public sealed class PhoneCatalog
{
    public sealed record Brand(string Name, IReadOnlyList<string> Aliases, IReadOnlyList<PhoneModel> Models);

    public static readonly PhoneCatalog Empty = new(Array.Empty<Brand>());

    public IReadOnlyList<Brand> Brands { get; }

    public IEnumerable<PhoneModel> Models => Brands.SelectMany(b => b.Models);

    private PhoneCatalog(IReadOnlyList<Brand> brands)
    {
        Brands = brands;
    }

    public static PhoneCatalog FromJson(string json)
    {
        RawFile? file;
        try
        {
            file = JsonSerializer.Deserialize<RawFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            throw new PhoneCatalogException($"解不开：{ex.Message}");
        }

        if (file?.Brands is null)
            throw new PhoneCatalogException("缺少 brands 字段");

        var seenModels = new HashSet<string>(StringComparer.Ordinal);
        var seenBrands = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Brand>();

        foreach (var rawBrand in file.Brands)
        {
            if (string.IsNullOrEmpty(rawBrand.Name) || rawBrand.Models is null || rawBrand.Models.Count == 0)
                throw new PhoneCatalogException($"品牌「{rawBrand.Name}」没有名字或没有机型");

            if (!seenBrands.Add(Normalize(rawBrand.Name)))
                throw new PhoneCatalogException($"品牌「{rawBrand.Name}」重复了");

            var models = new List<PhoneModel>();
            foreach (var entry in rawBrand.Models)
            {
                if (entry.Screen is null || entry.Screen.Count != 2)
                    throw new PhoneCatalogException($"{rawBrand.Name} {entry.Name}：screen 要写成 [宽, 高]");

                var screen = new PhoneScreenSpecData(entry.Screen[0], entry.Screen[1], entry.Points);
                var model = new PhoneModel(rawBrand.Name, entry.Name ?? string.Empty, screen);
                if (string.IsNullOrEmpty(entry.Name) || !screen.IsValid)
                    throw new PhoneCatalogException($"{model.Id}：分辨率要是正数、按竖握写（短边在前）");

                if (!seenModels.Add(Normalize(model.Id)))
                    throw new PhoneCatalogException($"{model.Id}：重复了");

                models.Add(model);
            }

            result.Add(new Brand(rawBrand.Name, rawBrand.Aliases ?? new List<string>(), models));
        }

        return new PhoneCatalog(result);
    }

    public static PhoneCatalog LoadFromFile(string path) => FromJson(System.IO.File.ReadAllText(path));

    public PhoneModel? FindModel(string id) => Models.FirstOrDefault(m => m.Id == id);

    public PhoneChoice Resolve(PhoneChoice choice)
    {
        if (choice.Model is not null && FindModel(choice.Model) is { } model)
            return PhoneChoice.FromModel(model);
        return choice;
    }

    public IReadOnlyList<Brand> Search(string query)
    {
        var q = Normalize(query);
        if (string.IsNullOrEmpty(q)) return Brands;

        var matched = new List<Brand>();
        foreach (var brand in Brands)
        {
            var brandNames = new[] { brand.Name }.Concat(brand.Aliases).Select(Normalize).ToList();
            var models = brand.Models.Where(model =>
            {
                var modelNorm = Normalize(model.Name);
                return brandNames.Any(bn => (bn + modelNorm).Contains(q, StringComparison.Ordinal));
            }).ToList();

            if (models.Count > 0)
                matched.Add(new Brand(brand.Name, brand.Aliases, models));
        }
        return matched;
    }

    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var chars = text.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars);
    }

    private sealed class RawFile
    {
        public List<RawBrand>? Brands { get; set; }
    }

    private sealed class RawBrand
    {
        public string? Name { get; set; }
        public List<string>? Aliases { get; set; }
        public List<RawModel>? Models { get; set; }
    }

    private sealed class RawModel
    {
        public string? Name { get; set; }
        public List<int>? Screen { get; set; }
        public int? Points { get; set; }
    }
}
