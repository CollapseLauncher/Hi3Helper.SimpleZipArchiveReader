// ReSharper disable InconsistentNaming
#pragma warning disable IDE0130
namespace System;

internal static class SR
{
    public const string CannotWriteToDeflateStream = "Writing to the compression stream is not supported.";
    public const string GenericInvalidData = "Found invalid data while decoding.";
    public const string InvalidBeginCall = "Only one asynchronous reader or writer is allowed time at one time.";
    public const string InvalidBlockLength = "Block length does not match with its complement.";
    public const string InvalidHuffmanData = "Failed to construct a huffman tree using the length array. The stream might be corrupted.";
    public const string NotSupported_UnreadableStream = "Stream does not support reading.";
    public const string UnknownBlockType = "Unknown block type. Stream might be corrupted.";
    public const string UnknownState = "Decoder is in some unknown state. This might be caused by corrupted data.";
}
