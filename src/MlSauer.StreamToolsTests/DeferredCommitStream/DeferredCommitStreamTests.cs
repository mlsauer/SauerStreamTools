using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MlSauer.StreamTools.Tests
{
    [TestClass()]
    public class DeferredCommitStreamTests
    {
        private readonly Random _Random = new Random();

        [TestMethod()]
        public void Ctor_MemoryStreamLength()
        {
            var shortArray = new byte[100];
            var longArray = new byte[16000];
            _Random.NextBytes(shortArray);
            _Random.NextBytes(longArray);

            using (var str = new DeferredCommitStream(new MemoryStream(), false))
            {
                Assert.AreEqual(0, str.Length);
            }

            using (var str = new DeferredCommitStream(new MemoryStream(shortArray), false))
            {
                Assert.AreEqual(shortArray.Length, str.Length);
            }

            using (var str = new DeferredCommitStream(new MemoryStream(longArray), false))
            {
                Assert.AreEqual(longArray.Length, str.Length);
            }
        }

        [TestMethod()]
        public void Dispose_MemoryStreamLeaveOpenFalse()
        {
            var memStr = new MemoryStream();
            var str = new DeferredCommitStream(memStr, false);
            str.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(() => str.Length);
            Assert.ThrowsException<ObjectDisposedException>(() => memStr.Length);
        }

        [TestMethod()]
        public void Dispose_MemoryStreamLeaveOpenTrue()
        {
            var memStr = new MemoryStream();
            var str = new DeferredCommitStream(memStr, true);
            str.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(() => str.Length);
            memStr.Seek(0, SeekOrigin.Begin);
        }

        [TestMethod()]
        public void Write_ByteArray()
        {
            var writeArrays = new byte[][] {
                new byte[100],
                new byte[16000],
            };
            var baseArray = new byte[160000];

            _Random.NextBytes(baseArray);

            foreach (var write in writeArrays)
            {
                _Random.NextBytes(write);

                using (var compareStream = new MemoryStream(baseArray))
                using (var underlyingStream = new MemoryStream(baseArray))
                using (var testStream = new DeferredCommitStream(underlyingStream, false))
                {
                    Assert.AreEqual(testStream.Position, compareStream.Position);

                    testStream.Seek(25, SeekOrigin.Begin);
                    compareStream.Seek(25, SeekOrigin.Begin);
                    Assert.AreEqual(testStream.Position, compareStream.Position);

                    testStream.Write(write, 10, write.Length - 10);
                    compareStream.Write(write, 10, write.Length - 10);
                    Assert.AreEqual(testStream.Length, compareStream.Length);
                    Assert.AreEqual(testStream.Position, compareStream.Position);

                    var compareStreamContents = compareStream.ToArray();

                    testStream.Seek(0, SeekOrigin.Begin);
                    var testStreamContentsBeforeCommit = new byte[testStream.Length];
                    testStream.Read(testStreamContentsBeforeCommit, 0, (int)testStream.Length);
                    Assert.IsTrue(Enumerable.SequenceEqual(testStreamContentsBeforeCommit, compareStreamContents), "Stream contents are different from expected before commit");

                    var underlyingStreamContents = underlyingStream.ToArray();
                    Assert.IsTrue(Enumerable.SequenceEqual(underlyingStreamContents, baseArray), "Underlying stream has unexpectedly been written to");

                    testStream.Commit();
                    Assert.IsTrue(Enumerable.SequenceEqual(underlyingStreamContents, compareStreamContents), "Writes have not been properly committed");

                    testStream.Seek(0, SeekOrigin.Begin);
                    var testStreamContentsAfterCommit = new byte[testStream.Length];
                    testStream.Read(testStreamContentsBeforeCommit, 0, (int)testStream.Length);
                    Assert.IsTrue(Enumerable.SequenceEqual(testStreamContentsBeforeCommit, compareStreamContents), "Stream contents are different from expected after commit");
                }
            }
        }
    }
}