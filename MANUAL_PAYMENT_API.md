# Manual Payment API — Frontend Integration Guide

Everything below is implemented and was verified against a live database and a running API. The JSON
in this document is copied from real responses, not written from the DTO definitions. Where
something is **not** implemented it says so explicitly.

Base paths: `/api/v1/billing/…` (customer) and `/api/v1/superadmin/…` (platform).

---

## 1. What changed from the original spec

Three things differ from the contract you sent. Two are cosmetic; one you should read.

**Request ids are `pyr_…`, not `pay_…`.** `pay_` already identifies a captured `Payment` in this
API. Two entity types behind one prefix makes `pay_5` ambiguous in an audit trail, which is exactly
where a financial control cannot be ambiguous. Ids are opaque to the client — you round-trip them —
so nothing in your code changes.

**`qrImageUrl` is `""` when no QR image has been uploaded**, not a placeholder. Render the account
details alone in that case. It becomes `/api/v1/billing/payment-channels/{Channel}/qr` once an
image exists.

**`GET /subscription` now accepts `?adminId=`.** It previously ignored it, which meant a Super Admin
viewing the subscription page saw *some other organisation's* plan — the filter is bypassed for
platform staff and the query took the first row it found. If your subscription page passes
`adminId` anywhere else, pass it here too.

I also added the admin-side channel CRUD you left open — §6. Without it the feature cannot work at
all: the three channels seed **inactive**, with obvious placeholder account numbers.

---

## 2. Conventions

Same as the rest of the platform.

**Auth.** `Authorization: Bearer <token>` on every call, including the file downloads.

**Success envelope.** `{ "data": …, "message": string|null, "traceId": string }`. The two file
endpoints are the exception — they return raw bytes.

**Paged envelope**, inside `data`:

```jsonc
{ "items": [ … ], "page": 1, "pageSize": 25, "totalItems": 137, "totalPages": 6 }
```

`page` defaults to 1, `pageSize` to 25, capped at 100 (larger values are clamped, not rejected).

**Errors** are RFC 7807 with a stable `errorCode`:

```jsonc
{
  "type": "https://docs.marketing-platform.io/errors/payment_request_pending",
  "title": "Business rule violated",
  "status": 409,
  "detail": "You already have a payment awaiting review. Withdraw it before submitting another.",
  "errorCode": "payment_request_pending",
  "traceId": "…"
}
```

**422** carries a field map: `"errors": { "proof": ["Attach a PNG, JPG, WEBP or PDF file."] }`.

**Permissions.** Customer endpoints need `settings.subscription`. Platform endpoints are **SuperAdmin
role only** — not a permission, deliberately. A tenant admin calling them gets 403 regardless of
what permissions they hold.

**Super Admin scoping.** `?adminId=adm_2` on the customer endpoints acts inside that organisation.
Ignored for every other role. **Required** for a Super Admin, who has no tenant of their own —
without it they get `403 tenant_not_resolved`. The platform endpoints do not accept it and do not
need it.

**Rate limits.** Submitting a payment and uploading a QR share an upload bucket: **10 per 10 minutes
per tenant**, no queue. Over the limit is `429`.

---

## 3. Enums

⚠️ **PascalCase**, unlike most of this API. Compare against these literals exactly.

```
PaymentChannel        JazzCash | EasyPaisa | BankTransfer
PaymentRequestStatus  Pending | Approved | Rejected | Cancelled
billingCycle          "Monthly" | "Yearly"     ← a string field, not an enum on the wire
```

`billingCycle` is a plain string on purpose: the shared `BillingCycle` enum crosses the wire
camelCase on every other billing endpoint, and giving it PascalCase globally would have broken them.

Terminal states are `Approved`, `Rejected` and `Cancelled`. `Cancelled` is the customer withdrawing
before review.

---

## 4. Customer endpoints

### 4.1 `GET /billing/payment-channels`

Active channels only, in display order.

