namespace Campus.Vault;

/// <summary>
/// A seekable, read-only view over an encrypted vault object. Viewers get an ordinary
/// <see cref="Stream"/> and never see the container format or hold the whole file in memory.
/// </summary>
public sealed class VaultReadStream : Stream
{
    private readonly Stream _source;
    private readonly SecureBuffer _objectKey;
    private readonly string _contentHash;
    private readonly ChunkedVaultFile.FileHeader _header;
    private readonly byte[] _chunk;
    private int _cachedChunkIndex = -1;
    private int _cachedChunkLength;
    private long _position;

    internal VaultReadStream(Stream source, SecureBuffer objectKey, string contentHash)
    {
        _source = source;
        _objectKey = objectKey;
        _contentHash = contentHash;
        _header = ChunkedVaultFile.ReadHeader(source);
        _chunk = new byte[_header.ChunkSize];
    }

    public override bool CanRead => true;
    public override bool CanSeek => _source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _header.PlaintextLength;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _header.PlaintextLength || buffer.Length == 0) return 0;

        var total = 0;
        while (total < buffer.Length && _position < _header.PlaintextLength)
        {
            var chunkIndex = (int)(_position / _header.ChunkSize);
            var offsetInChunk = (int)(_position % _header.ChunkSize);

            if (chunkIndex != _cachedChunkIndex)
            {
                _cachedChunkLength = ChunkedVaultFile.ReadChunk(
                    _source, _header, _objectKey, _contentHash, chunkIndex, _chunk);
                _cachedChunkIndex = chunkIndex;
            }

            var available = _cachedChunkLength - offsetInChunk;
            if (available <= 0) break;

            var take = Math.Min(available, buffer.Length - total);
            _chunk.AsSpan(offsetInChunk, take).CopyTo(buffer[total..]);
            total += take;
            _position += take;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _header.PlaintextLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("Cannot seek before the start of the stream.");
        _position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Array.Clear(_chunk);
            _objectKey.Dispose();
            _source.Dispose();
        }
        base.Dispose(disposing);
    }
}
