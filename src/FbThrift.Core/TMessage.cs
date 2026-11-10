namespace FbThrift.Core;

/// <summary>
/// Represents a Thrift RPC message header.
/// </summary>
/// <param name="Name">The name of the remote procedure.</param>
/// <param name="Type">The message type.</param>
/// <param name="SeqId">The sequence identifier.</param>
public readonly record struct TMessage(string Name, short Type, short SeqId);

/// <summary>
/// Specifies the type of a Thrift RPC message.
/// </summary>
public enum TMessageType : short
{
    /// <summary>A remote procedure call.</summary>
    Call = 1,
    /// <summary>A reply to a remote procedure call.</summary>
    Reply = 2,
    /// <summary>An exception raised during a remote procedure call.</summary>
    Exception = 3,
    /// <summary>A one-way remote procedure call with no reply.</summary>
    Oneway = 4
}

/// <summary>
/// Marker interface for Thrift service contracts.
/// </summary>
public interface ITService
{
}
