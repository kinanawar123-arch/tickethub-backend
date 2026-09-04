namespace TicketHub.DataAccess.Entities;

/// <summary>
/// The kind of problem a ticket is about: "Pothole", "Broken streetlight", "Illegal dumping".
/// </summary>
public class Category : AuditableEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// How many hours the municipality promises to take. Copied onto the ticket as a
    /// concrete <see cref="Ticket.DueAt"/> when the ticket is created.
    /// </summary>
    /// <remarks>
    /// Storing the resulting date on the ticket, rather than recomputing it from the category
    /// every time, is on purpose. If the SLA policy changes next year, tickets already in
    /// flight keep the promise that was made when they were filed. Copying the *value* at
    /// the moment of the decision is how you keep history honest.
    /// </remarks>
    public int SlaHours { get; set; } = 72;

    // ----- Required 1:N: every category belongs to exactly one department -----

    /// <summary>
    /// The foreign key, exposed as a normal property.
    /// </summary>
    /// <remarks>
    /// You could model this relationship with only the <see cref="Department"/> navigation and
    /// let EF create the column as a shadow property. Then you could not set the relationship
    /// without first loading a whole Department from the database. With the FK exposed,
    /// <c>category.DepartmentId = 3</c> is one assignment and zero queries.
    /// <b>Always expose the FK.</b>
    /// </remarks>
    public int DepartmentId { get; set; }

    /// <summary>
    /// <c>= null!</c> reads like a lie, and in a sense it is: it tells the compiler "trust me,
    /// this will not be null" so nullable reference types stop warning, while the truth is
    /// that EF fills it in when the row is loaded and it genuinely IS null on a
    /// freshly-constructed object. This is the accepted idiom for a required navigation.
    /// </summary>
    public Department Department { get; set; } = null!;

    // ----- The "one" side of 1:N with Ticket -----

    /// <summary>
    /// <c>ICollection&lt;T&gt;</c> rather than <c>List&lt;T&gt;</c> or
    /// <c>IEnumerable&lt;T&gt;</c>: EF must be able to add to it (which rules out
    /// IEnumerable) and the interface leaves EF free to substitute its own collection type.
    /// Initialising it means <c>category.Tickets.Add(...)</c> never throws on a new object.
    /// </summary>
    public ICollection<Ticket> Tickets { get; set; } = new List<Ticket>();
}
