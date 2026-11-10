namespace FbThrift.Core;

/// <summary>
/// Base exception for all errors thrown by the FBThrift client library.
/// </summary>
public class FbThriftException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FbThriftException"/> class.
    /// </summary>
    public FbThriftException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FbThriftException"/> class with a message.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public FbThriftException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FbThriftException"/> class with a message and inner exception.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public FbThriftException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
