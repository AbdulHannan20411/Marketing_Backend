# Frontend API Integration Guide

**Source of truth: the implemented backend in this repository.** Every endpoint, DTO, enum value and
permission below was extracted from the code, not from a design document. Where a screen needs
something the backend does not have, it is listed in §48 *Missing APIs* — not invented here.

Generated against commit-state of `Marketing_backend` on 2026-08-08.

---

## 1. Overview

Multi-tenant SaaS marketing platform on the Meta WhatsApp Business Platform (Cloud API).

Implemented modules: Authentication, Contacts, Groups, Tags, WhatsApp connection, Templates,
Campaigns (including dispatch and delivery receipts), Dashboard, Reports, Employees, Permission
sets, Notifications, Search, Subscription, Billing, Platform administration, Audit log.

**Email marketing and social automation are not implemented.** The permission catalogue and the plan
module map both contain `email` and `social` entries, and `GET /plans` will report those modules, but
no endpoint exists behind them. See §48.

---

## 2. Technology Stack

Backend: .NET 10, ASP.NET Core, EF Core 10 + Npgsql, Dapper (reporting), PostgreSQL 17, Redis
(cache), Quartz.NET (scheduling), Refit + Polly (Meta Cloud API), SignalR (real time), Serilog,
JWT bearer authentication.

Frontend target: Angular 19 standalone components, signals, Reactive Forms, RxJS, Tailwind CSS 4.

---

## 3. Environment Configuration

Local development (`launchSettings.json`):

| Scheme | URL |
| --- | --- |
| HTTPS | `https://localhost:7108` |
| HTTP | `http://localhost:5108` |

Swagger UI is served at `/swagger` in the **Development** environment only.

Angular environment keys to define:

```ts
export const environment = {
  apiBaseUrl: 'https://localhost:7108/api/v1',
  realtimeUrl: 'https://localhost:7108/hubs/realtime',
};
```

CORS is configured from the backend's `Cors:AllowedOrigins`. Development allows
`http://localhost:4200` and `https://localhost:4200`. A request from any other origin is rejected by
the browser, not by a 4xx you can read.

---

## 4. API Base URL

```
{host}/api/v1
```

Versioning is by URL segment (`Asp.Versioning`), current version `1.0`. The route template is
`api/v{version:apiVersion}/...`.

Non-versioned endpoints:

| Route | Purpose |
| --- | --- |
| `GET /health/live` | Liveness. No dependency checks. Anonymous. |
| `GET /health/ready` | Readiness. Checks PostgreSQL. Anonymous. |
| `GET /health` | Full dependency detail. Requires platform administrator. |
| `/hubs/realtime` | SignalR hub. |

---

## 5. Authentication

### 5.1 Sign in

```http
POST /api/v1/auth/login
Content-Type: application/json
```

Anonymous. Rate limited (§38).

```ts
interface LoginRequest {
  email: string;
  password: string;
  rememberMe?: boolean;   // default false
  portal?: string | null; // see below
}
```

`portal` gates which sign-in page an account may use. The Super Admin has a separate portal address;
passing the wrong portal value is refused exactly like a wrong password, so the response cannot be
used to discover which accounts are platform staff.

**Response** — `ApiResponse<AuthTokens>`:

```ts
interface AuthTokens {
  accessToken: string;
  refreshToken: string;
  expiresAtUtc: string;   // ISO 8601, UTC
}
```

Failure is `401` with a deliberately identical problem document for an unknown address, a wrong
password and a wrong portal. Do not try to distinguish them in the UI.

Repeated failures lock the account. The lock is time-based and configured by
`Authentication:Policy:MaxFailedLoginAttempts` / `LockoutDurationMinutes`.

### 5.2 Current user

```http
GET /api/v1/auth/me
Authorization: Bearer {accessToken}
```

```ts
interface CurrentUserResponse {
  id: number;                 // numeric key, see §9
  email: string;
  displayName: string;
  tenantName: string | null;  // null for platform staff
  isSuperAdmin: boolean;
  roles: string[];
  permissions: string[];      // the effective set — drive all UI gating from this
}
```

**There is no `tenantId` in this response, by design.** See §9.

### 5.3 Other auth endpoints

| Method | Route | Auth | Body | Returns |
| --- | --- | --- | --- | --- |
| `POST` | `/auth/refresh` | Anonymous | `{ refreshToken }` | `AuthTokens` |
| `POST` | `/auth/logout` | Bearer | — | `null` |
| `POST` | `/auth/logout-everywhere` | Bearer | — | `null` |
| `POST` | `/auth/forgot-password` | Anonymous | `{ email }` | `null` |
| `POST` | `/auth/accept-invitation` | Anonymous | `{ token, password }` | `AuthTokens` |
| `POST` | `/auth/reset-password` | Anonymous | `{ token, password }` | `AuthTokens` |
| `POST` | `/auth/change-password` | Bearer | `{ currentPassword, newPassword }` | `null` |

`forgot-password` always reports success, whether or not the address exists. Do not branch on it.

`accept-invitation` is how an invited employee sets their password and gets their first token pair.
It is **not** under `/employees`.

---

## 6. JWT and Refresh Token

### 6.1 Attaching the token

```http
Authorization: Bearer {accessToken}
```

### 6.2 Token contents

The access token carries these claims. The client reads them by base64url-decoding the payload; do
not call an endpoint to discover them.

| Claim | Meaning |
| --- | --- |
| `sub` | User key, numeric as a string |
| `name` | Display name |
| `role` | **A single role string**, not an array |
| `permissions` | **One claim containing a JSON array** of permission strings |
| `workspaceName` | Tenant display name |
| `avatarUrl` | Avatar, when set |
| `sid` | Session id (GUID) |
| `exp`, `iss`, `aud` | Standard |

`tenant_id` and `tenant_slug` exist as claims but are **backend-only**. See §9.

### 6.3 Expiry and refresh

Access token lifetime is `Authentication:Jwt:AccessTokenLifetimeMinutes` (15 in production, 60 in
development). Refresh token lifetime is `RefreshTokenLifetimeDays` (14).

Refresh rotates: the presented refresh token is consumed and a new pair issued.

**Replay detection.** Presenting an already-rotated refresh token revokes *every* session for that
user. This is deliberate — a replayed token means the token was stolen. The client must therefore
**serialise refresh calls**: if two requests 401 at once and both call refresh, the second is a replay
and will log the user out entirely.

### 6.4 Angular interceptor strategy

```ts
// Single-flight refresh. This is not optional — see 6.3.
private refreshInFlight$: Observable<AuthTokens> | null = null;

intercept(req, next) {
  const token = this.auth.accessToken();
  const authed = token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;

  return next.handle(authed).pipe(
    catchError(err => {
      if (err.status !== 401 || req.url.includes('/auth/')) return throwError(() => err);

      this.refreshInFlight$ ??= this.auth.refresh().pipe(
        finalize(() => (this.refreshInFlight$ = null)),
        shareReplay(1),
      );

      return this.refreshInFlight$.pipe(
        switchMap(t => next.handle(req.clone({
          setHeaders: { Authorization: `Bearer ${t.accessToken}` },
        }))),
        catchError(refreshErr => { this.auth.signOut(); return throwError(() => refreshErr); }),
      );
    }),
  );
}
```

A response carrying the `X-Token-Expired` header indicates expiry specifically, as opposed to a token
that was never valid.

### 6.5 Immediate revocation

Certain server-side changes invalidate outstanding tokens straight away by rotating the user's
security stamp:

- changing an employee's permissions
- changing an employee's role
- suspending an employee
- resetting or changing a password
- `logout-everywhere`

The next request from that user returns `401` even though the token has not expired. **Treat a 401
after a successful refresh as a sign-out, not a retry loop.** Permission changes are therefore
effective immediately; you do not need to tell users "changes apply within N minutes".

---

## 7. Roles

Exactly three, defined in `Roles`:

```
SuperAdmin   platform staff, no tenant, sees every tenant
Admin        administers one tenant
Employee     works within one tenant
```

Role comes from the `role` claim as a **single string**.

---

## 8. Permissions

Dot-notation, matched **exactly** — there are no wildcards. The complete implemented catalogue:

```
dashboard.view                  dashboard.statistics            dashboard.export

contacts.view                   contacts.create                 contacts.edit
contacts.delete                 contacts.import                 contacts.export
groups.manage                   tags.manage

whatsapp.connect                whatsapp.disconnect
whatsapp.templates.view         whatsapp.templates.sync
whatsapp.campaigns.create       whatsapp.campaigns.edit         whatsapp.campaigns.delete
whatsapp.campaigns.schedule     whatsapp.campaigns.send         whatsapp.campaigns.pause
whatsapp.campaigns.cancel       whatsapp.campaigns.reports

email.connect                   email.templates.manage          email.campaigns.create
email.campaigns.send            email.analytics.view

social.accounts.connect         social.posts.create             social.posts.schedule
social.posts.publish            social.posts.delete             social.analytics.view

reports.view                    reports.export                  reports.download.csv
reports.download.excel          reports.download.pdf

settings.company                settings.employees              settings.billing
settings.subscription           settings.integrations           settings.apikeys

platform.tenants                platform.audit                  platform.monitoring
platform.plans
```

`email.*` and `social.*` are catalogued but **no endpoint consumes them** (§48).

### Three distinct authorisation layers

| Layer | Question | Failure |
| --- | --- | --- |
| **Role** | Is this person platform staff or a tenant admin? | `403` |
| **Permission** | May *this user* do this? | `403` |
| **Plan module** | Has this *organisation* bought this feature? | `403` |

All three can apply to one endpoint. A tenant Admin holding every permission still cannot reach
`/contacts` if their plan lacks the `crm` module.

Endpoints marked with two permissions (e.g. `whatsapp.templates.view, whatsapp.connect`) accept
**either** — holding any one is sufficient.

### Angular gating

```ts
canCreateCampaign  = computed(() => this.perms().includes('whatsapp.campaigns.create'));
canSendCampaign    = computed(() => this.perms().includes('whatsapp.campaigns.send'));
canManageEmployees = computed(() => this.perms().includes('settings.employees'));
canConnectWhatsApp = computed(() => this.perms().includes('whatsapp.connect'));
canManageSubscription = computed(() => this.perms().includes('settings.subscription'));
```

Never hardcode a role-to-permission mapping in the client. Read `permissions` from the token or
`/auth/me`.

---

## 9. Multi-Tenancy

**This is the most important rule in this document.**

The tenant is resolved **solely** from the `tenant_id` claim on the validated JWT, through a single
backend abstraction. Nothing reads a tenant from a route, query string, header or request body.

### Rules for the Angular client

> **Never send a tenant id.** Not in a body, not in a query parameter, not in a route, not in a
> header. No endpoint accepts one. Do not store one in client state — it is not in `/auth/me` and it
> is not readable from any response.

Every list, count and write is filtered to the caller's tenant by a global query filter at the
database layer. A crafted request cannot widen it.

### The one exception: `?adminId=`

A `SuperAdmin` may view the platform *as* a particular Admin by appending `adminId` to any scopable
endpoint:

```
GET /api/v1/contacts?adminId=adm_42
```

Behaviour, verbatim from the implementation:

- Caller is `SuperAdmin` → the request is scoped to that Admin's tenant and returns **that tenant's
  real data**, including its real plan limits and usage.
- Caller is **not** `SuperAdmin` → the parameter is **silently ignored**, and the caller's own tenant
  is used. It is not an error. A forged `adminId` is inert.
- Unknown or unauthorised `adminId` → `403`.

Scope entry is written to the audit log.

