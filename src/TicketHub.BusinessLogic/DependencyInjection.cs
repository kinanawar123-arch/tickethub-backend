using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketHub.BusinessLogic.Options;
using TicketHub.BusinessLogic.Services;

namespace TicketHub.BusinessLogic;

public static class DependencyInjection
{
    public static IServiceCollection AddBusinessLogic(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ---------------------------------------------------------------
        // Options, validated at startup
        // ---------------------------------------------------------------
        //
        // ValidateOnStart is the important part. Without it, a missing JWT key is discovered
        // on the first login attempt — possibly at 3am, definitely not by you. With it, the
        // application refuses to start and says exactly which setting is wrong.
        services.AddOptions<JwtOptions>()
                .Bind(configuration.GetSection(JwtOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

        services.AddOptions<AppOptions>()
                .Bind(configuration.GetSection(AppOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

        services.AddOptions<EmailOptions>()
                .Bind(configuration.GetSection(EmailOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

        services.AddOptions<RateLimitOptions>()
                .Bind(configuration.GetSection(RateLimitOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

        services.AddOptions<FileStorageOptions>()
                .Bind(configuration.GetSection(FileStorageOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

        // ---------------------------------------------------------------
        // Services — all Scoped, because they hold a Scoped unit of work
        // ---------------------------------------------------------------
        //
        // Getting a lifetime wrong here is the classic captive-dependency bug: a Singleton
        // service holding a Scoped DbContext works for exactly one request and then fails
        // forever on a disposed object. When in doubt, Scoped.
        services.AddScoped<ITicketService, TicketService>();
        services.AddScoped<ICommentService, CommentService>();
        services.AddScoped<IDepartmentService, DepartmentService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IAgentService, AgentService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IRatingService, RatingService>();
        services.AddScoped<IAttachmentService, AttachmentService>();

        // The token service is stateless — it reads options and does maths — so it could be
        // a Singleton. Kept Scoped for consistency; the allocation is a rounding error and
        // one lifetime rule is easier to hold in your head than two.
        services.AddScoped<ITokenService, JwtTokenService>();

        return services;
    }
}
