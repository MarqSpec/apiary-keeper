# Requirements

What Apiary-Keeper is being built to do, and for whom. **These requirements are a proposal for the owner's review:
none is approved yet**, and most are not built. The code cites the IDs below (`UC-n` for a use case, `FR-n` for a
functional requirement, `NFR-n` for a non-functional one) and the section numbers (`§2`, `§6`), so both are kept
exactly as the code uses them. Where a first version exists in this delivery, the table says **Built**.

## 1. Purpose

One system of record for a family-run commercial apiary — roughly 20 to 500 colonies across several yards — used by
the owner and the family, perhaps five users in a busy season. The first region is the Texas South Plains and
Panhandle around Lubbock; eastern New Mexico comes later. The family uses one installable web app on phone and desktop
and, later, a Telegram group; customers use a separate public website. All of them sit on one API.

It is meant to solve four problems: colony history lives in notebooks, photos and memory; seasonal work (inspections,
mite treatments, feeding) is easy to miss across many yards; honey, wax, nucs and queens are tracked apart from the
colonies that produced them, so cost and yield per yard are guesswork; and compliance (registrations, treatment
records, labeling, pollination contracts) is manual.

**Principles that hold everywhere**

- **Records, not diagnoses.** The app records observations and photos; it never claims to diagnose a disease.
- **History is append-only.** A colony's events are never overwritten.
- **Rules live in the API**, so no app screen, chat command or assistant can get around them.
- **One account per apiary.** Every record belongs to an account; the server is ready for several, the screens show
  one.

## 2. Users and roles

Access is by role, per account: an account is one apiary, and every user belongs to one account and holds one role in
it. Users are invited by the owner; there is no public sign-up. An invited user signs in with email and password, or
with a Google account, through Keycloak (D-19). The design is sized for one to five users per account.

| Role | May do |
|---|---|
| `owner` | Everything, including financial reports, optional modules and inviting users |
| `helper` | Field and sales work: inspections, treatments, harvests, tasks, orders, inventory |
| `grower` | Read only their own pollination placements, hive counts and contract documents |

Today the yard, colony, inspection and media requests serve an `owner` or a `helper`; only an `owner` may invite.

Children under 13 have no user of their own, no email address in the system and no sign-in; the owner-operator
manages their involvement personally. Customers of the website hold a separate customer account (in Keycloak, the
`customer` role), which grants nothing in any apiary's data. Assistants (a later phase) will each be a separate
identity with its own limited permissions, never a person's login. A co-op member is a possible future user (§4.18),
not a role.

## 3. Use cases

| ID | Who | Wants to |
|---|---|---|
| UC-1 | Owner or helper | Record an inspection in the field with taps, photos, short video or a voice note, with no signal |
| UC-2 | Owner | Get the daily and weekly plan: which yards, what to bring, in order of urgency and drive time, adjusted for weather |
| UC-3 | Owner or helper | Track a split, merge, swarm capture, requeen, move or loss without breaking a colony's lineage |
| UC-4 | Owner or helper | Log a treatment and have harvest from that colony blocked until its withdrawal period is over |
| UC-5 | Owner or helper | Record a harvest into a lot and roll yield up to colony and yard |
| UC-6 | Owner | Run pollination contracts: placements, hive counts, strength grading, moves, invoices |
| UC-7 | Owner or helper | Sell nucs, queens and honey, with inventory and the storefront and point of sale kept in step |
| UC-8 | Owner | (Later) ask an assistant questions in the family chat and have it draft records and tasks for approval |
| UC-9 | Owner | Register yards with location, landowner terms, access and forage notes, and keep BeeCheck registration current |
| UC-10 | Family member | Get the day's tasks in the family Telegram group, mark them done, attach photos or notes to a colony by its tag |
| UC-11 | Customer | Browse honey, nucs and queens with live availability, order for pickup or delivery, and pay through a hosted checkout |
| UC-12 | Grower | See their placements, hive counts and contract documents, read only |
| UC-13 | Family member | Send a bug report or feature request from the app and see its status |
| UC-14 | Owner | Stay compliant: registrations, hive identification, sale certificates, labeling, treatment records |
| UC-15 | Owner | See survival, yield, cost per colony and margin per product on a dashboard |
| UC-16 | Owner (optional module) | Take bee-removal requests, quote and schedule jobs, and bring rescued colonies into the apiary |
| UC-17 | Owner | Turn optional modules on or off for the account and for individual users |
| UC-18 | Owner | Invite family members and give each a role |
| UC-19–UC-21 | A future co-op member | Show products on one co-op website, pool honey into co-op batches, and choose what data to share — an idea under discussion, not planned |

## 4. Requirements

