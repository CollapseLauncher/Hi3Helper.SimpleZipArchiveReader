using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

namespace Hi3Helper.SimpleZipArchiveReader;

/// <summary>
/// A delegate function to create an instance of <see cref="Stream"/> for the <see cref="ZipArchiveReader"/> to read from.
/// </summary>
/// <param name="offset">The offset or position of the <see cref="Stream"/> to read from.</param>
/// <param name="length">Size of data to be read.</param>
/// <param name="token">Cancellation token for asynchronous operations.</param>
/// <returns>An instance of <see cref="Stream"/> for the <see cref="ZipArchiveReader"/> to read from.</returns>
public delegate Task<Stream> StreamFactoryAsync(long? offset, long? length, CancellationToken token);

/// <summary>
/// A delegate function to create an instance of <see cref="Stream"/> for the <see cref="ZipArchiveReader"/> to read from.
/// </summary>
/// <param name="offset">The offset or position of the <see cref="Stream"/> to read from.</param>
/// <param name="length">Size of data to be read.</param>
/// <returns>An instance of <see cref="Stream"/> for the <see cref="ZipArchiveReader"/> to read from.</returns>
public delegate Stream StreamFactory(long? offset, long? length);

/// <summary>
/// A Simple Zip Archive reader with ability to read from remote HTTP(S) source or any Stream factories.<br/>
/// This instance extends <see cref="IReadOnlyCollection{T}"/> and can be enumerated.
/// </summary>
public class ZipArchiveReader : IReadOnlyCollection<ZipArchiveEntry>
{
    #region Properties
    public bool                  IsEmpty        => Entries.Count == 0;
    public List<ZipArchiveEntry> Entries        { get; } = [];
    public string?               ArchiveComment { get; private set; }

    public override string ToString() => $"Count: {Entries.Count} Entries" +
                                         (string.IsNullOrEmpty(ArchiveComment) ? string.Empty : $" | Comment: {ArchiveComment}");

    #endregion

    #region Public Methods

    /// <summary>
    /// Creates a <see cref="ZipArchiveReader"/> from a remote HTTP(S) URL.
    /// </summary>
    /// <param name="url">The URL of the Zip archive.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A parsed Zip Archive including entries to read from.</returns>
    public static Task<ZipArchiveReader> CreateFromRemoteAsync(
        string            url,
        CancellationToken token = default) =>
        CreateFromRemoteAsync(url, null, token);

    /// <summary>
    /// Creates a <see cref="ZipArchiveReader"/> from a remote HTTP(S) URL.
    /// </summary>
    /// <param name="url">The URL of the Zip archive.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A parsed Zip Archive including entries to read from.</returns>
    public static Task<ZipArchiveReader> CreateFromRemoteAsync(
        Uri               url,
        CancellationToken token = default) =>
        CreateFromRemoteAsync(url, null, token);

    /// <summary>
    /// Creates a <see cref="ZipArchiveReader"/> from a remote HTTP(S) URL.
    /// </summary>
    /// <param name="url">The URL of the Zip archive.</param>
    /// <param name="httpClient">Custom HttpClient to be used to gather the archive stream.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A parsed Zip Archive including entries to read from.</returns>
    public static Task<ZipArchiveReader> CreateFromRemoteAsync(
        string            url,
        HttpClient?       httpClient,
        CancellationToken token = default) =>
        CreateFromRemoteAsync(new Uri(url), httpClient, token);

