using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using Nino.UnitTests.Subset.Flat;

namespace Nino.UnitTests
{
    /// <summary>
    /// #452: classes whose body is one raw block nested in flat bodies (their presence byte or type id, then a block),
    /// which makes ShallowUser's and Comment's shapes flat: the bytes against twins written by generated bodies, the
    /// startup check of the nested blocks in the outer one's, reads into existing elements, every cut, subtypes and
    /// hostile headers read alike both ways, and the runs through the kernels.
    /// </summary>
    public partial class FlatTests
    {
        private static FlatUser User(Random random, int i) => i % 17 == 9 ? null : new FlatUser
        {
            UserId = i % 3 == 0 ? null : random.Next(), Name = i % 5 == 1 ? null : Text(random, 0, 16, i % 4 == 0),
            Reputation = i % 4 == 2 ? null : -i, Kind = (FlatKind)(i % 3), Link = "https://u/" + i,
            Badges = i % 6 == 4 ? null : new FlatBadge { Gold = i, Silver = -i, Bronze = random.Next() },
            Open = i % 7 == 3 ? null : i % 5 == 2 ? new FlatRawOpenSub { A = i * 9L, B = i, C = (short)-i }
                : new FlatRawOpen { A = random.Next() * 5L, B = -i },
        };

        private static FlatUser[] Users(int count, int seed = 31)
        {
            var random = new Random(seed);
            return Enumerable.Range(0, count).Select(i => User(random, i)).ToArray();
        }

        private static FlatPost[] Posts(int count, int seed = 32)
        {
            var random = new Random(seed);
            return Enumerable.Range(0, count).Select(i => i % 19 == 7 ? null : new FlatPost
            {
                Id = i, PostId = random.Next(), At = new DateTime(2022, 1, 1).AddMinutes(random.Next()), Score = i % 11,
                Edited = i % 2 == 0, Body = i % 9 == 5 ? null : Text(random, 10, 60, i % 3 == 0),
                Owner = User(random, i), Reply = User(random, i + 1), Link = "https://p/" + i,
            }).ToArray();
        }

        private static FlatUserTwin Twin(FlatUser v) => v == null ? null : new FlatUserTwin
            { UserId = v.UserId, Name = v.Name, Reputation = v.Reputation, Kind = v.Kind, Link = v.Link, Badges = v.Badges, Open = v.Open };

        private static FlatPostTwin Twin(FlatPost v) => v == null ? null : new FlatPostTwin
        {
            Id = v.Id, PostId = v.PostId, At = v.At, Score = v.Score, Edited = v.Edited, Body = v.Body,
            Owner = Twin(v.Owner), Reply = Twin(v.Reply), Link = v.Link,
        };

