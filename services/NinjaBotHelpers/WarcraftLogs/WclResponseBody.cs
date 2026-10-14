using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if NINJABOT_HELPERS
namespace NinjaBotHelpers.WarcraftLogs.Auth;
#else
namespace NinjaBot.WarcraftLogs;
#endif

internal static class WclResponseBody
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
        internal static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            using var raw = await response.Content.ReadAsStreamAsync(cancellationToken);
            Stream decoded = raw;
            var wrappers = new List<Stream>();
            try
            {
                // Handler automatic decompression removes its encoding header. Decode remaining
                // encodings before counting bytes, so compressed/chunked payloads cannot evade the cap.
                foreach (var encoding in response.Content.Headers.ContentEncoding.Reverse())
                {
                    decoded = encoding.ToLowerInvariant() switch
                    {
                        "gzip" => new GZipStream(decoded, CompressionMode.Decompress, leaveOpen: true),
                        "deflate" => new ZLibStream(decoded, CompressionMode.Decompress, leaveOpen: true),
                        "br" => new BrotliStream(decoded, CompressionMode.Decompress, leaveOpen: true),
                        "identity" => decoded,
                        _ => throw new InvalidOperationException("Unsupported WCL response encoding.")
                    };
                    if (!encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) wrappers.Add(decoded);
                }
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var remaining = MaxResponseBytes + 1 - (int)body.Length;
                    var read = await decoded.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                    if (read == 0) break;
                    if (body.Length + read > MaxResponseBytes)
                        throw new InvalidOperationException("WCL response exceeds the 4 MiB limit.");
                    body.Write(buffer, 0, read);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
            }
            finally
            {
                for (var i = wrappers.Count - 1; i >= 0; i--) wrappers[i].Dispose();
            }
        }

}