    /// <summary>
    /// Creates a <see cref="ZipArchiveReader"/> from a remote HTTP(S) URL.
    /// </summary>
    /// <param name="url">The URL of the Zip archive.</param>
    /// <param name="httpClient">Custom HttpClient to be used to gather the archive stream.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A parsed Zip Archive including entries to read from.</returns>
    public static async Task<ZipArchiveReader> CreateFromRemoteAsync(
        Uri               url,
        HttpClient?       httpClient,
        CancellationToken token = default)
    {
        bool useOwnHttpClient = httpClient == null;
        httpClient ??= CreateSocketHandlerHttpClient();

        try
        {
            long zipRemoteUrlLength = await httpClient.GetLengthAsync(url, token);
            if (zipRemoteUrlLength == 0)
            {
                throw new NotSupportedException($"The requested URL: {url} doesn't have Content-Length response header or the file content is empty!");
            }

            string? archiveComment;
            long    offsetOfCD;
            long    sizeOfCD;

            long offsetOfEOCD = zipRemoteUrlLength - Constants.EOCDBufferLength;
            offsetOfEOCD = Math.Clamp(offsetOfEOCD, 0, zipRemoteUrlLength);

            await using (Stream bufferStreamOfEOCD =
                         await httpClient.GetStreamFromPosAsync(url,
                                                                offsetOfEOCD,
                                                                null,
                                                                token))
            {
                (offsetOfCD, sizeOfCD, archiveComment) =
                    await FindCentralDirectoryOffsetAndSizeAsync(bufferStreamOfEOCD,
                                                                 Constants.EOCDBufferLength,
                                                                 token);
            }

            if (offsetOfCD == 0)
            {
                throw new InvalidOperationException("Cannot find Central Directory Record offset");
            }

            if (sizeOfCD == 0)
            {
                return new ZipArchiveReader
                {
                    ArchiveComment = archiveComment
                };
            }

            ZipArchiveReader reader =
                await CreateFromCentralDirectoryStreamAsync(CreateStreamFromOffset,
                                                            sizeOfCD,
                                                            offsetOfCD,
                                                            token);

            reader.ArchiveComment = archiveComment;
            return reader;
        }
        finally
        {
            if (useOwnHttpClient)
            {
                httpClient.Dispose();
            }
        }

        static HttpClient CreateSocketHandlerHttpClient()
        {
            HttpClientHandler httpHandler = new()
            {
                // Using HTTP-side compression causing content-length to be unsupported,
                // making us unable to get the exact size of the Zip archive and thus locating
                // the offset of central directory.
                //
                // So in this case, we are disabling it for good measure.
                AutomaticDecompression = DecompressionMethods.None
            };
            return new HttpClient(httpHandler);
        }

        Task<Stream> CreateStreamFromOffset(long? offset, long? length, CancellationToken innerToken)
            => httpClient.GetStreamFromPosAsync(url, offset, length, innerToken);
    }

    /// <summary>
    /// Creates a <see cref="ZipArchiveReader"/> from a Stream factory asynchronously.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A parsed Zip Archive including entries to read from.</returns>
    public static async Task<ZipArchiveReader> CreateFromStreamFactoryAsync(
        StreamFactoryAsync streamFactory,
        CancellationToken  token = default)
    {
        long streamLength = await GetLengthFromStreamFactoryAsync(streamFactory, token);
        if (streamLength <= 0)
        {
            throw new InvalidOperationException("Stream has 0 bytes in size!");
        }

        string? archiveComment;
        long    offsetOfCD;
        long    sizeOfCD;

        long offsetOfEOCD = Math.Clamp(streamLength - Constants.EOCDBufferLength,
                                       0,
                                       streamLength);

        await using (Stream bufferStreamOfEOCD =
                     await streamFactory(offsetOfEOCD, Constants.EOCDBufferLength, token))
        {
            (offsetOfCD, sizeOfCD, archiveComment) =
                await FindCentralDirectoryOffsetAndSizeAsync(bufferStreamOfEOCD,
                                                             Constants.EOCDBufferLength,
                                                             token);
        }

        if (offsetOfCD <= 0)
        {
            throw new InvalidOperationException("Cannot find Central Directory Record offset");
        }

        if (sizeOfCD == 0)
        {
            return new ZipArchiveReader
            {
                ArchiveComment = archiveComment
            };
        }

        ZipArchiveReader reader =
            await CreateFromCentralDirectoryStreamAsync(streamFactory,
                                                        sizeOfCD,
                                                        offsetOfCD,
                                                        token);

        reader.ArchiveComment = archiveComment;
        return reader;
    }

    /// <summary>
    /// Creates a <see cref="ZipArchiveReader"/> from a Stream factory.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <returns>A parsed Zip Archive including entries to read from.</returns>
    public static ZipArchiveReader CreateFromStreamFactory(StreamFactory streamFactory)
    {
        long streamLength = GetLengthFromStreamFactory(streamFactory);
        if (streamLength <= 0)
        {
            throw new InvalidOperationException("Stream has 0 bytes in size!");
        }

        string? archiveComment;
        long offsetOfCD;
        long sizeOfCD;

        long offsetOfEOCD = Math.Clamp(streamLength - Constants.EOCDBufferLength,
                                       0,
                                       streamLength);

        using (Stream bufferStreamOfEOCD = streamFactory(offsetOfEOCD, Constants.EOCDBufferLength))
        {
            scoped Span<byte> stackBuffer = stackalloc byte[Constants.EOCDBufferLength];
            int               read        = bufferStreamOfEOCD.Read(stackBuffer);
            (offsetOfCD, sizeOfCD, archiveComment) = FindCentralDirectoryOffsetAndSize(stackBuffer[..read]);
        }

        if (offsetOfCD <= 0)
        {
            throw new InvalidOperationException("Cannot find Central Directory Record offset");
        }

        if (sizeOfCD == 0)
        {
            return new ZipArchiveReader
            {
                ArchiveComment = archiveComment
            };
        }

        ZipArchiveReader reader =
            CreateFromCentralDirectoryStream(streamFactory,
                                             sizeOfCD,
                                             offsetOfCD);

        reader.ArchiveComment = archiveComment;
        return reader;
    }
    #endregion

