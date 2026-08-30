# City Dwellers — Persistent Project History

This is a compact chronological engineering log. It records decisions and verified outcomes that future sessions may need in order to understand why the current code looks the way it does.

## 2026-08 — early clientless / AOLite work

`[HISTORICAL]` Development included AOLite/clientless `PluginLoader` and `config.json` work, with multiple accounts and plugin DLL paths. The project then expanded into City Dwellers/APCManager orchestration rather than remaining a simple loader exercise.

`[DECISION]` Reproducible builds are preferred over developer-machine-specific references. Hard-coded paths such as `C:\ao#` should not become project requirements.

`[HISTORICAL]` A clean-clone/runtime test exposed AOSharp binary coupling problems, including:

- `OutOfMemoryException` in `SmokeLounge.AOtomation ArraySerializer.Deserialize`.
- `MissingMethodException: ChatHeader.get_Size()`.

The repository later pinned `AOSharp.Clientless 1.0.16` and `AOSharpSDK 1.0.84` exactly to keep clientless/plugin APIs aligned.

## 2026-08 — manager / raid architecture

`[DECISION]` Sensitive operations are admin-only and should be usable through trusted org/guest channels. Arbitrary tells are not a general trust boundary.

`[DECISION]` Admin/member state should persist rather than being rebuilt manually every process start.

`[DECISION]` The `#raid` flow uses a UI/select stage, separate one-minute veto and CRU-fill stages, then lifecycle automation around actual AO raid anchors.

Important raid anchors identified during live observation:

- cloak off / city-targeted event,
- wave 8 arrival for count timing,
- general landing as spindown/disconnect point.

## 2026-08 — Buddies home maintenance

`[INVARIANT]` `#home` maintenance owns navigation time independently of demo leases. It is not merely a short borrowed movement lease.

A manual Serenity route was built around a corridor/T-junction model. It worked for expected positions but deliberately refused some positions outside the safe corridor.

`[VERIFIED]` A live test eventually exposed the weak spot: one level-75 character was found west/left of the T junction instead of where the route logic expected. The character was manually rescued. A rerun of `#home 75` then completed 13/13 reached CT, 0 stopped.

This failure motivated replacing hand-authored corridor selection with real navmesh pathfinding.

## 2026-08-29 — lost local commits discovered

During recovery from a conversation that had become too long, two previously reported local commit IDs were checked against GitHub:

- `6585617`
- `91aeae6`

`[VERIFIED]` Neither commit object existed in the GitHub repository. They were therefore treated as chat-only/local work, not published history.

### Logout quarantine reconstruction

`[CHAT-ONLY]` `6585617` had been described as fixing a 35-second logout cooldown that could incorrectly become multiple hours.

The functionality was reconstructed from the known intended change and current source.

`[VERIFIED]` Published commit:

`71f36fbf9e016593ae102a78185a644ad5f04ffa` — `Fix buddy logout quarantine with monotonic timer`

The fix uses `Stopwatch.GetTimestamp()` for elapsed logout quarantine deadlines instead of wall-clock UTC arithmetic. Only the logout linger timing changed; ordinary lifecycle/lease timestamps remain UTC.

### Navmesh reconstruction

`[CHAT-ONLY]` `91aeae6` had been described as adding Grid/Serenity navmeshes and navmesh-based homing.

Recovery established:

- PF `6010` is Serenity Islands.
- PF `152` is Grid.
- uploaded `152.Navmesh` exactly matches AOSharp's public Grid navmesh.
- uploaded `6010.Navmesh` is the City Dwellers Serenity asset intended for publication.
- CritterAI runtime DLLs are redistributable/public but should be restored from a pinned upstream source instead of vendored here.
- the native CritterAI dependency is x86.
- the actual Grid-to-Serenity exit/handoff coordinate was not recovered and must not be guessed.

`[OPEN]` The reconstructed Serenity navmesh code and exact `6010.Navmesh` still need to be published as a clean commit.

## 2026-08-29 — continuity system introduced

The project had now crossed multiple long ChatGPT development sessions, with important context being expensive to reconstruct after conversation failures/limits.

`[DECISION]` Git becomes the durable memory layer for future AI/coding sessions.

Added:

- `AGENTS.md` — restart/read/update protocol and status vocabulary.
- `docs/PROJECT_STATE.md` — compact current-state restart image.
- `docs/PROJECT_HISTORY.md` — chronological reasoning/outcome log.

`[INVARIANT]` Git/code/test evidence outranks chat recollection.

