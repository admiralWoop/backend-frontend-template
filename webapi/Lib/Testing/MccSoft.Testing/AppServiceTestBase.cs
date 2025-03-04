﻿using System;
using Hangfire;
using MccSoft.IntegreSql.EF;
using MccSoft.IntegreSql.EF.DatabaseInitialization;
using MccSoft.LowLevelPrimitives;
using MccSoft.NpgSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Debug;
using Moq;
using NeinLinq;

namespace MccSoft.Testing
{
    /// <summary>
    /// The base class for application service tests.
    /// </summary>
    /// <remarks>
    /// This class serves to enforce a specific way of accessing the DB:
    /// * the service works with it's own instance of DbContext,
    ///   who lives at least as long as the service instance itself,
    /// * arrange- and assert-blocks of tests use a separate short-lived DbContext provided by
    ///   the helper method <see cref="WithDbContext"/>.
    ///
    /// This approach allows tests to check whether SaveChanges was called in the service method
    /// (the state of objects loaded in a separate DbContext will be incorrect, if SaveChanges is
    /// forgotten).
    /// </remarks>
    public class AppServiceTestBase<TService, TDbContext> : TestBase<TDbContext>, IDisposable
        where TDbContext : DbContext, ITransactionFactory
        where TService : class
    {
        protected readonly ILoggerFactory LoggerFactory = new LoggerFactory(
            new[] { new DebugLoggerProvider() }
        );

        private readonly Func<
            DbContextOptions<TDbContext>,
            IUserAccessor,
            TDbContext
        > _dbContextFactory;

        private readonly DbContextOptionsBuilder<TDbContext> _builder;
        private TDbContext _dbContext;

        protected readonly Mock<IUserAccessor> _userAccessorMock;

        private readonly DatabaseType? _databaseType;
        private readonly IDatabaseInitializer _databaseInitializer;
        protected ServiceProvider _serviceProvider;
        private Mock<IBackgroundJobClient> _backgroundJobClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="AppServiceTestBase{TService,TDbContext}" />
        /// class with the specified DbContext factory.
        /// </summary>
        /// <param name="databaseType">Type of database to use in tests</param>
        /// <param name="dbContextFactory">A function that creates a DbContext.</param>
        /// <param name="basicDatabaseSeedingOptions">Additional database seeding (beside EnsureCreated)</param>
        protected AppServiceTestBase(
            DatabaseType? databaseType,
            Func<DbContextOptions<TDbContext>, IUserAccessor, TDbContext> dbContextFactory,
            DatabaseSeedingOptions<TDbContext> basicDatabaseSeedingOptions = null
        )
        {
            _databaseType = databaseType;
            _dbContextFactory = dbContextFactory;

            _databaseInitializer = databaseType switch
            {
                null => null,
                DatabaseType.Postgres
                    => new NpgsqlDatabaseInitializer(
                        connectionStringOverride: new() { Host = "localhost", Port = 5434, }
                    ),
                DatabaseType.Sqlite => new SqliteDatabaseInitializer(),
                _ => throw new ArgumentOutOfRangeException(nameof(databaseType), databaseType, null)
            };

            string connectionString = _databaseInitializer?.CreateDatabaseGetConnectionStringSync(
                basicDatabaseSeedingOptions
            );

            _userAccessorMock = new Mock<IUserAccessor>();
            _userAccessorMock.Setup(x => x.IsHttpContextAvailable).Returns(true);

            if (databaseType != null)
            {
                // Its ok to call virtual methods because its just init the builder and doesn't use members.
                // ReSharper disable VirtualMemberCallInConstructor
                _builder = GetBuilder(connectionString ?? "");
                // ReSharper restore VirtualMemberCallInConstructor
            }
        }

        public void Dispose()
        {
            DisposeImpl();
        }

        /// <summary>
        /// Makes sure that the DB is created.
        /// </summary>
        protected virtual void EnsureDbCreated()
        {
            using TDbContext context = CreateDbContext();
            context.Database.EnsureCreated();
        }

        /// <summary>
        /// Returns the DbContextOptionsBuilder
        /// </summary>
        /// <param name="connectionString"></param>
        public virtual DbContextOptionsBuilder<TDbContext> GetBuilder(string connectionString)
        {
            var builder = new DbContextOptionsBuilder<TDbContext>();
            _databaseInitializer.UseProvider(builder, connectionString);

            builder
                .WithLambdaInjection()
                .UseLoggerFactory(LoggerFactory)
                .EnableSensitiveDataLogging()
                .EnableDetailedErrors();
            return builder;
        }

        /// <summary>
        /// Gets the long-living DbContext that is used to initialize the service under test.
        /// Should be used in alternative implementations of <see cref="InitializeService" />.
        /// </summary>
        /// <returns>The long-living DbContext instance.</returns>
        protected TDbContext GetLongLivingDbContext()
        {
            return _dbContext ??= CreateDbContext();
        }

        /// <summary>
        /// Provides access to a PostgresRetryHelper and DbContext
        /// that should be used to initialize the service being tested.
        /// </summary>
        /// <param name="action">The action that creates the service.</param>
        protected TService InitializeService(
            Func<PostgresRetryHelper<TDbContext, TService>, TDbContext, TService> action
        )
        {
            if (_databaseType == null)
                return action(null, null);
            return action(CreatePostgresRetryHelper<TService>(), GetLongLivingDbContext());
        }

        /// <summary>
        /// Creates PostgresRetryHelper for any service type
        /// </summary>
        protected PostgresRetryHelper<
            TDbContext,
            TAnyService
        > CreatePostgresRetryHelper<TAnyService>()
        {
            return new PostgresRetryHelper<TDbContext, TAnyService>(
                CreateDbContext(),
                CreateDbContext,
                LoggerFactory,
                new TransactionLogger<TAnyService>(LoggerFactory.CreateLogger<TAnyService>())
            );
        }

        /// <summary>
        /// Override this method to dispose of expensive resources created in descendants
        /// of this class. Always call the base method.
        /// </summary>
        protected virtual void DisposeImpl()
        {
            _dbContext?.Dispose();
            _databaseInitializer?.RemoveDatabase(CreateDbContext().Database.GetConnectionString());
            LoggerFactory.Dispose();
        }

        /// <summary>
        /// Creates a new instance of <typeparamref name="TDbContext"/>.
        /// Should be used in alternative implementations of <see cref="InitializeService" />.
        /// </summary>
        /// <returns>A new DbContext instance.</returns>
        protected override TDbContext CreateDbContext()
        {
            return _dbContextFactory(_builder.Options, _userAccessorMock.Object);
        }

        protected TService InitializeService(
            Action<IServiceCollection> configureRegistrations = null
        )
        {
            var serviceCollection = CreateServiceCollection();
            serviceCollection.AddTransient<TService>();
            configureRegistrations?.Invoke(serviceCollection);

            _serviceProvider = serviceCollection.BuildServiceProvider();

            return _serviceProvider.GetRequiredService<TService>();
        }

        /// <summary>
        /// Creates a service collection that you could resolve your System-Under-Test from.
        /// </summary>
        protected virtual ServiceCollection CreateServiceCollection()
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection
                .AddScoped(x => CreateDbContext())
                .AddSingleton<Func<TDbContext>>(CreateDbContext)
                .RegisterRetryHelper();

            serviceCollection.AddTransient(typeof(ILogger<>), typeof(NullLogger<>)).AddLogging();
            serviceCollection.AddSingleton(_userAccessorMock.Object);

            _backgroundJobClient = new Mock<IBackgroundJobClient>();
            serviceCollection.AddSingleton(_backgroundJobClient.Object);

            return serviceCollection;
        }

        /// <summary>
        /// Gets the service being tested (System Under Test).
        /// </summary>
        protected TService Sut { get; set; }
    }
}