`adminId` is an opaque `adm_`-prefixed identifier from `GET /superadmin/admins`. It is **not** a
tenant id.

---

## 10. Standard Response Format

### Success

Every successful response is wrapped:

```json
{
  "data": { },
  "message": "Contact saved.",
  "traceId": "0HN7ABC..."
}
```

| Field | Notes |
| --- | --- |
| `data` | The payload. `null` for endpoints that return nothing. |
| `message` | Present on writes, `null` on reads. **Show it as a success toast when non-null.** |
| `traceId` | Correlation id, also in the `X-Correlation-Id` header and in the server logs. |

There is **no `success` boolean.** Branch on HTTP status and content type, not on a flag.

Unwrap `data` centrally in `ApiService`; components should never see the envelope.

### Paged payloads

A paged endpoint puts the whole page object in `data`:

```json
{
  "data": {
    "items": [],
    "page": 1,
    "pageSize": 12,
    "totalItems": 148,
    "totalPages": 13
  },
  "message": null,
  "traceId": "..."
}
```

`totalPages` is never below 1, even for an empty result. `X-Total-Count` carries `totalItems` as a
header as well.

### Failure

Failures are **not** wrapped. They are RFC 7807 problem documents with
`Content-Type: application/problem+json`:

```json
{
  "type": "https://docs.marketing-platform.io/errors/contact_exists",
  "title": "Contact exists",
  "status": 409,
  "detail": "A contact with that phone number already exists.",
  "instance": "/api/v1/contacts",
  "errorCode": "contact_exists",
  "traceId": "0HN7ABC...",
  "exceptionId": "a1b2c3..."
}
```

A validation failure additionally carries `errors`:

```json
{
  "status": 422,
  "title": "Validation failed",
  "errorCode": "validation_failed",
  "errors": {
    "phoneNumber": ["Enter a valid phone number."],
    "country": ["The country could not be worked out from that number. Select one."]
  }
}
```

`exception` (a stack trace) appears in **Development only**.

---

## 11. Error Handling

Mapping is centralised in `GlobalExceptionHandler`. This table is the actual behaviour:

| Status | Backend cause | `errorCode` examples | Frontend action |
| --- | --- | --- | --- |
| `400` | Malformed JSON, bad route binding | — | Developer error; show generic failure |
| `401` | `AuthenticationException`, missing/expired token | `not_authenticated` | Refresh once, then sign out |
| `403` | `ForbiddenException`, `TenantResolutionException`, permission filter, module gate | `forbidden` | Show a permission or upgrade message. **Do not retry.** |
| `404` | `NotFoundException` | — | Empty / not-found state |
| `409` | `BusinessRuleException`, `ConcurrencyConflictException`, unique-key violation | `contact_exists`, `contact_limit_reached`, `downgrade_blocked`, `last_admin`, `duplicate_record` | Show `detail` verbatim |
| `422` | `ValidationException` | `validation_failed` | Bind `errors` to form controls |
| `429` | Rate limiter | `rate_limited` | Read `Retry-After`, back off |
| `499` | Client cancelled | `request_cancelled` | Ignore |
| `500` | Anything unhandled | `internal_error` | Generic message + `exceptionId` |
| `501` | Not implemented | — | Invoice PDF only (§32) |
| `502` | `ExternalServiceException` (non-transient) | `external_service_error` | Show a Meta-side failure message |
| `503` | `ExternalServiceException` (transient) | `external_service_unavailable` | Offer retry |

### ⚠ Deviation you must handle

**Business rule failures return `409`, not `422`.** The UI specs describe plan-limit and seat-limit
responses as `422`; the implementation returns `409` for all of them, because they are all
`BusinessRuleException`. Affected cases include:

- `contact_limit_reached` — plan contact ceiling
- `downgrade_blocked` — target plan below current usage
- `last_admin`, `cannot_target_self` — employee guards
- `contact_exists`, `group_name_taken`, `tag_name_taken`
- `permission_not_in_plan`

Handle the **`errorCode`**, not the status. Treat `409` and `422` the same way for these flows: show
`detail` as the message. This is flagged as a backend inconsistency in §49.

Always show `detail` — it is written for end users. Never show `type` or `exceptionId` except as a
support reference.

---

## 12. Validation

Backend rules you should mirror in Reactive Forms. These are the **implemented** rules.

### Contact (`CreateContactRequest` / `UpdateContactRequest`)

| Field | Rule |
| --- | --- |
| `fullName` | Required, trimmed, 1–120 characters, must contain a non-whitespace character |
| `phoneNumber` | Required, must be parseable; normalised to digits server-side; **unique per tenant** |
| `email` | Optional; must be a valid address; max 254; **not unique** |
| `country` | Optional. Omitted → inferred from the number's dialling prefix. If it cannot be inferred, `422` on `country` |
| `status` | Enum, default `subscribed` |
| `tagIds` / `groupIds` | Must all exist, else `422`. On update, supplying the array **replaces** the whole set |

Consent rules: `subscribed` always carries an `optedInAt`; `unsubscribed` and `blocked` clear it to
null. **`blocked → subscribed` is refused** — it needs a fresh opt-in.

### Group / Tag

| Field | Rule |
| --- | --- |
| Group `name` | Required, 1–60, unique per tenant (case-insensitive) |
| Group `description` | Optional, max 200, never returned null (empty string) |
| Tag `name` | Required, 1–40, unique per tenant (case-insensitive) |
| Tag `color` | Enum only. Hex colours are rejected |

### WhatsApp connect

| Field | Rule |
| --- | --- |
| `code` | Required, max 1024 |
| `wabaId`, `phoneNumberId` | Required, **digits only**, max 32 |

### Password

Minimum length from `Authentication:Policy:MinimumPasswordLength` (12). Must contain a letter and a
digit, and is checked against a deny-list of common fragments. **All failures are reported at once**,
so render the whole `errors.password` array.

### Bulk operations

`ids` capped at **1000** per call; beyond that `422`.

---

## 13. Pagination

Request parameters, bound from the query string:

| Parameter | Default | Notes |
| --- | --- | --- |
| `page` | `1` | One-based. Values below 1 coerce to 1 |
| `pageSize` | `25`, **`12` for `/contacts`** | Clamped to `[1, 100]` |
| `search` | — | Free text |
| `sortBy` | endpoint default | Matched against an allow-list; unknown values fall back |
| `sortDirection` | `Ascending` | `Ascending` \| `Descending` |

`pageNumber` is accepted as an alias for `page`.

Response shape is in §10.

**Paged endpoints:** `GET /contacts`, `GET /contacts/duplicates`, `GET /groups/{id}/contacts`,
`GET /tags/{id}/contacts`, `GET /admin/tenants`, `GET /admin/audit`, `GET /reports/failures`.

**Deliberately unpaged** (card grids and pickers): `GET /groups`, `GET /tags`, `GET /employees`,
`GET /permission-sets`, `GET /plans`, `GET /templates`, `GET /campaigns`, `GET /notifications`,
`GET /billing/history`.

---

## 14. Search / Filter / Sort

### `GET /contacts`

| Parameter | Values | Notes |
| --- | --- | --- |
| `search` | text | Case-insensitive substring over name, phone, email |
| `status` | `all` \| `subscribed` \| `unsubscribed` \| `blocked` | |
| `groupId` | `all` \| `grp_…` | |
| `tagId` | `all` \| `tag_…` | |

> **Send the literal string `all` when a filter is cleared** — not an empty string, not an omitted
> parameter. The backend treats `all` as "no filter". An unparseable group or tag id returns **zero
> rows** rather than ignoring the filter.

Sortable columns: `fullName`, `status`, `country`, `createdAt`, `lastMessagedAt`.

Example:

```
GET /api/v1/contacts?page=2&pageSize=12&search=weber&status=subscribed&groupId=all&tagId=all
```

### `GET /search` (global)

```
GET /api/v1/search?q=weber
```

Returns `SearchResultGroup[]`, grouped by `SearchResultKind`.

---

## 15. File Upload / Download

### CSV import (upload)

```http
POST /api/v1/contacts/import/preview
Content-Type: multipart/form-data
```

| Property | Value |
| --- | --- |
| Form field name | `file` |
| Extension | `.csv` **only** — checked by extension, not content type |
| Maximum size | 10 MB |
| Maximum rows | 50 000 |
| Encoding | UTF-8, BOM detected and stripped |

`422` for empty, wrong extension, oversized, or a file with no data rows.

### CSV export (download)

```http
GET /api/v1/contacts/export
```

Returns a **direct authenticated stream**: `Content-Type: text/csv; charset=utf-8` with
`Content-Disposition: attachment`.

> A browser will not attach the bearer token to a plain `<a href>`. **Fetch as a blob** and trigger
> the download client-side:

```ts
this.http.get(url, { responseType: 'blob', observe: 'response' })
  .subscribe(res => saveAs(res.body!, 'contacts.csv'));
```

Accepts every `/contacts` filter, plus `ids` for an explicit selection (which overrides the filters).

Columns: `fullName, phoneNumber, email, country, status, tags, groups, optedInAt, lastMessagedAt,
createdAt`. Tags and groups are `;`-delimited name lists.

### Invoice PDF

```http
GET /api/v1/billing/invoices/{id}/pdf
```

**Returns `501 Not Implemented`.** The route exists; the renderer does not. See §49.

---

## 16. Contacts API

All under `/api/v1/contacts`. **Every endpoint requires the `crm` plan module** in addition to its
permission.

| Method | Route | Permission | Returns |
| --- | --- | --- | --- |
| `GET` | `/contacts` | `contacts.view` | `PagedResult<Contact>` |
| `GET` | `/contacts/duplicates` | `contacts.view` | `PagedResult<DuplicateGroup>` |
| `GET` | `/contacts/{id}` | `contacts.view` | `Contact` |
| `POST` | `/contacts` | `contacts.create` | `Contact` |
| `PUT` | `/contacts/{id}` | `contacts.edit` | `Contact` |
| `DELETE` | `/contacts/{id}` | `contacts.delete` | `null` |
| `POST` | `/contacts/bulk-delete` | `contacts.delete` | `BulkOperationResult` |
| `POST` | `/contacts/bulk-tag` | `contacts.edit` | `BulkOperationResult` |
| `POST` | `/contacts/bulk-group` | `contacts.edit` | `BulkOperationResult` |
| `POST` | `/contacts/merge` | `contacts.edit` **and** `contacts.delete` | `Contact` |
| `POST` | `/contacts/import/preview` | `contacts.import` | `ImportPreview` |
| `POST` | `/contacts/import/commit` | `contacts.import` | `ImportResult` |
| `GET` | `/contacts/import/{jobId}` | `contacts.import` | `ImportResult` |
| `GET` | `/contacts/export` | `contacts.export` | CSV stream |

### Notes that will bite you otherwise

- **`GET /contacts/{id}` returns `404` both when the contact does not exist and when it belongs to
  another tenant.** This is deliberate; do not try to distinguish.
- `PUT` is **patch semantics** — omitted fields are left alone. But supplying `tagIds` or `groupIds`
  **replaces** the entire set. Use the bulk endpoints for additive changes.
- `country` is returned as an **ISO alpha-2 code** (`GB`), not a display name. See §49.
- Delete is a **soft delete**: the row survives for campaign history but disappears from every list,
  count and export.

### Bulk operations

```ts
interface BulkTagRequest  { ids: string[]; tagIds: string[];   mode?: BulkMode; }
interface BulkGroupRequest{ ids: string[]; groupIds: string[]; mode?: BulkMode; }
type BulkMode = 'add' | 'remove' | 'replace';   // default 'add'

interface BulkOperationResult {
  requested: number;
  succeeded: number;
  failed: { id: string; reason: string }[];
}
```

