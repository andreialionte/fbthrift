// Debug test for AckFrame
using System;
using FbThrift.Core.Rocket;

var frame = new AckFrame(
    largestAcknowledged: 10,
    ackDelay: 5,
    ackRanges: new[] { (Start: 5UL, End: 10UL) });

var buffer = new byte[1024];
var written = frame.Encode(buffer);

Console.WriteLine($"Written: {written}");
Console.WriteLine($"Buffer first 20 bytes: {BitConverter.ToString(buffer, 0, Math.Min(20, written))}");

if (written > 0)
{
    var decoded = QuicFrame.Decode(buffer.AsSpan(0, written), out int consumed);
    Console.WriteLine($"Decoded: {decoded.GetType().Name}");
    if (decoded is AckFrame ack)
    {
        Console.WriteLine($"LargestAcknowledged: {ack.LargestAcknowledged}");
        Console.WriteLine($"AckDelay: {ack.AckDelay}");
        Console.WriteLine($"AckRanges.Count: {ack.AckRanges.Count}");
    }
}