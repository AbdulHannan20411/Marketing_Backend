# Contact Import API — Frontend Integration Guide

Everything below is implemented, deployed and verified against a live database. Nothing here is
planned or aspirational. Where a behaviour is deliberately *not* implemented it says so explicitly.

Base path: `/api/v1/contact-imports`

---

## 1. How the flow works

Importing is **asynchronous**. Every long-running step returns immediately and hands you a batch to
poll. A 50,000-row file takes minutes; an HTTP request that takes minutes is one a proxy, a load
balancer or a closing laptop lid will kill.

```
  [1] GET  /template            (optional) download the blank CSV
       │
  [2] POST /contact-imports     upload → 202, status "Pending"
       │
       │  ← a worker picks it up within ~15s, reads the file, stages every row
       ▼
  [3] status becomes "AwaitingMapping"   ← poll, or listen for importProgress
       │                                   detectedColumns + suggestedMapping now populated
  [4] PUT  /{batchId}/mapping   confirm which column feeds which field
       │
  [5] POST /{batchId}/commit    → 202, status "Committing"
       │
       │  ← a worker writes contacts in chunks, reporting progressPercent as it goes
       ▼
  [6] status becomes "Completed" or "CompletedWithErrors"
       │
  [7] POST /{batchId}/failed-records/export   (only if hasFailedRecords)
       │  → 202, exportId
  [8] GET  /exports/{exportId}          poll until "Completed"
  [9] GET  /exports/{exportId}/download download the .xlsx
```

The operator can `POST /{batchId}/cancel` at any point up to step 5. Once committing has started,
cancel returns **409** — contacts are already in the audience by then, and calling that "cancelled"
would misdescribe what happened.

### Polling vs realtime

Both work. Use whichever fits.

- **Polling:** `GET /contact-imports/{batchId}` every 2–3 seconds while `status` is `Pending`,
  `Processing` or `Committing`. Stop when the status is terminal.
- **Realtime:** connect to the SignalR hub at **`/hubs/realtime`** and handle the
  **`importProgress`** event. It is pushed to everyone in the tenant as the worker advances.

Payload of `importProgress`:

```jsonc
{
  "batchId": "imp_42",
  "status": "Committing",
  "progressPercent": 64,
  "statistics": { "totalRows": 5000, "successful": 3180, "updated": 0,
                  "duplicates": 12, "failed": 8, "skipped": 12 },
  "failureReason": null
}
```

Realtime is **best-effort**. A dropped push never fails the import, so do not treat silence as an
error — fall back to a poll if you have not heard anything for a while.

---

## 2. Conventions that apply to every endpoint

**Authentication.** `Authorization: Bearer <accessToken>` on every call.

**Permissions.** Reads need `contacts.import` **or** `contacts.view`. All writes need
`contacts.import`. The tenant's plan must include the `crm` module, or you get **403**.

**Success envelope.** Every 2xx response is wrapped:

```jsonc
{ "data": { /* the payload described below */ }, "message": null, "traceId": "…" }
```

The two file downloads (`/template`, `/exports/{id}/download`) are the exception — they return raw
bytes with `Content-Disposition`, not an envelope.

**Paged envelope.** List endpoints put this whole object in `data`:

```jsonc
{ "items": [ … ], "page": 1, "pageSize": 25, "totalItems": 137, "totalPages": 6 }
```

`X-Total-Count` carries the same total as a response header.

**Paging parameters.** `?page=` (1-based, default 1) and `?pageSize=` (default **25**, maximum
**100** — larger values are clamped, not rejected).

**Sorting.** `?sortBy=` plus `?sortDirection=Ascending|Descending`. ⚠️ Those are the exact literals —
`asc` and `desc` are rejected with **422**.

**Errors.** Failures are RFC 7807 problem documents with an extra `errorCode` you can switch on:

```jsonc
{
  "type": "https://docs.marketing-platform.io/errors/import_not_mapped",
  "title": "Business rule violated",
  "status": 409,
  "detail": "Choose which column holds each field before importing.",
  "errorCode": "import_not_mapped",
  "traceId": "…"
}
```

Status codes used: **202** work queued · **409** the request conflicts with the import's current
state · **422** the payload itself is invalid · **404** unknown id or another tenant's · **403**
missing permission or plan module.

**Super Admin scoping.** A Super Admin adds `?adminId=adm_2` to any endpoint to act inside that
admin's organisation. For any other role the parameter is silently ignored — not rejected. **This is
required on writes for a Super Admin**, who has no tenant of their own; without it you get 403
`tenant_not_resolved`.

