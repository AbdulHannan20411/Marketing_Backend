# Employees, Access Control & Attributed Email — Frontend Integration Guide

Everything below is implemented and was verified against a live database and a running API. Where a
detail differs from the requirements document you sent, it says so and explains why. Where something
is not implemented, it says that too.

Base paths: `/api/v1/employees` and `/api/v1/permission-sets`.

---

## 1. Read this first — five things changed

**1. Every write now accepts `?adminId=`.** Seven of them did not: `POST /employees/invite`,
`PUT /{id}/permissions`, `PUT /{id}/status`, `DELETE /{id}`, `POST /permission-sets`,
`PUT /permission-sets/{id}`, `DELETE /permission-sets/{id}`. A Super Admin could list a workspace's
team and every button they pressed returned `403 tenant_not_resolved`. All 14 endpoints scope now.

**2. An invitee with no `permissionSetId` now starts with zero permissions**, as your document
specifies. They were previously getting the Employee role's 12 defaults.

**3. Seat exhaustion is now `409 seat_limit_reached`.** It was a `422` reported against a
`permissions` field the invite request does not have, so the form had nowhere to put the message.

**4. Unknown permission names are now rejected everywhere.** Editing an employee already returned
`422`; building a permission set silently dropped them. An admin ticked a box, saved, and found it
gone.

**5. Your document says the catalogue has 56 permissions. It has 49.** I diffed
`core/models/permission.model.ts` against the backend constants: **49 each side, zero difference in
either direction**. Nothing to fix — the number in the doc is just stale.

---

## 2. Conventions

**Auth.** `Authorization: Bearer <token>` on everything.

**Envelope.** `{ "data": …, "message": string|null, "traceId": string }`.

**Errors.** RFC 7807 with a stable `errorCode`. `422` carries `errors: { field: [messages] }`.

**Super Admin scoping.** `?adminId=adm_2` on any endpoint. Ignored for other roles. **Required for a
Super Admin on every call, read or write** — they have no tenant of their own, and without it the
request fails `403 tenant_not_resolved`.

**Roles** cross the wire PascalCase: `SuperAdmin`, `Admin`, `Employee`.
**Status** crosses the wire camelCase: `invited`, `active`, `suspended`.

Both are as they were; this is stated because the two differ and it is easy to assume otherwise.

---

## 3. Endpoints

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/employees` | Everyone in the workspace |
| POST | `/employees/invite` | §4 |
| PUT | `/employees/{id}` | `name`, `jobTitle`, `email` |
| PUT | `/employees/{id}/permissions` | Complete replacement set |
| PUT | `/employees/{id}/role` | `Admin` or `Employee` |
| PUT | `/employees/{id}/status` | `active` \| `suspended` |
| POST | `/employees/{id}/resend-invite` | Fresh token |
| DELETE | `/employees/{id}/invite` | Revokes an unaccepted invitation |
| DELETE | `/employees/{id}` | Removes access, frees a seat |
| GET/POST | `/permission-sets` | |
| PUT/DELETE | `/permission-sets/{id}` | |
| POST | `/permission-sets/{id}/apply` | `{ employeeIds }` |

---

## 4. `POST /employees/invite`

```jsonc
{
  "name": "Sara Malik",
  "email": "sara@northwind.io",
  "jobTitle": "Campaign Manager",
  "role": "Employee",
  "permissionSetId": "pms_1"
}
```

`role` defaults to `Employee`. There is also an optional `permissions` array — an explicit list wins
over `permissionSetId`, because it is the more specific instruction.

**Starting permissions, verified:**

| Sent | Result |
| --- | --- |
| No set, no list, `role: "Employee"` | **0 permissions** |
| `permissionSetId` | exactly that set's permissions |
| `role: "Admin"` | **45** — held by role; any set sent alongside is ignored |

Returns the created employee with `status: "invited"`.

### Failures

| Condition | Response |
| --- | --- |
| Address already registered | `409 email_taken` |
| Seats exhausted | `409 seat_limit_reached`, allowance in `detail` |
| Unknown permission name | `422 validation_failed`, `errors.permissions` |
| Permission outside the plan's modules | `409 permission_not_in_plan` |
| Granting something the caller lacks | `403` |

> ⚠️ **`email_taken`, not `employee_exists`.** Your document expects `employee_exists` "if that email
> is already in the workspace". The backend check is **platform-wide**, not workspace-wide: an
> address registered in *any* organisation cannot be invited. I did not rename the code, because the
> name would then misdescribe what is actually checked. If you want workspace-scoped uniqueness
> instead, that is a real behaviour change — say so and I will make it.

The invitation link lands on `/auth/accept-invitation?token=…`, built from `Email:ClientBaseUrl`.

---

## 5. Attributed email — implemented

Verified live. A real captured message:

```
From:      Marketing Platform <no-reply@…>          ← unchanged, platform's verified sender
Reply-To:  Super Administrator <admin@…>            ← the person who invited them
Subject:   Super Administrator invited you to join Northwind Retail on Marketing Platform
```

```html
<p>Hello Sara Malik,</p>
<p><strong>Super Administrator</strong> has invited you to work in
   <strong>Northwind Retail</strong> on Marketing Platform. Set your password to get started.</p>