Priority: **P0** the first usable version, **P1** version 1, **P2** later; the phase each lands in is in §6.

### 4.1 Yards and colonies (P0)

| ID | Requirement | Status |
|---|---|---|
| FR-1 | A yard has a name, GPS location, state, county, access notes and forage notes | Built |
| FR-2 | Each yard records its landowner: contact, terms (honey, fee or free), nearby crops and spray contact | Built |
| FR-3 | Each yard records its BeeCheck (FieldWatch) registration, with a reminder when hives move | Proposed |
| FR-4 | A colony has a tag ID (QR or NFC on the box), hive brand number, yard, hive type, box count and status (active, dead-out, merged, sold) | Built |
| FR-5 | A queen record holds source, mark colour and year, introduction date and replacement history | Built |
| FR-6 | Splits, merges, swarm captures, requeens and moves are kept in an append-only history | Built |

### 4.2 Inspections (P0)

| ID | Requirement | Status |
|---|---|---|
| FR-7 | An inspection records queen and eggs seen, brood frames and pattern, food stores, temperament, disease signs and actions taken | Built; an inspection's day may not be later than today (UTC) or before 2000-01-01 |
| FR-8 | Photos, short video, free text and voice notes attach to the inspection; media queues offline and uploads on reconnect | Photos and video built; voice notes and the offline queue proposed |
| FR-9 | Capture works offline and syncs: last write wins per field, events never overwritten | Proposed |
| FR-10 | Batch mode: walk a yard, scan tags and quick-grade each colony | Proposed |

### 4.3 Health and treatments (P0)

| ID | Requirement |
|---|---|
| FR-11 | Mite counts are recorded with their method (alcohol wash, sugar roll, sticky board) |
| FR-12 | A treatment records product, EPA registration or label ID, dose, applied and removed dates, and who applied it |
| FR-13 | Each product has withdrawal rules; the API refuses a harvest from an affected colony until it is clear |
| FR-14 | Losses are recorded with a suspected cause |

### 4.4 Tasks and scheduling (P0)

| ID | Requirement |
|---|---|
| FR-15 | Tasks belong to a colony or yard, with a due date, type and assignee |
| FR-16 | Seasonal templates generate tasks (spring build-up, swarm season, flow, fall prep, winter) |
| FR-17 | The daily plan is ordered by route, urgency and drive time, with a packing list |

### 4.5 In-app feedback (P0)

| ID | Requirement |
|---|---|
| FR-18 | "Send feedback" in the app menu takes a type (bug or feature request), title, description and optional picture |
| FR-19 | Feedback carries the app version, screen, device and browser, user, account and time; a bug also carries recent errors |
| FR-20 | The server files the feedback as a GitHub issue through a credential that never reaches the browser |
| FR-21 | A copy is kept in the app's database, so the sender sees its status (Received, Planned, Done) without a GitHub account |
| FR-22 | New feedback notifies the developer, and is queued for triage, where it is accepted or declined |

### 4.6 Family group chat (P1)

| ID | Requirement |
|---|---|
| FR-23 | A Telegram bot joins the family group, behind one chat interface so another platform can replace it |
| FR-24 | The bot posts the daily tasks per yard, overdue treatments, harvest totals and new orders |
| FR-25 | Chat commands mark a task done, attach a photo to a colony by tag, or add a quick note |
| FR-26 | Chat users map to app users, so every action is attributed |
| FR-27 | Video goes through the app's upload path; the chat gets a link, so chat file limits do not apply |

### 4.7 Harvest and inventory (P1)

| ID | Requirement |
|---|---|
| FR-28 | A harvest links to colonies and supers and records weight, moisture and floral source |
| FR-29 | Harvests go into lots traceable to yards and dates; yield rolls up to colony and yard |
| FR-30 | Inventory covers honey, wax, nucs, packages, queens, equipment, feed and treatments |
| FR-31 | Consumables have reorder thresholds |

### 4.8 Sales and customers (P1)

| ID | Requirement |
|---|---|
| FR-32 | Customers, orders and invoices, with wholesale and retail pricing |
| FR-33 | Nuc and queen pre-orders with pickup scheduling |
| FR-34 | Sales integrate with Square and/or Shopify; accounting is an export to QuickBooks |

### 4.9 Customer website (P1)

| ID | Requirement |
|---|---|
| FR-35 | A product catalog with live availability from inventory |
| FR-36 | Orders and pre-orders for pickup, local delivery or market pickup |
| FR-37 | Payment is handed to Square or Shopify hosted checkout; the site never handles card numbers |
| FR-38 | Customer logins (email or Google) with order history and email pickup reminders |
| FR-39 | Contact and bee-removal request forms arrive in the family app as leads |
| FR-40 | About, yard locations **by county only**, and where to buy |
| FR-41 | A separate React app in the business's branding, prerendered for search engines |
| FR-42 | A public API surface separate from the family's: read-only catalog, order and form endpoints, rate limits, spam protection, requests allowed only from the site's domain |
| FR-43 | The seller is resolved from the site's domain |
| FR-44 | (Phase 3) A read-only grower portal |

