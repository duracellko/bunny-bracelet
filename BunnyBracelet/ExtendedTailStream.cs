using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace BunnyBracelet;

/// <summary>
/// Defines a stream that replaces or removes tail of another stream.
/// </summary>
public sealed class ExtendedTailStream : Stream
{
    private readonly Stream stream;
    private readonly int tailSize;
    private readonly Func<ReadOnlyMemory<byte>>? replace;

    private bool isDisposed;

    // 0 - not started reading. Read operation should initialize currentTail first.
    // 1 - currentTail initialized. Read operation should copy bytes and shift tail.
    // 2 - reached end of inner stream. Read operation should read from replaceTail.
    private int state;
    private byte[] currentTail;
    private ReadOnlyMemory<byte> replaceTail;

    public ExtendedTailStream(Stream stream, int tailSize)
        : this(stream, tailSize, null)
    {
    }

    public ExtendedTailStream(Stream stream, int tailSize, Func<ReadOnlyMemory<byte>>? replace)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(tailSize);

        this.stream = stream;
        this.tailSize = tailSize;
        this.replace = replace;

        if (tailSize == 0)
        {
            Tail = currentTail = [];
        }
        else
        {
            Tail = new byte[tailSize];
            currentTail = ArrayPool<byte>.Shared.Rent(tailSize);
            currentTail.AsSpan(0, tailSize).Clear();
        }
    }

    public override bool CanRead => true;

    public override bool CanWrite => false;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Tail is byte-array from inner stream.")]
    public byte[] Tail { get; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, buffer.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, buffer.Length, nameof(count));

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        CheckNotDisposed();

        // First read operation. Initialize current tail.
        if (state == 0)
        {
            var isEndOfStream = ReadCurrentTail(stream);
            if (isEndOfStream)
            {
                CopyCurrentTail();
                StartReplaceMode();
                return CopyReplaceBytes(buffer);
            }

            state = 1;
        }

        // Reached end of stream. Read bytes from replaceTail.
        if (state == 2)
        {
            return CopyReplaceBytes(buffer);
        }

        // Read bytes from inner stream and shift tail.
        var sourceBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var sourceBufferSpan = sourceBuffer.AsSpan(0, buffer.Length);
            var bytesRead = stream.Read(sourceBufferSpan);

            if (bytesRead == 0)
            {
                CopyCurrentTail();
                StartReplaceMode();
                return CopyReplaceBytes(buffer);
            }

            return CopyBytesAndUpdateTail(sourceBufferSpan[..bytesRead], buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer);
        }

        bool ReadCurrentTail(Stream stream)
        {
            if (tailSize == 0)
            {
                return false;
            }

            var tailSpan = currentTail.AsSpan(0, tailSize);
            var bytesRead = stream.ReadAtLeast(tailSpan, tailSize, false);
            return bytesRead < tailSize;
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, buffer.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, buffer.Length, nameof(count));

        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckNotDisposed();

        // First read operation. Initialize current tail.
        if (state == 0)
        {
            var isEndOfStream = await ReadCurrentTailAsync(stream, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (isEndOfStream)
            {
                CopyCurrentTail();
                StartReplaceMode();
                return CopyReplaceBytes(buffer.Span);
            }

            state = 1;
        }

        // Reached end of stream. Read bytes from replaceTail.
        if (state == 2)
        {
            return CopyReplaceBytes(buffer.Span);
        }

        // Read bytes from inner stream and shift tail.
        var sourceBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var sourceBufferMemory = sourceBuffer.AsMemory(0, buffer.Length);
            var bytesRead = await stream.ReadAsync(sourceBufferMemory, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (bytesRead == 0)
            {
                CopyCurrentTail();
                StartReplaceMode();
                return CopyReplaceBytes(buffer.Span);
            }

            return CopyBytesAndUpdateTail(sourceBufferMemory.Span[..bytesRead], buffer.Span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer);
        }

        async ValueTask<bool> ReadCurrentTailAsync(Stream stream, CancellationToken cancellationToken)
        {
            if (tailSize == 0)
            {
                return false;
            }

            var tailMemory = currentTail.AsMemory(0, tailSize);
            var bytesRead = await stream.ReadAtLeastAsync(tailMemory, tailSize, false, cancellationToken);
            return bytesRead < tailSize;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override void Flush()
    {
        CheckNotDisposed();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!isDisposed)
            {
                if (currentTail.Length > 0)
                {
                    ArrayPool<byte>.Shared.Return(currentTail);
                    currentTail = [];
                }

                stream.Dispose();
                isDisposed = true;
            }
        }

        base.Dispose(disposing);
    }

    private void CheckNotDisposed()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
    }

    private int CopyBytesAndUpdateTail(ReadOnlySpan<byte> source, Span<byte> buffer)
    {
        var bytesRead = source.Length;
        var tail = currentTail.AsSpan(0, tailSize);

        if (tailSize > 0)
        {
            // Copy bytes from current tail to destination buffer, then shift bytes in buffer to start,
            // and then replenish current tail from source.
            // Byte flow is following: buffer << currentTail << source
            var tailShiftSize = Math.Min(bytesRead, tailSize);
            tail[..tailShiftSize].CopyTo(buffer);
            buffer = buffer[tailShiftSize..];

            if (tailShiftSize == tailSize)
            {
                source[..(bytesRead - tailSize)].CopyTo(buffer);
                source = source[(bytesRead - tailSize)..];
            }
            else
            {
                for (var i = 0; i < tailSize - tailShiftSize; i++)
                {
                    tail[i] = tail[i + tailShiftSize];
                }
            }

            source.CopyTo(tail[(tailSize - tailShiftSize)..]);
        }
        else
        {
            source.CopyTo(buffer);
        }

        return bytesRead;
    }

    private int CopyReplaceBytes(Span<byte> buffer)
    {
        if (replaceTail.IsEmpty)
        {
            return 0;
        }

        var length = Math.Min(buffer.Length, replaceTail.Length);
        replaceTail.Span[..length].CopyTo(buffer);
        replaceTail = replaceTail[length..];
        return length;
    }

    private void CopyCurrentTail()
    {
        if (tailSize > 0)
        {
            currentTail.AsSpan(0, tailSize).CopyTo(Tail);
        }
    }

    private void StartReplaceMode()
    {
        if (replace is not null)
        {
            replaceTail = replace();
        }

        state = 2;
    }
}