Returns `200` even with partial failure — that is the normal case for a selection built across
pages. Show `message`, and surface `failed` if non-empty.

### CSV import — three steps

**Step 1 — preview**

```http
POST /api/v1/contacts/import/preview   (multipart/form-data, field "file")
```

```ts
interface ImportPreview {
  uploadId: string;
  fileName: string;
  totalRows: number;
  detectedColumns: string[];
  suggestedMapping: ImportColumnMapping;
  sampleRows: Record<string, string>[];  // first 10, keyed by column name
  duplicatesInFile: number;
  duplicatesExisting: number;
  invalidRows: { rowNumber: number; reason: string }[];  // capped at 50
}

interface ImportColumnMapping {
  fullName?: string; phoneNumber?: string; email?: string;
  country?: string; status?: string; tags?: string; groups?: string;
}
```

**Step 2 — commit**

```http
POST /api/v1/contacts/import/commit
```

```ts
interface ImportCommitRequest {
  uploadId: string;
  mapping: ImportColumnMapping;              // phoneNumber is required
  duplicateStrategy?: 'skip' | 'update' | 'create';   // default 'skip'
  defaultStatus?: ContactStatus;             // default 'subscribed'
  assignTagIds?: string[];
  assignGroupIds?: string[];
}
```

Runs **synchronously** and returns the finished `ImportResult` with `status: 'completed'`. It does
not return `202`. Bounded at 50 000 rows, so a large file will hold the request open — show a
progress indicator, not a spinner with a short timeout.

`tags` and `groups` columns accept a **`;`-delimited** list in one cell; unknown names are created.

The plan's contact ceiling **stops** the import rather than failing it: rows beyond the limit are
counted in `skipped` with a reason.

**Step 3 — re-read**

```http
GET /api/v1/contacts/import/{jobId}
```

```ts
interface ImportResult {
  jobId: string;
  status: 'queued' | 'processing' | 'completed' | 'failed';
  processedRows: number; totalRows: number;
  created: number; updated: number; skipped: number; failed: number;
  errors: { rowNumber: number; reason: string }[];   // capped at 100
  completedAt: string | null;
}
```

### Duplicates and merge

```
GET /api/v1/contacts/duplicates?strategy=phone&page=1&pageSize=25
```

`strategy` is `phone` (default) | `email` | `name`.

```http
POST /api/v1/contacts/merge
```

```ts
interface MergeContactsRequest {
  keepId: string;
  mergeIds: string[];
  fieldOverrides?: Record<string, string | null>;
  // supported keys: fullName, email, country, phoneNumber
}
```

The survivor takes the union of tags and groups and the latest `lastMessagedAt`; merged records are
deleted.

---

## 17. Groups API

Requires the `crm` module. Permission `groups.manage` throughout.

| Method | Route | Returns |
| --- | --- | --- |
| `GET` | `/groups` | `ContactGroup[]` — unpaged |
| `POST` | `/groups` | `ContactGroup` |
| `PUT` | `/groups/{id}` | `ContactGroup` |
| `DELETE` | `/groups/{id}` | `null` |
| `GET` | `/groups/{id}/contacts` | `PagedResult<Contact>` |
| `POST` | `/groups/{id}/contacts` | `BulkOperationResult` |
| `DELETE` | `/groups/{id}/contacts` | `BulkOperationResult` |

Body for membership add/remove: `{ "contactIds": ["cnt_1"] }`.

`contactCount` on a group **equals** `totalItems` from `GET /contacts?groupId={id}` — both are
computed from the same non-deleted membership rows.

Deleting a group **never deletes contacts**. It returns `409` if a `Scheduled` or `Sending` campaign
uses that group as its audience, naming the campaign in `detail`.

---

## 18. Tags API

Requires the `crm` module. Permission `tags.manage` throughout. Same shape as groups.

| Method | Route | Returns |
| --- | --- | --- |
| `GET` | `/tags` | `ContactTag[]` — unpaged |
| `POST` | `/tags` | `ContactTag` |
| `PUT` | `/tags/{id}` | `ContactTag` |
| `DELETE` | `/tags/{id}` | `null` |
| `GET` | `/tags/{id}/contacts` | `PagedResult<Contact>` |
| `POST` | `/tags/{id}/contacts` | `BulkOperationResult` |
| `DELETE` | `/tags/{id}/contacts` | `BulkOperationResult` |

Deleting a tag removes it from every contact and deletes no contacts. The confirmation `message`
reports how many were affected.

`color` accepts only the five `TagColor` values. Anything else is rejected.

---

## 19–20. WhatsApp Integration and Connection

| Method | Route | Permission | Returns |
| --- | --- | --- | --- |
| `GET` | `/whatsapp/connection` | `whatsapp.templates.view` **or** `whatsapp.connect` | `WhatsAppConnection` |
| `POST` | `/whatsapp/connection/sync` | `whatsapp.connect` | `WhatsAppConnection` |
| `POST` | `/whatsapp/connect` | `whatsapp.connect` | `WhatsAppConnection` |
| `POST` | `/whatsapp/connect/manual` | **Role `SuperAdmin`** | `WhatsAppConnection` |
| `POST` | `/whatsapp/disconnect` | `whatsapp.disconnect` | `WhatsAppConnection` |

### Embedded Signup flow — implemented

```
Angular opens Meta Embedded Signup popup
        ↓
Meta returns { code, wabaId, phoneNumberId } to the client
        ↓
POST /api/v1/whatsapp/connect  { code, wabaId, phoneNumberId }
        ↓
Backend exchanges the code for an access token   (app secret never leaves the server)
        ↓
Token encrypted (AES-256-GCM) and stored
        ↓
Backend verifies the phone number against Meta
        ↓
Returns WhatsAppConnection with status "connected"
```

**There is no OAuth redirect/callback endpoint.** The client runs the popup and posts the three
values. Do not build a redirect handler.

If the token exchange succeeds but verification fails, the response is `409`
(`whatsapp_verification_failed`), the credential is **kept**, and the connection is left in `error`.
The operator can fix permissions at Meta and press sync — they do not restart signup.

`POST /whatsapp/connect/manual` takes a system-user token directly. It is an operator tool, **Super
Admin only**, for exercising the messaging path before app review. Do not expose it in tenant UI.

`POST /whatsapp/disconnect` **destroys** the stored token. Reconnecting means running signup again.

### `WhatsAppConnection`

```ts
interface WhatsAppConnection {
  status: ConnectionStatus;
  displayPhoneNumber: string;
  verifiedName: string;
  businessProfileAbout: string;
  businessCategory: string;
  qualityRating: 'green' | 'yellow' | 'red';
  messagingLimit: number;      // 0 means "not yet assigned by Meta", treat as unknown not zero
  messagesLast24h: number;
  connectedAt: string | null;
  webhookHealthy: boolean;
  templateNamespaceAlias: string;
}
```

`GET /whatsapp/connection` **never returns 404.** A tenant with no connection gets
`status: 'disconnected'` with empty fields — render a connect prompt from it.

---

## 21. WhatsApp Connection Status

Only four values are implemented:

| Value | Meaning | Suggested UI |
| --- | --- | --- |
| `connected` | Verified and usable | Green badge, show number and quality |
| `disconnected` | Never connected, or disconnected | Connect prompt |
| `pending` | Stored, verification in flight | Spinner / "verifying" |
| `error` | Credential stored but verification failed | Amber badge + "Reconnect" and the failure reason |

`expired` and `suspended` from the UI spec **do not exist**. Do not switch on them.

---

## 22. WhatsApp Templates

| Method | Route | Permission | Returns |
| --- | --- | --- | --- |
| `GET` | `/templates` | `whatsapp.templates.view` | `MessageTemplate[]` — unpaged |
| `POST` | `/templates/sync` | `whatsapp.templates.sync` | `MessageTemplate[]` |
| `POST` | `/templates` | `whatsapp.templates.sync` | `MessageTemplate` |
| `PUT` | `/templates/{id}` | `whatsapp.templates.sync` | `MessageTemplate` |
| `DELETE` | `/templates/{id}` | `whatsapp.templates.sync` | `null` |

```ts
interface MessageTemplate {
  id: string;                  // "tpl_…"
  name: string;
  category: 'marketing' | 'utility' | 'authentication';
  status: 'approved' | 'pending' | 'rejected' | 'paused';
  language: string;            // BCP 47, e.g. "en_GB"
  headerText: string | null;
  bodyText: string;            // placeholders preserved verbatim
  footerText: string | null;
  variables: string[];
  buttons: string[];
  qualityScore: 'green' | 'yellow' | 'red';
  timesUsed: number;
  updatedAt: string;
  rejectionReason: string | null;
}
```

`bodyText` keeps `{{1}}`, `{{2}}` exactly as Meta stores them. **Do not normalise or strip them** —
highlight them for the operator.

`POST /templates/sync` walks Meta's pagination fully and matches on **name + language**. Local
templates Meta no longer knows about are **left alone**, never deleted. `409`
(`whatsapp_not_connected`) if no account is connected.

---

## 23. WhatsApp Messaging

> **There is no `POST /whatsapp/messages` endpoint. Individual message sending is not exposed.**

Messages are sent **only** by the campaign dispatcher, a Quartz job. The flow:

```
POST /api/v1/campaigns/{id}/send        (moves campaign to "sending", returns immediately)
        ↓
CampaignDispatchJob polls every minute
        ↓
Materialises one CampaignMessage row per subscribed recipient
        ↓
Sends batches of 100 via Meta Cloud API
        ↓
Meta webhook returns delivery receipts
        ↓
Campaign counters update
```

`POST /campaigns/{id}/send` returns the `CampaignResponse` with `status: "sending"`, **not** a
completed send. Do not present it as "sent".

### Dispatcher behaviour worth surfacing in the UI

- **Templates with variables are refused.** The campaign draft has nowhere to supply bindings, so a
  template with `{{1}}` causes the campaign to move to `failed` with a reason on the failures report.
  Filter the template picker to `variables.length === 0`.
- Only `subscribed` contacts are messaged, resolved at dispatch time.
- Rate limiting: when the tenant's rolling 24-hour allowance is exhausted, the campaign moves to
  `paused` and **resumes automatically** when capacity returns. Show "paused — resumes automatically",
  not an error.
- Retries: 3 attempts per message before it is written off to the failures report.

---

## 24. WhatsApp Webhooks

```
GET  /api/v1/whatsapp/webhook     Meta subscription handshake
POST /api/v1/whatsapp/webhook     Delivery receipts and template reviews
```

> **Backend-to-backend only. Never call these from Angular.** They are anonymous and authenticated
> solely by an HMAC-SHA256 signature over the raw body (`X-Hub-Signature-256`). There is nothing for
> the client to do here.

Handled events: message status (`sent`, `delivered`, `read`, `failed`) and template review outcomes.
Receipts are idempotent — Meta redelivers, and a receipt that does not move a message forward is
dropped.

Their effect on the client: campaign counters change, `webhookHealthy` becomes true, delivery
failures appear in `GET /reports/failures`, and a `campaignProgress` event is pushed over SignalR
(§26).

---

## 25. Campaigns