<p><a href="…/auth/accept-invitation?token=…">Set your password</a></p>
<p>This link can be used once and expires in 72 hours.</p>
<p style="color:#6b7280;font-size:12px">This invitation was sent by Super Administrator
   (admin@…) through Marketing Platform. If you weren't expecting it, you can ignore this
   email.</p>
```

The subject matches your preview wording exactly: `{senderName} invited you to join {workspaceName}
on {appName}`. A plain-text alternative carries the same attribution.

When the acting user has no display name or address, the mail goes out **unattributed** — subject
"You have been invited to join …" and no `Reply-To` — rather than attributed to a blank.

### Injection — tested, not assumed

Renaming a workspace to `<script>alert(1)</script> & Co` produced `&lt;script&gt;` in the HTML body
with no raw tag. Renaming it to `Evil\nBcc: victim@example.com` produced a single-line subject with
no header split.

> **One deliberate deviation.** You asked for all three interpolated values to be HTML-escaped,
> including the subject. I header-sanitised the subject instead: escaping it would put a literal
> `&amp;` in the recipient's inbox for a workspace called "Smith & Co". A subject is text, not
> markup. What a header genuinely cannot contain is a line break, so control characters are stripped.

### Rate limit

**20 invitations per hour per workspace**, covering invite and resend. Over it: `429`. Sending
reputation is shared across tenants, so one workspace using the invite endpoint as a mailing list
would stop mail reaching every other customer.

---

## 6. Rules the UI relies on — all verified

| Rule | Status |
| --- | --- |
| `PUT /permissions` revokes sessions | ✅ security stamp rotated **and** refresh tokens revoked |
| Last admin cannot be demoted or removed | ✅ `409 last_admin` |
| No privilege escalation | ✅ `403`; an Employee can never edit permissions |
| Seat limits on invite | ✅ `409 seat_limit_reached` |
| Suspending signs the employee out | ✅ |
| Permission sets overwrite on apply | ✅ `"Applied to 1 people."` |

Other codes you may meet: `409 role_derived_permissions` (an Admin's permissions come from their
role and cannot be edited individually), `409 not_invited` (resend/revoke on someone who has already
accepted), `409 system_permission_set` (a platform-supplied set cannot be deleted),
`409 cannot_target_self`.

---

## 7. Super Admin behaviour

- Required `?adminId=` on **every** call, now honoured everywhere.
- **Seat limits do not apply** to a Super Admin acting in a workspace — a plan ceiling is a billing
  construct, not a security one.
- Actions are audited with the acting Super Admin's identity, taken from the authenticated
  principal.

---

## 8. Not implemented

- **No audit view of permission changes.** Changes reach the audit trail generically through the
  save interceptor, carrying the acting user. I did **not** verify that shape is usable for a
  screen — tell me which fields you need and I will check before you build against it.
- **No bulk invite.**
- **No per-tenant sending domains.** Genuinely sending *from* a customer's own domain needs SPF and
  DKIM records they add. Not attempted.
- **Plan-module gating** is enforced server-side on permissions (`permission_not_in_plan`), but the
  client remains the only thing filtering navigation by module.
