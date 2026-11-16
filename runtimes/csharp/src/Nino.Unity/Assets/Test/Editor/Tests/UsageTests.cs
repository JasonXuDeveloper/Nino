using System.Collections.Generic;
using Nino.Core;
using Nino.Test;
using NUnit.Framework;
using Test.Editor.CrossRef;
using UnityEngine;

namespace Test.Editor.Tests
{
    /// <summary>
    /// The v4 API as a Unity project uses it, compiled by Unity's compiler with the Roslyn 4.3 generator (regular
    /// calls, no interceptors) and run on Unity's Mono.
    /// </summary>
    public class UsageTests
    {
        private static NestedData Message(int i) => new NestedData
        {
            name = "message " + i,
            ps = new[] { new Data { x = i, y = 2, z = 3, f = 1.5f, d = 2.5m, db = 3.5, bo = true, en = TestEnum.B } }
        };

        [Test]
        public void SerializeIntoReusedBufferWriter()
        {
            using var buffer = new NinoArrayBufferWriter();
            for (int i = 0; i < 3; i++)
            {
                var message = Message(i);
                buffer.ResetWrittenCount();
                NinoSerializer.Serialize(message, buffer);

                CollectionAssert.AreEqual(NinoSerializer.Serialize(message), buffer.WrittenSpan.ToArray());
                var copy = NinoDeserializer.Deserialize<NestedData>(buffer.WrittenSpan);
                Assert.AreEqual(message.ToString(), copy.ToString());
            }
        }

        [Test]
        public void DeserializeIntoExistingInstance()
        {
            var target = new NestedData { name = "old", ps = new Data[1] };
            var instance = target;
            var array = target.ps;

            NinoDeserializer.Deserialize(NinoSerializer.Serialize(Message(5)), ref target);

            Assert.AreSame(instance, target);
            Assert.AreSame(array, target.ps);
            Assert.AreEqual(Message(5).ToString(), target.ToString());
        }

        [Test]
        public void PolymorphicRoundTrip()
        {
            var shapes = new List<Shape>
            {
                new Circle { Name = "circle", Radius = 2 },
                new Box { Name = "box", Size = new Vector3(1, 2, 3), Rotation = Quaternion.Euler(0, 90, 0) },
                null
            };

            var result = NinoDeserializer.Deserialize<List<Shape>>(NinoSerializer.Serialize(shapes));

            Assert.AreEqual(3, result.Count);
            Assert.IsInstanceOf<Circle>(result[0]);
            Assert.AreEqual("circle", result[0].Name);
            Assert.AreEqual(2f, ((Circle)result[0]).Radius);
            Assert.IsInstanceOf<Box>(result[1]);
            Assert.AreEqual("box", result[1].Name);
            Assert.AreEqual(new Vector3(1, 2, 3), ((Box)result[1]).Size);
            Assert.AreEqual(((Box)shapes[1]).Rotation, ((Box)result[1]).Rotation);
            Assert.IsNull(result[2]);

            Shape single = new Circle { Name = "single", Radius = 3 };
            var read = NinoDeserializer.Deserialize<Shape>(NinoSerializer.Serialize(single));
            Assert.IsInstanceOf<Circle>(read);
            Assert.AreEqual(3f, ((Circle)read).Radius);
        }

        [Test]
        public void PooledInstancesAreReused()
        {
            NinoPool<PooledMessage>.Shared.Clear();
            var first = NinoDeserializer.Deserialize<PooledMessage>(NinoSerializer.Serialize(
                new PooledMessage { Id = 1, Values = new List<int> { 1, 2, 3 } }));
            var values = first.Values;
            int returns = PooledMessage.Returns;

            first.ReturnToPool();
            Assert.AreEqual(returns + 1, PooledMessage.Returns, "the return hook runs");

            var next = NinoDeserializer.Deserialize<PooledMessage>(NinoSerializer.Serialize(
                new PooledMessage { Id = 2, Values = new List<int> { 4 } }));
            Assert.AreSame(first, next);
            Assert.AreSame(values, next.Values);
            Assert.AreEqual(2, next.Id);
            CollectionAssert.AreEqual(new[] { 4 }, next.Values);
            next.ReturnToPool();
        }

        [Test]
        public void KeyedTypeReadsOlderVersion()
        {
            var v1 = new KeyedSettingsV1 { Id = 7, Position = new Vector3(1, 2, 3), Speed = 4.5f, Tag = "enemy" };

            var v2 = NinoDeserializer.Deserialize<KeyedSettingsV2>(NinoSerializer.Serialize(v1));

            Assert.AreEqual(7, v2.Id);
            Assert.AreEqual(v1.Position, v2.Position);
            Assert.AreEqual(4.5f, v2.MoveSpeed);
            Assert.AreEqual(Color.white, v2.Color); // not in the data: keeps the constructed value
        }

        [Test]
        public void TypeOfAnotherAssemblyNeedsNoRegistration()
        {
            // RandomRef is generated in the CrossRef assembly; nothing here registers or initializes it.
            var items = new List<RandomRef> { new RandomRef { Id = 1 }, new RandomRef { Id = 42 } };

            var result = NinoDeserializer.Deserialize<List<RandomRef>>(NinoSerializer.Serialize(items));

            Assert.AreEqual(2, result.Count);
            Assert.AreEqual(1, result[0].Id);
            Assert.AreEqual(42, result[1].Id);
        }
    }
}