| Method | Route | Permission | Returns |
| --- | --- | --- | --- |
| `GET` | `/campaigns` | `whatsapp.campaigns.reports` or `.create` | `Campaign[]` — unpaged |
| `POST` | `/campaigns` | `whatsapp.campaigns.create` | `Campaign` |
| `PUT` | `/campaigns/{id}` | `whatsapp.campaigns.edit` | `Campaign` |
| `DELETE` | `/campaigns/{id}` | `whatsapp.campaigns.delete` | `null` |
| `POST` | `/campaigns/{id}/schedule` | `whatsapp.campaigns.schedule` | `Campaign` |
| `POST` | `/campaigns/{id}/send` | `whatsapp.campaigns.send` | `Campaign` |
| `POST` | `/campaigns/{id}/pause` | `whatsapp.campaigns.pause` | `Campaign` |
| `POST` | `/campaigns/{id}/cancel` | `whatsapp.campaigns.cancel` | `Campaign` |

Campaign writes are rate limited under the `rl:campaigns` policy (§38).

```ts
interface CampaignDraft {
  name: string;
  templateId: string;          // must be an approved template, else 409
  audienceLabel?: string;
  groupIds?: string[];
}

interface ScheduleCampaignRequest { scheduledAt: string; }  // must be in the future
```

### Lifecycle — implemented states only

```
draft ──schedule──> scheduled ──(due)──> sending ──> completed
  └──────send──────────────────────────────┘
                                  sending ──pause──> paused ──> sending (auto-resume)
   scheduled / sending / paused ──cancel──> failed
```

`CampaignStatus`: `draft`, `scheduled`, `sending`, `completed`, `paused`, `failed`.

> **There is no `cancelled` status.** Cancel maps to `failed` and stamps `completedAt`. Label it
> "Cancelled" in the UI if you prefer, but switch on `failed`.

Transitions are an explicit allow-list; an invalid one returns `409`
(`invalid_campaign_transition`). Editing is refused once a campaign has started (`campaign_not_editable`).
Sending with an empty audience returns `409` (`empty_audience`).

`POST /send` on an already sending or completed campaign is a **no-op that returns current state**,
not an error — retries are safe.

---

## 26. Campaign Progress (real time)

**SignalR is implemented.** There is no polling endpoint — `GET /campaigns/{id}/progress` does not
exist.

```
Hub URL: {host}/hubs/realtime
```

Authentication: the standard bearer token, passed **as a query-string parameter** because browsers
cannot set headers on a WebSocket handshake. The backend accepts `access_token` from the query string
for the `/hubs` path only.

```ts
this.connection = new signalR.HubConnectionBuilder()
  .withUrl(`${environment.realtimeUrl}`, {
    accessTokenFactory: () => this.auth.accessToken()!,
  })
  .withAutomaticReconnect()
  .build();

this.connection.on('campaignProgress', (campaign: Campaign) => this.store.upsert(campaign));
this.connection.on('notificationReceived', (n: AppNotification) => this.notifications.push(n));
```

| Event | Payload |
| --- | --- |
| `campaignProgress` | Full `Campaign` object with updated metrics |
| `notificationReceived` | `AppNotification` |

Groups are derived from the token's claims on the server. **The client cannot join a group** — there
is no `joinGroup` method to call, and attempting to subscribe to another tenant's stream is not
possible.

Redis backs the hub, so events reach the client whichever instance handled the write.

Reconnect handling: on `onreconnected`, refetch `GET /campaigns` — events fired while disconnected
are not replayed.

---

## 27. Dashboard API

```http
GET /api/v1/dashboard?period=30d
```

Permission `dashboard.view`. Rate limited under `rl:reports`. Backed by Dapper.

```ts
interface DashboardSnapshot {
  kpis: KpiSummary;
  trend: TrendPoint[];
  funnel: FunnelStage[];
  activity: ActivityEntry[];
}

interface KpiSummary {
  messagesSent: number; delivered: number; read: number; failed: number;
  clickThroughRate: number;
  messagesSentDelta: number; deliveredDelta: number; readDelta: number;
  failedDelta: number; clickThroughRateDelta: number;
}

interface TrendPoint { date: string; sent: number; delivered: number; read: number; }  // date-only
interface FunnelStage { label: string; value: number; }
interface ActivityEntry {
  id: string; actor: string; actorInitials: string;
  action: string; subject: string; occurredAt: string;
}
```

`*Delta` values are percentage changes against the preceding period of equal length.

---

## 28. Reports

| Method | Route | Permission | Returns |
| --- | --- | --- | --- |
| `GET` | `/reports/overview` | `reports.view` | Report snapshot |
| `GET` | `/reports/failures` | `reports.view` | `PagedResult<DeliveryFailure>` |

```ts
interface DeliveryFailure {
  id: string; campaignName: string; contactName: string;
  phoneNumber: string; reason: string; errorCode: number; occurredAt: string;
}
```

Both are Dapper-backed and rate limited under `rl:reports` (4 requests / window — do not poll them).

**Report export endpoints do not exist**, despite `reports.export`, `reports.download.csv`,
`reports.download.excel` and `reports.download.pdf` being in the catalogue. See §48.

---

## 29. Employees

All under `/api/v1/employees`. Permission `settings.employees`. **Requires the `employees` plan
module.**

| Method | Route | Returns |
| --- | --- | --- |
| `GET` | `/employees` | `Employee[]` — unpaged |
| `POST` | `/employees/invite` | `Employee` |
| `PUT` | `/employees/{id}` | `Employee` |
| `PUT` | `/employees/{id}/permissions` | `Employee` |
| `PUT` | `/employees/{id}/role` | `Employee` |
| `PUT` | `/employees/{id}/status` | `Employee` |
| `POST` | `/employees/{id}/resend-invite` | `null` |
| `DELETE` | `/employees/{id}/invite` | `null` |
| `DELETE` | `/employees/{id}` | `null` |

```ts
interface Employee {
  id: string;                  // "emp_…"
  name: string;
  initials: string;            // server-computed
  email: string;
  jobTitle: string;
  role: string;                // "SuperAdmin" | "Admin" | "Employee"
  status: 'active' | 'invited' | 'suspended';
  permissions: string[];       // effective set — identical to their token's claim
  lastActiveAt: string | null;
  invitedAt: string;
}

interface InviteEmployeeRequest {
  email: string; name: string; jobTitle: string;
  permissions?: string[];
  role?: string;               // "Admin" | "Employee", default Employee
  permissionSetId?: string;
}
```

### Guards you will hit — all return `409` or `403`

| Attempt | Result |
| --- | --- |
| Grant a permission the caller does not hold | `403` |
| Grant a permission whose module is not in the plan | `409` `permission_not_in_plan` |
| Grant `Admin` without being an Admin | `403` |
| Grant `SuperAdmin` | `403` — never assignable here |
| Edit an Admin's or Super Admin's permissions | `409` `role_derived_permissions` |
| Suspend, demote or remove yourself | `409` `cannot_target_self` |
| Suspend, demote or remove the last Admin | `409` `last_admin` |
| Invite past the seat limit | `409` `seat_limit_reached` |
| Invite an address already registered | `409` `email_taken` |
| Resend/revoke an invitation for someone already active | `409` `not_invited` |

`PUT /employees/{id}/permissions` takes the **complete replacement set**, not a delta:

```json
{ "permissions": ["dashboard.view", "contacts.view", "contacts.create"] }
```

Unknown permission strings → `422` listing them.

Suspending revokes active sessions immediately.

**Seat accounting:** invited employees **do** occupy a seat; suspended employees **do not**.
`usage.employees.used` matches this exactly.

Invitation acceptance is `POST /auth/accept-invitation` (§5.3), not an `/employees` route.

---

## 30. Permission Sets

`/api/v1/permission-sets`. Permission `settings.employees`. Requires the `employees` module.

| Method | Route | Returns |
| --- | --- | --- |
| `GET` | `/permission-sets` | `PermissionSet[]` |
| `POST` | `/permission-sets` | `PermissionSet` |
| `PUT` | `/permission-sets/{id}` | `PermissionSet` |
| `DELETE` | `/permission-sets/{id}` | `null` |
| `POST` | `/permission-sets/{id}/apply` | `Employee[]` |

```ts
interface PermissionSet {
  id: string; name: string; description: string;
  isSystem: boolean;
  permissions: string[];
  assignedCount: number;
}
```

`apply` body: `{ "employeeIds": ["emp_1", "emp_2"] }`. It **overwrites** each target's permissions
and is subject to every guard in §29.

System sets cannot be edited or deleted — `403`.

> ⚠ `assignedCount` counts employees holding **every** permission in the set, not an exact match.
> Someone with the set plus extras is counted. See §49.

### There is no roles/permissions catalogue endpoint

`GET /roles` and `GET /permissions` **do not exist**. The permission matrix must be built from the
static catalogue in §8, hardcoded in the client, and cross-referenced against
`plan.modules` to decide which categories to disable. See §48.

---

## 31. Subscriptions

| Method | Route | Permission | Returns |
| --- | --- | --- | --- |
| `GET` | `/subscription` | `settings.subscription` | `SubscriptionSnapshot` |
| `POST` | `/subscription/change-plan` | `settings.subscription` | `SubscriptionSnapshot` |
| `POST` | `/subscription/cancel` | `settings.subscription` | `SubscriptionSnapshot` |
| `POST` | `/subscription/resume` | `settings.subscription` | `SubscriptionSnapshot` |
| `POST` | `/subscription/auto-renew` | `settings.subscription` | `SubscriptionSnapshot` |
| `GET` | `/plans` | **none** (authenticated) | `SubscriptionPlan[]` |

```ts
interface SubscriptionSnapshot {
  subscription: Subscription;
  plan: SubscriptionPlan;      // embedded in full
  usage: UsageMetric[];
}

interface Subscription {
  planId: string; planName: string;
  status: 'active' | 'trial' | 'expired' | 'suspended' | 'cancelled';
  billingCycle: 'monthly' | 'yearly';
  currentPeriodStart: string; currentPeriodEnd: string;
  nextRenewalAt: string | null;
  expiresAt: string;
  autoRenew: boolean;
  trialEndsAt: string | null;
  seatsPurchased: number;
  amount: number; currency: string;
}
```

A tenant with no subscription returns **`404`** — render a "choose a plan" state.

### Cancel and resume

`POST /subscription/cancel` body `{ reason?, immediate? }`. Default (`immediate: false`) sets
`autoRenew: false`, **leaves `status` as `active`** until `expiresAt`, and clears `nextRenewalAt`.
`immediate: true` ends it now.

`POST /subscription/resume` reverses a pending cancellation. `409` if there is nothing to resume
(`not_cancelled`) or the subscription has already lapsed (`subscription_lapsed`).

### Change plan

```ts
interface ChangePlanRequest { planId: string; billingCycle: 'monthly' | 'yearly'; }
```

> The `effective` field from the UI spec **is not implemented**. The server decides.

Upgrade applies immediately and resets the period. Downgrade keeps the current period and takes
effect at renewal.

A downgrade below current usage is refused with **`409`** (`downgrade_blocked`), naming the first
offending metric in `detail`. Show `detail` verbatim.

> **Proration is not implemented.** No payment processor is configured; `IPaymentGateway` is a seam
> with a manual gateway behind it. See §49.

---

## 32. Billing

| Method | Route | Permission | Returns |
| --- | --- | --- | --- |
| `GET` | `/billing/history` | `settings.billing` | `BillingHistory` |
| `POST` | `/billing/invoices/{id}/pay` | `settings.billing` | `Invoice` |
| `GET` | `/billing/invoices/{id}/pdf` | `settings.billing` | **`501`** |
| `GET` | `/billing/payment-methods` | `settings.billing` | `PaymentMethod[]` |
| `POST` | `/billing/payment-methods` | `settings.billing` | `PaymentMethod` |
| `DELETE` | `/billing/payment-methods/{id}` | `settings.billing` | `null` |
| `PUT` | `/billing/payment-methods/{id}/default` | `settings.billing` | `PaymentMethod` |
| `GET` | `/billing/profile` | `settings.billing` | `BillingProfile` |
| `PUT` | `/billing/profile` | `settings.billing` | `BillingProfile` |

