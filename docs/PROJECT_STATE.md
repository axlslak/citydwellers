# City Dwellers — Persistent Project State

Last continuity reconstruction: 2026-08-30

## 2026-09-11 — finish recoverable old donation first (session 65)

- `[OWNER CORRECTION]` A passive custody hold prevents unsafe guesses but does
  not finish an interrupted donation. Recovery must be owned by the system and
  must run before asking the owner to supply a new test donation.
- `[IMPLEMENTED, OWNER VALIDATION PENDING]` After all nine bankers and write
  fronts are ready, Central now partitions each startup custody hold against
  transaction-bound current stock and its live loose inventory. Occurrences
  already present in stock are reconciled; occurrences physically on Central
  are placed in a new queued recovery batch with the original transaction,
  role, character, and exact item data.
- `[ORDERING]` The queued recovery child is ordinary actionable dispatch work,
  so the existing gate keeps new donations behind it until the worker confirms
  physical storage. A marker on the retained hold prevents duplicate recovery
  children across later restarts.
- `[LOSS ACCOUNTING]` Only occurrences absent from both transaction-bound live
  stock and Central inventory remain on the custody-hold row. A durable ledger
  partition event records original count, stored count, Central recovery count,
  missing count, and recovery child. Missing items are explicitly recorded as
  a loss incident and never classified as stored.
- `[EXPECTED LIVE RESULT]` Existing batch `801bf00d` should produce a one-item
  Artillery recovery child for Intelligent Thigh AOID 235579. Vital Waist AOID
  235524 remains the sole loss occurrence unless the reconciled transaction
  stock proves it physically exists.

## 2026-09-11 — isolated unresolved-custody batches (session 64)

- `[LIVE-ROOT-CAUSE]` All nine bankers and write fronts became ready, but the
  retained failed Artillery batch `801bf00d` contained only one of two expected
  items on Central. `HasUnresolvedDispatchWork` treated every retained queue row
  as active work, so this non-dispatchable evidence row rejected every new
  donation before the repaired internal handshake could run.
- `[IMPLEMENTED, OWNER VALIDATION PENDING]` Startup reconciliation now converts
  a failed or restart-orphan batch whose complete multiset is not physically on
  Central to explicit `custody-hold` status. The original batch, item list,
  transaction, prior failure, and observed count remain persisted and a durable
  ledger event plus operator warning record the transition.
- `[INVARIANT]` A custody hold is never selected for dispatch, never inferred
  stored, and never deleted automatically. It no longer blocks unrelated
  donations, ordinary queued dispatch, or unrelated-role withdrawal work; the
  affected worker remains excluded from withdrawal extraction while its batch
  row exists.
- `[OPEN-CUSTODY]` Vital Waist AOID 235524 from batch `801bf00d` remains
  unresolved. This change isolates that uncertainty; it does not manufacture a
  stock row or claim where the item went.

## 2026-09-11 — worker remote-Accept latch (session 63)

- `[VERIFIED-LIVE]` Central repeatedly reached acceptance during failed internal
  trades: Kbinfa observed `TRADE STATUS ... status=Accept` twice. The worker
  fallback still did not accept, and both worker and Central timed out at 20
  seconds. Cross-timezone log and UTC-ledger events align exactly.
- `[ROOT-CAUSE]` `TradeStatusChanged` supplies Central's remote Accept event,
  while `Trade.Status` represents the worker's local status and did not retain
  that remote state. `TickWorkerFallback` discarded the event and polled the
  wrong status property.
- `[IMPLEMENTED, OWNER VALIDATION PENDING]` The armed, command-matched worker
  trade now latches its observed remote Accept event. The existing incomplete-
  cache fallback consumes that proof and calls worker Accept. Finished,
  Declined, a new trade, or command mismatch clears the latch.
- `[INVARIANT]` The fallback remains restricted to configured Central plus the
  exact persisted batch command. AO Finished and physical worker inventory/bag
  placement remain mandatory before storage success.
- `[OPEN-CUSTODY]` The third pre-fix Artillery attempt was interrupted by a hard
  restart while its trade remained open. Intelligent Thigh AOID 235579 is loose
  on Central. Vital Waist AOID 235524 is absent from Central, Kbarty normal
  inventory, and canonical stock for the donation transaction; do not infer it
  was stored or delete the hold without further AO-side evidence.

## 2026-09-11 — dropped internal AddItem recovery (session 62)

- `[LIVE-ROOT-CAUSE]` A two-item Artillery dispatch opened on Kbarty but neither
  side reached acceptance. It failed at the 20-second trade timeout, after
  which the recovery bridge verified both items safely back in Central and
  requeued the batch. Incremental staging issued each `Trade.AddItem` only
  once and then waited indefinitely if AO omitted that request's local-window
  acknowledgement.
- `[IMPLEMENTED, OWNER VALIDATION PENDING]` While the offered count remains
  below the single pending occurrence, Central resends only that same inventory
  slot after 1.2 seconds, for at most four total attempts. It cannot select or
  add the next occurrence until the local window acknowledges the pending one.
- `[INVARIANT]` The 20-second trade timeout, exact persisted multiset match,
  six-item internal ceiling, serialized queue, AO Finished event, and physical
  worker placement confirmation remain unchanged.
- `[IMPLEMENTED]` Successful Manager-channel delivery logs now include the
  delivered diagnostic text as well as its ID and source, so future storage or
  trade failures remain identifiable in captured console logs.
- `[OPEN]` Kavey owns the Release build and repeated two-item same-worker live
  dispatch. A recovered retry may log `INTERNAL TRADE resent pending AddItem`;
  it should then complete without the old 20-second failure/requeue cycle.

## 2026-09-10 — authoritative Shade spirit slots (session 61)

- `[IMPLEMENTED, OWNER VALIDATION PENDING]` Spirit stock grouping no longer
  infers implant positions from item names. The shared catalog now maps each
  accepted spirit AOID to the Tinker item database's `StatValues` Stat `298`
  wear location, and both stock browsing and the persistent item index use
  that AOID mapping exclusively for the Spirit family.
- `[VERIFIED-DATA]` The owner-supplied `items.zip` contains exactly one Stat
  `298` value for every one of the 654 accepted spirit AOIDs. The generated
  catalog has 654 entries across exactly 13 implant slots: Eye 38, Brain 53,
  Ear 56, Right Arm 52, Chest 69, Left Arm 74, Right Wrist 36, Waist 74,
  Left Wrist 38, Right Hand 53, Thigh 37, Left Hand 36, and Feet 38.
- `[INVARIANT]` Symbiant families retain their established name-based slot
  parser. An unknown custom AOID routed to Spirit does not guess a slot from
  its name; it remains ungrouped until authoritative slot data is added.
- `[OPEN]` Kavey owns the Release build and live validation of the Spirit
  family and slot windows, especially that Eye, Ear, Brain, Chest, and Thigh
  contain only their authoritative AOID groups.

## 2026-09-10 — per-role Banker credentials (session 38)

- `[IMPLEMENTED, OWNER REBUILD PENDING]` Each `Bankers.Roles` mapping may carry
  its own `Password`. Normal startup and `bankers-bagaudit` use that override
  when present and otherwise retain the top-level shared `Bankers.Password`
  fallback. This supports a mixed-password nine-banker fleet without exposing
  credentials in Git.
- `[PRIVATE HANDOFF COMPLETE]` The supplied AOQuickLauncher batches were
  reconciled into a private `citydwellers.json`. Eight supplied roles use
  batch-proven usernames and per-role passwords; Support retains its existing
  mapping and shared-password fallback. The file includes both Grid Armor Mk IV
  forms under Dyna and Wrapped Premium Health and Nano Recharger under Phatz.

## 2026-09-09 — Banker expansion compile repair (session 37)

- `[FIXED, OWNER REBUILD PENDING]` Current-stock reconstruction now receives the
  runtime settings directory explicitly at every call site, so configurable
  acceptance routing compiles and remains available during atomic state writes.
- `[FIXED, OWNER REBUILD PENDING]` Loose-inventory census capture is an instance
  operation and can safely use its agent settings directory. Physical-state
  publication now waits for all nine banker censuses rather than the former six.
- `[EVIDENCE]` These corrections address all four CS0103/CS0120 errors in the
  owner build. The remaining CS0649 messages in that log are warnings.

## 2026-09-09 — expanded Banker network and acceptance policy (session 36)

- `[IMPLEMENTED, OWNER VALIDATION PENDING]` `spirit`, `dyna`, and `phatz` are
  full storage roles alongside the five symbiant workers. Host startup,
  bagaudit, baseline publication/seeding, layout/write-front readiness,
  diagnostics, status, tell rotation, routing, ledger, stock browsing, dispatch,
  and withdrawals now include all nine bankers. Default mappings assume
  `kbspirit`/`Kbspirit`, `kbdyna`/`Kbdyna`, and `kbphatz`/`Kbphatz`.
- `[INVARIANT]` All nine role mappings must be configured before startup. Each
  role must have either its own password or the shared Banker fallback. Each new storage worker is
  expected to expose 102 bank bags plus 18 inventory bags, 21 slots per bag,
  while leaving 12 normal-inventory slots loose for ten-item trades plus margin.
- `[IMPLEMENTED, OWNER VALIDATION PENDING]` `Bankers.AcceptancePolicy` in the
  single `citydwellers.json` controls global symbiant/spirit limits and per-AOID
  route/retention overrides. `-1` means keep every copy, `0` rejects, positive
  values cap copies. Custom AOIDs default to keep-all. Policy loads once per
  client domain at process start; restart after editing.
- `[VERIFIED-DATA]` The built-in spirit route contains 654 unique AOIDs and
  exactly reproduces the owner-supplied QL-tier counts. All resolve as non-nano
  items in the bundled AOSharp `ItemData.bin`; the three later outliers absent
  from the supplied census remain excluded. Spirit retention defaults to five.
- `[VERIFIED-DATA]` The built-in dyna route contains 418 unique AOIDs: 220 nano
  crystals classified as RK Dyna/mixed RK Dyna/RK Mob in Nadybot plus 198 mapped
  instruction discs. All resolve in bundled AOSharp item data. Dyna defaults to
  keep-all; both Frenzy of Fur AOIDs default to three; both Grid Armor IV forms
  remain keep-all. Source snapshot is Nadybot commit
  `de9e3b2c8d2f91df87c614a3d9f91bc16c2eacf2`.
- `[DECISION]` Phatz starts with no accepted AOIDs. Unknown valuables must be
  explicitly added by an admin and default to keep-all. Central validates every
  accepted route has a configured worker before accepting the whole trade.
