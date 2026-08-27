using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Miracast.Receiver.Linux;

internal sealed class WfdRtspServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Any, MiracastReceiverService.RtspPort);
    private readonly Action<string> _log;
    private readonly Action _connected;
    private readonly Action _disconnected;
    private readonly Action<int, int> _videoReady;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;
    private TcpClient? _client;

    public WfdRtspServer(
        Action<string> log,
        Action connected,
        Action disconnected,
        Action<int, int> videoReady)
    {
        _log = log;
        _connected = connected;
        _disconnected = disconnected;
        _videoReady = videoReady;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _listener.Start(1);
        _acceptLoop = AcceptLoopAsync(_stopping.Token);
        _log($"WFD RTSP sink is listening on TCP {MiracastReceiverService.RtspPort}.");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var previous = Interlocked.Exchange(ref _client, client);
                previous?.Dispose();
                try
                {
                    await RunSessionAsync(client, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    _log($"WFD RTSP session failed: {exception.Message}");
                }
                finally
                {
                    if (ReferenceEquals(Interlocked.CompareExchange(ref _client, null, client), client))
                        client.Dispose();
                    _disconnected();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        var session = new WfdRtspSession(
            client.GetStream(),
            _log,
            _connected,
            _videoReady);
        await session.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        Interlocked.Exchange(ref _client, null)?.Dispose();
        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(false);
        _stopping.Dispose();
    }
}

internal sealed class WfdRtspSession(
    NetworkStream stream,
    Action<string> log,
    Action connected,
    Action<int, int> videoReady)
{
    private static readonly string CapabilityBody =
        "wfd_audio_codecs: LPCM 00000002 00\r\n" +
        "wfd_video_formats: 00 00 02 10 0001FFFF 1FFFFFFF 00000FFF 00 0000 0000 00 none none\r\n" +
        $"wfd_client_rtp_ports: RTP/AVP/UDP;unicast {MiracastReceiverService.RtpPort} 0 mode=play\r\n" +
        "wfd_content_protection: none\r\n" +
        "wfd_display_edid: none\r\n" +
        "wfd_coupled_sink: none\r\n" +
        "wfd_uibc_capability: none\r\n" +
        "wfd_standby_resume_capability: none\r\n";

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string _presentationUrl = "rtsp://localhost/wfd1.0/streamid=0";
    private string? _sessionId;
    private int _nextCSeq = 1;
    private int _width = 1920;
    private int _height = 1080;
    private readonly Dictionary<int, string> _pendingMethods = new();
    private bool _optionsSent;
    private bool _videoAnnounced;
    private bool _teardownRequested;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await RtspMessage.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (message is null)
                return;

            log($"RTSP <- {message.StartLine}");
            if (message.IsResponse)
                await HandleResponseAsync(message, cancellationToken).ConfigureAwait(false);
            else
                await HandleRequestAsync(message, cancellationToken).ConfigureAwait(false);

            if (_teardownRequested)
                return;
        }
    }

    private async Task HandleRequestAsync(RtspMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method;
        switch (method)
        {
            case "OPTIONS":
                await SendResponseAsync(
                    request,
                    additionalHeaders: new Dictionary<string, string>
                    {
                        ["Public"] = "org.wfa.wfd1.0, GET_PARAMETER, SET_PARAMETER",
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!_optionsSent)
                {
                    _optionsSent = true;
                    await SendRequestAsync(
                        "OPTIONS",
                        "*",
                        new Dictionary<string, string>
                        {
                            ["Require"] = "org.wfa.wfd1.0",
                        },
                        null,
                        cancellationToken).ConfigureAwait(false);
                }
                break;

            case "GET_PARAMETER":
                var body = BuildCapabilityResponse(request.Body);
                await SendResponseAsync(request, body: body, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "SET_PARAMETER":
                ApplySourceParameters(request.Body);
                await SendResponseAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (HasParameter(request.Body, "wfd_trigger_method", "SETUP"))
                    await SendSetupAsync(cancellationToken).ConfigureAwait(false);
                else if (HasParameter(request.Body, "wfd_trigger_method", "TEARDOWN"))
                    _teardownRequested = true;
                break;

            case "TEARDOWN":
                await SendResponseAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
                _teardownRequested = true;
                break;

            default:
                await SendResponseAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleResponseAsync(RtspMessage response, CancellationToken cancellationToken)
    {
        if (!response.Headers.TryGetValue("CSeq", out var cseqText)
            || !int.TryParse(cseqText, NumberStyles.None, CultureInfo.InvariantCulture, out var cseq))
            return;

        if (response.Headers.TryGetValue("Session", out var sessionHeader))
            _sessionId = sessionHeader.Split(';', 2)[0].Trim();

        if (!_pendingMethods.Remove(cseq, out var pendingMethod))
            return;

        if (pendingMethod == "SETUP")
        {
            await SendPlayAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (pendingMethod == "PLAY" && !_videoAnnounced)
            AnnounceVideo();
    }

    private async Task SendSetupAsync(CancellationToken cancellationToken)
    {
        var cseq = await SendRequestAsync(
            "SETUP",
            _presentationUrl,
            new Dictionary<string, string>
            {
                ["Transport"] = $"RTP/AVP/UDP;unicast;client_port={MiracastReceiverService.RtpPort}",
            },
            null,
            cancellationToken).ConfigureAwait(false);
        _pendingMethods[cseq] = "SETUP";
    }

    private async Task SendPlayAsync(CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>();
        if (_sessionId is not null)
            headers["Session"] = _sessionId;
        var cseq = await SendRequestAsync("PLAY", _presentationUrl, headers, null, cancellationToken)
            .ConfigureAwait(false);
        _pendingMethods[cseq] = "PLAY";
    }

    private void AnnounceVideo()
    {
        if (_videoAnnounced)
            return;
        _videoAnnounced = true;
        connected();
        videoReady(_width, _height);
    }

    private void ApplySourceParameters(string body)
    {
        foreach (var line in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("wfd_presentation_URL:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(line.IndexOf(':') + 1)..].Trim();
                var separator = value.IndexOf(' ');
                _presentationUrl = separator < 0 ? value : value[..separator];
            }
            else if (line.StartsWith("wfd_video_formats:", StringComparison.OrdinalIgnoreCase)
                     && WfdVideoFormatParser.TryParseResolution(line, out var width, out var height))
            {
                _width = width;
                _height = height;
            }
        }
    }

    private static bool HasParameter(string body, string name, string value) =>
        body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)
                         && line[(line.IndexOf(':') + 1)..].Trim().Equals(value, StringComparison.OrdinalIgnoreCase));

    private static string BuildCapabilityResponse(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            return string.Empty;

        var requested = requestBody.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimEnd(':'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var line in CapabilityBody.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator > 0 && requested.Contains(line[..separator]))
                builder.Append(line).Append("\r\n");
        }
        return builder.ToString();
    }

    private async Task SendResponseAsync(
        RtspMessage request,
        IDictionary<string, string>? additionalHeaders = null,
        string? body = null,
        CancellationToken cancellationToken = default)
    {
        var headers = new Dictionary<string, string>();
        if (request.Headers.TryGetValue("CSeq", out var cseq))
            headers["CSeq"] = cseq;
        if (_sessionId is not null)
            headers["Session"] = _sessionId;
        if (additionalHeaders is not null)
        {
            foreach (var item in additionalHeaders)
                headers[item.Key] = item.Value;
        }
        await WriteAsync(new RtspMessage("RTSP/1.0 200 OK", headers, body ?? string.Empty), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> SendRequestAsync(
        string method,
        string uri,
        IDictionary<string, string> headers,
        string? body,
        CancellationToken cancellationToken)
    {
        var cseq = _nextCSeq++;
        var requestHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
        {
            ["CSeq"] = cseq.ToString(CultureInfo.InvariantCulture),
        };
        await WriteAsync(new RtspMessage($"{method} {uri} RTSP/1.0", requestHeaders, body ?? string.Empty), cancellationToken)
            .ConfigureAwait(false);
        return cseq;
    }

    private async Task WriteAsync(RtspMessage message, CancellationToken cancellationToken)
    {
        var bytes = message.Serialize();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            log($"RTSP -> {message.StartLine}");
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

internal sealed class RtspMessage(string startLine, IDictionary<string, string> headers, string body)
{
    private const int MaximumHeaderLength = 64 * 1024;
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    public string StartLine { get; } = startLine;
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
    public string Body { get; } = body;
    public bool IsResponse => StartLine.StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase);
    public string Method => StartLine.Split(' ', 2)[0].ToUpperInvariant();

    public byte[] Serialize()
    {
        var bodyBytes = Encoding.UTF8.GetBytes(Body);
        var builder = new StringBuilder(StartLine).Append("\r\n");
        foreach (var header in Headers)
            builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        if (bodyBytes.Length > 0)
        {
            if (!Headers.ContainsKey("Content-Type"))
                builder.Append("Content-Type: text/parameters\r\n");
            builder.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        }
        builder.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
        var result = new byte[headerBytes.Length + bodyBytes.Length];
        headerBytes.CopyTo(result, 0);
        bodyBytes.CopyTo(result, headerBytes.Length);
        return result;
    }

    public static async Task<RtspMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var header = new MemoryStream();
        var matched = 0;
        var oneByte = new byte[1];
        while (header.Length < MaximumHeaderLength)
        {
            var read = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return header.Length == 0 ? null : throw new EndOfStreamException("RTSP header ended unexpectedly.");
            header.WriteByte(oneByte[0]);
            matched = oneByte[0] == HeaderTerminator[matched]
                ? matched + 1
                : oneByte[0] == HeaderTerminator[0] ? 1 : 0;
            if (matched == HeaderTerminator.Length)
                break;
        }
        if (matched != HeaderTerminator.Length)
            throw new InvalidDataException("RTSP header exceeds 64 KiB.");

        var headerText = Encoding.ASCII.GetString(header.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
                break;
            var separator = line.IndexOf(':');
            if (separator <= 0)
                throw new InvalidDataException($"Invalid RTSP header: {line}");
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var contentLengthText)
            && (!int.TryParse(contentLengthText, out contentLength) || contentLength < 0 || contentLength > 1024 * 1024))
            throw new InvalidDataException($"Invalid RTSP Content-Length: {contentLengthText}");
        var bodyBytes = new byte[contentLength];
        if (contentLength > 0)
            await stream.ReadExactlyAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        return new RtspMessage(lines[0], headers, Encoding.UTF8.GetString(bodyBytes));
    }
}

internal static class WfdVideoFormatParser
{
    private static readonly (int Width, int Height)[] CeaModes =
    [
        (640, 480), (720, 480), (720, 480), (720, 576), (720, 576),
        (1280, 720), (1280, 720), (1920, 1080), (1920, 1080), (1920, 1080),
        (1920, 1080), (1280, 720), (1280, 720), (1920, 1080), (1920, 1080),
        (1920, 1080), (1920, 1080), (1920, 1080),
    ];

    public static bool TryParseResolution(string line, out int width, out int height)
    {
        width = 0;
        height = 0;
        var separator = line.IndexOf(':');
        var fields = (separator >= 0 ? line[(separator + 1)..] : line)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length < 7 || !uint.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cea))
            return false;
        for (var bit = CeaModes.Length - 1; bit >= 0; bit--)
        {
            if ((cea & (1u << bit)) == 0)
                continue;
            (width, height) = CeaModes[bit];
            return true;
        }
        return false;
    }
}
