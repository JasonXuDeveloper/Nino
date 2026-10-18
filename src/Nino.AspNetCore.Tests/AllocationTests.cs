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

        private static long Allocated(Func<ValueTask> action)
        {
            for (int i = 0; i < 10; i++)
                action().GetAwaiter().GetResult();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                action().GetAwaiter().GetResult();
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
            var readers = new PipeReader[110];
            for (int i = 0; i < readers.Length; i++)
                readers[i] = PipeReader.Create(sequence);
            int next = 0;

            Assert.AreEqual(0, Allocated(async () =>
            {
                body.Reader = readers[next++];
                var point = await context.Request.ReadFromNinoAsync<Point>();
                Assert.AreEqual(2, point.Y);
            }));
        }

        [TestMethod]
        public void WritingAResponseAllocatesNothing()
        {
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpResponseBodyFeature>(new ResponseFeature(pipe.Writer));

            Assert.AreEqual(0, Allocated(async () =>
            {
                await context.Response.WriteAsNinoAsync(new Point { X = 1, Y = 2 });
                var result = await pipe.Reader.ReadAsync();
                pipe.Reader.AdvanceTo(result.Buffer.End);
            }));
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
