# TicketHub

A municipal ticketing API — citizens report problems, departments fix them — built as a
**reference project for the 2026 backend training**. It is deliberately complete: every
pattern the lessons cover appears here in working code, with the reasoning written into
the comments rather than left as an exercise.

Read the code in the order of section 4 below. Almost every non-obvious line has a comment
explaining not just *what* it does but *why it is that way and what breaks otherwise* —
that is the part worth your time.

---

## 1. Run it

You need [.NET 10 SDK](https://dotnet.microsoft.com/download) and Docker Desktop.

**1. Start the database.**

```bash
docker compose up -d
```

**2. Set your secrets.** These are not in the repository — no connection string and no signing
key is ever committed, so each developer supplies their own once. Run these four commands from
the solution root; they are stored outside the project folder and survive `git clean`.

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost,1433;Database=TicketHub;User Id=sa;Password=TicketHub!Dev2026;TrustServerCertificate=True;MultipleActiveResultSets=True" --project src/TicketHub.Api
```

```bash
dotnet user-secrets set "Jwt:Key" "replace-this-with-a-long-random-string-at-least-32-chars" --project src/TicketHub.Api
```

If you skip these, start-up stops immediately and tells you exactly which command to run —
that is deliberate, and much kinder than an empty-key exception thrown from inside the JWT
library twenty seconds later.

**Email is optional.** Out of the box `Email:Provider` is `Console`, which writes the message —
including the reset link and its token — straight to the log, so the forgot-password and
confirm-email flows are fully usable with no account anywhere. To send real mail instead:

```bash
dotnet user-secrets set "Email:Provider" "Mailjet" --project src/TicketHub.Api
dotnet user-secrets set "Email:MailjetApiKey" "<key>" --project src/TicketHub.Api
dotnet user-secrets set "Email:MailjetApiSecret" "<secret>" --project src/TicketHub.Api
dotnet user-secrets set "Email:FromAddress" "<a sender validated in your Mailjet account>" --project src/TicketHub.Api
```

`Smtp` is the third option and works with Gmail, Office 365 or a local catcher — see the
`Email` section of `appsettings.json`.

**3. Run it.**

```bash
dotnet run --project src/TicketHub.Api
```

Then open **<http://localhost:5048/swagger>**.

On first run the API applies migrations and seeds demo data automatically. It takes about
30 seconds the very first time while SQL Server initialises — if it fails, wait for
`docker compose ps` to show `healthy` and run it again.

### Demo accounts

All use the password `Passw0rd!`.

| Email | Role | Sees |
|---|---|---|
| `admin@tickethub.local` | Admin | Everything |
| `supervisor@tickethub.local` | Supervisor | Roads department |
| `sara@tickethub.local` | Agent | Roads department |
| `omar@tickethub.local` | Agent | Sanitation department |
| `lina@tickethub.local` | Agent | Lighting department |
| `citizen@tickethub.local` | Citizen | Only the tickets they reported |
| `citizen2@tickethub.local` | Citizen | Only the tickets they reported |

In Swagger: `POST /api/auth/login`, copy `accessToken`, click **Authorize**, paste it.

### Live chat

Open **<http://localhost:5048/chat-demo.html>** in two browser windows, sign in as two
different people, open the conversation for ticket 1, and type. Swagger cannot exercise
WebSockets, which is what that page is for.

### If SQL Server will not start

Apple Silicon runs the SQL Server image under emulation. If it refuses, switch to
PostgreSQL — instructions are in the comments at the bottom of `docker-compose.yml`.

---

## 2. The four projects

```
TicketHub.Api            ← controllers, SignalR hubs, middleware, Program.cs
      │  depends on
TicketHub.BusinessLogic  ← services, workflow rules, JWT issuing
      │  depends on
TicketHub.DataAccess     ← entities, DbContext, configurations, repositories, migrations
      │  depends on
TicketHub.Contracts      ← DTOs, enums, ServiceResult — depends on nothing
```

**Dependencies only ever point downwards.** That single rule is what the whole layout is
for, and it has consequences you can see in the code:

- `TicketService` cannot return `NotFound()` — that is a `ControllerBase` method, and the
  business layer has no idea HTTP exists. It returns `ServiceResult` instead, and
  `ApiControllerBase` translates that into a status code.
- `ChatService` needs to push a message over a WebSocket, but SignalR lives up in the API
  project. So the business layer declares `IRealtimeNotifier` (what it needs) and the API
  supplies `SignalRNotifier` (how it is done). The arrow still points inwards.
- The DbContext needs to know who is making the request, without touching `HttpContext`.
  Same trick: `ICurrentUser` in Contracts, `CurrentUser` in the API project.

The payoff is testability and change-tolerance: you can unit-test a service with fake
repositories and no web server, and swapping SQL Server for PostgreSQL touches one file.

The cost is real too — more projects, more indirection, and a genuine temptation to add
ceremony where none is needed. Compare `TicketService` (workflow, notifications, security
filter, concurrency) with `DepartmentService` (four one-line methods). Not every service
needs to look like the first one.

---

## 3. What is in here

| Feature | Where to look |
|---|---|
| N-tier layering | the four `src/` projects |
| Entities, relationships, delete behaviour | `DataAccess/Entities`, `DataAccess/Configurations` |
| 1:1 two different ways | `AgentProfile` (shared key) vs `Rating` (unique FK) |
| N:N with and without a join class | `Agent`↔`Skill` vs `ConversationParticipant` |
| Automatic audit columns | `TicketHubDbContext.SaveChangesAsync` |
| Soft delete + global query filters | `AuditableEntity`, `ApplySoftDeleteQueryFilters` |
| Full audit log of every write | `AuditLog`, `CollectAuditEntries` |
| Optimistic concurrency | `Ticket.RowVersion`, `TicketService.UpdateAsync` |
| Repository + Unit of Work | `DataAccess/Repositories` |
| Filtering, sorting, paging in SQL | `TicketRepository.SearchAsync` |
| Projections, and avoiding N+1 | every `Projection` field in the repositories |
| Grouping and aggregates | `GetStatisticsAsync`, `ReportRepository` |
| ASP.NET Core Identity | `ApplicationUser`, `Program.cs` |
| JWT + refresh token rotation + reuse detection | `TokenService`, `AuthService` |
| Role, policy and per-row authorization | `AppRoles`, `TicketAccessFilter`, `CommentService` |
| Live chat over SignalR | `ChatHub`, `SignalRNotifier`, `ChatService` |
| File upload and download | `AttachmentService` |
| Global exception handling | `ExceptionHandlingMiddleware` |
| Swagger with auth | `Program.cs` |
| Seeded demo data | `DatabaseSeeder` |

### The data model

```
Department ──1:N── Category ──1:N── Ticket ──1:N── TicketComment
     │                                 │      ──1:N── TicketAttachment
     │                                 │      ──1:N── TicketHistory
     ├──1:N── Agent ──1:1── AgentProfile      ──1:1── Rating
     │           └──N:N── Skill               ──1:1── Conversation
     │           └──1:1── ApplicationUser              ├──1:N── ChatMessage
     └──1:N── Ticket (denormalised, for the security filter)
                                                       └──N:N── ConversationParticipant

ApplicationUser ──1:N── RefreshToken, Notification, TicketComment, Ticket
AuditLog — written by the DbContext, referenced by nothing
```

Two shapes are worth pausing on:

- **`Ticket.DepartmentId` duplicates `Category.DepartmentId` on purpose.** The department
  filter runs on every query in the application; going through `Category` would mean a join
  on every one of them and a chance to get it wrong every time. One denormalised column
  makes it `t.DepartmentId == currentUser.DepartmentId`. The cost is one line in
  `TicketService.UpdateAsync` to keep it in sync.
- **`TicketComment` and `ChatMessage` look almost identical and mean different things.**
  A comment is a durable record on the ticket that outlives the conversation; a chat
  message is live back-and-forth. Merging them would be a table nobody can reason about.

---

## 4. A reading order

1. **`Contracts/`** — DTOs first. They are the vocabulary everything else speaks.
2. **`DataAccess/Entities/Ticket.cs`** — the centre of the model, and every relationship
   shape in one file.
3. **`DataAccess/Configurations/TicketConfiguration.cs`** — indexes, delete behaviour,
   check constraints, and why each one is there.
4. **`DataAccess/TicketHubDbContext.cs`** — where auditing and soft delete actually happen.
5. **`DataAccess/Repositories/TicketRepository.cs`** — the most instructive file in the
   project. `SearchAsync` is filtering, paging, sorting and projection in one query.
6. **`BusinessLogic/Services/TicketService.cs`** — rules, orchestration, notifications.
7. **`Api/Controllers/TicketsController.cs`** — how thin a controller should be.
8. **`Api/Program.cs`** — registration and pipeline order.
9. **`Api/Hubs/ChatHub.cs`** + **`wwwroot/chat-demo.html`** — read them side by side.

Then open `src/TicketHub.Api/TicketHub.Api.http` and send the requests in order. Sections
14 and 15 are the interesting ones: the same endpoint returns different data depending on
who is asking, and nothing in the controller does that.

---

## 5. Things to try

Small experiments that teach more than reading does.

**Watch the SQL.** In `appsettings.Development.json`, set
`Microsoft.EntityFrameworkCore.Database.Command` to `Information` and restart. Now call
`GET /api/tickets` and read the SELECT next to the LINQ that produced it. Then look at
`GET /api/tickets/1` and count the queries.

**Break the projection.** In `TicketRepository.SearchAsync`, replace the `.Select(...)`
with `.Include(t => t.Category).Include(t => t.Comments)` and return entities. Compare the
SQL and the response size.

**Break the tie-breaker.** Remove `.ThenByDescending(t => t.Id)` from `ApplySort`, seed
tickets that share a `CreatedAt`, and page through them. Watch a ticket appear on two
pages while another never appears at all.

**Break the security filter.** In `TicketRepository.ApplyAccessFilter`, change the final
`query.Where(_ => false)` to `query`. Sign in as an agent with no department and see what
they can suddenly read. That one line is the difference between failing closed and failing
open.

**Trigger a concurrency conflict.** `GET /api/tickets/1`, keep the `rowVersion`. Update the
ticket once. Then `PUT` again with the *old* `rowVersion` — 409 instead of silently
overwriting the first change.

**Watch reuse detection fire.** Log in, refresh, then present the same refresh token a
second time. Every session for that user is revoked. See `AuthService.RefreshAsync`.

**Add a table end to end.** Add `SlaPolicy`: entity → configuration → migration →
repository → service → controller → `.http` request. That round trip is the whole course
in one exercise.

---

## 6. Commands

```bash
dotnet ef migrations add YourMigrationName --project src/TicketHub.DataAccess --startup-project src/TicketHub.Api
```

```bash
dotnet ef database update --project src/TicketHub.DataAccess --startup-project src/TicketHub.Api
```

```bash
dotnet ef migrations script --idempotent --project src/TicketHub.DataAccess --startup-project src/TicketHub.Api -o deploy/schema.sql
```

Read the generated `Up()` **before** you apply it. Every time.

To start over with a clean database:

```bash
docker compose down -v && docker compose up -d
```

---

## 7. What this project is not

Being explicit, so nobody copies the wrong thing into production:

- **Migrations are applied at start-up.** Convenient here, wrong in production — two
  instances starting together can deadlock, and the app needs schema rights at runtime.
  Generate an idempotent script and run it from the pipeline instead.
- **The JWT signing key is in `appsettings.Development.json`.** Only acceptable because
  this database holds nothing and lives on your laptop. Use user-secrets locally and a key
  vault in production.
- **The container uses the `sa` account.** A real application gets a least-privileged login
  that cannot alter the schema.
- **SignalR has no backplane.** With more than one server, a message sent from server A
  never reaches a client connected to server B. Add Redis before you scale out.
- **There are no automated tests.** The layering is built so they would be easy to write —
  which is the point — but writing them is left as the exercise it should be.
- **`/api/auth/register` reveals whether an email is already in use.** A deliberate
  usability trade, explained in `AuthService.RegisterAsync`. It would be the wrong trade
  for a bank.