```ts
interface BillingHistory {
  invoices: Invoice[];
  payments: Payment[];
  renewals: RenewalRecord[];
}
```

One call fills all three tabs, newest first. Client-side derivations:

- **Outstanding** = Σ `amount + tax` over invoices with status `due` or `overdue`
- **Paid to date** = Σ `amount` over payments with status `succeeded`
- **Next charge** = `subscription.amount` on `subscription.nextRenewalAt`
- **Failed-payment banner** when any payment has `status: 'failed'`

`POST /billing/invoices/{id}/pay` — send an `Idempotency-Key` header. It is honoured.

### Payment methods

```ts
interface AddPaymentMethodRequest {
  providerToken: string;                       // from the processor's client-side tokenisation
  kind?: 'card' | 'bank_transfer' | 'paypal';  // default 'card'
  makeDefault?: boolean;                       // default true
}
```

> **Never send card details.** The API stores a processor token and display-only fields. A request
> body that looks like a card number is rejected with `422`. Build the card form with the
> processor's SDK (Stripe Elements or equivalent), never a raw form posted here.

Removing the only method while auto-renew is on returns `409` (`payment_method_required`).

`GET /billing/profile` returns **empty strings rather than 404** when never filled in.

---

## 33. Feature Limits

The client must never hardcode plan rules. Two sources, both in `SubscriptionSnapshot`:

### Module flags — `plan.modules`

All eight keys are always present:

```json
{ "whatsapp": true, "email": false, "social": false, "crm": true,
  "reporting": true, "ai": false, "api": false, "employees": true }
```

Use these to show or hide whole areas of the sidebar, and to disable permission-matrix categories.
The backend enforces them too: a gated endpoint returns `403` regardless of the UI.

Module-gated endpoint groups:

| Module | Gated routes |
| --- | --- |
| `crm` | all `/contacts`, `/groups`, `/tags` |
| `employees` | all `/employees`, `/permission-sets` |

`/subscription` and `/billing/*` are **deliberately not gated** — a customer must always be able to
see what they pay for and fix a failed payment.

### Usage — `usage[]`

```ts
interface UsageMetric {
  key: UsageMetricKey;
  label: string;            // render verbatim
  used: number;
  limit: number | null;     // null = unlimited, render as ∞
  unit: string;
}
```

Keys: `employees`, `contacts`, `campaigns`, `whatsappAccounts`, `emailAccounts`, `socialAccounts`,
`apiCalls`, `storage`, `messagesDaily`, `messagesMonthly`.

> **`limit: null` means unlimited. Never treat `0` as "no limit"** — a genuine `limit: 0` (a Starter
> plan with no social accounts) is legitimately exhausted, and computing `used / 0` renders 100 % and
> fires a false upgrade prompt. Branch on `limit === null` first.

`storage` is in **megabytes**; divide by 1024 to display GB.

Thresholds are a client concern: ≥ 75 % amber, ≥ 90 % orange, at or over the limit red.

`used` is computed live on every request — it is safe to gate actions on.

**Enforced limits:** contacts (on create and import) and employee seats. `campaigns`,
`apiCalls`, `storage`, `messagesDaily` and `messagesMonthly` are **reported but not enforced**. See §49.

---

## 34. Notifications

| Method | Route | Permission | Returns |
| --- | --- | --- | --- |
| `GET` | `/notifications` | authenticated | `AppNotification[]` |
| `POST` | `/notifications/{id}/read` | authenticated | `AppNotification[]` |
| `POST` | `/notifications/read-all` | authenticated | `AppNotification[]` |

```ts
interface AppNotification {
  id: string;
  kind: NotificationKind;      // dotted string, see §42
  title: string; body: string;
  priority: 'critical' | 'warning' | 'info' | 'success';
  icon: string;
  read: boolean;
  actionLabel: string | null;
  actionRoute: string | null;  // an Angular route to navigate to
  occurredAt: string;
}
```

Both write endpoints return the **full updated list**, so the client replaces state in one step.

Unread count is derived client-side: `notifications.filter(n => !n.read).length`. There is no
dedicated count endpoint, and **no delete endpoint** (§48).

Real time: `notificationReceived` over SignalR (§26).

---

## 35. Admin APIs (SuperAdmin)

| Method | Route | Permission | Returns |
| --- | --- | --- | --- |
| `GET` | `/superadmin/admins` | `platform.tenants` | `AdminAccount[]` |
| `GET` | `/superadmin/overview` | `platform.tenants` | `PlatformOverview` |
| `POST` | `/superadmin/admins` | `platform.tenants` | `AdminAccount` |
| `PUT` | `/superadmin/admins/{id}` | `platform.tenants` | `AdminAccount` |
| `PUT` | `/superadmin/admins/{id}/status` | `platform.tenants` | `AdminAccount` |
| `DELETE` | `/superadmin/admins/{id}` | `platform.tenants` | `null` |
| `GET` | `/admin/tenants` | `platform.tenants` | `PagedResult<Tenant>` |
| `GET` | `/admin/audit` | `platform.audit` | `PagedResult<AuditLogEntry>` |
| `GET` | `/admin/system` | `platform.monitoring` | `SystemSnapshot` |
| `GET` | `/admin/plans` | `platform.plans` | `SubscriptionPlan[]` |
| `POST` | `/admin/plans` | `platform.plans` | `SubscriptionPlan` |
| `PUT` | `/admin/plans/{id}` | `platform.plans` | `SubscriptionPlan` |
| `POST` | `/admin/plans/{id}/duplicate` | `platform.plans` | `SubscriptionPlan` |
| `DELETE` | `/admin/plans/{id}` | `platform.plans` | `null` |

All rate limited under `rl:admin` (120/min).

`POST /superadmin/admins` creates an organisation **and** its first Admin in one transaction, and
sends an invitation. Body:

```ts
interface CreateAdminAccountRequest {
  name: string; email: string; organisation: string;
  plan?: 'starter' | 'growth' | 'scale' | 'enterprise';   // default 'starter'
}
```

The `id` returned is the `adm_`-prefixed value to use as `?adminId=` (§9).

---

## 36. Audit Logs

```
GET /api/v1/admin/audit?page=1&pageSize=25&search=...
```

Permission `platform.audit`.

```ts
interface AuditLogEntry {
  id: string; actor: string; actorInitials: string;
  action: string; target: string; workspace: string;
  ipAddress: string;
  severity: 'info' | 'warning' | 'critical';
  occurredAt: string;
}
```

Audit rows are written in the **same transaction** as the change they describe, and credential
fields are redacted before they are stored. No token, password hash or Meta secret can appear here.

**Audit export is not implemented** (§48).

---

## 37. Redis / Cache Behaviour

Caching is entirely server-side and **invisible to the client**. There are no cache headers to read
and no cache-busting parameters to send.

```
Request → API → Redis → (miss) → PostgreSQL → Redis → Response
Redis unavailable → PostgreSQL → Response          (fail-open, same shape)
```

Redis failure degrades latency, never correctness — the client sees identical responses. Do not build
retry logic for it.

---

## 38. Rate Limiting

Fixed-window limiters, partitioned per authenticated user (or per IP when anonymous).

| Policy | Applies to | Limit |
| --- | --- | --- |
| `rl:authentication` | all `/auth/*` | **10 / minute**, no queue |
| `rl:reports` | `/dashboard`, `/reports/*` | **4 per window**, queue 8 |
| `rl:campaigns` | campaign writes | queue 10 |
| `rl:admin` | `/admin/*`, `/superadmin/*` | **120 / minute**, no queue |
| `rl:webhook` | `/whatsapp/webhook` | queue 100 |
| `rl:default` | everything else | queue 20 |

A rejection returns `429` with a **`Retry-After` header in seconds** and a problem document.

```ts
if (err.status === 429) {
  const wait = Number(err.headers.get('Retry-After') ?? 5) * 1000;
  this.toast.warn(`Too many requests. Retrying in ${wait / 1000}s.`);
  return timer(wait).pipe(switchMap(() => retryOnce()));
}
```

> Do not auto-retry aggressively, and **never auto-retry `/auth/login`** — that burns the 10/minute
> budget and locks the account.

The reports limit of 4 is deliberately low. **Do not poll the dashboard.**

---

## 39. Backend Resilience Behaviour

Polly wraps the Meta Cloud API client only: retry with backoff, circuit breaker, per-attempt and
total timeouts. Only transient failures are retried — a `4xx` from Meta is never retried.

**Do not duplicate this in Angular.** When you receive a `502` or `503`, the backend has already
exhausted its retries. A `503` (`external_service_unavailable`) is worth offering a manual retry
button for; a `502` (`external_service_error`) is not — it means Meta rejected the request itself.

---

## 40. Quartz / Async Operations

Two scheduled jobs run server-side:

| Job | Schedule | Effect visible to the client |
| --- | --- | --- |
| `campaign-dispatch` | every minute | Campaign counters advance; `campaignProgress` events |
| `refresh-token-cleanup` | 03:15 UTC daily | None |

**Endpoints that return before the work finishes:**

| Endpoint | Returns | Actual completion |
| --- | --- | --- |
| `POST /campaigns/{id}/send` | `status: "sending"` | Minutes to hours, via the dispatcher |
| `POST /campaigns/{id}/schedule` | `status: "scheduled"` | At `scheduledAt` |

Everything else in this API is **synchronous** — including the CSV import commit, which returns the
finished result.

There is no generic `operationId` / async-operation contract. Campaign progress is observed via
SignalR (§26); import results via `GET /contacts/import/{jobId}`.

---

## 41. Timezones

**All timestamps are UTC, serialised as ISO 8601 with an offset**, e.g. `2026-08-08T09:14:00+00:00`.
Storage is PostgreSQL `timestamptz`.

```
User picks local time → convert to UTC → send ISO 8601 → backend stores UTC
Backend returns UTC → convert to local for display
```

The backend performs **no timezone conversion on your behalf.** `Tenant.TimeZoneId` exists (default
`UTC`) but is not applied to request or response values.

Campaign `scheduledAt` must be a future UTC instant; a past value returns `422`.

`TrendPoint.date` and `MessageDailyStat.Date` are **date-only** (`YYYY-MM-DD`) in UTC — do not apply
a timezone shift to them or the chart will move by a day.

---

## 42. Enums

**All enums serialise as camelCase strings**, never integers, via
`JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`. Use these exact strings.

