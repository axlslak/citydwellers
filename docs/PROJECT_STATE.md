# City Dwellers — Persistent Project State

Last continuity reconstruction: 2026-08-30

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

## Main components

### Manager / APCManager

Coordinates chat commands, raid lifecycle, cloak operations, helpers, admin/member policy, and Buddies operations.

### Flipper

Controls city cloak state. Boot behavior should be enable-only recovery/assessment: if cloak is already enabled, confirm it; if it can safely be enabled, recover it. Avoid exposing sensitive toggle behavior to arbitrary tells.

### Buddies

Clientless helper/account host. Important current behavior:

- configured home levels: 25, 50, 75, 100, 125, 150, 175, 200.
- `#home <level>` performs home verification/maintenance for all configured characters at that level.
- `[INVARIANT]` Home maintenance owns its navigation time independently of demo leases.
- logout quarantine is 35 seconds of monotonic elapsed time.
- home navigation timeout is 600 seconds.

### CityBuddies plugin

Handles AO movement/home behavior for a Buddies character. Serenity target
selection now recalculates a straight navmesh path after each confirmed bounded
movement pulse. The navmesh query is cached by playfield; pulse duration,
emergency-stop, divergence, stuck detection, and settled server-position
confirmation remain in control of movement.

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
- CityBuddies and Buddies now target x86 for CritterAI compatibility.
- `build/Restore-NavmeshDependencies.ps1` restores and SHA-256-verifies the
  three CritterAI DLLs and Grid `152.Navmesh` from pinned revision
  `474919d017759c39a530071a0c5b7e6eb162af7a` into ignored `.dependencies`.

## Navigation / home

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
`[OPEN]` Kavey must build and live-test `#home`, especially from west of the
old T-junction failure point.

### Grid

- Playfield ID: `152`.
- `[VERIFIED]` The user-provided `152.Navmesh` is byte-for-byte identical to AOSharp's public Grid navmesh.
- Size: 1,937,240 bytes.
- SHA-256: `da4f46630dcae195129b99340ea63ef0e96ca22a0565ec7fbc0ada54f345b961`.
- Git blob SHA: `1165314de5cf063580550a1ef3ae2599f62dd552`.
- `[DECISION]` Avoid duplicating this binary in City Dwellers; restore/fetch it from a pinned public AOSharp revision.
- `[OPEN]` Grid-to-Serenity exit/handoff is NOT mapped. Do not invent a coordinate or transition.

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
- In Serenity, choose the first path point far enough from current position and continue using the existing bounded movement pulse safety logic.
- In Grid, return an explicit route-unavailable reason until the real exit
  handoff is mapped.

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

Navmesh-based Serenity homing is published in `6d03574`. Next:

1. Kavey builds the solution and returns any compile/dependency-restore output.
2. Kavey live-tests `#home 75`, including a character west of the old Serenity
   T-junction failure point if practical.
3. Record the build and live-test result without changing the explicit
   route-unavailable Grid behavior.
