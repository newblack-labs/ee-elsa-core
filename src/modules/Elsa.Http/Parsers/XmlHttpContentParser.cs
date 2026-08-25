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

            // The body is attacker-controlled and unredacted: for a SOAP caller it carries WS-Security
            // credentials, and for any caller it can carry personal data. Report the failure at Error
            // without it, and keep the body itself at Debug so it is available when someone is actively
            // diagnosing a malformed payload but is not emitted at production log levels — where it
            // would also be served to the browser by the console-log diagnostics stream.
            //
            // Note for anyone reaching for that Debug line: a host overriding only "Elsa" to Debug will
            // not see it if it also pins this namespace higher, and raising it host-wide puts the body
            // on the console stream for every caller. Prefer an override scoped to
            // Elsa.Http.Parsers. The exception still carries the preview either way, so the
            // per-instance execution journal has it without any log level change.
            //
            // Length is a character count, not a byte count; for a UTF-8 body the two differ.
            _logger.LogError(ex, "Failed to parse XML content ({Length} characters).", xml.Length);
            _logger.LogDebug("Raw body that failed to parse as XML:\n{RawBody}", preview);

            // Wrap with raw body so upstream (HttpEndpoint) can log it in the execution log.
            throw new XmlParseException(ex.Message, preview, ex);
        }
    }
}