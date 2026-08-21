using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class RtspDuplexClientTests
{
    [Fact]
    public async Task MatchesResponseWhileSourceRequestIsPending()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;

        await using var client = new RtspDuplexClient();
        var sourceTask = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, 4096, leaveOpen: true)
            {
                NewLine = "\r\n",
            };

            var sinkRequest = Assert.IsType<RtspRequest>(await RtspMessage.ReadAsync(reader, timeout.Token));
            var sourceHeaders = RtspMessage.PrepareHeaders(null, string.Empty);
            sourceHeaders["CSeq"] = "77";
            await RtspMessage.WriteAsync(
                writer,
                "GET_PARAMETER rtsp://localhost/wfd1.0 RTSP/1.0",
                sourceHeaders,
                string.Empty,
                timeout.Token);

            var responseHeaders = RtspMessage.PrepareHeaders(null, string.Empty);
            responseHeaders["CSeq"] = sinkRequest.CSeq!.Value.ToString();
            await RtspMessage.WriteAsync(writer, "RTSP/1.0 200 OK", responseHeaders, string.Empty, timeout.Token);

            var sinkResponse = Assert.IsType<RtspResponse>(await RtspMessage.ReadAsync(reader, timeout.Token));
            Assert.Equal(77, sinkResponse.CSeq);
        }, timeout.Token);

        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, timeout.Token);
        var outgoing = client.SendRequestAsync("OPTIONS", "*", cancellationToken: timeout.Token);
        await using var requests = client.ReadRequestsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await requests.MoveNextAsync());
        await client.SendResponseAsync(requests.Current, cancellationToken: timeout.Token);
        Assert.Equal(200, (await outgoing).StatusCode);
        await sourceTask;
        listener.Stop();
    }
}
