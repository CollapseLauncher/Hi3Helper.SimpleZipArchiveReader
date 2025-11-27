using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
// ReSharper disable InconsistentNaming
// ReSharper disable UnusedMember.Global

namespace Hi3Helper.SimpleZipArchiveReader;

// Sources:
// https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Compression/src/System/IO/Compression/ZipBlocks.FieldLengths.cs
// https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Compression/src/System/IO/Compression/ZipBlocks.FieldLocations.cs
// https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Compression/src/System/IO/Compression/ZipBlocks.cs#L218
// https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Compression/src/System/IO/Compression/ZipHelper.cs#L36
public sealed partial class ZipArchiveEntry
{
    [Flags]
    public enum ZipCDRBitFlagValues : ushort
    {
        IsEncrypted               = 0x1,
        DataDescriptor            = 0x8,
        UnicodeFileNameAndComment = 0x800
    }

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

        ushort             tag  = MemoryMarshal.Read<ushort>(dataTrailing);
        ushort             size = MemoryMarshal.Read<ushort>(dataTrailing[sizeof(ushort)..]);
        ReadOnlySpan<byte> data = dataTrailing.Slice(tagSizeFieldLen, size);

        if (tag != tagConstant)
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
        if (data.Length < Zip32CDRFieldLengths.UncompressedSize)
        {
            return true;
        }

        // Advancing the stream (by reading from it) is possible only when:
        // 1. There is an explicit ask to do that (valid files, corresponding boolean flag(s) set to true).
        // 2. When the size indicates that all the information is available ("slightly invalid files").
        bool readAllFields = size >= Zip64ExtraFieldLengths.MaximumExtraFieldLength;

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
        ReadOnlySpan<byte>        currentBlockSpan,
        out ZipArchiveEntry entry)
    {
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(currentBlockSpan);

        if (signature != Constants.Zip32CDRHeaderMagic)
            throw new InvalidOperationException("Invalid Central Directory signature.");

        ZipCDRBitFlagValues flags = MemoryMarshal.Read<ZipCDRBitFlagValues>(currentBlockSpan[Zip32CDRFieldLocations.GeneralPurposeBitFlags..]);

        ushort compressionType = MemoryMarshal.Read<ushort>(currentBlockSpan[Zip32CDRFieldLocations.CompressionMethod..]);
        uint   lastModified    = MemoryMarshal.Read<uint>(currentBlockSpan[Zip32CDRFieldLocations.LastModified..]);
        uint   crc32           = MemoryMarshal.Read<uint>(currentBlockSpan[Zip32CDRFieldLocations.Crc32..]);
        ushort fileNameLen     = MemoryMarshal.Read<ushort>(currentBlockSpan[Zip32CDRFieldLocations.FilenameLength..]);
        ushort extraFieldLen   = MemoryMarshal.Read<ushort>(currentBlockSpan[Zip32CDRFieldLocations.ExtraFieldLength..]);
        ushort fileCommentLen  = MemoryMarshal.Read<ushort>(currentBlockSpan[Zip32CDRFieldLocations.FileCommentLength..]);

        if (flags.HasFlag(ZipCDRBitFlagValues.IsEncrypted))
        {
            throw new NotSupportedException("Encrypted archive is currently not supported.");
        }

        if (compressionType is not (0 or 8 or 9))
        {
            throw new NotSupportedException("Compression is not supported. It must be either Store, Deflate or Deflate64");
        }

        long compressedSize   = MemoryMarshal.Read<uint>(currentBlockSpan[Zip32CDRFieldLocations.CompressedSize..]);
        long uncompressedSize = MemoryMarshal.Read<uint>(currentBlockSpan[Zip32CDRFieldLocations.UncompressedSize..]);

        long relativeOffsetOfLocalHeader = MemoryMarshal.Read<uint>(currentBlockSpan[Zip32CDRFieldLocations.RelativeOffsetOfLocalHeader..]);
        ReadOnlySpan<byte> dynamicRecord = currentBlockSpan[Zip32CDRFieldLocations.DynamicData..];

        ReadOnlySpan<byte> fileNameSpan    = dynamicRecord[..fileNameLen];
        ReadOnlySpan<byte> extraFieldSpan  = dynamicRecord.Slice(fileNameLen, extraFieldLen);
        ReadOnlySpan<byte> fileCommentSpan = dynamicRecord.Slice(fileNameLen + extraFieldLen, fileCommentLen);

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

        string fileName    = Encoding.UTF8.GetString(fileNameSpan);
        bool   isDeflate64 = compressedSize == Constants.Zip64Mask || uncompressedSize == Constants.Zip64Mask;
        entry = new ZipArchiveEntry
        {
            Comment                    = fileComment,
            Filename                   = fileName,
            Crc32                      = crc32,
            Flags                      = flags,
            IsDeflate64                = isDeflate64,
            LastModified               = lastModified.DosTimeToDateTime(),
            LocalBlockOffsetFromStream = relativeOffsetOfLocalHeader,
            Size                       = uncompressedSize,
            SizeCompressed             = compressedSize,
            IsDeflate                  = compressionType is 8 or 9
        };

        int endOfBlock = Zip32CDRFieldLocations.DynamicData + fileNameLen + extraFieldLen + fileCommentLen;
        return currentBlockSpan[endOfBlock..];
    }
}
