using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketHub.BusinessLogic.Services;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Agents;
using TicketHub.Contracts.Categories;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Departments;

namespace TicketHub.Api.Controllers;

/// <summary>Departments — Roads, Sanitation, Lighting, Parks.</summary>
/// <remarks>
/// A textbook CRUD controller, and the clearest illustration of how thin these should be:
/// each action is one line. Notice how the authorization differs per verb — reading is open
/// to any signed-in user, writing is Admin only. That asymmetry is normal and worth copying.
/// </remarks>
[Route("api/departments")]
[Authorize]
[Produces("application/json")]
public class DepartmentsController : ApiControllerBase
{
    private readonly IDepartmentService _departments;

    public DepartmentsController(IDepartmentService departments) => _departments = departments;

    /// <summary>Lists departments.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<DepartmentDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<DepartmentDto>>> Search(
        [FromQuery] DepartmentQuery query, CancellationToken ct)
        => Ok(await _departments.SearchAsync(query, ct));

    /// <summary>One department with its category, agent and open-ticket counts.</summary>
    [HttpGet("{id:int}", Name = nameof(GetDepartment))]
    [ProducesResponseType(typeof(DepartmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DepartmentDto>> GetDepartment(int id, CancellationToken ct)
        => ToActionResult(await _departments.GetByIdAsync(id, ct));

    /// <summary>Creates a department.</summary>
    [HttpPost]
    [Authorize(Roles = AppRoles.Admin)]
    [ProducesResponseType(typeof(DepartmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DepartmentDto>> Create(
        [FromBody] CreateDepartmentDto dto, CancellationToken ct)
    {
        var result = await _departments.CreateAsync(dto, ct);
        return CreatedResult(result, nameof(GetDepartment), new { id = result.Value?.Id });
    }

    /// <summary>Updates a department.</summary>
    [HttpPut("{id:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    [ProducesResponseType(typeof(DepartmentDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DepartmentDto>> Update(
        int id, [FromBody] UpdateDepartmentDto dto, CancellationToken ct)
        => ToActionResult(await _departments.UpdateAsync(id, dto, ct));

    /// <summary>Deletes a department, if nothing depends on it.</summary>
    /// <response code="409">It still has categories. Deactivate it instead.</response>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Delete(int id, CancellationToken ct)
        => ToActionResult(await _departments.DeleteAsync(id, ct));
}

/// <summary>Ticket categories, each owned by a department.</summary>
[Route("api/categories")]
[Authorize]
[Produces("application/json")]
public class CategoriesController : ApiControllerBase
{
    private readonly ICategoryService _categories;

    public CategoriesController(ICategoryService categories) => _categories = categories;

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<CategoryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<CategoryDto>>> Search(
        [FromQuery] CategoryQuery query, CancellationToken ct)
        => Ok(await _categories.SearchAsync(query, ct));

    /// <summary>Id and name only — for filling a dropdown on the "report a problem" form.</summary>
    /// <remarks>
    /// A separate endpoint rather than a flag on the list endpoint. The two have different
    /// shapes, different sizes and different callers; splitting them keeps each one honest
    /// and means the dropdown does not download an audit block per row.
    /// </remarks>
    [HttpGet("lookup")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<CategoryLookupDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryLookupDto>>> Lookup(
    [FromQuery] int? departmentId, CancellationToken ct)
    => Ok(await _categories.GetLookupAsync(departmentId, ct));

    [HttpGet("{id:int}", Name = nameof(GetCategory))]
    [ProducesResponseType(typeof(CategoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CategoryDto>> GetCategory(int id, CancellationToken ct)
        => ToActionResult(await _categories.GetByIdAsync(id, ct));

    [HttpPost]
    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Supervisor}")]
    [ProducesResponseType(typeof(CategoryDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<CategoryDto>> Create(
        [FromBody] CreateCategoryDto dto, CancellationToken ct)
    {
        var result = await _categories.CreateAsync(dto, ct);
        return CreatedResult(result, nameof(GetCategory), new { id = result.Value?.Id });
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Supervisor}")]
    [ProducesResponseType(typeof(CategoryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CategoryDto>> Update(
        int id, [FromBody] UpdateCategoryDto dto, CancellationToken ct)
        => ToActionResult(await _categories.UpdateAsync(id, dto, ct));

    [HttpDelete("{id:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Delete(int id, CancellationToken ct)
        => ToActionResult(await _categories.DeleteAsync(id, ct));
}

/// <summary>Agents — the staff who resolve tickets.</summary>
[Route("api/agents")]
[Authorize(Roles = AppRoles.Staff)]   // citizens have no reason to browse the staff list
[Produces("application/json")]
public class AgentsController : ApiControllerBase
{
    private readonly IAgentService _agents;

    public AgentsController(IAgentService agents) => _agents = agents;

    /// <summary>Lists agents, optionally only those with spare capacity.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<AgentDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AgentDto>>> Search(
        [FromQuery] AgentQuery query, CancellationToken ct)
        => Ok(await _agents.SearchAsync(query, ct));

    [HttpGet("{id:int}", Name = nameof(GetAgent))]
    [ProducesResponseType(typeof(AgentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentDto>> GetAgent(int id, CancellationToken ct)
        => ToActionResult(await _agents.GetByIdAsync(id, ct));

    /// <summary>All skill tags, with how many agents hold each.</summary>
    [HttpGet("skills")]
    [ProducesResponseType(typeof(IReadOnlyList<SkillDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SkillDto>>> Skills(CancellationToken ct)
        => Ok(await _agents.GetSkillsAsync(ct));

    [HttpPost]
    [Authorize(Roles = AppRoles.Admin)]
    [ProducesResponseType(typeof(AgentDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<AgentDto>> Create(
        [FromBody] CreateAgentDto dto, CancellationToken ct)
    {
        var result = await _agents.CreateAsync(dto, ct);
        return CreatedResult(result, nameof(GetAgent), new { id = result.Value?.Id });
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Supervisor}")]
    [ProducesResponseType(typeof(AgentDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AgentDto>> Update(
        int id, [FromBody] UpdateAgentDto dto, CancellationToken ct)
        => ToActionResult(await _agents.UpdateAsync(id, dto, ct));

    /// <summary>Updates the 1:1 profile row (biography, avatar, office phone).</summary>
    [HttpPut("{id:int}/profile")]
    [ProducesResponseType(typeof(AgentDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AgentDto>> UpdateProfile(
        int id, [FromBody] UpdateAgentProfileDto dto, CancellationToken ct)
        => ToActionResult(await _agents.UpdateProfileAsync(id, dto, ct));

    [HttpDelete("{id:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Delete(int id, CancellationToken ct)
        => ToActionResult(await _agents.DeleteAsync(id, ct));
}
