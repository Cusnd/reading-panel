namespace ReadingPanel.Core;

// Share the producer until its last interested caller leaves. Only successful results enter the FIFO.
internal sealed class AsyncLookupCache<TKey, TValue>(int capacity, Func<TValue, bool>? cacheable = null) where TKey : notnull
{
    private sealed class Flight
    {
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource<TValue> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Readers;
        internal bool Finished;
        internal Task Cancellation = Task.CompletedTask;
    }

    private readonly object gate = new();
    private readonly Dictionary<TKey, TValue> values = new();
    private readonly LinkedList<TKey> order = new();
    private readonly Dictionary<TKey, Flight> flights = new();

    internal Task<TValue> GetAsync(TKey key, Func<CancellationToken, Task<TValue>> fetch, CancellationToken token, bool refresh = false)
    {
        token.ThrowIfCancellationRequested();
        Flight flight;
        bool start;
        lock (gate)
        {
            start = !flights.TryGetValue(key, out var existing);
            if (start && !refresh && values.TryGetValue(key, out var hit)) return Task.FromResult(hit);
            if (refresh && values.Remove(key)) order.Remove(key);
            flight = existing ?? new();
            if (start) flights.Add(key, flight);
            flight.Readers++;
        }
        int left = 0;
        var registration = token.Register(Leave);
        if (start) _ = ProduceAsync();
        return WaitAsync();

        void Leave()
        {
            if (Interlocked.Exchange(ref left, 1) != 0) return;
            lock (gate)
            {
                if (--flight.Readers != 0 || flight.Finished) return;
                flights.Remove(key);
                flight.Cancellation = flight.Stop.CancelAsync();
            }
        }

        async Task<TValue> WaitAsync()
        {
            using (registration)
            {
                try { return await flight.Result.Task.WaitAsync(token).ConfigureAwait(false); }
                finally { if (token.IsCancellationRequested) Leave(); }
            }
        }

        async Task ProduceAsync()
        {
            try
            {
                var value = await fetch(flight.Stop.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (!flight.Stop.IsCancellationRequested && (cacheable == null || cacheable(value)))
                    {
                        if (!values.ContainsKey(key))
                        {
                            if (order.Count == capacity) { values.Remove(order.First!.Value); order.RemoveFirst(); }
                            order.AddLast(key);
                        }
                        values[key] = value;
                    }
                    Finish();
                    flight.Result.TrySetResult(value);
                }
            }
            catch (Exception error)
            {
                lock (gate)
                {
                    Finish();
                    if (error is OperationCanceledException cancelled) flight.Result.TrySetCanceled(cancelled.CancellationToken);
                    else { flight.Result.TrySetException(error); _ = flight.Result.Task.Exception; }
                }
            }
        }

        async Task DisposeAfterCancelAsync()
        {
            try { await flight.Cancellation.ConfigureAwait(false); }
            finally { flight.Stop.Dispose(); }
        }

        void Finish()
        {
            flight.Finished = true;
            if (flights.GetValueOrDefault(key) == flight) flights.Remove(key);
            if (flight.Cancellation.IsCompleted) flight.Stop.Dispose();
            else _ = DisposeAfterCancelAsync();
        }
    }
}
