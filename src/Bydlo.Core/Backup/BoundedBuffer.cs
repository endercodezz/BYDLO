using System.Buffers;
using System.Security.Cryptography;

namespace Bydlo.Core.Backup;

/// <summary>
/// Pooled <see cref="IBufferWriter{T}"/> with a reserved prefix and a hard size limit (issue #394).
/// The limit is enforced while writing, so an oversized export fails before the result grows
/// without bound. Every buffer is cleared before it returns to the pool because it may hold plaintext.
/// </summary>
internal sealed class BoundedBuffer : IBufferWriter<byte>, IDisposable
{
    private readonly int _limit;
    private byte[] _buffer;
    private int _written;

    public BoundedBuffer(int prefix, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, prefix);
        _limit = limit;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(prefix + 4096, 64 * 1024), limit));
        _written = prefix;
    }

    /// <summary>Prefix plus everything written so far.</summary>
    public int WrittenCount => _written;

    /// <summary>Backing array; only the first <see cref="WrittenCount"/> bytes are meaningful.</summary>
    public byte[] Array => _buffer;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if ((long)_written + count > _buffer.Length) { throw new InvalidOperationException("Advanced past the buffer."); }
        if ((long)_written + count > _limit) { throw new InvalidDataException("Backup is too large."); }
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsMemory(_written); }

    public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsSpan(_written); }

    private void Ensure(int sizeHint)
    {
        if (_written >= _limit) { throw new InvalidDataException("Backup is too large."); }
        var needed = (long)_written + Math.Max(sizeHint, 1);
        if (needed <= _buffer.Length) { return; }
        // Writers over-request for worst-case escaping, so the buffer may exceed the limit by one
        // hint; Advance still rejects any data past the limit. The hint itself stays bounded.
        if (needed > (long)_limit * 8) { throw new InvalidDataException("Backup is too large."); }
        var size = (int)Math.Min(Math.Max(needed, (long)_buffer.Length * 2), Math.Max(needed, _limit));
        var next = ArrayPool<byte>.Shared.Rent(size);
        _buffer.AsSpan(0, _written).CopyTo(next);
        CryptographicOperations.ZeroMemory(_buffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    public void Dispose()
    {
        if (_buffer.Length == 0) { return; }
        // Clear the whole array: a writer may have produced bytes it never advanced over.
        CryptographicOperations.ZeroMemory(_buffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _written = 0;
    }
}
