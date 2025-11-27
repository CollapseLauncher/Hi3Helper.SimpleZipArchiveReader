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

    public string              Filename       { get; private set; } = "";
    public string?             Comment        { get; private set; }
    public long                Size           { get; private set; }
    public long                SizeCompressed { get; private set; }
    public DateTimeOffset      LastModified   { get; private set; }
    public ZipCDRBitFlagValues Flags          { get; private set; }
    public uint                Crc32          { get; private set; }
    public bool                IsDeflate64    { get; private set; }
    public bool                IsDeflate      { get; private set; }

    public bool IsDirectory => Filename.AsSpan()[^1] is '/' or '\\';

    public override string ToString() => IsDirectory
        ? Filename + (string.IsNullOrEmpty(Comment) ? string.Empty : $" | Comment: {Comment}")
        : $"{Filename} | SizeU: {Size} | SizeC: {SizeCompressed}" + (string.IsNullOrEmpty(Comment) ? string.Empty : $" | Comment: {Comment}");

    #endregion

    #region Isolated Properties
    private long LocalBlockOffsetFromStream { get; set; }
    #endregion

    /// <summary>
    /// Open the entry as <see cref="Stream"/> from the factory asynchronously.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <param name="token">Cancellation token for asynchronous operations.</param>
    /// <returns>Either decompression <see cref="DeflateStream"/> or non-compressed <see cref="Stream"/> (Stored).</returns>
    /// <exception cref="InvalidOperationException"/>
    public async Task<Stream> OpenStreamFromFactoryAsync(
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
            int read = await stream.ReadAsync(headerBuffer, 0, localHeaderLen, token);

            if (read < localHeaderLen)
            {
                throw new InvalidOperationException("Local Zip Block header is invalid!");
            }

            uint   signature     = MemoryMarshal.Read<uint>(headerBuffer);
            ushort fileNameLen   = MemoryMarshal.Read<ushort>(headerBuffer.AsSpan(filenameLenOffset));
            ushort extraFieldLen = MemoryMarshal.Read<ushort>(headerBuffer.AsSpan(extraFieldLenOffset));
            int    extraDataLen  = fileNameLen + extraFieldLen;

            if (Constants.Zip32LocalHeaderMagic != signature ||
                fileNameLen > short.MaxValue ||
                extraFieldLen > short.MaxValue)
            {
                throw new
                    InvalidOperationException("Local Zip Block header signature is invalid! Zip might be corrupted.");
            }
            
            extraDataBuffer = ArrayPool<byte>.Shared.Rent(extraDataLen);
            _               = await stream.ReadAsync(extraDataBuffer, 0, extraDataLen, token);

            SequentialReadSubStream chunkSubStream = new(stream, SizeCompressed);
            if (!IsDeflate)
            {
                return chunkSubStream;
            }

            return IsDeflate64
                ? new DeflateManagedStream(chunkSubStream, true, SizeCompressed)
                : new DeflateStream(chunkSubStream, CompressionMode.Decompress);
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
    /// Open the entry as <see cref="Stream"/> from the factory.
    /// </summary>
    /// <param name="streamFactory">The factory of the source <see cref="Stream"/> for the reader to read from.</param>
    /// <returns>Either decompression <see cref="DeflateStream"/> or non-compressed <see cref="Stream"/> (Stored).</returns>
    /// <exception cref="InvalidOperationException"/>
    public Stream OpenStreamFromFactory(StreamFactory streamFactory)
    {
        const int localHeaderLen      = 30;
        const int filenameLenOffset   = 26;
        const int extraFieldLenOffset = 28;

        if (IsDirectory)
        {
            throw new InvalidOperationException("Cannot open Stream for Directory-kind entry.");
        }

        // Try skip local header
        Stream stream = streamFactory(LocalBlockOffsetFromStream, null);
        scoped Span<byte> headerBuffer = stackalloc byte[localHeaderLen];
        int               read         = stream.Read(headerBuffer);

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

        scoped Span<byte> extraDataBuffer = stackalloc byte[extraDataLen];
        _ = stream.Read(extraDataBuffer);

        // Once getting the data position, assign to SubStream.
        SequentialReadSubStream chunkSubStream = new(stream, SizeCompressed);
        if (!IsDeflate)
        {
            return chunkSubStream;
        }

        return IsDeflate64
            ? new DeflateManagedStream(chunkSubStream, true, SizeCompressed)
            : new DeflateStream(chunkSubStream, CompressionMode.Decompress);
    }
}
