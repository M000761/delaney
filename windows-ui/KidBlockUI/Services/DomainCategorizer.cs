using System.IO;
using System.Text.Json;

namespace KidBlockUI.Services;

public sealed class DomainCategorizer
{
    public IReadOnlyList<string> CategoryOrder { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CategoryDomains { get; }

    private readonly Dictionary<string, string> _domainToCategory;

    private DomainCategorizer(
        IReadOnlyList<string> categoryOrder,
        IReadOnlyDictionary<string, IReadOnlyList<string>> categoryDomains,
        Dictionary<string, string> domainToCategory)
    {
        CategoryOrder    = categoryOrder;
        CategoryDomains  = categoryDomains;
        _domainToCategory = domainToCategory;
    }

    public string? CategoryFor(string domain) =>
        _domainToCategory.TryGetValue(domain, out var c) ? c : null;

    public static DomainCategorizer Load()
    {
        var resourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "domain-categories.json");
        if (!File.Exists(resourcePath)) return Empty();
        try
        {
            using var stream = File.OpenRead(resourcePath);
            using var doc = JsonDocument.Parse(stream);
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    internal static DomainCategorizer Parse(JsonElement root)
    {
        if (!root.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Object)
            return Empty();

        var order  = new List<string>();
        var byCat  = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var byDom  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in cats.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            var domains = new List<string>();
            foreach (var item in prop.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var v = item.GetString();
                if (string.IsNullOrWhiteSpace(v)) continue;
                domains.Add(v);
                // First-occurrence wins on duplicates across categories (deterministic).
                if (!byDom.ContainsKey(v)) byDom[v] = prop.Name;
            }
            order.Add(prop.Name);
            byCat[prop.Name] = domains;
        }
        return new DomainCategorizer(order, byCat, byDom);
    }

    private static DomainCategorizer Empty() =>
        new(
            new List<string>(),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
