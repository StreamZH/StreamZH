// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers;

namespace StreamZH.Usenet.Nntp;

/// <summary>A growable byte buffer on <see cref="ArrayPool{T}.Shared"/>. Not thread-safe; dispose to give the array back.</summary>
internal sealed class PooledBuffer(int initialSize) : IDisposable
{
    private byte[] _array = ArrayPool<byte>.Shared.Rent(initialSize);

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Span => _array.AsSpan(0, Length);

    public void Append(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_array.AsSpan(Length));
        Length += data.Length;
    }

    public void Append(byte value)
    {
        Ensure(1);
        _array[Length++] = value;
    }

    public void Dispose()
    {
        if (_array.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_array);
            _array = [];
            Length = 0;
        }
    }

    private void Ensure(int extra)
    {
        if (Length + extra <= _array.Length)
        {
            return;
        }

        var next = ArrayPool<byte>.Shared.Rent(Math.Max(_array.Length * 2, Length + extra));
        _array.AsSpan(0, Length).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_array);
        _array = next;
    }
}
