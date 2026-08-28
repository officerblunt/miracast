using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Miracast.Receiver.Entities.EventArgs;
using Tmds.DBus;
using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class WfdSessionTests
{
    [Fact]
    public async Task CompletesM1ThroughM7BeforeReportingMediaReady()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var sourceTask = RunFakeWfdSourceAsync(listener, timeout.Token);

        var peer = new WifiP2PPeer(
            new ObjectPath("/test/peer"), "Test Source", "02:00:00:00:00:01", 100, [1]);
        var context = new P2PConnectionContext(
            peer, "p2p-test", IPAddress.Loopback, IPAddress.Loopback, endpoint.Port);
        var renderer = new FakeRenderer();
        var mediaReady = new TaskCompletionSource<VideoSource>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var session = new WfdSession(context, renderer, mediaReady.SetResult, _ => { });
        await session.RunAsync(timeout.Token);
        var negotiatedRtpPort = await sourceTask;

        var source = await mediaReady.Task.WaitAsync(timeout.Token);
        Assert.True(renderer.WasPrepared);
        Assert.Equal(1920, source.Width);
        Assert.Equal(1080, source.Height);
        Assert.Equal(IPAddress.Loopback.ToString(), source.StreamUri.Host);
        Assert.Equal(negotiatedRtpPort, source.StreamUri.Port);
        Assert.True(renderer.WasStopped);
        listener.Stop();
    }

    private static async Task<int> RunFakeWfdSourceAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var tcp = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.ASCII, 4096, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = false,
        };

        await SendRequestAsync(writer, "OPTIONS", "*", 1, string.Empty, cancellationToken);
        Assert.Equal(200, (await ReadResponseAsync(reader, cancellationToken)).StatusCode);

        var reciprocalOptions = await ReadRequestAsync(reader, cancellationToken);
        Assert.Equal("OPTIONS", reciprocalOptions.Method);
        await SendResponseAsync(writer, reciprocalOptions.CSeq!.Value, null, null, cancellationToken);

        var m3Body = "wfd_content_protection\r\nwfd_video_formats\r\nwfd_audio_codecs\r\n"
            + "wfd_client_rtp_ports\r\nwfd_uibc_capability\r\n";
        await SendRequestAsync(writer, "GET_PARAMETER", "rtsp://localhost/wfd1.0", 2, m3Body, cancellationToken);
        var m3Response = await ReadResponseAsync(reader, cancellationToken);
        var portMatch = Regex.Match(m3Response.Body, @"RTP/AVP/UDP;unicast\s+(\d+)");
        Assert.True(portMatch.Success);
        var rtpPort = int.Parse(portMatch.Groups[1].Value);
        Assert.Contains("wfd_content_protection: none", m3Response.Body);

        var m4Body = "wfd_content_protection: none\r\n"
            + "wfd_video_formats: 00 00 02 10 00000080 00000000 00000000 00 0000 0000 00 none none\r\n"
            + "wfd_audio_codecs: LPCM 00000002 00\r\n"
            + "wfd_presentation_URL: rtsp://127.0.0.1/wfd1.0/streamid=0 none\r\n";
        await SendRequestAsync(writer, "SET_PARAMETER", "rtsp://localhost/wfd1.0", 3, m4Body, cancellationToken);
        Assert.Equal(200, (await ReadResponseAsync(reader, cancellationToken)).StatusCode);

        await SendRequestAsync(writer, "SET_PARAMETER", "rtsp://localhost/wfd1.0", 4,
            "wfd_trigger_method: SETUP\r\n", cancellationToken);
        Assert.Equal(200, (await ReadResponseAsync(reader, cancellationToken)).StatusCode);

        var setup = await ReadRequestAsync(reader, cancellationToken);
        Assert.Equal("SETUP", setup.Method);
        Assert.Contains($"client_port={rtpPort}-{rtpPort + 1}", setup.Headers["Transport"]);
        await SendResponseAsync(writer, setup.CSeq!.Value,
            new Dictionary<string, string>
            {
                ["Session"] = "12345678;timeout=30",
                ["Transport"] = $"RTP/AVP/UDP;unicast;client_port={rtpPort}-{rtpPort + 1};server_port=5000-5001",
            }, null, cancellationToken);

        var play = await ReadRequestAsync(reader, cancellationToken);
        Assert.Equal("PLAY", play.Method);
        Assert.Equal("12345678", play.Headers["Session"]);
        await SendResponseAsync(writer, play.CSeq!.Value,
            new Dictionary<string, string> { ["Session"] = "12345678" }, null, cancellationToken);

        await SendRequestAsync(writer, "TEARDOWN", "rtsp://localhost/wfd1.0", 5, string.Empty,
            cancellationToken, new Dictionary<string, string> { ["Session"] = "12345678" });
        Assert.Equal(200, (await ReadResponseAsync(reader, cancellationToken)).StatusCode);
        return rtpPort;
    }

    private static async Task<RtspRequest> ReadRequestAsync(StreamReader reader, CancellationToken cancellationToken) =>
        Assert.IsType<RtspRequest>(await RtspMessage.ReadAsync(reader, cancellationToken));

    private static async Task<RtspResponse> ReadResponseAsync(StreamReader reader, CancellationToken cancellationToken) =>
        Assert.IsType<RtspResponse>(await RtspMessage.ReadAsync(reader, cancellationToken));

    private static Task SendRequestAsync(
        StreamWriter writer,
        string method,
        string uri,
        int cseq,
        string body,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var headers = RtspMessage.PrepareHeaders(extraHeaders, body);
        headers["CSeq"] = cseq.ToString();
        return RtspMessage.WriteAsync(writer, $"{method} {uri} RTSP/1.0", headers, body, cancellationToken);
    }

    private static Task SendResponseAsync(
        StreamWriter writer,
        int cseq,
        IReadOnlyDictionary<string, string>? extraHeaders,
        string? body,
        CancellationToken cancellationToken)
    {
        body ??= string.Empty;
        var headers = RtspMessage.PrepareHeaders(extraHeaders, body);
        headers["CSeq"] = cseq.ToString();
        return RtspMessage.WriteAsync(writer, "RTSP/1.0 200 OK", headers, body, cancellationToken);
    }

    private sealed class FakeRenderer : IVideoRenderer
    {
        public bool WasPrepared { get; private set; }
        public bool WasStopped { get; private set; }
        public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived
        {
            add { }
            remove { }
        }

        public Task PlayAsync(IVideoSource source, CancellationToken cancellationToken = default)
        {
            WasPrepared = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            WasStopped = true;
            return Task.CompletedTask;
        }
    }
}