```ts
type ContactStatus     = 'subscribed' | 'unsubscribed' | 'blocked';
type ContactLifecycle  = 'lead' | 'customer';
type TagColor          = 'brand' | 'info' | 'warning' | 'danger' | 'neutral';

type ConnectionStatus  = 'connected' | 'disconnected' | 'pending' | 'error';
type QualityRating     = 'green' | 'yellow' | 'red';
type TemplateStatus    = 'approved' | 'pending' | 'rejected' | 'paused';
type TemplateCategory  = 'marketing' | 'utility' | 'authentication';
type CampaignStatus    = 'draft' | 'scheduled' | 'sending' | 'completed' | 'paused' | 'failed';

type BillingCycle        = 'monthly' | 'yearly';
type SubscriptionStatus  = 'active' | 'trial' | 'expired' | 'suspended' | 'cancelled';
type SupportLevel        = 'community' | 'email' | 'priority' | 'dedicated';
type PlanStatus          = 'active' | 'inactive' | 'archived';
type InvoiceStatus       = 'paid' | 'due' | 'overdue' | 'refunded' | 'void';
type PaymentStatus       = 'succeeded' | 'failed' | 'pending' | 'refunded';
type EmployeeStatus      = 'active' | 'invited' | 'suspended';

type TenantPlan          = 'starter' | 'growth' | 'scale' | 'enterprise';
type TenantAccountStatus = 'active' | 'trialing' | 'suspended';
type AuditSeverity       = 'info' | 'warning' | 'critical';
type ServiceStatus       = 'operational' | 'degraded' | 'outage';

type NotificationPriority = 'critical' | 'warning' | 'info' | 'success';
type SearchResultKind = 'contact' | 'campaign' | 'template' | 'employee' | 'report'
                      | 'subscription' | 'setting';

type UsageMetricKey = 'employees' | 'contacts' | 'campaigns' | 'whatsappAccounts'
                    | 'emailAccounts' | 'socialAccounts' | 'apiCalls' | 'storage'
                    | 'messagesDaily' | 'messagesMonthly';

type DuplicateStrategy         = 'phone' | 'email' | 'name';
type BulkMode                  = 'add' | 'remove' | 'replace';
type DuplicateStrategyOnImport = 'skip' | 'update' | 'create';
type ImportJobStatus           = 'queued' | 'processing' | 'completed' | 'failed';
```

### Two enums with non-camelCase wire values

These carry explicit `JsonStringEnumMemberName` overrides. **Do not camelCase them.**

```ts
type PaymentMethodKind = 'card' | 'bank_transfer' | 'paypal';   // note the snake_case

type NotificationKind =
  | 'subscription.expiring'  | 'meta.disconnected'   | 'whatsapp.token.expiring'
  | 'campaign.completed'     | 'campaign.failed'
  | 'payment.received'       | 'payment.failed'
  | 'employee.invited'       | 'plan.upgraded'
  | 'storage.limit'          | 'contacts.limit'      | 'messages.limit';
```

Plan module keys are also literal, **not** camelCased: `whatsapp`, `email`, `social`, `crm`,
`reporting`, `ai`, `api`, `employees`.

---

## 43. Complete API Endpoint Catalog

Every implemented endpoint. `Auth` = bearer required. `Module` = plan module gate.

| Module | Method | Endpoint | Auth | Role | Permission | Plan module |
| --- | --- | --- | --- | --- | --- | --- |
| Auth | POST | `/auth/login` | No | – | – | – |
| Auth | POST | `/auth/refresh` | No | – | – | – |
| Auth | POST | `/auth/forgot-password` | No | – | – | – |
| Auth | POST | `/auth/accept-invitation` | No | – | – | – |
| Auth | POST | `/auth/reset-password` | No | – | – | – |
| Auth | POST | `/auth/logout` | Yes | – | – | – |
| Auth | POST | `/auth/logout-everywhere` | Yes | – | – | – |
| Auth | POST | `/auth/change-password` | Yes | – | – | – |
| Auth | GET | `/auth/me` | Yes | – | – | – |
| Contacts | GET | `/contacts` | Yes | – | `contacts.view` | `crm` |
| Contacts | GET | `/contacts/duplicates` | Yes | – | `contacts.view` | `crm` |
| Contacts | GET | `/contacts/{id}` | Yes | – | `contacts.view` | `crm` |
| Contacts | POST | `/contacts` | Yes | – | `contacts.create` | `crm` |
| Contacts | PUT | `/contacts/{id}` | Yes | – | `contacts.edit` | `crm` |
| Contacts | DELETE | `/contacts/{id}` | Yes | – | `contacts.delete` | `crm` |
| Contacts | POST | `/contacts/bulk-delete` | Yes | – | `contacts.delete` | `crm` |
| Contacts | POST | `/contacts/bulk-tag` | Yes | – | `contacts.edit` | `crm` |
| Contacts | POST | `/contacts/bulk-group` | Yes | – | `contacts.edit` | `crm` |
| Contacts | POST | `/contacts/merge` | Yes | – | `contacts.edit` + `contacts.delete` | `crm` |
| Contacts | POST | `/contacts/import/preview` | Yes | – | `contacts.import` | `crm` |
| Contacts | POST | `/contacts/import/commit` | Yes | – | `contacts.import` | `crm` |
| Contacts | GET | `/contacts/import/{jobId}` | Yes | – | `contacts.import` | `crm` |
| Contacts | GET | `/contacts/export` | Yes | – | `contacts.export` | `crm` |
| Groups | GET | `/groups` | Yes | – | `groups.manage` | `crm` |
| Groups | POST | `/groups` | Yes | – | `groups.manage` | `crm` |
| Groups | PUT | `/groups/{id}` | Yes | – | `groups.manage` | `crm` |
| Groups | DELETE | `/groups/{id}` | Yes | – | `groups.manage` | `crm` |
| Groups | GET | `/groups/{id}/contacts` | Yes | – | `groups.manage` | `crm` |
| Groups | POST | `/groups/{id}/contacts` | Yes | – | `groups.manage` | `crm` |
| Groups | DELETE | `/groups/{id}/contacts` | Yes | – | `groups.manage` | `crm` |
| Tags | GET | `/tags` | Yes | – | `tags.manage` | `crm` |
| Tags | POST | `/tags` | Yes | – | `tags.manage` | `crm` |
| Tags | PUT | `/tags/{id}` | Yes | – | `tags.manage` | `crm` |
| Tags | DELETE | `/tags/{id}` | Yes | – | `tags.manage` | `crm` |
| Tags | GET | `/tags/{id}/contacts` | Yes | – | `tags.manage` | `crm` |
| Tags | POST | `/tags/{id}/contacts` | Yes | – | `tags.manage` | `crm` |
| Tags | DELETE | `/tags/{id}/contacts` | Yes | – | `tags.manage` | `crm` |
| WhatsApp | GET | `/whatsapp/connection` | Yes | – | `whatsapp.templates.view` or `whatsapp.connect` | – |
| WhatsApp | POST | `/whatsapp/connection/sync` | Yes | – | `whatsapp.connect` | – |
| WhatsApp | POST | `/whatsapp/connect` | Yes | – | `whatsapp.connect` | – |
| WhatsApp | POST | `/whatsapp/connect/manual` | Yes | **SuperAdmin** | – | – |
| WhatsApp | POST | `/whatsapp/disconnect` | Yes | – | `whatsapp.disconnect` | – |
| WhatsApp | GET/POST | `/whatsapp/webhook` | No (HMAC) | – | – | – |
| Templates | GET | `/templates` | Yes | – | `whatsapp.templates.view` | – |
| Templates | POST | `/templates/sync` | Yes | – | `whatsapp.templates.sync` | – |
| Templates | POST | `/templates` | Yes | – | `whatsapp.templates.sync` | – |
| Templates | PUT | `/templates/{id}` | Yes | – | `whatsapp.templates.sync` | – |
| Templates | DELETE | `/templates/{id}` | Yes | – | `whatsapp.templates.sync` | – |
| Campaigns | GET | `/campaigns` | Yes | – | `whatsapp.campaigns.reports` or `.create` | – |
| Campaigns | POST | `/campaigns` | Yes | – | `whatsapp.campaigns.create` | – |
| Campaigns | PUT | `/campaigns/{id}` | Yes | – | `whatsapp.campaigns.edit` | – |
| Campaigns | DELETE | `/campaigns/{id}` | Yes | – | `whatsapp.campaigns.delete` | – |
| Campaigns | POST | `/campaigns/{id}/schedule` | Yes | – | `whatsapp.campaigns.schedule` | – |
| Campaigns | POST | `/campaigns/{id}/send` | Yes | – | `whatsapp.campaigns.send` | – |
| Campaigns | POST | `/campaigns/{id}/pause` | Yes | – | `whatsapp.campaigns.pause` | – |
| Campaigns | POST | `/campaigns/{id}/cancel` | Yes | – | `whatsapp.campaigns.cancel` | – |
| Dashboard | GET | `/dashboard` | Yes | – | `dashboard.view` | – |
| Reports | GET | `/reports/overview` | Yes | – | `reports.view` | – |
| Reports | GET | `/reports/failures` | Yes | – | `reports.view` | – |
| Employees | GET | `/employees` | Yes | – | `settings.employees` | `employees` |
| Employees | POST | `/employees/invite` | Yes | – | `settings.employees` | `employees` |
| Employees | PUT | `/employees/{id}` | Yes | – | `settings.employees` | `employees` |
| Employees | PUT | `/employees/{id}/permissions` | Yes | – | `settings.employees` | `employees` |
| Employees | PUT | `/employees/{id}/role` | Yes | – | `settings.employees` | `employees` |
| Employees | PUT | `/employees/{id}/status` | Yes | – | `settings.employees` | `employees` |
| Employees | POST | `/employees/{id}/resend-invite` | Yes | – | `settings.employees` | `employees` |
| Employees | DELETE | `/employees/{id}/invite` | Yes | – | `settings.employees` | `employees` |
| Employees | DELETE | `/employees/{id}` | Yes | – | `settings.employees` | `employees` |
| Permission sets | GET | `/permission-sets` | Yes | – | `settings.employees` | `employees` |
| Permission sets | POST | `/permission-sets` | Yes | – | `settings.employees` | `employees` |
| Permission sets | PUT | `/permission-sets/{id}` | Yes | – | `settings.employees` | `employees` |
| Permission sets | DELETE | `/permission-sets/{id}` | Yes | – | `settings.employees` | `employees` |
| Permission sets | POST | `/permission-sets/{id}/apply` | Yes | – | `settings.employees` | `employees` |
| Subscription | GET | `/subscription` | Yes | – | `settings.subscription` | – |
| Subscription | POST | `/subscription/change-plan` | Yes | – | `settings.subscription` | – |
| Subscription | POST | `/subscription/cancel` | Yes | – | `settings.subscription` | – |
| Subscription | POST | `/subscription/resume` | Yes | – | `settings.subscription` | – |
| Subscription | POST | `/subscription/auto-renew` | Yes | – | `settings.subscription` | – |
| Plans | GET | `/plans` | Yes | – | none | – |
| Billing | GET | `/billing/history` | Yes | – | `settings.billing` | – |
| Billing | POST | `/billing/invoices/{id}/pay` | Yes | – | `settings.billing` | – |
| Billing | GET | `/billing/invoices/{id}/pdf` | Yes | – | `settings.billing` | – |
| Billing | GET | `/billing/payment-methods` | Yes | – | `settings.billing` | – |
| Billing | POST | `/billing/payment-methods` | Yes | – | `settings.billing` | – |
| Billing | DELETE | `/billing/payment-methods/{id}` | Yes | – | `settings.billing` | – |
| Billing | PUT | `/billing/payment-methods/{id}/default` | Yes | – | `settings.billing` | – |
| Billing | GET | `/billing/profile` | Yes | – | `settings.billing` | – |
| Billing | PUT | `/billing/profile` | Yes | – | `settings.billing` | – |
| Notifications | GET | `/notifications` | Yes | – | none | – |
| Notifications | POST | `/notifications/{id}/read` | Yes | – | none | – |
| Notifications | POST | `/notifications/read-all` | Yes | – | none | – |
| Search | GET | `/search` | Yes | – | none | – |
| Platform | GET | `/superadmin/admins` | Yes | – | `platform.tenants` | – |
| Platform | GET | `/superadmin/overview` | Yes | – | `platform.tenants` | – |
| Platform | POST | `/superadmin/admins` | Yes | – | `platform.tenants` | – |
| Platform | PUT | `/superadmin/admins/{id}` | Yes | – | `platform.tenants` | – |
| Platform | PUT | `/superadmin/admins/{id}/status` | Yes | – | `platform.tenants` | – |
| Platform | DELETE | `/superadmin/admins/{id}` | Yes | – | `platform.tenants` | – |
| Platform | GET | `/admin/tenants` | Yes | – | `platform.tenants` | – |
| Platform | GET | `/admin/audit` | Yes | – | `platform.audit` | – |
| Platform | GET | `/admin/system` | Yes | – | `platform.monitoring` | – |
| Platform | GET | `/admin/plans` | Yes | – | `platform.plans` | – |
| Platform | POST | `/admin/plans` | Yes | – | `platform.plans` | – |
| Platform | PUT | `/admin/plans/{id}` | Yes | – | `platform.plans` | – |
| Platform | POST | `/admin/plans/{id}/duplicate` | Yes | – | `platform.plans` | – |
| Platform | DELETE | `/admin/plans/{id}` | Yes | – | `platform.plans` | – |
| Health | GET | `/health/live` | No | – | – | – |
| Health | GET | `/health/ready` | No | – | – | – |
| Health | GET | `/health` | Yes | Platform | – | – |