**Identifiers are opaque strings.** Batches are `imp_<n>`, exports are `exp_<n>`. Never parse them,
never construct them — round-trip whatever the API gave you.

---

## 3. Enums

⚠️ **These serialise `PascalCase`, unlike every other enum in this API** (which is camelCase). The
import contract was specified that way. Compare against these literals exactly.

**`BatchStatus`**

| Value | Meaning | Terminal |
| --- | --- | --- |
| `Pending` | Accepted and stored; the parse has not started | no |
| `Processing` | A worker is reading and validating the file | no |
| `AwaitingMapping` | Parsed. Waiting for the operator to confirm the mapping | no |
| `Committing` | A worker is writing contacts | no |
| `Completed` | Finished with every usable row written | **yes** |
| `CompletedWithErrors` | Finished, but some rows could not be used | **yes** |
| `Failed` | The file could not be read, or a worker gave up | **yes** |
| `Cancelled` | Cancelled by the operator | **yes** |

**`RowStatus`** — `Pending` · `Valid` · `Imported` · `Updated` · `Duplicate` · `Skipped` · `Failed`

**`ExportStatus`** — `Pending` · `Processing` · `Completed` · `Failed`

**`ImportErrorCode`**

| Code | Meaning |
| --- | --- |
| `InvalidPhoneNumber` | The number could not be parsed or dialled |
| `MissingRequiredField` | A required field was empty |
| `InvalidEmail` | The email address is not valid |
| `DuplicateContact` | The number already belongs to a stored contact |
| `DuplicateInFile` | The number appears earlier in the same file |
| `UnsupportedColumn` | A mapped column is not one this import understands |
| `InvalidCountry` | The country is not one we recognise |
| `DatabaseError` | The write failed for a reason the operator cannot fix |
| `PlanLimitExceeded` | The plan's contact ceiling was reached before this row |

Every error object also carries a human-readable `message`. **Render `message` when you do not
recognise the `code`** — that way a code added later degrades gracefully instead of breaking the
screen.

**`duplicateStrategy`** (upload only) — `Skip` (default) or `Update`.

---

## 4. Endpoints

### 4.1 `GET /contact-imports/template`

Downloads the blank CSV an operator fills in. Returns `text/csv` as
`contact-import-template.csv`, UTF-8 with a BOM so Excel opens accented names correctly. It carries
one worked example row, because the format of the status, tags and groups columns is not obvious
from the header alone:

```csv
Full Name,Phone Number,Email,Country,Status,Tags,Groups
Jane Doe,+14155552671,jane@example.com,US,Subscribed,vip;newsletter,Customers
```

Tags and groups are `;`-delimited. Any that do not exist yet are created during the commit.

---

### 4.2 `POST /contact-imports`

Uploads a file and queues it for reading. **`multipart/form-data`**:

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `file` | file | yes | `.csv` or `.xlsx` |
| `duplicateStrategy` | string | no | `Skip` (default) or `Update` |

Limits: **25 MB** and **50,000 data rows**. Oversized files are rejected at upload with 422; the row
cap is only discovered once the worker reads the file, and fails the batch with a `failureReason`.

**202** →

```jsonc
{
  "batchId": "imp_42",
  "fileName": "contacts.csv",
  "fileSizeBytes": 40213,
  "status": "Pending",
  "uploadedAt": "2026-08-12T20:42:06.798Z"
}
```

`422` if the file is missing, empty, too large, or of a type we cannot read.

> `duplicateStrategy` is chosen here so the parse can classify duplicates against it. It cannot be
> changed later — to switch strategy, cancel and re-upload.

---

### 4.3 `GET /contact-imports`

A page of imports, **newest first** by default.

| Query | Notes |
| --- | --- |
| `status` | a `BatchStatus`, or `all` (default) |
| `search` | matches the file name, case-insensitively |
| `from` / `to` | ISO 8601, bound the upload date |
| `sortBy` | `fileName` `fileSizeBytes` `status` `totalRows` `failedCount` `uploadedAt` `completedAt` |
| `sortDirection` | `Ascending` / `Descending` |
| `page`, `pageSize` | as above |

Each item:

```jsonc
{
  "batchId": "imp_42",
  "fileName": "contacts.csv",
  "fileSizeBytes": 40213,
  "status": "CompletedWithErrors",
  "statistics": { "totalRows": 5, "successful": 3, "updated": 0,
                  "duplicates": 1, "failed": 1, "skipped": 1 },
  "uploadedAt": "2026-08-12T20:42:06.798Z",
  "completedAt": "2026-08-12T20:43:11.204Z",
  "uploadedBy": "Ayesha Khan",
  "hasFailedRecords": true
}
```

