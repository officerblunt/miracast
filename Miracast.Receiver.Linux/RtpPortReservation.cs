using System.Net;
using System.Net.Sockets;

namespace Miracast.Receiver.Linux;

internal sealed class RtpPortReservation : IDisposable
{
    private readonly UdpClient _rtp;
    private readonly UdpClient _rtcp;
    private bool _disposed;

    private RtpPortReservation(int rtpPort, UdpClient rtp, UdpClient rtcp)
    {
        RtpPort = rtpPort;
        _rtp = rtp;
        _rtcp = rtcp;
    }

    public int RtpPort { get; }
    public int RtcpPort => RtpPort + 1;

    public static RtpPortReservation Reserve(int preferredPort = 19000)
    {
        var firstPort = preferredPort % 2 == 0 ? preferredPort : preferredPort + 1;
        for (var port = firstPort; port <= 65000; port += 2)
        {
            UdpClient? rtp = null;
            UdpClient? rtcp = null;
            try
            {
                rtp = Bind(port);
                rtcp = Bind(port + 1);
                return new RtpPortReservation(port, rtp, rtcp);
            }
            catch (SocketException)
            {
                rtp?.Dispose();
                rtcp?.Dispose();
            }
        }

        throw new InvalidOperationException("Could not reserve an RTP/RTCP UDP port pair.");
    }

    private static UdpClient Bind(int port)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        return client;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _rtp.Dispose();
        _rtcp.Dispose();
    }
}
