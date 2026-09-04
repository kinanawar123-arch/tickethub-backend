using Microsoft.EntityFrameworkCore;
using TicketHub.Contracts.Agents;
using TicketHub.Contracts.Common;
using TicketHub.DataAccess;
using TicketHub.DataAccess.Entities;
using TicketHub.DataAccess.Repositories;

namespace TicketHub.BusinessLogic.Services;

public interface IAgentService
{
    Task<PagedResult<AgentDto>> SearchAsync(AgentQuery query, CancellationToken ct = default);
    Task<ServiceResult<AgentDto>> GetByIdAsync(int id, CancellationToken ct = default);
    Task<ServiceResult<AgentDto>> CreateAsync(CreateAgentDto dto, CancellationToken ct = default);
    Task<ServiceResult<AgentDto>> UpdateAsync(int id, UpdateAgentDto dto, CancellationToken ct = default);
    Task<ServiceResult<AgentDto>> UpdateProfileAsync(int id, UpdateAgentProfileDto dto, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<SkillDto>> GetSkillsAsync(CancellationToken ct = default);
}

public class AgentService : IAgentService
{
    private readonly IUnitOfWork _uow;
    private readonly TicketHubDbContext _db;

    public AgentService(IUnitOfWork uow, TicketHubDbContext db)
    {
        _uow = uow;
        _db = db;
    }

    public Task<PagedResult<AgentDto>> SearchAsync(AgentQuery query, CancellationToken ct = default)
        => _uow.Agents.SearchAsync(query, ct);

    public async Task<ServiceResult<AgentDto>> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var agent = await _uow.Agents.GetDtoAsync(id, ct);
        return agent is null
            ? ServiceResult<AgentDto>.NotFound($"Agent {id} was not found.")
            : ServiceResult<AgentDto>.Success(agent);
    }

    public async Task<ServiceResult<AgentDto>> CreateAsync(
        CreateAgentDto dto, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == dto.UserId, ct);
        if (user is null)
        {
            return ServiceResult<AgentDto>.Invalid($"User {dto.UserId} does not exist.");
        }

        // One agent per login — the same rule the unique index enforces. Checking first turns
        // the constraint violation into a message somebody can act on.
        if (await _uow.Agents.ExistsAsync(a => a.UserId == dto.UserId, ct))
        {
            return ServiceResult<AgentDto>.Conflict("That user already has an agent record.");
        }

        if (await _uow.Departments.GetByIdAsync(dto.DepartmentId, ct) is null)
        {
            return ServiceResult<AgentDto>.Invalid($"Department {dto.DepartmentId} does not exist.");
        }

        var agent = new Agent
        {
            FullName = dto.FullName.Trim(),
            UserId = dto.UserId,
            DepartmentId = dto.DepartmentId,
            MaxOpenTickets = dto.MaxOpenTickets
        };

        // ResolveSkillsAsync looks all of them up in ONE query and creates the missing ones.
        // Adding tracked Skill entities to the collection is all EF needs — it works out the
        // rows for the AgentSkills join table on its own.
        var skills = await _uow.Agents.ResolveSkillsAsync(dto.Skills, ct);
        foreach (var skill in skills)
        {
            agent.Skills.Add(skill);
        }

        await _uow.Agents.AddAsync(agent, ct);
        await _uow.SaveChangesAsync(ct);

        return await GetByIdAsync(agent.Id, ct);
    }

    public async Task<ServiceResult<AgentDto>> UpdateAsync(
        int id, UpdateAgentDto dto, CancellationToken ct = default)
    {
        // GetForUpdateAsync Includes the skills, which matters here: EF can only work out
        // which join rows to delete if it knows what the collection looked like before.
        // Without the Include the collection is empty, and "replace the skills" would look
        // to EF like "add these, remove nothing".
        var agent = await _uow.Agents.GetForUpdateAsync(id, ct);
        if (agent is null)
        {
            return ServiceResult<AgentDto>.NotFound($"Agent {id} was not found.");
        }

        agent.FullName = dto.FullName.Trim();
        agent.DepartmentId = dto.DepartmentId;
        agent.MaxOpenTickets = dto.MaxOpenTickets;
        agent.IsActive = dto.IsActive;

        var skills = await _uow.Agents.ResolveSkillsAsync(dto.Skills, ct);

        agent.Skills.Clear();
        foreach (var skill in skills)
        {
            agent.Skills.Add(skill);
        }

        await _uow.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    public async Task<ServiceResult<AgentDto>> UpdateProfileAsync(
        int id, UpdateAgentProfileDto dto, CancellationToken ct = default)
    {
        var agent = await _uow.Agents.GetForUpdateAsync(id, ct);
        if (agent is null)
        {
            return ServiceResult<AgentDto>.NotFound($"Agent {id} was not found.");
        }

        // Upsert. The profile is optional (an agent may never have filled it in), so the
        // first save has to create it.
        agent.Profile ??= new AgentProfile { AgentId = agent.Id };

        agent.Profile.Biography = dto.Biography?.Trim();
        agent.Profile.AvatarUrl = dto.AvatarUrl?.Trim();
        agent.Profile.OfficePhone = dto.OfficePhone?.Trim();
        agent.Profile.HiredOn = dto.HiredOn;

        await _uow.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        var agent = await _uow.Agents.GetByIdAsync(id, ct);
        if (agent is null)
        {
            return ServiceResult.NotFound($"Agent {id} was not found.");
        }

        var openTickets = await _uow.Tickets.CountOpenForAgentAsync(id, ct);
        if (openTickets > 0)
        {
            return ServiceResult.Conflict(
                $"{agent.FullName} still has {openTickets} open ticket(s). " +
                "Reassign them first, or deactivate the agent instead.");
        }

        _uow.Agents.Remove(agent);
        await _uow.SaveChangesAsync(ct);

        return ServiceResult.Success();
    }

    public Task<IReadOnlyList<SkillDto>> GetSkillsAsync(CancellationToken ct = default)
        => _uow.Agents.GetSkillsAsync(ct);
}
