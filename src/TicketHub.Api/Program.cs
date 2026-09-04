using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
// Microsoft.OpenApi v2 (which Swashbuckle 10 uses) flattened its namespaces:
// the old `Microsoft.OpenApi.Models` is gone and the types live here now.
// If you are following older tutorials and get "the type or namespace 'Models'
// does not exist", this is why.
using Microsoft.OpenApi;
using TicketHub.Api.Hubs;
using TicketHub.Api.Middleware;
using TicketHub.Api.Services;
using TicketHub.Api.Security;
using TicketHub.BusinessLogic;
using TicketHub.BusinessLogic.Abstractions;
using TicketHub.BusinessLogic.Options;
using TicketHub.Contracts.Abstractions;
using TicketHub.DataAccess;
using TicketHub.DataAccess.Entities;

// =============================================================================
// TicketHub — application start-up
// =============================================================================
//
// This one file replaces the old Startup.cs. Read it top to bottom: it is the
// map of the whole application.
//
// It has exactly TWO halves, and mixing them up is the most common start-up bug:
//
//   1. REGISTER  (builder.Services.Add…)  — "here is how to build things."
//                                            Order does not matter.
//   2. PIPELINE  (app.Use…)               — "here is what happens to each request,
//                                            in order." Order matters enormously.
//
// The dividing line is builder.Build(). After it, you cannot register anything else.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------------
// 1. REGISTRATION
// -----------------------------------------------------------------------------

// Each layer registers itself. This file never mentions TicketRepository or
// TicketService by name — see DependencyInjection.cs in each project.
builder.Services.AddDataAccess(builder.Configuration);
builder.Services.AddBusinessLogic(builder.Configuration);

// --- ASP.NET Core Identity ---------------------------------------------------
//
// AddIdentityCore, not AddIdentity. AddIdentity also wires up cookie authentication
// and its redirect behaviour — which for an API means an unauthenticated call gets a
// 302 to a login page that does not exist, instead of a 401. Confusing to debug, and
// completely avoidable by choosing the right method.
builder.Services
    .AddIdentityCore<ApplicationUser>(options =>
    {
        // Password policy in ONE place, rather than duplicated across DTO attributes.
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredLength = 8;

        // Lockout: five wrong passwords buys a five-minute pause. This is the entire
        // brute-force defence, and it only works because AuthService remembers to call
        // AccessFailedAsync on every failure.
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Lockout.AllowedForNewUsers = true;

        // One account per email address.
        options.User.RequireUniqueEmail = true;

        // Off for training so you can log in immediately after registering.
        // Turn it on the moment real people are involved: without it, anyone can
        // register using somebody else's address.
        options.SignIn.RequireConfirmedEmail = false;
    })
    .AddRoles<ApplicationRole>()
    .AddEntityFrameworkStores<TicketHubDbContext>()
    .AddDefaultTokenProviders();   // needed for password-reset and email-confirmation tokens

// --- Authentication: who are you? --------------------------------------------
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwtOptions = jwtSection.Get<JwtOptions>()
    ?? throw new InvalidOperationException("The 'Jwt' configuration section is missing.");

// FAIL LOUDLY, AND SAY WHAT TO DO ABOUT IT.
//
// Neither the connection string nor the signing key is in source control — they live in
// user-secrets, which is per-developer and outside the repository. That is the whole point:
// a key in appsettings.json is a key in everyone's git history forever.
//
// Without this block an empty key still starts the app and then fails much later inside the
// token handler with an exception that says nothing useful. A start-up check that names the
// exact command to run turns a twenty-minute mystery into a ten-second fix.
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Default")))
{
    throw new InvalidOperationException(
        "No database connection string. Set it once with:\n" +
        "  dotnet user-secrets set \"ConnectionStrings:Default\" " +
        "\"Server=localhost,1433;Database=TicketHub;User Id=sa;Password=TicketHub!Dev2026;" +
        "TrustServerCertificate=True;MultipleActiveResultSets=True\" " +
        "--project src/TicketHub.Api");
}

