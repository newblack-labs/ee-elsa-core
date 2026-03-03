namespace Elsa.Http.Parsers;

/// <summary>
/// Thrown when XML content parsing fails. Carries the raw body for diagnostic logging.
/// </summary>
public class XmlParseException : Exception
{
    public string RawBody { get; }

    public XmlParseException(string message, string rawBody, Exception innerException)
        : base(message, innerException)
    {
        RawBody = rawBody;
    }
}
