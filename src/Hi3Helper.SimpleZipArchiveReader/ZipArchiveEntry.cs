using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable ConvertIfStatementToSwitchStatement

// ReSharper disable UnusedMember.Global

namespace Hi3Helper.SimpleZipArchiveReader;

public sealed partial class ZipArchiveEntry
{
    #region Public Properties

    public string              Filename                   { get; private init; } = "";
    public string?             Comment                    { get; private init; }
    public long                Size                       { get; private init; }
    public long                SizeCompressed             { get; private init; }
    public ZipCompressionTypes CompressionType            { get; private init; }
    public ZipCdrBitFlagValues Flags                      { get; private init; }
    public DateTimeOffset      LastModified               { get; private init; }
    public uint                Crc32                      { get; private init; }
    public long                LocalBlockOffsetFromStream { get; private init; }

    public bool IsDirectory
    {
        get
        {
            if (Flags.HasFlag(ZipCdrBitFlagValues.DataDescriptor))
            {
                return false;
            }

            return Filename.AsSpan()[^1] is '/' or '\\';
        }
    }

    public override string ToString() => IsDirectory
        ? Filename + (string.IsNullOrEmpty(Comment) ? string.Empty : $" | Comment: {Comment}")
        : $"{Filename} | SizeU: {Size} | SizeC: {SizeCompressed}" + (string.IsNullOrEmpty(Comment) ? string.Empty : $" | Comment: {Comment}");

    #endregion

