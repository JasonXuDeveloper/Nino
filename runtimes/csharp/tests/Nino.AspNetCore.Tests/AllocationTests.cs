using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.AspNetCore.Tests
{
    /// <summary>
    /// Reading a request body and writing a response allocate nothing of their own: what is measured is the
    /// integration around a value that deserializes without allocating, on pipes that are already set up.
    /// </summary>
    [TestClass]
    public class AllocationTests
    {
        private sealed class BodyFeature : IRequestBodyPipeFeature
        {
            public PipeReader Reader { get; set; }
        }

        private sealed class ResponseFeature : IHttpResponseBodyFeature
        {
            public ResponseFeature(PipeWriter writer) => Writer = writer;

            public PipeWriter Writer { get; }
            public System.IO.Stream Stream => throw new NotSupportedException();
            public void DisableBuffering() { }
            public Task StartAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendFileAsync(string path, long offset, long? count,
                System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task CompleteAsync() => Task.CompletedTask;
        }

        /// <summary>
        /// Bytes this thread allocates over 100 calls of <paramref name="action"/>, after 100 to warm it up (static
        /// constructors, caches, the pipes' first segments). The calls are synchronous lambdas: an async one would
        /// allocate its own state machine on every call in a build without optimizations (Debug), where the compiler
        /// makes state machines classes.
        /// </summary>
        private static long Allocated(Action action)
        {
            for (int i = 0; i < 100; i++)
                action();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                action();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        [TestMethod]
        public void ReadingABodyAllocatesNothing()
        {
            var bytes = NinoSerializer.Serialize(new Point { X = 1, Y = 2 });
            var context = new DefaultHttpContext();
            var body = new BodyFeature();
            context.Features.Set<IRequestBodyPipeFeature>(body);
            context.Request.ContentType = "application/x-nino; charset=binary";
            context.Request.ContentLength = bytes.Length;
            var sequence = new ReadOnlySequence<byte>(bytes);
            var readers = new PipeReader[200];
            for (int i = 0; i < readers.Length; i++)
                readers[i] = PipeReader.Create(sequence);
            int next = 0;
            int y = 0;

            // The body is already in the pipe, so the read completes at once: that is the path measured.
            Assert.AreEqual(0, Allocated(() =>
            {
                body.Reader = readers[next++];
                var read = context.Request.ReadFromNinoAsync<Point>();
                y = read.IsCompletedSuccessfully ? read.Result.Y : -1;
            }));
            Assert.AreEqual(2, y);
        }

        [TestMethod]
        public void WritingAResponseAllocatesNothing()
        {
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpResponseBodyFeature>(new ResponseFeature(pipe.Writer));
            long expected = NinoSerializer.Serialize(new Point { X = 1, Y = 2 }).Length;
            long written = -1;

            // The pipe never pauses the writer, so the flush completes at once: that is the path measured.
            Assert.AreEqual(0, Allocated(() =>
            {
                written = -1;
                if (context.Response.WriteAsNinoAsync(new Point { X = 1, Y = 2 }).IsCompletedSuccessfully &&
                    pipe.Reader.TryRead(out var result))
                {
                    written = result.Buffer.Length;
                    pipe.Reader.AdvanceTo(result.Buffer.End);
                }
            }));
            Assert.AreEqual(expected, written);
            Assert.AreEqual(NinoMediaTypes.Application, context.Response.ContentType);
        }

        [TestMethod]
        public void RecognizesTheContentType()
        {
            Assert.IsTrue(NinoMediaTypes.IsNino("application/x-nino"));
            Assert.IsTrue(NinoMediaTypes.IsNino(" Application/X-Nino ; v=1"));
            Assert.IsFalse(NinoMediaTypes.IsNino("application/x-ninox"));
            Assert.IsFalse(NinoMediaTypes.IsNino("application/json"));
            Assert.IsFalse(NinoMediaTypes.IsNino(null));
        }
    }
}