```jsonc
[
  {
    "channel": "JazzCash",
    "displayName": "JazzCash",
    "accountTitle": "NextReach Technologies",
    "accountNumber": "0300 1234567",
    "bankName": null,
    "qrImageUrl": "",
    "instructions": [
      "Open the JazzCash app and choose Scan QR.",
      "Scan the code and enter the exact amount shown above.",
      "Complete the transfer and take a screenshot of the receipt."
    ],
    "isActive": true
  }
]
```

`bankName` is `null` for wallets — relabel your account field from it. An empty array means no
channel has been configured yet; show a "payment is not available, contact support" state rather
than an empty method picker.

### 4.2 `GET /billing/payment-channels/{channel}/qr`

The QR image, raw, with a `Content-Type`. Needs the bearer token, so fetch it as a blob.
**404** when the channel has no image — expected whenever `qrImageUrl` is `""`, so don't request it
in that case.

### 4.3 `POST /billing/payment-requests`

`multipart/form-data`:

| Field | Required | Notes |
| --- | --- | --- |
| `planId` | yes | Must be an **active** plan |
| `billingCycle` | yes | `Monthly` or `Yearly` |
| `channel` | yes | A `PaymentChannel` |
| `proof` | yes | PNG, JPG, WEBP or PDF · max **5 MB** |
| `reference` | no | Transaction id from the receipt |
| `note` | no | Free text |

**There is no `amount` field.** The server derives it from the plan and cycle. If you send one it is
ignored — verified: posting `amount=1` for a plan priced 10 stored 10.

**201** → the request object (§4.5) with `status: "Pending"`.

| Failure | Code |
| --- | --- |
| No file, wrong type, over 5 MB | `422 validation_failed` |
| Unknown or inactive plan | `422 validation_failed` |
| Missing or unreadable `billingCycle` / `channel` | `422 validation_failed` |
| A payment is already awaiting review | `409 payment_request_pending` |

The last one is the important one for UX: **one open request per workspace**. When you get it, show
the existing request's "under review" banner with its Withdraw action rather than an error toast.

### 4.4 `GET /billing/payment-requests?page&pageSize`

This workspace's own submissions, **newest first**. `listMine(1, 1).items[0]` is a valid way to drive
the "under review" banner — ordering is guaranteed.

### 4.5 The request object

Returned identically by every endpoint that yields one.

```jsonc
{
  "id": "pyr_1",
  "status": "Pending",
  "planId": "plan_1",
  "planName": "Testing",
  "billingCycle": "Monthly",
  "amount": 10.0,
  "currency": "USD",
  "channel": "JazzCash",
  "reference": "TXN-84920113",
  "note": "Paid from the finance account.",
  "proofUrl": "/api/v1/billing/payment-requests/pyr_1/proof",
  "proofFileName": "receipt.png",
  "proofContentType": "image/png",
  "organisation": "admin",
  "submittedByName": "Super Administrator",
  "submittedByEmail": "Honey@yopmail.com",
  "adminId": "adm_2",
  "submittedAt": "2026-08-14T08:46:04.9877163+00:00",
  "reviewedAt": null,
  "reviewedBy": null,
  "rejectionReason": null
}
```

- `reference` and `note` are `""` when not supplied, never `null`.
- `reviewedBy` is the administrator's display name, not an id.
- `rejectionReason` is non-null **only** when `status` is `Rejected`. It is cleared from the payload
  in every other state even if one was once recorded.
- `adminId` is the submitting workspace's admin account, for the platform queue's deep link. It is
  `null` only if that workspace somehow has no administrator.
- `proofUrl` starts with `/api/`, so your `downloadAbsolute` uses it as-is.

### 4.6 `GET /billing/payment-requests/{id}` · `POST /billing/payment-requests/{id}/cancel`

Cancel withdraws an unreviewed submission → `status: "Cancelled"`, and returns the updated object.
**409 `payment_already_decided`** once approved or rejected.

