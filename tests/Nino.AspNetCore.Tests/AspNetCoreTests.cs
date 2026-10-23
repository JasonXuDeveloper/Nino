using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.AspNetCore.Tests
{
    public class Plain
    {
        public int X { get; set; }
    }

    [ApiController]
    [Route("mvc")]
    public class OrdersController : ControllerBase
    {
        [HttpPost("echo")]
        public Order Echo([FromBody] Order order) => order;

        [HttpGet("shape")]
        public ActionResult<Shape> GetShape() => new Circle { Name = "c", Radius = 2.5 };

        [HttpGet("order/{id}.{format?}")]
        [FormatFilter]
        public Order Get(int id) => Sample();

        [HttpGet("plain")]
        public Plain Unsupported() => new Plain { X = 1 };

        internal static Order Sample() =>
            new Order { Id = 42, Customer = "Ada", Items = new List<int> { 1, 2, 3 } };
    }

    [TestClass]
    public class AspNetCoreTests
    {
        private static WebApplication _app;
        private static HttpClient _client;

        [ClassInitialize]
        public static async Task Start(TestContext _)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly).AddNinoFormatters();
            _app = builder.Build();
            _app.MapControllers();
            _app.MapPost("/minimal/echo", (NinoBody<Order> order) => NinoResults.Ok(order.Value));
            _app.MapPost("/minimal/created", (NinoBody<Order> order) => NinoResults.Status(201, order.Value));
            _app.MapPost("/minimal/manual", async (HttpContext context) =>
            {
                var order = await context.Request.ReadFromNinoAsync<Order>();
                order.Id++;
                await context.Response.WriteAsNinoAsync(order);
            });
            _app.MapGet("/minimal/shape", () => NinoResults.Ok<Shape>(new Square { Name = "s", Side = 3 }));
            await _app.StartAsync();
            _client = _app.GetTestClient();
        }

        [ClassCleanup]
        public static async Task Stop()
        {
            _client?.Dispose();
            if (_app != null)
                await _app.DisposeAsync();
        }

        private static ByteArrayContent Nino<T>(T value) => Body(NinoSerializer.Serialize(value));

        private static ByteArrayContent Body(byte[] bytes)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(NinoMediaTypes.Application);
            return content;
        }

        private static HttpRequestMessage Request(HttpMethod method, string url, HttpContent content = null,
            string accept = NinoMediaTypes.Application)
        {
            var request = new HttpRequestMessage(method, url) { Content = content };
            if (accept != null)
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            return request;
        }

        private static async Task<T> Read<T>(HttpResponseMessage response)
        {
            Assert.AreEqual(NinoMediaTypes.Application, response.Content.Headers.ContentType?.MediaType);
            return NinoDeserializer.Deserialize<T>(await response.Content.ReadAsByteArrayAsync());
        }

        private static void AssertSample(Order expected, Order actual)
        {
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Customer, actual.Customer);
            CollectionAssert.AreEqual(expected.Items, actual.Items);
        }

        [TestMethod]
        public async Task MvcReadsAndWritesNino()
        {
            var order = OrdersController.Sample();
            using var response = await _client.SendAsync(Request(HttpMethod.Post, "/mvc/echo", Nino(order)));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            AssertSample(order, await Read<Order>(response));
        }

        [TestMethod]
        public async Task MvcKeepsJsonAsTheDefault()
        {
            using var response = await _client.SendAsync(Request(HttpMethod.Get, "/mvc/order/1", accept: null));
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        [TestMethod]
        public async Task MvcFormatFilterSelectsNino()
        {
            using var response = await _client.SendAsync(Request(HttpMethod.Get, "/mvc/order/1.nino", accept: null));
            AssertSample(OrdersController.Sample(), await Read<Order>(response));
        }

        [TestMethod]
        public async Task MvcWritesTheDeclaredTypeWithSubtypeId()
        {
            using var response = await _client.SendAsync(Request(HttpMethod.Get, "/mvc/shape"));
            var circle = (Circle)await Read<Shape>(response);
            Assert.AreEqual("c", circle.Name);
            Assert.AreEqual(2.5, circle.Radius);
        }

        [TestMethod]
        public async Task MvcRejectsInvalidData()
        {
            var bytes = NinoSerializer.Serialize(OrdersController.Sample());
            using var response = await _client.SendAsync(Request(HttpMethod.Post, "/mvc/echo",
                Body(bytes.AsSpan(0, bytes.Length - 3).ToArray())));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [TestMethod]
        public async Task MvcLeavesUnsupportedTypesToOtherFormatters()
        {
            Assert.IsFalse(NinoSerializer.IsSupported(typeof(Plain)));
            using var response = await _client.SendAsync(Request(HttpMethod.Get, "/mvc/plain"));
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        [TestMethod]
        public async Task MinimalApiBindsAndWrites()
        {
            var order = OrdersController.Sample();
            using var response = await _client.SendAsync(Request(HttpMethod.Post, "/minimal/echo", Nino(order)));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            AssertSample(order, await Read<Order>(response));
        }

        [TestMethod]
        public async Task MinimalApiStatusCode()
        {
            using var response = await _client.SendAsync(Request(HttpMethod.Post, "/minimal/created",
                Nino(OrdersController.Sample())));
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            AssertSample(OrdersController.Sample(), await Read<Order>(response));
        }

        [TestMethod]
        public async Task MinimalApiExtensions()
        {
            using var response = await _client.SendAsync(Request(HttpMethod.Post, "/minimal/manual",
                Nino(OrdersController.Sample())));
            Assert.AreEqual(43, (await Read<Order>(response)).Id);
        }

        [TestMethod]
        public async Task MinimalApiPolymorphicResult()
        {
            using var response = await _client.SendAsync(Request(HttpMethod.Get, "/minimal/shape"));
            Assert.AreEqual(3, ((Square)await Read<Shape>(response)).Side);
        }

        [TestMethod]
        public async Task MinimalApiRejectsOtherContentTypes()
        {
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(Request(HttpMethod.Post, "/minimal/echo", content));
            Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }

        [TestMethod]
        public async Task MinimalApiRejectsInvalidData()
        {
            using var response = await _client.SendAsync(Request(HttpMethod.Post, "/minimal/echo",
                Body(new byte[] { 1, 2 })));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // A body far larger than a pipe segment, sent in small chunks without a Content-Length: read across segments.
        [TestMethod]
        public async Task LargeChunkedBody()
        {
            var order = new Order
            {
                Id = 7, Customer = new string('x', 100_000), Items = Enumerable.Range(0, 50_000).ToList()
            };
            var content = new StreamContent(new ChunkedStream(NinoSerializer.Serialize(order)), 1000);
            content.Headers.ContentType = new MediaTypeHeaderValue(NinoMediaTypes.Application);
            foreach (var url in new[] { "/mvc/echo", "/minimal/echo" })
            {
                using var request = Request(HttpMethod.Post, url, content);
                using var response = await _client.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, url);
                AssertSample(order, await Read<Order>(response));
                content = new StreamContent(new ChunkedStream(NinoSerializer.Serialize(order)), 1000);
                content.Headers.ContentType = new MediaTypeHeaderValue(NinoMediaTypes.Application);
            }
        }

        // ReadFromNinoAsync reads a body already in the pipe without an async method and awaits one still arriving:
        // both give the value, and report errors through the task (never thrown by the call itself).
        [TestMethod]
        public async Task ReadFromNinoOnBothPaths()
        {
            var bytes = NinoSerializer.Serialize(new Point { X = 3, Y = 4 });
            foreach (bool arrived in new[] { true, false })
            {
                var (context, pipe) = BufferedRequest(NinoMediaTypes.Application, bytes.Length);
                if (arrived)
                    await pipe.Writer.WriteAsync(bytes);
                var task = context.Request.ReadFromNinoAsync<Point>();
                Assert.AreEqual(arrived, task.IsCompletedSuccessfully);
                if (!arrived)
                    await pipe.Writer.WriteAsync(bytes);
                Assert.AreEqual(4, (await task).Y);
            }

            foreach (bool arrived in new[] { true, false })
            {
                var (context, pipe) = BufferedRequest(NinoMediaTypes.Application, 2);
                if (arrived)
                    await pipe.Writer.WriteAsync(new byte[] { 1, 2 });
                var task = context.Request.ReadFromNinoAsync<Point>();
                if (!arrived)
                    await pipe.Writer.WriteAsync(new byte[] { 1, 2 });
                var e = await Assert.ThrowsExactlyAsync<BadHttpRequestException>(async () => await task);
                Assert.AreEqual(StatusCodes.Status400BadRequest, e.StatusCode);
            }

            var (json, _) = BufferedRequest("application/json", 2);
            var wrongType = json.Request.ReadFromNinoAsync<Point>();
            Assert.IsTrue(wrongType.IsFaulted);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await wrongType);
        }

        private static (DefaultHttpContext, System.IO.Pipelines.Pipe) BufferedRequest(string contentType, long length)
        {
            var pipe = new System.IO.Pipelines.Pipe();
            var context = new DefaultHttpContext();
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IRequestBodyPipeFeature>(
                new RequestBodyPipeFeature(pipe.Reader));
            context.Request.ContentType = contentType;
            context.Request.ContentLength = length;
            return (context, pipe);
        }

        private sealed class RequestBodyPipeFeature : Microsoft.AspNetCore.Http.Features.IRequestBodyPipeFeature
        {
            public RequestBodyPipeFeature(System.IO.Pipelines.PipeReader reader) => Reader = reader;
            public System.IO.Pipelines.PipeReader Reader { get; }
        }

        [TestMethod]
        public void IsSupported()
        {
            Assert.IsTrue(NinoSerializer.IsSupported(typeof(Order)));
            Assert.IsTrue(NinoSerializer.IsSupported(typeof(Shape)));
            Assert.IsTrue(NinoSerializer.IsSupported(typeof(int)));
            Assert.IsTrue(NinoSerializer.IsSupported(typeof(List<string>)));
            Assert.IsFalse(NinoSerializer.IsSupported(typeof(List<>)));
            Assert.IsFalse(NinoSerializer.IsSupported(typeof(Span<int>)));
            Assert.IsFalse(NinoSerializer.IsSupported(typeof(Type)));
            Assert.IsFalse(NinoSerializer.IsSupported(typeof(Stream)));
            Assert.IsFalse(NinoSerializer.IsSupported(typeof(int).MakeByRefType()));
        }

        /// <summary>A stream with no known length that returns at most 1000 bytes per read.</summary>
        private sealed class ChunkedStream : MemoryStream
        {
            public ChunkedStream(byte[] data) : base(data) { }
            public override bool CanSeek => false;
            public override int Read(byte[] buffer, int offset, int count) =>
                base.Read(buffer, offset, Math.Min(count, 1000));
            public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1000)]);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                System.Threading.CancellationToken cancellationToken) =>
                base.ReadAsync(buffer, offset, Math.Min(count, 1000), cancellationToken);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer,
                System.Threading.CancellationToken cancellationToken = default) =>
                base.ReadAsync(buffer[..Math.Min(buffer.Length, 1000)], cancellationToken);
        }
    }
}
