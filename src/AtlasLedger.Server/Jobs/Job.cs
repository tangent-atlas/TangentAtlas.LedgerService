// <copyright file="Job.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Jobs
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The two kinds of job.
    /// </summary>
    public enum JobKind
    {
        /// <summary>A task: runs to completion, at its schedule's times or when run by hand (<see cref="ITenantJob"/>).</summary>
        Task,

        /// <summary>A service: runs until stopped, while on by hand or inside its schedule's window (<see cref="ITenantService"/>).</summary>
        Service,
    }

    /// <summary>
    /// A task: a job that runs to completion for one tenant.  Write the class, give it a <see cref="JobAttribute"/>, and it
    /// can be scheduled or run by hand: nothing else to register.
    /// </summary>
    public interface ITenantJob
    {
        /// <summary>
        /// Runs the task for a tenant.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="cancellationToken">Cancelled when its schedule is turned off, changed or deleted, or the server
        /// stops: stop at the next safe point.</param>
        /// <returns>What happened, in a few words, for the job's status.</returns>
        Task<string> RunAsync(Guid tenantId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// A service: a job that runs for one tenant until it is stopped, such as a stream of a customer's prices.  If it ends
    /// or fails while it should still be on, it is started again after a pause.
    /// </summary>
    public interface ITenantService
    {
        /// <summary>
        /// Runs the service for a tenant until it is cancelled.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="cancellationToken">Cancelled when it is to stop: turned off, its window over, or the server
        /// stopping.</param>
        /// <returns>A task that completes when the service stops.</returns>
        Task RunAsync(Guid tenantId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Names a job: the key a schedule row refers to it by.  The key is fixed here, not taken from the class, so the class
    /// can be renamed or moved without breaking a stored schedule.
    /// </summary>
    /// <param name="key">The key, such as <c>GenerateOrders</c>.</param>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class JobAttribute(string key) : Attribute
    {
        /// <summary>
        /// Gets the job's key.
        /// </summary>
        public string Key { get; } = key;

        /// <summary>
        /// Gets a value indicating whether the job runs on market days only: its schedule skips the NYSE's holidays.
        /// </summary>
        public bool MarketDaysOnly { get; init; }
    }

    /// <summary>
    /// A job the server knows.  Its kind follows from its class: a task implements <see cref="ITenantJob"/>, a service
    /// <see cref="ITenantService"/>; never both.
    /// </summary>
    public sealed record JobDescriptor
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JobDescriptor"/> class.
        /// </summary>
        /// <param name="key">Its key.</param>
        /// <param name="type">Its class.</param>
        /// <param name="marketDaysOnly">Whether its schedules skip market holidays.</param>
        public JobDescriptor(string key, Type type, bool marketDaysOnly)
        {
            ArgumentNullException.ThrowIfNull(type);
            var isTask = typeof(ITenantJob).IsAssignableFrom(type);
            var isService = typeof(ITenantService).IsAssignableFrom(type);
            if (isTask == isService)
            {
                throw new InvalidOperationException($"The job \"{key}\" ({type.FullName}) must be either a task (ITenantJob) or a service (ITenantService).");
            }

            this.Key = key;
            this.Type = type;
            this.Kind = isService ? JobKind.Service : JobKind.Task;
            this.MarketDaysOnly = marketDaysOnly;
        }

        /// <summary>Gets its key.</summary>
        public string Key { get; }

        /// <summary>Gets its class.</summary>
        public Type Type { get; }

        /// <summary>Gets whether it is a task or a service.</summary>
        public JobKind Kind { get; }

        /// <summary>Gets a value indicating whether its schedules skip market holidays.</summary>
        public bool MarketDaysOnly { get; }
    }

    /// <summary>
    /// The jobs the server knows: every class in its own assembly with a <see cref="JobAttribute"/> that is a task or a
    /// service.  A schedule's job is resolved only among these, never by type name, so data can't choose what code runs.
    /// </summary>
    public sealed class JobRegistry
    {
        private readonly Dictionary<string, JobDescriptor> jobs;

        /// <summary>
        /// Initializes a new instance of the <see cref="JobRegistry"/> class.
        /// </summary>
        /// <param name="jobs">The jobs.</param>
        public JobRegistry(IEnumerable<JobDescriptor> jobs)
        {
            ArgumentNullException.ThrowIfNull(jobs);
            this.jobs = new Dictionary<string, JobDescriptor>(StringComparer.Ordinal);
            foreach (var job in jobs)
            {
                if (!this.jobs.TryAdd(job.Key, job))
                {
                    throw new InvalidOperationException($"Two jobs claim the key \"{job.Key}\": {this.jobs[job.Key].Type.FullName} and {job.Type.FullName}.");
                }
            }
        }

        /// <summary>
        /// Gets the jobs.
        /// </summary>
        public IReadOnlyCollection<JobDescriptor> All => this.jobs.Values;

        /// <summary>
        /// Gets the jobs' keys, in order.
        /// </summary>
        public string Keys => string.Join(", ", this.jobs.Keys.Order(StringComparer.Ordinal));

        /// <summary>
        /// Finds every job in an assembly.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <returns>The registry.</returns>
        public static JobRegistry Discover(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            return new JobRegistry(assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false })
                .Select(type => (Type: type, Attribute: type.GetCustomAttribute<JobAttribute>()))
                .Where(item => item.Attribute is not null)
                .Select(item => new JobDescriptor(item.Attribute!.Key, item.Type, item.Attribute.MarketDaysOnly)));
        }

        /// <summary>
        /// Finds a job by its key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The job, or null if there is none by that key.</returns>
        public JobDescriptor? Find(string key) => this.jobs.GetValueOrDefault(key);
    }

    /// <summary>
    /// The NYSE's full-day holidays, as it publishes them (NYSE Group, December 23, 2025, for 2026 to 2028).  Extend the list
    /// when the next years are announced; a job marked for market days doesn't run on these days.  Early closings (1:00 pm)
    /// are not holidays: a job scheduled after 1:00 pm still runs on them.
    /// </summary>
    public static class MarketCalendar
    {
        /// <summary>
        /// The holidays.
        /// </summary>
        public static readonly IReadOnlySet<DateOnly> Holidays = new HashSet<DateOnly>
        {
            new(2026, 1, 1), new(2026, 1, 19), new(2026, 2, 16), new(2026, 4, 3), new(2026, 5, 25), new(2026, 6, 19), new(2026, 7, 3), new(2026, 9, 7), new(2026, 11, 26), new(2026, 12, 25),
            new(2027, 1, 1), new(2027, 1, 18), new(2027, 2, 15), new(2027, 3, 26), new(2027, 5, 31), new(2027, 6, 18), new(2027, 7, 5), new(2027, 9, 6), new(2027, 11, 25), new(2027, 12, 24),
            new(2028, 1, 17), new(2028, 2, 21), new(2028, 4, 14), new(2028, 5, 29), new(2028, 6, 19), new(2028, 7, 4), new(2028, 9, 4), new(2028, 11, 23), new(2028, 12, 25),
        };

        /// <summary>
        /// The last year the list covers: from that year on, the scheduler warns at start-up that the list needs extending.
        /// </summary>
        public const int LastYear = 2028;
    }
}
