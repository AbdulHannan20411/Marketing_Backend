# Marketing Backend — Multi-Tenant WhatsApp Marketing SaaS API

Production ASP.NET Core API for the Meta WhatsApp Business Platform (Cloud API).
Enterprise quality bar: no tutorial code, no placeholder implementations, no shortcuts.

## Stack

.NET 10 · ASP.NET Core 10 · EF Core 10 · PostgreSQL 17 · Dapper · Redis · Quartz.NET ·
Serilog · Refit · Polly · JWT · AutoMapper · FluentValidation · Swagger/OpenAPI.

Nullable reference types, implicit usings, file-scoped namespaces, ReSharper conventions.
`TargetFramework` lives once in `Directory.Build.props`; every package version lives once in
`Directory.Packages.props`.

## Layers and reference direction

```
Marketing.API            → Infrastructure, Application, Business, DataAccess, Shared, Common
Marketing.Infrastructure → Application, Shared, Common
Marketing.Application    → Business, Shared, Common          (services, DTOs, validators, mappings)
Marketing.Business       → DataAccess, Shared, Common        (repositories, unit of work, Dapper)
Marketing.DataAccess     → Shared, Common                    (entities, DbContext, interceptors)
Marketing.Shared         → Common                            (ambient abstractions)
Marketing.Common         → nothing                           (enums, constants, exceptions, responses)
```

A layer references only lower layers. Adding a reference that violates this is a design change,
not a convenience — raise it rather than adding it.

- Controllers contain no business logic. Bind, delegate, shape the response.
- Repositories contain no business rules. They talk to the database and nothing else.
- Services own rules, transactions, integrations, caching and orchestration.
- Endpoints return DTOs. An entity must never cross the API boundary.

## Multi-tenancy — the rule everything else defers to

`TenantId` comes from the validated `tenant_id` JWT claim and from nowhere else. It is never read
from a route, query string, header or request body, and never returned to a tenant-scoped client.

- `ITenantContext` is the only supported source. `RequireTenantId()` throws rather than returning
  null, so a missing tenant fails loudly instead of silently widening a query.
- Entities implementing `ITenantScoped` get the tenant query filter automatically. Entities
  implementing `IRequiresTenant` additionally refuse to persist without a tenant.
- `ITenantScoped` allows a null tenant, for platform-level rows (platform admin users, their role
  assignments and sessions). `IRequiresTenant` does not — use it for every domain entity.
- The auditing interceptor refuses to let an update change an entity's `TenantId`.
- `IgnoreQueryFilters()` appears in exactly three files and nowhere else — `UserRepository`,
  `RefreshTokenRepository` and `DatabaseSeeder`. All three run either before authentication has
  established a tenant (sign-in, refresh, startup seeding) or key off a value the caller already
  holds (a user id from their own token, a 256-bit token hash). **Every one of them re-applies
  `!IsDeleted` by hand**, because bypassing the filter bypasses the soft-delete predicate too.
  A new call site outside these files needs justification in review; `grep -rn IgnoreQueryFilters src`
  is the audit.
- Raw SQL bypasses query filters, so `ISqlQueryExecutor` injects `@TenantId` itself and **rejects
  any statement that does not reference it**, plus anything that is not a `SELECT`/`WITH`.
  Cross-tenant reporting goes through `QueryPlatformAsync`, which requires platform admin.

## Persistence

- Code-first, **Fluent API only**. Data annotations are not used for the EF model (they are used
  for options validation, which is a different thing).
- Every entity derives from `BaseEntity`: `Id`, `TenantId`, `CreatedBy/On`, `ModifiedBy/On`,
  `DeletedBy/On`, `IsDeleted`, `RowVersion`.
- **Never assign audit columns by hand.** `AuditingSaveChangesInterceptor` owns them.
- Primary keys are version 7 GUIDs (`SequentialGuid.Create()`) so inserts append to the right edge
  of the index instead of fragmenting it.
- `RowVersion` is a `uint` mapped onto PostgreSQL's `xmin` system column — free optimistic
  concurrency, no extra column. `DbUpdateConcurrencyException` is translated to
  `ConcurrencyConflictException` in the context, so upper layers never reference EF to handle it.
- Deletes are soft. The interceptor rewrites `Deleted` to an `IsDeleted` update touching only the
  delete columns.
- Unique indexes are partial (`WHERE is_deleted = false`) so a deleted row frees its slug/email.
- Snake_case naming, `timestamptz` for every instant, `jsonb` for change payloads.
- Every audited change writes an `AuditLog` row **in the same transaction** as the change.
  Password hashes, security stamps and token hashes are redacted from it.

### CRUD versus reporting

| Work | Tool |
| --- | --- |
| CRUD, single-aggregate reads, writes | EF Core |
| Dashboards, analytics, aggregations, CTEs, window functions, exports | Dapper via `ISqlQueryExecutor` |

EF reads are `AsNoTracking` by default in the repository; write paths call `GetForUpdateAsync`.
Project in the database — `GetPagedAsync` takes a projection so entities never materialise.

## Security

