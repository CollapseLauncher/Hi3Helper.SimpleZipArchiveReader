using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
// ReSharper disable InconsistentNaming
// ReSharper disable UnusedMember.Global
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

namespace Hi3Helper.SimpleZipArchiveReader;

// Sources:
// https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Compression/src/System/IO/Compression/ZipBlocks.FieldLengths.cs
// https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Compression/src/System/IO/Compression/ZipBlocks.FieldLocations.cs
// https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Compression/src/System/IO/Compression/ZipBlocks.cs#L218
// https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Compression/src/System/IO/Compression/ZipHelper.cs#L36
public sealed partial class ZipArchiveEntry
{
    private static bool TryReadZip64SizeFromExtraField(
        ReadOnlySpan<byte> dataTrailing,
        ref long           uncompressedSize,
        ref long           compressedSize,
        ref long           relativeOffsetOfLocalHeader)
    {
        const ushort tagConstant     = 1;
        const int    tagSizeFieldLen = sizeof(ushort) * 2;

    TryReadAnother:
        if (dataTrailing.Length < tagSizeFieldLen)
        {
            return false;
        }

        ZipExtraFieldHeader header = MemoryMarshal.Read<ZipExtraFieldHeader>(dataTrailing);
        ReadOnlySpan<byte>  data   = dataTrailing.Slice(tagSizeFieldLen, header.Size);

        if (header.Tag != tagConstant)
        {
            dataTrailing = dataTrailing[(data.Length + tagSizeFieldLen)..];
            goto TryReadAnother;
        }

        // The spec section 4.5.3:
        //      The order of the fields in the zip64 extended
        //      information record is fixed, but the fields MUST
        //      only appear if the corresponding Local or Central
        //      directory record field is set to 0xFFFF or 0xFFFFFFFF.
        // However, tools commonly write the fields anyway; the prevailing convention
        // is to respect the size, but only actually use the values if their 32 bit
        // values were all 0xFF.
        if (data.Length < sizeof(uint))
        {
            return true;
        }

        // Advancing the stream (by reading from it) is possible only when:
        // 1. There is an explicit ask to do that (valid files, corresponding boolean flag(s) set to true).
        // 2. When the size indicates that all the information is available ("slightly invalid files").
        bool readAllFields = header.Size >= Zip64ExtraFieldLengths.MaximumExtraFieldLength;

        if (uncompressedSize == Constants.Zip64Mask)
        {
            uncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(data);
            data             = data[Zip64ExtraFieldLengths.UncompressedSize..];
        }
        else if (readAllFields)
        {
            data = data[Zip64ExtraFieldLengths.UncompressedSize..];
        }

        if (data.Length < Zip64ExtraFieldLengths.CompressedSize)
        {
            return true;
        }

        if (compressedSize == Constants.Zip64Mask)
        {
            compressedSize = BinaryPrimitives.ReadInt64LittleEndian(data);
            data           = data[Zip64ExtraFieldLengths.CompressedSize..];
        }
        else if (readAllFields)
        {
            data = data[Zip64ExtraFieldLengths.CompressedSize..];
        }

        if (data.Length < Zip64ExtraFieldLengths.LocalHeaderOffset)
        {
            return true;
        }

        if (relativeOffsetOfLocalHeader == Constants.Zip64Mask)
        {
            relativeOffsetOfLocalHeader = BinaryPrimitives.ReadInt64LittleEndian(data);
        }

        return true;
    }

    internal static ReadOnlySpan<byte> CreateFromBlockSpan(
        ReadOnlySpan<byte>  currentBlockSpan,
        out ZipArchiveEntry entry)
    {
        if (currentBlockSpan.Length < Zip32CDRHeaderLength)
        {
            throw new IndexOutOfRangeException("Buffer is insufficient or the Zip Central Directory Record is malformed!");
        }

        Zip32CDRHeader header = MemoryMarshal.Read<Zip32CDRHeader>(currentBlockSpan);
        header.EnsureHeaderIsValid();

        long compressedSize              = header.CompressedSize;
        long uncompressedSize            = header.Size;
        long relativeOffsetOfLocalHeader = header.OffsetOfLocalHeader;

        ReadOnlySpan<byte> dynamicRecord = currentBlockSpan[Zip32CDRHeaderLength..];

        int extraFieldOffset = header.FilenameLength;
        int extraFieldLen    = header.ExtraFieldLength;

        int fileCommentOffset = extraFieldOffset + extraFieldLen;
        int fileCommentLen    = header.CommentLength;

        ReadOnlySpan<byte> fileNameSpan    = dynamicRecord[..header.FilenameLength];
        ReadOnlySpan<byte> extraFieldSpan  = dynamicRecord.Slice(extraFieldOffset,  extraFieldLen);
        ReadOnlySpan<byte> fileCommentSpan = dynamicRecord.Slice(fileCommentOffset, fileCommentLen);

        // Parse filename and comment
        string? fileComment = null;
        if (!fileCommentSpan.IsEmpty)
        {
            fileComment = Encoding.Default.GetString(fileCommentSpan);
        }

        _ = TryReadZip64SizeFromExtraField(extraFieldSpan,
                                           ref uncompressedSize,
                                           ref compressedSize,
                                           ref relativeOffsetOfLocalHeader);

        string fileName = Encoding.UTF8.GetString(fileNameSpan);
        entry = new ZipArchiveEntry
        {
            Crc32           = header.Crc32,
            Flags           = header.Flags,
            CompressionType = header.CompressionType,
            LastModified    = header.DosLastModifiedDateTime.DosTimeToDateTime(),

            Comment                    = fileComment,
            Filename                   = fileName,
            LocalBlockOffsetFromStream = relativeOffsetOfLocalHeader,
            Size                       = uncompressedSize,
            SizeCompressed             = compressedSize
        };

        int endOfBlock = Zip32CDRHeaderLength + header.FilenameLength + extraFieldLen + fileCommentLen;
        return currentBlockSpan[endOfBlock..];
    }

    #region Private classes and structs
    private static readonly unsafe int Zip32CDRHeaderLength = sizeof(Zip32CDRHeader);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private readonly struct Zip32CDRHeader
    {
        public readonly uint                Signature;
        public readonly ushort              Version;
        public readonly ushort              VersionNeeded;
        public readonly ZipCdrBitFlagValues Flags;
        public readonly ZipCompressionTypes CompressionType;
        public readonly uint                DosLastModifiedDateTime;
        public readonly uint                Crc32;
        public readonly uint                CompressedSize;
        public readonly uint                Size;
        public readonly ushort              FilenameLength;
        public readonly ushort              ExtraFieldLength;
        public readonly ushort              CommentLength;
        public readonly ushort              DiskNumberStart;
        public readonly ushort              InternalAttributes;
        public readonly uint                ExternalAttributes;
        public readonly uint                OffsetOfLocalHeader;

        public void EnsureHeaderIsValid()
        {
            if (Signature != Constants.Zip32CDRHeaderMagic)
            {
                throw new InvalidOperationException("Invalid Central Directory signature.");
            }

            if (Flags.HasFlag(ZipCdrBitFlagValues.IsEncrypted))
            {
                throw new NotSupportedException("Encrypted archive is currently not supported.");
            }
        }
    }

    private readonly struct ZipExtraFieldHeader
    {
        public readonly ushort Tag;
        public readonly ushort Size;
    }
    #endregion
}
