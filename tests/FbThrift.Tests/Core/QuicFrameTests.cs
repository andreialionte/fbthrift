namespace FbThrift.Tests.Core;

using FbThrift.Core.Rocket;
using Xunit;

public class QuicFrameTests
{
    [Fact]
    public void StreamFrame_EncodeDecode_RoundTrip()
    {
        var frame = new StreamFrame(
            streamId: 4,
            offset: 0,
            data: System.Text.Encoding.UTF8.GetBytes("hello"),
            fin: true);

        var buffer = new byte[1024];
        var written = frame.Encode(buffer);
        
        Assert.True(written > 0);
        
        var decoded = QuicFrame.Decode(buffer.AsSpan(0, written), out int consumed);
        
        Assert.Equal(written, consumed);
        Assert.IsType<StreamFrame>(decoded);
        
        var streamFrame = (StreamFrame)decoded;
        Assert.Equal(4UL, streamFrame.StreamId);
        Assert.Equal(0UL, streamFrame.Offset);
        Assert.True(streamFrame.Fin);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(streamFrame.Data.Span));
    }

    [Fact]
    public void AckFrame_EncodeDecode_RoundTrip()
    {
        var ranges = new (ulong Start, ulong End)[] { (5UL, 10UL) };
        var frame = new AckFrame(
            largestAcknowledged: 10,
            ackDelay: 5,
            ackRanges: ranges);

        var buffer = new byte[1024];
        var written = frame.Encode(buffer);
        
        Assert.True(written > 0);
        
        var span = buffer.AsSpan(0, written);
        
        Assert.True(span.Length > 0);
        
        // Additional check - verify the span contents
        var firstByte = span[0];
        Assert.Equal((byte)QuicFrameType.Ack, firstByte);
        
        var decoded = QuicFrame.Decode(span, out int consumed);
        
        Assert.Equal(written, consumed);
        Assert.IsType<AckFrame>(decoded);
        
        var ackFrame = (AckFrame)decoded;
        Assert.Equal(10UL, ackFrame.LargestAcknowledged);
        Assert.Equal(5UL, ackFrame.AckDelay);
        Assert.Single(ackFrame.AckRanges);
    }

    [Fact]
    public void CryptoFrame_EncodeDecode_RoundTrip()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("client hello");
        var frame = new CryptoFrame(offset: 0, data);

        var buffer = new byte[1024];
        var written = frame.Encode(buffer);
        
        Assert.True(written > 0);
        
        var decoded = QuicFrame.Decode(buffer.AsSpan(0, written), out int consumed);
        
        Assert.Equal(written, consumed);
        Assert.IsType<CryptoFrame>(decoded);
        
        var cryptoFrame = (CryptoFrame)decoded;
        Assert.Equal(0UL, cryptoFrame.Offset);
        Assert.Equal(data, cryptoFrame.Data.ToArray());
    }

    [Fact]
    public void ConnectionCloseFrame_EncodeDecode_RoundTrip()
    {
        var frame = new ConnectionCloseFrame(
            errorCode: 0x1C,
            frameType: 0,
            reason: "test reason");

        var buffer = new byte[1024];
        var written = frame.Encode(buffer);
        
        Assert.True(written > 0);
        
        var decoded = QuicFrame.Decode(buffer.AsSpan(0, written), out int consumed);
        
        Assert.Equal(written, consumed);
        Assert.IsType<ConnectionCloseFrame>(decoded);
        
        var closeFrame = (ConnectionCloseFrame)decoded;
        Assert.Equal(0x1CUL, closeFrame.ErrorCode);
        Assert.Equal("test reason", closeFrame.Reason);
    }
}

public class QuicConnectionStateMachineTests
{
    [Fact]
    public void InitialState_IsInitial()
    {
        var stateMachine = new QuicConnectionStateMachine();
        
        Assert.Equal(QuicConnectionState.Initial, stateMachine.State);
    }

    [Fact]
    public void CanCreateBidirectionalStream()
    {
        var stateMachine = new QuicConnectionStateMachine();
        
        var result = stateMachine.TryCreateStream(out long streamId, bidi: true);
        
        Assert.True(result);
        Assert.Equal(0L, streamId);
    }

    [Fact]
    public void CanCreateUnidirectionalStream()
    {
        var stateMachine = new QuicConnectionStateMachine();
        
        var result = stateMachine.TryCreateStream(out long streamId, bidi: false);
        
        Assert.True(result);
        Assert.Equal(0L, streamId); // First uni stream is also 0
    }

    [Fact]
    public void StreamStateTransitions()
    {
        var stateMachine = new QuicConnectionStateMachine();
        
        stateMachine.TryCreateStream(out long streamId, bidi: true);
        
        Assert.True(stateMachine.TryGetStream(streamId, out var stream));
        Assert.Equal(StreamStateEnum.Ready, stream.State);
        
        stateMachine.UpdateStreamState(streamId, StreamStateEnum.Sending);
        Assert.True(stateMachine.TryGetStream(streamId, out stream));
        Assert.Equal(StreamStateEnum.Sending, stream.State);
        
        stateMachine.UpdateStreamState(streamId, StreamStateEnum.SendComplete);
        Assert.True(stateMachine.TryGetStream(streamId, out stream));
        Assert.Equal(StreamStateEnum.SendComplete, stream.State);
    }

    [Fact]
    public void ConnectionLifecycle()
    {
        var stateMachine = new QuicConnectionStateMachine();
        
        Assert.Equal(QuicConnectionState.Initial, stateMachine.State);
        
        stateMachine.Close();
        Assert.Equal(QuicConnectionState.Closing, stateMachine.State);
        
        stateMachine.Drain();
        Assert.Equal(QuicConnectionState.Draining, stateMachine.State);
        
        stateMachine.CloseComplete();
        Assert.Equal(QuicConnectionState.Closed, stateMachine.State);
    }

    [Fact]
    public void ActivityTracking()
    {
        var stateMachine = new QuicConnectionStateMachine();
        
        var before = stateMachine.LastActivity;
        System.Threading.Thread.Sleep(10);
        stateMachine.RecordActivity();
        var after = stateMachine.LastActivity;
        
        Assert.True(after > before);
    }

    [Fact]
    public void ByteCounters()
    {
        var stateMachine = new QuicConnectionStateMachine();
        
        Assert.Equal(0L, stateMachine.BytesSent);
        Assert.Equal(0L, stateMachine.BytesReceived);
        
        stateMachine.RecordBytesSent(100);
        stateMachine.RecordBytesReceived(200);
        
        Assert.Equal(100L, stateMachine.BytesSent);
        Assert.Equal(200L, stateMachine.BytesReceived);
    }
}

public class ChecksumUtilTests
{
    [Fact]
    public void ComputeChecksum_Crc32_RoundTrip()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("test data");
        var checksum = ChecksumUtil.ComputeChecksum(ChecksumAlgorithm.Crc32, data);
        
        Assert.True(ChecksumUtil.VerifyChecksum(ChecksumAlgorithm.Crc32, data, checksum));
    }

    [Fact]
    public void VerifyChecksum_WrongData_Fails()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("test data");
        var wrongData = System.Text.Encoding.UTF8.GetBytes("wrong data");
        var checksum = ChecksumUtil.ComputeChecksum(ChecksumAlgorithm.Crc32, data);
        
        Assert.False(ChecksumUtil.VerifyChecksum(ChecksumAlgorithm.Crc32, wrongData, checksum));
    }
}