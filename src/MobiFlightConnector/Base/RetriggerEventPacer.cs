using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MobiFlight.Execution
{
    /// <summary>
    /// Sits between the input devices and the input event executors and handles the
    /// burst of events that a retrigger produces.
    ///
    /// A retrigger makes every board and joystick report the current state of all of
    /// its buttons at once, which can be hundreds of events within a few milliseconds.
    /// Some simulator add-ons (or the WASM module) cannot keep up with that.
    ///
    /// While a retrigger is in progress, every event is tagged with
    /// <see cref="InputEventArgs.IsRetrigger"/>, so configs can opt out of it, and,
    /// when a delay is configured, events are queued and executed one after the other
    /// with that delay in between, in the order they arrived.
    ///
    /// The retrigger window closes once no button event arrived for
    /// <see cref="QuietPeriodMs"/>, or after <see cref="MaxWindowMs"/> at the latest.
    /// Events arriving after that are no longer tagged, but still wait for the queue
    /// to be worked off so the order is kept. The delay only applies after events that
    /// actually executed an action. Outside a retrigger, events are executed
    /// immediately, exactly as before.
    /// </summary>
    public class RetriggerEventPacer
    {
        public const int QuietPeriodMs = 500;
        public const int MaxWindowMs = 5000;
        public const int MaxDelayMs = 1000;

        private readonly Func<InputEventArgs, bool> _execute;
        private readonly Func<int> _getDelayMs;
        private readonly Func<DateTime> _now;
        private readonly Func<int, CancellationToken, Task> _delay;

        private readonly Lock _lock = new();
        private readonly Queue<InputEventArgs> _queue = new Queue<InputEventArgs>();
        private CancellationTokenSource _cancellation = new CancellationTokenSource();
        private bool _windowOpen;
        private DateTime _windowStart;
        private DateTime _lastButtonEvent;
        private Task _worker = Task.CompletedTask;
        private bool _workerRunning;

        /// <param name="execute">Executes a single input event and returns whether any action was executed.</param>
        /// <param name="getDelayMs">Returns the delay between two events during a retrigger, read for every event so a changed setting applies immediately. 0 disables pacing.</param>
        /// <param name="now">Clock, injectable for tests.</param>
        /// <param name="delay">Delay implementation, injectable for tests.</param>
        public RetriggerEventPacer(
            Func<InputEventArgs, bool> execute,
            Func<int> getDelayMs,
            Func<DateTime> now = null,
            Func<int, CancellationToken, Task> delay = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = execute;
            _getDelayMs = getDelayMs ?? (() => 0);
            _now = now ?? (() => DateTime.Now);
            _delay = delay ?? ((ms, token) => Task.Delay(ms, token));
        }

        /// <summary>True from <see cref="BeginRetrigger"/> until the window closed and the queue is empty.</summary>
        public bool IsRetriggerActive
        {
            get
            {
                lock (_lock)
                {
                    return IsBusy() || IsWindowOpen(_now());
                }
            }
        }

        /// <summary>The task working off the queue. Only meant for tests.</summary>
        internal Task Worker
        {
            get
            {
                lock (_lock)
                {
                    return _worker;
                }
            }
        }

        /// <summary>Starts a retrigger window. Call this right before the devices are asked to retrigger.</summary>
        public void BeginRetrigger()
        {
            lock (_lock)
            {
                _windowOpen = true;
                _windowStart = _now();
                _lastButtonEvent = _windowStart;
            }

            Log.Instance.log($"Retrigger started (delay between events: {ClampDelay(_getDelayMs())}ms).", LogSeverity.Debug);
        }

        /// <summary>Drops all queued events and ends the retrigger window, e.g. when execution is stopped.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _cancellation.Cancel();
                _cancellation = new CancellationTokenSource();
                _queue.Clear();
                _windowOpen = false;
                // a cancelled worker exits on its own; it no longer counts as running
                _workerRunning = false;
            }
        }

        /// <summary>Executes the event now, or queues it if a paced retrigger is in progress.</summary>
        public void Handle(InputEventArgs e)
        {
            if (e is null) return;

            lock (_lock)
            {
                var now = _now();
                var inWindow = IsWindowOpen(now);
                if (!inWindow)
                {
                    _windowOpen = false;
                }
                else
                {
                    e.IsRetrigger = true;

                    // Only button events count as retrigger activity. A noisy axis
                    // must not keep the window open; it is still capped by MaxWindowMs.
                    if (e.InputType == DeviceType.Button)
                    {
                        _lastButtonEvent = now;
                    }
                }

                // While events are queued, later ones have to wait behind them, even
                // after the window closed, so that a button's PRESS never overtakes
                // its own queued RELEASE.
                var mustQueue = IsBusy() || (inWindow && ClampDelay(_getDelayMs()) > 0);
                if (mustQueue)
                {
                    _queue.Enqueue(e);
                    if (!_workerRunning)
                    {
                        _workerRunning = true;
                        var token = _cancellation.Token;
                        _worker = Task.Run(() => ProcessQueueAsync(token));
                    }
                    return;
                }
            }

            _execute(e);
        }

        private bool IsBusy()
        {
            return _workerRunning || _queue.Count > 0;
        }

        private bool IsWindowOpen(DateTime now)
        {
            if (!_windowOpen) return false;

            var quiet = (now - _lastButtonEvent).TotalMilliseconds >= QuietPeriodMs;
            var expired = (now - _windowStart).TotalMilliseconds >= MaxWindowMs;
            return !quiet && !expired;
        }

        private async Task ProcessQueueAsync(CancellationToken token)
        {
            while (true)
            {
                InputEventArgs next;
                lock (_lock)
                {
                    // after Reset() a newer worker may already own the queue
                    if (token.IsCancellationRequested) return;

                    if (_queue.Count == 0)
                    {
                        _workerRunning = false;
                        return;
                    }
                    next = _queue.Dequeue();
                }

                var executedAction = false;
                try
                {
                    executedAction = _execute(next);
                }
                catch (Exception ex)
                {
                    Log.Instance.log($"Error during paced retrigger execution: {ex.Message} - {next.GetMsgEventLabel()}", LogSeverity.Error);
                }

                // events that were skipped or have nothing bound to them sent nothing,
                // so there is nothing to space out
                var delayMs = ClampDelay(_getDelayMs());
                if (!executedAction || delayMs <= 0) continue;

                try
                {
                    await _delay(delayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static int ClampDelay(int delayMs)
        {
            if (delayMs < 0) return 0;
            return Math.Min(delayMs, MaxDelayMs);
        }
    }
}
