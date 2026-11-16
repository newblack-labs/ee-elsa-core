using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Middleware.Activities;
using Microsoft.Extensions.DependencyInjection;

namespace Elsa.Workflows.Runtime.UnitTests.Middleware;

/// <summary>
/// The 3.9.0 fork wrote a background activity's bookmarks as a JSON array in a string, with plain System.Text.Json
/// (web defaults). 3.10.1 writes them with the payload serializer instead (#8804). A resume queued by the 3.9.0 fork
/// and picked up after the upgrade hands the old string to the new reader, so that string must still read.
/// </summary>
public class ForkWrittenBackgroundBookmarksTests
{
    // Exactly what the 3.9.0 fork's BackgroundActivityBookmarks.Write produced for one bookmark.
    private const string WrittenByThe390Fork = """[{"id":"31368758a0e5501","name":"BackgroundActivity","hash":"00539044DBF36904","payload":{"JobId":"efaa3659"},"activityId":"req","activityNodeId":"Workflow1:flow:req","activityInstanceId":"1aee029a72cc53af","createdAt":"2026-10-21T20:44:26+00:00","autoBurn":true,"callbackMethodName":null,"autoComplete":false,"metadata":null}]""";

    private static readonly IPayloadSerializer Serializer = CreateSerializer();

    [Fact(DisplayName = "The literal is what the 3.9.0 fork's writer produces")]
    public void The_literal_matches_the_fork_writer()
    {
        var bookmark = new Bookmark(
            "31368758a0e5501", "BackgroundActivity", "00539044DBF36904", new Dictionary<string, object> { ["JobId"] = "efaa3659" },
            "req", "Workflow1:flow:req", "1aee029a72cc53af", new DateTimeOffset(2026, 10, 21, 20, 44, 26, TimeSpan.Zero), autoBurn: true, autoComplete: false);

        // The fork's Write, verbatim.
        var written = JsonSerializer.Serialize(new List<Bookmark> { bookmark }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(WrittenByThe390Fork, written);
    }

    [Fact(DisplayName = "A bookmark string written by the 3.9.0 fork reads")]
    public void A_fork_written_string_reads() =>
        AssertBookmark(Assert.Single(BackgroundActivityInvokerMiddleware.ReadBackgroundBookmarks(WrittenByThe390Fork, Serializer)!));

    [Fact(DisplayName = "A bookmark string written by the 3.9.0 fork reads after a persistent bookmark queue stored it")]
    public void A_fork_written_string_reads_after_the_queue_stored_it()
    {
        var properties = new Dictionary<string, object> { ["bookmarks"] = WrittenByThe390Fork };
        var stored = Serializer.Deserialize<Dictionary<string, object>>(Serializer.Serialize(properties));

        AssertBookmark(Assert.Single(BackgroundActivityInvokerMiddleware.ReadBackgroundBookmarks(stored["bookmarks"], Serializer)!));
    }

    private static void AssertBookmark(Bookmark bookmark)
    {
        Assert.Equal("31368758a0e5501", bookmark.Id);
        Assert.Equal("BackgroundActivity", bookmark.Name);
        Assert.Equal("00539044DBF36904", bookmark.Hash);
        Assert.Equal("req", bookmark.ActivityId);
        Assert.Equal("Workflow1:flow:req", bookmark.ActivityNodeId);
        Assert.Equal("1aee029a72cc53af", bookmark.ActivityInstanceId);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 20, 44, 26, TimeSpan.Zero), bookmark.CreatedAt);
        Assert.True(bookmark.AutoBurn);
        Assert.False(bookmark.AutoComplete);
        Assert.Null(bookmark.CallbackMethodName);
        Assert.NotNull(bookmark.Payload);
        Assert.Contains("efaa3659", Serializer.Serialize(bookmark.Payload!));
    }

    private static IPayloadSerializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddElsa();
        return services.BuildServiceProvider().GetRequiredService<IPayloadSerializer>();
    }
}
