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

`[HISTORICAL-INVARIANT]` In `6d03574`, Grid remained explicitly
route-unavailable. No Grid-to-Serenity coordinate or city exit was guessed.

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

`[SUPERSEDED BY 23069f0]` At the time of this observation CityBuddies still
reported route-unavailable in Grid, and ICC activation remained unmapped. The
following transaction implements both from the later owner direction and
static/live dynel evidence.

`[SECURITY]` The raw diagnostic dump was not committed. Only sanitized route
facts needed for future implementation were retained.

## 2026-08-30 — Continuous ICC-to-CT homing published

`[OWNER-DIRECTION]` Kavey requested that the existing bounded-pulse walker be
retained in full, that a smoother method become the default for all buddies
during a multi-day comparison, and that the route be extended from a manually
positioned buddy near `Enter The Grid` in ICC through Grid and Serenity to CT.

`[DESIGN]` An owner-supplied full-client AO# movement plugin demonstrated the
useful control pattern: one forward start, continuously refreshed heading and
position, and a final stop. Its controller could not be reused directly because
it depends on the full AO client engine. CityBuddies instead ports the pattern
onto the existing CritterAI route and explicitly integrates conservative
clientless command positions at 1.6667 m/s every 200 ms. The private reference
archive was not committed.

`[IMPLEMENTED]` Published commit:

`23069f055817a567baf35fc8253bdec2afbdac37` — `Add continuous ICC-to-CT homing`

The Buddies directive and telemetry now carry a movement mode. New or missing
modes default to `continuous`; `bounded-pulse`, `bounded`, or `pulse` selects
the preserved old controller. A single `DefaultHomeMovementMode` constant in
Buddies changes the global default.

The continuous controller slerps headings across CritterAI straight-path
waypoints and sends incremental `Update` positions while forward remains held.
Server-reported movement stays authoritative for arrival and progress. Command
lead, cross-track drift, and three seconds without measurable server progress
cause a full stop and route rebuild; repeated recovery remains bounded.

`[IMPLEMENTED]` Stable `Playfield.ModelId` now drives an end-to-end state
machine: ICC `655`, Grid `152`, and Serenity `6010`. In ICC the raw playfield
packet is captured before interaction. Nearby static dynels are logged, `Enter
The Grid` is found by name or verified template `95350`, and the static terminal
is reconciled to a live packet identity through metadata/type ordinal matching.
Ambiguous mappings are rejected rather than guessed. The resolved terminal is
used with limited retries while waiting for Grid.

In Grid the restored `152.Navmesh` routes to the owner-observed city-exit point
`(211.6727, 3.775, 186.7213)`. The controller stops there and waits for model
`6010`, matching the captured movement-only handoff. Serenity then follows the
published `6010.Navmesh` through the old T-junction region to the CT target and
final heading.

`[OPEN]` No assistant-side build or AO runtime test was run, by owner policy.
Kavey will build, monitor a small ICC sample through the complete route, and
compare continuous walking with bounded pulses over several days.

## 2026-08-30 — ICC static terminal activation corrected

`[LIVE-OBSERVATION]` The first complete Buddies run disproved the ICC live-only
assumption. The ICC character saw named static `Enter The Grid`
`(Terminal:C002028F)` at 1.7 m, but its playfield packet contained no live
`Terminal` entry. CityBuddies consequently waited for an identity that never
arrived and returned `route-unavailable` without sending `Use`.

The same parallel login exposed a separate AOSharp.Clientless 1.0.16 defect:
each client AppDomain lazily opens the shared `StaticDynelData.bin` using the
exclusive defaults of `File.Open(path, FileMode.Open)`. Concurrent initial
playfield loads can therefore throw a sharing-violation `IOException`.

`[DESIGN]` AOSharp's own `StaticDynel.Use()` sends the stored static identity.
The live-only reconciliation layer was an untested restriction, not a library
requirement. Sharing AOSharp's private nested dictionary from the parent would
require a maintained library fork or brittle cross-AppDomain marshalling.
Instead, each domain retains its own normal cache, while one named mutex
serializes the small one-time preload before `domain.Start()`.

`[IMPLEMENTED]` Published commit:

`abe19cbf8ce6b6b2cc256348eb3650afeb28e2a1` — `Fix ICC static terminal entry`

