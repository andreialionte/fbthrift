namespace FBThrift;

public class RequestContext
{
    public RequestContext()
    {
        Headers = new Dictionary<string, string>(StringComparer.Ordinal);
        Properties = new Dictionary<string, object>();
    }

    public string? ServiceName { get; set; }
    public string? MethodName { get; set; }
    public string? ClientAddress { get; set; }
    public IDictionary<string, string> Headers { get; }
    public IDictionary<string, object> Properties { get; }
}