---

## 44. Angular Service Mapping

| Backend controller | Angular service | Methods |
| --- | --- | --- |
| `AuthController` | `AuthApiService` | `login`, `refresh`, `logout`, `logoutEverywhere`, `forgotPassword`, `acceptInvitation`, `resetPassword`, `changePassword`, `me` |
| `ContactsController` + `ContactWriteController` | `ContactApiService` | `list`, `getById`, `getDuplicates`, `create`, `update`, `delete`, `bulkDelete`, `bulkTag`, `bulkGroup`, `merge`, `importPreview`, `importCommit`, `getImportJob`, `export` |
| `GroupsController` + `GroupWriteController` | `GroupApiService` | `list`, `create`, `update`, `delete`, `listMembers`, `addMembers`, `removeMembers` |
| `TagsController` + `TagWriteController` | `TagApiService` | `list`, `create`, `update`, `delete`, `listTagged`, `tag`, `untag` |
| `WhatsAppController` | `WhatsAppApiService` | `getConnection`, `syncConnection`, `connect`, `connectManual`, `disconnect` |
| `TemplatesController` + `TemplateWriteController` | `TemplateApiService` | `list`, `sync`, `create`, `update`, `delete` |
| `CampaignsController` + `CampaignWriteController` | `CampaignApiService` | `list`, `create`, `update`, `delete`, `schedule`, `send`, `pause`, `cancel` |
| `DashboardController` | `DashboardApiService` | `getSnapshot`, `getReportOverview`, `getFailures` |
| `EmployeesController` | `EmployeeApiService` | `list`, `invite`, `update`, `updatePermissions`, `updateRole`, `updateStatus`, `resendInvite`, `revokeInvite`, `delete` |
| `PermissionSetsController` | `PermissionSetApiService` | `list`, `create`, `update`, `delete`, `apply` |
| `SubscriptionController` + `PlansController` | `SubscriptionApiService` | `getSnapshot`, `getPlans`, `changePlan`, `cancel`, `resume`, `setAutoRenew` |
| `BillingController` + `BillingProfileController` | `BillingApiService` | `getHistory`, `payInvoice`, `downloadInvoice`, `listPaymentMethods`, `addPaymentMethod`, `removePaymentMethod`, `setDefaultPaymentMethod`, `getProfile`, `updateProfile` |
| `NotificationsController` | `NotificationApiService` | `list`, `markRead`, `markAllRead` |
| `SearchController` | `SearchApiService` | `search` |
| `SuperAdminController` + `AdminAccountController` + `PlatformAdminController` + `PlanAdministrationController` | `AdminApiService` | `listAdmins`, `getOverview`, `createAdmin`, `updateAdmin`, `updateAdminStatus`, `deleteAdmin`, `listTenants`, `getAudit`, `getSystem`, `listPlans`, `createPlan`, `updatePlan`, `duplicatePlan`, `deletePlan` |
| `RealtimeHub` | `RealtimeService` | `connect`, `disconnect`, `campaignProgress$`, `notifications$` |

---

## 45. TypeScript Models

Exact mirrors of the backend DTOs. Field names are camelCased by the serialiser.

```ts
// ---- Envelope -------------------------------------------------------------
export interface ApiResponse<T> { data: T; message: string | null; traceId: string; }
export interface PagedResult<T> {
  items: T[]; page: number; pageSize: number; totalItems: number; totalPages: number;
}
export interface ProblemDetails {
  type: string; title: string; status: number; detail: string; instance: string;
  errorCode: string; traceId: string; exceptionId?: string;
  errors?: Record<string, string[]>;
}

// ---- Auth -----------------------------------------------------------------
export interface AuthTokens { accessToken: string; refreshToken: string; expiresAtUtc: string; }
export interface CurrentUser {
  id: number; email: string; displayName: string; tenantName: string | null;
  isSuperAdmin: boolean; roles: string[]; permissions: string[];
}

// ---- Contacts -------------------------------------------------------------
export interface Contact {
  id: string; fullName: string; initials: string; phoneNumber: string;
  email: string | null; country: string; status: ContactStatus;
  tagIds: string[]; groupIds: string[];
  optedInAt: string | null; lastMessagedAt: string | null; createdAt: string;
}
export interface ContactGroup {
  id: string; name: string; description: string;
  contactCount: number; createdAt: string; updatedAt: string;
}
export interface ContactTag {
  id: string; name: string; color: TagColor; contactCount: number; createdAt: string;
}
export interface DuplicateGroup {
  matchValue: string; strategy: DuplicateStrategy; contacts: Contact[];
}

// ---- WhatsApp -------------------------------------------------------------
export interface WhatsAppConnection {
  status: ConnectionStatus; displayPhoneNumber: string; verifiedName: string;
  businessProfileAbout: string; businessCategory: string; qualityRating: QualityRating;
  messagingLimit: number; messagesLast24h: number; connectedAt: string | null;
  webhookHealthy: boolean; templateNamespaceAlias: string;
}
export interface MessageTemplate {
  id: string; name: string; category: TemplateCategory; status: TemplateStatus;
  language: string; headerText: string | null; bodyText: string; footerText: string | null;
  variables: string[]; buttons: string[]; qualityScore: QualityRating;
  timesUsed: number; updatedAt: string; rejectionReason: string | null;
}

// ---- Campaigns ------------------------------------------------------------
export interface CampaignMetrics {
  audienceSize: number; sent: number; delivered: number;
  read: number; clicked: number; failed: number;
}
export interface Campaign {
  id: string; name: string; templateName: string; status: CampaignStatus;
  metrics: CampaignMetrics; audienceLabel: string;
  scheduledAt: string | null; completedAt: string | null;
  createdBy: string; createdAt: string;
}
export interface DeliveryFailure {
  id: string; campaignName: string; contactName: string; phoneNumber: string;
  reason: string; errorCode: number; occurredAt: string;
}

// ---- Workspace ------------------------------------------------------------
export interface Employee {
  id: string; name: string; initials: string; email: string; jobTitle: string;
  role: string; status: EmployeeStatus; permissions: string[];
  lastActiveAt: string | null; invitedAt: string;
}
export interface PermissionSet {
  id: string; name: string; description: string; isSystem: boolean;
  permissions: string[]; assignedCount: number;
}
export interface AppNotification {
  id: string; kind: NotificationKind; title: string; body: string;
  priority: NotificationPriority; icon: string; read: boolean;
  actionLabel: string | null; actionRoute: string | null; occurredAt: string;
}
export interface SearchResultGroup {
  kind: SearchResultKind; label: string; results: SearchResult[];
}
export interface SearchResult {
  id: string; kind: SearchResultKind; title: string;
  subtitle: string; icon: string; route: string;
}

// ---- Billing --------------------------------------------------------------
export interface PlanLimits {
  maxEmployees: number | null; maxContacts: number | null; maxCampaigns: number | null;
  maxWhatsAppAccounts: number | null; maxEmailAccounts: number | null;
  maxSocialAccounts: number | null; maxApiCallsPerMonth: number | null;
  maxStorageMb: number | null; dailyMessageLimit: number | null;
  monthlyMessageLimit: number | null;
}
export interface SubscriptionPlan {
  id: string; name: string; tagline: string;
  monthlyPrice: number; yearlyPrice: number; currency: string;
  trialDays: number; renewalPeriodMonths: number; discountPercent: number;
  isPromotional: boolean; isMostPopular: boolean; isRecommended: boolean;
  status: PlanStatus; supportLevel: SupportLevel;
  modules: Record<string, boolean>; limits: PlanLimits;
  highlights: string[]; sortOrder: number; updatedAt: string;
}
export interface Subscription {
  planId: string; planName: string; status: SubscriptionStatus; billingCycle: BillingCycle;
  currentPeriodStart: string; currentPeriodEnd: string;
  nextRenewalAt: string | null; expiresAt: string; autoRenew: boolean;
  trialEndsAt: string | null; seatsPurchased: number; amount: number; currency: string;
}
export interface UsageMetric {
  key: UsageMetricKey; label: string; used: number; limit: number | null; unit: string;
}
export interface SubscriptionSnapshot {
  subscription: Subscription; plan: SubscriptionPlan; usage: UsageMetric[];
}
export interface Invoice {
  id: string; number: string; planName: string; billingCycle: BillingCycle;
  amount: number; tax: number; currency: string; status: InvoiceStatus;
  issuedAt: string; dueAt: string; paidAt: string | null;
  periodStart: string; periodEnd: string; downloadUrl: string;
}
export interface Payment {
  id: string; invoiceNumber: string; amount: number; currency: string;
  status: PaymentStatus; method: PaymentMethodKind;
  cardBrand: string | null; cardLast4: string | null;
  processedAt: string; failureReason: string | null;
}
export interface RenewalRecord {
  id: string; planName: string; billingCycle: BillingCycle; amount: number;
  currency: string; renewedAt: string; periodEnd: string; automatic: boolean;
}
export interface BillingHistory {
  invoices: Invoice[]; payments: Payment[]; renewals: RenewalRecord[];
}
export interface PaymentMethod {
  id: string; kind: PaymentMethodKind; brand: string | null; last4: string | null;
  expiryMonth: number | null; expiryYear: number | null;
  isDefault: boolean; createdAt: string;
}
export interface BillingProfile {
  companyName: string; addressLine1: string; addressLine2: string;
  city: string; region: string; postalCode: string; country: string;
  taxId: string; billingEmail: string;
}

// ---- Platform -------------------------------------------------------------
export interface AdminAccount {
  id: string; name: string; initials: string; email: string; organisation: string;
  plan: TenantPlan; status: TenantAccountStatus;
  employeeCount: number; contactCount: number; campaignCount: number;
  leadCount: number; customerCount: number; messagesThisMonth: number;
  lastActiveAt: string; createdAt: string;
}
export interface AuditLogEntry {
  id: string; actor: string; actorInitials: string; action: string; target: string;
  workspace: string; ipAddress: string; severity: AuditSeverity; occurredAt: string;
}
```

### Identifier format

Public identifiers are **prefixed strings**: `cnt_42`, `cmp_7`, `tpl_3`, `grp_1`, `tag_9`, `emp_5`,
`plan_2`, `inv_11`, `pay_4`, `rnw_2`, `adm_8`, `tnt_1`, `ntf_6`, `aud_3`, `pms_2`, `dlf_5`, `pm_1`.

Treat them as **opaque strings**. Never parse, increment or construct one. `CurrentUser.id` is the
one numeric identifier in the API and is not used in any route.

---

## 46. Request / Response Examples

### Create a contact