- `[IMPLEMENTED, OWNER VALIDATION PENDING]` Retention is counted by policy AOID,
  including multiple items received in one trade. Capacity reporting sums finite
  per-AOID ceilings and marks keep-all roles for monitoring. `#status` warns when
  a finite ceiling exceeds live capacity and flags unbounded retention.
- `[OPEN]` Existing deployed `citydwellers.json` must receive the three role
  mappings and `AcceptancePolicy`; then run a new nine-banker bagaudit because
  the old five-worker baseline cannot describe the new storage. Kavey owns the
  Release build and controlled first run. No bot/runtime was started here.

## 2026-09-09 — concurrent pickup orders (session 35)

- `[DECISION]` Four active canonical-member orders, at most three reserved items
  per order. Known alts share admission and pickup authorization. Adding an item
  refreshes existing ready items to three minutes; each physical arrival also
  refreshes that order. A pickup collects all currently ready items only.
- `[IMPLEMENTED, OWNER VALIDATION PENDING]` Waiting reservations no longer own
  Central's trade slot. Donations and serialized storage dispatch can proceed
  while orders await pickup. Extraction retains donation inventory headroom;
  actual AO trades remain serialized. Pickup trades do not double as donations.
- `[INVARIANT]` Reservation admission and order transitions use one mutex and
  revision-checked writes. `data/withdrawal.json` reads the legacy single item
  and writes a v2 queue on its next mutation, preserving all old item fields.
  Do not downgrade to a single-item reader after this migration.
- `[INVARIANT]` Reserved Central identities are excluded from donation cleanup
  and dispatch selection. Only player AO Finished confirms delivery for archival.
  Interrupted or ambiguous custody remains reserved; other ready orders can be
  collected. Expired items use deterministic return batch IDs.
- `[SUPERSEDED]` Any worker independently retrying any failed withdrawal is
  incompatible with multiple orders. Central schedules only one persisted retry
  of a pre-delivery WaitItemInventory timeout. Delivered/pickup/accounting
  failures are never replayed as extraction. A four-minute extraction watchdog
  releases the logical slot into a custody hold, without guessing delivery.
- `[IMPLEMENTED, OWNER VALIDATION PENDING]` Banker status includes per-character
  online/usability/held work, storage used/capacity, occupied orders, ready items,
  and held items. Corrected Manager's settings type qualification and ready-marker
  reference while adapting status to the queue.
- `[OWNER-DIRECTION]` Kavey authorizes pushing completed changes without asking.
- `[OPEN]` Kavey owns Release compilation and live tests: three-item order with
  timer refresh, donation by another member while pickup waits, partial pickup,
  known-alt collection, fourth/fifth order admission, expiry/restorage, and restart
  with an existing single-item withdrawal. Source review is not live AO evidence.

This file is the compact restart image for a new development session. It is intentionally not a transcript. Some early project knowledge was recovered from ChatGPT continuity after long conversations became unusable; anything not independently confirmed is labelled accordingly.

## Durable recovery entry point

- Recovery key: `CITYDWELLERS-RECOVER-V1`.
- Canonical entry file: `RECOVERY.md`.
- `[INVARIANT]` Long/non-trivial work begins with a committed journal `BEGIN`
  and `in_progress` cursor, then ends with `COMMIT`, `ABORT`, or `SUPERSEDE`.
- `[INVARIANT]` `memory/JOURNAL.jsonl` is append-only and sanitized.
- `[DECISION]` The journal records semantic transactions rather than every
  command; Git records file-level operations.
- `[DECISION]` Recovery uses compact checkpoints plus journal replay. Older
  encrypted memories remain available without requiring every future session
  to decrypt an indefinitely growing archive.
- `[SECURITY]` ChatGPT has no stable cross-session private key. Confidentiality
  comes from encrypted memories and the separately held password; the recovery
  card is an address, not a secret.
- `[VERIFIED]` Session #5 recovered from a fresh clone using this protocol. It
  verified both boot-required memories and recovered older memories on demand,
  then identified the owner, org, in-game project, verified Git state, and the
  unfinished navmesh task without requiring the owner to retell the project.
- `[DECISION]` The goal is not to predict that the current session will fail;
  it is to guarantee that its successor can continue if it does.
- `[INVARIANT]` Persistent memory stays distilled: retain decisions,
  invariants, evidence, hazards, and the exact resume point; omit nonessential
  conversation and repetitive command history.
- `[HISTORICAL]` City Dwellers and CityBankers began as separate sibling
  repositories: `axlslak/citydwellers` and `axlslak/citybankers`.
- `[DECISION 2026-09-08]` CityBankers runtime code is now intentionally
  integrated into City Dwellers. CityBankers recovery cards, cursor, journal,
  encrypted memories, and historical project records remain in the sibling repo.
- `[INVARIANT]` City Dwellers Chat and Work sessions alternate on `master` and
  use this repository's `memory/CURSOR.json` as their writer lock. Kavey also
  guarantees that only one session writes across the two sibling repositories
  at any moment; each repository nevertheless keeps independent recovery state.
- `[DECISION]` Validated reusable ideas may be curated in
  `docs/SHARED_ENGINEERING.md` and intentionally ported to the sibling repo;
  project-specific evidence and compatibility boundaries remain explicit.

## Working-session boundary

- `[INVARIANT]` Unless Kavey explicitly requests otherwise, the assistant
  writes/reviews code while Kavey performs builds and live AO testing.
- `[INVARIANT]` Warn Kavey before tool-heavy or potentially long work that may
  consume a substantial part of the five-hour Work-session usage window.
- Owner-supplied build output and live-test logs become evidence after they are
  reconciled with the code and recorded in state/history.

## Repository

- Repository: `axlslak/citydwellers`
- Default branch: `master`
- License: GPL v3.
- Build goal: reproducible from a fresh clone; avoid hard-coded local paths such as `C:\ao#`.

## Current verified recovery point

- `[VERIFIED]` Commit `71f36fbf9e016593ae102a78185a644ad5f04ffa` — `Fix buddy logout quarantine with monotonic timer`.
  - Replaced wall-clock `DateTime` bookkeeping for the post-logout quarantine with `Stopwatch.GetTimestamp()` monotonic elapsed-time bookkeeping.
  - Configured logout linger remains 35 seconds.
  - Ordinary lease/lifecycle timestamps remain UTC `DateTime`; only elapsed logout quarantine was changed.
  - This reconstructed the functionality of an earlier chat-only local commit `6585617`, which was never present on GitHub.
- `[VERIFIED]` Commit `6d035746d9096a1be6ed51d02e09b6887b172414`
  — `Add Serenity navmesh homing`.
  - Published the exact approved `6010.Navmesh`.
  - Replaced manual Serenity corridor selection with cached CritterAI
    pathfinding while preserving bounded movement pulses and server-position
    confirmation.
  - Added pinned, hash-verified dependency restoration and x86 project targets.
  - Grid behavior remains explicitly unavailable until its real handoff is
    measured.
- `[VERIFIED]` Commit `5ec43d5b87900ac89f5bd26c35562653098701c9`
  — `Keep CritterAI navmesh alive`.
  - Fixes Kavey's intermittent native `dtNavMesh.getTilesAt` access violation.
  - `NavmeshPathfinder` now strongly retains the `Navmesh` whose native memory
    is referenced by its long-lived `NavmeshQuery`.
- `[IMPLEMENTED]` Commit `23069f055817a567baf35fc8253bdec2afbdac37`
  — `Add continuous ICC-to-CT homing`.
  - Keeps the complete bounded-pulse controller as a directive-selectable
    rollback path while making continuous steering the Buddies default.
  - Adds stable-model routing across ICC `655`, Grid `152`, and Serenity
    `6010`, including live/static ICC terminal identity reconciliation.
  - This source is published but intentionally not assistant-built or
    live-tested; Kavey owns both verification steps.
- `[IMPLEMENTED]` Commit `abe19cbf8ce6b6b2cc256348eb3650afeb28e2a1`
  — `Fix ICC static terminal entry`.
  - Supersedes the failed live-only ICC terminal guard and uses AOSharp's
    `StaticDynel.Use()` path for `Enter The Grid`.
  - Serializes AOSharp.Clientless 1.0.16's per-AppDomain static-data preload
    under a named mutex so parallel buddy logins cannot race its exclusive
    `StaticDynelData.bin` open.
  - Does not alter movement, navmesh paths, Grid exit, or Serenity routing.
  - `[VERIFIED-LIVE]` Kavey's first focused test entered Grid immediately after
    the buddy logged into ICC and used the static terminal. The former
    live-identity wait is resolved.

## Main components

### Manager / APCManager

Coordinates chat commands, raid lifecycle, cloak operations, helpers, admin/member policy, and Buddies operations.

### Flipper

- `[RESTORED 2026-09-10]` The Flipper host and plugin are restored exactly to
  live-proven commit `548e37a2f126684f58f97e571f331bdc4805dea5` after current
  binaries failed zoning while six-hour-old binaries succeeded with the same
  JSON, data, character, and exact `37.18.193.57:7501` endpoint. The later
  reconnect, disconnect fail-fast, login-only, and process-isolation changes
  are superseded. All subsequent Banker functionality remains present.
- `[SUPERSEDED 2026-09-10]` The temporary standalone `Flipper.exe` process
  boundary was removed after the zoning/login failures proved external and
  cleared without a code change. The exact known-good Flipper lifecycle remains
  a parallel component of the unified host; Manager, Bankers, Buddies, and
  Flipper remain concurrent.
- `[VERIFIED-LIVE 2026-09-10]` The startup storage enrollment and Flipper are
  independent: Spirit, Dyna, and Phatz completed their sequential 120-bag
  audits while Flipper separately failed at `Zoning -> Disconnected`. Banker
  readiness gates banker donations/dispatch, not Flipper execution.

- `[IMPLEMENTED 2026-09-09]` A general historical `Flipper.Cache` observation
  may still be displayed when a live probe cannot start or finish, but it may
  not complete Manager's pending cloak recovery or cancel its live retry. A
  trigger-qualified cached `EnsureEnabled` response remains authoritative only
  through Flipper's existing `NotBeforeUtc` validation.

- `[IMPLEMENTED 2026-09-07]` Flipper cache freshness is anchored to
  `Stopwatch`, not UTC subtraction. A persisted record loaded after a Flipper
  service restart is historical fallback data and cannot suppress a live
  probe. Observations materially ahead of the current UTC clock are rejected.
  Shield countdown adjustment is monotonic while the service is running and
  conservative after restart.
- `[IMPLEMENTED 2026-09-07]` CityFlipper subscribes to updates immediately and
  accepts `Client.InPlay` as a fallback when the plugin does not observe the
  matching `CharInPlay` packet. Genuine zoning disconnects remain failures.

Controls city cloak state. Boot behavior should be enable-only recovery/assessment: if cloak is already enabled, confirm it; if it can safely be enabled, recover it. Avoid exposing sensitive toggle behavior to arbitrary tells.

