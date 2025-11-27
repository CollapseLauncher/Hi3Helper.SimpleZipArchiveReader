using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.SimpleZipArchiveReader;

public sealed partial class ZipArchiveEntry
{
    private sealed class SequentialReadSubStream(Stream stream, long size) : Stream
    {
        private          long _remainedToRead = size;

        public override int Read(Span<byte> buffer)
        {
            if (_remainedToRead == 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(_remainedToRead, buffer.Length);
            int read = stream.Read(buffer[..toRead]);

            _remainedToRead -= read;

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_remainedToRead == 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(_remainedToRead, buffer.Length);
            int read = await stream.ReadAsync(buffer[..toRead], token);

            _remainedToRead -= read;

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            await ReadAsync(buffer.AsMemory(offset, count), token);

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            return Task.FromException(new NotSupportedException());
        }

        public override bool CanRead => stream.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override void Flush() => stream.Flush();

        public override long Length { get; } = size;

        public override long Position
        {
            get => Length - _remainedToRead;
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            stream.Dispose();
        }

        public override ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