    #region Utilities
    private static ZipArchiveReader
        CreateFromCentralDirectoryStream(
            StreamFactory streamFactory,
            long          size,
            long          offset)
    {
        if (size == 0)
        {
            return new ZipArchiveReader();
        }

        bool isUseRentBuffer = size <= 4 << 20;
        byte[] centralDirectoryBuffer = isUseRentBuffer
            ? ArrayPool<byte>.Shared.Rent((int)size)
            : DelegateOverrides.GetHeapArray((int)size);
        Span<byte> centralDirectorySpan =
            centralDirectoryBuffer.AsSpan(0, (int)size);

        try
        {
            using Stream centralDirectoryStream = streamFactory(offset, null);

            int bufferOffset = 0;
            while (size > 0)
            {
                int read = centralDirectoryStream.Read(centralDirectorySpan);
                if (read == 0)
                {
                    throw new IndexOutOfRangeException("Stream has prematurely reached End of Stream while more bytes need to be read");
                }

                bufferOffset += read;
                size         -= (uint)read;
            }

            ZipArchiveReader archive = new();

            ReadOnlySpan<byte> bufferSpan = centralDirectorySpan[..bufferOffset];
            while (!bufferSpan.IsEmpty)
            {
                bufferSpan = ZipArchiveEntry
                   .CreateFromBlockSpan(bufferSpan, out ZipArchiveEntry entry);
                archive.Entries.Add(entry);
            }

            return archive;
        }
        finally
        {
            if (isUseRentBuffer)
            {
                ArrayPool<byte>.Shared.Return(centralDirectoryBuffer);
            }
        }
    }

    private static async Task<ZipArchiveReader>
        CreateFromCentralDirectoryStreamAsync(
        StreamFactoryAsync streamFactory,
        long               size,
        long               offset,
        CancellationToken  token = default)
    {
        if (size == 0)
        {
            return new ZipArchiveReader();
        }

        bool isUseRentBuffer = size <= 4 << 20;
        byte[] centralDirectoryBuffer = isUseRentBuffer
            ? ArrayPool<byte>.Shared.Rent((int)size)
            : DelegateOverrides.GetHeapArray((int)size);

        try
        {
            await using Stream centralDirectoryStream =
                await streamFactory(offset, null, token);

            int bufferOffset = 0;
            while (size > 0)
            {
                int read = await centralDirectoryStream
                   .ReadAsync(centralDirectoryBuffer.AsMemory(bufferOffset, (int)size),
                              token);

                if (read == 0)
                {
                    throw new IndexOutOfRangeException("Stream has prematurely reached End of Stream while more bytes need to be read");
                }

                bufferOffset += read;
                size         -= (uint)read;
            }

            ZipArchiveReader archive = new();

            ReadOnlySpan<byte> bufferSpan = centralDirectoryBuffer.AsSpan(0, bufferOffset);
            while (!bufferSpan.IsEmpty)
            {
                bufferSpan = ZipArchiveEntry
                   .CreateFromBlockSpan(bufferSpan, out ZipArchiveEntry entry);
                archive.Entries.Add(entry);
            }

            return archive;
        }
        finally
        {
            if (isUseRentBuffer)
            {
                ArrayPool<byte>.Shared.Return(centralDirectoryBuffer);
            }
        }
    }

    private static async Task<long> GetLengthFromStreamFactoryAsync(
        StreamFactoryAsync streamFactory,
        CancellationToken token = default)
    {
        await using Stream stream = await streamFactory(0, null, token);
        return stream.Length;
    }

    private static long GetLengthFromStreamFactory(
        StreamFactory streamFactory)
    {
        using Stream stream = streamFactory(0, null);
        return stream.Length;
    }

