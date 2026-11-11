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
    /// A job that runs for one tenant when a schedule of the tenant's says so.  Write the class, give it a
    /// <see cref="JobAttribute"/>, and it can be scheduled: nothing else to register.
    /// </summary>
    public interface ITenantJob
    {
        /// <summary>
        /// Runs the job for a tenant.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="cancellationToken">Cancelled when the server stops.</param>
        /// <returns>What happened, in a few words, for the job's status.</returns>
        Task<string> RunAsync(Guid tenantId, CancellationToken cancellationToken);
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
    /// A job the server knows.
    /// </summary>
    /// <param name="Key">Its key.</param>
    /// <param name="Type">Its class.</param>
    /// <param name="MarketDaysOnly">Whether it skips market holidays.</param>
    public sealed record JobDescriptor(string Key, Type Type, bool MarketDaysOnly);

    /// <summary>
    /// The jobs the server knows: every class in its own assembly with a <see cref="JobAttribute"/> that is an
    /// <see cref="ITenantJob"/>.  A schedule's job is resolved only among these, never by type name, so data can't choose
    /// what code runs.
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
        /// Gets the jobs, by key.
        /// </summary>
        public IReadOnlyCollection<JobDescriptor> All => this.jobs.Values;

        /// <summary>
        /// Finds every job in an assembly.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <returns>The registry.</returns>
        public static JobRegistry Discover(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            return new JobRegistry(assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(ITenantJob).IsAssignableFrom(type))
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