// 32 bytes is the minimum for HMAC-SHA256. A shorter key throws deep inside the JWT library
// with a message that does not mention configuration at all.
if (string.IsNullOrWhiteSpace(jwtOptions.Key) || jwtOptions.Key.Length < 32)
{
    throw new InvalidOperationException(
        "The JWT signing key is missing or shorter than 32 characters. Set it once with:\n" +
        "  dotnet user-secrets set \"Jwt:Key\" \"<a long random string>\" --project src/TicketHub.Api");
}

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        // EVERY ONE OF THESE FLAGS MATTERS. Turning any of them off is a real hole,
        // and each one is somebody's production incident:
        options.TokenValidationParameters = new TokenValidationParameters
        {
            // Was it signed by us? Off = anyone can mint tokens. This is the big one.
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),

            // Did WE issue it? Off = a token from any system signed with a key we happen
            // to share is accepted.
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,

            // Was it meant for THIS API? Off = a token your identity provider minted for
            // a different application works here too.
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,

            // Has it expired? Off = every token ever issued is valid forever.
            ValidateLifetime = true,

            // Default is 5 minutes, which silently extends every token's life. Zero,
            // because machines have correct clocks now.
            ClockSkew = TimeSpan.FromSeconds(jwtOptions.ClockSkewSeconds)
        };

        options.Events = new JwtBearerEvents
        {
            // --- SignalR needs help finding the token ---------------------------
            //
            // The browser WebSocket API cannot set an Authorization header. So the
            // JavaScript client puts the token in the query string instead, and this
            // handler picks it up.
            //
            // We only accept it from the query string for /hubs paths. Allowing it
            // everywhere would mean tokens in URLs — and URLs end up in server logs,
            // browser history, and the Referer header sent to third-party sites.
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },

            // --- The revocation check -------------------------------------------
            //
            // A JWT cannot normally be revoked: the server does not store it, it only
            // checks the signature. This handler is the emergency brake.
            //
            // Every token carries the user's SecurityStamp. Identity changes that stamp
            // on password change, role change or forced logout — so a mismatch means the
            // token was minted before something important happened, and we reject it.
            //
            // It costs one database read per request. That is a real price, and it is why
            // a high-traffic system would cache the stamp for a few seconds. For a
            // municipal API it is the right trade: an immediately-revocable token beats a
            // microsecond.
            OnTokenValidated = async context =>
            {
                var userManager = context.HttpContext.RequestServices
                    .GetRequiredService<UserManager<ApplicationUser>>();

                var userId = context.Principal?.FindFirst(
                    System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

                if (userId is null)
                {
                    context.Fail("Token has no user id.");
                    return;
                }

                var user = await userManager.FindByIdAsync(userId);

                if (user is null || !user.IsActive)
                {
                    context.Fail("The account no longer exists or has been deactivated.");
                    return;
                }

                var stampInToken = context.Principal?.FindFirst(AppClaimTypes.SecurityStamp)?.Value;

                if (stampInToken != user.SecurityStamp)
                {
                    context.Fail("This session is no longer valid. Please sign in again.");
                }
            }
        };
    });

