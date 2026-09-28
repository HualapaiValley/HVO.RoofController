using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>One part of a <c>multipart/x-mixed-replace</c> MJPEG stream: its headers and its body.</summary>
internal sealed record MjpegPart(IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// Reads the parts of an MJPEG stream strictly, as the tests need: each part is the boundary line, headers, a blank
/// line, exactly <c>Content-Length</c> bytes and a CRLF. Anything else fails the read.
/// </summary>
internal sealed class MjpegReader(Stream stream, string boundary)
{
    private readonly byte[] _one = new byte[1];

    /// <summary>The next part, or null when the stream ends between parts.</summary>
    public async Task<MjpegPart?> ReadPartAsync(CancellationToken cancellationToken)
    {
        var delimiter = await ReadLineAsync(cancellationToken);
        if (delimiter is null)
        {
            return null;
        }

        if (delimiter != "--" + boundary)
        {
            throw new InvalidDataException($"Expected the boundary line, got \"{delimiter}\".");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await ReadLineAsync(cancellationToken) is { Length: > 0 } line)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var body = new byte[int.Parse(headers["Content-Length"], NumberStyles.None, CultureInfo.InvariantCulture)];
        await stream.ReadExactlyAsync(body, cancellationToken);
        if (await ReadLineAsync(cancellationToken) != string.Empty)
        {
            throw new InvalidDataException("The part's body did not end with CRLF.");
        }

        return new MjpegPart(headers, body);
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (await stream.ReadAsync(_one, cancellationToken) == 0)
            {
                return line.Length == 0 ? null : throw new EndOfStreamException("The stream ended inside a line.");
            }

            if (_one[0] == '\n')
            {
                return line.ToString().TrimEnd('\r');
            }

            line.Append((char)_one[0]);
        }
    }
}
