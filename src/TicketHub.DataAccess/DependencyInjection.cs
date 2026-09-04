using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketHub.DataAccess.Repositories;

namespace TicketHub.DataAccess;

/// <summary>
/// Everything the data layer needs to register itself.
/// </summary>
/// <remarks>
/// WHY EACH LAYER REGISTERS ITSELF.
/// The alternative is a Program.cs that knows about <c>TicketRepository</c>,
/// <c>CommentRepository</c> and every other internal class. Then the API project is coupled
/// to the data layer's internals, and adding a repository means editing a file in a different
/// project. One extension method per layer keeps the knowledge where it belongs — this is the
/// only file in the solution that knows these classes exist.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddDataAccess(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "SqlServer";
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "No connection string named 'Default'. Check appsettings.json, or run " +
                "docker compose up -d to start the database.");

        services.AddDbContext<TicketHubDbContext>(options =>
        {
            if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
            {
                // The fallback for machines where SQL Server will not run. The rest of the
                // project is provider-agnostic: LINQ is the same, migrations are regenerated,
                // and only a handful of raw SQL strings (check constraints, filtered indexes)
                // would need their bracket quoting changed.
                options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsAssembly(typeof(TicketHubDbContext).Assembly.FullName));
            }
            else
            {
                options.UseSqlServer(connectionString, sql =>
                {
                    // Migrations live in THIS assembly, not in the API project. That is what
                    // lets `dotnet ef migrations add` work with
                    //   --project TicketHub.DataAccess --startup-project TicketHub.Api
                    sql.MigrationsAssembly(typeof(TicketHubDbContext).Assembly.FullName);

                    // Retry on transient failures — a container that is still starting, a
                    // brief network blip, an Azure SQL failover. Without this, one hiccup is
                    // a 500 for the user.
                    sql.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorNumbersToAdd: null);

                    sql.CommandTimeout(30);
                });
            }

            // Development-only diagnostics. Both of these leak data into logs — parameter
            // values include email addresses, and detailed errors include row contents — so
            // they are guarded by the environment and must never be on in production.
            if (string.Equals(configuration["Database:EnableSensitiveDataLogging"], "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
        });

        // Scoped = one instance per HTTP request.
        //
        // It must match the DbContext's lifetime. A Singleton repository holding a Scoped
        // DbContext is the classic "captive dependency" bug: the context is disposed at the
        // end of the first request and every request after that fails on a dead object.
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

        // Also registered individually, so a service can inject just the one it needs
        // instead of taking the whole unit of work.
        services.AddScoped<ITicketRepository, TicketRepository>();
        services.AddScoped<ICommentRepository, CommentRepository>();
        services.AddScoped<IDepartmentRepository, DepartmentRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IChatRepository, ChatRepository>();
        services.AddScoped<IReportRepository, ReportRepository>();

        services.AddScoped<DatabaseSeeder>();

        return services;
    }
}
