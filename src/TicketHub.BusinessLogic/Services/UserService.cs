using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Auth;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Enums;
using TicketHub.DataAccess;
using TicketHub.DataAccess.Entities;
using TicketHub.DataAccess.Repositories;

namespace TicketHub.BusinessLogic.Services;

public interface IUserService
{
    Task<PagedResult<UserDto>> SearchAsync(PagedQuery query, CancellationToken ct = default);
    Task<ServiceResult<UserDto>> GetByIdAsync(int id, CancellationToken ct = default);
    Task<ServiceResult<UserDto>> CreateAsync(CreateUserDto dto, CancellationToken ct = default);
    Task<ServiceResult<UserDto>> UpdateAsync(int id, UpdateUserDto dto, CancellationToken ct = default);

    /// <summary>Deactivate — never delete. See the note on the method.</summary>
    Task<ServiceResult> DeactivateAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// Admin-only user management: create staff logins, change roles, deactivate accounts.
/// </summary>
public class UserService : IUserService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<ApplicationRole> _roles;
    private readonly TicketHubDbContext _db;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<UserService> _log;

    public UserService(
        UserManager<ApplicationUser> users,
        RoleManager<ApplicationRole> roles,
        TicketHubDbContext db,
        IUnitOfWork uow,
        ICurrentUser currentUser,
        ILogger<UserService> log)
    {
        _users = users;
        _roles = roles;
        _db = db;
        _uow = uow;
        _currentUser = currentUser;
        _log = log;
    }

    public async Task<PagedResult<UserDto>> SearchAsync(PagedQuery query, CancellationToken ct = default)
    {
        var users = _db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            users = users.Where(u =>
                EF.Functions.Like(u.DisplayName, $"%{term}%") ||
                EF.Functions.Like(u.Email!, $"%{term}%"));
        }

        var total = await users.CountAsync(ct);