```http
POST /api/v1/contacts
Authorization: Bearer eyJhbGciOi...
Content-Type: application/json

{ "fullName": "Camila Weber", "phoneNumber": "+44 7700 900123",
  "email": "camila@example.com", "status": "subscribed", "groupIds": ["grp_1"] }
```

```json
{
  "data": {
    "id": "cnt_142", "fullName": "Camila Weber", "initials": "CW",
    "phoneNumber": "+44 7700 900123", "email": "camila@example.com",
    "country": "GB", "status": "subscribed",
    "tagIds": [], "groupIds": ["grp_1"],
    "optedInAt": "2026-08-08T13:20:17+00:00", "lastMessagedAt": null,
    "createdAt": "2026-08-08T13:20:17+00:00"
  },
  "message": "Camila Weber added.",
  "traceId": "0HN7ABCDEF"
}
```

### Contact limit reached

```json
{
  "type": "https://docs.marketing-platform.io/errors/contact_limit_reached",
  "title": "Contact limit reached", "status": 409,
  "detail": "Your Growth plan allows 25,000 contacts. Upgrade to add more.",
  "errorCode": "contact_limit_reached", "traceId": "0HN7ABCDEF"
}
```

### Validation failure

```json
{
  "type": "https://docs.marketing-platform.io/errors/validation_failed",
  "title": "Validation failed", "status": 422,
  "errorCode": "validation_failed",
  "errors": { "phoneNumber": ["Enter a valid phone number."] },
  "traceId": "0HN7ABCDEF"
}
```

### Send a campaign

```http
POST /api/v1/campaigns/cmp_12/send
Authorization: Bearer eyJhbGciOi...
Idempotency-Key: 6f1c8e2a-...
```

```json
{
  "data": {
    "id": "cmp_12", "name": "Loyalty reminder", "templateName": "loyalty_reminder",
    "status": "sending",
    "metrics": { "audienceSize": 4820, "sent": 0, "delivered": 0,
                 "read": 0, "clicked": 0, "failed": 0 },
    "audienceLabel": "Loyalty members",
    "scheduledAt": "2026-08-08T13:22:00+00:00", "completedAt": null,
    "createdBy": "Hannan", "createdAt": "2026-08-07T09:00:00+00:00"
  },
  "message": "Campaign started.",
  "traceId": "0HN7ABCDEF"
}
```

`sent: 0` is expected — the dispatcher has not run yet. Subscribe to `campaignProgress` (§26).

---

## 47. Angular Integration Rules

### HttpInterceptor chain, in order

1. **Auth interceptor** — attach `Authorization`, single-flight refresh on 401 (§6.4).
2. **Scope interceptor** — append `?adminId=` when a Super Admin has selected an Admin (§9). Never
   append a tenant id.
3. **Unwrap interceptor** — map `ApiResponse<T>` to `T`; surface `message` to the toast service.
4. **Error interceptor** — map `ProblemDetails` per §11.

### Auth store (signals)

```ts
readonly user        = signal<CurrentUser | null>(null);
readonly permissions = computed(() => this.user()?.permissions ?? []);
readonly role        = computed(() => this.user()?.roles[0] ?? null);
readonly isSuperAdmin= computed(() => this.user()?.isSuperAdmin ?? false);

has(p: string) { return this.permissions().includes(p); }
```

Store tokens where your threat model allows; **do not store a tenant id** — you are never given one.

### Guards

| Guard | Checks |
| --- | --- |
| `AuthGuard` | A valid access token exists |
| `RoleGuard` | `role` matches the route's required role |
| `PermissionGuard` | `permissions` contains the route's required permission |
| `SubscriptionGuard` | `subscription.status` is `active` or `trial` |
| `FeatureGuard` | `plan.modules[key] === true` |

`FeatureGuard` and `PermissionGuard` are **both** needed on `/contacts` and `/employees` — the
backend enforces both and will return `403` even if only one is checked client-side.

### Error interceptor

```ts
switch (err.status) {
  case 401: /* handled by auth interceptor */ break;
  case 403: this.toast.error(err.error?.detail ?? 'You do not have access to this.'); break;
  case 404: /* let the component render an empty state */ break;
  case 409:
  case 422: this.forms.applyProblem(err.error); break;   // errors{} if present, else detail toast
  case 429: this.backoff(err); break;
  case 502:
  case 503: this.toast.error('WhatsApp is temporarily unavailable. Try again shortly.'); break;
  default:  this.toast.error(`Something went wrong. Reference ${err.error?.exceptionId}.`);
}
```

Because business rules return **409 with an `errors`-free document**, `applyProblem` must fall back
to a toast of `detail` when `errors` is absent.

### Loading state and toasts

Every write returns `message` — show it. Every read returns `message: null` — show nothing, or the
user gets a toast on every refresh.

The CSV import commit and the contacts export can run for a long time. Use a determinate progress
indicator, not a spinner with a timeout.

---

## 48. Missing APIs

Functionality the UI needs that **does not exist in the backend**. Do not call these; build the
screens against what is listed above, or raise them as backend work.

| Needed for | Missing endpoint |
| --- | --- |
| Permission matrix | `GET /roles`, `GET /permissions` — no catalogue endpoint. Hardcode §8. |
| Email marketing | Every `email.*` endpoint. Module and permissions exist; no controller does. |
| Social automation | Every `social.*` endpoint. Same. |
| Reports | `GET /reports/export`, CSV/Excel/PDF downloads. Permissions exist; endpoints do not. |
| Audit | Audit log export. |
| Notifications | `DELETE /notifications/{id}` — no delete endpoint. |
| Contacts | `xlsx` export format — CSV only. |
| Contacts | Signed-URL export — direct stream only. |
| Billing | Refunds and credit notes. `refunded` exists as a status; nothing issues one. |
| Billing | Signed-URL invoice download. |
| Campaigns | Per-recipient log endpoint. `CampaignMessage` rows exist server-side but are not exposed. |
| Campaigns | Template variable bindings on a campaign draft. |
| WhatsApp | Single-message send. Campaigns only. |
| Subscription | Trial start / conversion endpoints. |
| Settings | Company/workspace settings (`settings.company`), API keys (`settings.apikeys`), integrations (`settings.integrations`). |

---

## 49. Incomplete APIs and Known Inconsistencies

| Item | State | Impact on the client |
| --- | --- | --- |
| `GET /billing/invoices/{id}/pdf` | **Returns `501`.** Route exists, renderer does not. | Disable the download button, or show "coming soon". |
| Business-rule status code | Returns **`409`, not `422`** as the UI specs assume. | Handle 409 and 422 identically for `errorCode`-driven flows (§11). |
| `Contact.country` | Returns an **ISO alpha-2 code** (`GB`), not a display name. | Map codes to names client-side, or expect `GB` in the table. |
| Country by name on write | Sending `"United Kingdom"` is **rejected**; send `GB`. | Use a code-based country picker. |
| `PermissionSet.assignedCount` | Counts employees holding **every** permission in the set, not an exact match. | The number will read high if people have extras. |
| Campaign `cancelled` | Cancel maps to **`failed`**; no `cancelled` status exists. | Label `failed` carefully, or you will show "Failed" for a deliberate cancel. |
| Plan proration | **Not implemented.** No payment processor configured. | Upgrades do not charge a difference. Do not promise proration in the UI. |
| Payment processing | `IPaymentGateway` is a **manual/offline gateway**. `POST /invoices/{id}/pay` does not charge a real card. | Payment flows are structurally complete but not live. |
| Email delivery | `IEmailSender` **writes invitations and resets to the application log** instead of sending them. | Invitation links must be copied from server logs in development. |
| Usage enforcement | Only `contacts` and `employees` limits are enforced. `campaigns`, `apiCalls`, `storage`, `messagesDaily`, `messagesMonthly` are reported but never block. | Gauges are accurate; the gates behind them are not all there. |
| `messagingLimit: 0` | Means "Meta has not assigned a tier yet", treated as unlimited server-side. | Do not render `0` as "no messages allowed". |
| Import commit | **Synchronous**, despite returning an `ImportJobStatus`. Always `completed`. | Do not build a polling loop; the result is final. |
| `settings.company`, `settings.apikeys`, `settings.integrations` | Permissions exist, no endpoints. | Hide those settings tabs. |

---

## 50. Frontend Integration Checklist

```
[ ] Authentication          POST /auth/login, GET /auth/me
[ ] JWT interceptor         Authorization: Bearer, claims decoded client-side
[ ] Refresh token           SINGLE-FLIGHT — concurrent refresh triggers replay revocation
[ ] Roles                   role claim is a single string, not an array
[ ] Permissions             permissions claim is one JSON-array claim; exact match, no wildcards
[ ] Tenant context          NEVER send a tenant id; ?adminId= only for SuperAdmin
[ ] Response envelope       unwrap data; toast message when non-null
[ ] Error handling          RFC 7807; 409 for business rules, 422 for field validation
[ ] Pagination              page / pageSize / search / sortBy / sortDirection; max pageSize 100
[ ] Filtering               send the literal "all" to clear a filter
[ ] Contacts                list, get, create, update, delete, bulk, merge, duplicates
[ ] Groups                  CRUD + membership sub-resources
[ ] Tags                    CRUD + membership sub-resources
[ ] CSV import              preview → commit → re-read; multipart field "file"; .csv only, 10 MB
[ ] CSV export              fetch as blob; bearer token cannot ride a plain link
[ ] WhatsApp connection     Embedded Signup popup → POST /whatsapp/connect (no redirect callback)
[ ] WhatsApp templates      list + sync; preserve {{n}} placeholders verbatim
[ ] WhatsApp messages       NO direct send endpoint — campaigns only
[ ] WhatsApp webhooks       backend-to-backend; never call from Angular
[ ] Campaigns               create, schedule, send, pause, cancel; no "cancelled" status
[ ] Campaign progress       SignalR /hubs/realtime, accessTokenFactory; no polling endpoint
[ ] Dashboard               GET /dashboard — rate limited to 4, do NOT poll
[ ] Reports                 overview + failures; no export endpoints
[ ] Employees               invite, permissions, role, status, resend/revoke invite, delete
[ ] Permission sets         CRUD + apply
[ ] Subscription            snapshot drives every gauge and gate; 404 when none
[ ] Billing                 history, pay, payment methods, profile; PDF returns 501
[ ] Feature limits          plan.modules for gating; limit === null means unlimited
[ ] Notifications           list, mark read, mark all read; no delete
[ ] Admin                   superadmin/admins, admin/tenants, admin/audit, admin/system, admin/plans
[ ] Audit logs              paged; no export
[ ] Rate limiting           read Retry-After; never auto-retry /auth/login
[ ] Timezones               everything UTC ISO 8601; TrendPoint.date is date-only
[ ] Enums                   camelCase strings, EXCEPT PaymentMethodKind and NotificationKind
```

---

## Appendix — Verification

This document was produced by extracting, from the source:

- every `[Route]`, `[HttpGet/Post/Put/Delete]`, `[RequirePermission]`, `[RequireModule]`,
  `[Authorize(Roles=)]` and `[AllowAnonymous]` attribute across all controllers
- every `public sealed record` in `Marketing.Application/DTOs`
- every enum in `Marketing.Common/Constants/ContractEnums.cs`, including
  `JsonStringEnumMemberName` overrides
- the permission catalogue in `Marketing.Common/Constants/Permissions.cs`
- the exception-to-status mapping in `GlobalExceptionHandler` and each `AppException` subclass
- the rate-limiter policies in `RateLimitingExtensions`
- `ApiResponse<T>`, `PagedResult<T>` and `PageRequest`

**Endpoint count: 104 implemented HTTP endpoints across 24 controllers**, plus 3 health endpoints
and 1 SignalR hub.
