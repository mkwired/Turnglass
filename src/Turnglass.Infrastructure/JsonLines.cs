using System.Runtime.CompilerServices;
using System.Text;

namespace Turnglass.Infrastructure;

internal sealed record JsonLine(string Text, long EndOffset);

internal static class JsonLines
{
    internal static async IAsyncEnumerable<JsonLine> ReadAsync(FileStream stream, long offset, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        stream.Position = offset;
        var buffer = new byte[65536];
        using var line = new MemoryStream();
        var position = offset;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var start = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                line.Write(buffer, start, index - start);
                var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, checked((int)line.Length)).TrimEnd('\r');
                if (position == 0 && text.Length > 0)
                {
                    text = text.TrimStart('\uFEFF');
                }

                yield return new JsonLine(text, position + index + 1);
                line.SetLength(0);
                start = index + 1;
            }

            line.Write(buffer, start, read - start);
            position += read;
        }
    }
}
