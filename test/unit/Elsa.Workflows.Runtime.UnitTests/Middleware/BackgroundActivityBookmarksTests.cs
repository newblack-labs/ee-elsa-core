using Elsa.Extensions;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Middleware.Activities;
using Microsoft.Extensions.DependencyInjection;

namespace Elsa.Workflows.Runtime.UnitTests.Middleware;

/// <summary>
/// The bookmarks a background activity hands back to the workflow travel in the resume request's properties, which a
/// persistent bookmark queue stores with the payload serializer. A plain list came back in a shape that did not convert
/// to bookmarks, so every activity run asynchronously faulted on resume ("Failed to deserialize {"_items":[…]} to
/// ICollection`1[Bookmark]"); in-memory stores never serialize, which is why no test caught it.
/// </summary>
public class BackgroundActivityBookmarksTests
{
    private static readonly Bookmark Sample = new(
        "31368758a0e5501", "BackgroundActivity", "00539044DBF36904", new Dictionary<string, object> { ["JobId"] = "efaa3659" },
        "req", "Workflow1:flow:req", "1aee029a72cc53af", new DateTimeOffset(2026, 10, 21, 20, 44, 26, TimeSpan.Zero), autoBurn: true, autoComplete: false);

    [Fact(DisplayName = "Bookmarks survive the bookmark queue's serializer")]
    public void Bookmarks_survive_the_payload_serializer()
    {
        var services = new ServiceCollection();
        services.AddElsa();
        var serializer = services.BuildServiceProvider().GetRequiredService<IPayloadSerializer>();
        var properties = new Dictionary<string, object> { ["bookmarks"] = BackgroundActivityBookmarks.Write([Sample]) };

        var stored = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(properties));
        var bookmarks = BackgroundActivityBookmarks.Read(stored["bookmarks"]);

        AssertSample(Assert.Single(bookmarks!));
    }

    [Fact(DisplayName = "A resume queued before the fix, with the list wrapped in _items, still reads")]
    public void The_wrapped_shape_written_before_the_fix_reads()
    {
        // The value from the incident, as the resumed workflow received it.
        const string queuedBeforeTheFix = """{"_items":[{"id":"31368758a0e5501","name":"BackgroundActivity","hash":"00539044DBF36904","payload":{"JobId":"efaa3659"},"activityId":"req","activityNodeId":"Workflow1:flow:req","activityInstanceId":"1aee029a72cc53af","createdAt":"2026-10-21T20:44:26+00:00","autoBurn":true,"autoComplete":false}]}""";

        AssertSample(Assert.Single(BackgroundActivityBookmarks.Read(queuedBeforeTheFix)!));
    }

    [Fact(DisplayName = "A list that was never serialized (in-memory queue) reads as it is")]
    public void An_unserialized_list_reads() =>
        AssertSample(Assert.Single(BackgroundActivityBookmarks.Read(new List<Bookmark> { Sample })!));

    [Theory(DisplayName = "No bookmarks reads as none")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    public void No_bookmarks(string? value)
    {
        var bookmarks = BackgroundActivityBookmarks.Read(value);

        Assert.True(bookmarks is null or { Count: 0 });
    }

    private static void AssertSample(Bookmark bookmark)
    {
        Assert.Equal(Sample.Id, bookmark.Id);
        Assert.Equal(Sample.Name, bookmark.Name);
        Assert.Equal(Sample.Hash, bookmark.Hash);
        Assert.Equal(Sample.ActivityId, bookmark.ActivityId);
        Assert.Equal(Sample.ActivityNodeId, bookmark.ActivityNodeId);
        Assert.Equal(Sample.ActivityInstanceId, bookmark.ActivityInstanceId);
        Assert.Equal(Sample.CreatedAt, bookmark.CreatedAt);
        Assert.Equal(Sample.AutoBurn, bookmark.AutoBurn);
        Assert.Equal(Sample.AutoComplete, bookmark.AutoComplete);
        Assert.NotNull(bookmark.Payload);
    }
}
