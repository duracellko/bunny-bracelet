using System.Diagnostics.CodeAnalysis;

using ByteArray = byte[];
#pragma warning disable SA1008 // Opening parenthesis should be spaced correctly
using TailResult = (byte[] data, byte[] tail);
#pragma warning restore SA1008 // Opening parenthesis should be spaced correctly

namespace BunnyBracelet.Tests;

[TestClass]
[SuppressMessage("Performance", "CA1835:Prefer the 'Memory'-based overloads for 'ReadAsync' and 'WriteAsync'", Justification = "Basic byte-array function is tested.")]
public class ExtendedTailStreamTest
{
    private static readonly Lazy<Random> Random = new Lazy<Random>(() => new Random());

    public static IEnumerable<object[]> ReadEmptyDataTestData { get; } = new object[][]
    {
        new object[] { 0 },
        new object[] { 10 },
        new object[] { 1000 }
    };

    public static IEnumerable<object[]> ReadTailIsShorterThanDataTestData { get; } = new object[][]
    {
        new object[] { 120, 0 },
        new object[] { 1, 0 },
        new object[] { 5000, 0 },
        new object[] { 1, 1 },
        new object[] { 120, 120 },
        new object[] { 5000, 5000 },
        new object[] { 120, 24 },
        new object[] { 120, 4 },
        new object[] { 5000, 120 },
        new object[] { 9999, 5001 }
    };

    public static IEnumerable<object[]> ReadTailIsLongerThanDataTestData { get; } = new object[][]
    {
        new object[] { 120, 121 },
        new object[] { 1, 2 },
        new object[] { 5000, 10000 },
        new object[] { 1, 120 },
        new object[] { 120, 4000 },
        new object[] { 5000, 5432 },
        new object[] { 9999, 10000 }
    };

    public static IEnumerable<object[]> ReadReplaceDataTestData { get; } = new object[][]
    {
        new object[] { 120, 0 },
        new object[] { 1, 0 },
        new object[] { 5000, 0 },
        new object[] { 1, 1 },
        new object[] { 120, 120 },
        new object[] { 5000, 5000 },
        new object[] { 120, 24 },
        new object[] { 5000, 120 },
        new object[] { 9999, 5001 }
    };

    public static IEnumerable<object[]> ReadTailSizeAndReplaceDataTestData { get; } = new object[][]
    {
        new object[] { 1, 1, 0 },
        new object[] { 1, 1, 1 },
        new object[] { 120, 120, 120 },
        new object[] { 5000, 5000, 5000 },
        new object[] { 120, 32, 0 },
        new object[] { 120, 32, 20 },
        new object[] { 120, 120, 32 },
        new object[] { 4321, 21, 120 },
        new object[] { 4321, 20, 121 },
        new object[] { 5000, 5000, 1 },
        new object[] { 5000, 1, 5000 },
        new object[] { 5000, 100, 120 },
        new object[] { 9999, 5001, 4321 }
    };

    public static IEnumerable<object[]> ReadIndexOrCountIsInvalidTestData { get; } = new object[][]
    {
        new object[] { -1, 10 },
        new object[] { 0, 0 },
        new object[] { 1, -1 },
        new object[] { 0, 51 },
        new object[] { 50, 50 },
        new object[] { 25, 26 },
        new object[] { 50, 1 }
    };

