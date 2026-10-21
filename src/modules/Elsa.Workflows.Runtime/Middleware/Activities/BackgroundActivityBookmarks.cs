using System.Text.Json;
using System.Text.Json.Nodes;
using Elsa.Workflows.Models;

namespace Elsa.Workflows.Runtime.Middleware.Activities;

/// <summary>
/// Carries the bookmarks a background activity created back to the workflow that resumes it.
/// </summary>
/// <remarks>
/// The bookmarks travel in the properties of the resume request, which a persistent bookmark queue stores with the
/// polymorphic payload serializer. That serializer writes a list as <c>{"_type": …, "_items": [...]}</c> and, when it has
/// seen the list before, without the <c>_type</c>, so the value came back as an object that does not convert to a
/// collection of bookmarks, and every background activity faulted on resume. They are written as a JSON array in a
/// string instead, which any serializer keeps as it is (the scheduled activities travel the same way), and read from
/// either shape, so a resume queued before the change still works.
/// </remarks>
internal static class BackgroundActivityBookmarks
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The bookmarks as the value to put in the resume request's properties.</summary>
    public static string Write(IEnumerable<Bookmark> bookmarks) => JsonSerializer.Serialize(bookmarks.ToList(), SerializerOptions);

    /// <summary>The bookmarks from a value <see cref="Write"/> produced, or one written before it existed; null when there are none.</summary>
    public static ICollection<Bookmark>? Read(object? value) => value switch
    {
        null => null,
        IEnumerable<Bookmark> bookmarks => bookmarks.ToList(),
        string json when !string.IsNullOrWhiteSpace(json) => Read(JsonNode.Parse(json)),
        JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array } element => Read(JsonNode.Parse(element.GetRawText())),
        JsonArray array => array.Deserialize<List<Bookmark>>(SerializerOptions),
        JsonObject wrapped when wrapped["_items"] is JsonArray items => items.Deserialize<List<Bookmark>>(SerializerOptions),
        _ => null,
    };
}
