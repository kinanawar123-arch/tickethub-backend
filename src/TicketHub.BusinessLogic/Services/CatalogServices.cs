using TicketHub.Contracts.Categories;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Departments;
using TicketHub.DataAccess.Entities;
using TicketHub.DataAccess.Repositories;

namespace TicketHub.BusinessLogic.Services;

// =========================================================================
// Departments
// =========================================================================

public interface IDepartmentService
{
    Task<PagedResult<DepartmentDto>> SearchAsync(DepartmentQuery query, CancellationToken ct = default);
    Task<ServiceResult<DepartmentDto>> GetByIdAsync(int id, CancellationToken ct = default);
    Task<ServiceResult<DepartmentDto>> CreateAsync(CreateDepartmentDto dto, CancellationToken ct = default);
    Task<ServiceResult<DepartmentDto>> UpdateAsync(int id, UpdateDepartmentDto dto, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// Straightforward CRUD. Read it as the baseline that <see cref="TicketService"/> departs from.
/// </summary>
/// <remarks>
/// Not every service needs a workflow, notifications and a security filter. A service whose
/// only rule is "names must be unique" should look exactly like this — thin. Adding ceremony
/// that has nothing to do is how a codebase gets hard to read.
/// </remarks>
public class DepartmentService : IDepartmentService
{
    private readonly IUnitOfWork _uow;

    public DepartmentService(IUnitOfWork uow) => _uow = uow;

    public Task<PagedResult<DepartmentDto>> SearchAsync(DepartmentQuery query, CancellationToken ct = default)
        => _uow.Departments.SearchAsync(query, ct);

    public async Task<ServiceResult<DepartmentDto>> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var department = await _uow.Departments.GetDtoAsync(id, ct);
        return department is null
            ? ServiceResult<DepartmentDto>.NotFound($"Department {id} was not found.")
            : ServiceResult<DepartmentDto>.Success(department);
    }

    public async Task<ServiceResult<DepartmentDto>> CreateAsync(
        CreateDepartmentDto dto, CancellationToken ct = default)
    {
        var name = dto.Name.Trim();

        // The friendly half of the uniqueness rule. The unique index is the half that is
        // actually true — two simultaneous requests can both pass this check, and only the
        // index stops both inserts. This exists to turn the common case into a clean 400
        // instead of a 500 from a constraint violation.
        if (await _uow.Departments.NameExistsAsync(name, null, ct))
        {
            return ServiceResult<DepartmentDto>.Conflict($"A department named '{name}' already exists.");
        }

        var department = new Department
        {
            Name = name,
            Description = dto.Description?.Trim(),
            ContactEmail = dto.ContactEmail?.Trim()
        };

        await _uow.Departments.AddAsync(department, ct);
        await _uow.SaveChangesAsync(ct);

        return await GetByIdAsync(department.Id, ct);
    }

    public async Task<ServiceResult<DepartmentDto>> UpdateAsync(
        int id, UpdateDepartmentDto dto, CancellationToken ct = default)
    {
        var department = await _uow.Departments.GetByIdAsync(id, ct);
        if (department is null)
        {
            return ServiceResult<DepartmentDto>.NotFound($"Department {id} was not found.");
        }

        var name = dto.Name.Trim();

        // excludeId, so renaming a department to the name it already has is not a conflict
        // with itself. An easy bug to write and an annoying one to have.
        if (await _uow.Departments.NameExistsAsync(name, id, ct))
        {
            return ServiceResult<DepartmentDto>.Conflict($"A department named '{name}' already exists.");
        }

        department.Name = name;
        department.Description = dto.Description?.Trim();
        department.ContactEmail = dto.ContactEmail?.Trim();
        department.IsActive = dto.IsActive;

        await _uow.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        var department = await _uow.Departments.GetByIdAsync(id, ct);
        if (department is null)
        {
            return ServiceResult.NotFound($"Department {id} was not found.");
        }

        // Check the children before deleting the parent.
        //
        // The FK is configured DeleteBehavior.Restrict, so the database would refuse anyway —
        // but it would refuse with a foreign key violation, which reaches the user as a 500
        // and tells them nothing. Checking first turns it into a 409 that says what to do.
        if (await _uow.Departments.HasCategoriesAsync(id, ct))
        {
            return ServiceResult.Conflict(
                "This department still has categories. Move or remove them first, " +
                "or deactivate the department instead of deleting it.");
        }

        _uow.Departments.Remove(department);
        await _uow.SaveChangesAsync(ct);

        return ServiceResult.Success();
    }
}

// =========================================================================
// Categories
// =========================================================================

public interface ICategoryService
{
    Task<PagedResult<CategoryDto>> SearchAsync(CategoryQuery query, CancellationToken ct = default);
    Task<ServiceResult<CategoryDto>> GetByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryLookupDto>> GetLookupAsync(int? departmentId, CancellationToken ct = default);
    Task<ServiceResult<CategoryDto>> CreateAsync(CreateCategoryDto dto, CancellationToken ct = default);
    Task<ServiceResult<CategoryDto>> UpdateAsync(int id, UpdateCategoryDto dto, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);
}

public class CategoryService : ICategoryService
{
    private readonly IUnitOfWork _uow;

