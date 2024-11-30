using System.Diagnostics.CodeAnalysis;
using RabbitMQ.Client;

namespace BunnyBracelet.Tests;

[TestClass]
public class MessageTest
{
    private static readonly DateTime TestTimestamp = new(2024, 8, 11, 23, 2, 32, 922, 129, DateTimeKind.Utc);
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Y2K38 = new(2038, 1, 19, 3, 14, 7, DateTimeKind.Unspecified);

    private static readonly byte[] TestBody = Guid.NewGuid().ToByteArray();
    private static readonly BasicProperties TestBasicProperties = CreateBasicProperties();

    public static IEnumerable<object?[]> EqualsTestData { get; } =
    [
        [null, null, default(DateTime)],
        [null, null, TestTimestamp],
        [new ReadOnlyMemory<byte>(null), new BasicProperties(), TestTimestamp],
        [new ReadOnlyMemory<byte>([]), CreateBasicProperties(), DateTime.SpecifyKind(TestTimestamp, DateTimeKind.Unspecified)],
        [new ReadOnlyMemory<byte>([1, 2, 3]), TestBasicProperties, UnixEpoch],
        [new ReadOnlyMemory<byte>(TestBody), null, Y2K38],
    ];

    [SuppressMessage("Performance", "CA1825:Avoid zero-length array allocations", Justification = "Test new array instance.")]
    [SuppressMessage("Style", "IDE0300:Simplify collection initialization", Justification = "Test new array instance.")]
    public static IEnumerable<object?[]> NonEqualsTestData { get; } =
    [
        [null, null, default(DateTime), new ReadOnlyMemory<byte>([]), null, default(DateTime)],
        [new ReadOnlyMemory<byte>(new byte[0]), null, UnixEpoch, new ReadOnlyMemory<byte>([]), null, UnixEpoch],
        [new ReadOnlyMemory<byte>([1, 2, 3]), null, TestTimestamp, new ReadOnlyMemory<byte>([1, 2, 3]), null, TestTimestamp],
        [new ReadOnlyMemory<byte>(Guid.NewGuid().ToByteArray()), null, default(DateTime), new ReadOnlyMemory<byte>(Guid.NewGuid().ToByteArray()), null, default(DateTime)],
        [new ReadOnlyMemory<byte>(null), null, TestTimestamp, new ReadOnlyMemory<byte>(null), new BasicProperties(), TestTimestamp],
        [new ReadOnlyMemory<byte>([]), new BasicProperties(), Y2K38, new ReadOnlyMemory<byte>([]), new BasicProperties(), Y2K38],
        [new ReadOnlyMemory<byte>([]), new BasicProperties(), UnixEpoch, new ReadOnlyMemory<byte>([]), CreateBasicProperties(), UnixEpoch],
        [new ReadOnlyMemory<byte>([127]), CreateBasicProperties(), TestTimestamp, new ReadOnlyMemory<byte>([1, 2, 3]), CreateBasicProperties(), TestTimestamp],
        [null, null, default(DateTime), null, null, TestTimestamp],
        [new ReadOnlyMemory<byte>([]), null, UnixEpoch, new ReadOnlyMemory<byte>([]), null, TestTimestamp],
        [new ReadOnlyMemory<byte>(TestBody), TestBasicProperties, TestTimestamp, new ReadOnlyMemory<byte>(TestBody), TestBasicProperties, Y2K38]
    ];

    [TestMethod]
    public void DefaultValue_PropertiesAreNull()
    {
        var result = default(Message);

        Assert.AreEqual(new ReadOnlyMemory<byte>(null), result.Body);
        Assert.IsNull(result.Properties);
        Assert.AreEqual(default, result.Timestamp);
        Assert.AreEqual(DateTimeKind.Unspecified, result.Timestamp.Kind);
    }

    [TestMethod]
    public void Constructor_DefaultValues_PropertiesAreSet()
    {
        var result = new Message(null, null, default);

        Assert.AreEqual(new ReadOnlyMemory<byte>(null), result.Body);
        Assert.IsNull(result.Properties);
        Assert.AreEqual(default, result.Timestamp);
        Assert.AreEqual(DateTimeKind.Unspecified, result.Timestamp.Kind);
    }

