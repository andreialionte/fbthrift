namespace FbThrift.Core;

/// <summary>
/// Request context carrying metadata and cancellation information.
/// </summary>
public class RequestContext
{
    /// <summary>
    /// Gets the singleton empty context.
    /// </summary>
    public static RequestContext Empty { get; } = new();

    /// <summary>
    /// Gets or sets headers/metadata.
    /// </summary>
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the client address.
    /// </summary>
    public string? ClientAddress { get; set; }

    /// <summary>
    /// Gets or sets additional properties.
    /// </summary>
    public IDictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
}
