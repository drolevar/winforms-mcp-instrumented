using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Protocol;

namespace WinFormsMcp.Instrumented.Protocol.Tests
{
    [TestClass]
    public sealed class FrameCodecTests
    {
        [TestMethod]
        public async Task RoundTrip_PreservesUnicodeAndJsonValueTypes()
        {
            var request = new RpcRequest
            {
                Id = "42",
                Token = "test-token",
                Method = "set_text",
                Parameters = new JObject
                {
                    ["text"] = "Bergen — æøå — 日本語 — \U0001F642",
                    ["number"] = 17,
                    ["flag"] = true,
                    ["dateLikeText"] = "2026-11-13T12:00:00Z"
                }
            };
            using (var stream = new MemoryStream())
            {
                await FrameCodec.WriteAsync(stream, request, CancellationToken.None);
                stream.Position = 0;
                RpcRequest? actual = await FrameCodec.ReadAsync<RpcRequest>(stream, CancellationToken.None);

                Assert.IsNotNull(actual);
                Assert.AreEqual(request.Id, actual!.Id);
                Assert.AreEqual(request.Method, actual.Method);
                Assert.IsTrue(JToken.DeepEquals(request.Parameters, actual.Parameters));
                Assert.AreEqual(JTokenType.String, actual.Parameters["dateLikeText"]!.Type);
                Assert.IsNull(await FrameCodec.ReadAsync<RpcRequest>(stream, CancellationToken.None));
            }
        }

        [TestMethod]
        public async Task Read_HandlesSingleByteFragmentsAndConsecutiveFrames()
        {
            using (var output = new MemoryStream())
            {
                await FrameCodec.WriteAsync(output, new RpcRequest { Id = "one", Method = "hello" }, CancellationToken.None);
                await FrameCodec.WriteAsync(output, new RpcRequest { Id = "two", Method = "tree" }, CancellationToken.None);
                using (var input = new FragmentedReadStream(output.ToArray(), 1))
                {
                    Assert.AreEqual("one", (await FrameCodec.ReadAsync<RpcRequest>(input, CancellationToken.None))!.Id);
                    Assert.AreEqual("two", (await FrameCodec.ReadAsync<RpcRequest>(input, CancellationToken.None))!.Id);
                    Assert.IsNull(await FrameCodec.ReadAsync<RpcRequest>(input, CancellationToken.None));
                }
            }
        }

