using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace TicketHub.DataAccess;

/// <summary>
/// How <c>dotnet ef</c> builds a DbContext without starting the application.
/// </summary>
/// <remarks>
/// WHAT PROBLEM THIS SOLVES.
/// <c>dotnet ef migrations add</c> needs a <see cref="TicketHubDbContext"/> so it can read
/// your model. By default it tries to start the API's host to get one — and that means
/// running Program.cs, which builds the whole DI container, validates options, and would
/// happily try to connect to a database that may not be running yet.
/// <para/>
/// Implementing <see cref="IDesignTimeDbContextFactory{TContext}"/> short-circuits all of
/// that: EF finds this class and uses it instead. Migrations then work regardless of the
/// state of the application's start-up code.
/// <para/>
/// Note the <c>currentUser: null</c>. There is no HTTP request at design time and nobody to
/// attribute changes to — which is exactly why the DbContext takes that dependency as an
/// optional constructor parameter.
/// <para/>
/// Used ONLY by the EF tools. It never runs as part of the application.
/// </remarks>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TicketHubDbContext>
{
    public TicketHubDbContext CreateDbContext(string[] args)
    {
        // Read the API project's configuration if we can find it, so the migration is
        // generated against the same provider and connection string the app uses.
        var basePath = Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), "..", "TicketHub.Api"));

        if (!Directory.Exists(basePath))
        {
            basePath = Directory.GetCurrentDirectory();
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var provider = configuration["Database:Provider"] ?? "SqlServer";

        // A fallback connection string, because generating a migration does not actually
        // connect to anything — EF only needs to know WHICH provider to generate SQL for.
        // "dotnet ef database update" does connect, and then this value matters.
        var connectionString = configuration.GetConnectionString("Default")
            ?? "Server=localhost,1433;Database=TicketHub;User Id=sa;Password=TicketHub!Dev2026;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<TicketHubDbContext>();

        if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            options.UseNpgsql(connectionString,
                b => b.MigrationsAssembly(typeof(TicketHubDbContext).Assembly.FullName));
        }
        else
        {
            options.UseSqlServer(connectionString,
                b => b.MigrationsAssembly(typeof(TicketHubDbContext).Assembly.FullName));
        }

        return new TicketHubDbContext(options.Options, currentUser: null);
    }
}