### 4.7 `GET /billing/payment-requests/{id}/proof`

The uploaded file, raw, with `Content-Type` and `Content-Disposition`. Needs the token — fetch as a
blob and revoke the object URL when done.

Readable by the owning workspace and by a Super Admin. **Another tenant's id returns 404, not 403** —
verified — so ids cannot be probed for existence.

---

## 5. Platform endpoints

SuperAdmin only. No `adminId`.

### 5.1 `GET /superadmin/payment-requests`

| Query | Notes |
| --- | --- |
| `status` | a `PaymentRequestStatus`, or `all` (default) |
| `search` | matches organisation **or** submitter email, case-insensitively |
| `page`, `pageSize` | as elsewhere |

Ordered **newest first**, matching what your queue assumes. An unrecognised `status` returns an empty
page rather than everything — a filter that silently does nothing is worse than one that shows zero
rows.

### 5.2 `GET /superadmin/payment-requests/{id}` · `/{id}/proof`

Same shapes as §4.5 and §4.7, readable across every tenant.

### 5.3 `POST /superadmin/payment-requests/{id}/approve`

No body. **The only endpoint in the platform that grants a plan.** In one transaction it re-checks
the request is still pending, moves the tenant onto the plan starting a new period, writes an
invoice and a payment row so `/billing/history` shows them, and stamps the decision.

**200** → the updated request, `message: "Payment approved and plan granted."`

| Failure | Code |
| --- | --- |
| Already approved, rejected or cancelled | `409 payment_already_decided` |
| The plan has since been archived or deleted | `409 plan_unavailable` |

The pending check happens *inside* the transaction, so a double-click or two reviewers working the
same queue grant the plan exactly once — verified. You can leave the button enabled and treat a 409
as "someone else got there first": refresh the row and show its current state.

### 5.4 `POST /superadmin/payment-requests/{id}/reject`

```jsonc
{ "reason": "The screenshot shows USD 4 but the Testing plan costs USD 15 per year." }
```

**Required, minimum 10 characters**, enforced server-side as well as in your form. It is emailed to
the customer verbatim and is the only thing telling them what to fix.

**200** → the updated request. **409 `payment_already_decided`** · **422 `validation_failed`** with
`errors.reason`.

---

## 6. Channel administration — a screen you may want to build

This is the piece your spec left open. It exists now, and the feature does not function without it:
the three channels are seeded **inactive** with `accountTitle: "CONFIGURE THIS ACCOUNT"` and a
zeroed account number, so the checkout screen renders but offers nothing until someone fills them
in. That is deliberate — a plausible-looking wrong account number is worse than an absent one.

**`GET /superadmin/payment-channels`** — every channel including inactive ones, same object as §4.1.

**`PUT /superadmin/payment-channels/{channel}`**

```jsonc
{
  "displayName": "JazzCash",
  "accountTitle": "NextReach Technologies",
  "accountNumber": "0300 1234567",
  "bankName": null,
  "instructions": ["Open the JazzCash app and choose Scan QR.", "…"],
  "isActive": true
}
```

`accountTitle` and `accountNumber` are required (`422` otherwise). Blank `instructions` lines are
dropped. `bankName` null or blank marks it a wallet.

**`POST /superadmin/payment-channels/{channel}/qr`** — `multipart/form-data`, field `qr`, PNG/JPG/WEBP,
max **2 MB**. Returns the updated channel. The previous image is deleted only after the new one is
committed.

A minimal screen: three cards, one per channel, each with the account fields, an instructions list
editor, a QR upload with preview, and an active toggle.

---

## 7. Notifications

**In-app** — three new `kind` values on the existing `/notifications` endpoint and `AppNotification`
shape. Verified as delivered:

| Kind | Who sees it | `actionRoute` | Priority |
| --- | --- | --- | --- |
| `payment.submitted` | Each platform reviewer | `/superadmin/payments` | `warning` |
| `payment.approved` | Everyone in the workspace | `/subscription` | `success` |
| `payment.rejected` | Everyone in the workspace | `/subscription` | `critical` |

