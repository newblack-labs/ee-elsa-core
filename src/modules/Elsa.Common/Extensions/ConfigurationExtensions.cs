using System.Text.Json;
using Microsoft.Extensions.Configuration;

// ReSharper disable once CheckNamespace
namespace Elsa.Extensions;

public static class ConfigurationExtensions
{
    public static string? GetSectionAsJson(this IConfiguration configuration, string sectionKey, JsonSerializerOptions? options = null)
    {
        var section = configuration.GetSection(sectionKey);
        var element = ConvertToJsonElement(section);
        return element.ValueKind == JsonValueKind.Null ? null : JsonSerializer.Serialize(element, options);
    }

    private static JsonElement ConvertToJsonElement(IConfigurationSection section)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ToObject(section)));
        return doc.RootElement.Clone();
    }

    private static object? ToObject(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (!children.Any())
        {
            var value = section.Value;
            if (value == null) return null;
            if (bool.TryParse(value, out var b)) return b;
            if (long.TryParse(value, out var l)) return l;
            if (double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
            return value;
        }

        if (children.All(c => int.TryParse(c.Key, out _)))
        {
            // Treat as array
            return children
                .OrderBy(c => int.Parse(c.Key))
                .Select(ToObject)
                .ToList();
        }

        var dict = new Dictionary<string, object?>();
        foreach (var child in children)
        {
            dict[child.Key] = ToObject(child);
        }

        return dict;
    }
}