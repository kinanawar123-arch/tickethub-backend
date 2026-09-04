using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Enums;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess;

/// <summary>
/// Fills an empty database with enough realistic data to actually use the API.
/// </summary>
/// <remarks>
/// WHY NOT <c>HasData</c> IN THE CONFIGURATIONS?
/// EF's <c>HasData</c> puts seed rows into the migration itself, which is exactly right for
/// reference data that must travel with the schema and never change — a list of countries,
/// say. It is wrong for what we are doing here, for two reasons:
/// <list type="bullet">
/// <item>It needs every primary key hard-coded, including the keys of related rows, which
///       becomes unmanageable past a handful of records.</item>
/// <item>It cannot call <c>UserManager</c>, so it cannot hash a password. Writing a hash
///       literal into a migration is both fragile and a bad habit to teach.</item>
/// </list>
/// So: reference data through <c>HasData</c>, demo data through a seeder like this one.
/// <para/>
/// EVERY STEP IS IDEMPOTENT. It checks before it inserts, so running it twice does nothing
/// the second time. A seeder that duplicates its data on the second run is a seeder people
/// stop running.
/// </remarks>
public class DatabaseSeeder
{
    private readonly TicketHubDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<ApplicationRole> _roles;
    private readonly ILogger<DatabaseSeeder> _log;

    /// <summary>
    /// The password every demo account uses.
    /// </summary>
    /// <remarks>
    /// Fine for a training database that lives in a container on your laptop. In anything
    /// real this comes from configuration or a secret store, and the first login forces a
    /// change. A default password in source control is how breaches start.
    /// </remarks>
    public const string DemoPassword = "Passw0rd!";

    public DatabaseSeeder(
        TicketHubDbContext db,
        UserManager<ApplicationUser> users,
        RoleManager<ApplicationRole> roles,
        ILogger<DatabaseSeeder> log)
    {
        _db = db;
        _users = users;
        _roles = roles;
        _log = log;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedRolesAsync();
        var departments = await SeedDepartmentsAsync(ct);
        var categories = await SeedCategoriesAsync(departments, ct);
        var users = await SeedUsersAsync(departments);
        var agents = await SeedAgentsAsync(users, departments, ct);
        await SeedTicketsAsync(categories, agents, users, ct);

        _log.LogInformation("Seeding complete.");
    }

    // ---------------------------------------------------------------------

    private async Task SeedRolesAsync()
    {
        foreach (var role in AppRoles.All)
        {
            if (await _roles.RoleExistsAsync(role))
            {
                continue;
            }

            await _roles.CreateAsync(new ApplicationRole(role)
            {
                Description = role switch
                {
                    AppRoles.Admin => "Full control, including user management.",
                    AppRoles.Supervisor => "Runs a department: assigns work, sees everything in it.",
                    AppRoles.Agent => "Works tickets inside their own department.",
                    _ => "A member of the public. Sees only their own tickets."
                }
            });

            _log.LogInformation("Created role {Role}", role);
        }
    }

    private async Task<Dictionary<string, Department>> SeedDepartmentsAsync(CancellationToken ct)
    {
        var wanted = new[]
        {
            new Department { Name = "Roads", Description = "Potholes, signage, road markings.", ContactEmail = "roads@tickethub.local" },
            new Department { Name = "Sanitation", Description = "Waste collection, illegal dumping, street cleaning.", ContactEmail = "sanitation@tickethub.local" },
            new Department { Name = "Lighting", Description = "Street lights and public illumination.", ContactEmail = "lighting@tickethub.local" },
            new Department { Name = "Parks", Description = "Green spaces, playgrounds, trees.", ContactEmail = "parks@tickethub.local" }
        };

        // ONE query for what exists, then insert only what is missing.
        // The N+1 version — AnyAsync inside the loop — is four round trips here and forty
        // when the list grows. The habit is what matters, not the four.
        var existing = await _db.Departments.IgnoreQueryFilters()
            .ToDictionaryAsync(d => d.Name, ct);

        foreach (var department in wanted.Where(d => !existing.ContainsKey(d.Name)))
        {
            _db.Departments.Add(department);
            existing[department.Name] = department;
        }

        await _db.SaveChangesAsync(ct);
        return existing;
    }

