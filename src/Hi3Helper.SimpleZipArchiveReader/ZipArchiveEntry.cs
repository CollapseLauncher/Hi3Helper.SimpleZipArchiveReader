using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// ReSharper disable UnusedMember.Global

namespace Hi3Helper.SimpleZipArchiveReader;

public sealed partial class ZipArchiveEntry
{
    #region Public Properties

    public string              Filename        { get; private init; } = "";
    public string?             Comment         { get; private init; }
    public long                Size            { get; private init; }
    public long                SizeCompressed  { get; private init; }
    public DateTimeOffset      LastModified    { get; private set; }
    public ZipCdrBitFlagValues Flags           { get; private set; }
    public uint                Crc32           { get; private set; }
    public bool                IsDeflate64     { get; private init; }
    public ZipCompressionTypes CompressionType { get; private init; }

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

    #region Isolated Properties
    private long LocalBlockOffsetFromStream { get; set; }
    #endregion

    /// <summary>
    /// Open the entry as the raw Sub-<see cref="Stream"/> from the factory asynchronously.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>A raw Sub-<see cref="Stream"/> of the current entry. If you need to create the decompression stream one, use <see cref="OpenStreamFromFactoryAsync"/> instead.</returns>
    /// <exception cref="InvalidOperationException"/>
    public async Task<Stream> OpenRawSubStreamFromFactoryAsync(
        StreamFactoryAsync streamFactory,
        CancellationToken  token = default)
    {
        const int localHeaderLen      = 30;
        const int filenameLenOffset   = 26;
        const int extraFieldLenOffset = 28;

        if (IsDirectory)
        {
            throw new InvalidOperationException("Cannot open Stream for Directory-kind entry.");
        }

        Stream stream = await streamFactory(LocalBlockOffsetFromStream, null, token);

        byte[]  headerBuffer    = ArrayPool<byte>.Shared.Rent(localHeaderLen);
        byte[]? extraDataBuffer = null;
        try
        {
            // Try skip local header
            int read = await stream.ReadAsync(headerBuffer.AsMemory(0, localHeaderLen), token);

            int extraDataLen = GetExtraFieldsDataLength(read, localHeaderLen, headerBuffer, filenameLenOffset, extraFieldLenOffset);

            extraDataBuffer = ArrayPool<byte>.Shared.Rent(extraDataLen);
            _ = await stream.ReadAsync(extraDataBuffer.AsMemory(0, extraDataLen), token);

            // Once getting the data position, assign to SubStream.
            return new SequentialReadSubStream(stream, SizeCompressed);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
            if (extraDataBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(extraDataBuffer);
            }
        }
    }

    /// <summary>
    /// Open the entry as <see cref="Stream"/> from the factory asynchronously.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>Either decompression <see cref="DeflateStream"/> or non-compressed <see cref="Stream"/> (Stored).</returns>
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="NotSupportedException"/>
    public async Task<Stream> OpenStreamFromFactoryAsync(
        StreamFactoryAsync streamFactory,
        CancellationToken  token = default)
    {
        Stream subStream = await OpenRawSubStreamFromFactoryAsync(streamFactory, token);
        return DetermineCreateStreamType(subStream, CompressionType, IsDeflate64, Size);
    }

    /// <summary>
    /// Open the entry as the raw Sub-<see cref="Stream"/> from the factory.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <returns>A raw Sub-<see cref="Stream"/> of the current entry. If you need to create the decompression stream one, use <see cref="OpenStreamFromFactory"/> instead.</returns>
    /// <exception cref="InvalidOperationException"/>
    public Stream OpenRawSubStreamFromFactory(StreamFactory streamFactory)
    {
        const int localHeaderLen      = 30;
        const int filenameLenOffset   = 26;
        const int extraFieldLenOffset = 28;

        if (IsDirectory)
        {
            throw new InvalidOperationException("Cannot open Stream for Directory-kind entry.");
        }

        Stream stream = streamFactory(LocalBlockOffsetFromStream, null);

        try
        {
            // Try skip local header
            scoped Span<byte> headerBuffer = stackalloc byte[localHeaderLen];
            int               read         = stream.Read(headerBuffer);

            int extraDataLen = GetExtraFieldsDataLength(read, localHeaderLen, headerBuffer, filenameLenOffset, extraFieldLenOffset);

            scoped Span<byte> extraDataBuffer = stackalloc byte[extraDataLen];
            _ = stream.Read(extraDataBuffer);

            // Once getting the data position, assign to SubStream.
            return new SequentialReadSubStream(stream, SizeCompressed);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Open the entry as <see cref="Stream"/> from the factory.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <returns>Either decompression <see cref="DeflateStream"/> or non-compressed <see cref="Stream"/> (Stored).</returns>
    /// <exception cref="InvalidOperationException"/>
    /// <exception cref="NotSupportedException"/>
    public Stream OpenStreamFromFactory(StreamFactory streamFactory) =>
        DetermineCreateStreamType(OpenRawSubStreamFromFactory(streamFactory), CompressionType, IsDeflate64, Size);

    private static int GetExtraFieldsDataLength(
        int        read,
        int        localHeaderLen,
        Span<byte> headerBuffer,
        int        filenameLenOffset,
        int        extraFieldLenOffset)
    {
        if (read < localHeaderLen)
        {
            throw new InvalidOperationException("Local Zip Block header is invalid!");
        }

        uint   signature     = MemoryMarshal.Read<uint>(headerBuffer);
        ushort fileNameLen   = MemoryMarshal.Read<ushort>(headerBuffer[filenameLenOffset..]);
        ushort extraFieldLen = MemoryMarshal.Read<ushort>(headerBuffer[extraFieldLenOffset..]);
        int    extraDataLen  = fileNameLen + extraFieldLen;

        if (Constants.Zip32LocalHeaderMagic != signature ||
            fileNameLen > short.MaxValue ||
            extraFieldLen > short.MaxValue)
        {
            throw new
                InvalidOperationException("Local Zip Block header signature is invalid! Zip might be corrupted.");
        }

        return extraDataLen;
    }

    private static Stream DetermineCreateStreamType(
        Stream              subStream,
        ZipCompressionTypes compType,
        bool                isDeflate64,
        long                expectedUncompressedSize) =>
        compType switch
        {
            ZipCompressionTypes.Store => subStream,
            ZipCompressionTypes.Deflate or ZipCompressionTypes.EnhancedDeflate => isDeflate64
                ? new DeflateManagedStream(subStream, true, expectedUncompressedSize)
                : new DeflateStream(subStream, CompressionMode.Decompress),
            _ => throw new NotSupportedException($"Compression type: {compType} is not supported (Has Zip64 characteristics?: {isDeflate64}). It must be either Store (0), Deflate (8), EnhancedDeflate (9) or EnhancedDeflate64 (9 + Zip64 characteristics)")
        };
}