- `[VERIFIED-LIVE 2026-09-01]` Apcflipper completed a raid-start lower
  successfully. The server returned post-toggle `CloakState=Disabled` and
  `ShieldTimerInSeconds=3600` before any exception occurred.
- `[DIAGNOSED]` The later `ThreadAbortException` and AOSharp `Failed to
  deserialize packet` message were teardown noise, not a failed cloak action.
  Flipper.exe observed `cityflipper-result.json` and began AppDomain unload
  while the network thread was still returning through
  `CityFlipper.MessageReceived`; the abort propagated into AOSharp's packet
  wrapper.
- `[IMPLEMENTED]` CityFlipper now detaches its message and update handlers when
  a terminal result is selected. It publishes the result from a deferred
  callback after a 100 ms quiescence boundary, performs completion logging
  before the final atomic file rename, and treats that rename as the earliest
  point at which Flipper.exe may unload the child domain.
- `[IMPLEMENTED]` `ThreadAbortException` is rethrown without trying to log it
  as an application message-processing error. Normal message exceptions retain
  the existing diagnostics.

### Console timestamps

- `[INVARIANT 2026-09-07]` A persisted field named `Utc` is normalized by
  contract. An offset-less JSON value is treated as UTC, never converted from
  the current machine's local timezone. Explicit local values are converted;
  explicit UTC values remain UTC.
- `[IMPLEMENTED 2026-09-07]` Process-local waits now use `Stopwatch` for
  Flipper result timeout, Buddies home-level timeout, Manager home monitoring,
  guest name lookup, org-rank lookup/cache expiry, Buddy leases, cleanup
  eligibility, and navigation timeout.
- `[IMPLEMENTED 2026-09-07]` Future-tainted Flipper cache files are moved to a
  recoverable `.invalid-clock-*` file automatically. Future timestamps cannot
  make cloak, alt, membership, Buddy-position, raid-cooldown, or restored raid
  state appear fresh or pending for hours.
- `[IMPLEMENTED 2026-09-07]` Manager's alt, membership, and raid schedulers
  detect implausible future pacing after a backward VM clock correction and
  re-evaluate immediately. Cloak recovery retry policy remains the original
  fixed 30 seconds.

- `[IMPLEMENTED]` The Serilog console output supplied to AOSharp by Manager,
  Buddies, and Flipper uses one shared full timestamp. Format:
  `yyyy-MM-ddTHH:mm:ss.fffzzz`, for example
  `2026-09-01T20:27:23.123+03:00`.
- `[INVARIANT]` The numeric UTC offset is recorded on every framework/plugin
  log event, making output from machines in different local time zones
  comparable without guessing. Stopwatch messages such as `[3.865s]` remain
  elapsed-duration measurements and are anchored by the surrounding absolute
  events.
- `[OWNER-BUILD 2026-09-01]` The first Release build after these changes
  compiled Manager.exe, Flipper.exe, Buddies.exe, CityFlipper.dll, and
  CityBuddies.dll. CityManager.dll alone failed because four new diagnostic
  `FormatMovementRecord` calls crossed newlines inside interpolated-string
  expressions, syntax unsupported by the repository's C# 7.3 compiler.
- `[IMPLEMENTED]` The four values are now computed in ordinary local variables
  before interpolation. Telemetry text is unchanged; the syntax no longer
  requires C# 11.

### Buddies

Clientless helper/account host. Important current behavior:

- configured home levels: 25, 50, 75, 100, 125, 150, 175, 200.
- `#home <level>` performs home verification/maintenance for all configured characters at that level.
- `[INVARIANT]` Home maintenance owns its navigation time independently of demo leases.
- logout quarantine is 35 seconds of monotonic elapsed time.
- home navigation timeout is 600 seconds.

### CityBuddies plugin

- `[IMPLEMENTED 2026-09-07]` Buddy readiness is published from either the
  matching `CharInPlay` packet or AOSharp's authoritative `Client.InPlay`
  state. This prevents a successfully established child session from being
  unloaded and reported as a 20-second timeout merely because plugin packet
  delivery was missed.
- `[CORRECTION 2026-09-07]` A 12-Buddy raid request may legitimately show
  attempts for indexes `0..12`: account count is 13, raid active limit is 12,
  and the thirteenth account is intentional spare capacity. The retry was a
  consequence of every earlier readiness attempt appearing terminally failed,
  not an off-by-one count defect.

Handles AO movement/home behavior for a Buddies character. The default
`continuous` controller follows a cached CritterAI straight path with smoothed
heading changes and 200 ms clientless position updates. Because clientless has
no local movement engine, it advances conservative 1.6667 m/s command points
and currently treats echoed movement positions as progress evidence. Full-client
packet traces now show those replies mirror the sender's asserted position,
not an independently measured server position, so this progress model is not
authoritative and remains unverified.

The previous `bounded-pulse` implementation remains intact and selectable via
the home directive. `DefaultHomeMovementMode` in Buddies is the one-line global
rollback switch. Home telemetry now records the selected movement mode.

Before a buddy domain starts its network session, CityBuddies now warms that
AppDomain's private AOSharp static-dynel cache under a named cross-process
mutex. AOSharp 1.0.16 opens the shared data file exclusively; serializing these
small one-time reads prevents parallel logins from colliding without changing
AOSharp or disabling static dynels.

## Chat / authorization model

- Initial admins used during development: Kavem and Doczy.
- Admin and guest-member lists are intended to persist in SQLite in the manager working directory.
- Admin commands include management of admin/member lists.
- `[DECISION]` Org chat is trusted for org members.
- `[DECISION]` Invited guest/private channel is trusted because admission is controlled by admins.
- `[DECISION]` Tells are not treated as generally trusted identity for sensitive operations; sensitive tell commands are restricted to admins.
- Public/status-style commands should be visible in shared channels rather than silently controllable through arbitrary tells.

Known command policy recovered from development:

- Public/compatibility: `status`, `cloak` name retained where required for compatibility.
- Sensitive/admin-only operations: `wakeup`, `sleep`, `spinup`, `spindown`.
- Guest channel administration: invite/kick/join are admin-only; leave is available to all.
- `[DO-NOT-USE]` Legacy chat command `probe` was intended for removal; EXE-level flipper probe/toggle behavior is separate.

## Raid flow

Recovered intended `#raid` flow:

1. User invokes `#raid` in an allowed trusted channel.
2. AOPP window is used for selection.
3. Raid selection includes type, level (default 200), and count.
4. One-minute admin veto stage.
5. Separate one-minute CRU-fill stage for the raider.
6. AO requires CT fill >= 50%; a practical higher cutoff around 75% was discussed.

Timing anchors used for automation design:

1. Cloak-off event and `Your city has been targeted by hostile forces` are near-simultaneous absolute raid-start anchors.
2. Wave 8 arrival is a safe point to begin the player count relevant to the next wave/general timing.
3. General landing / spindown is the point at which the server has stopped counting helpers and helpers may be disconnected.

`[OPEN]` Desired behavior: on raid detection offer officers a spinup prompt; automatically spindown when the general enters the city.

## Org roster

`[DECISION]` Manager configuration should contain numeric `orgid` unless it can be read directly from game state.

`[DECISION]` Org roster may be fetched from `people.anarchy-online.com`, but not more often than once per 24 hours from the last successful fetch.

## Dependency / build state

Known dependency state from recovery:

- `AOSharp.Clientless` pinned to `1.0.16`.
- `AOSharpSDK` pinned exactly to `1.0.84`.
- Fresh-clone/runtime failures previously observed included:
  - `OutOfMemoryException` in `SmokeLounge.AOtomation ArraySerializer.Deserialize`.
  - `MissingMethodException` for `ChatHeader.get_Size()`, indicating AOSharp binary/version coupling.
- `[DECISION]` Do not solve reproducibility by introducing hard-coded developer-machine AOSharp references.
- `[VERIFIED-CODE]` AOSharp.Clientless 1.0.16 lazily reads
  `GameData\StaticDynelData.bin` with `File.Open(path, FileMode.Open)`, which
  defaults to an exclusive file share. Its cache is private to each client
  AppDomain, so concurrent first reads can fail even inside one Buddies host.
- `[DECISION]` Do not maintain an AOSharp fork or attempt to marshal its private,
  internal static-dynel dictionary between AppDomains for this issue. Warm each
  domain's cache under one named mutex before its network session starts.
- CityBuddies and Buddies now target x86 for CritterAI compatibility.
- `build/Restore-NavmeshDependencies.ps1` restores and SHA-256-verifies the
  three CritterAI DLLs and Grid `152.Navmesh` from pinned revision
  `474919d017759c39a530071a0c5b7e6eb162af7a` into ignored `.dependencies`.

## Navigation / home

### Deferred live navigation observations (2026-08-30)

These are owner-observed runtime results and proposed directions, not yet
implemented or independently verified:

- `[LIVE-OBSERVATION]` The default continuous clientless walker looks like
  rapid rubber-banding: the character keeps running forward but snaps backward
  several times per second, as if each command advances from a position echo
  roughly 100 ms out of date. The new walking method is not acceptable in its
  current form.
- `[LIVE-OBSERVATION]` Clientless movement can pass through ordinary building
  collision that stops a full client. Trigger volumes still take effect: a
  diagonal route crossed a teleporter, zoned unexpectedly, and the character
  died. Collision bypass therefore makes direct diagonals unsafe rather than
  obstacle-free.
- `[LIVE-OBSERVATION]` In Serenity, the current route went directly toward CT
  instead of first reaching the safe north/south main street. The relevant
  street coordinate appears to be near `X=994` (axis/value still requires a
  live coordinate confirmation). The proposed later strategy is to move
  east/west toward that street coordinate first, then travel north/south along
  the broad unobstructed corridor to CT.
- `[LIVE-OBSERVATION]` In Grid, navigation reached the intended city-exit point
  but did not zone into Serenity. The captured trigger coordinate or the
  crossing behavior therefore still needs correction.
- `[INVARIANT]` Do not combine these movement and routing investigations with
  the ICC terminal test. Address them one problem at a time after the focused
  static terminal/login checks.

### Walk/run movement packet observation (2026-09-01)

- `[LIVE-OBSERVATION]` Kavey's full-client dump shows that `SwitchToWalk` and
  `SwitchToRun` are ordinary `CharDCMoveMessage` actions carrying the sender's
  current heading and position. Each received message echoed exactly the sent
  heading and position while resetting `DeltaTime` to zero. The response is an
  acknowledgement/relay of the asserted position, not an independent
  server-measured position correction.
- `[DECISION]` Do not toggle walk/run several times per second as a position
  synchronization mechanism. With clientless supplying a stale or predicted
  position, each toggle would reassert and echo that same value and could add
  more visible hesitation.
