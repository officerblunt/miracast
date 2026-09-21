using System.Globalization;

namespace Miracast.Receiver.Linux;

internal sealed class WfdSession : IAsyncDisposable
{
    private const string DefaultRtspUri = "rtsp://localhost/wfd1.0";
    private readonly P2PConnectionContext _connection;
    private readonly IVideoRenderer _renderer;
    private readonly Action<VideoSource> _mediaReady;
    private readonly Action<string> _report;
    private readonly WfdDisplayCapabilities _displayCapabilities;
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
    private int _disposed;

    public WfdSession(
        P2PConnectionContext connection,
        IVideoRenderer renderer,
        Action<VideoSource> mediaReady,
        Action<string> report,
        WfdDisplayCapabilities? displayCapabilities = null)
    {
        _connection = connection;
        _renderer = renderer;
        _mediaReady = mediaReady;
        _report = report;
        _displayCapabilities = displayCapabilities ?? new WfdDisplayCapabilities(1920, 1080);
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
            _report(
                $"Advertising a {_displayCapabilities.Width}x{_displayCapabilities.Height} "
                + "Miracast display mode for the monitor wall; "
                + $"native output mode is {_displayCapabilities.NativeWidth}x"
                + $"{_displayCapabilities.NativeHeight}.");
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
            if (_renderer is VideoRenderer { AudioPlaybackEnabled: false })
            {
                _report(
                    "GStreamer LPCM decoder is unavailable; continuing with video only. "
                    + "Install the GStreamer ugly plugins to enable Miracast audio.");
            }

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
        _report($"WFD Source requested M3 capabilities: {string.Join(", ", requested)}.");
        var requestsWfd2Video = requested.Contains("wfd2_video_formats", StringComparer.OrdinalIgnoreCase);
        var requestsWfd2Audio = requested.Contains("wfd2_audio_codecs", StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wfd_video_formats"] = _displayCapabilities.LegacyVideoFormats,
            ["wfd2_video_formats"] = _displayCapabilities.Wfd2VideoFormats,
            ["wfdx_video_formats"] = _displayCapabilities.ExtendedVideoFormats,
            ["microsoft_video_formats"] = _displayCapabilities.MicrosoftVideoFormats,
            ["microsoft_custom_video_formats"] = _displayCapabilities.MicrosoftCustomVideoFormats,
            ["wfd_audio_codecs"] = "LPCM 00000002 00",
            ["wfd2_audio_codecs"] = "LPCM 00000002 00",
            ["wfd_client_rtp_ports"] = $"RTP/AVP/UDP;unicast {_ports.RtpPort} 0 mode=play",
            ["wfd_content_protection"] = "none",
            ["wfd_display_edid"] = _displayCapabilities.DisplayEdid,
            ["wfd_uibc_capability"] = "none",
            ["wfd_connector_type"] = "05",
            ["wfd_standby_resume_capability"] = "none",
        };

        // WFD 2.1 requires the R1 video/audio parameters to be omitted when
        // their WFD2 counterparts were requested in the same M3 message.
        var responseParameters = requested.Where(name =>
            !(requestsWfd2Video && name.Equals("wfd_video_formats", StringComparison.OrdinalIgnoreCase))
            && !(requestsWfd2Audio && name.Equals("wfd_audio_codecs", StringComparison.OrdinalIgnoreCase)));
        var body = string.Join(string.Empty, responseParameters.Select(name =>
            values.TryGetValue(name, out var value) ? $"{name}: {value}\r\n" : $"{name}: none\r\n"));
        _report(
            $"WFD M3 display response: native={_displayCapabilities.NativeWidth}x"
            + $"{_displayCapabilities.NativeHeight}, wall={_displayCapabilities.Width}x"
            + $"{_displayCapabilities.Height}, microsoft_video_formats="
            + $"{_displayCapabilities.MicrosoftVideoFormats}, custom="
            + $"{(requested.Contains("microsoft_custom_video_formats", StringComparer.OrdinalIgnoreCase) ? _displayCapabilities.MicrosoftCustomVideoFormats : "not requested")}, "
            + $"WFD2={(requestsWfd2Video ? _displayCapabilities.Wfd2VideoFormats : "not requested")}, "
            + $"EDID={(_displayCapabilities.HasEdid && requested.Contains("wfd_display_edid", StringComparer.OrdinalIgnoreCase) ? "sent" : "not requested")}.");
        await _rtsp.SendResponseAsync(request, body: body, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleSetParameterAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        var parameters = ParseParameters(request.Body);
        if (parameters.TryGetValue("wfd_video_formats", out var videoFormats))
            ApplyVideoFormat(videoFormats);
        if (parameters.TryGetValue("wfd2_video_formats", out var wfd2VideoFormats))
            ApplyWfd2VideoFormat(wfd2VideoFormats);
        if (parameters.TryGetValue("wfdx_video_formats", out var extendedVideoFormats))
            ApplyVideoFormat(extendedVideoFormats);
        if (parameters.TryGetValue("wfd_preferred_display_mode", out var preferredDisplayMode))
            ApplyPreferredDisplayMode(preferredDisplayMode);
        if (parameters.TryGetValue("microsoft_custom_video_formats", out var customVideoFormats))
            ApplyCustomVideoFormat(customVideoFormats);
        if (parameters.TryGetValue("microsoft_video_formats", out var microsoftVideoFormats))
            ApplyMicrosoftVideoFormat(microsoftVideoFormats);
        if (parameters.ContainsKey("wfd_video_formats")
            || parameters.ContainsKey("wfd2_video_formats")
            || parameters.ContainsKey("wfdx_video_formats")
            || parameters.ContainsKey("wfd_preferred_display_mode")
            || parameters.ContainsKey("microsoft_custom_video_formats")
            || parameters.ContainsKey("microsoft_video_formats"))
        {
            _report(
                $"WFD Source selected {_width}x{_height}; parameters: "
                + $"{string.Join(", ", parameters.Where(parameter =>
                    parameter.Key.Equals("wfd_video_formats", StringComparison.OrdinalIgnoreCase)
                    || parameter.Key.Equals("wfd2_video_formats", StringComparison.OrdinalIgnoreCase)
                    || parameter.Key.Equals("wfdx_video_formats", StringComparison.OrdinalIgnoreCase)
                    || parameter.Key.Equals("wfd_preferred_display_mode", StringComparison.OrdinalIgnoreCase)
                    || parameter.Key.Equals("microsoft_custom_video_formats", StringComparison.OrdinalIgnoreCase)
                    || parameter.Key.Equals("microsoft_video_formats", StringComparison.OrdinalIgnoreCase))
                    .Select(parameter => $"{parameter.Key}={parameter.Value}"))}.");
        }
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
        if (fields.Length <= 6)
            return;

