using System.Diagnostics.CodeAnalysis;
using RabbitMQ.Client;

namespace BunnyBracelet.Tests;

[TestClass]
public class MessageTest
{
    public static IEnumerable<object?[]> EqualsTestData { get; } = new object?[][]
    {
        new object?[] { null, null },
        new object?[] { new ReadOnlyMemory<byte>(null), new BasicPropertiesMock() },
        new object?[] { new ReadOnlyMemory<byte>(Array.Empty<byte>()), CreateBasicProperties() },
        new object?[] { new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }), CreateBasicProperties() },
        new object?[] { new ReadOnlyMemory<byte>(Guid.NewGuid().ToByteArray()), null },
    };

    [SuppressMessage("Performance", "CA1825:Avoid zero-length array allocations", Justification = "Test new array instance.")]
    public static IEnumerable<object?[]> NonEqualsTestData { get; } = new object?[][]
    {
        new object?[] { null, null, new ReadOnlyMemory<byte>(Array.Empty<byte>()), null },
        new object?[] { new ReadOnlyMemory<byte>(new byte[0]), null, new ReadOnlyMemory<byte>(Array.Empty<byte>()), null },
        new object?[] { new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }), null, new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }), null },
        new object?[] { new ReadOnlyMemory<byte>(Guid.NewGuid().ToByteArray()), null, new ReadOnlyMemory<byte>(Guid.NewGuid().ToByteArray()), null },
        new object?[] { new ReadOnlyMemory<byte>(null), null, new ReadOnlyMemory<byte>(null), new BasicPropertiesMock() },
        new object?[] { new ReadOnlyMemory<byte>(Array.Empty<byte>()), new BasicPropertiesMock(), new ReadOnlyMemory<byte>(Array.Empty<byte>()), new BasicPropertiesMock() },
        new object?[] { new ReadOnlyMemory<byte>(Array.Empty<byte>()), new BasicPropertiesMock(), new ReadOnlyMemory<byte>(Array.Empty<byte>()), CreateBasicProperties() },
        new object?[] { new ReadOnlyMemory<byte>(new byte[] { 127 }), CreateBasicProperties(), new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }), CreateBasicProperties() }
    };

    [TestMethod]
    public void DefaultValue_PropertiesAreNull()
    {
        var result = default(Message);

        Assert.AreEqual(new ReadOnlyMemory<byte>(null), result.Body);
        Assert.IsNull(result.Properties);
    }

    [TestMethod]
    public void Constructor_DefaultValues_PropertiesAreSet()
    {
        var result = new Message(null, null);

        Assert.AreEqual(new ReadOnlyMemory<byte>(null), result.Body);
        Assert.IsNull(result.Properties);
    }

    [TestMethod]
    public void Constructor_InstancesAsValues_PropertiesAreSet()
    {
        var body = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 });
        var properties = new BasicPropertiesMock();

        var result = new Message(body, properties);

        Assert.AreEqual(body, result.Body);
        Assert.AreEqual(properties, result.Properties);
    }

    [TestMethod]
    [DynamicData(nameof(EqualsTestData))]
    public void Equals_SameValues_ReturnsTrue(ReadOnlyMemory<byte> body, IBasicProperties? properties)
    {
        var target1 = new Message(body, properties);
        var target2 = new Message(body, properties);

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
        var target2 = new Message(null, null);

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
        var target1 = new Message(new ReadOnlyMemory<byte>(null), properties);
        var target2 = new Message(null, properties);

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
        var properties = new BasicPropertiesMock();
        var array = new byte[] { 1, 2, 3 };
        var target1 = new Message(new ReadOnlyMemory<byte>(array), properties);
        var target2 = new Message(array, properties);

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
        ReadOnlyMemory<byte> body2,
        IBasicProperties? properties2)
    {
        var target1 = new Message(body1, properties1);
        var target2 = new Message(body2, properties2);

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
        var target2 = new Message(Array.Empty<byte>(), new BasicPropertiesMock());

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

    private static BasicPropertiesMock CreateBasicProperties()
    {
        return new BasicPropertiesMock
        {
            MessageId = Guid.NewGuid().ToString(),
            Type = "Test",
            Headers = new Dictionary<string, object>()
            {
                { "TestHeader", Array.Empty<int>() }
            }
        };
    }
}