- `[LIVE-OBSERVATION]` In the supplied short sample, forward running covered
  approximately `0.985 m` in `0.440 s` (`2.24 m/s`), walking covered
  approximately `1.457 m` in `1.218 s` (`1.20 m/s`), and backward running
  covered approximately `2.696 m` in `1.142 s` (`2.36 m/s`). These are
  trace-specific measurements, not universal AO movement constants.
- `[CORRECTION]` No measured displacement rate may be promoted to a fleet-wide
  constant. AO Run Speed varies by character skill, breed, profession, level,
  abilities, and temporary state. The home route normally runs after death,
  when resurrection recovery restores diminished skills over time. The fleet
  spans eight level brackets, all professions, and multiple breeds. Jump
  behavior has a separate Strength-related variable and must not be folded
  into the horizontal movement model.
- `[LIVE-OBSERVATION]` A longer timestamped full-client trace from another
  character measured settled running spans around `5.84-6.49 m/s` and walking
  spans around `1.40-1.51 m/s`. The first `192 ms` after one `ForwardStart`
  covered only about `0.24 m` (`1.25 m/s`), and other short run legs were below
  the settled spans. This is evidence of startup/transient behavior in
  addition to per-character variation; terrain also changed vertical position
  during the capture.
- `[LIVE-OBSERVATION]` The normal client did not publish positions every
  `200 ms`. During uninterrupted legs, repeated `Update` packets appeared at
  about `5001-5002 ms`, while actions or other client conditions caused earlier
  updates at intervals such as `1929`, `2739`, and `3333 ms`. Every received
  movement message again echoed the corresponding sent position; no
  unsolicited corrective position appeared in this capture. The Unix
  timestamps have one-second resolution, so they cannot establish precise
  round-trip latency.
- `[SUPERSEDED-INFERENCE]` The earlier specific comparison between a `2.3 m/s`
  server rate and the `1.6667 m/s` predictor was too narrow. The durable finding
  is that one fixed predictor is inherently wrong across the roster and during
  resurrection recovery. Reasserting synthetic positions every `200 ms`, far
  more often than the observed normal-client cadence, is a separate plausible
  contributor to rubber-banding. The trace does not yet prove either factor is
  the sole cause.
- `[PROTOCOL-EVIDENCE]` AOSharp's protocol definitions expose `Stat.RunSpeed`
  and `SimpleCharFullUpdateMessage.RunSpeedBase`. Normal-client AOSharp also
  has live vehicle `Runspeed`, `Accel`, and `Velocity` fields. It remains open
  whether AOSharp.Clientless 1.0.16 retains the effective Run Speed value and
  receives its incremental resurrection changes. Verify that before designing
  a stat-driven predictor.
- `[DECISION]` Do not calibrate general movement from one character. A later
  general walker must either use verified live per-character movement state
  and adapt during resurrection recovery, or avoid requiring precise velocity
  prediction. Its outbound update cadence should be evaluated against the
  sparse full-client trace rather than preserving the current `200 ms` loop by
  assumption.
- `[POSSIBLE-USE]` A single `SwitchToWalk` may still be valuable as a slower,
  more controllable mode for a complete movement leg. The observed walk rate
  is character/trace evidence only, and switching still does not synchronize
  position. This remains deferred until the Grid exit handoff is repeatable.

### Serenity Islands

- Playfield ID: `6010`.
- Home/CT target recovered from code: approximately `(996.004, 5.010, 1248.512)`.
- Home heading quaternion recovered from code: approximately `(0, -0.997, 0, 0.079)`.
- `[HISTORICAL]` The replaced manual route contained a T-junction near
  approximately `(892.50, 7.00, 1288.50)`.
- The old route was deliberately restrictive and could report route-unavailable for a character west/left of the T junction.
- A real test exposed exactly this failure mode; the character had to be manually rescued west of the T junction.
- A subsequent `#home 75` run reported 13/13 reached CT and 0 stopped.

`[VERIFIED]` Navmesh target selection is published in `6d03574`.
`[IMPLEMENTED]` Continuous route following is published in `23069f0`; the
navmesh supplies the route through the top of the old T and south to CT.
`[OPEN]` Kavey must build and live-test `#home`, comparing the continuous
default with the retained bounded-pulse fallback over several days.

### Grid

- Playfield ID: `152`.
- `[VERIFIED]` The user-provided `152.Navmesh` is byte-for-byte identical to AOSharp's public Grid navmesh.
- Size: 1,937,240 bytes.
- SHA-256: `da4f46630dcae195129b99340ea63ef0e96ca22a0565ec7fbc0ada54f345b961`.
- Git blob SHA: `1165314de5cf063580550a1ef3ae2599f62dd552`.
- `[DECISION]` Avoid duplicating this binary in City Dwellers; restore/fetch it from a pinned public AOSharp revision.
- `[LIVE-OBSERVATION]` An owner-supplied player-perspective protocol dump
  captured the complete Grid-to-Serenity leg for the local player:
  - Grid arrival/spawn: approximately `(234.3062, 3.775, 212.8138)`.
  - Last reported Grid position at the exit trigger: approximately
    `(211.6727, 3.775, 186.7213)`.
  - Serenity arrival: approximately `(1068.757, 5.010, 1416.942)`.
- `[VERIFIED]` The local outbound stream contained only movement packets for
  the Grid exit. A `ForwardStart` led to the last exit coordinate; no click,
  use, target, or action packet preceded `Changing area. Please wait.` The
  handoff is therefore a walk-into-volume zoning trigger.
- `[INVARIANT]` Protocol `PlayfieldId` values in this capture were transient
  instance identities. Navigation code must continue branching on stable
  `Playfield.ModelId` values (`152` for Grid and `6010` for Serenity), not the
  captured instance values.
- `[IMPLEMENTED]` CityBuddies loads the restored `152.Navmesh` and follows it
  from the current Grid position toward `(211.6727, 3.775, 186.7213)`.
- `[SUPERSEDED-INFERENCE]` Treating that last observed coordinate as a point at
  which to stop and wait was incorrect. The player capture showed movement to
  the coordinate, but did not prove that stopping there crosses the zoning
  volume.
- `[VERIFIED-LIVE]` The buddy reached the exit area, stopped, remained in Grid,
  and returned `route-unavailable` after 20 seconds:
  `Grid did not change to Serenity within 20s after crossing the observed exit.`
- `[HISTORICAL-CODE]` In the first failed implementation, at `<=0.25m`,
  `BeginGridCrossing` called `StopMovement`; while waiting,
  `ProcessGridCrossing` sent no further movement. The buddy reached the point
  but did not actually cross beyond it.
- `[SUPERSEDED]` Commit `d927cd59ca69b28800c237447c93b5607f34811a`
  replaced stop-at-edge with a dedicated 2 m/1.2 s pulse beyond the exit. The
  pulse stayed bounded but did not reproduce the successful client sequence.
- `[LIVE-OBSERVATION 2026-09-01]` The bounded crossing pulse still did not zone
  a buddy into Serenity. Repeated home jobs are non-idempotent: one run moves
  the buddy off the observed exit point, the next routes it back, and later
  runs alternate between those positions while continuing to fail.
- `[CORRECTED-EVIDENCE]` A closer reading of the successful full-client trace
  shows `ForwardStop` at exactly `(211.6727, 3.775, 186.7213)`, followed by
  small turn-stop messages and then the area change. It does not show a
  `FullStop` two metres beyond that point. The superseded crossing pulse moved
  beyond the only confirmed trigger coordinate and used a different stop action
  from the successful client sequence.
- `[VERIFIED-TRACE 2026-09-01]` The first complete per-job JSONL contained 354
  ordered events over 54.889 s. ICC entered Grid after two static-terminal
  uses. In Grid the continuous route sent 101 synthetic `Update` commands at
  approximately 204 ms/0.340 m intervals; every `Update` had a matching echo
  of the asserted position. The route then sent `FullStop` 0.192 m before the
  captured trigger, started again, and sent `FullStop` 1.808 m beyond it. It
  never sent the successful client's exact-position `ForwardStop`.
- `[IMPLEMENTED]` Commit `0f73756945df282ccf0601f9c8c80c40d1ead148`
  makes the Grid handoff deterministic. Every attempt first navmesh-routes to
  the fixed Grid-side staging point approximately
  `(212.9832, 3.7750, 188.2321)`, exactly 2 m before the trigger on the observed
  arrival-to-exit line. It asserts and receives the staging `FullStop` before
  one uninterrupted 1.2 s final leg, then sends `ForwardStop` exactly at
  `(211.6727, 3.775, 186.7213)` and waits 20 s for stable model `6010`.
- `[IMPLEMENTED]` Every new home directive resets the Grid exit phase. A retry
  beginning at the trigger or the old overshoot therefore routes back to
  staging and performs the whole attempt instead of continuing an old wait or
  alternating between endpoints.
- `[INVARIANT]` The final Grid leg is independent of the selected general
  walking mode, sends no synthetic `Update`, and is bounded by an exact
  `ForwardStop`. General walking remains unchanged pending separate work.
- `[VERIFIED-TRACE 2026-09-01]` The deterministic attempt behaved repeatably
  but still did not zone. Staging `FullStop`, final `ForwardStart`, and exact
  exit `ForwardStop` all received clean matching echoes; the buddy remained at
  `(211.6727, 3.7750, 186.7213)` for the full 20-second wait in model `152`.
- `[CORRECTED-EVIDENCE]` Exact `ForwardStop` was not the complete successful
  client tail. The full client first sent `TurnLeftMouse` at
  `(211.9757, 3.7750, 187.0108)`, then `ForwardStop` 35 ms later, followed at
  the exit by `TurnRightMouse`, `TurnLeftMouse`, and `TurnLeftStop` before the
  area changed.
- `[IMPLEMENTED]` Commit `1464d99a01e24e848e70806fcf2f6ee3ba977f3f`
  preserves deterministic staging and replays that missing bounded movement
  tail with the captured positions, headings, actions, and relative delays.
  The 20-second Serenity wait now begins after the final `TurnLeftStop`.
- `[OWNER-VERIFIED]` Do not add the normal client's approximately 15-second
  post-login teleport restriction to clientless. Kavey observed that it is
  enforced by the full client; the clientless buddy used `Enter The Grid`
  immediately after login.
- `[SECURITY]` The raw protocol dump is owner-supplied diagnostic material and
  remains outside the public repository; only these sanitized conclusions are
  durable project state.

### ICC HQ

- Playfield ID: `655`.
- Owner-supplied ICC position near the terminal: approximately
  `(3181.3, 35.9, 880.6)` in code `Vector3` order.