    /// <summary>
    /// Open the entry as the raw Sub-<see cref="Stream"/> from the factory asynchronously.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A raw Sub-<see cref="Stream"/> of the current entry. If you need to create the decompression stream one, use <see cref="OpenStreamFromAsync(StreamFactoryAsync, CancellationToken)"/> instead.</returns>
    /// <exception cref="InvalidOperationException"/>
    public async Task<Stream> OpenRawSubStreamFromAsync(
        StreamFactoryAsync streamFactory,
        CancellationToken  token = default)
    {
        Stream stream = await streamFactory(LocalBlockOffsetFromStream, null, token);

        try
        {
            return await OpenRawSubStreamFromAsync(stream,
                                                   true,
                                                   true,
                                                   token);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Open the entry as the raw Sub-<see cref="Stream"/> from the factory.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <returns>A raw Sub-<see cref="Stream"/> of the current entry. If you need to create the decompression stream one, use <see cref="OpenStreamFrom(StreamFactory)"/> instead.</returns>
    /// <exception cref="InvalidOperationException"/>
    public Stream OpenRawSubStreamFrom(StreamFactory streamFactory)
    {
        Stream stream = streamFactory(LocalBlockOffsetFromStream, null);

        try
        {
            return OpenRawSubStreamFrom(stream, true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Open the entry as the raw Sub-<see cref="Stream"/> from a source <see cref="Stream"/> asynchronously.
    /// </summary>
    /// <param name="sourceStream">The source <see cref="Stream"/> for the reader to read from.</param>
    /// <param name="streamAtLocalHeaderOffset">If set to <see langword="true"/>, the <paramref name="sourceStream"/> position will not getting seek to the Local Header offset of the current entry.</param>
    /// <param name="disposeSourceStream">If set to <see langword="true"/>, the <paramref name="sourceStream"/> will be disposed after the returned Sub-<see cref="Stream"/> was disposed.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A raw Sub-<see cref="Stream"/> of the current entry. If you need to create the decompression stream one, use <see cref="OpenStreamFromAsync(Stream, bool, bool, CancellationToken)"/> instead.</returns>
    /// <exception cref="InvalidOperationException"/>
    public async Task<Stream> OpenRawSubStreamFromAsync(
        Stream            sourceStream,
        bool              streamAtLocalHeaderOffset = false,
        bool              disposeSourceStream       = true,
        CancellationToken token                     = default)
    {
        if (IsDirectory)
        {
            throw new InvalidOperationException("Cannot open Stream for Directory-kind entry.");
        }

        if (!streamAtLocalHeaderOffset && !sourceStream.CanSeek)
        {
            throw new InvalidOperationException($"Cannot seek to local header offset at: {LocalBlockOffsetFromStream} as the source stream is not seekable!");
        }

        if (!streamAtLocalHeaderOffset)
        {
            sourceStream.Position = LocalBlockOffsetFromStream;
        }

        return await OpenSubStreamFromEntryAsync(sourceStream,
                                                 ZipCompressionTypes.Store,
                                                 SizeCompressed,
                                                 Size,
                                                 disposeSourceStream,
                                                 token);
    }

    /// <summary>
    /// Open the entry as the raw Sub-<see cref="Stream"/> from a source <see cref="Stream"/>.
    /// </summary>
    /// <param name="sourceStream">The source <see cref="Stream"/> for the reader to read from.</param>
    /// <param name="streamAtLocalHeaderOffset">If set to <see langword="true"/>, the <paramref name="sourceStream"/> position will not getting seek to the Local Header offset of the current entry.</param>
    /// <param name="disposeSourceStream">If set to <see langword="true"/>, the <paramref name="sourceStream"/> will be disposed after the returned Sub-<see cref="Stream"/> was disposed.</param>
    /// <returns>A raw Sub-<see cref="Stream"/> of the current entry. If you need to create the decompression stream one, use <see cref="OpenStreamFrom(Stream, bool, bool)"/> instead.</returns>
    /// <exception cref="InvalidOperationException"/>
    public Stream OpenRawSubStreamFrom(
        Stream sourceStream,
        bool   streamAtLocalHeaderOffset = false,
        bool   disposeSourceStream       = true)
    {
        if (IsDirectory)
        {
            throw new InvalidOperationException("Cannot open Stream for Directory-kind entry.");
        }

        if (!streamAtLocalHeaderOffset && !sourceStream.CanSeek)
        {
            throw new InvalidOperationException($"Cannot seek to local header offset at: {LocalBlockOffsetFromStream} as the source stream is not seekable!");
        }

        if (!streamAtLocalHeaderOffset)
        {
            sourceStream.Position = LocalBlockOffsetFromStream;
        }

        return OpenSubStreamFromEntry(sourceStream,
                                      ZipCompressionTypes.Store,
                                      SizeCompressed,
                                      Size,
                                      disposeSourceStream);
    }

    /// <summary>
    /// Open the entry as <see cref="Stream"/> from a source <see cref="Stream"/> asynchronously.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>Either decompression <see cref="DeflateStream"/> or non-compressed <see cref="Stream"/> (Stored).</returns>
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="NotSupportedException"/>
    public async Task<Stream> OpenStreamFromAsync(
        StreamFactoryAsync streamFactory,
        CancellationToken  token = default)
    {
        Stream subStream = await OpenRawSubStreamFromAsync(streamFactory, token);
        return DetermineCreateStreamType(subStream, CompressionType, Size);
    }

    /// <summary>
    /// Open the entry as <see cref="Stream"/> from a source <see cref="Stream"/>.
    /// </summary>
    /// <param name="sourceStream">The source <see cref="Stream"/> for the reader to read from.</param>
    /// <param name="streamAtLocalHeaderOffset">If set to <see langword="true"/>, the <paramref name="sourceStream"/> position will not getting seek to the Local Header offset of the current entry.</param>
    /// <param name="disposeSourceStream">If set to <see langword="true"/>, the <paramref name="sourceStream"/> will be disposed after the returned Sub-<see cref="Stream"/> was disposed.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>Either decompression <see cref="DeflateStream"/> or non-compressed <see cref="Stream"/> (Stored).</returns>
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="NotSupportedException"/>
    public async Task<Stream> OpenStreamFromAsync(
        Stream            sourceStream,
        bool              streamAtLocalHeaderOffset = false,
        bool              disposeSourceStream       = true,
        CancellationToken token                     = default)
    {
        Stream subStream = await OpenRawSubStreamFromAsync(sourceStream, streamAtLocalHeaderOffset, disposeSourceStream, token);
        return DetermineCreateStreamType(subStream, CompressionType, Size);
    }

    /// <summary>
    /// Open the entry as <see cref="Stream"/> from the factory.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <returns>Either decompression <see cref="DeflateStream"/> or non-compressed <see cref="Stream"/> (Stored).</returns>
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="NotSupportedException"/>
    public Stream OpenStreamFrom(StreamFactory streamFactory) =>
        DetermineCreateStreamType(OpenRawSubStreamFrom(streamFactory), CompressionType, Size);

    /// <summary>
    /// Open the entry as <see cref="Stream"/> from the factory.
    /// </summary>
    /// <param name="sourceStream">The source <see cref="Stream"/> for the reader to read from.</param>
    /// <param name="streamAtLocalHeaderOffset">If set to <see langword="true"/>, the <paramref name="sourceStream"/> position will not getting seek to the Local Header offset of the current entry.</param>
    /// <param name="disposeSourceStream">If set to <see langword="true"/>, the <paramref name="sourceStream"/> will be disposed after the returned Sub-<see cref="Stream"/> was disposed.</param>
    /// <returns>Either decompression <see cref="DeflateStream"/> or non-compressed <see cref="Stream"/> (Stored).</returns>
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="NotSupportedException"/>
    public Stream OpenStreamFrom(Stream sourceStream,
                                 bool   streamAtLocalHeaderOffset = false,
                                 bool   disposeSourceStream = true) =>
        DetermineCreateStreamType(OpenRawSubStreamFrom(sourceStream, streamAtLocalHeaderOffset, disposeSourceStream), CompressionType, Size);

    private static async Task<Stream> OpenSubStreamFromEntryAsync(
        Stream              sourceStream,
        ZipCompressionTypes compType,
        long                subStreamSize,
        long                uncompressedStreamSize,
        bool                disposeStream,
        CancellationToken   token)
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(Zip32LocalHeaderLength);
        try
        {
            // Try skip local header
            int read = await sourceStream.ReadAsync(headerBuffer.AsMemory(0, Zip32LocalHeaderLength), token);
            if (read < Zip32LocalHeaderLength)
            {
                throw new InvalidOperationException("Local Zip Block header is invalid!");
            }

            Zip32LocalHeader header = MemoryMarshal.Read<Zip32LocalHeader>(headerBuffer);
            header.EnsureHeaderIsValid();

            // Seek extra data
            int extraDataLen = header.FilenameLength + header.ExtraFieldLength;
            await sourceStream.SeekStreamAdvancedToAsync(extraDataLen, token);

            // Once getting the data position, assign to SubStream.
            SequentialReadSubStream subStream = new(sourceStream, subStreamSize, disposeStream);
            return DetermineCreateStreamType(subStream, compType, uncompressedStreamSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    private static Stream OpenSubStreamFromEntry(
        Stream              stream,
        ZipCompressionTypes compType,
        long                subStreamSize,
        long                uncompressedStreamSize,
        bool                disposeStream)
    {
        // Try skip local header
        scoped Span<byte> headerBuffer = stackalloc byte[Zip32LocalHeaderLength];
        int               read         = stream.Read(headerBuffer);
        if (read < Zip32LocalHeaderLength)
        {
            throw new InvalidOperationException("Local Zip Block header is invalid!");
        }

        Zip32LocalHeader header = MemoryMarshal.Read<Zip32LocalHeader>(headerBuffer);
        header.EnsureHeaderIsValid();

        // Seek extra data
        int extraDataLen = header.FilenameLength + header.ExtraFieldLength;
        stream.SeekStreamAdvancedTo(extraDataLen);

        // Once getting the data position, assign to SubStream.
        SequentialReadSubStream subStream = new(stream, subStreamSize, disposeStream);
        return DetermineCreateStreamType(subStream, compType, uncompressedStreamSize);
    }

    private static Stream DetermineCreateStreamType(
        Stream              subStream,
        ZipCompressionTypes compType,
        long                expectedUncompressedSize) =>
        compType switch
        {
            ZipCompressionTypes.Store => subStream,
            ZipCompressionTypes.Deflate => new DeflateStream(subStream, CompressionMode.Decompress),
            ZipCompressionTypes.Deflate64 => new DeflateManagedStream(subStream, true, expectedUncompressedSize),
            _ => throw new NotSupportedException($"Compression type: {compType} is not supported. It must be either Store (0), Deflate (8) and Deflate64 (9)")
        };

    #region Private classes and structs
    private static readonly unsafe int Zip32LocalHeaderLength = sizeof(Zip32LocalHeader);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct Zip32LocalHeader
    {
        public uint                Signature;
        public ushort              Version;
        public ZipCdrBitFlagValues Flags;
        public ZipCompressionTypes CompressionType;
        public uint                DosLastModifiedDateTime;
        public uint                Crc32;
        public uint                CompressedSize;
        public uint                Size;
        public ushort              FilenameLength;
        public ushort              ExtraFieldLength;

        public void EnsureHeaderIsValid()
        {
            if (Signature != Constants.Zip32LocalHeaderMagic)
            {
                throw new InvalidOperationException("Invalid Local Header signature.");
            }

            if (FilenameLength > short.MaxValue ||
                ExtraFieldLength > short.MaxValue)
            {
                throw new InvalidOperationException("Filename and ExtraField length is beyond signed Int16 Maximum value.");
            }
        }
    }
    #endregion
}