        [TestMethod]
        public async Task Write_UsesCamelCaseAndLittleEndianByteLength()
        {
            using (var stream = new MemoryStream())
            {
                await FrameCodec.WriteAsync(stream, new RpcResponse
                {
                    Id = "42",
                    Generation = "app-generation",
                    Result = new JObject { ["text"] = "ø" }
                }, CancellationToken.None);

                byte[] bytes = stream.ToArray();
                int length = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
                Assert.AreEqual(bytes.Length - 4, length);
                JObject json = JObject.Parse(Encoding.UTF8.GetString(bytes, 4, length));
                Assert.AreEqual(1, (int)json["protocolVersion"]!);
                Assert.AreEqual("app-generation", (string?)json["generation"]);
                Assert.IsNull(json["ProtocolVersion"]);
                Assert.IsNull(json["error"]);
            }
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public async Task Read_RejectsPartialHeader(int bytes)
        {
            using (var stream = new MemoryStream(new byte[bytes]))
                await ExpectExceptionAsync<EndOfStreamException>(
                    () => FrameCodec.ReadAsync<RpcRequest>(stream, CancellationToken.None));
        }

        [TestMethod]
        public async Task Read_RejectsPartialPayload()
        {
            using (var stream = new MemoryStream(new byte[] { 8, 0, 0, 0, (byte)'{' }))
                await ExpectExceptionAsync<EndOfStreamException>(
                    () => FrameCodec.ReadAsync<RpcRequest>(stream, CancellationToken.None));
        }

        [TestMethod]
        [DataRow(0u)]
        [DataRow(16777217u)]
        [DataRow(uint.MaxValue)]
        public async Task Read_RejectsLengthBeforeAllocatingPayload(uint length)
        {
            byte[] header = { (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)(length >> 24) };
            using (var stream = new MemoryStream(header))
                await ExpectExceptionAsync<InvalidDataException>(
                    () => FrameCodec.ReadAsync<RpcRequest>(stream, CancellationToken.None));
        }

        [TestMethod]
        [DataRow("{")]
        [DataRow("null")]
        [DataRow("[]")]
        [DataRow("{} {}")]
        [DataRow("{\"id\":\"one\",\"id\":\"two\"}")]
        public async Task Read_RejectsInvalidOrAmbiguousJson(string json)
        {
            using (var stream = PayloadStream(Encoding.UTF8.GetBytes(json)))
                await ExpectExceptionAsync<InvalidDataException>(
                    () => FrameCodec.ReadAsync<RpcRequest>(stream, CancellationToken.None));
        }

        [TestMethod]
        public async Task Read_RejectsInvalidUtf8()
        {
            byte[] payload = { 0x7b, 0x22, 0x78, 0x22, 0x3a, 0x22, 0xff, 0x22, 0x7d };
            using (var stream = PayloadStream(payload))
                await ExpectExceptionAsync<InvalidDataException>(
                    () => FrameCodec.ReadAsync<RpcRequest>(stream, CancellationToken.None));
        }

        [TestMethod]
        public async Task Read_RejectsExcessiveNesting()
        {
            string json = "{\"parameters\":{\"x\":" + new string('[', 70) + "0" + new string(']', 70) + "}}";
            using (var stream = PayloadStream(Encoding.UTF8.GetBytes(json)))
                await ExpectExceptionAsync<InvalidDataException>(
                    () => FrameCodec.ReadAsync<RpcRequest>(stream, CancellationToken.None));
        }

        [TestMethod]
        public async Task Read_DoesNotUseTypeMetadataToConstructRuntimeTypes()
        {
            const string json = "{\"$type\":\"An.Unavailable.Type, MissingAssembly\",\"id\":\"ok\",\"method\":\"hello\"}";
            using (var stream = PayloadStream(Encoding.UTF8.GetBytes(json)))
            {
                RpcRequest? request = await FrameCodec.ReadAsync<RpcRequest>(stream, CancellationToken.None);
                Assert.AreEqual("ok", request!.Id);
            }
        }

        [TestMethod]
        public async Task CancelledToken_PreventsReadAndWrite()
        {
            using (var cancellation = new CancellationTokenSource())
            using (var stream = new MemoryStream())
            {
                cancellation.Cancel();
                await ExpectExceptionAsync<OperationCanceledException>(
                    () => FrameCodec.ReadAsync<RpcRequest>(stream, cancellation.Token));
                await ExpectExceptionAsync<OperationCanceledException>(
                    () => FrameCodec.WriteAsync(stream, new RpcRequest(), cancellation.Token));
                Assert.AreEqual(0L, stream.Length);
            }
        }

        [TestMethod]
        public async Task Write_RejectsOversizePayloadWithoutWritingHeader()
        {
            using (var stream = new MemoryStream())
            {
                var message = new RpcResponse { Result = new JValue(new string('x', BridgeProtocol.MaxFrameBytes)) };
                await ExpectExceptionAsync<InvalidDataException>(
                    () => FrameCodec.WriteAsync(stream, message, CancellationToken.None));
                Assert.AreEqual(0L, stream.Length);
            }
        }

        private static MemoryStream PayloadStream(byte[] payload)
        {
            var stream = new MemoryStream();
            int length = payload.Length;
            stream.WriteByte((byte)length);
            stream.WriteByte((byte)(length >> 8));
            stream.WriteByte((byte)(length >> 16));
            stream.WriteByte((byte)(length >> 24));
            stream.Write(payload, 0, payload.Length);
            stream.Position = 0;
            return stream;
        }

        private static async Task ExpectExceptionAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action();
            }
            catch (TException)
            {
                return;
            }

            Assert.Fail("Expected " + typeof(TException).Name + ".");
        }

        private sealed class FragmentedReadStream : Stream
        {
            private readonly MemoryStream inner;
            private readonly int fragmentSize;

            public FragmentedReadStream(byte[] bytes, int fragmentSize)
            {
                inner = new MemoryStream(bytes);
                this.fragmentSize = fragmentSize;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, fragmentSize));
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