- `[VERIFIED-DATA]` The pinned clientless static-dynel data places terminal
  template `95350` approximately 2.6 m from that position, with identity
  `Terminal:C002028F`.
- `[LIVE-OBSERVATION]` An ICC buddy saw that named static terminal at 1.7 m,
  while its playfield packet exposed no live `Terminal` record. The former
  live-only resolver therefore waited 15 seconds and returned
  `route-unavailable` without attempting the terminal.
- `[VERIFIED-CODE]` AOSharp.Clientless models world objects such as this as
  `StaticDynel`; its built-in `StaticDynel.Use()` sends the stored static
  identity directly.
- `[SUPERSEDED BY abe19cb]` Packet live/static ICC terminal reconciliation was
  an unverified restriction and is removed.
- `[IMPLEMENTED]` CityBuddies still locates `Enter The Grid` by exact name with
  template `95350` as fallback and retains the 12 m guard. It now calls the
  standard static-dynel `Use()` up to three times at five-second intervals
  while waiting for stable model `152`.

### Serenity navmesh

- User-provided `6010.Navmesh` is unique in the recovery work and approved for publication in City Dwellers.
- Size: 2,087,208 bytes.
- SHA-256: `d3bbb491f8e5b575f269f73fee8443c977f371bc0173231105954b3a34eef27c`.
- Git blob SHA: `7dee622c49ab0778ad4398bc2bd9df4d91b70a5f`.
- `[VERIFIED]` The exact raw binary is published as
  `plugins/CityBuddies/NavMeshes/6010.Navmesh` in `6d03574`.

## CritterAI / navmesh dependency implementation

CritterAI is the native navigation/pathfinding layer used by AOSharp navmesh code. Required runtime DLLs:

- `cai-nav.dll`
- `cai-nav-rcn.dll`
- `cai-util.dll`

`[DECISION]` Do NOT vendor these DLLs into City Dwellers. Restore them from a pinned public AOSharp-related repository revision.

Pinned recovery revision used during reconstruction:

`474919d017759c39a530071a0c5b7e6eb162af7a`

The CritterAI references are x86, so the CityBuddies plugin and Buddies host need to run x86 for this integration.

Implemented pathfinder behavior:

- Deserialize raw `.Navmesh` with `BinaryFormatter` to `byte[]`.
- `Navmesh.Create(...)`.
- Build a `NavmeshQuery`.
- Find nearest start/destination points.
- `FindPath` then `GetStraightPath`.
- Convert results to AOSharp Common `Vector3` movement targets.
- Cache pathfinders by playfield.
- In `continuous` mode, keep forward movement active, slerp the heading toward
  successive straight-path points, and send conservative incremental
  clientless positions every 200 ms. Stop/replan on excessive command lead,
  lateral drift, or missing server-confirmed progress.
- In `bounded-pulse` mode, use the prior orient/start/stop/settle controller
  unchanged as a rollback path for both mapped navmeshes.
- In Grid, follow `152.Navmesh` to the observed city-exit trigger, stop, and
  wait for the stable playfield model to become Serenity.

### Reusable full-client navigation references

Owner-supplied `NavGen` and `NavManager` source archives were reviewed on
2026-09-01. The archives themselves remain outside the repository; only these
sanitized reusable ideas are durable project state.

- `[REFERENCE]` NavGen uses the full AO client plus AOSharp Recast to bake and
  save playfield navmeshes. It supports configurable agent dimensions and
  rasterization parameters, explicit off-mesh links, and visual inspection of
  both straight paths and path corridors.
- `[POSSIBLE-USE]` A repaired load-existing-mesh mode could validate Grid
  `152.Navmesh` and Serenity `6010.Navmesh` in the full client. A mesh that
  succeeds there but fails in clientless would narrow the CritterAI
  `AccessViolationException` investigation toward ABI, native lifetime, or
  query concurrency rather than mesh content.
- `[REFERENCE]` NavManager combines navmesh legs with direct waypoint legs for
  ramps and drops, followed by a separate interaction. CityBuddies should keep
  this hybrid model available: navmesh travel for broad safe movement,
  deterministic waypoint/staging legs for troublesome geometry, and explicit
  interaction or playfield-wait legs for transitions.
- `[DO-NOT-PORT-WHOLESALE]` Both tools depend on the normal AO client and newer
  AOSharpSDK versions (`1.0.100`/`1.0.105`) even though they reference
  `AOSharpSDK.Nav 1.0.5`. They provide no clientless movement implementation,
  no ICC/Serenity route data, and no fix for native CritterAI crashes. The
  supplied archives contain source but no navmesh binaries.
- `[KNOWN-DEFECT]` The supplied NavGen load helper uses a literal
  `name.Navmesh` path and its config loader reads a directory path as a file.
  Repair those defects before using it as a validator.

## Navigation forensic trace

- `[IMPLEMENTED]` Every new non-cancelled home job opens a character-specific
  JSONL trace in the CityBuddies runtime plugin directory under
  `NavigationTraces`. The filename includes UTC start time, character, and
  home-job ID; a new job never appends to an older job's trace.
- `[IMPLEMENTED]` Trace events distinguish outbound movement commands from
  received local `CharDCMove` echoes. Each event has a UTC timestamp and
  sequence number plus the available asserted position/heading, locally
  observed position/heading, packet delta time, stable playfield model, Run
  Speed stat, route state, pulse/continuous controller state, Grid crossing
  state, and ICC-use count.
- `[IMPLEMENTED]` One controller sample per second preserves position and
  state during quiet waits such as the 20-second Grid zoning window. Route
  construction, ICC terminal use, playfield changes, lifecycle boundaries,
  state/detail changes, and all movement actions receive explicit events.
- `[IMPLEMENTED]` JSON lines omit null fields and are buffered for at most one
  second. The trace is forced to disk at playfield changes, terminal home
  states, disconnect, and plugin teardown so the logger does not perform a
  disk write for every movement packet.
- `[IMPLEMENTED]` Buddy.exe prints the trace path when CityBuddies starts it
  and includes `NavigationTraces\\<filename>` in the terminal home result.
  Manager `#position` now reports Run Speed, trace filename/sequence, and the
  latest command and echo. It does not flood guest chat with every event.
- `[INVARIANT]` This is observation only. Movement modes, timings, paths,
  terminal interaction, and the known Grid crossing pulse are unchanged.
- `[SECURITY]` Runtime `NavigationTraces` directories are ignored by Git. Raw
  live traces remain owner-supplied diagnostics and are not published unless
  Kavey explicitly asks for a sanitized artifact.

## Publication constraints

- `[DO-NOT-USE]` `InfoHelper` / `InfoHelper.zip` must remain outside the public repository.
- `[DECISION]` Public/reproducible dependencies should preferably be fetched from pinned immutable Git revisions rather than copied into this repo when duplication is unnecessary.
- User-created Serenity navmesh may be committed.

## Recovery status from lost/too-long chats

Two chat-only local commit IDs were mentioned by an earlier session:

- `6585617` — logout quarantine fix.
- `91aeae6` — navmesh/homing work.

`[VERIFIED]` Neither object existed in the GitHub repository when checked during recovery; they were local to a previous session/worktree and therefore cannot be treated as published commits.

- `6585617` functionality has been reconstructed and published as `71f36fb`.
- `91aeae6` functionality was reconstructed and published cleanly as
  `6d03574`; the old local object remains absent and non-authoritative.

The first long conversation was titled `AOLite Config JSON Format`. A complete raw transcript was not accessible to the current session by title or conversation ID, so this file records only information that survived continuity/recovery and later verification.

## Current task / next work

`[IMPLEMENTED 2026-09-08]` The six legacy projects now emit into a single
repository-relative `release` or `debug` runtime root; no project writes
through a `bin` directory. At that stage, component administrator configuration
was executable-adjacent in three files; Session 22 supersedes that layout with
one `citydwellers.json`. All bot-owned
mutable state, caches, generated lists, process markers, diagnostics, and
navigation traces are rooted beneath `data`.

`[IMPLEMENTED 2026-09-08]` First start from the new repository-relative
runtime conservatively copies the old repository `settings` content and loose
mutable files from `bin\Release`/`bin\Debug`. Existing destinations win and
legacy files are never deleted, so rollback remains possible.

`[DECISION 2026-09-08]` The runtime root is portable and must not depend on a
Git checkout after deployment. It may be redirected to durable storage with a
Windows directory symbolic link. A future Windows service must not rely on an
interactive user's mapped drive; its service identity needs direct access to
the network location.

`[OPEN]` Replace the three manually launched hosts with one City Dwellers
executable that can run interactively or as a Windows service and explicitly
waits for usable network/time readiness before starting AO clients.

Earlier pending owner verification remains for the organization-output
resilience and restart change:

1. Start Manager normally and verify an org help or status reply uses the
   observed channel directly.
2. If AOSharp again reports that it cannot obtain LocalPlayer org stat, verify
   org replies still arrive. status and dump must describe the direct route or
   a degraded route truthfully.
3. If direct org delivery is unavailable, verify the command issuer receives a
   private fallback rather than silence.
4. Open a raid in org chat. If org output degrades, retry raid from tell or
   guest and confirm its interface migrates to that usable channel.
5. As an administrator, run restart. Confirm the acknowledgement arrives, the
   old Manager exits, and a replacement Manager reconnects after approximately
   two seconds.
6. Automatic ICC-to-city home movement is parked as a low-priority nice-to-have
   with a reliable manual administrator workaround. Preserve its existing code
   and traces; do not resume it without a new operational reason or evidence.

## Grid trigger traversal experiment (2026-09-02)

- `[VERIFIED-LIVE]` Apcr20000 trace
  `250ae67451cb445f93ade329f940a279-200` entered Grid model `152`
  from ICC on the first static-terminal attempt, reached the exact staging
  stop, sent and echoed every restored full-client tail action, and remained
  at `(211.6727, 3.7750, 186.7213)` for the complete 20-second wait.
  No Serenity model `6010` event occurred.
- `[CORRECTION]` The missing-tail-action hypothesis is disproven. Exact
  endpoint actions and headings alone do not make the clientless session
  traverse the Grid exit.
- `[IMPLEMENTED]` The isolated two-metre final approach now publishes five
  deterministic intermediate `Update` positions at 200 ms intervals between
  the confirmed staging point and the captured near-exit point. It then
  preserves the captured `TurnLeftMouse`, exact `ForwardStop`, and three
  post-stop turn actions unchanged.
- `[INVARIANT]` This experiment changes only the final Grid crossing leg.
  ICC entry, Grid staging/retry behavior, general continuous movement,
  bounded-pulse rollback, Serenity routing, and the 20-second zone timeout are
  unchanged.