        if (!ulong.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ceaMask)
            || !ulong.TryParse(fields[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vesaMask)
            || !ulong.TryParse(fields[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handheldMask))
        {
            return;
        }

        var selected = SelectStandardVideoFormat(ceaMask, vesaMask, handheldMask);
        if (selected is { } format && _displayCapabilities.CanDecode(format.Width, format.Height))
            (_width, _height) = format;
    }

    private void ApplyWfd2VideoFormat(string value)
    {
        // An M4 request contains exactly one codec/profile tuple. Ignore a
        // trailing portrait-mode marker and defensively accept only H.264,
        // which is the codec currently handled by the renderer.
        var fields = value.Split(',', 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 12
            || !byte.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codec)
            || (codec & 0x01) == 0
            || !ulong.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ceaMask)
            || !ulong.TryParse(fields[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vesaMask)
            || !ulong.TryParse(fields[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handheldMask))
        {
            return;
        }

        var selected = SelectWfd2VideoFormat(ceaMask, vesaMask, handheldMask);
        if (selected is { } format && _displayCapabilities.CanDecode(format.Width, format.Height))
            (_width, _height) = format;
    }

    private void ApplyPreferredDisplayMode(string value)
    {
        var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length > 5
            && int.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(fields[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var height)
            && _displayCapabilities.CanDecode(width, height))
        {
            _width = width;
            _height = height;
        }
    }

    private void ApplyMicrosoftVideoFormat(string value)
    {
        if (!ulong.TryParse(value.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask))
            return;

        var selected = SelectMicrosoftVideoFormat(mask);
        if (selected is { } format && _displayCapabilities.CanDecode(format.Width, format.Height))
            (_width, _height) = format;
    }

