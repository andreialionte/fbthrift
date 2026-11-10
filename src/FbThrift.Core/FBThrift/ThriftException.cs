namespace FBThrift;

public class ThriftException : Exception
{
    public ThriftException() { }
    public ThriftException(string message) : base(message) { }
    public ThriftException(string message, Exception inner) : base(message, inner) { }
}

public class ThriftApplicationException : ThriftException
{
    public int Type { get; set; }
    public ThriftApplicationException() { }
    public ThriftApplicationException(string message) : base(message) { }
    public ThriftApplicationException(int type, string message) : base(message) { Type = type; }
    public ThriftApplicationException(int type, string message, Exception inner) : base(message, inner) { Type = type; }
}