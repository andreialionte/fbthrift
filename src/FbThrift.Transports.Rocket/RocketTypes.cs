// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using FbThrift.Core;

/// <summary>
/// Rocket frame types as defined by the RSocket protocol.
/// </summary>
public enum RocketFrameType : byte
{
    Setup = 0x01,
    Lease = 0x02,
    KeepAlive = 0x03,
    RequestResponse = 0x04,
    RequestFNF = 0x05,
    RequestStream = 0x06,
    RequestChannel = 0x07,
    RequestBidi = 0x08,
    Cancel = 0x09,
    Payload = 0x0A,
    Error = 0x0B,
    MetadataPush = 0x0C,
    Resume = 0x0D,
    ResumeOk = 0x0E,
    Ext = 0x3F,
}

/// <summary>
/// Rocket frame flags.
/// </summary>
[Flags]
public enum RocketFrameFlags : ushort
{
    None = 0,
    Ignore = 0x01,
    Metadata = 0x02,
    ResumeEnable = 0x04,
    Lease = 0x08,
    RequestResponse = 0x10,
    Complete = 0x20,
    Follows = 0x40,
    Next = 0x80,
}

/// <summary>
/// Rocket error codes as defined by RSocket.
/// </summary>
public enum RocketErrorCode : uint
{
    Reserved = 0x00,
    Setup = 0x01,
    RejectedSetup = 0x02,
    RejectedResume = 0x03,
    ConnectionError = 0x04,
    ApplicationError = 0x05,
    Rejected = 0x06,
    Canceled = 0x07,
    Invalid = 0x08,
    UnsupportedSetupParameter = 0x09,
    InvalidSetup = 0x0A,
    UnsupportedTransport = 0x0B,
    UnsupportedEncoding = 0x0C,
    MissingMetadata = 0x0D,
    InvalidPayload = 0x0E,
    UnsupportedStreaming = 0x0F,
    ChecksumMismatch = 0x10,
}

/// <summary>
/// Rocket RPC kinds as defined in fbthrift.
/// </summary>
public enum RpcKind : byte
{
    SingleRequestNoResponse = 0,
    SingleRequestSingleResponse = 1,
    SingleRequestStreamingResponse = 2,
    StreamingRequestSingleResponse = 3,
    StreamingRequestStreamingResponse = 4,
    InteractionConstruction = 5,
    InteractionTermination = 6,
    InteractionFunction = 7,
    InteractionSingleRequestNoResponse = 8,
    InteractionSingleRequestSingleResponse = 9,
    InteractionSingleRequestStreamingResponse = 10,
    InteractionStreamingRequestSingleResponse = 11,
    InteractionStreamingRequestStreamingResponse = 12,
    InteractionInteractionConstruction = 13,
    InteractionInteractionTermination = 14,
    InteractionInteractionFunction = 15,
}
