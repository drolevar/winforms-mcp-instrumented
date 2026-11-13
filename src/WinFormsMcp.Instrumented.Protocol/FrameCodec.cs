using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace WinFormsMcp.Instrumented.Protocol
{
    /// <summary>Length-prefixed UTF-8 JSON framing over a stream.</summary>
    /// <remarks>
    /// A connection must serialize writes externally. A failed or cancelled write
    /// can leave a partial frame; the connection owner must then close that stream.
    /// </remarks>
    public static class FrameCodec
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            MaxDepth = BridgeProtocol.MaxJsonDepth
        };

        public static async Task WriteAsync(
            Stream stream,
            object message,
            CancellationToken cancellationToken)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (message == null) throw new ArgumentNullException(nameof(message));
            cancellationToken.ThrowIfCancellationRequested();

            string json = JsonConvert.SerializeObject(message, Formatting.None, Settings);
            if (json.Length == 0 || json[0] != '{')
                throw new InvalidDataException("A bridge frame must contain a JSON object.");

            // Check the byte count before allocating the encoded payload.
            int payloadLength = Utf8.GetByteCount(json);
            if (payloadLength <= 0 || payloadLength > BridgeProtocol.MaxFrameBytes)
                throw new InvalidDataException("The serialized bridge message exceeds the frame limit.");

            byte[] payload = Utf8.GetBytes(json);
            byte[] header =
            {
                (byte)payloadLength,
                (byte)(payloadLength >> 8),
                (byte)(payloadLength >> 16),
                (byte)(payloadLength >> 24)
            };

            await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads one frame. Returns null only for clean EOF before the next header.
        /// Truncated frames, invalid UTF-8, and invalid payloads are errors.
        /// </summary>
        public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
            where T : class
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            cancellationToken.ThrowIfCancellationRequested();

            byte[] header = new byte[4];
            int firstRead = await stream.ReadAsync(header, 0, header.Length, cancellationToken)
                .ConfigureAwait(false);
            if (firstRead == 0) return null;
            await ReadRemainingAsync(stream, header, firstRead, cancellationToken).ConfigureAwait(false);

            uint length = (uint)header[0]
                | ((uint)header[1] << 8)
                | ((uint)header[2] << 16)
                | ((uint)header[3] << 24);
            if (length == 0 || length > BridgeProtocol.MaxFrameBytes)
                throw new InvalidDataException("The bridge frame length is outside the permitted range.");

            byte[] payload = new byte[(int)length];
            await ReadRemainingAsync(stream, payload, 0, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using (var textReader = new StringReader(Utf8.GetString(payload)))
                using (var jsonReader = new JsonTextReader(textReader)
                {
                    MaxDepth = BridgeProtocol.MaxJsonDepth,
                    DateParseHandling = DateParseHandling.None,
                    CloseInput = false
                })
                {
                    if (!jsonReader.Read() || jsonReader.TokenType != JsonToken.StartObject)
                        throw new InvalidDataException("A bridge frame must contain a JSON object.");

                    JObject value = JObject.Load(jsonReader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                    if (jsonReader.Read())
                        throw new InvalidDataException("A bridge frame must contain exactly one JSON object.");

                    T? message = value.ToObject<T>(JsonSerializer.Create(Settings));
                    return message ?? throw new InvalidDataException("A bridge message cannot be null.");
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The bridge frame contains invalid UTF-8.", exception);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The bridge frame contains invalid JSON or message data.", exception);
            }
        }

        private static async Task ReadRemainingAsync(
            Stream stream,
            byte[] buffer,
            int offset,
            CancellationToken cancellationToken)
        {
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("The connection ended within a bridge frame.");
                offset += read;
            }
        }
    }
}