`[INVARIANT]` Future meaningful changes should update persistent project memory during the same work session instead of waiting for a conversation to become too long.

`[DECISION]` Raw ChatGPT transcripts are not intended to be committed to the public repository. The durable record should be sanitized, project-specific, and concise.

## 2026-08-30 — third-session recovery bootstrap

`[HISTORICAL]` The first long session (`AOLite Config JSON Format`) and second long session (`Continue City Dwellers`) both became too long/unreliable to continue directly. A third ChatGPT session was opened to recover whatever could still be reconstructed.

`[VERIFIED]` Session #3 treated GitHub as source of truth, reconstructed the lost logout-cooldown functionality as commit `71f36fb`, created the persistent state/history/agent instructions, and then distilled the surviving context of Sessions #1 and #2 into encrypted conversation memories under `memory/conversations/`.

`[DECISION]` Conversation memories are indexed by `memory/MANIFEST.json`. Future agents should decrypt them using `memory/README.md` and a password supplied by the user; the password is deliberately not committed to this public repository.

`[VERIFIED]` Session #3 also wrote its own encrypted recovery/bootstrap memory as Conversation #3. That record explicitly explains why the memory system exists, how #1 and #2 were reconstructed, why old chat-only commit IDs cannot be trusted without Git verification, and what work remains open.

`[OPEN]` After continuity recovery, the next substantive implementation task remains the clean navmesh-based Serenity homing change, including publication of `6010.Navmesh`, pinned external CritterAI/Grid-navmesh restore, required x86 project settings, and live testing from positions that the old manual T-junction route rejected.

## 2026-08-30 — fourth-session recovery proof and continuity hardening

`[VERIFIED]` Session #4 started from a clean clone, followed the repository
instructions, decrypted all three memories in manifest order, verified every
plaintext byte count and SHA-256, and reconstructed the project without asking
the user to retell Sessions #1 or #2.

`[VERIFIED]` Session #4 rechecked current Git: `master` was clean at
`23da70e6566fb5b6ce3303b3a04efa338ab8c91e`; verified replacement commit
`71f36fb` existed; chat-only commits `6585617` and `91aeae6` did not.

`[HISTORICAL]` After this successful recovery test, the user deleted the old
ChatGPT conversations. Git and the encrypted memories became the sole durable
session lineage apart from the current live conversation.

`[DECISION]` Continuity is treated like checkpoint plus write-ahead recovery.
The canonical key is `CITYDWELLERS-RECOVER-V1`; `RECOVERY.md` is the front
door; `memory/CURSOR.json` exposes interrupted work; and
`memory/JOURNAL.jsonl` records append-only semantic transactions.

`[DECISION]` The project follows the “Heaven Sent” rule: every session is
mortal, so it must leave a small durable clue before undertaking work that
would be costly for the owner to reconstruct.

`[DECISION]` Journal growth must remain useful rather than indiscriminate.
Git records file operations; the journal records intent, boundaries, evidence,
outcomes, and recovery positions. Periodic compact checkpoints allow older
encrypted session memories to remain available without making normal startup
unbounded.

`[SECURITY]` No mechanism can honestly make a public recovery record readable
only by future ChatGPT sessions because ChatGPT has no persistent private key.
Encrypted session memories plus the owner-held password are the confidentiality
boundary. The recovery card deliberately contains no password.

`[OPEN]` After this continuity transaction is sealed, the next substantive
City Dwellers task remains navmesh-based Serenity homing.

## 2026-08-30 — fifth-session recovery and return to development

`[VERIFIED]` Session #5 cloned `master` at the Session #4 seal, followed
`RECOVERY.md`, found an idle cursor, decrypted the two boot-required memories
with matching byte counts and SHA-256 hashes, and decrypted historical Memories
#1 and #2 only when the owner tested older identity/project context.

`[VERIFIED]` The recovered state was sufficient to identify Kavey, Athen
Paladins, the in-game City Dwellers Raid/Apcmanager distinction, the last live
Serenity `#home 75` outcome, the published monotonic cooldown replacement, and
the still-unpublished navmesh implementation without asking the owner to
reconstruct Sessions #1 or #2.

`[DECISION]` Session #3 was the rescue/continuity-construction session, Session
#4 was the first clean recovery proof, and Session #5 is the first intended
return to ordinary development through that recovery system. Continuity is
designed so Session #6 succeeds if needed, not because Session #5 is expected
to fail.

`[DECISION]` Future session memories remain compact engineering records rather
than transcripts. Preserve decisions, invariants, verified evidence, hazards,
and the exact resume point; omit nonessential conversation and command noise.

