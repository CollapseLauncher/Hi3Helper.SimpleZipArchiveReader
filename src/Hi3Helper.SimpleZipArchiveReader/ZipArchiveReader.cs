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
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="IndexOutOfRangeException"/>
    /// <exception cref="NotSupportedException"/>
    public static Task<ZipArchiveReader> CreateFromAsync(
        string            url,
        CancellationToken token = default) =>
        CreateFromAsync(url, null, token);

    /// <summary>
    /// Creates a <see cref="ZipArchiveReader"/> from a remote HTTP(S) URL.
    /// </summary>
    /// <param name="url">The URL of the Zip archive.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A parsed Zip Archive including entries to read from.</returns>
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="IndexOutOfRangeException"/>
    /// <exception cref="NotSupportedException"/>
    public static Task<ZipArchiveReader> CreateFromAsync(
        Uri               url,
        CancellationToken token = default) =>
        CreateFromAsync(url, null, token);

    /// <summary>
    /// Creates a <see cref="ZipArchiveReader"/> from a remote HTTP(S) URL.
    /// </summary>
    /// <param name="url">The URL of the Zip archive.</param>
    /// <param name="httpClient">Custom HttpClient to be used to gather the archive stream.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A parsed Zip Archive including entries to read from.</returns>
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="IndexOutOfRangeException"/>
    /// <exception cref="NotSupportedException"/>
    public static Task<ZipArchiveReader> CreateFromAsync(
        string            url,
        HttpClient?       httpClient,
        CancellationToken token = default) =>
        CreateFromAsync(new Uri(url), httpClient, token);

    /// <summary>
    /// Creates a <see cref="ZipArchiveReader"/> from a remote HTTP(S) URL.
    /// </summary>
    /// <param name="url">The URL of the Zip archive.</param>
    /// <param name="httpClient">Custom HttpClient to be used to gather the archive stream.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A parsed Zip Archive including entries to read from.</returns>
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="IndexOutOfRangeException"/>
    /// <exception cref="NotSupportedException"/>
    public static async Task<ZipArchiveReader> CreateFromAsync(
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
                await CreateFromCentralDirectoryStreamFactoryAsync(CreateStreamFromOffset,
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
#if NETCOREAPP2_1_OR_GREATER
            SocketsHttpHandler httpHandler = new()
#else
            HttpClientHandler httpHandler = new()
#endif
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
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="IndexOutOfRangeException"/>
    public static async Task<ZipArchiveReader> CreateFromAsync(
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
            await CreateFromCentralDirectoryStreamFactoryAsync(streamFactory,
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
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="IndexOutOfRangeException"/>
    public static ZipArchiveReader CreateFrom(StreamFactory streamFactory)
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

            int read;
            int offset = 0;
            while ((read = bufferStreamOfEOCD.Read(stackBuffer[offset..])) > 0)
            {
                offset += read;
            }
            (offsetOfCD, sizeOfCD, archiveComment) = FindCentralDirectoryOffsetAndSize(stackBuffer[..offset]);
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
            CreateFromCentralDirectoryStreamFactory(streamFactory,
                                                    sizeOfCD,
                                                    offsetOfCD);

        reader.ArchiveComment = archiveComment;
        return reader;
    }

    /// <summary>
    /// Creates a <see cref="ZipArchiveReader"/> from a <see cref="Stream"/> asynchronously.
    /// </summary>
    /// <param name="sourceStream">The source <see cref="Stream"/> for the reader to read from.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A parsed Zip Archive including entries to read from.</returns>
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="IndexOutOfRangeException"/>
    public static async Task<ZipArchiveReader> CreateFromAsync(
        Stream            sourceStream,
        CancellationToken token = default)
    {
        long streamLength = sourceStream.Length;
        if (streamLength <= 0)
        {
            throw new InvalidOperationException("Stream has 0 bytes in size!");
        }

        if (!sourceStream.CanSeek)
        {
            throw new InvalidOperationException("Stream must be seekable!");
        }

        string? archiveComment;
        long offsetOfCD;
        long sizeOfCD;

        long offsetOfEOCD = Math.Clamp(streamLength - Constants.EOCDBufferLength,
                                       0,
                                       streamLength);

        sourceStream.Position = offsetOfEOCD;

        (offsetOfCD, sizeOfCD, archiveComment) =
            await FindCentralDirectoryOffsetAndSizeAsync(sourceStream,
                                                         Constants.EOCDBufferLength,
                                                         token);

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

        sourceStream.Position = offsetOfCD;

        ZipArchiveReader reader =
            await CreateFromCentralDirectoryStreamAsync(sourceStream,
                                                        sizeOfCD,
                                                        token);

        reader.ArchiveComment = archiveComment;
        return reader;
    }

    /// <summary>
    /// Creates a <see cref="ZipArchiveReader"/> from a <see cref="Stream"/>.
    /// </summary>
    /// <param name="sourceStream">The source <see cref="Stream"/> for the reader to read from.</param>
    /// <returns>A parsed Zip Archive including entries to read from.</returns>
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="IndexOutOfRangeException"/>
    public static unsafe ZipArchiveReader CreateFrom(Stream sourceStream)
    {
        long streamLength = sourceStream.Length;
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

        sourceStream.Position = offsetOfEOCD;

        scoped Span<byte> stackBuffer = stackalloc byte[Constants.EOCDBufferLength];

        int read;
        int offset = 0;
        while ((read = sourceStream.Read(stackBuffer[offset..])) > 0)
        {
            offset += read;
        }
        (offsetOfCD, sizeOfCD, archiveComment) = FindCentralDirectoryOffsetAndSize(stackBuffer[..offset]);

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

        // Assume the offset is beyond the length due to invalid Zip, back-read from the offset position.
        if (sourceStream.Length < offsetOfCD)
        {
            long wentBackward = sizeOfCD;
            wentBackward += offsetOfCD > uint.MaxValue
                ? sizeof(Zip64EOCDRHeader)
                : sizeof(Zip32EOCDRHeader);
            sourceStream.Position -= wentBackward;
        }
        else
        {
            sourceStream.Position = offsetOfCD;
        }

        ZipArchiveReader reader = CreateFromCentralDirectoryStream(sourceStream, sizeOfCD);
        reader.ArchiveComment = archiveComment;
        return reader;
    }
    #endregion

    #region Utilities
    private static ZipArchiveReader CreateFromCentralDirectoryStreamFactory(
        StreamFactory streamFactory,
        long          size,
        long          offset)
    {
        if (size == 0)
        {
            return new ZipArchiveReader();
        }

        using Stream centralDirectoryStream = streamFactory(offset, null);
        return CreateFromCentralDirectoryStream(centralDirectoryStream, size);
    }

    private static ZipArchiveReader CreateFromCentralDirectoryStream(
        Stream sourceStream,
        long   size)
    {
        bool isUseStackalloc = size <= 64 << 10;
        bool isUseRentBuffer = !isUseStackalloc && size <= 4 << 20;
        byte[]? centralDirectoryBuffer = null;

        if (!isUseStackalloc && isUseRentBuffer)
        {
            centralDirectoryBuffer = ArrayPool<byte>.Shared.Rent((int)size);
        }

        if (!isUseStackalloc && !isUseRentBuffer)
        {
            centralDirectoryBuffer = DelegateOverrides.GetHeapArray((int)size);
        }

        scoped Span<byte> centralDirectorySpan = centralDirectoryBuffer ?? stackalloc byte[(int)size];

        try
        {
            int bufferOffset = 0;
            while (size > 0)
            {
                int read = sourceStream.Read(centralDirectorySpan.Slice(bufferOffset, (int)size));
                if (read == 0)
                {
                    throw new IndexOutOfRangeException("Stream has prematurely reached End of Stream while more bytes need to be read");
                }

                bufferOffset += read;
                size -= (uint)read;
            }

            return CreateFromCentralDirectoryBuffer(centralDirectoryBuffer.AsSpan(0, bufferOffset));
        }
        finally
        {
            if (isUseRentBuffer && centralDirectoryBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(centralDirectoryBuffer);
            }
        }
    }

    private static async Task<ZipArchiveReader>
        CreateFromCentralDirectoryStreamFactoryAsync(
        StreamFactoryAsync streamFactory,
        long               size,
        long               offset,
        CancellationToken  token = default)
    {
        if (size == 0)
        {
            return new ZipArchiveReader();
        }

        await using Stream centralDirectoryStream = await streamFactory(offset, null, token);
        return await CreateFromCentralDirectoryStreamAsync(centralDirectoryStream,
                                                           size,
                                                           token);
    }

    private static async Task<ZipArchiveReader>
        CreateFromCentralDirectoryStreamAsync(
        Stream            sourceStream,
        long              size,
        CancellationToken token = default)
    {
        bool isUseRentBuffer = size <= 4 << 20;
        byte[] centralDirectoryBuffer = isUseRentBuffer
            ? ArrayPool<byte>.Shared.Rent((int)size)
            : DelegateOverrides.GetHeapArray((int)size);

        try
        {
            int bufferOffset = 0;
            while (size > 0)
            {
                int read = await sourceStream
                                .ReadAsync(centralDirectoryBuffer.AsMemory(bufferOffset, (int)size),
                                           token)
                                .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IndexOutOfRangeException("Stream has prematurely reached End of Stream while more bytes need to be read");
                }

                bufferOffset += read;
                size -= (uint)read;
            }

            return CreateFromCentralDirectoryBuffer(centralDirectoryBuffer.AsSpan(0, bufferOffset));
        }
        finally
        {
            if (isUseRentBuffer)
            {
                ArrayPool<byte>.Shared.Return(centralDirectoryBuffer);
            }
        }
    }

    private static ZipArchiveReader CreateFromCentralDirectoryBuffer(ReadOnlySpan<byte> bufferSpan)
    {
        ZipArchiveReader archive = new();
        while (!bufferSpan.IsEmpty)
        {
            bufferSpan = ZipArchiveEntry.CreateFromBlockSpan(bufferSpan, out ZipArchiveEntry entry);
            archive.Entries.Add(entry);
        }

        return archive;
    }

    private static async Task<long> GetLengthFromStreamFactoryAsync(
        StreamFactoryAsync streamFactory,
        CancellationToken token = default)
    {
        await using Stream stream = await streamFactory(0, null, token);
        return stream.Length;
    }

    private static long GetLengthFromStreamFactory(StreamFactory streamFactory)
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
            int offset = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(offset), token)) > 0)
            {
                offset += read;
            }
            return FindCentralDirectoryOffsetAndSize(buffer.AsSpan(0, offset));
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
        buffer = buffer[offset..];

        Zip32EOCDRHeader header = MemoryMarshal.Read<Zip32EOCDRHeader>(buffer);
        header.EnsureHeaderIsValid();

        uint   sizeCDOnStream   = header.CentralDirectorySize;
        uint   offsetCDOnStream = header.CentralDirectoryOffset;
        ushort commentLength    = header.CommentLength;

        int bufferLenRemained = buffer.Length - Zip32EOCDRHeaderLength;
        if (bufferLenRemained < commentLength)
        {
            commentLength = (ushort)bufferLenRemained;
        }
        ReadOnlySpan<byte> commentSpan = buffer.Slice(Zip32EOCDRHeaderLength, commentLength);

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
        (uint offsetCDR32, uint sizeCDR32, string? archiveComment) = FindCentralDirectoryOffsetAndSize32(buffer, offset32);

        // Skip if both offset and size aren't exceeding uint.MaxValue, even though Zip64 End of Central Directory Record exist.
        if (offsetCDR32 != Constants.Zip64Mask &&
            sizeCDR32 != Constants.Zip64Mask)
        {
            return (offsetCDR32, sizeCDR32, archiveComment);
        }

        long offsetCDR64 = offsetCDR32;
        long sizeCDR64   = sizeCDR32;

        // Then, we try to capture the offset and size from Zip64 End of Central Directory Record.
        ReadOnlySpan<byte> buffer64 = buffer[offset64..];
        if (buffer64.Length < Zip64EOCDRHeaderLength)
        {
            throw new InvalidOperationException("The size of Zip64 End-Of-Central Directory Record header is premature");
        }

        Zip64EOCDRHeader header = MemoryMarshal.Read<Zip64EOCDRHeader>(buffer64);
        header.EnsureHeaderIsValid();

        if (sizeCDR32 == Constants.Zip64Mask)
        {
            sizeCDR64 = header.CentralDirectorySize;
        }

        if (offsetCDR32 == Constants.Zip64Mask)
        {
            offsetCDR64 = header.CentralDirectoryOffset;
        }

        return (offsetCDR64, sizeCDR64, archiveComment);
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

    #region Private classes and structs

    private static readonly unsafe int Zip32EOCDRHeaderLength = sizeof(Zip32EOCDRHeader);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private readonly struct Zip32EOCDRHeader
    {
        public readonly uint   Signature;
        public readonly ushort DiskNumber;
        public readonly ushort DiskNumberCentralDirectoryStart;
        public readonly ushort CentralDirectoryCountOnDisk;
        public readonly ushort CentralDirectoryCount;
        public readonly uint   CentralDirectorySize;
        public readonly uint   CentralDirectoryOffset;
        public readonly ushort CommentLength;

        public void EnsureHeaderIsValid()
        {
            if (Signature != Constants.Zip32EOCDRHeaderMagic)
            {
                throw new InvalidOperationException("Invalid Zip32 End-Of-Central Directory Header signature.");
            }
        }
    }

    private static readonly unsafe int Zip64EOCDRHeaderLength = sizeof(Zip64EOCDRHeader);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private readonly struct Zip64EOCDRHeader
    {
        public readonly uint   Signature;
        public readonly long   SizeOfEOCD64;
        public readonly ushort Version;
        public readonly ushort VersionNeeded;
        public readonly uint   DiskNumber;
        public readonly uint   DiskNumberCentralDirectoryStart;
        public readonly long   CentralDirectoryCountOnDisk;
        public readonly long   CentralDirectoryCount;
        public readonly long   CentralDirectorySize;
        public readonly long   CentralDirectoryOffset;

        public void EnsureHeaderIsValid()
        {
            if (Signature != Constants.Zip64EOCDRHeaderMagic)
            {
                throw new InvalidOperationException("Invalid Zip64 End-Of-Central Directory Header signature.");
            }
        }
    }

    #endregion
}
