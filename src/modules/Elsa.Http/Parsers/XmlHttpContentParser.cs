using System.Xml;
using System.Xml.Serialization;
using Elsa.Http.Contexts;
using Microsoft.Extensions.Logging;

namespace Elsa.Http.Parsers;

/// <summary>
/// Reads application/xml and text/xml content type streams.
/// </summary>
public class XmlHttpContentParser : IHttpContentParser
{
    private readonly ILogger<XmlHttpContentParser> _logger;

    public XmlHttpContentParser(ILogger<XmlHttpContentParser> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public int Priority => 0;

    /// <inheritdoc />
    public bool GetSupportsContentType(HttpResponseParserContext context) => context.ContentType.Contains("xml", StringComparison.InvariantCultureIgnoreCase);

    /// <inheritdoc />
    public async Task<object> ReadAsync(HttpResponseParserContext context)
    {
        var content = context.Content;
        using var reader = new StreamReader(content, leaveOpen: true);
        var xml = await reader.ReadToEndAsync();
        var returnType = context.ReturnType;

        if (returnType == null || returnType == typeof(string))
            return xml;

        try
        {
            var serializer = new XmlSerializer(returnType);
            using var stringReader = new StringReader(xml);
            return serializer.Deserialize(stringReader)!;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            var preview = xml.Length > 2000 ? xml[..2000] + "... (truncated)" : xml;
            _logger.LogError(ex, "Failed to parse XML content. Raw body:\n{RawBody}", preview);

            // Wrap with raw body so upstream (HttpEndpoint) can log it in the execution log.
            throw new XmlParseException(ex.Message, preview, ex);
        }
    }
}