CityBuddies now uses the named static terminal through AOSharp's standard
`Use()` method and retains the existing distance guard, bounded retry count,
and stable Grid-model wait. The raw live-dynel capture and the live-only guard
were removed. Movement controllers, path selection, Grid exit, Serenity
routing, and chat reporting were deliberately untouched.

`[OPEN]` Kavey owns the build and two focused live checks: parallel buddy login
without static-data sharing violations, and one ICC terminal activation into
Grid model `152`.

## 2026-08-30 — Failed navigation results preserved for later passes

After narrowing the active work to ICC entry, Kavey supplied additional live
results that must survive without expanding the current code change. The
continuous walker visibly rubber-bands backward several times per second while
running. Clientless movement can ignore ordinary building collision, but still
activates teleporter volumes, making diagonal shortcuts dangerous. In Serenity
the route went directly toward CT rather than first aligning with the broad
north/south street, whose east/west coordinate appears to be approximately
`X=994` pending confirmation. In Grid the buddy reached the intended exit point
but did not zone into Serenity.

`[DECISION]` These findings are a deferred backlog only. The next live check is
the sealed ICC static-terminal/login fix; walking control, Serenity street
routing, and the Grid trigger will each be handled in separate later passes.

## 2026-08-30 — ICC entry verified; Grid stop diagnosed

`[VERIFIED-LIVE]` Kavey's focused build entered Grid immediately after the ICC
buddy logged on. This verifies the static `Enter The Grid` interaction from
`abe19cb`; the former wait for a live terminal identity is gone.

The buddy then navigated to the Grid exit area but stayed in model `152` until
CityBuddies reported:

`Grid did not change to Serenity within 20s after crossing the observed exit.`

`[VERIFIED-CODE]` It had not crossed the volume. `ProcessGridRoute` accepts the
target at `<=0.25m`; `BeginGridCrossing` immediately calls `StopMovement`; and
the 20-second waiting state sends no additional movement. The wording “after
crossing” is therefore inaccurate. Our earlier inference that the final
captured coordinate was itself sufficient to zone is superseded by this test.

`[OPEN]` The next focused change should keep movement bounded but carry the
buddy forward through the exit volume until stable model `6010` is observed,
rather than stopping at the recorded edge coordinate. No implementation was
made in this evidence transaction.

## 2026-08-30 — Bounded Grid exit crossing published

`[OWNER-DIRECTION]` Kavey approved the isolated Grid handoff fix and clarified
that the normal client's roughly 15-second post-login teleport restriction is
client-side. Clientless successfully used `Enter The Grid` immediately, so no
artificial login delay belongs in this route.

`[IMPLEMENTED]` Published commit:

`d927cd59ca69b28800c237447c93b5607f34811a` — `Cross the Grid exit volume`

At the observed exit edge, CityBuddies now abandons the old stop-and-wait
inference and starts one dedicated bounded crossing pulse immediately. The
pulse travels 2 m over 1.2 seconds along the captured Grid arrival-to-exit
direction. If the playfield has not changed during that pulse, it sends a full
stop at the bounded endpoint and continues waiting for stable model `6010`.
The existing 20-second zone timeout remains the final safety limit.

This handoff deliberately does not reuse or modify continuous steering. It
uses the established bounded-pulse distance and duration regardless of the
selected home movement mode. Walking smoothness, Serenity main-street routing,
and chat diagnostics remain outside this transaction.

`[OPEN]` Kavey owns the build and one monitored ICC-to-Grid-to-Serenity test.

## 2026-09-01 — Grid crossing retry failure and navigation references

`[VERIFIED-LIVE]` ICC entry remains solved: a buddy already near `Enter The
Grid` uses the static terminal and enters Grid. The dedicated 2 m crossing pulse
from `d927cd5` did not complete the Grid-to-Serenity handoff. Across repeated
home jobs the buddy alternates between the observed exit position and the
bounded endpoint: one run moves away, the next routes back, and neither zones.

`[CORRECTED-EVIDENCE]` The successful full-client trace was re-examined. Its
last translational message is `ForwardStop` at exactly
`(211.6727, 3.775, 186.7213)`. Small turn-stop messages follow, then the area
change. There is no successful `FullStop` two metres beyond the coordinate.
The implemented pulse therefore differs from the trace in both endpoint and
stop action. Increasing pulse distance again is not evidence-based.

