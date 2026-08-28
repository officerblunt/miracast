using System.Globalization;

namespace Miracast.Receiver.Linux;

internal sealed class WfdSession : IAsyncDisposable
{
    private const string DefaultRtspUri = "rtsp://localhost/wfd1.0";
    private readonly P2PConnectionContext _connection;
    private readonly IVideoRenderer _renderer;
    private readonly Action<VideoSource> _mediaReady;
    private readonly Action<string> _report;
    private readonly RtspDuplexClient _rtsp = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _m1Received = NewSignal();
    private readonly TaskCompletionSource _setupTriggered = NewSignal();
    private readonly TaskCompletionSource _sessionEnded = NewSignal();
    private RtpPortReservation? _ports;
    private string _presentationUrl = DefaultRtspUri;
    private string? _sessionId;
    private int _width = 1280;
    private int _height = 720;
    private DateTimeOffset _playStartedAt;
    private bool _rendererStarted;
    private bool _disposed;

    public WfdSession(
        P2PConnectionContext connection,
        IVideoRenderer renderer,
        Action<VideoSource> mediaReady,
        Action<string> report)
    {
        _connection = connection;
        _renderer = renderer;
        _mediaReady = mediaReady;
        _report = report;
        _rtsp.Disconnected += OnDisconnected;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = run.Token;
        Task? requestLoop = null;
        Task? watchdog = null;
        try
        {
            _report($"Connecting to WFD Source {_connection.SourceAddress}:{_connection.WfdControlPort}…");
            await _rtsp.ConnectAsync(_connection.SourceAddress, _connection.WfdControlPort, token)
                .ConfigureAwait(false);
            requestLoop = ProcessSourceRequestsAsync(token);

            await _m1Received.Task.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            var options = await _rtsp.SendRequestAsync(
                "OPTIONS",
                "*",
                new Dictionary<string, string>
                {
                    ["Require"] = "org.wfa.wfd1.0",
                },
                cancellationToken: token).ConfigureAwait(false);
            options.EnsureSuccess();
            _report("WFD M1/M2 OPTIONS completed; waiting for capability negotiation…");

            await _setupTriggered.Task.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            _ports ??= RtpPortReservation.Reserve();
            var source = new VideoSource(
                new Uri($"rtp://{_connection.LocalAddress}:{_ports.RtpPort}"),
                _width,
                _height);

            // GStreamer must own and bind the RTP port before SETUP/PLAY.
            _ports.Dispose();
            _ports = null;
            await _renderer.PlayAsync(source, token).ConfigureAwait(false);
            _rendererStarted = true;

            var setup = await _rtsp.SendRequestAsync(
                "SETUP",
                _presentationUrl,
                new Dictionary<string, string>
                {
                    ["Transport"] = $"RTP/AVP/UDP;unicast;client_port={source.StreamUri.Port}-{source.StreamUri.Port + 1}",
                },
                cancellationToken: token).ConfigureAwait(false);
            setup.EnsureSuccess();
            _sessionId = ReadSessionId(setup);
            if (setup.Headers.TryGetValue("Transport", out var negotiatedTransport))
                _report($"WFD SETUP transport: {negotiatedTransport}.");

            var play = await _rtsp.SendRequestAsync(
                "PLAY",
                _presentationUrl,
                SessionHeaders(),
                cancellationToken: token).ConfigureAwait(false);
            play.EnsureSuccess();
            _playStartedAt = DateTimeOffset.UtcNow;
            _mediaReady(source);
            _report($"WFD M1-M7 completed. Receiving RTP on UDP {source.StreamUri.Port}.");

            watchdog = WatchMediaAsync(token);
            var completed = await Task.WhenAny(_sessionEnded.Task, watchdog).ConfigureAwait(false);
            await completed.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            run.Cancel();
            if (watchdog is not null)
            {
                try { await watchdog.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (requestLoop is not null)
            {
                try { await requestLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (_rendererStarted)
            {
                await _renderer.StopAsync().ConfigureAwait(false);
                _rendererStarted = false;
            }
        }
    }

    private async Task ProcessSourceRequestsAsync(CancellationToken cancellationToken)
    {
        await foreach (var request in _rtsp.ReadRequestsAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (request.Method.ToUpperInvariant())
            {
                case "OPTIONS":
                    await _rtsp.SendResponseAsync(
                        request,
                        headers: new Dictionary<string, string>
                        {
                            ["Public"] = "org.wfa.wfd1.0, GET_PARAMETER, SET_PARAMETER",
                        },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    _m1Received.TrySetResult();
                    break;

                case "GET_PARAMETER":
                    await HandleGetParameterAsync(request, cancellationToken).ConfigureAwait(false);
                    break;

                case "SET_PARAMETER":
                    await HandleSetParameterAsync(request, cancellationToken).ConfigureAwait(false);
                    break;

                case "PLAY":
                case "PAUSE":
                    await _rtsp.SendResponseAsync(
                        request,
                        headers: SessionHeaders(),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;

                case "TEARDOWN":
                    await _rtsp.SendResponseAsync(
                        request,
                        headers: SessionHeaders(),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    _sessionId = null;
                    _sessionEnded.TrySetResult();
                    return;

                default:
                    await _rtsp.SendResponseAsync(
                        request,
                        405,
                        "Method Not Allowed",
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        _sessionEnded.TrySetResult();
    }

    private async Task HandleGetParameterAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            await _rtsp.SendResponseAsync(
                request,
                headers: SessionHeaders(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        _ports ??= RtpPortReservation.Reserve();
        var requested = request.Body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wfd_video_formats"] = "00 00 02 10 000000a0 00000000 00000000 00 0000 0000 00 none none",
            ["wfd_audio_codecs"] = "LPCM 00000002 00",
            ["wfd_client_rtp_ports"] = $"RTP/AVP/UDP;unicast {_ports.RtpPort} 0 mode=play",
            ["wfd_content_protection"] = "none",
            ["wfd_display_edid"] = "none",
            ["wfd_uibc_capability"] = "none",
            ["wfd_connector_type"] = "05",
            ["wfd_standby_resume_capability"] = "none",
        };

        var body = string.Join(string.Empty, requested.Select(name =>
            values.TryGetValue(name, out var value) ? $"{name}: {value}\r\n" : $"{name}: none\r\n"));
        await _rtsp.SendResponseAsync(request, body: body, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleSetParameterAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        var parameters = ParseParameters(request.Body);
        if (parameters.TryGetValue("wfd_video_formats", out var videoFormats))
            ApplyVideoFormat(videoFormats);
        if (parameters.TryGetValue("wfd_presentation_URL", out var presentationUrl))
        {
            var selected = presentationUrl.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (Uri.TryCreate(selected, UriKind.Absolute, out _))
                _presentationUrl = selected!;
        }

        await _rtsp.SendResponseAsync(
            request,
            headers: SessionHeaders(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (parameters.TryGetValue("wfd_trigger_method", out var trigger))
        {
            switch (trigger.Trim().ToUpperInvariant())
            {
                case "SETUP":
                    _setupTriggered.TrySetResult();
                    break;
                case "TEARDOWN":
                    _sessionEnded.TrySetResult();
                    break;
                case "PAUSE" when _sessionId is not null:
                    await SendControlAsync("PAUSE", cancellationToken).ConfigureAwait(false);
                    break;
                case "PLAY" when _sessionId is not null:
                    await SendControlAsync("PLAY", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task SendControlAsync(string method, CancellationToken cancellationToken)
    {
        var response = await _rtsp.SendRequestAsync(
            method,
            _presentationUrl,
            SessionHeaders(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        response.EnsureSuccess();
    }

    private async Task WatchMediaAsync(CancellationToken cancellationToken)
    {
        var idrRequested = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            var lastFrame = (_renderer as VideoRenderer)?.LastFrameReceivedAt ?? _playStartedAt;
            var silence = DateTimeOffset.UtcNow - lastFrame;
            if (!idrRequested && silence >= TimeSpan.FromSeconds(4))
            {
                idrRequested = true;
                try
                {
                    var response = await _rtsp.SendRequestAsync(
                        "SET_PARAMETER",
                        _presentationUrl,
                        SessionHeaders(),
                        "wfd_idr_request\r\n",
                        TimeSpan.FromSeconds(2),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccess();
                    var rendererDiagnostic = (_renderer as VideoRenderer)?.DescribeNoFrames();
                    _report(
                        "No decoded video frames received; requested a new IDR frame."
                        + (rendererDiagnostic is null ? string.Empty : $" {rendererDiagnostic}"));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _report($"Could not request an IDR frame: {exception.Message}");
                }
            }

            if (silence >= TimeSpan.FromSeconds(12))
            {
                var rendererDiagnostic = (_renderer as VideoRenderer)?.DescribeNoFrames();
                throw new TimeoutException(
                    "No decoded RTP video frames were received for 12 seconds."
                    + (rendererDiagnostic is null ? string.Empty : $" {rendererDiagnostic}"));
            }
        }
    }

    private static Dictionary<string, string> ParseParameters(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
                result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return result;
    }

    private void ApplyVideoFormat(string value)
    {
        var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length > 4
            && uint.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ceaMask)
            && (ceaMask & 0x80) != 0)
        {
            _width = 1920;
            _height = 1080;
        }
        else
        {
            _width = 1280;
            _height = 720;
        }
    }

    private Dictionary<string, string>? SessionHeaders() => _sessionId is null
        ? null
        : new Dictionary<string, string> { ["Session"] = _sessionId };

    private static string ReadSessionId(RtspResponse response)
    {
        if (!response.Headers.TryGetValue("Session", out var session))
            throw new InvalidDataException("The RTSP SETUP response did not contain a Session header.");
        return session.Split(';', 2)[0].Trim();
    }

    private void OnDisconnected(object? sender, Exception? exception) => _sessionEnded.TrySetResult();

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_sessionId is not null)
        {
            using var teardownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await SendControlAsync("TEARDOWN", teardownTimeout.Token).ConfigureAwait(false); }
            catch { }
        }
        _lifetime.Cancel();
        _ports?.Dispose();
        _ports = null;
        if (_rendererStarted)
        {
            await _renderer.StopAsync().ConfigureAwait(false);
            _rendererStarted = false;
        }
        _rtsp.Disconnected -= OnDisconnected;
        await _rtsp.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