**Reading `statistics`.** Once an import finishes,
`totalRows = successful + updated + skipped + failed`. `duplicates` is what the *parse* observed and
**overlaps the others deliberately** — a duplicate becomes a skip or an update depending on the
strategy, and reporting both is what lets the operator see why a number moved. Do not add
`duplicates` into a total.

`hasFailedRecords` is your gate for showing the "export failed records" action. When it is false,
hide the button rather than offering one that will 409.

---

### 4.4 `GET /contact-imports/{batchId}`

The endpoint the wizard polls. Everything in the list item, plus:

```jsonc
{
  "batchId": "imp_42",
  "fileName": "contacts.csv",
  "fileSizeBytes": 40213,
  "status": "AwaitingMapping",
  "statistics": { … },
  "uploadedAt": "…",
  "completedAt": null,
  "uploadedBy": "Ayesha Khan",
  "hasFailedRecords": true,
  "progressPercent": 100,
  "detectedColumns": ["Full Name", "Phone Number", "Email", "Country", "Status", "Tags", "Groups"],
  "suggestedMapping": {
    "fullName": "Full Name", "phoneNumber": "Phone Number", "email": "Email",
    "country": "Country", "status": "Status", "tags": "Tags", "groups": "Groups"
  },
  "mapping": null,
  "errorGroups": [
    { "code": "DuplicateInFile", "count": 1 },
    { "code": "InvalidPhoneNumber", "count": 1 }
  ],
  "failureReason": null,
  "planLimit": null
}
```

- **`progressPercent`** — real progress, reported not inferred. Draw it directly. `0` while
  `Pending`, real fractions during `Processing` and `Committing`, `100` once a stage has finished or
  the batch is terminal. It never sits at 99 while waiting on the operator.
- **`detectedColumns`** — the file's headers in file order. **Empty while `Pending`/`Processing`** —
  hide the mapping step until it fills.
- **`suggestedMapping`** — our guess from the headers. All seven keys are always present; `null`
  means we could not guess. Pre-fill the mapping form with this.
- **`mapping`** — what the operator saved, or `null`. Fall back to `suggestedMapping`.
- **`errorGroups`** — failure counts by reason, **sorted descending**, so the reason that stopped
  the most rows leads the summary.
- **`failureReason`** — batch-level failure (unreadable file, too many rows). Present only when
  `status` is `Failed`. Show it verbatim; it is written for the operator.
- **`planLimit`** — `null` on an unlimited plan (omit the whole section rather than rendering
  "0 of unlimited"). Otherwise
  `{ "contactLimit": 5000, "currentContacts": 4870, "skippedForLimit": 130 }`.

---

### 4.5 `GET /contact-imports/{batchId}/rows`

A page of staged rows — never the whole file.

| Query | Notes |
| --- | --- |
| `status` | a `RowStatus`, or `all` (default) |
| `page`, `pageSize` | as above |

An unrecognised `status` matches **nothing**, not everything — silently showing the whole file would
let an operator believe it had been filtered.

```jsonc
{
  "rowNumber": 4,
  "status": "Failed",
  "values": {
    "Full Name": "Broken Row",
    "Phone Number": "not-a-phone",
    "Email": "bad@example.com",
    "Country": "US", "Status": "Subscribed", "Tags": "", "Groups": ""
  },
  "errors": [
    { "code": "InvalidPhoneNumber", "field": "PhoneNumber",
      "message": "The phone number is missing or could not be read." }
  ]
}
```

- `rowNumber` is the **spreadsheet** row number — the header is row 1, so data starts at 2. Show it
  as-is; the operator will use it to find the row in their own file.
- `values` is **keyed by the source column header**, matching `detectedColumns`. Render one table
  column per detected column; no positional tracking needed.
- `errors` is empty for rows that are fine.

---

### 4.6 `PUT /contact-imports/{batchId}/mapping`

```jsonc
{
  "mapping": {
    "fullName": "Full Name", "phoneNumber": "Phone Number", "email": "Email",
    "country": "Country", "status": "Status", "tags": "Tags", "groups": "Groups"
  }
}
```

Send **all seven keys**; `null` means unmapped. Values are column *names* from `detectedColumns`,
never indexes.

**`phoneNumber` is the only required field.** Omitting it is 422. Naming a column the file does not
have is 422 with the field path (`mapping.email`).