- `[OPEN]` Kavey owns the build and one monitored ICC-to-Grid test. The next
  JSONL should contain five `Grid final traversal update` commands before the
  captured near-exit tail, followed by either model `6010` or the bounded
  timeout.


## Manager member interface and diagnostics (2026-09-06)

- [IMPLEMENTED] Manager uptime is measured with a monotonic Stopwatch and
  paired with a UTC start timestamp. status opens a colored operational blob
  containing Manager/AO state, live-diagnostic connection state, cloak status
  and five recent cloak events, raid recovery, Flipper and Buddies link detail,
  Buddy activity counts, raid state, alt-cache state, and membership freshness.
- [IMPLEMENTED] Help is a topic-based blob manual modeled after established
  AO bot conventions: overview, command list, syntax guide, member subjects,
  administrator-only subjects for administrators, and individual command
  explanations. Concrete commands are clickable; argument-bearing syntax is
  highlighted without sending placeholder text.
- [DECISION] Blob content is paginated at explicit target-aware limits:
  organization 5600 characters, guest 6500, and tell 7200. Page links identify
  their page number and preserve the originating reply route for command links.
- [IMPLEMENTED] Developer telemetry no longer flushes a buffered burst when
  the guest channel becomes active. New events remain concise, colored, and
  live. All diagnostics are timestamped to a rotating two-megabyte disk log;
  an administrator-only dump command writes a timestamped snapshot beneath
  diagnostic-dumps.
- [IMPLEMENTED] Detailed Buddy position telemetry is recorded for an explicit
  dump but no longer pushed into guest chat by the positions command.
- [OPEN] Kavey owns compilation and AO rendering/runtime verification. No
  assistant-side build, test suite, or live AO test was run.


## Manager build compatibility correction (2026-09-06)

- [VERIFIED-BUILD] Kavey's first Release build after the Manager presentation
  overhaul built five projects successfully. CityManager alone failed with one
  invalid char/StringComparison IndexOf overload and four unresolved Logger
  references in the new partial file.
- [IMPLEMENTED] The presentation partial now imports
  AOSharp.Clientless.Logging and uses the framework-compatible IndexOf(char)
  overload. No presentation or command behavior changed.
- [OPEN] Kavey owns the confirming Release rebuild and live AO rendering checks.


## Organization-output resilience and Manager restart (2026-09-06)

- [VERIFIED-LIVE] The captured failure did not stop inbound organization
  commands. Manager received later org commands after AOSharp began logging
  "Could not obtain LocalPlayer org stat." The failure was outbound org
  delivery.
- [ROOT-CAUSE] Two AOSharp packet-processing failures occurred before InPlay:
  an apparent allocation failure while deserializing an array and a duplicate
  dynel insertion. The session then lacked the LocalPlayer organization stat
  used by Client.SendOrgMessage. That method logged failure internally without
  throwing, so the old wrapper falsely claimed success.
- [IMPLEMENTED] Manager remembers the channel ID and name of received
  organization traffic. Org replies construct the same GroupMsgMessage packet
  as AOSharp.Clientless SendOrgMessage, but supply the observed channel ID
  directly and pass it to public Client.Send. This bypasses only the broken
  LocalPlayer stat-5 lookup.
- [IMPLEMENTED] If direct delivery is unavailable and ordinary SendOrgMessage
  cannot safely be used, Manager marks org output degraded and delivers the
  reply privately to the command issuer. status and dump expose the route,
  last observed channel, and last send attempt.
- [IMPLEMENTED] An active raid whose org origin is degraded migrates to its
  owner's current tell or guest target when retried there, rebuilding future
  buttons for that usable route.
- [IMPLEMENTED] Administrators have restart. It acknowledges the request,
  persists Manager state, starts a hidden PowerShell helper that launches the
  same Manager executable after two seconds in the same working directory,
  then exits the damaged process.
- [DECISION] restart is intentionally administrator-only. It restarts Manager
  and its AO session, not Flipper or Buddies.
- [OPEN] Kavey owns compilation and live AO verification. No assistant-side
  build or AO runtime test was run.


## Raw organization-channel correction (2026-09-06)

- [VERIFIED-LIVE] The first resilience build truthfully detected degradation
  and delivered status by tell, but AOSharp.Clientless.Chat.ChatClient exposes
  no public SendGroupMessage method. The reflection route therefore could not
  send to org.
- [VERIFIED-BINARY] Inspection of the pinned AOSharp.Clientless 1.0.16 NuGet
  assembly showed SendOrgMessage reads LocalPlayer stat 5, rejects a missing or
  zero result, then creates GroupMsgMessage with GroupMessageType.Org, the
  integer channel ID, and text before calling public Client.Send.
- [IMPLEMENTED] Manager now constructs that exact message using the channel ID
  observed on incoming organization traffic. Reflection and its unused import
  were removed. Private fallback and degraded-health reporting remain.
- [OPEN] Kavey owns the confirming build and org status test.


## Unified portable host and service (2026-09-08)

- `[IMPLEMENTED]` Release and Debug are self-contained runtime roots with no
  `bin` layer. The sole administrator settings file, `citydwellers.json`, lives
  beside the executable; bot-owned
  state, caches, coordination files, diagnostics, and logs live under `data`.
- `[IMPLEMENTED]` Before the banker import, the solution built four projects:
  three AO plugins and one `CityDwellers.exe`. Manager, Flipper, and Buddies are supervised
  in-process components; their separate console projects are gone.
- `[INVARIANT]` Flipper remains logged out while idle and Buddies starts zero
  AO helpers while idle. Manager is the only persistent AO client. A component
  failure stops the host so Windows Service Control Manager can apply its
  configured restart policy.
- `[IMPLEMENTED]` The executable runs interactively or as the delayed automatic
  `CityDwellers` Windows service. Install/uninstall commands declare TCP/IP and
  Workstation dependencies and configure restart-on-failure. Network-backed
  deployment requires a service identity with share permissions; interactive
  mapped drive letters are not a service contract.
- `[IMPLEMENTED]` Before AO starts, the host verifies `data` is writable and
  requires independent NTP confirmation that system UTC is within the allowed
  skew. Failed confirmation triggers bounded `w32tm` resync attempts and
  monotonic retries. The host stays alive and diagnostic while waiting and
  never silently bypasses the gate; bypass is an explicit admin setting.
- `[IMPLEMENTED]` Pre-trust logs use monotonic uptime. After confirmation they
  use offset-bearing timestamps. Combined output rotates under
  `data\citydwellers.log` for headless diagnosis.
- `[CORRECTION]` Administrator `restart` no longer uses PowerShell to start a
  second executable or exits the process. The coordinator gracefully recycles
  Manager only, leaving Flipper and Buddies online; global shutdown wins over
  a simultaneous restart request.
- `[OPEN]` Kavey owns the authoritative Release build and Windows 10 N checks:
  interactive startup, migration, service lifecycle, reboot with delayed
  network storage, NTP-unavailable waiting, clock repair, Manager-only restart,
  and live AO behavior. No assistant-side build or runtime test was run.


## Runtime inventory warnings (2026-09-08)

- `[IMPLEMENTED]` Unified-host startup inventories the executable/settings
  root and `data` after durable logging is available and before AO components
  start. Known entries on the wrong side receive a specific destination
  warning; unknown files and directories are reported as unused alien entries.
- `[INVARIANT]` The validator is diagnostic only. It never reads competing
  copies, chooses a winner, deletes, or moves an entry. Runtime behavior keeps
  using only the documented location.
- `[IMPLEMENTED]` Runtime binaries, symbols, configuration artifacts,
  `GameData`, and `NavMeshes` are distinguished from the administrator
  JSON settings. Known mutable files and temporary/invalid preservation
  patterns are distinguished from alien data. `NavigationTraces` and
  `diagnostic-dumps` validate their generated child filename shapes.
- `[OPEN]` Kavey owns build confirmation and startup-log verification against
  the deliberately mixed deployment directories.


## Cross-AppDomain runtime-root correction (2026-09-08)

- `[VERIFIED-LIVE]` The unified host resolved `release\data`, but AOSharp child
  AppDomains resolved the legacy repository `settings` directory. CityFlipper
  completed its observation there while the loader waited in `release\data`,
  causing repeated result timeouts.
- `[IMPLEMENTED]` `CityDwellers.exe` now binds its own base directory into
  process-scoped runtime state before any AOSharp component starts. Every
  linked `SettingsPaths` copy reads that contract before considering its local
  AppDomain base directory.
- `[INVARIANT]` The fallback remains available for a plugin hosted outside the
  unified executable. Flipper timeouts and the fixed 30-second cloak-recovery
  cadence are unchanged.
- `[VERIFIED-LIVE 2026-09-09]` A timed-out Flipper probe could unload its local
  AppDomain while AO still retained Apcflipper's session. The next Manager
  retry received `AlreadyLoggedIn`. During host shutdown, an already accepted
  request could also finish domain setup and call `Start` after the Flipper
  service announced it was stopping.
- `[IMPLEMENTED]` Flipper rejects new IPC work once shutdown begins, active
  result waits observe shutdown and unload, domain startup rechecks shutdown
  after plugin loading, and service teardown waits up to 15 seconds for the
  active probe to finish unloading. An unsuccessful started probe imposes a
  90-second `Stopwatch`-based login cooldown; intervening Manager retries fail
  immediately without creating another AO session. The Manager's 30-second
  recovery cadence itself is unchanged.
- `[OPEN]` Kavey owns the confirming Release build. Manager initialization and
  Flipper result production/consumption must all report the same `release` and
  `release\data` roots.


## Implied unified-runtime plugins (2026-09-08)

- `[DECISION]` Plugin DLL paths are no longer administrator settings. Manager,
  Flipper, Buddies, and Bankers each load their one fixed DLL from the unified
  runtime root: `CityManager.dll`, `CityFlipper.dll`, `CityBuddies.dll`,
  and `CityBankers.dll`.
- `[IMPLEMENTED]` Plugin paths do not exist in administrator settings. Each
  component loads its fixed sibling DLL from the unified runtime root.
- `[INVARIANT]` An absolute or parent-relative legacy plugin path cannot divert
  a component into the old `settings` directory. A missing implied DLL is a
  clear startup configuration failure.
- `[OPEN]` Kavey owns rebuild and live confirmation that all four plugins load
  from beside `CityDwellers.exe`.


## Single administrator settings file (2026-09-08)

- `[IMPLEMENTED]` `citydwellers.json` is the sole administrator settings file.
  Its top level holds trusted-time settings and required `Manager`, `Flipper`,
  and `Buddies` objects.
- `[IMPLEMENTED]` Manager, Flipper, Buddies, and CityManager's alt-bot lookup
  all read their settings from their named section of the same file.
- `[IMPLEMENTED]` The required `Bankers` section now preserves the six-role
  CityBankers account schema in that same file.
