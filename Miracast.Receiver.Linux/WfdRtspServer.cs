using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Miracast.Receiver.Linux;

internal sealed class WfdRtspServer : IAsyncDisposable
{
    public const int ControlPort = 7236;
    public const int RtpPort = 7236;
    public const int OutputWidth = 1920;
    public const int OutputHeight = 1080;

    private readonly TcpListener _listener = new(IPAddress.Any, ControlPort);
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;
    private TcpClient? _client;
    private bool _started;

    public event EventHandler? StreamReady;
    public event EventHandler? SessionClosed;
    public event EventHandler<string>? StatusChanged;

    public void Start(CancellationToken cancellationToken)
    {
        if (_started)
            return;
        _started = true;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
        Report($"WFD RTSP service is listening on TCP {ControlPort}.");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _client?.Dispose();
                _client = client;
                try
                {
                    await RunSessionAsync(client, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    Report($"WFD RTSP session failed: {exception.Message}");
                }
                finally
                {
                    client.Dispose();
                    if (ReferenceEquals(_client, client))
                        _client = null;
                    SessionClosed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        // AcceptTcpClientAsync may surface cancellation before the linked token's
        // IsCancellationRequested value is observable on this continuation.
        // Cancellation is always a normal terminal condition for this loop because
        // this is the only token passed to the accept operation.
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (!_started || cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException exception) when (
            !_started
            || cancellationToken.IsCancellationRequested
            || exception.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
        {
        }
        catch (Exception exception)
        {
            // The accept loop is a background task. Observe and report unexpected
            // failures here so they cannot become an unobserved task exception.
            Report($"WFD RTSP listener stopped unexpectedly: {exception.Message}");
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "source";
        Report($"Miracast source opened an RTSP session from {endpoint}.");

        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(stream, new ASCIIEncoding(), 4096, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true,
        };

        var outgoingCSeq = 1;
        string? presentationUrl = null;
        string? sessionId = null;
        var playSent = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await RtspMessage.ReadAsync(reader, cancellationToken).ConfigureAwait(false);
            if (message is null)
                break;

            if (message.IsResponse)
            {
                if (message.Headers.TryGetValue("Session", out var responseSession))
                    sessionId = responseSession.Split(';', 2)[0].Trim();

                if (message.Headers.TryGetValue("CSeq", out var responseCSeq)
                    && responseCSeq == (outgoingCSeq - 1).ToString()
                    && sessionId is not null
                    && presentationUrl is not null
                    && !playSent)
                {
                    await SendRequestAsync(writer, "PLAY", presentationUrl, outgoingCSeq++, sessionId,
                        cancellationToken).ConfigureAwait(false);
                    playSent = true;
                    StreamReady?.Invoke(this, EventArgs.Empty);
                    Report("WFD negotiation completed; waiting for the RTP media stream…");
                }
                continue;
            }

            var cseq = message.Headers.GetValueOrDefault("CSeq", "1");
            switch (message.Method)
            {
                case "OPTIONS":
                    await SendResponseAsync(writer, cseq, null,
                        new Dictionary<string, string>
                        {
                            ["Public"] = "org.wfa.wfd1.0, SETUP, TEARDOWN, PLAY, PAUSE, GET_PARAMETER, SET_PARAMETER",
                        }, cancellationToken).ConfigureAwait(false);
                    await SendRequestAsync(writer, "OPTIONS", "*", outgoingCSeq++, null, cancellationToken,
                        new Dictionary<string, string> { ["Require"] = "org.wfa.wfd1.0" }).ConfigureAwait(false);
                    break;

                case "GET_PARAMETER":
                    var capabilities = BuildCapabilities(message.Body);
                    await SendResponseAsync(writer, cseq, capabilities, null, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case "SET_PARAMETER":
                    presentationUrl = FindParameter(message.Body, "wfd_presentation_URL")?
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? presentationUrl;
                    await SendResponseAsync(writer, cseq, null, null, cancellationToken).ConfigureAwait(false);

                    if (message.Body.Contains("wfd_trigger_method: SETUP", StringComparison.OrdinalIgnoreCase))
                    {
                        var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString()
                            ?? "192.168.49.1";
                        presentationUrl ??= $"rtsp://{remoteAddress}/wfd1.0/streamid=0";
                        await SendRequestAsync(writer, "SETUP", presentationUrl, outgoingCSeq++, null,
                            cancellationToken,
                            new Dictionary<string, string>
                            {
                                ["Transport"] = $"RTP/AVP/UDP;unicast;client_port={RtpPort}",
                            }).ConfigureAwait(false);
                    }
                    break;

                case "TEARDOWN":
                    await SendResponseAsync(writer, cseq, null, null, cancellationToken).ConfigureAwait(false);
                    return;

                default:
                    await SendResponseAsync(writer, cseq, null, null, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static string BuildCapabilities(string requestedParameters)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wfd_content_protection"] = "none",
            ["wfd_video_formats"] = "00 00 03 10 0000001f 00000003 00000000 00 0000 0000 10 none none",
            ["wfd_audio_codecs"] = "LPCM 00000003 00, AAC 0000000f 00, AC3 00000007 00",
            ["wfd_client_rtp_ports"] = $"RTP/AVP/UDP;unicast {RtpPort} 0 mode=play",
            ["wfd_display_edid"] = "none",
            ["wfd_connector_type"] = "05",
            ["wfd_uibc_capability"] = "none",
            ["wfd_coupled_sink"] = "00 none",
            ["wfd_idr_request_capability"] = "1",
        };

        var requested = requestedParameters.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var response = new StringBuilder();
        foreach (var name in requested)
        {
            if (values.TryGetValue(name, out var value))
                response.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        return response.ToString();
    }

    private static string? FindParameter(string body, string name)
    {
        foreach (var line in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator > 0 && line[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(separator + 1)..].Trim();
        }
        return null;
    }

    private static async Task SendRequestAsync(
        StreamWriter writer,
        string method,
        string uri,
        int cseq,
        string? sessionId,
        CancellationToken cancellationToken,
        IDictionary<string, string>? extraHeaders = null)
    {
        await writer.WriteLineAsync($"{method} {uri} RTSP/1.0".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync($"CSeq: {cseq}".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (sessionId is not null)
            await writer.WriteLineAsync($"Session: {sessionId}".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
                await writer.WriteLineAsync($"{name}: {value}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendResponseAsync(
        StreamWriter writer,
        string cseq,
        string? body,
        IDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken)
    {
        body ??= string.Empty;
        await writer.WriteLineAsync("RTSP/1.0 200 OK".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync($"CSeq: {cseq}".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
                await writer.WriteLineAsync($"{name}: {value}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        if (body.Length > 0)
        {
            await writer.WriteLineAsync("Content-Type: text/parameters".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await writer.WriteLineAsync($"Content-Length: {Encoding.ASCII.GetByteCount(body)}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
            await writer.WriteAsync(body.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (!_started)
            return;
        _started = false;
        _lifetime?.Cancel();
        _listener.Stop();
        _client?.Dispose();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _acceptLoop = null;
        _client = null;
        _lifetime?.Dispose();
        _lifetime = null;
    }

    private void Report(string status) => StatusChanged?.Invoke(this, status);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed record RtspMessage(
        bool IsResponse,
        string Method,
        Dictionary<string, string> Headers,
        string Body)
    {
        public static async Task<RtspMessage?> ReadAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            string? firstLine;
            do
            {
                firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (firstLine is null)
                    return null;
            } while (firstLine.Length == 0);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("RTSP headers were truncated.");
                if (line.Length == 0)
                    break;
                var separator = line.IndexOf(':');
                if (separator > 0)
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            var length = headers.TryGetValue("Content-Length", out var lengthText)
                && int.TryParse(lengthText, out var parsedLength) ? parsedLength : 0;
            var bodyBuffer = new char[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await reader.ReadAsync(bodyBuffer.AsMemory(offset, length - offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("RTSP body was truncated.");
                offset += read;
            }

            var isResponse = firstLine.StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase);
            var method = isResponse ? string.Empty : firstLine.Split(' ', 2)[0].ToUpperInvariant();
            return new RtspMessage(isResponse, method, headers, new string(bodyBuffer));
        }
    }
}