`[PROPOSED]` Make the Grid exit operation repeatable before investigating
walking smoothness. Every attempt should use a fixed Grid-side staging point,
one uninterrupted final forward leg, `ForwardStop` at the captured exit point,
and a bounded wait for stable model `6010`. A job starting near either point
must perform the whole staging-to-exit attempt rather than alternate between
movement halves across jobs.

`[REFERENCE REVIEW]` Kavey supplied NavGen and NavManager source archives.
NavGen's useful pieces are full-client navmesh baking, configurable Recast
parameters, off-mesh links, and straight-path/corridor visualization. A repaired
existing-mesh loader could help separate corrupt-mesh failures from clientless
CritterAI ABI/lifetime/concurrency failures. NavManager's useful idea is hybrid
navigation: navmesh for broad legs, explicit direct waypoints for difficult
ramps/drops, then a separate interaction.

Neither archive is a clientless implementation or a general Grid/Whom-Pah
planner, and neither contains navmesh binaries. They target normal-client
AOSharpSDK `1.0.100`/`1.0.105`; City Dwellers remains binary-coupled to
AOSharpSDK `1.0.84`. No archive code was imported, built, or tested.

`[OWNER POLICY]` Kavey builds and performs AO runtime tests. Walking
rubber-banding remains a separate later pass after Grid exit repeatability.

## 2026-09-01 — Walk/run toggling evaluated from packet evidence

`[OWNER HYPOTHESIS]` Because AO supports running and walking states, Kavey
proposed rapidly alternating `SwitchToWalk` and `SwitchToRun` instead of
stop/start pulses, hoping each mode switch would make the server return the
character's current authoritative position.

`[LIVE-OBSERVATION]` Kavey captured full-client sent/received movement pairs.
Every received walk/run switch contained exactly the heading and position sent
by the client, with only `DeltaTime` reset to zero. Forward/backward start and
stop actions behaved the same way. These replies acknowledge or relay the
sender's asserted movement state; they do not supply a separately measured
server position.

`[DECISION]` Reject rapid walk/run toggling as a synchronization loop. In
clientless it would keep resending the same stale or predicted position and may
increase hesitation. A sustained walking leg remains a possible later
experiment because the sample shows that walking is materially slower, not
because switching modes requests position.

`[LIVE-OBSERVATION]` The trace's approximate displacement rates were `2.24
m/s` running forward, `1.20 m/s` walking forward, and `2.36 m/s` running
backward. CityBuddies currently predicts `1.6667 m/s` while emitting updates
every 200 ms. The speed mismatch is a plausible explanation for the visible
backward snaps: the server advances the running character farther, then the
next slower synthetic position pulls it backward. The rates are specific to
this character/sample and must not be promoted to universal constants without
additional evidence.

No movement code was changed and no assistant-side build or AO test was run.
The deterministic Grid exit experiment remains the next implementation task;
walking control stays separate.

## 2026-09-01 — Longer movement trace corrects fixed-speed inference

Kavey clarified that AO Run Speed is character-specific and can vary with
breed, profession, level, abilities, and temporary resurrection state. City
Dwellers covers eight level brackets, all professions, and multiple breeds;
after death, diminished skills recover incrementally. Jump height is a
separate Strength-related variable. A single global velocity therefore cannot
represent the fleet or even one recovering character over time.

`[LIVE-OBSERVATION]` In a longer timestamped full-client capture, one character
settled near `5.84-6.49 m/s` while running and `1.40-1.51 m/s` while walking.
The first `192 ms` run interval and later short run legs were slower, showing
startup/transient behavior; vertical terrain position also varied. These rates
are evidence for that character and capture only.

The same trace showed a second structural difference from CityBuddies. The
normal client emitted repeated uninterrupted-leg `Update` packets after about
`5001-5002 ms`, with earlier updates associated with intervening actions or
other client conditions. It did not assert synthetic positions every `200 ms`.
Every received movement packet still mirrored a sent position, and no
unsolicited authoritative correction appeared.

`[CORRECTION]` The earlier `2.3 m/s` versus `1.6667 m/s` comparison is not a
general speed model. The supported conclusion is broader: CityBuddies' fixed
`1.6667 m/s` predictor cannot match the heterogeneous fleet or resurrection
recovery, and its `200 ms` self-position update cadence is unlike the captured
normal client. Either or both may contribute to rubber-banding; neither is yet
proven as the sole cause.

AOSharp protocol definitions contain `Stat.RunSpeed` and
`SimpleCharFullUpdateMessage.RunSpeedBase`, while normal-client vehicle state
also exposes run speed, acceleration, and velocity. Whether
AOSharp.Clientless 1.0.16 retains the effective live value and receives
incremental resurrection changes remains unverified. No raw owner log, code
change, build, or AO test was committed. Grid exit repeatability remains the
next isolated implementation task.

## 2026-09-01 — Per-home-job navigation forensics added

`[OWNER-DIRECTION]` Kavey asked for an objective trace before further movement
changes so live behavior no longer depends on unreliable visual narration.
One character was deliberately left in ICC for a clean end-to-end specimen.

`[IMPLEMENTED]` CityBuddies now creates one durable JSONL file for each new
home job. It records outbound movement assertions and received self echoes as
different event types, along with UTC time, sequence, observed and asserted
transforms, Run Speed, playfield, home state/detail, route/controller state,
Grid crossing state, ICC interactions, and once-per-second quiet-state samples.
Writes are buffered and forced at important or terminal boundaries.

Buddy.exe reports the trace filename with the terminal result. Manager
`#position` exposes the active filename/event number, Run Speed, and newest
command/echo without streaming the full trace into guest chat. Runtime trace
directories are ignored by Git and raw captures remain outside the public
repository.