`[INVARIANT]` Kavey builds and live-tests. Unless explicitly requested, the
assistant writes/reviews code and warns before potentially expensive
investigation instead of spending the limited Work-session usage window on
assistant-side builds or runtime tests.

`[SUPERSEDED]` At the recovery-proof checkpoint, navmesh-based Serenity homing
and the exact `6010.Navmesh` were still absent. The following Session #5
transaction recovered and published both.

## 2026-08-30 — Serenity navmesh homing published

`[VERIFIED]` Session #5 recovered the exact owner-supplied `6010.Navmesh` and
confirmed all recorded identifiers before publication: 2,087,208 bytes,
SHA-256 `d3bbb491f8e5b575f269f73fee8443c977f371bc0173231105954b3a34eef27c`,
and Git blob `7dee622c49ab0778ad4398bc2bd9df4d91b70a5f`.

`[VERIFIED]` Published commit:

`6d035746d9096a1be6ed51d02e09b6887b172414` — `Add Serenity navmesh homing`

The change replaces manual Serenity corridor selection with cached CritterAI
navmesh queries. After every settled server-confirmed movement pulse it finds a
fresh straight path to the CT and selects the first waypoint far enough ahead.
The existing bounded pulse, emergency-stop, divergence, stuck, and settled
position checks remain authoritative for actual movement.

`[VERIFIED]` The same commit adds a pinned PowerShell restore step for the three
x86 CritterAI DLLs and upstream Grid `152.Navmesh`. Every restored file has a
fixed byte count and SHA-256; public binaries remain outside Git. CityBuddies
and Buddies target x86. The unique Serenity navmesh is committed directly.

`[INVARIANT]` Grid remains explicitly route-unavailable. No Grid-to-Serenity
coordinate or city exit was guessed.

`[OPEN]` No assistant-side build or runtime test was run, by owner policy.
Kavey must build and live-test `#home`, especially west of the old T-junction,
then return the result for reconciliation and transaction sealing.

## 2026-08-30 — CritterAI native lifetime fix

`[LIVE-TEST]` Kavey reported that one buddy would not move and that Buddies
intermittently crashed with `AccessViolationException` in
`dtNavMesh.getTilesAt`, reached through `NavmeshQuery.GetNearestPoint`.

`[DIAGNOSIS]` The new `NavmeshPathfinder` retained its query and filter but not
the managed `Navmesh` used to create the query. The query holds a native
pointer into that navmesh, so garbage collection could finalize/free
`dtNavMesh` while later path queries still used it. The upstream reference
implementation retained the navmesh as a field.

`[VERIFIED]` Published fix:

`5ec43d5b87900ac89f5bd26c35562653098701c9` — `Keep CritterAI navmesh alive`

The pathfinder now strongly retains the navmesh for its entire lifetime. No
assistant-side build or runtime test was run. Kavey must rebuild and confirm
the access violation is gone. Diagnose the single non-moving buddy separately
only if it remains afterward, using its reported state/detail and position.

## 2026-08-30 — Grid exit observed from player protocol dump

`[LIVE-OBSERVATION]` Kavey supplied a full player-perspective protocol dump of
the Grid-to-Serenity transition. The local player arrived in Grid at
approximately `(234.3062, 3.775, 212.8138)`, moved to approximately
`(211.6727, 3.775, 186.7213)`, zoned, and arrived in Serenity at approximately
`(1068.757, 5.010, 1416.942)`.

`[VERIFIED]` The local outbound messages for the exit were movement-only. A
forward movement sequence reached the exit coordinate, followed by the area
change; there was no local click, use, target, or action message. The Grid exit
is a walk-triggered zoning volume rather than an object interaction.

`[DECISION]` The observed Grid exit is now sufficient evidence for the next
implementation: load/use the restored Grid `152.Navmesh`, route toward the
observed trigger, wait for the stable playfield model to become Serenity
`6010`, and then hand control to the existing Serenity path.

`[INVARIANT]` Transient protocol playfield-instance identities from a single
capture must not be coded as model IDs. `Playfield.ModelId` remains the stable
branching key.

`[OPEN]` The current CityBuddies code still deliberately reports
route-unavailable in Grid; this observation records the missing evidence but
does not itself implement the route. The dump also began after ICC-to-Grid
zoning was underway, so the ICC-side activation remains unmapped.

`[SECURITY]` The raw diagnostic dump was not committed. Only sanitized route
facts needed for future implementation were retained.
