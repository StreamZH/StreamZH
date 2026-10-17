// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Usenet.Nntp;

namespace StreamZH.Usenet.Pool;

/// <summary>
/// A connection handed out by <see cref="ServerPool"/>. With one article per request it serves one fetch at a time.
/// With more (pipelining) several fetches share it: each sends its BODY command as soon as it gets the connection, and
/// the answers are read strictly in sending order (every fetch waits for the one before it to finish reading).
/// </summary>
internal sealed class PooledConnection(NntpConnection connection, int articlesPerRequest)
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private Task _lastRead = Task.CompletedTask;

    public NntpConnection Connection { get; } = connection;

    /// <summary>Fetches using the connection right now. Guarded by the pool's lock.</summary>
    public int Users { get; set; }

    /// <summary>Guarded by the pool's lock.</summary>
    public DateTimeOffset LastUsed { get; set; }

    /// <summary>A fetch gave it back as not reusable: no new fetches, closed once the last one is done. Guarded by the pool's lock.</summary>
    public bool Discarded { get; set; }

    /// <summary>BODY on this connection; <paramref name="received"/> gets the bytes read for it (also when it fails).</summary>
    public async Task<DecodedArticle> BodyAsync(string messageId, Action<long> received, CancellationToken cancellationToken)
    {
        if (articlesPerRequest <= 1)
        {
            var before = Connection.BytesReceived;
            try
            {
                return await Connection.BodyAsync(messageId, cancellationToken);
            }
            finally
            {
                received(Connection.BytesReceived - before);
            }
        }

        // Completes once this fetch has read its answer (or given up) and every fetch before it has, too.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _send.WaitAsync(cancellationToken);
        var turn = _lastRead;
        _lastRead = done.Task;
        try
        {
            await Connection.SendBodyAsync(messageId, cancellationToken);
        }
        catch
        {
            CompleteAfter(turn, done);
            throw;
        }
        finally
        {
            _send.Release();
        }

        try
        {
            try
            {
                await turn.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The answer to our BODY will arrive but nobody reads it: the answers after it would be out of step.
                Connection.MarkBroken();
                throw;
            }

            var before = Connection.BytesReceived;
            try
            {
                return await Connection.ReceiveBodyAsync(messageId, cancellationToken);
            }
            finally
            {
                received(Connection.BytesReceived - before);
            }
        }
        finally
        {
            CompleteAfter(turn, done);
        }
    }

    private static void CompleteAfter(Task turn, TaskCompletionSource done)
    {
        if (turn.IsCompleted)
        {
            done.TrySetResult();
        }
        else
        {
            turn.ContinueWith(_ => done.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
