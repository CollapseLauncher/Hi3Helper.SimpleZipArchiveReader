using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable IdentifierTypo

namespace Hi3Helper.SimpleZipArchiveReader;

internal static class Utilities
{
    extension(HttpClient client)
    {
        public ValueTask<long> GetLengthAsync(Uri               url,
                                              CancellationToken token)
            => DelegateOverrides.GetUrlFileLengthAsync(client, url, token);

        public Task<Stream> GetStreamFromPosAsync(Uri               url,
                                                  long?             offset,
                                                  long?             length,
                                                  CancellationToken token)
            => DelegateOverrides.GetStreamFromPosAsync(client,
                                                       url,
                                                       offset,
                                                       length,
                                                       token);
    }

    extension(Stream stream)
    {
        internal async ValueTask SeekStreamAdvancedToAsync(int               dataLength,
                                                           CancellationToken token)
        {
            byte[]? buffer = dataLength > 4 << 20
                ? null
                : ArrayPool<byte>.Shared.Rent(dataLength);
            Memory<byte> memory = (buffer ?? DelegateOverrides.GetHeapArray(dataLength))
               .AsMemory(0, dataLength);

            try
            {
                int offset = 0;
                int read;
                while ((read = await stream.ReadAsync(memory[offset..], token)
                                           .ConfigureAwait(false)) > 0)
                {
                    offset += read;
                }
            }
            finally
            {
                if (buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        internal void SeekStreamAdvancedTo(int dataLength)
        {
            bool isUseStackalloc = dataLength <= 1 << 10;
            byte[]? buffer = isUseStackalloc || dataLength > 4 << 20
                ? null
                : ArrayPool<byte>.Shared.Rent(dataLength);

            scoped Span<byte> span = isUseStackalloc
                ? stackalloc byte[dataLength]
                : (buffer ?? DelegateOverrides.GetHeapArray(dataLength))
               .AsSpan(0, dataLength);

            try
            {
                int offset = 0;
                int read;
                while ((read = stream.Read(span[offset..])) > 0)
                {
                    offset += read;
                }
            }
            finally
            {
                if (buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }

    public static unsafe int LastIndexOfFromBittable<T>(this ReadOnlySpan<byte> span, T value)
        where T : unmanaged
    {
        void*              valueP    = &value;
        ReadOnlySpan<byte> valueSpan = new(valueP, sizeof(T));

        return span.LastIndexOf(valueSpan);
    }

    public static DateTimeOffset DosTimeToDateTime(this uint dateTime)
    {
        const int validZipDateYearMin = 1980;

        if (dateTime == 0)
        {
            goto ReturnInvalidDateIndicator;
        }

        // DosTime format 32 bits
        // Year: 7 bits, 0 is ValidZipDate_YearMin, unsigned (ValidZipDate_YearMin = 1980)
        // Month: 4 bits
        // Day: 5 bits
        // Hour: 5
        // Minute: 6 bits
        // Second: 5 bits

        // do the bit shift as unsigned because the fields are unsigned, but
        // we can safely convert to int, because they won't be too big
        int year   = (int)(validZipDateYearMin + (dateTime >> 25));
        int month  = (int)((dateTime >> 21) & 0xF);
        int day    = (int)((dateTime >> 16) & 0x1F);
        int hour   = (int)((dateTime >> 11) & 0x1F);
        int minute = (int)((dateTime >> 5) & 0x3F);
        int second = (int)((dateTime & 0x001F) * 2); // only 5 bits for second, so we only have a granularity of 2 sec.

        try
        {
            return new DateTime(year, month, day, hour, minute, second, 0);
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        catch (ArgumentException)
        {
        }

        ReturnInvalidDateIndicator:
        return new DateTime(1980, 1, 1, 0, 0, 0);
    }
}
