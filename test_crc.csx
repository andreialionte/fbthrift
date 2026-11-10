// Quick test to find the correct CRC32 value
using System;
using System.Text;

var data = Encoding.UTF8.GetBytes("hello world");
uint crc = 0xFFFFFFFF;
const uint polynomial = 0xEDB88320;

foreach (byte b in data)
{
    crc ^= b;
    for (int i = 0; i < 8; i++)
    {
        crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320 : 0);
    }
}

Console.WriteLine($"CRC32: {~crc:X8}"); // 0xD4A1185E is the standard
Console.WriteLine($"CRC32 (decimal): {~crc}");