- `[IMPLEMENTED]` First startup creates one complete template and exits so no
  component can create or select a competing settings file.
- `[IMPLEMENTED]` `manager.json`, `flipper.json`, and `buddies.json` are ignored
  and explicitly reported as obsolete by the runtime inventory.
- `[PRIVATE HANDOFF]` Kavey's supplied legacy component settings are merged
  into a separate untracked `citydwellers.json`; credentials are never stored
  in Git.


## Integrated CityBankers runtime (2026-09-08)

- `[IMPLEMENTED]` CityBankers `main` at
  `eadf5a3dce028ba83f3930ce83f96b2e41f91137` is the imported behavior baseline.
  Only compiled application/runtime sources were imported; sibling recovery
  and historical records remain separate.
- `[IMPLEMENTED]` The solution now builds one executable and four implied AO
  plugin DLLs. `CityDwellers.exe` supervises Flipper, Buddies, Bankers, and
  Manager. Bankers preserves Central-first startup and six client domains.
- `[IMPLEMENTED]` `Banker.exe` is retired. Physical audit remains available as
  `CityDwellers.exe bankers-bagaudit`, behind the same trusted-time gate.
- `[IMPLEMENTED]` Banker settings come only from the required `Bankers` object
  in `citydwellers.json`; no `banker.json` is created or read.
- `[IMPLEMENTED]` Banker storage/stock/queue state, active ledger/index, event
  ledger, history, logs, diagnostics, recovery handoffs, and baseline archives
  all resolve beneath the unified `data` directory.
- `[INVARIANT]` CityBankers physical AO inventory remains authoritative. Existing
  state must be copied to the unified data layout before live startup when the
  banker characters already hold stock; never initialize over real custody as
  if it were an empty bank.
- `[VERIFIED-STATIC]` Every compiled Banker state, readiness, diagnostic,
  audit, queue, ledger, and tell-queue path resolves beneath the unified
  executable-adjacent `data` directory. No imported runtime reader consumes
  mutable state from the old settings root.
- `[IMPLEMENTED]` Runtime inventory recognizes the Bankers-generated
  `physical-states` archives and `.log` files in `logs`; it continues to warn
  about obsolete repair-normalizer attempt directories that current code no
  longer produces or reads.
- `[DEFERRED]` City Dwellers admins, members, and alts are not yet shared with
  CityBankers. Kavem remains the imported bootstrap administrator until the
  owner defines the next integration layer.
- `[VERIFIED-LIVE 2026-09-08]` The unified executable started Manager, the
  idle Flipper/Buddies services, and all six Bankers. The migrated storage
  baseline reconciled with zero bag remaps, all six readiness barriers opened,
  and two donations routed 17 symbiants into nine worker batches. The shared
  tell queue rotated coherent trade messages across Apcmanager and the bankers.
- `[VERIFIED-LIVE 2026-09-09]` A concurrent `current-stock.json` read exposed a
  Windows sharing violation after one Kbexte AO bag placement. The worker
  correctly stopped the remaining batch and Central held it as post-transfer
  rather than requeueing it.
- `[IMPLEMENTED]` Banker JSON reads and atomic writes now share a per-file
  named mutex. Atomic replacement retries transient sharing violations for up
  to five seconds and never deletes the last good target after replacement
  fails.
- `[SUPERSEDED 2026-09-09]` The first partial-placement recovery required a
  distinct usable AO identity on every queued occurrence. Live batch
  `07c904d1` proved imported trade snapshots may not satisfy that condition;
  it blocked safely without moving anything.
- `[IMPLEMENTED]` Partial-placement recovery now selects destination-worker
  persisted occurrences by the original batch transaction id, subtracts them
  multiplicity-aware from the expected batch, then proves the complete
  remainder loose on that worker. Usable identities remain authoritative;
  identity-less occurrences match only by AO id, high id, and QL. Recovery
  moves only the loose remainder and reports success for the complete original
  batch; any mismatch remains held.
- `[VERIFIED-LIVE 2026-09-09]` Failed extermination batch `07c904d1` recovered
  from transaction-bound evidence as one already-persisted item and no loose
  remainder. The worker published full-batch success and Central removed the
  held queue entry without a second trade or physical move.
- `[IMPLEMENTED 2026-09-09]` Partial-placement recovery is idempotent across
  the short interval before Central removes a successful failed-queue entry.
  An exact same-batch success result suppresses another worker recovery; a
  same-batch success with mismatched role, character, or counts blocks for
  reconciliation instead of being trusted.


## Unified public command gateway (2026-09-09)

- `[DECISION]` Apcmanager is the integrated system's sole public command
  identity in organization and guest chat. Banker information does not require
  a second prefix or a second public bot vocabulary.
- `[IMPLEMENTED]` Manager owns the `#stock`, `#symb`, `#spirit`, `#dyna`, and
  `#phatz` stock families and the `#donor` family. It reads the same
  mutex-protected `current-stock.json`,
  `ledger.json`, `symbiant-index.json`, and monthly departure history that
  CityBankers maintains; no second stock, donor, admin, member, or alt database
  exists.
- `[INVARIANT]` Stock and donor information is AP-members-only. Organization
  channel origin supplies membership context; guest and tell requests must
  resolve through Manager's canonical admin/member/alt identity model.
- `[IMPLEMENTED]` The existing StockCommandEngine source is compiled into
  CityManager instead of CityBankers. Every generated stock navigation link
  targets the configured Manager character and includes the Manager `#`
  prefix. Old direct `stock`, `don`, or `donor` tells to Kbcentral receive a
  redirect to Manager; Kbcentral retains physical trades and private operator
  duties, not a second public information processor.
- `[IMPLEMENTED]` CityBankers notices formerly addressed as Kavem tells enter
  a durable, sequence-ordered Manager-channel queue. Apcmanager drains that
  queue into its confirmed guest channel and retains a message when channel
  delivery throws. Other player tells continue through the rotating tell
  sender pool.
- `[IMPLEMENTED 2026-09-10]` Active donation-partner UX always uses the direct
  rotating tell queue, even when the donor is the bootstrap administrator.
  Internal Central/storage notices addressed to that administrator retain the
  Manager-channel guest route. Each added item now produces one compact line:
  cyan trade position, green store or red delete/reject disposition, clickable
  item with cyan QL/copy count, and yellow destination banker.
- `[IMPLEMENTED 2026-09-09]` `#donor top` ranks canonical mains by all-time
  donated item count; `#donor last` shows the latest 25 donated items with
  absolute UTC receipt time and donor; `#donor <member>` shows that canonical
  main's latest 10 and all-time total. Active ledger items and archived
  departure items are combined and deduplicated by stable ledger item ID, so
  items remain credited after withdrawal or deletion. Donor alts are resolved
  through Manager's live canonical alt map at query time. Responses are
  paginated AO blobs with item links and clickable donor navigation.
- `[IMPLEMENTED 2026-09-09]` AP members may request one available copy by
  Anarchy Online item ID with `#get <AOID>` or its `#withdraw` alias.
  Apcmanager reserves one exact audited ledger/location row and hides that copy
  from stock while its durable `withdrawal.json` transaction is active. The
  source worker extracts the exact bag/inner-slot item and returns it to
  Kbcentral. Only then does the three-minute pickup window start; the requesting
  character or a currently known alt in the same canonical group may collect.
- `[INVARIANT]` AO `TradeStatus.Finished` for the member pickup is the only
  authority that archives the exact active-ledger ID as `withdrawn`, with the
  canonical recipient and UTC departure time. Declines leave the reservation
  open. Expiry queues the physical item back through the normal serialized
  dispatch/storage path and leaves the active ledger intact. An ambiguous or
  failed physical transition enters a persistent failed hold rather than
  guessing or permitting another withdrawal.
- `[IMPLEMENTED 2026-09-09]` Item-level stock results and active donor-history
  rows include a `GET` chat command targeting Apcmanager. Historical items that
  have already left custody do not show a pickup action.
- `[IMPLEMENTED 2026-09-09]` A Central restart no longer strands a durable
  dispatch row forever in `trading`. Post-login reconciliation treats such a
  row as a restart orphan only when its persisted `UpdatedUtc` predates the
  current reconciliation-agent start. It then requires the complete expected
  multiset loose on Central and no same-batch worker custody/storage evidence
  before clearing stale sidecars and returning the batch to `queued`. Trades
  created by the current process are never eligible for this recovery.

## Family stock commands and live Phatz policy (2026-09-10)

- `[IMPLEMENTED]` Bare `#stock` is the complete overview: Symbiants, Spirits,
  Dyna Nanos, Phatz, and Central-held specials, including zero-count modules.
  Symbiant searching moved to `#symb`/`#symbs`; Spirit uses the same slot/QL
  navigation; Dyna (`#nano`/`#nanos`) and Phatz (`#phat`) search by partial
  item name or browse by QL.
- `[IMPLEMENTED]` Administrators can add an exact linked AO item with
  `#phatz add <item-link> <-1|positive-limit>`, remove it by AOID, and open the
  effective list with `#phatz list` or `#phatz print`. The list supplies an
  administrator-only remove button for each row.
- `[INVARIANT]` Runtime Phatz changes persist in
  `data/citybankers-phatz-policy.json`, never rewrite credential-bearing
  `citydwellers.json`, and overlay configured bootstrap entries on every
  acceptance lookup. Removal tombstones can therefore suppress a configured
  Phatz default without deleting private configuration.
- `[IMPLEMENTED]` Public `#status` shows every configured banker as
  `used/total slots (percentage)` with free slots. Occupancy is green below
  75%, orange from 75% through 89.9%, and red from 90%, while process, bank,
  readiness, queue, reservation, and retention warnings remain visible.
- `[OPEN]` Kavey owns the Release build and live validation of AO item-link
  parsing, cross-AppDomain policy refresh, stock navigation links, and the
  displayed storage census.

## City office building bank terminal (2026-09-10)

- `[VERIFIED-LIVE]` In playfield model `6152312`, the game UI identifies
  `Rubi-Ka Banking Service Terminal` instance `1478048485` at
  `(185, 6.02, 173)`, while the clientless AOSharp dynel collection reports no
  static dynels. The location is a separate city office building, not HQ and
  not Apcmanager's location.
- `[IMPLEMENTED]` Banker startup still prefers ordinary nearby named
  `StaticDynel` discovery. If none exists, it sends the repository's proven
  `GenericCmdAction.Use` packet to the exact terminal identity only when the
  playfield matches and the local character is within eight metres of the
  supplied terminal position.
- `[INVARIANT]` The fallback is one-shot and read-only, retains the existing
  eight-second `Inventory.Bank.IsOpen` confirmation timeout, and cannot fire
  in another playfield or away from the verified terminal coordinates.
