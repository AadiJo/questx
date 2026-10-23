using System;
using System.Threading;

namespace QuestX.Utils
{
    /// <summary>
    /// Runs items on one dedicated background thread. At most one item waits: posting while one
    /// is pending replaces it, so a consumer slower than the camera always gets the newest frame
    /// instead of a growing backlog. Replaced and abandoned items go to the discard callback on
    /// the thread that dropped them, so their owner can reclaim them.
    /// </summary>
    public sealed class LatestOnlyWorker<T>
        where T : class
    {
        private readonly object sync = new object();
        private readonly Action<T> process;
        private readonly Action<T> discard;
        private readonly Thread thread;
        private T pending;
        private bool stopping;

        public LatestOnlyWorker(string name, Action<T> process, Action<T> discard)
        {
            this.process = process ?? throw new ArgumentNullException(nameof(process));
            this.discard = discard ?? throw new ArgumentNullException(nameof(discard));
            thread = new Thread(Run) { Name = name, IsBackground = true };
            thread.Start();
        }

        public string Name => thread.Name;

        /// <summary>Queues <paramref name="item"/>, discarding any item still waiting.</summary>
        public void Post(T item)
        {
            T dropped;
            lock (sync)
            {
                if (stopping)
                {
                    dropped = item;
                }
                else
                {
                    dropped = pending;
                    pending = item;
                    Monitor.Pulse(sync);
                }
            }
            if (dropped != null)
            {
                discard(dropped);
            }
        }

        /// <summary>
        /// Discards the waiting item and stops after the current one. Returns false if the
        /// thread is still running after <paramref name="timeout"/>.
        /// </summary>
        public bool Stop(TimeSpan timeout)
        {
            T dropped;
            lock (sync)
            {
                stopping = true;
                dropped = pending;
                pending = null;
                Monitor.Pulse(sync);
            }
            if (dropped != null)
            {
                discard(dropped);
            }
            return thread.Join(timeout);
        }

        private void Run()
        {
            while (true)
            {
                T item;
                lock (sync)
                {
                    while (pending == null && !stopping)
                    {
                        Monitor.Wait(sync);
                    }
                    if (stopping)
                    {
                        return;
                    }
                    item = pending;
                    pending = null;
                }

                try
                {
                    process(item);
                }
                catch (Exception ex)
                {
                    // process owns its error handling; this only keeps the thread alive.
                    QueuedLogger.LogError($"{thread.Name} failed: {ex.Message}");
                }
            }
        }
    }
}
