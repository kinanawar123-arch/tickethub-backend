using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TicketHub.Contracts.Agents;
using TicketHub.Contracts.Categories;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Departments;
using TicketHub.Contracts.Enums;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess.Repositories;

// =========================================================================
// Departments
// =========================================================================

public interface IDepartmentRepository : IRepository<Department>
{
    Task<PagedResult<DepartmentDto>> SearchAsync(DepartmentQuery query, CancellationToken ct = default);
    Task<DepartmentDto?> GetDtoAsync(int id, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, int? excludeId = null, CancellationToken ct = default);
    Task<bool> HasCategoriesAsync(int departmentId, CancellationToken ct = default);
}

public class DepartmentRepository : Repository<Department>, IDepartmentRepository
{
    public DepartmentRepository(TicketHubDbContext db) : base(db) { }

    public async Task<PagedResult<DepartmentDto>> SearchAsync(
        DepartmentQuery query, CancellationToken ct = default)
    {
        var departments = Db.Departments.AsNoTracking();

        if (query.IsActive.HasValue)
        {
            departments = departments.Where(d => d.IsActive == query.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            departments = departments.Where(d => EF.Functions.Like(d.Name, $"%{term}%"));
        }

        var total = await departments.CountAsync(ct);

        var items = await departments
            .OrderBy(d => d.Name)
            .ThenBy(d => d.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(Projection)
            .ToListAsync(ct);

        return new PagedResult<DepartmentDto>(items, total, query.Page, query.PageSize);
    }

    public Task<DepartmentDto?> GetDtoAsync(int id, CancellationToken ct = default)
        => Db.Departments.AsNoTracking()
             .Where(d => d.Id == id)
             .Select(Projection)
             .FirstOrDefaultAsync(ct)!;

    public Task<bool> NameExistsAsync(string name, int? excludeId = null, CancellationToken ct = default)
        => Db.Departments.AsNoTracking()
             .AnyAsync(d => d.Name == name && (excludeId == null || d.Id != excludeId), ct);

    public Task<bool> HasCategoriesAsync(int departmentId, CancellationToken ct = default)
        => Db.Categories.AsNoTracking().AnyAsync(c => c.DepartmentId == departmentId, ct);

    /// <summary>
    /// One projection, reused by the list query and the single-item query.
    /// </summary>
    /// <remarks>
    /// ⚠ READ THE TYPE. It is an <see cref="Expression{TDelegate}"/>, not a
    /// <c>Func&lt;Department, DepartmentDto&gt;</c> and not a static method — and that
    /// distinction is the difference between one efficient SELECT and a performance bug.
    /// <list type="bullet">
    /// <item><b><c>Expression&lt;Func&lt;&gt;&gt;</c></b> — the compiler hands EF the code as
    ///       <em>data</em> (a tree it can walk), so EF turns it into a SELECT of exactly
    ///       these columns. This is what we want.</item>
    /// <item><b><c>Func&lt;&gt;</c></b> — a compiled delegate. A black box to EF, so it
    ///       fetches whole rows and runs the delegate in memory. Correct results, far more
    ///       data over the wire.</item>
    /// <item><b>A static method</b> called inside <c>Select(d =&gt; Project(d))</c> — EF does
    ///       not inline arbitrary method bodies. Depending on the shape it either throws
    ///       "could not be translated" or quietly falls back to client evaluation, and then
    ///       the navigation properties the projection reads are <c>null</c> and you get a
    ///       <c>NullReferenceException</c> from a line that looks perfectly correct.</item>
    /// </list>
    /// Same three lines of code; three very different outcomes. When you want one reusable
    /// projection, the field type has to be <c>Expression&lt;Func&lt;&gt;&gt;</c>.
    /// </remarks>
    private static readonly Expression<Func<Department, DepartmentDto>> Projection = d => new DepartmentDto
    {
        Id = d.Id,
        Name = d.Name,
        Description = d.Description,
        ContactEmail = d.ContactEmail,
        IsActive = d.IsActive,
        CategoryCount = d.Categories.Count,
        AgentCount = d.Agents.Count,
        OpenTicketCount = d.Tickets.Count(t => t.Status == TicketStatus.Open),
        Audit = new AuditInfoDto
        {
            CreatedAt = d.CreatedAt,
            CreatedById = d.CreatedById,
            UpdatedAt = d.UpdatedAt,
            UpdatedById = d.UpdatedById
        }
    };
}

// =========================================================================
// Categories
// =========================================================================

public interface ICategoryRepository : IRepository<Category>
{
    Task<PagedResult<CategoryDto>> SearchAsync(CategoryQuery query, CancellationToken ct = default);
    Task<CategoryDto?> GetDtoAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryLookupDto>> GetLookupAsync(int? departmentId, CancellationToken ct = default);
    Task<bool> NameExistsInDepartmentAsync(string name, int departmentId, int? excludeId = null, CancellationToken ct = default);

    /// <summary>
    /// The department and SLA of one category, in a single small query.
    /// </summary>
    /// <remarks>
    /// Creating a ticket needs exactly these two values. Loading the whole Category entity to
    /// read two fields is a habit worth breaking early — ask for what you need.
    /// </remarks>
    Task<(int DepartmentId, int SlaHours)?> GetRoutingInfoAsync(int categoryId, CancellationToken ct = default);
}

public class CategoryRepository : Repository<Category>, ICategoryRepository
{
    public CategoryRepository(TicketHubDbContext db) : base(db) { }

    public async Task<PagedResult<CategoryDto>> SearchAsync(CategoryQuery query, CancellationToken ct = default)
    {
        var categories = Db.Categories.AsNoTracking();

        if (query.DepartmentId.HasValue)
        {
            categories = categories.Where(c => c.DepartmentId == query.DepartmentId.Value);
        }

        if (query.IsActive.HasValue)
        {
            categories = categories.Where(c => c.IsActive == query.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            categories = categories.Where(c => EF.Functions.Like(c.Name, $"%{term}%"));
        }

        var total = await categories.CountAsync(ct);

        var items = await categories
            .OrderBy(c => c.Department.Name)
            .ThenBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(Projection)
            .ToListAsync(ct);

        return new PagedResult<CategoryDto>(items, total, query.Page, query.PageSize);
    }

    public Task<CategoryDto?> GetDtoAsync(int id, CancellationToken ct = default)
        => Db.Categories.AsNoTracking()
             .Where(c => c.Id == id)
             .Select(Projection)
             .FirstOrDefaultAsync(ct)!;

    public async Task<IReadOnlyList<CategoryLookupDto>> GetLookupAsync(
        int? departmentId, CancellationToken ct = default)
    {
        var categories = Db.Categories.AsNoTracking().Where(c => c.IsActive);

        if (departmentId.HasValue)
        {
            categories = categories.Where(c => c.DepartmentId == departmentId.Value);
        }

        return await categories
            .OrderBy(c => c.Name)
            .Select(c => new CategoryLookupDto { Id = c.Id, Name = c.Name })
            .ToListAsync(ct);
    }

    public Task<bool> NameExistsInDepartmentAsync(
        string name, int departmentId, int? excludeId = null, CancellationToken ct = default)
        => Db.Categories.AsNoTracking().AnyAsync(
            c => c.Name == name && c.DepartmentId == departmentId && (excludeId == null || c.Id != excludeId), ct);

    public async Task<(int DepartmentId, int SlaHours)?> GetRoutingInfoAsync(
        int categoryId, CancellationToken ct = default)
    {
        var row = await Db.Categories.AsNoTracking()
            .Where(c => c.Id == categoryId && c.IsActive)
            .Select(c => new { c.DepartmentId, c.SlaHours })
            .FirstOrDefaultAsync(ct);

        return row is null ? null : (row.DepartmentId, row.SlaHours);
    }

    /// <summary>Reusable projection. See the long note on DepartmentRepository.Projection
    /// for why the type is Expression&lt;Func&lt;&gt;&gt; and not a method.</summary>
    private static readonly Expression<Func<Category, CategoryDto>> Projection = c => new CategoryDto
    {
        Id = c.Id,
        Name = c.Name,
        Description = c.Description,
        IsActive = c.IsActive,
        DepartmentId = c.DepartmentId,
        DepartmentName = c.Department.Name,
        SlaHours = c.SlaHours,
        TicketCount = c.Tickets.Count,
        Audit = new AuditInfoDto
        {
            CreatedAt = c.CreatedAt,
            CreatedById = c.CreatedById,
            UpdatedAt = c.UpdatedAt,
            UpdatedById = c.UpdatedById
        }
    };
}

// =========================================================================
// Agents
// =========================================================================

public interface IAgentRepository : IRepository<Agent>
{
    Task<PagedResult<AgentDto>> SearchAsync(AgentQuery query, CancellationToken ct = default);
    Task<AgentDto?> GetDtoAsync(int id, CancellationToken ct = default);

    /// <summary>The agent with skills and profile attached, tracked, for the write path.</summary>
    Task<Agent?> GetForUpdateAsync(int id, CancellationToken ct = default);

    Task<Agent?> GetByUserIdAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// The least-loaded active agent in a department who is still under their cap.
    /// The auto-assignment rule, as one query.
    /// </summary>
    Task<int?> FindLeastLoadedAgentAsync(int departmentId, CancellationToken ct = default);

    Task<IReadOnlyList<SkillDto>> GetSkillsAsync(CancellationToken ct = default);

    /// <summary>Finds the named skills, creating any that do not exist yet.</summary>
    Task<List<Skill>> ResolveSkillsAsync(IEnumerable<string> names, CancellationToken ct = default);
}

public class AgentRepository : Repository<Agent>, IAgentRepository
{
    public AgentRepository(TicketHubDbContext db) : base(db) { }

    public async Task<PagedResult<AgentDto>> SearchAsync(AgentQuery query, CancellationToken ct = default)
    {
        var agents = Db.Agents.AsNoTracking();

        if (query.DepartmentId.HasValue)
        {
            agents = agents.Where(a => a.DepartmentId == query.DepartmentId.Value);
        }

        if (query.IsActive.HasValue)
        {
            agents = agents.Where(a => a.IsActive == query.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Skill))
        {
            // Any() across the N:N. EF turns this into an EXISTS with a join to AgentSkills —
            // it does not load a single skill row.
            var skill = query.Skill.Trim();
            agents = agents.Where(a => a.Skills.Any(s => s.Name == skill));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            agents = agents.Where(a => EF.Functions.Like(a.FullName, $"%{term}%"));
        }

        if (query.HasCapacity == true)
        {
            // Comparing a COUNT of related rows against a column on this row, in SQL.
            agents = agents.Where(a =>
                a.AssignedTickets.Count(t =>
                    t.Status == TicketStatus.Open ||
                    t.Status == TicketStatus.InProgress ||
                    t.Status == TicketStatus.OnHold) < a.MaxOpenTickets);
        }

        var total = await agents.CountAsync(ct);

        var items = await agents
            .OrderBy(a => a.FullName)
            .ThenBy(a => a.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(Projection)
            .ToListAsync(ct);

        return new PagedResult<AgentDto>(items, total, query.Page, query.PageSize);
    }

    public Task<AgentDto?> GetDtoAsync(int id, CancellationToken ct = default)
        => Db.Agents.AsNoTracking()
             .Where(a => a.Id == id)
             .Select(Projection)
             .FirstOrDefaultAsync(ct)!;

    public Task<Agent?> GetForUpdateAsync(int id, CancellationToken ct = default)
        // Include, not a projection: this is the WRITE path, and we need real tracked Skill
        // entities so EF can work out which rows to add to and remove from the join table.
        // A projection would give us strings, which EF cannot save.
        => Db.Agents
             .Include(a => a.Skills)
             .Include(a => a.Profile)
             .FirstOrDefaultAsync(a => a.Id == id, ct);

    public Task<Agent?> GetByUserIdAsync(int userId, CancellationToken ct = default)
        => Db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, ct);

    public async Task<int?> FindLeastLoadedAgentAsync(int departmentId, CancellationToken ct = default)
    {
        // Everything here runs in SQL: the filter, the per-agent count, the capacity
        // comparison and the ordering. One round trip, one row back.
        var candidate = await Db.Agents.AsNoTracking()
            .Where(a => a.DepartmentId == departmentId && a.IsActive)
            .Select(a => new
            {
                a.Id,
                a.MaxOpenTickets,
                OpenCount = a.AssignedTickets.Count(t =>
                    t.Status == TicketStatus.Open ||
                    t.Status == TicketStatus.InProgress ||
                    t.Status == TicketStatus.OnHold)
            })
            .Where(a => a.OpenCount < a.MaxOpenTickets)
            .OrderBy(a => a.OpenCount)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync(ct);

        return candidate?.Id;
    }

    public async Task<IReadOnlyList<SkillDto>> GetSkillsAsync(CancellationToken ct = default)
        => await Db.Skills.AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new SkillDto { Id = s.Id, Name = s.Name, AgentCount = s.Agents.Count })
            .ToListAsync(ct);

    public async Task<List<Skill>> ResolveSkillsAsync(
        IEnumerable<string> names, CancellationToken ct = default)
    {
        var wanted = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wanted.Count == 0)
        {
            return new List<Skill>();
        }

        // ONE query for all of them, not one per name.
        //
        // The N+1 version — foreach (var name in wanted) await db.Skills.FirstOrDefault(...) —
        // looks harmless with three skills and is a disaster with three hundred. Contains()
        // becomes SQL IN (...).
        var existing = await Db.Skills.Where(s => wanted.Contains(s.Name)).ToListAsync(ct);

        var result = new List<Skill>(existing);

        foreach (var name in wanted)
        {
            if (!existing.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                var created = new Skill { Name = name };
                Db.Skills.Add(created);
                result.Add(created);
            }
        }

        return result;
    }

    /// <summary>Reusable projection. See the long note on DepartmentRepository.Projection.</summary>
    private static readonly Expression<Func<Agent, AgentDto>> Projection = a => new AgentDto
    {
        Id = a.Id,
        FullName = a.FullName,
        Email = a.User.Email ?? string.Empty,
        IsActive = a.IsActive,
        DepartmentId = a.DepartmentId,
        DepartmentName = a.Department.Name,
        MaxOpenTickets = a.MaxOpenTickets,
        OpenTicketCount = a.AssignedTickets.Count(t =>
            t.Status == TicketStatus.Open ||
            t.Status == TicketStatus.InProgress ||
            t.Status == TicketStatus.OnHold),
        Skills = a.Skills.Select(s => s.Name).ToList(),
        Profile = a.Profile == null ? null : new AgentProfileDto
        {
            Biography = a.Profile.Biography,
            AvatarUrl = a.Profile.AvatarUrl,
            OfficePhone = a.Profile.OfficePhone,
            HiredOn = a.Profile.HiredOn
        },
        Audit = new AuditInfoDto
        {
            CreatedAt = a.CreatedAt,
            CreatedById = a.CreatedById,
            UpdatedAt = a.UpdatedAt,
            UpdatedById = a.UpdatedById
        }
    };
}