`[INVARIANT]` No routing, movement timing, Grid crossing behavior, or command
semantics were intentionally changed. Kavey retains build and AO runtime-test
ownership. The next evidence is one ordinary ICC-to-Grid home attempt and its
resulting JSONL trace.

## 2026-09-01 — Flipper result publication made unload-safe

Kavey supplied one complete Apcflipper service log. The raid-start watch saw
100% controller charge, sent one lower action, ignored the repeated pre-toggle
`Enabled` packet, and then accepted a changed `Disabled` packet with a
3600-second shield timer. The cloak action was therefore confirmed successful.

Only after Flipper.exe printed `Unloading flipper client...` did a
`ThreadAbortException` interrupt `CityFlipper.MessageReceived`. AOSharp then
reported `Failed to deserialize packet` with the same abort still rooted at the
end of that handler. This identifies a result-publication/unload race rather
than corrupt cloak data or a failed raid action.

CityFlipper now stops receiving message/update callbacks as soon as a terminal
result is selected. A thread-pool callback waits 100 ms, writes and logs the
result, and exposes the final JSON file last through its existing atomic rename.
Because that file is Flipper.exe's unload signal, the handler that selected the
result has time to return before AppDomain teardown begins. A teardown abort is
re-thrown rather than recursively logged from the interrupted handler.

Manager, Buddies, and Flipper now give AOSharp one shared Serilog console
template containing ISO-style date, time to milliseconds, and numeric UTC
offset, for example `2026-09-01T20:27:23.123+03:00`. Logs collected on machines
with different time zones can therefore be correlated directly. No raw owner
log was committed, and no assistant-side build or AO runtime test was run.

Kavey's immediate Release build then compiled five of six projects. Only
CityManager failed, with four `CS8967` errors caused by multiline method calls
inside interpolated-string expressions. That form requires C# 11, while City
Dwellers currently compiles as C# 7.3. The command and echo strings are now
computed in local variables before they are inserted into the position window
and developer telemetry. This is a syntax-only compatibility correction; the
resulting text and runtime decisions are unchanged. Kavey owns the confirming
rebuild.

## 2026-09-01 — Deterministic Grid staging and exact ForwardStop

Kavey returned the first complete per-home-job navigation trace. It contained
354 monotonically ordered events from `2026-09-01T18:14:38.0576114Z` through
`2026-09-01T18:15:32.9469416Z`. ICC used the static terminal twice and entered
stable Grid model `152`. The Grid navmesh route itself succeeded.

`[TRACE-DIAGNOSIS]` Continuous Grid movement sent 101 synthetic `Update`
commands at approximately 204 ms intervals and 0.340 m per command, exactly
the hard-coded 1.6667 m/s predictor. All 101 corresponding echoes repeated the
asserted positions; the trace contained no independent correction. This is
useful evidence for the later rubber-banding redesign, but that work remains
deliberately separate because AO speed varies by character and resurrection
state.

