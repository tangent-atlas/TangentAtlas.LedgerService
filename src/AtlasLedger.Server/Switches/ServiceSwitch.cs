// <copyright file="ServiceSwitch.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Switches
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Turns one of the server's optional services on for a while, or off.  A service that is off does no work, so the
    /// server and its database can idle; one that is on turns itself off when its time runs out.
    /// </summary>
    /// <remarks>
    /// Every switch starts off, so a restart always lands in the cheap state.  The state is kept in memory only.
    /// </remarks>
    public sealed class ServiceSwitch
    {
        private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMilliseconds(50);

        private readonly TimeProvider timeProvider;

        private readonly object syncRoot = new object();

        private DateTimeOffset? onUntil;

        private TaskCompletionSource changed = NewSignal();

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceSwitch"/> class, off.
        /// </summary>
        /// <param name="name">The service's name in the API, for example <c>market-feed</c>.</param>
        /// <param name="description">What the service does, for people.</param>
        /// <param name="timeProvider">The clock.</param>
        public ServiceSwitch(string name, string description, TimeProvider timeProvider)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentNullException.ThrowIfNull(timeProvider);
            this.Name = name;
            this.Description = description ?? string.Empty;
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// Gets the service's name in the API.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets what the service does.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets a value indicating whether the service is on now.
        /// </summary>
        public bool IsOn => this.OnUntil is not null;

        /// <summary>
        /// Gets when the service turns itself off, or null if it is off.
        /// </summary>
        public DateTimeOffset? OnUntil
        {
            get
            {
                lock (this.syncRoot)
                {
                    return this.onUntil is { } until && until > this.timeProvider.GetUtcNow() ? until : null;
                }
            }
        }

        /// <summary>
        /// Turns the service on until a time from now, replacing any earlier time: turning on again extends or shortens it.
        /// </summary>
        /// <param name="duration">How long to stay on.</param>
        public void TurnOn(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
            this.Set(this.timeProvider.GetUtcNow() + duration);
        }

        /// <summary>
        /// Turns the service off now.
        /// </summary>
        public void TurnOff() => this.Set(null);

        /// <summary>
        /// Waits until the switch is turned on or off, or, while it is on, until its time runs out.
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>A task that completes at the next change.</returns>
        public async Task WaitForChangeAsync(CancellationToken cancellationToken)
        {
            Task signal;
            DateTimeOffset? until;
            lock (this.syncRoot)
            {
                signal = this.changed.Task;
                until = this.onUntil;
            }

            // Off, or on with its time already run out (which is off): only a turn on or off is a change.
            var remaining = until is { } time ? time - this.timeProvider.GetUtcNow() : Timeout.InfiniteTimeSpan;
            if (remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero)
            {
                remaining = Timeout.InfiniteTimeSpan;
            }
            else if (remaining != Timeout.InfiniteTimeSpan)
            {
                // Timers may fire a little early: wake just after the end, when the switch already reads off.
                remaining += ExpiryMargin;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var expiry = Task.Delay(remaining, this.timeProvider, linked.Token);
            await Task.WhenAny(signal, expiry).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        private static TaskCompletionSource NewSignal() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Set(DateTimeOffset? until)
        {
            TaskCompletionSource previous;
            lock (this.syncRoot)
            {
                this.onUntil = until;
                previous = this.changed;
                this.changed = NewSignal();
            }

            previous.TrySetResult();
        }
    }
}