    private async Task<Dictionary<string, Category>> SeedCategoriesAsync(
        Dictionary<string, Department> departments, CancellationToken ct)
    {
        var wanted = new (string Name, string Department, int Sla, string Description)[]
        {
            ("Pothole",            "Roads",      48,  "Damaged road surface."),
            ("Damaged signage",    "Roads",      120, "Missing, bent or unreadable signs."),
            ("Blocked drain",      "Roads",      24,  "Standing water, blocked gully."),
            ("Missed collection",  "Sanitation", 24,  "Bins not emptied on schedule."),
            ("Illegal dumping",    "Sanitation", 48,  "Waste dumped in a public space."),
            ("Street light out",   "Lighting",   72,  "A single light not working."),
            ("Exposed cabling",    "Lighting",   4,   "Live cable exposed — safety critical."),
            ("Overgrown vegetation", "Parks",    168, "Branches or hedges blocking a path."),
            ("Damaged playground", "Parks",      24,  "Broken or unsafe play equipment.")
        };

        var existing = await _db.Categories.IgnoreQueryFilters().ToListAsync(ct);
        var result = existing.ToDictionary(c => c.Name);

        foreach (var (name, departmentName, sla, description) in wanted)
        {
            if (result.ContainsKey(name))
            {
                continue;
            }

            var category = new Category
            {
                Name = name,
                Description = description,
                SlaHours = sla,
                DepartmentId = departments[departmentName].Id
            };

            _db.Categories.Add(category);
            result[name] = category;
        }

        await _db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<Dictionary<string, ApplicationUser>> SeedUsersAsync(
        Dictionary<string, Department> departments)
    {
        var wanted = new (string Email, string Name, UserType Type, string Role, string? Department)[]
        {
            ("admin@tickethub.local",      "System Administrator", UserType.Employee, AppRoles.Admin,      null),
            ("supervisor@tickethub.local", "Rana Haddad",          UserType.Employee, AppRoles.Supervisor, "Roads"),
            ("sara@tickethub.local",       "Sara Khoury",          UserType.Employee, AppRoles.Agent,      "Roads"),
            ("omar@tickethub.local",       "Omar Nasser",          UserType.Employee, AppRoles.Agent,      "Sanitation"),
            ("lina@tickethub.local",       "Lina Aziz",            UserType.Employee, AppRoles.Agent,      "Lighting"),
            ("citizen@tickethub.local",    "Ahmad Barakat",        UserType.Citizen,  AppRoles.Citizen,    null),
            ("citizen2@tickethub.local",   "Maya Saleh",           UserType.Citizen,  AppRoles.Citizen,    null)
        };

        var result = new Dictionary<string, ApplicationUser>();

        foreach (var (email, name, type, role, _) in wanted)
        {
            // FindByEmailAsync normalises the address for us, which is the whole point of
            // Identity's NormalizedEmail column — see the note on ApplicationUser.
            var user = await _users.FindByEmailAsync(email);

            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    DisplayName = name,
                    UserType = type,
                    EmailConfirmed = true,
                    IsActive = true
                };

                // CreateAsync hashes the password, sets the security stamp, applies the
                // configured password policy, and saves. Never build an ApplicationUser with
                // a PasswordHash by hand — you would be reimplementing all of that, badly.
                var created = await _users.CreateAsync(user, DemoPassword);

                if (!created.Succeeded)
                {
                    var errors = string.Join("; ", created.Errors.Select(e => e.Description));
                    throw new InvalidOperationException($"Could not seed user {email}: {errors}");
                }

                _log.LogInformation("Seeded user {Email}", email);
            }

            if (!await _users.IsInRoleAsync(user, role))
            {
                await _users.AddToRoleAsync(user, role);
            }

            result[email] = user;
        }

