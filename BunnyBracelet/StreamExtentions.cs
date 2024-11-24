using System.Buffers;

namespace BunnyBracelet;

/// <summary>
/// Operations on <see cref="System.IO.Stream"/> object.
/// </summary>
public static class StreamExtentions
{
    public static async ValueTask ReadToEndAsync(this Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var arrayPool = ArrayPool<byte>.Shared;
        var buffer = arrayPool.Rent(128);
        try
        {
            // 128 bytes is size of 2 blocks of SHA256.
            var bufferMemory = buffer.AsMemory()[..128];

            var bytesRead = await stream.ReadAsync(buffer);
            while (bytesRead > 0)
            {
                bytesRead = await stream.ReadAsync(buffer);
            }
        }
        finally
        {
            arrayPool.Return(buffer);
        }
    }
}