At the transition, the old controller sent `FullStop` approximately 0.192 m
before the captured trigger, then began another forward leg and sent
`FullStop` approximately 1.808 m beyond it. That precisely explains both the
timeout and the alternating retry positions. It did not send the successful
full client's `ForwardStop` at `(211.6727, 3.775, 186.7213)`.

`[IMPLEMENTED]` Published commit:

`0f73756945df282ccf0601f9c8c80c40d1ead148` — `Make Grid exit attempts deterministic`

Every Grid attempt now targets a staging point exactly 2 m before the captured
exit along the observed arrival-to-exit line, approximately
`(212.9832, 3.7750, 188.2321)`. After navmesh arrival it asserts an exact
staging `FullStop` and waits for the matching self echo as a packet-ordering
barrier. That echo is not treated as an independent authoritative server
position. The handoff then sends one `ForwardStart`, no synthetic `Update`, and
after 1.2 s sends `ForwardStop` exactly at the captured trigger. It waits a
bounded 20 seconds for stable Serenity model `6010`.

New home directives now reset Grid and ICC transition substates as well as the
two walking controllers. Therefore a job starting at the old overshoot, at the
trigger, or during a previous wait always returns to staging and performs the
same complete attempt. ICC terminal use, general continuous movement,
bounded-pulse rollback, Serenity routing, and city street selection were not
changed.

`[OPEN]` Kavey owns the build and one monitored ICC-to-Grid-to-Serenity test.
The returned JSONL should show staging `FullStop`, final `ForwardStart`, exact
exit `ForwardStop`, and either stable model `6010` or the bounded timeout.

## 2026-09-01 — Full-client Grid exit movement tail restored

Kavey's second deterministic trace proved that the repeatability change worked:
the buddy stopped at the fixed staging point, sent and received `ForwardStart`,
sent and received `ForwardStop` exactly at `(211.6727, 3.7750, 186.7213)`, and
remained there rather than alternating endpoints. It nevertheless stayed in
Grid model `152` for the complete 20-second wait.

`[CORRECTION]` The successful full-client sequence had previously been reduced
too far. Its last translational tail was `TurnLeftMouse` at
`(211.9757, 3.7750, 187.0108)`, then `ForwardStop` 35 ms later at the exact
exit. It subsequently sent stationary `TurnRightMouse`, `TurnLeftMouse`, and
`TurnLeftStop` packets at the exit before the area changed. The deterministic
clientless attempt omitted all four of those surrounding actions.

`[IMPLEMENTED]` Published commit:

`1464d99a01e24e848e70806fcf2f6ee3ba977f3f` — `Replay full Grid exit movement tail`

The fixed staging and retry-safe state machine remain unchanged. The final leg
now sends the captured near-exit `TurnLeftMouse`, exact `ForwardStop`, and the
three post-stop turn actions using the captured headings and relative 35 ms,
83 ms, 6 ms, and 160 ms delays. The bounded 20-second model-`6010` wait starts
after `TurnLeftStop`. AOSharp's pinned `MovementAction` enum was verified to
contain every action used by the capture.

The same live trace showed why walking could look somewhat better without a
general-controller change: its 96 synthetic updates were exceptionally regular
at approximately 203.05-203.20 ms, and the final 2 m leg contained no updates.
The main route still used the hard-coded 1.6667 m/s predictor and remained
visibly rubber-bandy. General walking remains a separate later transaction.

`[OPEN]` Kavey owns the build and next monitored ICC-to-Grid test. No raw trace,
assistant-side build, or AO runtime test was committed or run.

## 2026-09-01 — Repository-wide single-writer coordination

Kavey authorized City Dwellers Chat mode for smaller bugs, City Dwellers Work
mode for serious work, and two additional GPT sessions for the cousin City
Banker project in this same repository. Kavey also established the simplifying
invariant: only one of those sessions will write at a time.

`[DECISION]` All projects and modes continue on `master`; no session branches
are needed. The existing cursor is now explicitly the repository-wide writer
lock. A writer fetches `master`, publishes its `BEGIN`/`in_progress` marker,
makes one focused change, and publishes a terminal seal returning the cursor
to `idle`. Other sessions may read and discuss during an active transaction,
but cannot write unless they are recovering that exact interrupted work.

`[DECISION]` City Banker receives separate compact state/history and its own
recovery card. The projects share the global journal and may share validated
engineering ideas through `docs/SHARED_ENGINEERING.md`; no code is ported
without project-specific compatibility evidence.