        return result;
    }

    private async Task<Dictionary<string, Agent>> SeedAgentsAsync(
        Dictionary<string, ApplicationUser> users,
        Dictionary<string, Department> departments,
        CancellationToken ct)
    {
        var wanted = new (string Email, string Department, int Cap, string[] Skills)[]
        {
            ("supervisor@tickethub.local", "Roads",      20, new[] { "Planning", "Arabic" }),
            ("sara@tickethub.local",       "Roads",      10, new[] { "Asphalt", "Heavy machinery", "Arabic" }),
            ("omar@tickethub.local",       "Sanitation", 12, new[] { "Waste handling", "Arabic" }),
            ("lina@tickethub.local",       "Lighting",    8, new[] { "Electrical", "Working at height" })
        };

        var existing = await _db.Agents.IgnoreQueryFilters()
            .Include(a => a.Skills)
            .ToListAsync(ct);

        var skills = await _db.Skills.ToDictionaryAsync(s => s.Name, ct);
        var result = new Dictionary<string, Agent>();

        foreach (var (email, departmentName, cap, skillNames) in wanted)
        {
            var user = users[email];
            var agent = existing.FirstOrDefault(a => a.UserId == user.Id);

            if (agent is null)
            {
                agent = new Agent
                {
                    FullName = user.DisplayName,
                    UserId = user.Id,
                    DepartmentId = departments[departmentName].Id,
                    MaxOpenTickets = cap
                };

                foreach (var skillName in skillNames)
                {
                    if (!skills.TryGetValue(skillName, out var skill))
                    {
                        skill = new Skill { Name = skillName };
                        _db.Skills.Add(skill);
                        skills[skillName] = skill;
                    }

                    agent.Skills.Add(skill);
                }

                agent.Profile = new AgentProfile
                {
                    Biography = $"{user.DisplayName} works in {departmentName}.",
                    HiredOn = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc)
                };

                _db.Agents.Add(agent);
            }

            result[email] = agent;
        }

        await _db.SaveChangesAsync(ct);
        return result;
    }

    private async Task SeedTicketsAsync(
        Dictionary<string, Category> categories,
        Dictionary<string, Agent> agents,
        Dictionary<string, ApplicationUser> users,
        CancellationToken ct)
    {
        if (await _db.Tickets.IgnoreQueryFilters().AnyAsync(ct))
        {
            _log.LogInformation("Tickets already present — skipping ticket seed.");
            return;
        }

        // A FIXED reference date, not DateTime.UtcNow.
        //
        // Seeded data that shifts every time you run it makes "why is this ticket overdue
        // today but not yesterday" impossible to reason about, and makes any test built on
        // the seed flaky. Anchor the demo data to a known point.
        var reference = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);

        var samples = new (string Category, string Title, string Description, TicketPriority Priority,
                           TicketStatus Status, string? AgentEmail, string ReporterEmail, int DaysAgo, string Address)[]
        {
            ("Pothole", "Deep pothole on Al-Quds Street",
             "A pothole about 40cm across near the bakery. Two cars have already been damaged.",
             TicketPriority.High, TicketStatus.InProgress, "sara@tickethub.local", "citizen@tickethub.local", 6,
             "Al-Quds Street, near the bakery"),

            ("Pothole", "Sunken drain cover on Rainbow Road",
             "The cover has sunk about 5cm below the road surface and makes a loud bang when cars pass.",
             TicketPriority.Medium, TicketStatus.Open, null, "citizen2@tickethub.local", 2,
             "Rainbow Road, opposite number 14"),

            ("Exposed cabling", "Exposed cable at the bus shelter",
             "A cable is hanging loose from the light in the bus shelter. Children wait here every morning.",
             TicketPriority.Urgent, TicketStatus.InProgress, "lina@tickethub.local", "citizen@tickethub.local", 1,
             "Central bus shelter, Main Square"),

            ("Illegal dumping", "Construction waste dumped behind the school",
             "Someone has dumped what looks like a truckload of rubble behind the school fence.",
             TicketPriority.High, TicketStatus.Open, "omar@tickethub.local", "citizen2@tickethub.local", 4,
             "Behind Al-Amal School"),

            ("Missed collection", "Bins not emptied on Tuesday",
             "The whole street was missed on the Tuesday collection. Bins are overflowing.",
             TicketPriority.Medium, TicketStatus.Resolved, "omar@tickethub.local", "citizen@tickethub.local", 12,
             "Olive Grove Street"),

            ("Street light out", "Street light out for two weeks",
             "The light outside number 8 has been off for two weeks. The street is very dark at night.",
             TicketPriority.Medium, TicketStatus.Resolved, "lina@tickethub.local", "citizen2@tickethub.local", 20,
             "Jasmine Street, outside number 8"),

            ("Damaged playground", "Broken swing in the park",
             "One of the swing chains has snapped. The seat is hanging by a single chain.",
             TicketPriority.Urgent, TicketStatus.Closed, "sara@tickethub.local", "citizen@tickethub.local", 30,
             "Municipal Park, north playground"),

            ("Blocked drain", "Standing water after every rain",
             "The drain at the corner is blocked and the junction floods every time it rains.",
             TicketPriority.High, TicketStatus.OnHold, "sara@tickethub.local", "citizen2@tickethub.local", 9,
             "Corner of Rainbow Road and Al-Quds Street"),

            ("Overgrown vegetation", "Branches blocking the pavement",
             "A tree branch has grown right across the pavement so pushchairs have to go into the road.",
             TicketPriority.Low, TicketStatus.Open, null, "citizen@tickethub.local", 15,
             "Park Lane, near the fountain"),

            ("Damaged signage", "Stop sign knocked over",
             "The stop sign at the junction has been knocked flat, probably by a lorry.",
             TicketPriority.Urgent, TicketStatus.Cancelled, null, "citizen2@tickethub.local", 25,
             "Junction of Main Square and Park Lane")
        };

        var sequence = 1;

        foreach (var s in samples)
        {
            var category = categories[s.Category];
            var createdAt = reference.AddDays(-s.DaysAgo);
            var reporter = users[s.ReporterEmail];

            var ticket = new Ticket
            {
                TicketNumber = $"TKT-{createdAt.Year}-{sequence:D6}",
                Title = s.Title,
                Description = s.Description,
                Priority = s.Priority,
                Status = s.Status,
                CategoryId = category.Id,
                DepartmentId = category.DepartmentId,
                AssignedAgentId = s.AgentEmail is null ? null : agents[s.AgentEmail].Id,
                CreatedByUserId = reporter.Id,
                ReporterName = reporter.DisplayName,
                ReporterEmail = reporter.Email,
                ReporterPhone = "+970-59-000-0000",
                LocationAddress = s.Address,
                Latitude = 31.9522m,
                Longitude = 35.2332m,
                DueAt = createdAt.AddHours(category.SlaHours),

                // We set CreatedAt by hand here, which is the ONE place in the project that
                // does. SaveChanges would stamp "now", and every seeded ticket would look as
                // if it were reported this second — no overdue tickets, no history, no useful
                // demo. The audit stamper only fills CreatedAt when it is still default.
                CreatedAt = createdAt
            };

            if (s.Status is TicketStatus.Resolved or TicketStatus.Closed)
            {
                ticket.ResolvedAt = createdAt.AddHours(category.SlaHours / 2.0);
                if (s.Status == TicketStatus.Closed)
                {
                    ticket.ClosedAt = ticket.ResolvedAt!.Value.AddDays(1);
                }
            }

            // A couple of comments, so the comment table is not empty on a fresh checkout.
            ticket.Comments.Add(new TicketComment
            {
                Body = "Thank you for reporting this. We have logged it and will send someone to look.",
                AuthorId = s.AgentEmail is null ? null : agents[s.AgentEmail].UserId,
                AuthorNameSnapshot = s.AgentEmail is null ? "System" : agents[s.AgentEmail].FullName,
                IsInternal = false,
                CreatedAt = createdAt.AddHours(2)
            });

            if (s.AgentEmail is not null)
            {
                ticket.Comments.Add(new TicketComment
                {
                    Body = "Crew is booked for Thursday morning. Materials are already on the van.",
                    AuthorId = agents[s.AgentEmail].UserId,
                    AuthorNameSnapshot = agents[s.AgentEmail].FullName,
                    IsInternal = true,   // staff-only: a citizen must never see this one
                    CreatedAt = createdAt.AddHours(5)
                });
            }

            ticket.History.Add(new TicketHistory
            {
                Field = "Status",
                OldValue = null,
                NewValue = TicketStatus.Open.ToString(),
                Note = "Ticket created.",
                ChangedAt = createdAt,
                ChangedById = reporter.Id,
                ChangedByName = reporter.DisplayName
            });

            if (s.Status != TicketStatus.Open)
            {
                ticket.History.Add(new TicketHistory
                {
                    Field = "Status",
                    OldValue = TicketStatus.Open.ToString(),
                    NewValue = s.Status.ToString(),
                    ChangedAt = createdAt.AddHours(6),
                    ChangedById = s.AgentEmail is null ? null : agents[s.AgentEmail].UserId,
                    ChangedByName = s.AgentEmail is null ? "System" : agents[s.AgentEmail].FullName
                });
            }

            if (s.Status is TicketStatus.Resolved or TicketStatus.Closed)
            {
                ticket.Rating = new Rating
                {
                    Stars = s.Status == TicketStatus.Closed ? 5 : 4,
                    Comment = "Sorted quickly, thank you.",
                    RatedByUserId = reporter.Id,
                    CreatedAt = ticket.ResolvedAt!.Value.AddHours(3)
                };
            }

            _db.Tickets.Add(ticket);
            sequence++;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Seeded {Count} demo tickets.", samples.Length);
    }
}
