// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Net.Sockets;

using Microsoft.Testing.TestInfrastructure;

using Newtonsoft.Json.Linq;

namespace Microsoft.Testing.Platform.ServerMode.IntegrationTests.Messages.V100;

/// <summary>
/// A minimal Microsoft.Testing.Platform server-protocol client that reads and writes <b>raw</b>,
/// pre-serialized JSON-RPC frames directly on the TCP socket, without going through StreamJsonRpc.
/// This lets a test replay the exact bytes captured from Visual Studio Test Explorer (including its
/// JSON unicode escapes such as <c>\u0022</c> and <c>\u0026</c> and a literal <c>|</c>) and thereby
/// exercise the server's own JSON parse/unescape path end to end.
/// <para>
/// The framing matches <c>TcpMessageHandler</c>: <c>Content-Length: N\r\n</c> header(s), a blank
/// <c>\r\n</c> line, then <c>N</c> bytes of UTF-8 body.
/// </para>
/// </summary>
public sealed class RawTestingPlatformClient : IDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly IProcessHandle _processHandler;
    private readonly NetworkStream _stream;

    public RawTestingPlatformClient(TcpClient tcpClient, IProcessHandle processHandler)
    {
        _tcpClient = tcpClient;
        _processHandler = processHandler;
        _stream = tcpClient.GetStream();
    }

    public int ExitCode => _processHandler.ExitCode;

    public async Task<int> WaitServerProcessExitAsync(CancellationToken cancellationToken)
    {
        await _processHandler.WaitForExitAsync(cancellationToken);
        return _processHandler.ExitCode;
    }

    /// <summary>
    /// Writes a single JSON-RPC message frame with the given raw JSON body, exactly as the bytes appear
    /// (no re-serialization). <paramref name="jsonBody"/> is sent verbatim as UTF-8.
    /// </summary>
    public async Task SendRawAsync(string jsonBody, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(jsonBody);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\nContent-Type: application/testingplatform\r\n\r\n");
        await _stream.WriteAsync(header, cancellationToken);
        await _stream.WriteAsync(body, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the next framed JSON-RPC message and returns it parsed, or <see langword="null"/> on EOF.
    /// </summary>
    public async Task<JObject?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        int contentLength = -1;
        while (true)
        {
            string? line = await ReadHeaderLineAsync(cancellationToken);
            if (line is null)
            {
                // Connection closed.
                return null;
            }

            if (line.Length == 0)
            {
                if (contentLength >= 0)
                {
                    break;
                }

                // Tolerate a leading blank line between frames.
                continue;
            }

            const string ContentLengthHeaderName = "Content-Length:";
            if (line.StartsWith(ContentLengthHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(line[ContentLengthHeaderName.Length..].Trim(), out contentLength);
            }
        }

        byte[] body = await ReadExactlyAsync(contentLength, cancellationToken);
        return JObject.Parse(Encoding.UTF8.GetString(body));
    }

    public Task ExitAsync(CancellationToken cancellationToken)
        => SendRawAsync("""{"jsonrpc":"2.0","method":"exit","params":{}}""", cancellationToken);

    public void Dispose()
    {
        _stream.Dispose();
        _tcpClient.Dispose();
        _processHandler.Dispose();
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        // Header lines are ASCII and terminated by \r\n. Read byte-by-byte so we don't overshoot into
        // the body (the body is length-prefixed and read separately).
        var bytes = new List<byte>();
        byte[] one = new byte[1];
        while (true)
        {
            int read = await _stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }

            if (one[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }

                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add(one[0]);
        }
    }

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException($"Expected {count} bytes but the stream ended after {offset}.");
            }

            offset += read;
        }

        return buffer;
    }
}
