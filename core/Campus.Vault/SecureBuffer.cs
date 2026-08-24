using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Campus.Vault;

/// <summary>
/// A byte buffer that is pinned for its lifetime and zeroed on dispose, so key material is not
/// left behind by the GC compacting the heap. This raises the cost of recovering a key from a
/// memory dump; it does not make it impossible for code running as the same user.
/// </summary>
public sealed class SecureBuffer : IDisposable
{
    private byte[]? _bytes;
    private GCHandle _handle;

    public SecureBuffer(int length)
    {
        _bytes = new byte[length];
        _handle = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
    }

    public SecureBuffer(ReadOnlySpan<byte> source) : this(source.Length)
        => source.CopyTo(_bytes!);

    public int Length => _bytes?.Length ?? 0;

    public bool IsDisposed => _bytes is null;

    public ReadOnlySpan<byte> Span
        => _bytes ?? throw new ObjectDisposedException(nameof(SecureBuffer));

    /// <summary>Writable view, for filling the buffer in place. Avoid copying the contents out.</summary>
    public Span<byte> WritableSpan
        => _bytes ?? throw new ObjectDisposedException(nameof(SecureBuffer));

    public static SecureBuffer Random(int length)
    {
        var buffer = new SecureBuffer(length);
        RandomNumberGenerator.Fill(buffer.WritableSpan);
        return buffer;
    }

    public SecureBuffer Copy() => new(Span);

    public void Dispose()
    {
        if (_bytes is null) return;
        CryptographicOperations.ZeroMemory(_bytes);
        if (_handle.IsAllocated) _handle.Free();
        _bytes = null;
    }
}