    public CategoryService(IUnitOfWork uow) => _uow = uow;

    public Task<PagedResult<CategoryDto>> SearchAsync(CategoryQuery query, CancellationToken ct = default)
        => _uow.Categories.SearchAsync(query, ct);

    public async Task<ServiceResult<CategoryDto>> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var category = await _uow.Categories.GetDtoAsync(id, ct);
        return category is null
            ? ServiceResult<CategoryDto>.NotFound($"Category {id} was not found.")
            : ServiceResult<CategoryDto>.Success(category);
    }

    public Task<IReadOnlyList<CategoryLookupDto>> GetLookupAsync(
        int? departmentId, CancellationToken ct = default)
        => _uow.Categories.GetLookupAsync(departmentId, ct);

    public async Task<ServiceResult<CategoryDto>> CreateAsync(
        CreateCategoryDto dto, CancellationToken ct = default)
    {
        var department = await _uow.Departments.GetByIdAsync(dto.DepartmentId, ct);
        if (department is null)
        {
            return ServiceResult<CategoryDto>.Invalid($"Department {dto.DepartmentId} does not exist.");
        }

        var name = dto.Name.Trim();

        // Unique WITHIN the department, matching the composite index. Roads and Parks may
        // both have a "Maintenance" category and that is not a mistake.
        if (await _uow.Categories.NameExistsInDepartmentAsync(name, dto.DepartmentId, null, ct))
        {
            return ServiceResult<CategoryDto>.Conflict(
                $"'{name}' already exists in {department.Name}.");
        }

        var category = new Category
        {
            Name = name,
            Description = dto.Description?.Trim(),
            DepartmentId = dto.DepartmentId,
            SlaHours = dto.SlaHours
        };

        await _uow.Categories.AddAsync(category, ct);
        await _uow.SaveChangesAsync(ct);

        return await GetByIdAsync(category.Id, ct);
    }

    public async Task<ServiceResult<CategoryDto>> UpdateAsync(
        int id, UpdateCategoryDto dto, CancellationToken ct = default)
    {
        var category = await _uow.Categories.GetByIdAsync(id, ct);
        if (category is null)
        {
            return ServiceResult<CategoryDto>.NotFound($"Category {id} was not found.");
        }

        var name = dto.Name.Trim();

        if (await _uow.Categories.NameExistsInDepartmentAsync(name, dto.DepartmentId, id, ct))
        {
            return ServiceResult<CategoryDto>.Conflict($"'{name}' already exists in that department.");
        }

        category.Name = name;
        category.Description = dto.Description?.Trim();
        category.DepartmentId = dto.DepartmentId;
        category.SlaHours = dto.SlaHours;
        category.IsActive = dto.IsActive;

        // NOTE what we deliberately do NOT do: back-fill Ticket.DepartmentId for tickets
        // already in this category. Existing tickets keep the department they were filed
        // against, which is the honest answer — moving a category should not silently rewrite
        // the history of work that was already routed and possibly already done.
        //
        // A real system would offer "move existing tickets too?" as an explicit choice.

        await _uow.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        var category = await _uow.Categories.GetByIdAsync(id, ct);
        if (category is null)
        {
            return ServiceResult.NotFound($"Category {id} was not found.");
        }

        var ticketCount = await _uow.Tickets.CountAsync(t => t.CategoryId == id, ct);
        if (ticketCount > 0)
        {
            return ServiceResult.Conflict(
                $"This category is used by {ticketCount} ticket(s). Deactivate it instead — " +
                "existing tickets keep working and no new ones can be filed against it.");
        }

        _uow.Categories.Remove(category);
        await _uow.SaveChangesAsync(ct);

        return ServiceResult.Success();
    }
}
