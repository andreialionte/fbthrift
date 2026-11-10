using System;

namespace FBThrift;

public class ThriftProtocolException : Exception
{
    public ThriftProtocolException(string message) : base(message) { }
    public ThriftProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
