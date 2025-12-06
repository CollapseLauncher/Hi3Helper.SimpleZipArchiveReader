using System;

namespace Hi3Helper.SimpleZipArchiveReader;

[Flags]
public enum ZipCdrBitFlagValues : ushort
{
    IsEncrypted               = 0x1,
    DataDescriptor            = 0x8,
    UnicodeFileNameAndComment = 0x800
}

public enum ZipCompressionTypes : ushort
{
    Store     = 0,
    Deflate   = 8,
    Deflate64 = 9
}