Icons are `credit-card`, `check-circle` and `alert-triangle` — swap them for whatever your registry
actually has and tell me, and I'll change the server side.

**Emails** go out on all three events through the existing sender, using `Email:ClientBaseUrl` for
links. The rejection mail carries the reason verbatim and links to `/pricing`; the approval mail
names the plan and the new period end.

**SignalR** — existing `/hubs/realtime`, method **`paymentRequestUpdated`**:

```jsonc
{
  "requestId": "pyr_1",
  "status": "Approved",
  "planName": "Testing",
  "organisation": "admin",
  "rejectionReason": null
}
```

Pushed on submission and on every decision, to the submitting workspace **and** to platform
reviewers. Group membership is decided server-side from the role claim — a tenant cannot subscribe
its way into the review stream.

> ⚠️ **I could not verify a delivery.** The hub method, the payload and the platform group are all
> wired and the code path runs, but I had no connected client to observe. Everything else in this
> document I watched happen. Treat realtime as best-effort regardless: both pages must refetch on
> load, and a dropped push must never lose a decision.

---

## 8. Suggested UI states

| Status | Customer view |
| --- | --- |
| — | Method picker from `/payment-channels`; empty list ⇒ "payment unavailable" |
| `Pending` | "Payment under review" banner, amount and channel, **Withdraw** action |
| `Approved` | Success state; refetch `/subscription?adminId=` to show the new plan |
| `Rejected` | Show `rejectionReason` verbatim, prominent; **Try again** → `/pricing` |
| `Cancelled` | Neutral; allow a new submission |

| Status | Reviewer view |
| --- | --- |
| `Pending` | Proof viewer, amount, organisation, **Approve** / **Reject** |
| `Approved` | Read-only, `reviewedBy` + `reviewedAt` |
| `Rejected` | Read-only, plus the reason |
| `Cancelled` | Read-only, greyed |

Required per the design system: skeleton loader, empty state, error state, success state, responsive,
keyboard accessible, WCAG AA contrast, hover animation.

---

## 9. Verified behaviour — you can rely on these

| Behaviour | Result |
| --- | --- |
| `amount=1` posted for a plan priced 10 | Ignored; `10.0` stored |
| Monthly vs Yearly | `10.0` / `15.0`, derived from the plan |
| Second open request | `409 payment_request_pending` |
| `.txt` upload | `422`, `errors.proof` |
| Reason `"nope"` | `422`, `errors.reason` |
| Approve twice | `409 payment_already_decided` |
| Cancel after decision | `409 payment_already_decided` |
| Another tenant's proof | `404` |
| After approval | Subscription Active/Monthly/10.00, period 14/08→14/09; invoice `INV-202608-1` paid; payment row `bank_transfer` / `succeeded` |
| Queue search | `search=admin` → 1 hit, `search=nomatchxyz` → 0 |

---

## 10. Gaps — do not build against these

- **No malware scanning on uploads.** Type, size and extension are enforced, and the served media
  type is derived server-side from the extension we recognised rather than echoed from the client's
  header — so an "image" cannot come back as a script. But nothing scans the bytes. If you show
  PDFs inline, sandbox the viewer.
- **No image re-encoding.**
- **No purpose-written audit entry for decisions.** Changes are captured generically by the audit
  interceptor as `PaymentRequest` updates; I did not verify that, and a financial control deserves
  a dedicated record naming the reviewer and the reason.
- **No refund, no partial payment, no amount override.** A wrong amount is rejected with a reason
  and re-submitted.
- **`duplicateStrategy`-style options do not exist here** — one plan, one cycle, one file.
- **Channels cannot be created or deleted**, only the three seeded ones edited. That matches the
  `PaymentChannel` enum; adding a fourth is a backend change.
