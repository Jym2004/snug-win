using System;
using System.Threading.Tasks;

namespace SnugWin.Core.Support;

public sealed class SerialLane
{
    private Task _tail = Task.CompletedTask;
    private int _queuedCoalesced;

    public Task Run(Func<Task> operation)
    {
        var previous = _tail;
        var next = ExecuteAsync(previous, operation);
        _tail = next;
        return next;
    }

    public Task RunCoalesced(Func<Task> operation)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _queuedCoalesced, 1, 0) != 0)
            return _tail;

        return Run(async () =>
        {
            System.Threading.Interlocked.Exchange(ref _queuedCoalesced, 0);
            await operation().ConfigureAwait(false);
        });
    }

    public async Task DrainAsync()
    {
        while (true)
        {
            var current = _tail;
            await current.ConfigureAwait(false);
            if (ReferenceEquals(_tail, current)) return;
        }
    }

    private static async Task ExecuteAsync(Task previous, Func<Task> operation)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // previous errors shouldn't break subsequent operations
        }
        await operation().ConfigureAwait(false);
    }
}