// --- Authorization: what may you do? -----------------------------------------
builder.Services.AddAuthorization(options =>
{
    // Named policies for rules that are more than one role. A policy is the right tool
    // as soon as the answer depends on more than "do you hold role X" — and it means the
    // rule is written once instead of copied into every attribute.
    options.AddPolicy("StaffOnly", policy =>
        policy.RequireRole(AppRoles.Admin, AppRoles.Supervisor, AppRoles.Agent));

    options.AddPolicy("CanManageDepartment", policy =>
        policy.RequireRole(AppRoles.Admin, AppRoles.Supervisor));

    // Every endpoint requires authentication unless it says [AllowAnonymous].
    //
    // FAIL CLOSED. The alternative — remembering [Authorize] on each new controller —
    // fails the first time somebody forgets, and nothing tells you. Opting IN to being
    // public is a decision you have to write down.
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// --- The current-user bridge -------------------------------------------------
//
// The concrete implementation of the interface that the lower layers depend on.
// This is the line that completes the dependency inversion.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// --- Email ------------------------------------------------------------------
//
// The business layer depends on IEmailSender; this line chooses the implementation.
// In development that is ConsoleEmailSender, which writes the message to the log — so the
// whole forgot-password flow is testable with no SMTP credentials and no internet.
//
// Production would register an SmtpEmailSender (or a SendGrid/SES client) here instead, and
// AuthService would not change by a single character. That is the entire point of the
// interface: the decision lives in one line of composition, not in the code that uses it.
//
// THE PROVIDER SWITCH. One place, three options, and AuthService knows about none of them.
var emailProvider = builder.Configuration["Email:Provider"] ?? "Console";

switch (emailProvider.ToLowerInvariant())
{
    case "smtp":
        builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
        break;

    case "mailjet":
        // AddHttpClient registers a pooled, recycled HttpClient for this type. Never
        // "new HttpClient()" per send — that exhausts the machine's sockets under load.
        builder.Services.AddHttpClient<IEmailSender, MailjetEmailSender>()
               .SetHandlerLifetime(TimeSpan.FromMinutes(5));
        break;

    default:
        // The development default: writes the message to the log so the whole
        // forgot-password flow is testable with no credentials and no internet.
        builder.Services.AddScoped<IEmailSender, ConsoleEmailSender>();
        break;
}

// --- SignalR -----------------------------------------------------------------
builder.Services.AddSignalR(options =>
{
    // Development only: send the real exception text to the client. Never in production —
    // it is a free stack trace for anyone with a WebSocket.
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();

    // If a client says nothing for this long, the server drops it. Clients ping at
    // roughly half the interval on their own, so this only fires on a genuinely dead
    // connection.
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

// The SignalR side of IRealtimeNotifier. Singleton, because IHubContext is.
//
// ⚠ SCALING OUT: with more than one server, a message sent from server A never reaches a
// client connected to server B — each process only knows its own connections. The fix is a
// backplane (AddStackExchangeRedis) so servers relay to each other. One server here, so it
// is not wired up; know that it exists before you deploy two.
builder.Services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();

// --- Rate limiting -----------------------------------------------------------
//
// WHY, WHEN IDENTITY ALREADY HAS LOCKOUT.
// Lockout stops five bad guesses against ONE account. It does nothing about one attacker
// trying one common password against ten thousand accounts — "password spraying" — because
// no single account ever reaches its failure threshold. Rate limiting is the other half of
// the defence: lockout protects an account, this protects the system.
var rateLimits = builder.Configuration.GetSection(RateLimitOptions.SectionName)
                        .Get<RateLimitOptions>() ?? new RateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    // 429 Too Many Requests, so a well-written client backs off instead of hammering.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Tell the caller WHEN to come back. Without Retry-After a client has to guess, and
    // most guess "immediately", which is how a rate limit turns into a busy loop.
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter =
            rateLimits.AuthWindowSeconds.ToString();

        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            title = "Too many requests.",
            status = 429,
            detail = $"Try again in {rateLimits.AuthWindowSeconds} seconds."
        }, ct);
    };

    options.AddPolicy("auth", context =>
        // Partition by IP: each caller gets their own bucket. Without a partition key the
        // limit is global, and one noisy client locks out the entire internet.
        //
        // Behind a proxy, RemoteIpAddress is the proxy — every user shares one bucket. Fix
        // that with UseForwardedHeaders and a known-proxies list before you deploy.
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                // Read from configuration, so development can be generous and production
                // strict without a rebuild. See the "RateLimit" section in appsettings.
                Window = TimeSpan.FromSeconds(rateLimits.AuthWindowSeconds),
                PermitLimit = rateLimits.AuthPermitLimit,
                QueueLimit = 0      // no queueing — reject immediately rather than delaying
            }));

    // A second, much looser policy for everything else. It does not stop a determined
    // attacker, but it does stop one buggy client with a runaway retry loop from taking the
    // whole API down for everybody else.
    options.AddPolicy("global", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(rateLimits.GlobalWindowSeconds),
                PermitLimit = rateLimits.GlobalPermitLimit,
                QueueLimit = 0
            }));
});

// --- Controllers and JSON ----------------------------------------------------
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        // Enums as strings: "Open" instead of 0.
        //
        // The response becomes self-explanatory, and a front end no longer has to keep its
        // own copy of the enum in sync with ours. The cost is a few more bytes and a
        // rename becoming a breaking API change — which is arguably a feature, since it
        // makes you notice.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());

        // Do not serialise nulls. Smaller payloads, and a cleaner-looking response.
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();

// --- Swagger -----------------------------------------------------------------
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "TicketHub API",
        Version = "v1",
        Description =
            "Municipal ticketing system — a training reference project.\n\n" +
            "**Sign in first:** POST /api/auth/login with `admin@tickethub.local` / `Passw0rd!`, " +
            "then click **Authorize** and paste the accessToken.\n\n" +
            "Live chat runs over SignalR at `/hubs/chat` (Swagger cannot exercise WebSockets — " +
            "use `/chat-demo.html`)."
    });

    // Pull the /// <summary> comments into the Swagger UI. This is why
    // GenerateDocumentationFile is set in Directory.Build.props.
    var xmlPath = Path.Combine(AppContext.BaseDirectory, "TicketHub.Api.xml");
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }

    // The Authorize button. Without this, every protected endpoint returns 401 in
    // Swagger and it looks broken.
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste ONLY the token. Swagger adds the 'Bearer ' prefix itself."
    });

    // "Every endpoint may use the Bearer scheme defined above."
    //
    // In Microsoft.OpenApi v2 a reference to a named scheme is its own type
    // (OpenApiSecuritySchemeReference) rather than an empty scheme carrying an
    // OpenApiReference, which is what older examples show.
    // Swashbuckle 10 wants a factory here rather than a ready-made object, because a
    // reference has to be resolved against the document it points into.
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        { new OpenApiSecuritySchemeReference("Bearer", document), new List<string>() }
    });
});

