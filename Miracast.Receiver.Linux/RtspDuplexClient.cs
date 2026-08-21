using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Miracast.Receiver.Linux;

internal sealed class RtspDuplexClient : IAsyncDisposable
{
    private readonly TcpClient _tcpClient = new(AddressFamily.InterNetwork);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<RtspResponse>> _pending = new();
    private readonly Channel<RtspRequest> _requests = Channel.CreateUnbounded<RtspRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _lifetime = new();
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _readLoop;
    private int _nextCSeq;
    private bool _disposed;

    public event EventHandler<Exception?>? Disconnected;

    public async Task ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _tcpClient.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
        var stream = _tcpClient.GetStream();
        _reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(stream, Encoding.ASCII, 4096, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = false,
        };
        _readLoop = ReadLoopAsync(_lifetime.Token);
    }

    public IAsyncEnumerable<RtspRequest> ReadRequestsAsync(CancellationToken cancellationToken) =>
        _requests.Reader.ReadAllAsync(cancellationToken);

    public async Task<RtspResponse> SendRequestAsync(
        string method,
        string uri,
        IReadOnlyDictionary<string, string>? headers = null,
        string body = "",
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var cseq = Interlocked.Increment(ref _nextCSeq);
        var preparedHeaders = RtspMessage.PrepareHeaders(headers, body);
        preparedHeaders["CSeq"] = cseq.ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<RtspResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(cseq, completion))
            throw new InvalidOperationException($"Duplicate RTSP CSeq {cseq}.");

        try
        {
            await WriteAsync($"{method} {uri} RTSP/1.0", preparedHeaders, body, cancellationToken)
                .ConfigureAwait(false);
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
            return await completion.Task.WaitAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(cseq, out _);
        }
    }

    public Task SendResponseAsync(
        RtspRequest request,
        int statusCode = 200,
        string reasonPhrase = "OK",
        IReadOnlyDictionary<string, string>? headers = null,
        string body = "",
        CancellationToken cancellationToken = default)
    {
        if (request.CSeq is null)
            throw new InvalidDataException("The RTSP request does not contain CSeq.");
        var preparedHeaders = RtspMessage.PrepareHeaders(headers, body);
        preparedHeaders["CSeq"] = request.CSeq.Value.ToString(CultureInfo.InvariantCulture);
        return WriteAsync($"RTSP/1.0 {statusCode} {reasonPhrase}", preparedHeaders, body, cancellationToken);
    }

    private async Task WriteAsync(
        string startLine,
        IReadOnlyDictionary<string, string> headers,
        string body,
        CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new InvalidOperationException("RTSP client is not connected.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RtspMessage.WriteAsync(writer, startLine, headers, body, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            var reader = _reader ?? throw new InvalidOperationException("RTSP client is not connected.");
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await RtspMessage.ReadAsync(reader, cancellationToken).ConfigureAwait(false);
                if (message is null)
                    break;

                if (message is RtspResponse response)
                {
                    if (response.CSeq is { } cseq && _pending.TryGetValue(cseq, out var completion))
                        completion.TrySetResult(response);
                }
                else if (message is RtspRequest request)
                {
                    await _requests.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            var terminal = failure ?? new EndOfStreamException("The RTSP source closed the connection.");
            foreach (var completion in _pending.Values)
                completion.TrySetException(terminal);
            _requests.Writer.TryComplete(failure);
            Disconnected?.Invoke(this, failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _tcpClient.Dispose();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _reader?.Dispose();
        _writer?.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }
}
