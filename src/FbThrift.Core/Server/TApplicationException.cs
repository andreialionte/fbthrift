namespace FbThrift.Core.Server;

/// <summary>
/// Standard Thrift application exception types, matching fbthrift's <c>TApplicationException</c>.
/// </summary>
public enum TApplicationExceptionType : int
{
    Unknown = 0,
    UnknownMethod = 1,
    InvalidMessageType = 2,
    WrongMethodName = 3,
    BadSequenceId = 4,
    MissingResult = 5,
    InternalError = 6,
    ProtocolError = 7,
    InvalidTransform = 8,
    InvalidProtocol = 9,
    UnsupportedClientType = 10,
    ChecksumMismatch = 11,
    Canceled = 12
}

/// <summary>
    /// A Thrift application-level exception, sent to clients when a remote procedure fails.
    /// </summary>
    public class TApplicationException : FbThrift.Core.FbThriftException
{
    /// <summary>
    /// The exception type.
    /// </summary>
    public TApplicationExceptionType Type { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TApplicationException"/> class.
    /// </summary>
    /// <param name="type">The exception type.</param>
    /// <param name="message">The exception message.</param>
    public TApplicationException(TApplicationExceptionType type, string message)
        : base(message)
    {
        Type = type;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TApplicationException"/> class.
    /// </summary>
    /// <param name="type">The exception type.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public TApplicationException(TApplicationExceptionType type, string message, Exception innerException)
        : base(message, innerException)
    {
        Type = type;
    }
}
