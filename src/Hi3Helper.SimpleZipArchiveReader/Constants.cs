// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

namespace Hi3Helper.SimpleZipArchiveReader;

internal static class Constants
{
    internal const int EOCDBufferLength = 128 << 10;

    internal const uint Zip32EOCDRHeaderMagic = 0x06054b50;
    internal const uint Zip64EOCDRHeaderMagic = 0x06064b50;

    internal const uint Zip32CDRHeaderMagic   = 0x02014b50;
    internal const uint Zip32LocalHeaderMagic = 0x04034b50;

    internal const uint Zip64Mask             = 0xffffffff;
}

internal static class Zip64ExtraFieldLengths
{
    public const int UncompressedSize  = sizeof(long);
    public const int CompressedSize    = sizeof(long);
    public const int LocalHeaderOffset = sizeof(long);
    public const int StartDiskNumber   = sizeof(int);

    public const int MaximumExtraFieldLength = UncompressedSize +
                                               CompressedSize +
                                               LocalHeaderOffset +
                                               StartDiskNumber;
}

internal static class Zip32CDRFieldLengths
{
    // Must match the signature constant bytes length, but should stay a const int or sometimes
    // static initialization of Zip32CDRFieldLengths and NullReferenceException occurs.
    public const int Signature                   = 4;
    public const int VersionMadeBySpecification  = sizeof(byte);
    public const int VersionMadeByCompatibility  = sizeof(byte);
    public const int VersionNeededToExtract      = sizeof(ushort);
    public const int GeneralPurposeBitFlags      = sizeof(ushort);
    public const int CompressionMethod           = sizeof(ushort);
    public const int LastModified                = sizeof(ushort) + sizeof(ushort);
    public const int Crc32                       = sizeof(uint);
    public const int CompressedSize              = sizeof(uint);
    public const int UncompressedSize            = sizeof(uint);
    public const int FilenameLength              = sizeof(ushort);
    public const int ExtraFieldLength            = sizeof(ushort);
    public const int FileCommentLength           = sizeof(ushort);
    public const int DiskNumberStart             = sizeof(ushort);
    public const int InternalFileAttributes      = sizeof(ushort);
    public const int ExternalFileAttributes      = sizeof(uint);
    public const int RelativeOffsetOfLocalHeader = sizeof(uint);
}

internal static class Zip32CDRFieldLocations
{
    public const int Signature = 0;
    public const int VersionMadeBySpecification = Signature + Zip32CDRFieldLengths.Signature;
    public const int VersionMadeByCompatibility = VersionMadeBySpecification + Zip32CDRFieldLengths.VersionMadeBySpecification;
    public const int VersionNeededToExtract = VersionMadeByCompatibility + Zip32CDRFieldLengths.VersionMadeByCompatibility;
    public const int GeneralPurposeBitFlags = VersionNeededToExtract + Zip32CDRFieldLengths.VersionNeededToExtract;
    public const int CompressionMethod = GeneralPurposeBitFlags + Zip32CDRFieldLengths.GeneralPurposeBitFlags;
    public const int LastModified = CompressionMethod + Zip32CDRFieldLengths.CompressionMethod;
    public const int Crc32 = LastModified + Zip32CDRFieldLengths.LastModified;
    public const int CompressedSize = Crc32 + Zip32CDRFieldLengths.Crc32;
    public const int UncompressedSize = CompressedSize + Zip32CDRFieldLengths.CompressedSize;
    public const int FilenameLength = UncompressedSize + Zip32CDRFieldLengths.UncompressedSize;
    public const int ExtraFieldLength = FilenameLength + Zip32CDRFieldLengths.FilenameLength;
    public const int FileCommentLength = ExtraFieldLength + Zip32CDRFieldLengths.ExtraFieldLength;
    public const int DiskNumberStart = FileCommentLength + Zip32CDRFieldLengths.FileCommentLength;
    public const int InternalFileAttributes = DiskNumberStart + Zip32CDRFieldLengths.DiskNumberStart;
    public const int ExternalFileAttributes = InternalFileAttributes + Zip32CDRFieldLengths.InternalFileAttributes;
    public const int RelativeOffsetOfLocalHeader = ExternalFileAttributes + Zip32CDRFieldLengths.ExternalFileAttributes;
    public const int DynamicData = RelativeOffsetOfLocalHeader + Zip32CDRFieldLengths.RelativeOffsetOfLocalHeader;
}