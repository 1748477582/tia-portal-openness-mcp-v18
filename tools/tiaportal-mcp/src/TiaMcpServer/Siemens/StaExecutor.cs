using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Owns a single STA thread and pumps a SynchronizationContext so that every
    /// Siemens.Engineering (TIA Portal Openness) COM call executes on one
    /// apartment-consistent thread.
    ///
    /// Why this exists: Openness requires the calling thread to belong to a
    /// Single-Threaded Apartment (STA). The MCP server's request threads are MTA
    /// (thread-pool), and the previous code offloaded Portal calls with
    /// `Task.Run(() => Portal.X())`, moving them onto *different* MTA pool
    /// threads. That violates the COM contract and, under concurrency / long
    /// sessions, produces intermittent RPC_E_WRONG_THREAD / hangs / TIA Portal
    /// process stalls. Routing every Portal.* access through this single STA
    /// thread eliminates the cross-apartment access.
    ///
    /// Usage: serialize all Openness access through <see cref="Run{T}"/> /
    /// <see cref="RunAsync{T}"/>. The TiaPortal instance is created and touched
    /// exclusively on this thread, so _portal stays apartment-consistent.
    /// </summary>
    public sealed class StaExecutor : IDisposable
    {
        private readonly Thread _thread;
        private readonly BlockingCollection<WorkItem> _queue = new BlockingCollection<WorkItem>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _disposed;

        public StaExecutor()
        {
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "PortalSta"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Loop()
        {
            // Install a SynchronizationContext so any Openness-internal
            // SynchronizationContext.Send/Post marshals back onto this STA thread.
            SynchronizationContext.SetSynchronizationContext(new StaSynchronizationContext(_queue));
            while (!_cts.IsCancellationRequested)
            {
                WorkItem item;
                try { item = _queue.Take(_cts.Token); }
                catch (OperationCanceledException) { break; }
                item.Run();
            }
        }

        /// <summary>Runs <paramref name="func"/> on the STA thread, blocking the caller until done.</summary>
        public T Run<T>(Func<T> func)
        {
            if (Thread.CurrentThread == _thread) return func();
            var item = new WorkItem(() => func());
            _queue.Add(item);
            item.Completed.Wait();
            if (item.Exception != null) throw item.Exception;
            return (T)(item.Result ?? default(T))!;
        }

        /// <summary>Runs <paramref name="action"/> on the STA thread, blocking the caller until done.</summary>
        public void Run(Action action)
        {
            if (Thread.CurrentThread == _thread) { action(); return; }
            var item = new WorkItem(() => { action(); return null; });
            _queue.Add(item);
            item.Completed.Wait();
            if (item.Exception != null) throw item.Exception;
        }

        public Task<T> RunAsync<T>(Func<T> func) => Task.Run(() => Run(func));
        public Task RunAsync(Action action) => Task.Run(() => Run(action));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _thread.Join(2000); } catch { /* best effort */ }
            _queue.Dispose();
            _cts.Dispose();
        }

        private sealed class WorkItem
        {
            private readonly Func<object?> _invoke;
            public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);
            public object? Result;
            public Exception? Exception;

            public WorkItem(Func<object?> invoke) => _invoke = invoke;

            public void Run()
            {
                try { Result = _invoke(); }
                catch (Exception ex) { Exception = ex; }
                finally { Completed.Set(); }
            }
        }

        /// <summary>
        /// Minimal SynchronizationContext that queues work onto the STA thread's
        /// BlockingCollection. Send runs synchronously (already on STA thread or
        /// via the queue + wait); Post queues fire-and-forget.
        /// </summary>
        private sealed class StaSynchronizationContext : SynchronizationContext
        {
            private readonly BlockingCollection<WorkItem> _q;
            public StaSynchronizationContext(BlockingCollection<WorkItem> q) => _q = q;

            public override void Send(SendOrPostCallback d, object? state)
            {
                if (Thread.CurrentThread.Name == "PortalSta")
                {
                    d(state);
                    return;
                }
                var done = new ManualResetEventSlim(false);
                Exception? ex = null;
                _q.Add(new WorkItem(() => { try { d(state); } catch (Exception e) { ex = e; } finally { done.Set(); } return null; }));
                done.Wait();
                if (ex != null) throw ex;
            }

            public override void Post(SendOrPostCallback d, object? state)
            {
                _q.Add(new WorkItem(() => { try { d(state); } catch { /* swallow posted callbacks */ } return null; }));
            }

            public override SynchronizationContext CreateCopy() => this;
        }
    }
}