        // Projected, so PasswordHash and SecurityStamp never even leave the database.
        // The strongest way to not leak a secret is to not select it.
        var items = await users
            .OrderBy(u => u.DisplayName)
            .ThenBy(u => u.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Email = u.Email ?? string.Empty,
                DisplayName = u.DisplayName,
                PhoneNumber = u.PhoneNumber,
                UserType = u.UserType,
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt,
                AgentId = u.Agent != null ? u.Agent.Id : null,
                DepartmentId = u.Agent != null ? u.Agent.DepartmentId : null,
                DepartmentName = u.Agent != null ? u.Agent.Department.Name : null,

                // Roles come through Identity's join tables. This turns into a correlated
                // sub-select per user — acceptable on a page of 20, and the reason you do not
                // want it on a page of 10,000.
                Roles = _db.UserRoles
                    .Where(ur => ur.UserId == u.Id)
                    .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name!)
                    .ToList()
            })
            .ToListAsync(ct);

        return new PagedResult<UserDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<ServiceResult<UserDto>> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(id.ToString());
        return user is null
            ? ServiceResult<UserDto>.NotFound($"User {id} was not found.")
            : ServiceResult<UserDto>.Success(await MapAsync(user, ct));
    }

    public async Task<ServiceResult<UserDto>> CreateAsync(
        CreateUserDto dto, CancellationToken ct = default)
    {
        if (await _users.FindByEmailAsync(dto.Email) is not null)
        {
            return ServiceResult<UserDto>.Conflict("An account with that email already exists.");
        }

        // Validate the role names before creating anything. Identity's AddToRolesAsync fails
        // silently-ish on unknown names, and an account that was supposed to be an Admin and
        // quietly has no roles at all is a confusing morning for somebody.
        var unknownRoles = new List<string>();
        foreach (var role in dto.Roles)
        {
            if (!await _roles.RoleExistsAsync(role))
            {
                unknownRoles.Add(role);
            }
        }

        if (unknownRoles.Count > 0)
        {
            return ServiceResult<UserDto>.Invalid(
                $"Unknown role(s): {string.Join(", ", unknownRoles)}. " +
                $"Valid roles are: {string.Join(", ", AppRoles.All)}.");
        }

        if (dto.UserType == UserType.Employee && dto.DepartmentId is null)
        {
            return ServiceResult<UserDto>.Invalid("An employee must be given a department.");
        }

        var user = new ApplicationUser
        {
            UserName = dto.Email,
            Email = dto.Email,
            DisplayName = dto.DisplayName.Trim(),
            PhoneNumber = dto.PhoneNumber,
            UserType = dto.UserType,
            EmailConfirmed = true,   // created by an admin, so the address is already trusted
            IsActive = true
        };

        var created = await _users.CreateAsync(user, dto.Password);
        if (!created.Succeeded)
        {
            return ServiceResult<UserDto>.Invalid(new Dictionary<string, string[]>
            {
                ["Password"] = created.Errors.Select(e => e.Description).ToArray()
            });
        }

        if (dto.Roles.Count > 0)
        {
            await _users.AddToRolesAsync(user, dto.Roles);
        }

        // An employee needs an Agent row, or they have a login and no way to be assigned
        // work — which looks exactly like a bug to whoever reports it.
        if (dto.UserType == UserType.Employee && dto.DepartmentId.HasValue)
        {
            await _uow.Agents.AddAsync(new Agent
            {
                FullName = user.DisplayName,
                UserId = user.Id,
                DepartmentId = dto.DepartmentId.Value
            }, ct);

            await _uow.SaveChangesAsync(ct);
        }

        _log.LogInformation("Admin {AdminId} created user {UserId} ({Email})",
            _currentUser.UserId, user.Id, user.Email);

        return ServiceResult<UserDto>.Success(await MapAsync(user, ct));
    }

    public async Task<ServiceResult<UserDto>> UpdateAsync(
        int id, UpdateUserDto dto, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return ServiceResult<UserDto>.NotFound($"User {id} was not found.");
        }

        // An admin removing their own Admin role locks everyone out of user management if
        // they were the last one. Cheap guard, expensive to be without.
        if (user.Id == _currentUser.UserId && !dto.Roles.Contains(AppRoles.Admin)
            && await _users.IsInRoleAsync(user, AppRoles.Admin))
        {
            return ServiceResult<UserDto>.Conflict("You cannot remove your own Admin role.");
        }

        user.DisplayName = dto.DisplayName.Trim();
        user.PhoneNumber = dto.PhoneNumber;
        user.IsActive = dto.IsActive;

        await _users.UpdateAsync(user);

        // Roles: work out the difference rather than remove-all-then-add-all. The blunt
        // version briefly leaves the user with no roles, and if the second call fails they
        // stay that way.
        var current = await _users.GetRolesAsync(user);
        var toRemove = current.Except(dto.Roles).ToList();
        var toAdd = dto.Roles.Except(current).ToList();

        if (toRemove.Count > 0)
        {
            await _users.RemoveFromRolesAsync(user, toRemove);
        }

        if (toAdd.Count > 0)
        {
            await _users.AddToRolesAsync(user, toAdd);
        }

        // Roles live in the token, so a change does not take effect until the token is
        // refreshed. Bumping the security stamp invalidates every existing token
        // immediately — the difference between "your demotion applies in 15 minutes" and
        // "your demotion applies now". For a role change, now is usually what you want.
        await _users.UpdateSecurityStampAsync(user);

        return ServiceResult<UserDto>.Success(await MapAsync(user, ct));
    }

    /// <summary>
    /// Deactivates an account. There is no delete, and that is on purpose.
    /// </summary>
    /// <remarks>
    /// Their tickets, comments, history rows and audit entries all point at this user. Delete
    /// the row and either those references break, or they cascade and take real records with
    /// them. Deactivating keeps every name in the history resolving correctly while making
    /// the login stop working — which is the actual requirement behind "delete this user".
    /// </remarks>
    public async Task<ServiceResult> DeactivateAsync(int id, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return ServiceResult.NotFound($"User {id} was not found.");
        }

        if (user.Id == _currentUser.UserId)
        {
            return ServiceResult.Conflict("You cannot deactivate your own account.");
        }

        user.IsActive = false;
        await _users.UpdateAsync(user);

        // Two doors, both need closing:
        //   1. Bump the security stamp → every access token they hold stops validating.
        //   2. Revoke the refresh tokens → they cannot mint a new one.
        // Do only the first and they refresh their way back in. Do only the second and they
        // keep working until the access token expires.
        await _users.UpdateSecurityStampAsync(user);

        var now = DateTime.UtcNow;
        await _db.RefreshTokens
            .Where(t => t.UserId == id && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.RevokedAt, now)
                      .SetProperty(t => t.RevokedReason, "account deactivated"),
                ct);

        _log.LogWarning("User {UserId} deactivated by admin {AdminId}", id, _currentUser.UserId);

        return ServiceResult.Success();
    }

    private async Task<UserDto> MapAsync(ApplicationUser user, CancellationToken ct)
    {
        var roles = await _users.GetRolesAsync(user);
        var agent = await _uow.Agents.GetByUserIdAsync(user.Id, ct);

        string? departmentName = null;
        if (agent is not null)
        {
            departmentName = (await _uow.Departments.GetByIdAsync(agent.DepartmentId, ct))?.Name;
        }

        return new UserDto
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            PhoneNumber = user.PhoneNumber,
            UserType = user.UserType,
            IsActive = user.IsActive,
            Roles = roles.ToList(),
            AgentId = agent?.Id,
            DepartmentId = agent?.DepartmentId,
            DepartmentName = departmentName,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt
        };
    }
}
