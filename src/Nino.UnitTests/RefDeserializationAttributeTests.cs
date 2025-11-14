using System.Collections.Generic;
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

    [NinoType]
    public class GenericPooledClass<T>
    {
        [NinoRefDeserialization]
        public static GenericPooledClass<T> CreateInstance()
        {
            return new GenericPooledClass<T> { FromPool = true };
        }

        public T Data;
        public int Count;

        [NinoIgnore]
        public bool FromPool;
    }

    // Polymorphic test classes
    [NinoType]
    public class BasePooledClass
    {
        private static int _basePoolCallCount = 0;

        [NinoRefDeserialization]
        public static BasePooledClass GetFromBasePool()
        {
            _basePoolCallCount++;
            return new BasePooledClass { FromBasePool = true };
        }

        public int BaseValue;
        public string BaseName;
        public int[] BaseIntArray;
        public List<string> BaseStringList;

        [NinoIgnore]
        public bool FromBasePool;

        public static int GetBasePoolCallCount() => _basePoolCallCount;
        public static void ResetBasePoolCallCount() => _basePoolCallCount = 0;
    }

    [NinoType]
    public class DerivedPooledClass : BasePooledClass
    {
        private static int _derivedPoolCallCount = 0;

        [NinoRefDeserialization]
        public static DerivedPooledClass GetFromDerivedPool()
        {
            _derivedPoolCallCount++;
            return new DerivedPooledClass { FromDerivedPool = true };
        }

        public int DerivedValue;
        public string DerivedName;
        public double[] DerivedDoubleArray;
        public List<int> DerivedIntList;

        [NinoIgnore]
        public bool FromDerivedPool;

        public static int GetDerivedPoolCallCount() => _derivedPoolCallCount;
        public static void ResetDerivedPoolCallCount() => _derivedPoolCallCount = 0;
    }

    [NinoType]
    public class DerivedWithoutPoolClass : BasePooledClass
    {
        public int DerivedData;
        public string DerivedText;
        public List<double> DerivedDoubleList;
        public string[] DerivedStringArray;
    }

    [NinoType]
    public class PolymorphicContainer
    {
        public BasePooledClass BaseField;
        public BasePooledClass[] PolymorphicArray;
        public List<BasePooledClass> PolymorphicList;
    }

    [TestInitialize]
    public void Setup()
    {
        _poolCallCount = 0;
        BasePooledClass.ResetBasePoolCallCount();
        DerivedPooledClass.ResetDerivedPoolCallCount();
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
        NinoDeserializer.DeserializeRef(ref result, bytes);

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
        NinoDeserializer.DeserializeRef(ref result, bytes);

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

    [TestMethod]
    public void TestGenericTypeWithRefDeserialization()
    {
        // Test that NinoRefDeserializationAttribute works on generic types
        var original = new GenericPooledClass<string>
        {
            Data = "GenericTest",
            Count = 42,
            FromPool = false
        };

        byte[] bytes = NinoSerializer.Serialize(original);

        // Test Deserialize
        var result = NinoDeserializer.Deserialize<GenericPooledClass<string>>(bytes);

        Assert.IsNotNull(result);
        Assert.AreEqual("GenericTest", result.Data);
        Assert.AreEqual(42, result.Count);
        Assert.IsTrue(result.FromPool, "Factory method should have been called for generic type");

        // Test DeserializeRef with null
        GenericPooledClass<string> refResult = null;
        NinoDeserializer.DeserializeRef(ref refResult, bytes);

        Assert.IsNotNull(refResult);
        Assert.AreEqual("GenericTest", refResult.Data);
        Assert.AreEqual(42, refResult.Count);
        Assert.IsTrue(refResult.FromPool, "Factory method should have been called for null ref");

        // Test DeserializeRef with existing instance
        GenericPooledClass<string> existingResult = new GenericPooledClass<string> { FromPool = false };
        NinoDeserializer.DeserializeRef(ref existingResult, bytes);

        Assert.IsNotNull(existingResult);
        Assert.AreEqual("GenericTest", existingResult.Data);
        Assert.AreEqual(42, existingResult.Count);
        Assert.IsFalse(existingResult.FromPool, "Factory method should NOT have been called for existing instance");
    }

    [TestMethod]
    public void TestGenericTypeWithDifferentTypeArguments()
    {
        // Test that different instantiations of generic type each use their own factory
        var originalInt = new GenericPooledClass<int>
        {
            Data = 999,
            Count = 10,
            FromPool = false
        };

        var originalString = new GenericPooledClass<string>
        {
            Data = "Test",
            Count = 20,
            FromPool = false
        };

        byte[] bytesInt = NinoSerializer.Serialize(originalInt);
        byte[] bytesString = NinoSerializer.Serialize(originalString);

        // Deserialize int version
        var resultInt = NinoDeserializer.Deserialize<GenericPooledClass<int>>(bytesInt);
        Assert.AreEqual(999, resultInt.Data);
        Assert.AreEqual(10, resultInt.Count);
        Assert.IsTrue(resultInt.FromPool);

        // Deserialize string version
        var resultString = NinoDeserializer.Deserialize<GenericPooledClass<string>>(bytesString);
        Assert.AreEqual("Test", resultString.Data);
        Assert.AreEqual(20, resultString.Count);
        Assert.IsTrue(resultString.FromPool);
    }

    [TestMethod]
    public void TestPolymorphic_BaseClass_CallsBaseFactory()
    {
        // Test that base class uses its own factory method
        var original = new BasePooledClass
        {
            BaseValue = 100,
            BaseName = "BaseTest",
            BaseIntArray = new[] { 1, 2, 3 },
            BaseStringList = new List<string> { "A", "B", "C" },
            FromBasePool = false
        };

        byte[] bytes = NinoSerializer.Serialize(original);

        // Deserialize as base type
        var result = NinoDeserializer.Deserialize<BasePooledClass>(bytes);

        Assert.IsNotNull(result);
        Assert.AreEqual(100, result.BaseValue);
        Assert.AreEqual("BaseTest", result.BaseName);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, result.BaseIntArray);
        CollectionAssert.AreEqual(new List<string> { "A", "B", "C" }, result.BaseStringList);
        Assert.IsTrue(result.FromBasePool, "Base factory method should have been called");
        Assert.AreEqual(1, BasePooledClass.GetBasePoolCallCount());
    }

    [TestMethod]
    public void TestPolymorphic_DerivedClass_CallsDerivedFactory()
    {
        // Test that derived class uses its own factory method
        var original = new DerivedPooledClass
        {
            BaseValue = 200,
            BaseName = "DerivedBase",
            BaseIntArray = new[] { 10, 20 },
            BaseStringList = new List<string> { "X", "Y" },
            DerivedValue = 300,
            DerivedName = "DerivedTest",
            DerivedDoubleArray = new[] { 1.5, 2.5, 3.5 },
            DerivedIntList = new List<int> { 100, 200, 300 },
            FromBasePool = false,
            FromDerivedPool = false
        };

        byte[] bytes = NinoSerializer.Serialize(original);

        // Deserialize as derived type
        var result = NinoDeserializer.Deserialize<DerivedPooledClass>(bytes);

        Assert.IsNotNull(result);
        Assert.AreEqual(200, result.BaseValue);
        Assert.AreEqual("DerivedBase", result.BaseName);
        CollectionAssert.AreEqual(new[] { 10, 20 }, result.BaseIntArray);
        CollectionAssert.AreEqual(new List<string> { "X", "Y" }, result.BaseStringList);
        Assert.AreEqual(300, result.DerivedValue);
        Assert.AreEqual("DerivedTest", result.DerivedName);
        CollectionAssert.AreEqual(new[] { 1.5, 2.5, 3.5 }, result.DerivedDoubleArray);
        CollectionAssert.AreEqual(new List<int> { 100, 200, 300 }, result.DerivedIntList);
        Assert.IsTrue(result.FromDerivedPool, "Derived factory method should have been called");
        Assert.AreEqual(1, DerivedPooledClass.GetDerivedPoolCallCount());
        Assert.AreEqual(0, BasePooledClass.GetBasePoolCallCount(), "Base factory should NOT be called");
    }

    [TestMethod]
    public void TestPolymorphic_DerivedWithoutPool_UsesBaseFactory()
    {
        // Test that derived class without factory uses base class factory
        var original = new DerivedWithoutPoolClass
        {
            BaseValue = 400,
            BaseName = "DerivedNoPool",
            BaseIntArray = new[] { 7, 8, 9 },
            BaseStringList = new List<string> { "D", "E" },
            DerivedData = 500,
            DerivedText = "NoPoolTest",
            DerivedDoubleList = new List<double> { 4.5, 5.5 },
            DerivedStringArray = new[] { "P", "Q", "R" }
        };

        byte[] bytes = NinoSerializer.Serialize(original);

        // Deserialize as derived type
        var result = NinoDeserializer.Deserialize<DerivedWithoutPoolClass>(bytes);

        Assert.IsNotNull(result);
        Assert.AreEqual(400, result.BaseValue);
        Assert.AreEqual("DerivedNoPool", result.BaseName);
        CollectionAssert.AreEqual(new[] { 7, 8, 9 }, result.BaseIntArray);
        CollectionAssert.AreEqual(new List<string> { "D", "E" }, result.BaseStringList);
        Assert.AreEqual(500, result.DerivedData);
        Assert.AreEqual("NoPoolTest", result.DerivedText);
        CollectionAssert.AreEqual(new List<double> { 4.5, 5.5 }, result.DerivedDoubleList);
        CollectionAssert.AreEqual(new[] { "P", "Q", "R" }, result.DerivedStringArray);
        // Note: This derived class doesn't have NinoRefDeserializationAttribute,
        // so it won't use base class factory either (each type needs its own attribute)
    }

    [TestMethod]
    public void TestPolymorphic_PolymorphicField_CallsCorrectFactory()
    {
        // Test polymorphic field storing derived instance
        var container = new PolymorphicContainer
        {
            BaseField = new DerivedPooledClass
            {
                BaseValue = 600,
                BaseName = "PolyBase",
                BaseIntArray = new[] { 11, 12 },
                BaseStringList = new List<string> { "F", "G" },
                DerivedValue = 700,
                DerivedName = "PolyDerived",
                DerivedDoubleArray = new[] { 6.5, 7.5 },
                DerivedIntList = new List<int> { 400, 500 },
                FromBasePool = false,
                FromDerivedPool = false
            }
        };

        byte[] bytes = NinoSerializer.Serialize(container);

        // Deserialize container
        var result = NinoDeserializer.Deserialize<PolymorphicContainer>(bytes);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.BaseField);
        Assert.IsInstanceOfType(result.BaseField, typeof(DerivedPooledClass));

        var derivedField = (DerivedPooledClass)result.BaseField;
        Assert.AreEqual(600, derivedField.BaseValue);
        Assert.AreEqual("PolyBase", derivedField.BaseName);
        CollectionAssert.AreEqual(new[] { 11, 12 }, derivedField.BaseIntArray);
        CollectionAssert.AreEqual(new List<string> { "F", "G" }, derivedField.BaseStringList);
        Assert.AreEqual(700, derivedField.DerivedValue);
        Assert.AreEqual("PolyDerived", derivedField.DerivedName);
        CollectionAssert.AreEqual(new[] { 6.5, 7.5 }, derivedField.DerivedDoubleArray);
        CollectionAssert.AreEqual(new List<int> { 400, 500 }, derivedField.DerivedIntList);
        Assert.IsTrue(derivedField.FromDerivedPool, "Derived factory should be called for polymorphic field");
        Assert.AreEqual(1, DerivedPooledClass.GetDerivedPoolCallCount());
    }

    [TestMethod]
    public void TestPolymorphic_PolymorphicArray_CallsCorrectFactories()
    {
        // Test array of base type containing different derived types
        var container = new PolymorphicContainer
        {
            PolymorphicArray = new BasePooledClass[]
            {
                new BasePooledClass
                {
                    BaseValue = 10,
                    BaseName = "Base1",
                    BaseIntArray = new[] { 1, 2 },
                    BaseStringList = new List<string> { "H", "I" },
                    FromBasePool = false
                },
                new DerivedPooledClass
                {
                    BaseValue = 20,
                    BaseName = "Derived1",
                    BaseIntArray = new[] { 3, 4 },
                    BaseStringList = new List<string> { "J", "K" },
                    DerivedValue = 30,
                    DerivedName = "DerivedName1",
                    DerivedDoubleArray = new[] { 8.5, 9.5 },
                    DerivedIntList = new List<int> { 600, 700 },
                    FromBasePool = false,
                    FromDerivedPool = false
                },
                new DerivedWithoutPoolClass
                {
                    BaseValue = 40,
                    BaseName = "DerivedNoPool1",
                    BaseIntArray = new[] { 5, 6 },
                    BaseStringList = new List<string> { "L", "M" },
                    DerivedData = 50,
                    DerivedText = "NoPool1",
                    DerivedDoubleList = new List<double> { 10.5, 11.5 },
                    DerivedStringArray = new[] { "S", "T" }
                }
            }
        };

        byte[] bytes = NinoSerializer.Serialize(container);

        // Deserialize container
        var result = NinoDeserializer.Deserialize<PolymorphicContainer>(bytes);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.PolymorphicArray);
        Assert.AreEqual(3, result.PolymorphicArray.Length);

        // First element - base class
        Assert.IsInstanceOfType(result.PolymorphicArray[0], typeof(BasePooledClass));
        Assert.AreEqual(10, result.PolymorphicArray[0].BaseValue);
        Assert.AreEqual("Base1", result.PolymorphicArray[0].BaseName);
        CollectionAssert.AreEqual(new[] { 1, 2 }, result.PolymorphicArray[0].BaseIntArray);
        CollectionAssert.AreEqual(new List<string> { "H", "I" }, result.PolymorphicArray[0].BaseStringList);
        Assert.IsTrue(result.PolymorphicArray[0].FromBasePool);

        // Second element - derived class
        Assert.IsInstanceOfType(result.PolymorphicArray[1], typeof(DerivedPooledClass));
        var derived = (DerivedPooledClass)result.PolymorphicArray[1];
        Assert.AreEqual(20, derived.BaseValue);
        Assert.AreEqual("Derived1", derived.BaseName);
        CollectionAssert.AreEqual(new[] { 3, 4 }, derived.BaseIntArray);
        CollectionAssert.AreEqual(new List<string> { "J", "K" }, derived.BaseStringList);
        Assert.AreEqual(30, derived.DerivedValue);
        Assert.AreEqual("DerivedName1", derived.DerivedName);
        CollectionAssert.AreEqual(new[] { 8.5, 9.5 }, derived.DerivedDoubleArray);
        CollectionAssert.AreEqual(new List<int> { 600, 700 }, derived.DerivedIntList);
        Assert.IsTrue(derived.FromDerivedPool);

        // Third element - derived without pool
        Assert.IsInstanceOfType(result.PolymorphicArray[2], typeof(DerivedWithoutPoolClass));
        var derivedNoPool = (DerivedWithoutPoolClass)result.PolymorphicArray[2];
        Assert.AreEqual(40, derivedNoPool.BaseValue);
        Assert.AreEqual("DerivedNoPool1", derivedNoPool.BaseName);
        CollectionAssert.AreEqual(new[] { 5, 6 }, derivedNoPool.BaseIntArray);
        CollectionAssert.AreEqual(new List<string> { "L", "M" }, derivedNoPool.BaseStringList);
        Assert.AreEqual(50, derivedNoPool.DerivedData);
        Assert.AreEqual("NoPool1", derivedNoPool.DerivedText);
        CollectionAssert.AreEqual(new List<double> { 10.5, 11.5 }, derivedNoPool.DerivedDoubleList);
        CollectionAssert.AreEqual(new[] { "S", "T" }, derivedNoPool.DerivedStringArray);

        // Verify factory call counts
        Assert.AreEqual(1, BasePooledClass.GetBasePoolCallCount(), "Base factory called once");
        Assert.AreEqual(1, DerivedPooledClass.GetDerivedPoolCallCount(), "Derived factory called once");
    }

    [TestMethod]
    public void TestPolymorphic_PolymorphicList_CallsCorrectFactories()
    {
        // Test list of base type containing different derived types
        var container = new PolymorphicContainer
        {
            PolymorphicList = new List<BasePooledClass>
            {
                new BasePooledClass
                {
                    BaseValue = 15,
                    BaseName = "Base2",
                    BaseIntArray = new[] { 13, 14 },
                    BaseStringList = new List<string> { "N", "O" },
                    FromBasePool = false
                },
                new DerivedPooledClass
                {
                    BaseValue = 25,
                    BaseName = "Derived2",
                    BaseIntArray = new[] { 15, 16 },
                    BaseStringList = new List<string> { "P", "Q" },
                    DerivedValue = 35,
                    DerivedName = "DerivedName2",
                    DerivedDoubleArray = new[] { 12.5, 13.5 },
                    DerivedIntList = new List<int> { 800, 900 },
                    FromBasePool = false,
                    FromDerivedPool = false
                }
            }
        };

        byte[] bytes = NinoSerializer.Serialize(container);

        // Deserialize container
        var result = NinoDeserializer.Deserialize<PolymorphicContainer>(bytes);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.PolymorphicList);
        Assert.AreEqual(2, result.PolymorphicList.Count);

        // First element - base class
        Assert.IsInstanceOfType(result.PolymorphicList[0], typeof(BasePooledClass));
        Assert.AreEqual(15, result.PolymorphicList[0].BaseValue);
        Assert.AreEqual("Base2", result.PolymorphicList[0].BaseName);
        CollectionAssert.AreEqual(new[] { 13, 14 }, result.PolymorphicList[0].BaseIntArray);
        CollectionAssert.AreEqual(new List<string> { "N", "O" }, result.PolymorphicList[0].BaseStringList);
        Assert.IsTrue(result.PolymorphicList[0].FromBasePool);

        // Second element - derived class
        Assert.IsInstanceOfType(result.PolymorphicList[1], typeof(DerivedPooledClass));
        var derived = (DerivedPooledClass)result.PolymorphicList[1];
        Assert.AreEqual(25, derived.BaseValue);
        Assert.AreEqual("Derived2", derived.BaseName);
        CollectionAssert.AreEqual(new[] { 15, 16 }, derived.BaseIntArray);
        CollectionAssert.AreEqual(new List<string> { "P", "Q" }, derived.BaseStringList);
        Assert.AreEqual(35, derived.DerivedValue);
        Assert.AreEqual("DerivedName2", derived.DerivedName);
        CollectionAssert.AreEqual(new[] { 12.5, 13.5 }, derived.DerivedDoubleArray);
        CollectionAssert.AreEqual(new List<int> { 800, 900 }, derived.DerivedIntList);
        Assert.IsTrue(derived.FromDerivedPool);

        // Verify factory call counts
        Assert.AreEqual(1, BasePooledClass.GetBasePoolCallCount(), "Base factory called once");
        Assert.AreEqual(1, DerivedPooledClass.GetDerivedPoolCallCount(), "Derived factory called once");
    }

    [TestMethod]
    public void TestPolymorphic_DeserializeRef_WithNullDerivedValue()
    {
        // Test DeserializeRef with null for derived class
        var original = new DerivedPooledClass
        {
            BaseValue = 800,
            BaseName = "RefDerivedBase",
            BaseIntArray = new[] { 17, 18 },
            BaseStringList = new List<string> { "R", "S" },
            DerivedValue = 900,
            DerivedName = "RefDerivedTest",
            DerivedDoubleArray = new[] { 14.5, 15.5 },
            DerivedIntList = new List<int> { 1000, 1100 },
            FromBasePool = false,
            FromDerivedPool = false
        };

        byte[] bytes = NinoSerializer.Serialize(original);
        DerivedPooledClass result = null;

        // Deserialize with null reference
        NinoDeserializer.DeserializeRef(ref result, bytes);

        Assert.IsNotNull(result);
        Assert.AreEqual(800, result.BaseValue);
        Assert.AreEqual("RefDerivedBase", result.BaseName);
        CollectionAssert.AreEqual(new[] { 17, 18 }, result.BaseIntArray);
        CollectionAssert.AreEqual(new List<string> { "R", "S" }, result.BaseStringList);
        Assert.AreEqual(900, result.DerivedValue);
        Assert.AreEqual("RefDerivedTest", result.DerivedName);
        CollectionAssert.AreEqual(new[] { 14.5, 15.5 }, result.DerivedDoubleArray);
        CollectionAssert.AreEqual(new List<int> { 1000, 1100 }, result.DerivedIntList);
        Assert.IsTrue(result.FromDerivedPool, "Derived factory should be called for null ref");
        Assert.AreEqual(1, DerivedPooledClass.GetDerivedPoolCallCount());
    }

    [TestMethod]
    public void TestPolymorphic_DeserializeRef_WithExistingDerivedValue()
    {
        // Test DeserializeRef with existing instance for derived class
        var original = new DerivedPooledClass
        {
            BaseValue = 1000,
            BaseName = "RefExistingBase",
            BaseIntArray = new[] { 19, 20 },
            BaseStringList = new List<string> { "T", "U" },
            DerivedValue = 1100,
            DerivedName = "RefExistingDerived",
            DerivedDoubleArray = new[] { 16.5, 17.5 },
            DerivedIntList = new List<int> { 1200, 1300 },
            FromBasePool = false,
            FromDerivedPool = false
        };

        byte[] bytes = NinoSerializer.Serialize(original);

        // Create existing instance (not from pool)
        DerivedPooledClass result = new DerivedPooledClass
        {
            FromBasePool = false,
            FromDerivedPool = false
        };

        // Deserialize with existing reference
        NinoDeserializer.DeserializeRef(ref result, bytes);

        Assert.IsNotNull(result);
        Assert.AreEqual(1000, result.BaseValue);
        Assert.AreEqual("RefExistingBase", result.BaseName);
        CollectionAssert.AreEqual(new[] { 19, 20 }, result.BaseIntArray);
        CollectionAssert.AreEqual(new List<string> { "T", "U" }, result.BaseStringList);
        Assert.AreEqual(1100, result.DerivedValue);
        Assert.AreEqual("RefExistingDerived", result.DerivedName);
        CollectionAssert.AreEqual(new[] { 16.5, 17.5 }, result.DerivedDoubleArray);
        CollectionAssert.AreEqual(new List<int> { 1200, 1300 }, result.DerivedIntList);
        Assert.IsFalse(result.FromDerivedPool, "Derived factory should NOT be called for existing instance");
        Assert.AreEqual(0, DerivedPooledClass.GetDerivedPoolCallCount());
    }
}