- `[VERIFIED-LIVE]` All nine banker clients opened the office-building bank
  through the guarded terminal identity fallback.

## Live banker inventory and withdrawal extraction recovery (2026-09-10)

- `[VERIFIED-LIVE]` Withdrawal `wd-4c6469303ffd48049e8c6da81584b15b`
  removed the reserved item from its audited Kbexte bag slot, but the existing
  matcher did not recognize it in normal inventory. The owner verified the
  item was physically present there. The immediate retry then failed against
  the now-empty old slot.
- `[IMPLEMENTED]` Every banker heartbeat contains its current normal-inventory
  slots, identities, item template IDs, QL, names, and container flag.
  Administrators can use `#inventory`/`#inv` for the nine-banker summary and
  `#inventory <role|character>` for a live slot-by-slot AO-link window.
- `[IMPLEMENTED]` Before bag extraction, the worker persists its occupied
  inventory slots. Post-move recognition still prefers exact identity and
  template matching, then permits only one exact-name item in a newly
  occupied slot. The recognized live identity is retained for subsequent
  trade phases.
- `[INVARIANT]` A failed withdrawal may resume from worker inventory only when
  its fresh heartbeat contains exactly one exact-name loose item. That recovery
  persists the proven live slot and identity for the worker, has its own single
  anchored attempt, bypasses the emptied audited source slot, and continues to
  fail closed on ambiguity.
- `[OPEN]` Kavey owns the Release build and live validation that the existing
  Kbexte-held item resumes to Central, and that inventory windows display all
  nine workers without stopping the host.
- `[VERIFIED-LIVE]` The recovered Feet and an earlier Right Wrist completed
  their physical return to Kbexte and reappeared in `current-stock.json` under
  their exact original donation transaction IDs. Their withdrawal rows stayed
  `return-queued`, however, leaving one order and two reservations active and
  hiding the otherwise-restored items from stock output.
- `[IMPLEMENTED]` Return finalization now accepts the durable combination of
  an absent return batch and an exact canonical stock row matching AOID,
  original donation transaction, and source worker. A still-queued batch,
  missing stock row, or matching same-batch failure result remains a hard stop.
  The transient worker result is no longer required after the normal dispatch
  consumer has legitimately consumed and deleted it.

## Automatic startup storage enrollment (2026-09-10)

- `[VERIFIED-LIVE]` The office-building identity fallback opened all nine
  banks. Five established storage workers reconciled normally; Spirit, Dyna,
  and Phatz each exposed 102 bank bags plus 18 inventory bags but could not
  become ready because operational state had no worker entry for them.
- `[IMPLEMENTED]` Normal startup now detects a missing/empty configured worker
  map or a current-run live-layout rejection. After all nine banks have fresh
  successful diagnostics and a ten-second settle period, Central audits one
  affected worker at a time. No separate `bankers-bagaudit` invocation is
  required for this repair path.
- `[INVARIANT]` Each bank bag is staged, opened, read, and verified returned
  before the next bag. A failed, incomplete, mismatched, or timed-out audit
  halts enrollment for the run and keeps readiness closed.
- `[INVARIANT]` A validated audit replaces only the affected worker under the
  live-layout and runtime-state locks. Other workers, ledger history, and known
  item transaction provenance remain intact. Existing workers automatically
  repeat live reconciliation when the enrollment baseline identifier changes.
- `[IMPLEMENTED]` An active-enrollment marker prevents readiness from opening
  during repair. Completed and rejected audit evidence is archived beneath
  `data/storage-enrollments`; manual full `bankers-bagaudit` remains available.
- `[OPEN]` Kavey owns the Release build and live validation of sequential
  Spirit, Dyna, and Phatz enrollment, final eight-worker readiness, occupancy,
  and stock publication.

## Banker startup UTC marker parsing (2026-09-10)

- `[VERIFIED-LIVE]` On a `+03:00` host, all eight workers wrote fresh
  write-front-ready markers between `19:59:04Z` and `19:59:08Z`, after
  Central's `19:58:47Z` start, but Central continued reporting all eight as
  missing. The preserved markers had the expected roles, characters,
  baseline identifier, and positive capacity.
- `[ROOT-CAUSE]` Newtonsoft materializes ISO JSON timestamps as Date-valued
  `JToken`s. Converting such a token to text before parsing can emit localized
  offset-less text; the subsequent `ToUniversalTime()` then applies the host
  offset again and makes a current marker appear three hours stale.
- `[IMPLEMENTED]` Banker startup freshness readers now extract `DateTime`
  directly from the token and apply the shared `UtcTimestamp` contract. The
  fix covers bank diagnostics, repair requests, live layouts, write fronts,
  the all-bankers barrier, and its watchdog without weakening any readiness
  requirement.
- `[OPEN]` Kavey owns the Release build and live `+03:00` startup check. The
  expected result is one all-bankers-ready message after the last worker
  marker, with no repeating all-eight-workers warning.

## Central outbound tell queue (2026-09-08)

- `[IMPLEMENTED]` Manager and CityBankers no longer send application tells
  directly. Producers append ordered jobs beneath `data/tell-queue`; the first
  configured Manager account is the sole scheduler.
- `[IMPLEMENTED]` Apcmanager and all six configured bankers publish live
  sender heartbeats. The scheduler admits only configured, fresh, in-play,
  idle clients and rotates across them with a 1.2-second per-character pace.
- `[INVARIANT]` Only one tell is assigned at a time. The next ordered job is
  not released until the sender acknowledges the AO send call. A failed call
  is retried up to five times; final failures and acknowledgements remain in
  the queue data tree for diagnosis.
- `[INVARIANT]` Banker clients advertise busy while an AO trade is open and do
  not consume tell work until idle. If a banker becomes busy after assignment,
  it atomically returns that same ordered job without charging a failed attempt
  so another idle sender can take it. A genuinely abandoned assignment returns
  to the pending queue after 45 seconds.
- `[INVARIANT]` Request/reply traffic whose answer returns to the sending toon,
  currently the external alt-bot lookup, is pinned to Apcmanager. Ordinary
  notices and replies may be sent by any available queue worker.
- `[IMPLEMENTED]` Durable monotonic sequence numbers preserve enqueue order
  independently of wall-clock corrections. Runtime inventory recognizes only
  the queue's fixed directories, JSON records, and sequence file.
- `[DEFERRED]` Flipper and Buddies do not own persistent AO sessions and are
  not queue senders. They can be registered later if that lifecycle changes.
- `[OPEN]` Kavey owns the authoritative Release build and live validation of
  AO tell pacing, sender rotation, trade-busy exclusion, restart recovery, and
  alt-bot reply ownership.

## Central inventory status census (2026-09-10)

- `[VERIFIED-LIVE]` `#inventory central` listed three current loose items, but
  the Banker status window said Central's inventory census was unavailable.
- `[ROOT-CAUSE]` Status used canonical storage-bag capacity for every role.
  Central deliberately has no storage-worker bag map and instead publishes its
  ordinary inventory items and free-slot count in the same live heartbeat used
  by the inventory command.
- `[IMPLEMENTED]` Central's status line now derives used, total, percentage,
  and free ordinary inventory slots from that heartbeat. The eight storage
  workers retain canonical bag-map occupancy, and Central is still excluded
  from the aggregate storage total.

## Incremental internal trade staging (2026-09-10)

- `[VERIFIED-LIVE]` A three-item Phatz dispatch completed, while a simultaneous
  six-item Spirit dispatch timed out three times. Kbspirit's preserved result
  reports `StoredCount=0` and `Timed out waiting for expected Central trade
  contents`; all six Spirits remained safely on Central.
- `[ROOT-CAUSE]` Central requested every batch item in one update and waited
  for the complete local trade cache. The worker required a complete remote
  cache, while the compatibility fallback ignored non-empty partial caches.
  Incrementally published AO caches could therefore strand both sides until
  timeout on the larger batch.
- `[IMPLEMENTED]` Central now adds one outgoing item occurrence and waits for
  its local offered-count acknowledgement before requesting the next. The
  command-bound worker fallback accepts an incomplete as well as empty remote
  cache only after Central has accepted its exact persisted multiset.
- `[INVARIANT]` AO Finished and worker-side normal-inventory/bag placement
  verification remain authoritative. Partial caches alone cannot start or
  complete storage.
- `[VERIFIED-LIVE]` The repaired six-item Spirit retry completed and stored
  all six. A following ten-item Spirit donation then reproduced the timeout
  with `ExpectedCount=10`, `StoredCount=0`, proving the remaining boundary was
  batch size rather than incremental staging.
- `[IMPLEMENTED]` Player donations may still contain ten items, but routed
  Central-to-worker work is deterministically chunked into batches of at most
  six per destination. A failed oversized pre-transfer batch is split only
  after Central verifies its entire expected multiset has returned; the
  original batch retains its ID for the first chunk and later chunks receive
  new IDs under the same donation transaction.

## Internal storage success-message policy (2026-09-10)

- `[VERIFIED-LIVE]` Successful six-plus-four Spirit recovery produced a
  Manager-channel delivery line for every routine internal trade and placement
  confirmation, obscuring meaningful operator events.
- `[DECISION]` Normal internal trade opening, exact transfer, worker receipt,
  compatibility fallback acceptance, per-item placement, and successful batch
  completion remain durable in activity/ledger state but do not enqueue tells
  to the Manager channel.
- `[INVARIANT]` Failures, exceptional recovery/reconciliation notices,
  explicit command replies, and donor-facing trade messages remain visible.

## Full storage audit expected-state comparison (2026-09-11)

- `[IMPLEMENTED]` `CityDwellers.exe bankers-bagaudit` snapshots the existing
  `storage-state.json` before any banker starts, then compares every audited
  worker, bag, slot, and item against that snapshot.
- `[IMPLEMENTED]` The console and durable combined dump report every missing,
  unexpected, or unreadable bag/item; bag location and identity changes; item
  identity/name changes; capacity changes; and per-template count deltas.
  Incomplete comparisons are explicitly labelled and never presented as proof
  of absence.
- `[INVARIANT]` The comparison has no item-specific special cases. It applies
  to the complete persisted storage model and complete live audit result.
- `[INVARIANT]` Normal layout repair, write-front reconciliation, storage,
  enrollment, failed-batch recovery, and baseline seeding are disabled in
  bagaudit mode. Audit evidence cannot replace operational storage state.
- `[DO-NOT-USE]` Session 65's custody partition/recovery remains
  validation-blocked. Do not run normal service recovery from that revision
  until the physical audit evidence is reviewed.
- `[OPEN]` Kavey owns the Release build and full live bagaudit. Return the
  generated `diagnostic-dumps/citybankers-bagaudit-*.log` for interpretation.
