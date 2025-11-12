using System.Runtime.CompilerServices;
using System.Text;

namespace Overdrive.HttpProtocol;

internal static unsafe class HeaderParser
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FindCrlfCrlf(byte* buf, int head, int tail)
    {
        // Construct a Span<byte> view over the raw memory.
        // The caller must guarantee that (tail - head) bytes are valid and readable.
        var span = new ReadOnlySpan<byte>(buf + head, tail - head);

        int idx = span.IndexOf(CrlfCrlf);
        return idx >= 0 ? head + idx : -1;
    }

    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<byte> FindCrlfCrlf(byte* buf, int head, int tail, ref int idx)
    {
        // Construct a Span<byte> view over the raw memory.
        // The caller must guarantee that (tail - head) bytes are valid and readable.
        var span = new ReadOnlySpan<byte>(buf + head, tail - head);

        idx = span.IndexOf(CrlfCrlf);
        if (idx >= 0)
            idx += head;
        else
            idx = -1;

        return span;
    }
    
    internal static H1HeaderData ExtractH1HeaderData(ReadOnlySpan<byte> headerSpan)
    {
        var headerData = new H1HeaderData();
        
        var lineEnd = headerSpan.IndexOf(Crlf);
        var firstHeader = headerSpan[..lineEnd];

        var firstSpace = firstHeader.IndexOf(Space);
        if (firstSpace == -1)
            throw new InvalidOperationException("Invalid request line");

        if (CachedH1Data.CachedHttpMethods.TryGetOrAdd(firstHeader[..firstSpace], out var httpMethod))
        {
            headerData.HttpMethod = httpMethod;
        }
        
        var secondSpaceRelative = firstHeader[(firstSpace + 1)..].IndexOf(Space);
        if (secondSpaceRelative == -1)
            throw new InvalidOperationException("Invalid request line");

        var secondSpace = firstSpace + secondSpaceRelative + 1;
        
        // REQUEST-TARGET slice: may include path + query (e.g., "/foo?bar=baz")
        var url = firstHeader[(firstSpace + 1)..secondSpace];

        var queryParamSeparator = url.IndexOf(Question);

        if (queryParamSeparator == -1)
        {
            if (CachedH1Data.CachedHttpMethods.TryGetOrAdd(url, out var route))
            {
                headerData.Route = route;
            }
        }
        else
        {
            if (CachedH1Data.CachedHttpMethods.TryGetOrAdd(url[..queryParamSeparator], out var route))
            {
                headerData.Route = route;
            }

            var querySpan = url[(queryParamSeparator + 1)..];
            var current = 0;


            while (current < querySpan.Length)
            {
                var separator = querySpan[current..].IndexOf(QuerySeparator); // (byte)'&'
                ReadOnlySpan<byte> pair;

                if (separator == -1)
                {
                    pair = querySpan[current..];
                    current = querySpan.Length;
                }
                else
                {
                    pair = querySpan.Slice(current, separator);
                    current += separator + 1;
                }

                var equalsIndex = pair.IndexOf(Equal);
                if (equalsIndex == -1)
                    break;

                headerData.QueryParameters!.TryAdd(CachedH1Data.CachedQueryKeys.GetOrAdd(pair[..equalsIndex]),
                    Encoding.UTF8.GetString(pair[(equalsIndex + 1)..]));
            }
            
            // Parse remaining headers
            
            var lineStart = 0;
            while (true)
            {
                lineStart += lineEnd + 2;

                lineEnd = headerSpan[lineStart..].IndexOf("\r\n"u8);
                if (lineEnd == 0)
                {
                    // All Headers read
                    break;
                }

                var header = headerSpan.Slice(lineStart, lineEnd);
                var colonIndex = header.IndexOf(Colon);

                if (colonIndex == -1)
                {
                    // Malformed header
                    continue;
                }

                var headerKey = header[..colonIndex];
                var headerValue = header[(colonIndex + 2)..];

                headerData.Headers!.TryAdd(CachedH1Data.CachedHeaderKeys.GetOrAdd(headerKey),
                    CachedH1Data.CachedHeaderValues.GetOrAdd(headerValue));
            }
        }
        
        return headerData;
    }

    // ===== Common tokens (kept as ReadOnlySpan<byte> for zero-allocation literals) =====

    private static ReadOnlySpan<byte> Crlf => "\r\n"u8;
    private static ReadOnlySpan<byte> CrlfCrlf => "\r\n\r\n"u8;

    // ASCII byte codes (documented for clarity)
    private const byte Space = 0x20;        // ' '
    private const byte Question = 0x3F;     // '?'
    private const byte QuerySeparator = 0x26; // '&'
    private const byte Equal = 0x3D;        // '='
    private const byte Colon = 0x3A;        // ':'
    private const byte SemiColon = 0x3B;    // ';'
}