**200** → the full batch detail object (§4.4), with `mapping` populated.
**409** `import_not_mappable` once the import is past the point where its mapping can change.

Callable repeatedly while the batch is `AwaitingMapping` — let the operator adjust and re-save.

---

### 4.7 `POST /contact-imports/{batchId}/commit`

No body. Queues the writing of contacts.

**202** → `{ "batchId": "imp_42", "status": "Committing", "queuedAt": "…" }`

- **409 `import_not_mapped`** — no mapping saved yet. Send the operator back to step 4.
- **409 `import_not_committable`** — the batch is not waiting to be confirmed (already committed,
  cancelled, or still processing).

After this, poll §4.4 or watch `importProgress` until the status is terminal.

**What the commit does per row:** a number that already exists is skipped (`Skip`) or has the file's
values applied over it (`Update`). New contacts are created up to the plan ceiling; rows beyond it
are skipped and marked `PlanLimitExceeded` rather than failing the whole import — an operator who
waited for a 40,000-row upload gets the 12,000 that fit. An unrecognised country or a malformed
email does not fail a row; the number is what makes a contact reachable.

---

### 4.8 `POST /contact-imports/{batchId}/cancel`

No body. **200** → the batch detail with `status: "Cancelled"`.

**409 `import_not_cancellable`** if the batch is already `Committing` or terminal. The `detail`
string distinguishes the two cases and is written to be shown to the operator.

---

### 4.9 `POST /contact-imports/{batchId}/failed-records/export`

No body. Queues a workbook of the rows that could not be used.

**202** →

```jsonc
{
  "exportId": "exp_7",
  "batchId": "imp_42",
  "status": "Pending",
  "fileName": "contacts-failed-records.xlsx",
  "rowCount": 0,
  "requestedAt": "2026-08-12T20:45:58.668Z",
  "completedAt": null,
  "failureReason": null
}
```

**409 `import_has_no_failures`** when every row was used — gate on `hasFailedRecords` instead.

`rowCount` is 0 until generation finishes. Calling this more than once is allowed; each attempt is
its own export with its own id.

---

### 4.10 `GET /contact-imports/exports/{exportId}`

Poll this until `status` is `Completed` or `Failed`. Same shape as §4.9, with `rowCount`,
`completedAt` and `failureReason` filled in.

---

### 4.11 `GET /contact-imports/exports/{exportId}/download`

Returns the `.xlsx` (`application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`).

**409 `export_not_ready`** if generation has not finished or failed — check §4.10 first.

The workbook is the operator's **correction file**: their original columns, in their original order,
with `Row`, `Error Code` and `Error` appended and the header frozen. The intended loop is fix the
named cells → upload the corrected file again.

> Downloads need the `Authorization` header, so a plain `<a href>` will not work. Fetch as a blob
> and trigger the save from JavaScript.

---

## 5. Suggested UI states

| Batch status | What to show |
| --- | --- |
| `Pending` | Spinner, "Queued". Poll. Cancel available. |
| `Processing` | Progress bar from `progressPercent`, "Reading your file". Cancel available. |
| `AwaitingMapping` | The mapping step. Pre-fill from `mapping ?? suggestedMapping`. Show `errorGroups` as a pre-commit warning. Commit + Cancel. |
| `Committing` | Progress bar, live `statistics`. **No cancel** — hide the button, don't disable it with no explanation. |
| `Completed` | Success summary from `statistics`. |
| `CompletedWithErrors` | Summary + `errorGroups` + "Export failed records" (gated on `hasFailedRecords`) + a link to `rows?status=Failed`. |
| `Failed` | `failureReason` verbatim, and a re-upload action. |
| `Cancelled` | Neutral end state, re-upload action. |

Required per the design system: skeleton loader, empty state, error state, success state, responsive,
keyboard accessible, WCAG AA contrast, hover animation.

---

## 6. Known gaps — do not build against these

- **No import history deletion endpoint.** Batches accumulate; there is no `DELETE`.
- **No expiry on generated exports.** `hasFailedRecords` is derived purely from the failed-row count,
  so it never goes false because a file aged out. If you show "download expired", that is a frontend
  invention with no backend behind it.
- **Retrying a single failed row is not supported.** The loop is export → correct → re-upload.
- **The strategy cannot be changed after upload.** Cancel and re-upload.
- **A superseded synchronous import still exists** at `POST /contacts/import/preview` and
  `/contacts/import/commit`. It formats batch ids as `cnt_`, not `imp_`, and its batches report
  zeroed statistics in the new list. **Do not use it.** Build only against `/contact-imports`.