// --- CORS --------------------------------------------------------------------
//
// A browser refuses cross-origin calls unless the SERVER says they are allowed. This
// policy is that permission.
const string CorsPolicy = "TicketHubCors";

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy =>
    {
        var origins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?? Array.Empty<string>();

        origins = origins
            .Append("http://localhost:4200")
            .Distinct()
            .ToArray();

        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod()

              // REQUIRED BY SIGNALR, and it comes with a hard rule: AllowCredentials
              // cannot be combined with AllowAnyOrigin. The browser rejects the
              // combination outright — a wildcard plus credentials is exactly the setup
              // that would let any website on the internet make authenticated calls as
              // your logged-in user. So the origins must be listed explicitly.
              .AllowCredentials();
    });
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<TicketHubDbContext>("database");

var app = builder.Build();

// -----------------------------------------------------------------------------
// 2. THE PIPELINE — order is the behaviour
// -----------------------------------------------------------------------------
//
// Each Use… wraps the ones after it, like a set of nested boxes. A request travels
// down and the response travels back up.
//
// Get the order wrong and things fail in ways that make no sense:
//   • Authentication after Authorization → every request is anonymous → 401 everywhere.
//   • Exception handling registered late  → exceptions from earlier middleware escape it.
//   • CORS after routing                  → preflight OPTIONS requests are never answered.

// FIRST, so it wraps everything below it. Anything that throws anywhere downstream
// comes back out here and leaves as a clean ProblemDetails.
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "TicketHub API v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "TicketHub API";
    });
}
else
{
    // HSTS tells the browser "never speak to me over plain HTTP again". Production only:
    // on localhost it would poison your browser's cache for every other local project.
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Serves wwwroot — including the chat demo page.
// --- Security headers -------------------------------------------------------
//
// One line each, and each one closes a real hole:
//
//   nosniff  — stops the browser second-guessing our Content-Type. Without it, a file we
//              serve as text/plain can be re-interpreted as HTML and executed, which turns
//              any upload endpoint into stored XSS. See section 05 of the lesson.
//   DENY     — this is an API; nothing here should ever be framed. Blocks clickjacking.
//   Referrer — do not leak our URLs (which contain ids) to third-party sites.
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseStaticFiles();

// Routing must come before CORS, authentication and authorization: those three need to
// know WHICH endpoint was matched before they can decide anything about it.
app.UseRouting();

app.UseCors(CorsPolicy);

// After routing (it needs to know which endpoint was matched, to find the [EnableRateLimiting]
// attribute) and before authentication (so a flood of bad tokens is rejected cheaply, without
// paying for signature validation and a database read on every one of them).
app.UseRateLimiter();

// Authentication BEFORE authorization, always.
//   Authentication = "who are you?"   → reads the token, builds HttpContext.User
//   Authorization  = "may you do it?" → reads that user and checks the rules
// Swap them and authorization inspects an empty user, so everything is 401 and nothing
// in your code looks wrong.
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// The WebSocket endpoint. The URL the JavaScript client connects to.
app.MapHub<ChatHub>("/hubs/chat");

app.MapHealthChecks("/health").AllowAnonymous();

// -----------------------------------------------------------------------------
// 3. Database setup on start-up — development convenience
// -----------------------------------------------------------------------------
//
// ⚠ DO NOT DO THIS IN PRODUCTION. Two instances starting at once can both try to apply
// migrations and deadlock or half-apply. It also means the application needs
// schema-modification rights on the database at runtime, which most security reviews
// will (correctly) object to.
//
// Production teams generate an idempotent script —
//     dotnet ef migrations script --idempotent -o deploy/schema.sql
// — and run it from the deployment pipeline before the new version starts.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();

    try
    {
        var db = services.GetRequiredService<TicketHubDbContext>();

        logger.LogInformation("Applying migrations…");
        await db.Database.MigrateAsync();

        logger.LogInformation("Seeding demo data…");
        await services.GetRequiredService<DatabaseSeeder>().SeedAsync();

        logger.LogInformation("Database is ready. Swagger: /swagger");
    }
    catch (Exception ex)
    {
        logger.LogError(ex,
            "Database setup failed. Is SQL Server running? Try: docker compose up -d");

        // Deliberately NOT rethrown. The API still starts, so /health and Swagger work
        // and you can read the error — rather than the process dying with a stack trace
        // in a terminal you may not be watching.
    }
}

app.Run();

/// <summary>
/// Exposed so an integration test project can use <c>WebApplicationFactory&lt;Program&gt;</c>
/// to spin the whole API up in memory.
/// </summary>
/// <remarks>
/// A top-level-statements Program class is internal by default, and
/// <c>WebApplicationFactory</c> needs it to be public. This one-line partial declaration is
/// the standard workaround.
/// </remarks>
public partial class Program;