### 4.10 Pollination contracts (P1)

| ID | Requirement |
|---|---|
| FR-45 | A contract records grower, crop, hive count, minimum strength, rate per hive and dates |
| FR-46 | Placements track which colonies are at which orchard, with move-in and move-out dates |
| FR-47 | Strength-grading reports for growers, and invoices |

### 4.11 Bee removals (P2, optional module, off by default)

FR-48–FR-54: a removal request with caller, address, structure and photos; quoting and scheduling; the job record
with photos, recovered comb and the signed waiver; a rescued colony becoming a new colony with "removal" as its
source; invoicing through orders; no job scheduled without a current TAIS registration and Bee Removal Statement,
with a reminder each September 1; and an optional public request form. When the module is off, every screen, form
and endpoint for it is hidden and recorded data is kept.

### 4.12 Optional modules

| ID | Requirement | Priority |
|---|---|---|
| FR-55 | A module is switched per deployment by an environment variable (e.g. `Features__BeeRemovals=true`) | P0 |
| FR-56 | Per-account switches in the database override that default, changed by the owner only and logged | P1 |
| FR-57 | The server checks the switch on every gated endpoint (a hidden module answers 404), and both apps show only what is on | P0 |

### 4.13 Compliance and reporting (P1)

Each rule is to be confirmed with the agency before it is built.

| ID | Requirement |
|---|---|
| FR-58 | Texas: TAIS beekeeper registration and interstate permit with renewal reminders, the health certificate for bees entering Texas, and the on-request apiary inspection |
| FR-59 | Hive identification: a permanent ID on each hive body, or a yard sign with the manager's contact |
| FR-60 | A sale of bees, nucs or queens carries the TAIS health certificate or the required affidavit |
| FR-61 | Honey labels follow Texas Agriculture Code ch. 131 subch. E, as amended by HB 519 (2025) |
| FR-62 | Treatment records export |
| FR-63 | Reports: survival rate, yield per colony and yard, cost per colony, margin per product |
| FR-64 | (Phase 6) New Mexico rules: per-yard registration, stocking and move notices, pollination permits — provisional |
| FR-76 | When an inspection notes a sign of American foulbrood, Tropilaelaps mites or the yellow-legged hornet, the app prompts the user to report it to TAIS, without diagnosing |

### 4.14 Weather and sensors

| ID | Requirement | Priority |
|---|---|---|
| FR-65 | Weather per yard from the National Weather Service feeds the feeding and inspection task rules | P1 |
| FR-66 | (If ever adopted) third-party hive scales and sensors, normalized to one time series | P2 |

### 4.15 Assistants (Phase 4, P2)

FR-67–FR-73: the domain exposed to AI assistants through an MCP server with read, limited-write and propose-only
tools; anything touching money, sales or regulatory data approved by a person; every assistant action audited;
answers in the family chat; a field scribe that turns a voice note or photo into inspection fields and asks when
unsure; per-assistant permissions, daily cost and rate limits and a monthly spend cap; and a readable journal per yard
and colony.

### 4.16 Accounts and dashboard (P0)

| ID | Requirement | Status |
|---|---|---|
| FR-74 | The owner invites family members by email and gives each a role; no public sign-up | Built in the server: an owner invites a `helper` or `grower` by email (`POST /api/invitations`); not yet on a deployed environment. An `owner` is created by the Keycloak administrator, not invited — see §8 |
| FR-75 | The owner's dashboard shows colony health, yields and margin | Proposed |

### 4.17 Non-functional requirements