    [TestMethod]
    public void Constructor_InstancesAsValues_PropertiesAreSet()
    {
        var body = new ReadOnlyMemory<byte>([1, 2, 3]);
        var properties = new BasicProperties();
        var timestamp = DateTime.UtcNow;

        var result = new Message(body, properties, timestamp);

        Assert.AreEqual(body, result.Body);
        Assert.AreEqual(properties, result.Properties);
        Assert.AreEqual(timestamp, result.Timestamp);
        Assert.AreEqual(DateTimeKind.Utc, result.Timestamp.Kind);
    }

    [TestMethod]
    public void Constructor_LocalTime_ArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() => new Message(TestBody, TestBasicProperties, DateTime.Now));
    }

    [TestMethod]
    [DynamicData(nameof(EqualsTestData))]
    public void Equals_SameValues_ReturnsTrue(
        ReadOnlyMemory<byte> body,
        IBasicProperties? properties,
        DateTime timestamp)
    {
        var target1 = new Message(body, properties, timestamp);
        var target2 = new Message(body, properties, DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));

        Assert.IsTrue(target1.Equals(target2));
        Assert.IsTrue(target2.Equals(target1));
        Assert.IsTrue(target1.Equals((object)target2));
        Assert.IsTrue(target2.Equals((object)target1));
        Assert.IsTrue(target1 == target2);
        Assert.IsTrue(target2 == target1);
        Assert.IsFalse(target1 != target2);
        Assert.IsFalse(target2 != target1);
        Assert.AreEqual(target2.GetHashCode(), target1.GetHashCode());
    }

    [TestMethod]
    public void Equals_DefaultAndDefault_ReturnsTrue()
    {
        var target1 = default(Message);
        var target2 = default(Message);

        Assert.IsTrue(target1.Equals(target2));
        Assert.IsTrue(target2.Equals(target1));
        Assert.IsTrue(target1.Equals((object)target2));
        Assert.IsTrue(target2.Equals((object)target1));
        Assert.IsTrue(target1 == target2);
        Assert.IsTrue(target2 == target1);
        Assert.IsFalse(target1 != target2);
        Assert.IsFalse(target2 != target1);
        Assert.AreEqual(target2.GetHashCode(), target1.GetHashCode());
    }

    [TestMethod]
    [SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1129:Do not use default value type constructor", Justification = "Testing default constructor")]
    public void Equals_DefaultConstructorAndDefaultConstructor_ReturnsTrue()
    {
        var target1 = new Message();
        var target2 = new Message();

        Assert.IsTrue(target1.Equals(target2));
        Assert.IsTrue(target2.Equals(target1));
        Assert.IsTrue(target1.Equals((object)target2));
        Assert.IsTrue(target2.Equals((object)target1));
        Assert.IsTrue(target1 == target2);
        Assert.IsTrue(target2 == target1);
        Assert.IsFalse(target1 != target2);
        Assert.IsFalse(target2 != target1);
        Assert.AreEqual(target2.GetHashCode(), target1.GetHashCode());
    }

    [TestMethod]
    [SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1129:Do not use default value type constructor", Justification = "Testing default constructor")]
    public void Equals_DefaultValueAndDefaultConstructor_ReturnsTrue()
    {
        var target1 = default(Message);
        var target2 = new Message();

        Assert.IsTrue(target1.Equals(target2));
        Assert.IsTrue(target2.Equals(target1));
        Assert.IsTrue(target1.Equals((object)target2));
        Assert.IsTrue(target2.Equals((object)target1));
        Assert.IsTrue(target1 == target2);
        Assert.IsTrue(target2 == target1);
        Assert.IsFalse(target1 != target2);
        Assert.IsFalse(target2 != target1);
        Assert.AreEqual(target2.GetHashCode(), target1.GetHashCode());
    }

    [TestMethod]
    public void Equals_DefaultValueAndNullValues_ReturnsTrue()
    {
        var target1 = default(Message);
        var target2 = new Message(null, null, default);

        Assert.IsTrue(target1.Equals(target2));
        Assert.IsTrue(target2.Equals(target1));
        Assert.IsTrue(target1.Equals((object)target2));
        Assert.IsTrue(target2.Equals((object)target1));
        Assert.IsTrue(target1 == target2);
        Assert.IsTrue(target2 == target1);
        Assert.IsFalse(target1 != target2);
        Assert.IsFalse(target2 != target1);
        Assert.AreEqual(target2.GetHashCode(), target1.GetHashCode());
    }

    [TestMethod]
    public void Equals_TestData_ReturnsTrue()
    {
        var target1 = new Message(TestBody, TestBasicProperties, TestTimestamp);
        var target2 = new Message(TestBody, TestBasicProperties, TestTimestamp);

        Assert.IsTrue(target1.Equals(target2));
        Assert.IsTrue(target2.Equals(target1));
        Assert.IsTrue(target1.Equals((object)target2));
        Assert.IsTrue(target2.Equals((object)target1));
        Assert.IsTrue(target1 == target2);
        Assert.IsTrue(target2 == target1);
        Assert.IsFalse(target1 != target2);
        Assert.IsFalse(target2 != target1);
        Assert.AreEqual(target2.GetHashCode(), target1.GetHashCode());
    }

    [TestMethod]
    public void Equals_NullArrayAndNullValues_ReturnsFalse()
    {
        var properties = CreateBasicProperties();
        var timestamp = DateTime.UtcNow;
        var target1 = new Message(new ReadOnlyMemory<byte>(null), properties, timestamp);
        var target2 = new Message(null, properties, timestamp);

        Assert.IsTrue(target1.Equals(target2));
        Assert.IsTrue(target2.Equals(target1));
        Assert.IsTrue(target1.Equals((object)target2));
        Assert.IsTrue(target2.Equals((object)target1));
        Assert.IsTrue(target1 == target2);
        Assert.IsTrue(target2 == target1);
        Assert.IsFalse(target1 != target2);
        Assert.IsFalse(target2 != target1);
        Assert.AreEqual(target2.GetHashCode(), target1.GetHashCode());
    }

    [TestMethod]
    public void Equals_SameArrayAndNewMemory_ReturnsFalse()
    {
        var properties = new BasicProperties();
        var array = new byte[] { 1, 2, 3 };
        var target1 = new Message(new ReadOnlyMemory<byte>(array), properties, default);
        var target2 = new Message(array, properties, default);

        Assert.IsTrue(target1.Equals(target2));
        Assert.IsTrue(target2.Equals(target1));
        Assert.IsTrue(target1.Equals((object)target2));
        Assert.IsTrue(target2.Equals((object)target1));
        Assert.IsTrue(target1 == target2);
        Assert.IsTrue(target2 == target1);
        Assert.IsFalse(target1 != target2);
        Assert.IsFalse(target2 != target1);
        Assert.AreEqual(target2.GetHashCode(), target1.GetHashCode());
    }

    [TestMethod]
    [DynamicData(nameof(NonEqualsTestData))]
    public void Equals_DifferentValues_ReturnsFalse(
        ReadOnlyMemory<byte> body1,
        IBasicProperties? properties1,
        DateTime timestamp1,
        ReadOnlyMemory<byte> body2,
        IBasicProperties? properties2,
        DateTime timestamp2)
    {
        var target1 = new Message(body1, properties1, timestamp1);
        var target2 = new Message(body2, properties2, timestamp2);

        Assert.IsFalse(target1.Equals(target2));
        Assert.IsFalse(target2.Equals(target1));
        Assert.IsFalse(target1.Equals((object)target2));
        Assert.IsFalse(target2.Equals((object)target1));
        Assert.IsFalse(target1 == target2);
        Assert.IsFalse(target2 == target1);
        Assert.IsTrue(target1 != target2);
        Assert.IsTrue(target2 != target1);
        Assert.AreNotEqual(target2.GetHashCode(), target1.GetHashCode());
    }

    [TestMethod]
    public void Equals_DefaultAndDefaultInstances_ReturnsFalse()
    {
        var target1 = default(Message);
        var target2 = new Message(Array.Empty<byte>(), new BasicProperties(), default);

        Assert.IsFalse(target1.Equals(target2));
        Assert.IsFalse(target2.Equals(target1));
        Assert.IsFalse(target1.Equals((object)target2));
        Assert.IsFalse(target2.Equals((object)target1));
        Assert.IsFalse(target1 == target2);
        Assert.IsFalse(target2 == target1);
        Assert.IsTrue(target1 != target2);
        Assert.IsTrue(target2 != target1);
        Assert.AreNotEqual(target2.GetHashCode(), target1.GetHashCode());
    }

    private static BasicProperties CreateBasicProperties()
    {
        return new BasicProperties
        {
            MessageId = Guid.NewGuid().ToString(),
            Type = "Test",
            Headers = new Dictionary<string, object?>()
            {
                { "TestHeader", Array.Empty<int>() }
            }
        };
    }
}