    private static async ValueTask<(long Offset, long Size, string? ArchiveComment)>
        FindCentralDirectoryOffsetAndSizeAsync(
            Stream            stream,
            int               bufferSize,
            CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            int read = await stream.ReadAsync(buffer, 0, bufferSize, token);
            return FindCentralDirectoryOffsetAndSize(buffer.AsSpan(0, read));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (long Offset, long Size, string? ArchiveComment)
        FindCentralDirectoryOffsetAndSize(ReadOnlySpan<byte> bufferSpan)
    {
        int lastIndexOfMagic32 = bufferSpan.LastIndexOfFromBittable(Constants.Zip32EOCDRHeaderMagic);
        int lastIndexOfMagic64 = bufferSpan.LastIndexOfFromBittable(Constants.Zip64EOCDRHeaderMagic);
        if (lastIndexOfMagic32 < 0 && lastIndexOfMagic64 < 0)
        {
            throw new IndexOutOfRangeException("Cannot find an offset of the Central Directory");
        }

        // First, check if the archive uses Zip64 record for End of Central Directory Record.
        // If so, parse the Zip64 instead.
        return lastIndexOfMagic64 > 0
            ? FindCentralDirectoryOffsetAndSize64(bufferSpan, lastIndexOfMagic32, lastIndexOfMagic64)
            : FindCentralDirectoryOffsetAndSize32(bufferSpan, lastIndexOfMagic32);
    }

    private static (uint Offset, uint Size, string? ArchiveComment)
        FindCentralDirectoryOffsetAndSize32(ReadOnlySpan<byte> buffer, int offset)
    {
        const int MaxEOCDRLen = 22;

        buffer = buffer[offset..];

        uint   sizeCDOnStream   = MemoryMarshal.Read<uint>(buffer[12..]);
        uint   offsetCDOnStream = MemoryMarshal.Read<uint>(buffer[16..]);
        ushort commentLength    = MemoryMarshal.Read<ushort>(buffer[20..]);

        int bufferLenRemained = buffer.Length - MaxEOCDRLen;
        if (bufferLenRemained < commentLength)
        {
            commentLength = (ushort)bufferLenRemained;
        }
        ReadOnlySpan<byte> commentSpan = buffer.Slice(MaxEOCDRLen, commentLength);

        string? archiveComment = !commentSpan.IsEmpty
            ? Encoding.Default.GetString(commentSpan)
            : null;

        return (offsetCDOnStream, sizeCDOnStream, archiveComment);
    }

    private static (long Offset, long Size, string? ArchiveComment)
        FindCentralDirectoryOffsetAndSize64(ReadOnlySpan<byte> buffer, int offset32, int offset64)
    {
        // Try to get the offset from Zip32 record first. Since the size can be dynamic
        // and not always be defined in Zip64 record.
        (uint offsetEOCDR32, uint sizeEOCDR32, string? archiveComment) = FindCentralDirectoryOffsetAndSize32(buffer, offset32);

        // Skip if both offset and size aren't exceeding uint.MaxValue, even though Zip64 End of Central Directory Record exist.
        if (offsetEOCDR32 != Constants.Zip64Mask &&
            sizeEOCDR32 != Constants.Zip64Mask)
        {
            return (offsetEOCDR32, sizeEOCDR32, archiveComment);
        }

        long offsetEOCDR64 = offsetEOCDR32;
        long sizeEOCDR64   = sizeEOCDR32;

        // Then, we try to capture the offset and size from Zip64 End of Central Directory Record.
        ReadOnlySpan<byte> buffer64 = buffer[offset64..];

        if (sizeEOCDR32 == Constants.Zip64Mask)
        {
            sizeEOCDR64 = MemoryMarshal.Read<long>(buffer64[40..]);
        }

        if (offsetEOCDR32 == Constants.Zip64Mask)
        {
            offsetEOCDR64 = MemoryMarshal.Read<long>(buffer64[48..]);
        }

        return (offsetEOCDR64, sizeEOCDR64, archiveComment);
    }

    #endregion

    #region IReadOnlyCollection extensions

    /// <summary>
    /// Gets the <see cref="ZipArchiveEntry"/> entry at specific index.
    /// </summary>
    public ZipArchiveEntry this[int index]
    {
        get => Entries[index];
        set => Entries[index] = value;
    }

    public IEnumerator<ZipArchiveEntry> GetEnumerator() => Entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Gets the total count of available <see cref="ZipArchiveEntry"/> entries
    /// </summary>
    public int Count => Entries.Count;

    #endregion
}