| ID | Requirement | Status |
|---|---|---|
| NFR-1 | Offline-first: full inspection capture, photos and video included, with no signal; sync on reconnect or app open | Proposed |
| NFR-2 | Logging an inspection takes a median of under 60 seconds | The capture screen measures it on the phone |
| NFR-3 | Field use: large tap targets, usable with gloves, readable in sunlight, one-handed, simple enough for children | Large tap targets built |
| NFR-4 | Photos resized on the device; video capped at one minute and compressed before upload | Resizing and the one-minute cap built |
| NFR-5 | Durability: append-only history, nightly backups, point-in-time restore | History built; backups proposed |
| NFR-6 | Sign-in on every page through Keycloak (email and password, or Google), invited users only, MFA the owner may require; the user's role and account, carried in Keycloak's signed tokens, checked by the API on every endpoint (from the session for a browser, from a scoped token for an assistant); no sign-in for children under 13. Before sign-in is on, a shared password in front of staging, on every path but `/health` | Built in the server (sign-in, roles and account checked on every apiary request); on no deployed environment yet, so staging keeps the shared password |
| NFR-7 | Cost: production and staging together at $30 a month as the target, up to $40 in a month with higher expenses, on Railway's Pro plan (which private images need), before AI spend; free chat platforms only | Proposed |
| NFR-8 | Every record carries its account, every query is filtered to it, media paths are prefixed by it; the screens show one account | Built |
| NFR-9 | iPhone: sync on app open, a visible unsynced badge, and install to the home screen encouraged, because Safari can clear an uninstalled site's data after 7 days | Proposed |
| NFR-10 | Regulatory rules are configurable per account, never hard-coded | Proposed |
| NFR-11 | A separate staging environment with its own database, bucket, test bot and invented data; production changes only after staging passes, outside working hours, with one-step rollback | Staging's deployment and rollback are scripted; nothing is deployed yet |
| NFR-12 | Traces, logs and metrics from the API through OpenTelemetry | Metrics built (`/metrics`) |
| NFR-13 | Text contrast at least 4.5:1 in the light and dark themes | Built, held by tests |
| NFR-14 | Assistant guardrails: no autonomous payments, customer messages or regulatory filings; customer messages are data, never instructions | Proposed |

### 4.18 Co-op (future, P2)

FR-77–FR-80 record an idea under discussion, not a plan: data private to each account unless its owner shares it; a
shared co-op website; pooled honey batches under a co-op label; and one point of contact for stores. Compliance,
origin claims, health claims and traceability for pooled honey are open questions, and no co-op entity is in the
data model.

## 5. Out of scope for version 1

Self sign-up, billing or switching accounts in the screens; assistants in the first release; hive-sensor hardware;
full accounting (QuickBooks export instead); a full e-commerce platform (hosted checkout instead); disease diagnosis;
a native iPhone app (iPhone users use the website); migratory pollination; accounts for children under 13; feedback
from the customer website.

## 6. Phases

| Phase | Scope | Done when |
|---|---|---|
| 0 — Prototype | A working prototype (yards, colonies, photo inspections) deployed to staging behind a shared password, for the owner to see | The owner has seen the prototype on staging and the decision is made to go on |
| 1 — First version (Texas) | First, with the owner: shadow a season week; list Texas yards and landowner terms; validate the inspection fields and task templates; finalize the data model. Then yards, colonies, inspections with media, treatments, tasks; the offline app; family sign-in and invitations; feedback; optional modules; Texas basics | The inspection fields and task templates are validated; the family logs every inspection for 4 weeks without paper |
| 2 — Chat, business, weather | Telegram bot, harvests and lots, inventory, sales, the customer website, Square/Shopify, QuickBooks, weather, compliance and reports | Yield and margin per yard reported for one season |
| 3 — Pollination | Contracts, placements, grower reports and portal, invoices | One pollination season run end to end |
| 4 — Assistants | MCP server, field scribe, season planner, health watcher, approval queue, in the chat | 80% or more of voice notes become correct records without edits |
| 5 — Sensors | Hive scales and sensors, if ever adopted | Scale data drives at least one task rule |
| 6 — New Mexico | NM rules, NM yards, cross-border move checklist | First NM yard registered and stocked through the app |

Bee removals and the co-op ideas have no phase. Pollination in Phase 3 is a best guess, to be revisited.

## 7. Risks

| Risk | Mitigation |
|---|---|
| Field adoption: capture slower than paper fails | Photo-first capture, batch mode (FR-10), testing in season with the family |
| PWA limits on iPhone: storage eviction and no background sync can lose queued media | Sync on open, a visible unsynced badge, install to the home screen (NFR-9); a native wrapper only if field tests lose media (D-4) |
| Media storage growth from video | The length cap, on-device compression (NFR-4), a retention policy |
| Seasonality: one real test window a year for some features | Seed with historical notes; simulate seasons |
| Regulatory variance by state | Configurable rule packs, not hard-coded (NFR-10) |
| (Phase 4) Assistant misreads | Tag scanning, confidence thresholds, follow-up questions, edit history (FR-71) |

## 8. Open questions

Whether the owner does bee removals; Square, Shopify or both; which transactional email provider; whether Phase 3 is
right for pollination; background workers on `BackgroundService` or Hangfire, and self-hosted or hosted
speech-to-text; the co-op questions above; and how FR-74's "the owner … gives each a role" fits an `owner` created
by the Keycloak administrator rather than invited, with changes to an existing user (its role, required MFA,
re-enabling it, correcting its account) still made only by a Keycloak administrator (D-22).
