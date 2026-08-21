using System.Globalization;
using System.Text;

namespace Miracast.Receiver.Linux;

internal abstract record RtspMessage(
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    private const int MaximumBodyLength = 1024 * 1024;

    public int? CSeq => Headers.TryGetValue("CSeq", out var value)
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cseq)
            ? cseq
            : null;

    public static async Task<RtspMessage?> ReadAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        string? startLine;
        do
        {
            startLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (startLine is null)
                return null;
        }
        while (startLine.Length == 0);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("RTSP connection closed while reading headers.");
            if (line.Length == 0)
                break;

            var separator = line.IndexOf(':');
            if (separator <= 0)
                throw new InvalidDataException($"Invalid RTSP header: {line}");
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText)
            && (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength)
                || contentLength < 0
                || contentLength > MaximumBodyLength))
        {
            throw new InvalidDataException($"Invalid RTSP Content-Length: {lengthText}");
        }

        var body = string.Empty;
        if (contentLength > 0)
        {
            var buffer = new char[contentLength];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("RTSP connection closed while reading the message body.");
                offset += read;
            }
            body = new string(buffer);
        }

        if (startLine.StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode))
            {
                throw new InvalidDataException($"Invalid RTSP status line: {startLine}");
            }
            return new RtspResponse(statusCode, parts.Length == 3 ? parts[2] : string.Empty, headers, body);
        }

        var requestParts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length != 3 || !requestParts[2].StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid RTSP request line: {startLine}");
        return new RtspRequest(requestParts[0], requestParts[1], headers, body);
    }

    public static async Task WriteAsync(
        StreamWriter writer,
        string startLine,
        IReadOnlyDictionary<string, string> headers,
        string body,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(startLine.AsMemory(), cancellationToken).ConfigureAwait(false);
        foreach (var header in headers)
            await writer.WriteLineAsync($"{header.Key}: {header.Value}".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
            await writer.WriteAsync(body.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static Dictionary<string, string> PrepareHeaders(
        IReadOnlyDictionary<string, string>? source,
        string body)
    {
        var headers = source is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        if (body.Length > 0)
        {
            headers.TryAdd("Content-Type", "text/parameters");
            headers["Content-Length"] = Encoding.ASCII.GetByteCount(body).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            headers.Remove("Content-Length");
        }
        return headers;
    }
}

internal sealed record RtspRequest(
    string Method,
    string Uri,
    IReadOnlyDictionary<string, string> Headers,
    string Body) : RtspMessage(Headers, Body);

internal sealed record RtspResponse(
    int StatusCode,
    string ReasonPhrase,
    IReadOnlyDictionary<string, string> Headers,
    string Body) : RtspMessage(Headers, Body)
{
    public void EnsureSuccess()
    {
        if (StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"RTSP request failed: {StatusCode} {ReasonPhrase}.");
    }
}
