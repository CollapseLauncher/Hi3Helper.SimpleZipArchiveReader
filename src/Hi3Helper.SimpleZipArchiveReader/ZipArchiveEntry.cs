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
    public ZipCompressionTypes CompressionType { get; private init; }
    public ZipCdrBitFlagValues Flags           { get; private init; }
    public DateTimeOffset      LastModified    { get; private init; }
    public uint                Crc32           { get; private init; }

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
    private long LocalBlockOffsetFromStream { get; init; }
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
        if (IsDirectory)
        {
            throw new InvalidOperationException("Cannot open Stream for Directory-kind entry.");
        }

        Stream stream = await streamFactory(LocalBlockOffsetFromStream, null, token);

        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(Zip32LocalHeaderLength);
        try
        {
            // Try skip local header
            int read = await stream.ReadAsync(headerBuffer.AsMemory(0, Zip32LocalHeaderLength), token);
            if (read < Zip32LocalHeaderLength)
            {
                throw new InvalidOperationException("Local Zip Block header is invalid!");
            }

            Zip32LocalHeader header = MemoryMarshal.Read<Zip32LocalHeader>(headerBuffer);
            header.EnsureHeaderIsValid();

            // Seek extra data
            int extraDataLen = header.FilenameLength + header.ExtraFieldLength;
            await stream.SeekStreamAdvancedToAsync(extraDataLen, token);

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
        return DetermineCreateStreamType(subStream, CompressionType, Size);
    }

    /// <summary>
    /// Open the entry as the raw Sub-<see cref="Stream"/> from the factory.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <returns>A raw Sub-<see cref="Stream"/> of the current entry. If you need to create the decompression stream one, use <see cref="OpenStreamFromFactory"/> instead.</returns>
    /// <exception cref="InvalidOperationException"/>
    public unsafe Stream OpenRawSubStreamFromFactory(StreamFactory streamFactory)
    {
        if (IsDirectory)
        {
            throw new InvalidOperationException("Cannot open Stream for Directory-kind entry.");
        }

        Stream stream = streamFactory(LocalBlockOffsetFromStream, null);

        try
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
        DetermineCreateStreamType(OpenRawSubStreamFromFactory(streamFactory), CompressionType, Size);

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