    private void ApplyCustomVideoFormat(string value)
    {
        var selected = value.Split(',', 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (selected.Length >= 2
            && int.TryParse(selected[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(selected[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var height)
            && _displayCapabilities.Contains(width, height))
        {
            _width = width;
            _height = height;
        }
    }

    private static (int Width, int Height)? SelectStandardVideoFormat(
        ulong ceaMask,
        ulong vesaMask,
        ulong handheldMask)
    {
        (int Width, int Height)? selected = null;
        long selectedArea = -1;

        Consider(ceaMask, 0, 640, 480);
        Consider(ceaMask, 1, 720, 480);
        Consider(ceaMask, 5, 1280, 720);
        Consider(ceaMask, 6, 1280, 720);
        Consider(ceaMask, 7, 1920, 1080);
        Consider(ceaMask, 8, 1920, 1080);
        Consider(ceaMask, 17, 3840, 2160);
        Consider(ceaMask, 18, 3840, 2160);
        Consider(ceaMask, 19, 4096, 2160);
        Consider(ceaMask, 20, 4096, 2160);

        Consider(vesaMask, 0, 800, 600);
        Consider(vesaMask, 2, 1024, 768);
        Consider(vesaMask, 4, 1152, 864);
        Consider(vesaMask, 6, 1280, 768);
        Consider(vesaMask, 8, 1280, 800);
        Consider(vesaMask, 10, 1360, 768);
        Consider(vesaMask, 12, 1366, 768);
        Consider(vesaMask, 14, 1280, 1024);
        Consider(vesaMask, 16, 1400, 1050);
        Consider(vesaMask, 18, 1440, 900);
        Consider(vesaMask, 20, 1600, 900);
        Consider(vesaMask, 22, 1600, 1200);
        Consider(vesaMask, 24, 1680, 1024);
        Consider(vesaMask, 26, 1680, 1050);
        Consider(vesaMask, 28, 1920, 1200);
        Consider(vesaMask, 29, 2560, 1440);
        Consider(vesaMask, 30, 2560, 1440);
        Consider(vesaMask, 31, 2560, 1600);
        Consider(vesaMask, 32, 2560, 1600);

        Consider(handheldMask, 0, 800, 480);
        Consider(handheldMask, 2, 854, 480);
        Consider(handheldMask, 6, 640, 360);
        Consider(handheldMask, 8, 960, 540);

        return selected;

        void Consider(ulong mask, int bit, int width, int height)
        {
            if ((mask & (1UL << bit)) == 0)
                return;
            var area = (long)width * height;
            if (area <= selectedArea)
                return;
            selected = (width, height);
            selectedArea = area;
        }
    }

    private static (int Width, int Height)? SelectMicrosoftVideoFormat(ulong mask)
    {
        (int Width, int Height)? selected = null;
        long selectedArea = -1;

        Consider(0, 1920, 1280);
        Consider(1, 1920, 1280);
        Consider(2, 1920, 1280);
        Consider(3, 2160, 1440);
        Consider(4, 2160, 1440);
        Consider(5, 2160, 1440);
        Consider(6, 2256, 1504);
        Consider(7, 2256, 1504);
        Consider(8, 2256, 1504);
        Consider(9, 2736, 1824);
        Consider(10, 2736, 1824);
        Consider(11, 2736, 1824);
        Consider(12, 3000, 2000);
        Consider(13, 3000, 2000);
        Consider(14, 3000, 2000);
        Consider(15, 3240, 2160);
        Consider(16, 3240, 2160);
        Consider(17, 3240, 2160);
        Consider(18, 4500, 3000);
        Consider(19, 4500, 3000);
        Consider(20, 4500, 3000);
        return selected;

        void Consider(int bit, int width, int height)
        {
            if ((mask & (1UL << bit)) == 0)
                return;
            var area = (long)width * height;
            if (area <= selectedArea)
                return;
            selected = (width, height);
            selectedArea = area;
        }
    }

    private static (int Width, int Height)? SelectWfd2VideoFormat(
        ulong ceaMask,
        ulong vesaMask,
        ulong handheldMask)
    {
        (int Width, int Height)? selected = null;
        long selectedArea = -1;

        Consider(ceaMask, 0, 640, 480);
        ConsiderRange(ceaMask, 1, 4, 720, 576);
        ConsiderRange(ceaMask, 5, 6, 1280, 720);
        ConsiderRange(ceaMask, 7, 9, 1920, 1080);
        ConsiderRange(ceaMask, 10, 11, 1280, 720);
        ConsiderRange(ceaMask, 12, 14, 1920, 1080);
        Consider(ceaMask, 15, 1280, 720);
        Consider(ceaMask, 16, 1920, 1080);
        ConsiderRange(ceaMask, 17, 21, 3840, 2160);
        ConsiderRange(ceaMask, 22, 26, 4096, 2160);

        (int Width, int Height)[] vesaModes =
        [
            (800, 600), (1024, 768), (1152, 864), (1280, 768),
            (1280, 800), (1360, 768), (1366, 768), (1280, 1024),
            (1400, 1050), (1440, 900), (1600, 900), (1600, 1200),
            (1680, 1024), (1680, 1050), (1920, 1200), (2560, 1440),
            (2560, 1600),
        ];
        for (var index = 0; index < vesaModes.Length; index++)
        {
            var mode = vesaModes[index];
            ConsiderRange(vesaMask, index * 2, index * 2 + 1, mode.Width, mode.Height);
        }

        (int Width, int Height)[] handheldModes =
        [
            (800, 480), (854, 480), (864, 480),
            (640, 360), (960, 540), (848, 480),
        ];
        for (var index = 0; index < handheldModes.Length; index++)
        {
            var mode = handheldModes[index];
            ConsiderRange(handheldMask, index * 2, index * 2 + 1, mode.Width, mode.Height);
        }

        return selected;

        void ConsiderRange(ulong mask, int firstBit, int lastBit, int width, int height)
        {
            for (var bit = firstBit; bit <= lastBit; bit++)
                Consider(mask, bit, width, height);
        }

        void Consider(ulong mask, int bit, int width, int height)
        {
            if ((mask & (1UL << bit)) == 0)
                return;
            var area = (long)width * height;
            if (area <= selectedArea)
                return;
            selected = (width, height);
            selectedArea = area;
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
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
