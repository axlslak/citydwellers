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
