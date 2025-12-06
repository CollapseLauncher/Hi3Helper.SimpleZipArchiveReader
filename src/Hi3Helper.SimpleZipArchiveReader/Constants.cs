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