        [TestMethod]
        public void RawClassesNestAsBlocks()
        {
            Assert.IsTrue(HasFlat(Formatter<FlatUser>()));
            Assert.IsTrue(HasFlat(Formatter<FlatPost>()));
            Assert.IsFalse(HasFlat(Formatter<FlatUserTwin>()));
            // A raw body's own runs stay as they were: flat only nested.
            Assert.IsFalse(HasFlat(Formatter<FlatBadge>()));
            Assert.IsFalse(HasFlat(Formatter<FlatRawOpen>()));
            // The nested blocks are proven by the outer check, which includes the raw classes' own.
            if (Environment.Is64BitProcess && Type.GetType("Mono.Runtime") == null)
            {
                Assert.IsTrue(Checked<FlatBadge>("Write"));
                Assert.IsTrue(Checked<FlatUser>("Write"));
                Assert.IsTrue(Checked<FlatPost>("Write"));
            }

            Assert.AreEqual(Checked<FlatBadge>("Write") && Checked<FlatRawOpen>("Write"), Checked<FlatUser>("Write"));

            foreach (int count in RunCounts)
            {
                var users = Users(count);
                byte[] bytes = NinoSerializer.Serialize(users);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(users.Select(Twin).ToArray()), bytes, $"users {count}");
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(users.ToList()), $"list {count}");
                CollectionAssert.AreEqual(bytes, Each(users), $"each {count}");
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<FlatUser[]>(bytes)));
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<List<FlatUser>>(bytes)));

                var posts = Posts(count);
                byte[] postBytes = NinoSerializer.Serialize(posts);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(posts.Select(Twin).ToArray()), postBytes, $"posts {count}");
                CollectionAssert.AreEqual(postBytes, NinoSerializer.Serialize(posts.ToList()), $"post list {count}");
                CollectionAssert.AreEqual(postBytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<FlatPost[]>(postBytes)));
                CollectionAssert.AreEqual(postBytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<FlatPostTwin[]>(postBytes)));
                // Into existing elements: every member replaced, the outer instances kept, nested raw ones too where
                // the data has one of the same type.
                var existing = Posts(count, seed: 5);
                var kept = (FlatPost[])existing.Clone();
                NinoDeserializer.Deserialize(postBytes, ref existing);
                CollectionAssert.AreEqual(postBytes, NinoSerializer.Serialize(existing), $"into existing {count}");
                for (int i = 0; i < count; i++)
                    if (posts[i] != null && kept[i] != null)
                        Assert.AreSame(kept[i], existing[i], $"post {i} kept");
            }

            var (writes, reads) = Stacks(Posts(100).Where(static v => v != null).ToArray());
            Assert.IsTrue(writes[0].Contains("NinoFlat`2.WriteSealed"), "FlatPost[] write");
            Assert.IsTrue(reads[0].Contains("NinoFlat`2.ReadSealed"), "FlatPost[] read");
            var (userWrites, userReads) = Stacks(Users(100).Where(static v => v != null).ToArray());
            Assert.IsTrue(userWrites[1].Contains("NinoFlat`2.WriteSealed"), "List<FlatUser> write");
            Assert.IsTrue(userReads[1].Contains("NinoFlat`2.ReadSealed"), "List<FlatUser> read");
        }

        /// <summary>
        /// Hostile nested raw bodies: every cut, a presence byte or type id other than the ones written, rejected or
        /// left to the formatter alike by the flat read and the generated one.
        /// </summary>
        [TestMethod]
        public void HostileRawClassesAreReadAlikeBothWays()
        {
            var users = Users(NinoFlatRuns.MinRun + 4).Select(static v => v ?? new FlatUser()).ToArray();
            foreach (var user in users)
            {
                user.Badges ??= new FlatBadge { Gold = 1 };
                user.Open = new FlatRawOpen { A = 2, B = 3 };
            }

            byte[] bytes = NinoSerializer.Serialize(users);
            for (int cut = 0; cut < bytes.Length; cut++)
            {
                var prefix = bytes.AsSpan(0, cut).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatUser[]>(prefix), $"cut {cut}");
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatUserTwin[]>(prefix), $"twin cut {cut}");
            }

            // The last element's nested headers: Badges' presence byte, then Open's type id, are its last 1 + 12 + 4 + 12
            // bytes.
            int badges = bytes.Length - (1 + 12 + 4 + 12), open = badges + 1 + 12;
            foreach (byte flag in new byte[] { 2, 7, 0xFF })
            {
                byte[] bad = (byte[])bytes.Clone();
                bad[badges] = flag;
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatUser[]>(bad), $"presence {flag}");
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatUserTwin[]>(bad));
            }

            // Another type id: the subtype's (read by the formatter, the element left by the chain), and an unknown one.
            int subId = BitConverter.ToInt32(NinoSerializer.Serialize<FlatRawOpen>(new FlatRawOpenSub()), 0);
            foreach (int id in new[] { subId, 12345 })
            {
                byte[] other = (byte[])bytes.Clone();
                BitConverter.GetBytes(id).CopyTo(other, open);
                bool flatThrew = Throws(() => NinoDeserializer.Deserialize<FlatUser[]>(other), out var flat);
                bool twinThrew = Throws(() => NinoDeserializer.Deserialize<FlatUserTwin[]>(other), out var twin);
                Assert.AreEqual(twinThrew, flatThrew, $"id {id}");
                if (!flatThrew)
                    CollectionAssert.AreEqual(NinoSerializer.Serialize(twin), NinoSerializer.Serialize(flat.Select(Twin).ToArray()));
            }
        }

        private static bool Throws<T>(Func<T> read, out T value)
        {
            try
            {
                value = read();
                return false;
            }
            catch (NinoDataException)
            {
                value = default;
                return true;
            }
        }
    }
}
