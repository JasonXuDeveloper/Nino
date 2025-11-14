using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class RefDeserializationAttributeTests
{
    private static int _poolCallCount = 0;

    [NinoType]
    public class PooledClass
    {
        [NinoRefDeserialization]
        public static PooledClass GetInstance()
        {
            _poolCallCount++;
            return new PooledClass { WasPooled = true };
        }

        public int Value;
        public string Name;

        [NinoIgnore]
        public bool WasPooled;
    }

    [NinoType]
    public class NestedPooledClass
    {
        [NinoRefDeserialization]
        public static NestedPooledClass CreateFromPool()
        {
            return new NestedPooledClass { PoolCreated = true };
        }

        public int Id;
        public PooledClass NestedObject;

        [NinoIgnore]
        public bool PoolCreated;
    }

    [NinoType]
    public class RegularClass
    {
        public int Value;
        public string Name;
    }

    [TestInitialize]
    public void Setup()
    {
        _poolCallCount = 0;
    }

    [TestMethod]
    public void TestDeserialize_CallsFactoryMethod()
    {
        // Arrange
        var original = new PooledClass
        {
            Value = 42,
            Name = "Test",
            WasPooled = false  // Will be set to true by factory
        };

        byte[] bytes = NinoSerializer.Serialize(original);

        // Act
        var result = NinoDeserializer.Deserialize<PooledClass>(bytes);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(42, result.Value);
        Assert.AreEqual("Test", result.Name);
        Assert.IsTrue(result.WasPooled, "Factory method should have been called");
        Assert.AreEqual(1, _poolCallCount, "Factory method should have been called exactly once");
    }

    [TestMethod]
    public void TestDeserializeRef_WithNullValue_CallsFactoryMethod()
    {
        // Arrange
        var original = new PooledClass
        {
            Value = 100,
            Name = "RefTest",
            WasPooled = false
        };

        byte[] bytes = NinoSerializer.Serialize(original);
        PooledClass result = null;

        // Act
        var reader = new Reader(bytes);
        NinoDeserializer.DeserializeRef(ref result, ref reader);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(100, result.Value);
        Assert.AreEqual("RefTest", result.Name);
        Assert.IsTrue(result.WasPooled, "Factory method should have been called for null ref");
        Assert.AreEqual(1, _poolCallCount, "Factory method should have been called exactly once");
    }

    [TestMethod]
    public void TestDeserializeRef_WithExistingValue_DoesNotCallFactory()
    {
        // Arrange
        var original = new PooledClass
        {
            Value = 200,
            Name = "ExistingTest",
            WasPooled = false
        };

        byte[] bytes = NinoSerializer.Serialize(original);

        // Create an existing instance (not from pool)
        PooledClass result = new PooledClass { WasPooled = false };

        // Act
        var reader = new Reader(bytes);
        NinoDeserializer.DeserializeRef(ref result, ref reader);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(200, result.Value);
        Assert.AreEqual("ExistingTest", result.Name);
        Assert.IsFalse(result.WasPooled, "Factory method should NOT have been called for existing instance");
        Assert.AreEqual(0, _poolCallCount, "Factory method should NOT have been called");
    }

    [TestMethod]
    public void TestNestedPooledObjects()
    {
        // Arrange
        var original = new NestedPooledClass
        {
            Id = 999,
            NestedObject = new PooledClass
            {
                Value = 123,
                Name = "Nested",
                WasPooled = false
            },
            PoolCreated = false
        };

        byte[] bytes = NinoSerializer.Serialize(original);

        // Act
        var result = NinoDeserializer.Deserialize<NestedPooledClass>(bytes);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(999, result.Id);
        Assert.IsTrue(result.PoolCreated, "Outer factory should have been called");

        Assert.IsNotNull(result.NestedObject);
        Assert.AreEqual(123, result.NestedObject.Value);
        Assert.AreEqual("Nested", result.NestedObject.Name);
        Assert.IsTrue(result.NestedObject.WasPooled, "Nested factory should have been called");
    }

    [TestMethod]
    public void TestRegularClassStillWorks()
    {
        // Ensure regular classes without the attribute still work
        var original = new RegularClass
        {
            Value = 456,
            Name = "Regular"
        };

        byte[] bytes = NinoSerializer.Serialize(original);
        var result = NinoDeserializer.Deserialize<RegularClass>(bytes);

        Assert.IsNotNull(result);
        Assert.AreEqual(456, result.Value);
        Assert.AreEqual("Regular", result.Name);
    }

    [TestMethod]
    public void TestMultipleDeserializationCalls()
    {
        // Test that factory is called for each deserialization
        var original = new PooledClass
        {
            Value = 777,
            Name = "Multi",
            WasPooled = false
        };

        byte[] bytes = NinoSerializer.Serialize(original);

        // First deserialization
        var result1 = NinoDeserializer.Deserialize<PooledClass>(bytes);
        Assert.AreEqual(1, _poolCallCount);
        Assert.IsTrue(result1.WasPooled);

        // Second deserialization
        var result2 = NinoDeserializer.Deserialize<PooledClass>(bytes);
        Assert.AreEqual(2, _poolCallCount);
        Assert.IsTrue(result2.WasPooled);

        // Both should have correct values
        Assert.AreEqual(777, result1.Value);
        Assert.AreEqual(777, result2.Value);
        Assert.AreEqual("Multi", result1.Name);
        Assert.AreEqual("Multi", result2.Name);
    }
}