- Roles: `PlatformAdmin`, `TenantOwner`, `TenantUser`. Policies in `PolicyNames`; controllers
  reference the policy, never a raw role string.
- Passwords: PBKDF2-HMAC-SHA256, iteration count stored per hash, transparent upgrade on sign-in,
  constant-time comparison. Argon2id is the intended successor — the versioned hash prefix exists
  so that migration does not force a password reset.
- Refresh tokens: 512-bit CSPRNG values, only the SHA-256 hash is stored, single use with rotation.
  Replay of a rotated token revokes **every** session for that user.
- `SecurityStamp` rotates on credential and role changes, which is what makes revocation take
  effect immediately rather than at access-token expiry.
- Sign-in failures are uniform. Wrong password, unknown address, disabled account and suspended
  tenant all return the same message — anything else enumerates users.
- An unknown address still pays for a hash verification, to close the timing oracle.
- Never log secrets. `SensitiveDataRedactionEnricher` is a backstop, not permission to be careless.
- Errors are RFC 7807 problem documents with a stable `errorCode`, a `correlationId`, and an
  `exceptionId` for server faults. Stack traces appear in Development only.

## Resilience and caching

- Redis is **fail-open**. A cache outage produces misses, never exceptions; reads fall through to
  PostgreSQL. After a failure the service pauses cache access for a cool-down rather than paying
  the connect timeout on every request. The Redis health check reports `Degraded`, not `Unhealthy`,
  and is not a readiness dependency.
- Cache keys are built exclusively through `CacheKeys` and are always tenant-prefixed.
- Polly: total timeout → bulkhead → circuit breaker → retry → per-attempt timeout, outermost first.
  Retries use exponential backoff with jitter. **Only transient failures are retried** — a 4xx from
  Meta means the request is wrong and retrying wastes the tenant's rate-limit budget.
- Rate limiting is partitioned by tenant (or user, or IP for anonymous endpoints). A single global
  bucket would let one customer throttle another.

## Meta Cloud API

- Never call Graph from a controller. Use the typed `IWhatsAppCloudApi` Refit client through a
  WhatsApp service.
- The Graph API version is pinned in configuration, not floated.
- `GraphApiErrorHandler` converts error bodies to `ExternalServiceException` with an accurate
  `IsTransient`, and never returns Meta's message to the client — it echoes request parameters,
  which here means recipient phone numbers.

## Configuration and secrets

Secrets are never committed. `Database:ConnectionString`, `Authentication:Jwt:SigningKey`,
`WhatsApp:AppSecret` and the `Bootstrap:*` credentials come from user secrets locally and the
platform secret store elsewhere; `appsettings.json` ships them empty.

`appsettings.Development.json` contains deliberately worthless values pointing at the local
docker-compose stack, so a fresh clone runs. They protect nothing and must not be reused.

Options classes validate with data annotations and `ValidateOnStart()`, so a misconfigured host
fails at startup instead of on the first request that needs the value.

## Running it

```bash
docker compose up -d
```

```bash
dotnet run --project src/Marketing.API
```

Swagger is at `/swagger` in Development. Migrations and seeding run on startup in Development only
(`Database:ApplyMigrationsOnStartup`, `Database:SeedOnStartup`) — in production they belong in a
deployment step that runs once, not in every replica's startup.

```bash
dotnet ef migrations add <Name> --project src/Marketing.DataAccess --startup-project src/Marketing.API
```

```bash
dotnet test
```

Integration tests need a running Docker daemon; they spin up throwaway PostgreSQL and Redis
containers via Testcontainers.

## Feature delivery contract

Every feature request is answered in **this exact order**, no section skipped. Sections that do not
apply are listed and marked "not applicable", never silently dropped.

1. Folder structure
2. Database entities
3. Entity configurations
4. DTOs
5. Validators
6. Repository interfaces
7. Repository implementations
8. Business service interfaces
9. Business service implementations
10. AutoMapper profiles
11. Refit client
12. Polly configuration
13. Redis integration
14. Quartz job (if applicable)
15. Controller
16. Dependency injection registration
17. Swagger documentation
18. Unit tests
19. Integration tests
20. Performance considerations
21. Security considerations
22. Future enhancements

## Coding standards

SOLID · DRY · KISS · YAGNI · async all the way with `CancellationToken` on every I/O path ·
readonly fields · constructor injection · XML documentation on public members · meaningful names.

Comments explain *why*, not *what*. A comment restating the code is noise; a comment recording a
trade-off, a non-obvious constraint or a rejected alternative is the reason the next person does
not undo the decision.

## Known gaps in the foundation

- No migration has been generated yet — run `dotnet ef migrations add InitialSchema` once a
  .NET 10 SDK and PostgreSQL are available.
- The Refit client has no per-tenant authorization handler. Access tokens are per-tenant and
  encrypted at rest, so the handler lands with the WhatsApp connection module rather than being
  faked now.
- Quartz uses the in-memory store, which is correct for a single instance. Multi-instance
  deployment needs the AdoJobStore with clustering enabled, or two replicas will both fire every
  trigger.