    [TestMethod]
    public void Constructor_StreamIsNull_ArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new ExtendedTailStream(null!, 1));
    }

    [TestMethod]
    public void Constructor_NegativeTailSize_ArgumentNullException()
    {
        var stream = new MemoryStream(120);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ExtendedTailStream(stream, -1));
    }

    [TestMethod]
    [DynamicData(nameof(ReadEmptyDataTestData))]
    public async Task ReadAsync_EmptyData_ReadsNoData(int tailSize)
    {
        var result = await ExtendTailAsync(Array.Empty<byte>(), tailSize);

        Assert.AreEqual(0, result.data.Length);
        Assert.AreEqual(tailSize, result.tail.Length);
        AssertBytesAreZero(result.tail);
    }

    [TestMethod]
    [DynamicData(nameof(ReadTailIsShorterThanDataTestData))]
    public async Task ReadAsync_TailIsShorterThanData_SkipTail(int dataSize, int tailSize)
    {
        var data = GetRandomBytes(dataSize);

        var result = await ExtendTailAsync(data, tailSize);

        Assert.AreEqual(dataSize - tailSize, result.data.Length);
        CollectionAssert.AreEqual(data.SkipLast(tailSize).ToArray(), result.data);
        Assert.AreEqual(tailSize, result.tail.Length);
        CollectionAssert.AreEqual(data.TakeLast(tailSize).ToArray(), result.tail);
    }

    [TestMethod]
    [DynamicData(nameof(ReadTailIsLongerThanDataTestData))]
    public async Task ReadAsync_TailIsLongerThanData_NoData(int dataSize, int tailSize)
    {
        var data = GetRandomBytes(dataSize);

        var result = await ExtendTailAsync(data, tailSize);

        Assert.AreEqual(0, result.data.Length);
        Assert.AreEqual(tailSize, result.tail.Length);
        CollectionAssert.AreEqual(data, result.tail.Take(dataSize).ToArray());
        AssertBytesAreZero(data, tailSize);
    }

    [TestMethod]
    [DynamicData(nameof(ReadReplaceDataTestData))]
    public async Task ReadAsync_ReplaceData_DataAreExtended(int dataSize, int replaceSize)
    {
        var data = GetRandomBytes(dataSize);
        var replaceData = GetRandomBytes(replaceSize);

        var result = await ExtendTailAsync(data, 0, replaceData);

        Assert.AreEqual(dataSize + replaceSize, result.data.Length);
        CollectionAssert.AreEqual(data.Concat(replaceData).ToArray(), result.data);
        Assert.AreEqual(0, result.tail.Length);
    }

    [TestMethod]
    [DynamicData(nameof(ReadTailSizeAndReplaceDataTestData))]
    public async Task ReadAsync_TailSizeAndReplaceData_TailIsReplaced(int dataSize, int tailSize, int replaceSize)
    {
        var data = GetRandomBytes(dataSize);
        var replaceData = GetRandomBytes(replaceSize);

        var result = await ExtendTailAsync(data, tailSize, replaceData);

        Assert.AreEqual(dataSize - tailSize + replaceSize, result.data.Length);
        CollectionAssert.AreEqual(data.SkipLast(tailSize).Concat(replaceData).ToArray(), result.data);
        Assert.AreEqual(tailSize, result.tail.Length);
        CollectionAssert.AreEqual(data.TakeLast(tailSize).ToArray(), result.tail);
    }

    [TestMethod]
    public async Task ReadAsync_BufferIsNull_ArgumentNullException()
    {
        using var stream = new MemoryStream(120);
        using var target = new ExtendedTailStream(stream, 0);

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () => await target.ReadAsync(null!, 0, 10));
    }

    [TestMethod]
    [DynamicData(nameof(ReadIndexOrCountIsInvalidTestData))]
    public async Task ReadAsync_IndexOrCountIsInvalid_ArgumentOutOfRangeException(int index, int count)
    {
        using var stream = new MemoryStream(120);
        using var target = new ExtendedTailStream(stream, 0);

        var buffer = new byte[50];
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(async () => await target.ReadAsync(buffer, index, count));
    }

    [TestMethod]
    [DynamicData(nameof(ReadEmptyDataTestData))]
    public void Read_EmptyData_ReadsNoData(int tailSize)
    {
        var result = ExtendTail(Array.Empty<byte>(), tailSize);

        Assert.AreEqual(0, result.data.Length);
        Assert.AreEqual(tailSize, result.tail.Length);
        AssertBytesAreZero(result.tail);
    }

    [TestMethod]
    [DynamicData(nameof(ReadTailIsShorterThanDataTestData))]
    public void Read_TailIsShorterThanData_SkipTail(int dataSize, int tailSize)
    {
        var data = GetRandomBytes(dataSize);

        var result = ExtendTail(data, tailSize);

        Assert.AreEqual(dataSize - tailSize, result.data.Length);
        CollectionAssert.AreEqual(data.SkipLast(tailSize).ToArray(), result.data);
        Assert.AreEqual(tailSize, result.tail.Length);
        CollectionAssert.AreEqual(data.TakeLast(tailSize).ToArray(), result.tail);
    }

    [TestMethod]
    [DynamicData(nameof(ReadTailIsLongerThanDataTestData))]
    public void Read_TailIsLongerThanData_NoData(int dataSize, int tailSize)
    {
        var data = GetRandomBytes(dataSize);

        var result = ExtendTail(data, tailSize);

        Assert.AreEqual(0, result.data.Length);
        Assert.AreEqual(tailSize, result.tail.Length);
        CollectionAssert.AreEqual(data, result.tail.Take(dataSize).ToArray());
        AssertBytesAreZero(data, tailSize);
    }

    [TestMethod]
    [DynamicData(nameof(ReadReplaceDataTestData))]
    public void Read_ReplaceData_DataAreExtended(int dataSize, int replaceSize)
    {
        var data = GetRandomBytes(dataSize);
        var replaceData = GetRandomBytes(replaceSize);

        var result = ExtendTail(data, 0, replaceData);

        Assert.AreEqual(dataSize + replaceSize, result.data.Length);
        CollectionAssert.AreEqual(data.Concat(replaceData).ToArray(), result.data);
        Assert.AreEqual(0, result.tail.Length);
    }

    [TestMethod]
    [DynamicData(nameof(ReadTailSizeAndReplaceDataTestData))]
    public void Read_TailSizeAndReplaceData_TailIsReplaced(int dataSize, int tailSize, int replaceSize)
    {
        var data = GetRandomBytes(dataSize);
        var replaceData = GetRandomBytes(replaceSize);

        var result = ExtendTail(data, tailSize, replaceData);

        Assert.AreEqual(dataSize - tailSize + replaceSize, result.data.Length);
        CollectionAssert.AreEqual(data.SkipLast(tailSize).Concat(replaceData).ToArray(), result.data);
        Assert.AreEqual(tailSize, result.tail.Length);
        CollectionAssert.AreEqual(data.TakeLast(tailSize).ToArray(), result.tail);
    }

    [TestMethod]
    public void Read_BufferIsNull_ArgumentNullException()
    {
        using var stream = new MemoryStream(120);
        using var target = new ExtendedTailStream(stream, 0);

        Assert.ThrowsExceptionAsync<ArgumentNullException>(async () => await target.ReadAsync(null!, 0, 10));
    }

    [TestMethod]
    [DynamicData(nameof(ReadIndexOrCountIsInvalidTestData))]
    public void Read_IndexOrCountIsInvalid_ArgumentOutOfRangeException(int index, int count)
    {
        using var stream = new MemoryStream(120);
        using var target = new ExtendedTailStream(stream, 0);

        var buffer = new byte[50];
        Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(async () => await target.ReadAsync(buffer, index, count));
    }

    [TestMethod]
    public async Task Close_Reading_ObjectDisposedException()
    {
        using var stream = new MemoryStream(120);
        using var target = new ExtendedTailStream(stream, 0);

        target.Close();

        var buffer = new byte[50];
        Assert.ThrowsException<ObjectDisposedException>(() => stream.Read(buffer));
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await stream.ReadAsync(buffer));
    }

    [TestMethod]
    public void Close_InnerStreamIsClosed()
    {
        using var stream = new MemoryStream(120);
        using var target = new ExtendedTailStream(stream, 0);

        target.Close();

        Assert.ThrowsException<ObjectDisposedException>(() => stream.ReadByte());
    }

    [TestMethod]
    public async Task Dispose_Reading_ObjectDisposedException()
    {
        using var stream = new MemoryStream(120);
        using var target = new ExtendedTailStream(stream, 0);

        await target.DisposeAsync();

        var buffer = new byte[50];
        Assert.ThrowsException<ObjectDisposedException>(() => stream.Read(buffer));
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await stream.ReadAsync(buffer));
    }

    [TestMethod]
    public void Dispose_InnerStreamIsClosed()
    {
        using var stream = new MemoryStream(120);
        using var target = new ExtendedTailStream(stream, 0);

        target.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() => stream.ReadByte());
    }

    private static async Task<TailResult> ExtendTailAsync(byte[] data, int tailSize)
    {
        return await ExtendTailAsync(data, tailSize, null);
    }

    private static async Task<TailResult> ExtendTailAsync(byte[] data, int tailSize, ByteArray? replace)
    {
        var replaceFunction = replace is not null ? new Func<ReadOnlyMemory<byte>>(() => replace.AsMemory()) : null;
        await using var dataStream = new MemoryStream(data);
        await using var tailStream = new ExtendedTailStream(dataStream, tailSize, replaceFunction);
        await using var destinationStream = new MemoryStream();

        await tailStream.CopyToAsync(destinationStream, 100);

        return (destinationStream.ToArray(), tailStream.Tail);
    }

    private static TailResult ExtendTail(byte[] data, int tailSize)
    {
        return ExtendTail(data, tailSize, null);
    }

    private static TailResult ExtendTail(byte[] data, int tailSize, ByteArray? replace)
    {
        var replaceFunction = replace is not null ? new Func<ReadOnlyMemory<byte>>(() => replace.AsMemory()) : null;
        using var dataStream = new MemoryStream(data);
        using var tailStream = new ExtendedTailStream(dataStream, tailSize, replaceFunction);
        using var destinationStream = new MemoryStream();

        tailStream.CopyTo(destinationStream, 100);

        return (destinationStream.ToArray(), tailStream.Tail);
    }

    private static void AssertBytesAreZero(byte[] data) => AssertBytesAreZero(data, 0, data.Length);

    private static void AssertBytesAreZero(byte[] data, int index)
    {
        AssertBytesAreZero(data, index, data.Length - index);
    }

    private static void AssertBytesAreZero(byte[] data, int index, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Assert.AreEqual(0, data[index + i]);
        }
    }

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Random is sufficient for test data.")]
    private static byte[] GetRandomBytes(int length)
    {
        var result = new byte[length];
        Random.Value.NextBytes(result);
        return result;
    }
}
