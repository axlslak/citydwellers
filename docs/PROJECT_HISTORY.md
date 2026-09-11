## Session 74 — withdrawal pickup identity and Central custody accounting

- [OWNER EVIDENCE] Remote offer/remove/cancel test passed: ten corrections and CANCELLATION VERIFIED, with no new audit in supplied excerpt. Later withdrawal reached Central-ready but owner pickup silently closed. On expiry the return failed with no matching sender-held ledger occurrences; census subsequently returned/stored the item with explicit verification. That is recovered storage, not successful pickup.
- [SOURCE FIX] FindWithdrawalInventoryItem treated (None:0000) Central identity as unique, then returned null when multiple inventory items shared it. Central/extracted/reservation identity checks now reject placeholders; Central falls back only to a unique exact template/QL match, excluding other usable-identity reservations. Worker extraction anchors are not reused on Central; worker anchor identity must be usable or name+QL must agree. Ambiguity still rejects pickup rather than guessing a copy.
- [ACCOUNTING] OpenPickupWindow previously removed source stock and marked central-ready without moving its active-ledger occurrence to Central. It now persists the verified received item's existing ledger ID/transaction/template as Central inventory before publishing readiness. Entry validation checks expected source-or-Central ownership and preserves provenance. This makes ordinary expiry dispatch find the sender-held occurrence; persistence retries remain idempotent. No verification bypass or direct live data repair.
- [VISIBILITY] Added PICKUP OPENED and reasoned PICKUP DECLINED telemetry/tells for expiration, offered donation items and unidentifiable reserved items. Incoming trades declined for active extraction/storage or withdrawal recovery now explain the closure. Existing successful delivery archives the exact active ID before telling the requester completion.
- [VALIDATION/NEXT] Focused static identity/ambiguity, callback/confirmation, verified-receipt-to-ready/ledger-to-return, shared source and whitespace review only. No assistant compile/test/live run. Owner rebuild then test actual pickup through completion/history removal, and separately an untouched three-minute expiry returning to verified storage without audit. These flows remain unvalidated for this patch. Prior raw evidence is private; historical repair must not repeat. Manager packet decode and broader recovery checks remain open.

## Session 73 — remote offer removal must not populate local inventory

- [EVIDENCE] Owner stopped bankers after cancelled-donation mismatch and repeated Central/worker timeout/audit loop. Retained receipt evidence shows direction=0, expected empty, and three additional local-cache items exactly matching items previously removed from the remote offer. Repeated dispatch attempts reached peer-opened then cancellation with unchanged inventories. This supports phantom local-cache entries, not completed transfers. Private receipts/logs remain outside Git.
- [CAUSE/FIX] Available Clientless Trade.RemoveItemAction unconditionally adds a removed item to local Inventory for either offer window. Its NetworkSession dispatches MessageReceived before native trade callbacks. The existing CityBankers diagnostic message hook now removes an exact current-remote-partner offer-slot entry before that native callback can incorrectly return it to local inventory. Local-player removals, Complete, Decline and other messages follow native behavior. No reflection, dependency upgrade, inventory deletion or ledger deletion is introduced. Guard installation and each corrected removal are logged.
- [CANCELLATION] Existing direction-zero unchanged-inventory verification remains mandatory and now emits CANCELLATION VERIFIED to console/dev channel before clearing its receipt. Ordinary unchanged cancellation does not request census; actual unexplained inventory differences still do.
- [RECOVERY] A fresh process obtains fresh game inventory rather than retaining the polluted in-memory cache. Existing startup census applies observed inventory and archives differences; do not repeat the historical manual repair or manually delete custody/ledger records. An audit inside the already-polluted process was not independent confirmation of those items.
- [VALIDATION/NEXT] Static packet-dispatch order, native window/slot semantics, local-vs-remote/duplicate/non-removal branches, cancellation control flow, source inclusion and diff/whitespace reviewed. No assistant build/test/live run. Owner rebuild first, then fresh startup census; controlled offer/remove/cancel should log remote removal and CANCELLATION VERIFIED with no extra audit. Then validate completed donation/routing/storage and local-offer cancellation. Deployment behavior remains owner-tested; keep stopped until build ready. Separate Manager packet decode error and broader recovery/withdrawal validation remain open.

## Session 72 — restore operator visibility of donation routing and storage

- [OWNER EVIDENCE] Startup succeeds after Session71. Latest full launch log initializes all nine actors and shows Central count decrease with infantry count increase, supporting successful routing; longer recovery/withdrawal acceptance remains unverified. A subsequent donation excerpt reports three items queued followed by raw bank-slot moves but no operator-visible storage completion. Manager packet-decoding OutOfMemoryException remains separately unresolved.
- [CAUSE] Existing donation/dispatch milestones persisted through AppendTradeLedger and stored-item activity but were not forwarded to the Manager dev channel or normal console. This visibility gap was not justified by confidence in recovery. Those accounting/activity records remain intact.
- [CHANGE] Mirror trade milestones to Logger and the existing ManagerChannelQueue, retaining transaction/batch/role and item name/QL/AOID. Add persisted donation queue destination, individually verified storage location, batch counts and storage failure messages. Report worker preparation, ineligible route and storage-acknowledgement waits with per-batch thirty-second monotonic throttling. Messages are escaped and split into bounded chunks. Telemetry delivery failures are isolated from custody/accounting; no physical action is driven by logging. Removed misleading hard-coded donor name from mirrored donation descriptions.
- [VALIDATION/NEXT] Focused static call-site/control-flow/diff, shared queue signature and whitespace review only; no assistant build/tests/live run. Owner rebuild then verify dev channel and console show queue -> transfer -> receipt -> STORED VERIFIED -> batch completion for a small donation. Existing transfer facts determine milestones; this does not assert completion of the supplied donation solely from raw slot moves. Historical data repair must not repeat. No live data/config changes.

## Session 71 — pre-login readiness exception preserves deferred startup

- [VERIFIED] Owner startup diagnostics identify NullReferenceException in StartupCensusGate.IsOpen through Defer for all nine banking services, before login. The Clientless source getter for InPlay dereferences its network session. That readiness guard was outside IsOpen's fail-closed try/catch, so the exception escaped before startup could enter the deferred queue. Only ActiveLedgerCoordinator appeared in later deferred-component logs.
- [FIX] Moved the existing initial readiness guard inside the existing exception boundary. An unavailable pre-login client now yields false, allowing Defer to retain the banking startup action. The census gate still requires a released matching connection and all existing readiness checks before invoking it. Session70 startup diagnostics and actor-heartbeat admission remain in place.
- [VALIDATION/NEXT] Focused static control-flow/diff and whitespace review only, no assistant compilation/tests/live run. Owner rebuild and controlled launch next: verify BANKING SERVICE startup registered, then startup entering and initialized after census release, followed by routing. This corrects the observed startup exception; physical transfer remains unverified. No live data/config changes and no repeated historical repair.

## Session 70 — observable banking initialization and operational readiness

- [EVIDENCE] Owner full launch log shows all nine census participants released with one queued route, then no banking initialization/transfer for more than eleven minutes. Available upstream Clientless loader suppresses Init exceptions; the exact exception and equivalence to the deployed loader remain unverified.
- [CHANGE] Banking startup now logs registration and entry, with dependency-heavy initialization isolated behind a non-inlined method and full exception reporting plus best-effort teardown. Deferred components are identified by type/method. Deterministic initialization failures do not themselves request repeated physical audits.
- [CHANGE] Only an enabled banking actor with a running IPC server publishes a monotonic operational heartbeat. Shared readiness requires its current cycle/connection and freshness under ten seconds as well as existing census/presence checks. Manager status reports banking service not ready when that proof is absent. Census gate startup itself does not require this heartbeat, avoiding circular startup dependency. Teardown removes the marker; old-cycle/connection markers cannot release a new actor.
- [VALIDATION/NEXT] Focused static diff/call-site/project and whitespace review only; no assistant build, tests or live AO run. Owner must pull/rebuild and return BANKING SERVICE startup registered/entering/initialized or full initialization failed exception. This diagnostic/readiness update does not establish the underlying stall is fixed. No live data/config changes; historical repair already applied and must not repeat.

## Session 69 — live census sharing and operational handoff

- [OWNER-VERIFIED] Session 68 build succeeded. Controlled live startup later reached a released census with all nine bankers, but repeatedly logged presence-file sharing/missing-file errors. The supplied log had no banking-service initialized/dispatch lines, and the owner reported a symbiant remaining in Central inventory. Release of the shared census alone did not establish operational readiness. Item identity/destination and current queue contents were not supplied, so its specific routing failure is not proven.
- [SOURCE FIX] Census evidence and shared readiness reads now use RuntimeStateStore.ReadJsonStrict/ReadTextStrict, coordinated with atomic writers through the same per-file mutex. The open snapshot permits atomic replacement without denying delete sharing. Missing files remain absent, while corrupt/locked/unreadable files remain explicit retryable errors, never an empty ledger. No persistence-failure bypass was introduced.
- [HANDOFF] The census gate directly drains its deferred initialization actions after local readiness, rather than relying on separately registered update-event closures. Replacement banking actors use the same cancellable gate queue. Added BANKER READY with deferred component count, explicit local handoff waiting diagnostics, and queued-route counts beside census release. This makes collector/application/actor-start stages distinguishable. Static review cannot establish that the reported symbiant now routes.
- [VALIDATION/NEXT] Static shared-source/project checks, delimiter and call-site review, and whitespace checks only; no assistant build/tests/live run. Owner was asked to stop the current test, but stop has not been confirmed. Pull/rebuild, then repeat controlled startup and provide the new readiness/initialization/routing logs. If the item remains, inspect its actual identity, current dispatch queue, and current cycle/ready records. The log also contains one Manager packet-deserialization OutOfMemoryException; no causal connection to the banker symptom is established and no packet parser change was made. Historical data repair is already applied; do not repeat it.

## Session 68 — owner-reported Release build errors corrected

- Owner build of Session 67 reported four errors: missing WithdrawalStore in the executable's linked TrustedOperators source; missing TrustedOperators in the manager's linked WithdrawalState source; an instance-qualified static TryDeclineTrade call; and a storage-recovery handler missing its final boolean return.
- Extracted the existing readiness implementation into standalone bankers/shared/BankerReadiness.cs, linked once by CityDwellers, CityManager and CityBankers. WithdrawalStore delegates its public readiness methods; TrustedOperators delegates directly to BankerReadiness and aliases its marker constant. Readiness logic, connection freshness and admission policy are unchanged. Corrected the static call and added the handled=true return after a successful recovery grant reply.
- Static comparison verified the moved readiness methods are unchanged except their local marker constant reference. Reviewed all three consuming project source lists, the static call and handler exit, and whitespace. No assistant compilation, test suite or live AO run. Owner rebuild is next; runtime acceptance remains pending and service remains stopped. Historical data repair is already applied and must not be repeated.

## Session 67 — recovery implementation complete; owner validation next

- [VERIFIED: SOURCE] Replaced the one-shot nine-client startup barrier with connection-bound census cycles. Central collects the currently online, bank-ready configured roster after a short gathering interval. Each participant stops its operational actor, archives its original operations, closes any trade, retires collector handles and waits for stable inventory before a full audit. Only a completely applied immutable census can release those exact connections. Reconnecting or newly available bankers enter a fresh cycle; old pause tokens, callbacks and actor fields cannot release or restart old work.
- [DECISION] Generic disconnects/unrelated holds and recovery-owner collisions use a coordinated pause of the available roster. Eligible local/paired recovery still runs locally. Stalled ownership or verified peer accounting falls back after 60 seconds; active full bag scans have no such arbitrary deadline. Multiple legacy unresolved dispatch attempts can use this fallback. This is a temporary reconciliation pause, not a permanent discrepancy shutdown.
- [INVARIANT] Central is required. Offline/bank-unavailable characters keep their prior ledger/storage records, with no claim that their items disappeared. Readiness, GET selection/admission and stock-to-ledger synchronization exclude those connections. A failed worker audit is retained, removes that worker from readiness, and retries after a minute; healthy participants can finish. A returning worker is audited before its records become usable. Roster configuration still requires all nine distinct banker mappings.
- [INVARIANT] Interrupted application finishes its retained plan while actors remain paused. If a participant disconnected or another hold arrived, that snapshot cannot release anyone: another physical cycle follows. Cycle publication precedes consuming recovery requests. Source address anchors from interrupted extractions are not treated as identity proof; uniquely verified Central identities and known deliveries retain their stronger evidence. Original requests/operations and differences remain archived. Broad recovery closes old pickup requests without inferring delivery; users can request again after recovery.
- [VERIFIED: SOURCE] Withdrawal transfers now persist a unique attempt and obtain live Central preparation before opening, peer-opened acknowledgement before offering, and fresh exact-attempt accepted acknowledgement before confirmation. Returns use the same staged protocol. Both reject contradictory reciprocal items, pace acceptance/confirmation and verify physical custody after closure. Pickup confirmation uses its captured partner, exact full offer and a paced one-shot confirmation; item offers are spaced. Dispatch preparation rejects retired queue attempts.
- [VERIFIED: SOURCE] Pickup deadlines and audit phase timeouts use the host's monotonic clock. UTC remains display/history metadata. Recovery restores valid local pickup windows through the shared renewal helper; prior-process deadlines are not resumed. Donation handshake pickup exclusion uses the same deadline rule.
- [VALIDATION] Static source/call-site review, lexical delimiter checks, project source inclusion and whitespace checks passed. No compilation, test suite or live AO test was run, per owner instructions. This closes the implementation backlog for Session 67, not runtime acceptance.
- [NEXT] Kavey builds the current master and returns compiler output, then performs controlled startup/reconnect/dispatch/withdrawal/return testing. The service remains stopped and is not restart-cleared by static review. Historical repair (2300 active entries, 395 excess claims removed, four loss-history entries, old Vital dispatch closed) is already applied; never repeat it. No configuration enablement or live data changes were made.

## Session 67 — interrupted withdrawal and pickup census

- Added a retained withdrawal-census grant spanning Central and the source characters of all already-staged active requests. Merely queued requests on other workers remain outside the scope. Under the withdrawal admission mutex, character leases are persisted before the captured requests become `reconciling`; exact revisions and a frozen marker support interrupted persistence retries. Trade callbacks and new admission respect recovery ownership. A source discovers its durable lease; complete audit evidence returns through the existing local-census IPC path.
- Closed withdrawal receipt mismatches, conflicting terminal callbacks and interrupted accounting request physical recovery. Central also schedules failed requests automatically. Every included character must close its trade, settle its inventory, and finish a complete bag/loose-bank/inventory audit. The fixed joint application preserves unrelated characters' latest data, archives original operations and differences, rebuilds Central routing, and retains unrelated unresolved dispatches with duplicate-retry exclusion.
- Previously confirmed delivery is retained in history and its ledger ID is excluded from matching newly observed items. After possible extraction, the historical source-slot anchor is disabled: reconnect only through a unique remaining occurrence or a verified Central item identity. A uniquely identified Central pickup anchors to that identity instead of a possibly reused old source slot. A retained exact copy on Central can regain its pickup window; an exact source-bag copy can return to `requested` with current slot/identity. Loose, missing, ambiguous, expired or already-returning copies return to ordinary physical routing/review and their old requests close without a delivery claim. Recovery does not guess donors or recipients for unmatched items.
- Pickup expiry is captured before the audit so valid windows do not expire merely because recovery takes time; resumed ready requests receive a fresh three-minute window at durable request restoration. Old extraction/receipt wait timers are cleared on recovery. Withdrawal phase deadlines now use Stopwatch. A missing terminal callback after an observed trade closure waits one second and verifies the complete inventory; it cannot itself establish delivery or return.
- Static source/call-site/project inclusion, JSON/journal and whitespace review only. No assistant compilation, test suite or live AO test; Kavey owns those. This code is unvalidated at runtime and does not clear the stopped service for restart.
- OPEN: generic disconnected/unrelated holds and recovery-owner collisions; partial-roster startup; remaining live trade-stage IPC for withdrawal/return flows. A peer already held by an unrelated recovery or offline still prevents its group from finishing. Existing storage/return recovery and full startup continue to own their respective paths. Continue Session 67; historical data repair is already applied and must not be repeated.

## Session 67 — recovered paired census and missing dispatch receipts

- Recovered the predecessor's uncommitted paired-census implementation from its surviving workspace at published `1b08988`; checkpoint 181 was local, not published. The current Work session is the sole writer and continues the same transaction.
- A closed dispatch mismatch or failed dispatch accounting enters a coordinated Central/worker full audit. An idle Central also revisits failed attempted dispatches. Missing sender or receiver receipts are retained explicitly as missing evidence; the persisted attempt/manifest/participants identify what is being retired, while both fresh complete audits establish physical custody. No transfer, cancellation, or delivery is inferred from missing receipts.
- Both character reservations are acquired under the withdrawal mutex. New dispatch preparation, local storage, extraction and loose-return admission respect those reservations before the peer has joined. A late return-ready reply cannot open a trade into a census. Existing unrelated actions must finish before joining; missing/incomplete audits keep the pair paused. Retained grants are rechecked against the live queue before admission.
- Joint application retains the old records and differences, merges only Central and the affected worker into the latest storage/ledger, and rebuilds Central routing from its observed loose inventory. It preserves other workers' unresolved dispatch rows rather than dropping them or blocking every pair. Those rows prevent overlapping-template routing and new dispatches to their worker. A durable RequiresPairedCensus flag prevents old cancellation receipts from adding a duplicate retry over the new physical queue. Retried applications preserve current unrelated rows and never restore completed unrelated batches.
- Static source/call-site/project inclusion and whitespace review only; no compilation, test suites, or live AO tests. The user owns builds and live testing. Service remains stopped; this is not restart clearance.
- OPEN: active withdrawal/pickup recovery; generic unrelated/disconnect holds; partial-roster startup; remaining return/withdrawal trade-stage IPC. Multiple legacy unresolved attempts on the SAME worker remain excluded from a single-attempt pair; new dispatch admission prevents creating that overlap. Offline/busy peers must become available before their pair can finish. Continue within Session 67; do not repeat the already-applied historical data repair.

## Session 67 — paired census for partial dispatch custody

- A closed dispatch whose inventory delta remains mismatched now requests a coordinated Central/worker census instead of immediately entering the generic non-resuming hold. Both participants must retain evidence for the same attempt, batch, transaction and full manifest. Central retains a grant, original batch and source proof; each participant archives its own receipt/storage operation before clearing only that operation's callbacks and movement state.
- Both character reservations are acquired together under the withdrawal mutex. Central and the affected worker pause under owned hold versions, keep recovery IPC running, wait for stable outer inventory/bank observations, and reuse the complete bag-audit collector. Central does not apply either snapshot separately: both complete results feed one retained physical reconciliation plan. Other workers' current ledger and storage are preserved on each retry.
- The plan records all differences and affected unstarted withdrawal dispositions, preserves grounded provenance, permits unknown origin, removes unmatched active claims to history, and rebuilds dispatch solely from physically observed Central loose items. The old transfer is archived, not replayed or declared successful. Joint completion is durable before either local completion acknowledgement can resume movement; delayed preparation cannot restart a completed pair. The worker retains its most recent completed dispatch receipt for a peer whose own verification subsequently fails.
- Static source/API/call-site/project inclusion and whitespace review only; no compilation/test suite/live AO test. OPEN: missing peer/open evidence, concurrent unresolved dispatches that cannot yet drain independently, active withdrawal/pickup recovery (pair admission currently excludes these), unrelated/disconnect holds and partial-roster startup. This implements eligible closed partial-dispatch reconciliation, not every runtime recovery case. Service remains stopped; session67 active.

## Session 67 — live dispatch stage acknowledgements

- Added BankingServiceAgent.TradeStages.cs. Central waits for an attempt-bound live worker opened acknowledgement before adding items. The worker accepts only after live IPC proves Central accepted the exact complete local offer; an incomplete receiver cache may be a matching subset but may not contain an extra or contradictory template. Unexpected reciprocal items are rejected.
- Both sides require their own accepted state, current consistent windows and a fresh peer accepted acknowledgement before confirmation. Confirmation waits at least 500 ms after local acceptance and the AO confirmation callback, uses the current/captured trade partner rather than a potentially unrelated callback identity, and is submitted once per receipt. Stage queries are evaluated on the AO thread and do not themselves perform trade actions. Acknowledgements are scoped to full manifest, participants, transaction, batch and attempt, expire by request-departure age, and are retained in custody evidence.
- Disabled the legacy InternalTradeHandshakeBridge in physical-recovery mode so its accept-event/cache fallback cannot bypass the new IPC stages. Dispatch and worker trade deadlines and AddItem retry spacing now use Stopwatch rather than wall-clock subtraction. Closed trades without a terminal callback enter physical cancellation verification after a one-second grace period; closure alone does not infer transfer or return.
- Static source/call-site/project inclusion and whitespace review only; no build/test suite/live AO test per owner boundary. OPEN: genuinely partial transfer requires paired physical census/disposition, missing peer-open evidence, interrupted active withdrawals, receipt mismatch/disconnect/unrelated holds and partial-roster startup. These handshake changes address prevention and missing close callbacks; they are not a completed partial-transfer repair. Service remains stopped; session67 active.

## Session 67 — verified-transfer storage failure recovery

- Failed worker storage now requests a scoped full census over IPC. Central requires retained applied sender removal and receiver arrival receipts for the same attempt, batch, transaction and full AOID/HighId/QL multiset, plus the transferred ledger occurrence IDs. Trade completion or a storage count alone cannot grant recovery.
- The durable dispatch grant exempts only that proven batch from local census admission. The affected worker remains movement-exclusive while awaiting the grant and scan. Census application retains the original batch, both receipts, all physical differences and queued request dispositions; it updates only that worker and removes only the original attempt. It never resends the old manifest from Central. Existing physical storage/return routing resumes after acknowledgement.
- Queued GET requests no longer deadlock worker census. Extraction admission and census reservation use the same withdrawal mutex; already extracting/other active transfer states still exclude census. Affected unstarted requests are retained in history and closed as reconciled without inferring delivery; users can request again. Unrelated requests continue. Dispatch and return preparation exclude a censusing source, including persistence retry windows.
- Static source/API/call-site/project inclusion and whitespace review only. No compilation, test suite or live AO test per owner boundary. OPEN: mixed/partial transfer or missing peer proof, interrupted active withdrawals, receipt mismatch/disconnect/unrelated holds, partial-roster startup and remaining trade-stage IPC. This completes the verified-transfer storage failure path, not the entire runtime recovery project. Service remains stopped; session67 stays active.

## Session 67 — paired physical cancellation recovery

- Dispatch prepares/commands/receipts now share a persisted unique attempt ID. Applied cancellation receipts retain the original manifest after Expected becomes empty, and both bankers exchange their unchanged full-inventory proof over IPC. Proofs bind attempt, batch, transaction, participants and exact AOID/HighId/QL multiplicity. Neither a timeout nor a single participant is sufficient.
- Central retains both proofs and the original failed batch before resolving it. If the bound ledger occurrences and settled live source inventory still contain the manifest, retry uses a fresh batch ID and a fresh attempt ID with destination backoff. Old storage replies and cancellation messages cannot affect that retry. If source custody has changed, the cancelled instruction instead requests Central recensus; there is no invented successful transfer or item loss.
- Queue/completion persistence retries do not recreate an old batch after it has been superseded. Applied receipts remain durable even while IPC acknowledgement is pending; unrelated operations can continue. Worker trade failures now actively decline, with volatile command/batch cleared before decline callbacks to avoid recursively failing the same operation.
- New donation ledger rows now retain HighId and QL and count idempotent imports by the full template/QL. Dispatch sender ledger binding uses that same full key. Retired the normal-mode failed-withdrawal retry that treated cached heartbeat/name matches as physical proof.
- Static source, call-site, project inclusion and whitespace review only; no builds/test suite/live AO test per owner boundary. OPEN: mixed/partial transfer and failed storage recovery, a missing peer/open acknowledgement, interrupted withdrawal paired recovery, receipt mismatch/disconnect/unrelated holds, partial-roster startup, and remaining live trade-stage IPC. This covers paired unchanged cancellation, not general runtime recovery completion. Service remains stopped; session67 active.

## Session 67 — Central recensus and startup withdrawal disposition

- Central now uses scoped local census for eligible idle inventory differences/local extraction failures. Its accounting-only recovery IPC remains available while auditing; no new AO trade starts. The retained plan rebuilds Central's queued dispatch from observed loose items, preserves the original queue, and restores current-generation readiness after durable application. Other workers' storage/ledger changes continue to be merged from current state.
- A source check that fails before issuing a dispatch command/trade records TransferNeverStarted and can trigger Central recensus. The flag is cleared before a real trade attempt; legacy/post-open failures do not acquire it. It permits reconciliation without claiming the peer received anything. Unresolved opened trades still require paired recovery.
- Full startup census now closes prior interrupted withdrawal requests as terminal reconciled records, retaining originals/dispositions in census history and the application bundle. Physically present items return to ordinary storage/routing; absence does not imply delivery. Confirmed delivery records (including DeliveredUtc retained after an archival failure) preserve withdrawal history, and their old ledger IDs cannot be reassigned to similar newly observed items. Startup bundle format is v2.
- Withdrawal admission is serialized with census reservations and requires readiness from this exact process generation; stale readiness from a stopped/disabled prior host cannot admit GET. Central census reserves admission while auditing. Runtime confirmed-delivery accounting retries with monotonic backoff rather than turning persistence errors into extraction work. Terminal stale local withdrawal references are cleared. Cancellation verification waits for the AO trade to close before accepting unchanged inventory.
- Static source/API/call-site and whitespace review only; no compilation or test suite per owner boundary. OPEN: paired runtime recovery for opened/failed dispatches and interrupted withdrawals, mismatched receipt/disconnect/unrelated holds, partial-roster startup, and remaining explicit trade-stage IPC. Current full startup still requires all nine; service remains stopped and this checkpoint is not restart clearance.

## Session 67 — scoped worker recensus and acknowledgement retries

- Added worker-only automatic full recensus after eligible interrupted local extraction/storage and a stable loose-inventory discrepancy. The worker pauses under a versioned hold and a character withdrawal reservation; existing peer trades/failed dispatches and active withdrawals are not discarded. Incomplete scans retain each attempt and retry after 30 seconds. Successful scans include all bags plus loose bank/inventory, not only the triggering item.
- Central receives the complete scan over shared IPC, retains a fixed per-worker application bundle and difference history, replaces only that worker's storage under the shared mutex, and merges only that worker's ledger rows into the latest ledger on every persistence retry. Other bankers' intervening work is preserved. Unknown origin is permitted and ordinary local storage/return routing resumes after acknowledgement. Completed replies are idempotent; the pause token cannot clear an unrelated later hold.
- Withdrawal admission and Manager selection/GET availability exclude a censusing character. Periodic cached-stock ledger synchronization excludes its rows until census application completes. Late extraction proofs cannot apply over an active local census. A successful local scan releases only its own census/extraction reservations; initial full census still resets old process reservations.
- Verified returns no longer disable IPC after the 30-second acknowledgement deadline. They retain transaction ownership and keep polling; return accounting retries without replaying AO movement. Verified extraction accounting also keeps retrying, and recoverable IPC-handler persistence failures reply pending instead of disabling Central's own census.
- Static source/API/project and whitespace review only; no compilation or test suite, per owner boundary. OPEN: Central local recensus/unknown import, disconnected or unrelated holds, unresolved peer/failed dispatch and withdrawal disposition, partial-roster startup, and remaining explicit trade-stage IPC. Worker recensus deliberately cannot supersede those operations. Service remains stopped; session67 stays active and this is not restart clearance.

## Session 67 — verified extraction and withdrawal exclusion

- Added exclusive BankingService extraction from worker/Central bags and loose bank storage. The exact source slot must disappear, the remaining source must match, and normal inventory must gain exactly one matching item. The observed result settles before bank-bag return; return is verified before accounting. Same-slot item requests retry only while both source and inventory remain unchanged.
- Central owns extraction accounting over IPC: remove the verified source occurrence under the runtime storage mutex, rebuild stock, update the same ledger ID to its actual loose slot, and propagate a returned bag's outer-slot remap to remaining ledger rows. Retained evidence and deterministic completion/queue IDs support persistence/acknowledgement retries.
- Central bag topology is now retained in storage-state alongside the eight worker maps, but Central bags are excluded from ordinary current-stock. Central extracts recognized items for onward dispatch; workers extract misplaced/alien bag items for verified return, or own-category loose-bank items for local storage. Central's unfamiliar held items stay in place for review.
- Added recovery item reservations under the existing withdrawal mutex. Admission revalidates current ledger/storage location, preventing a stale GET from taking an item recovery is moving. Manager selection and donor GET availability exclude reservations. A complete startup census preserves and resets superseded reservations. New evidence directories/files are recognized by runtime layout inspection.
- OPEN: automatic local recensus/resumption, runtime unknown/unanchored item import, partial-roster boot and remaining withdrawal/custody coordination. Source review only; owner compilation/live validation pending. Service stays stopped and this remains an intermediate checkpoint.

## Session 67 — verified loose-item returns through IPC

- Added BankingService-owned worker-to-Central return for ledger-anchored loose items routed elsewhere or unknown to the catalog. Withdrawal reservations are excluded. Central reserves capacity over the shared named pipe; only the originating worker and Central participate.
- Sender submits one exact source slot with settling/retry; both parties accept only the exact template/QL and an empty reciprocal offer. Existing physical before/after receipt verification proves removal and arrival independently. Central waits for sender proof via IPC before moving the ledger occurrence and acknowledging completion.
- Original ledger ID, transaction and donor are preserved. Recognized returns enter a deterministic onward dispatch batch; alien returns remain on Central with a review notification. Durable prepared/sent/received/acknowledged/completed records permit duplicate acknowledgement without repeating accounting or queue insertion. Timestamps are audit metadata; leases and delays use Stopwatch.
- Return ownership excludes competing donation, withdrawal, dispatch and storage actions. Notifications cannot invalidate an already-committed return. Delayed Confirm is retained until the local return side has accepted.
- OPEN: bag and loose-bank extraction, runtime unknown/unanchored item import, automatic local recensus/resumption, partial-roster boot and remaining withdrawal/custody coordination. This checkpoint is not runtime completion or restart clearance. Static source review only; owner compilation/live validation pending.

## Session 67 — startup routing and local storage recovery

- Startup dispatch is rebuilt from physically observed Central loose items after census application; the complete prior queue is retained in the application bundle. No missing occurrence is called successfully transferred. Active withdrawal templates are excluded from automatic routing to avoid reallocating ambiguous reserved copies.
- Workers can store their own category's loose items without a new trade. Recovery uses exact census slot anchors or a unique unanchored occurrence, monotonic settling/backoff, and the existing verified storage phases. Local jobs do not overwrite a previous dispatch result awaiting IPC consumption. Storage jobs now retain exclusive movement ownership ahead of withdrawal extraction.
- Retired 17 legacy normal-mode recovery/hold agents plus Central's old bridge queue recovery. Startup discrepancies now flow through physical reconciliation, not permanent comparison holds. Initial startup still requires all nine complete censuses. After release, census holds are per-banker and a worker hold does not clear Central's public readiness marker.
- IPC prepare also checks aggregate storage capacity. released.json is written last, after application and readiness publication; no worker can move items while application remains retryable.
- OPEN: automatic worker-to-Central misplaced/alien returns, extraction from worker/Central bags and loose bank, automatic local recensus/resumption, partial-roster boot, remaining withdrawal/custody reconciliation and live trade-stage IPC. This remains an intermediate implementation; service stays stopped. Owner compilation and live validation pending; no assistant builds/tests.

## Session 67 — combined census application implemented, runtime still incomplete

- Central now waits for nine complete, request-bound census results before applying physical state. The startup gate also requires the completed application marker.
- A retained application bundle includes the original ledger/storage and fixed replacement plan. Retries reuse that plan. Existing unreadable/corrupt records raise a retryable error instead of being treated as empty. Investigation history records observation time, not fabricated loss time.
- Storage/current-stock are rebuilt from the eight storage workers; the ledger includes all observed loose and bagged items across all nine characters. Central bag observations remain in census evidence and the ledger. Unknown-origin items receive stable found transaction IDs without a donor claim.
- Unique bag identities carry location anchors across outer-slot remaps before item matching. High-ID routing fallback and actual loose inventory slot accounting are supported. Removed the automatic old storage-baseline.json import from the banking tick.
- OPEN: replace the remaining global discrepancy holds, resolve prior queue/custody work, implement automatic extraction/return/sorting and retire conflicting legacy recovery actors. Existing holds still apply; this is not restart clearance. No assistant compilation/test suite/live test per owner boundary.
- Historical repair remains owner-confirmed applied. The entire service remains stopped voluntarily; BankersEnabled=false is saved for future use.

# Session 67 continuation — census reconciliation proposals

Owner applied the authorized historical repair and chose to keep the whole service stopped. Added a pure physical-census reconciliation component and per-character durable proposals without introducing another live ledger writer. Complete observations include bag contents and loose bank/inventory; duplicate physical addresses and incomplete reads are rejected. Exact location claims are matched first, then unique compatible remaining occurrences; ambiguous copies get unknown provenance and unmatched old claims remain explicit differences. Optional high-template/QL fields preserve stronger future matching. Removed the legacy fabricated bootstrap-admin donor fallback. Application, automatic routing and replacement of global gates remain unfinished; source review only, no assistant builds/tests.

# City Dwellers — Persistent Project History

## 2026-09-11 — Old donation recovery partition

The owner correctly rejected passive custody containment as completion of the
old donation. Startup recovery now owns the decision after the readiness barrier:
it matches the held expected multiset against transaction-bound current stock
and Central's live loose inventory, then queues the Central-resident portion
under the original donation transaction before admitting new donations.

The retained hold is reduced to genuinely absent occurrences and becomes an
explicit loss incident. The original counts and partition are written to the
ledger, and a recovery-child marker prevents duplicate dispatch after restart.
For batch `801bf00d`, the expected partition is one recoverable Intelligent
Thigh queued to Kbarty and one missing Vital Waist retained as loss evidence.
No absent occurrence is invented, silently discarded, or marked stored.

## 2026-09-11 — Worker fallback remote-Accept latch

The complete local activity log and UTC ledger clarified the repeated internal
trade failure across hosts/timezones. Central opened exact persisted batches;
workers opened the matching commands; and Kbinfa twice observed the remote
`TradeStatus.Accept` event. Nevertheless, the worker compatibility fallback did
not accept, and both clients timed out. Its tick polled `Trade.Status`, which is
the worker's local state and did not preserve Central's remote acceptance.

`[IMPLEMENTED]` An armed worker now latches `TradeStatusChanged(Accept)` for its
matching Central trade. The existing command-bound incomplete-cache fallback
uses that event proof instead of polling local status. Reset paths clear the
latch, and exact command matching, AO Finished, worker inventory receipt, and
physical bag placement remain unchanged.

The final pre-fix Artillery attempt remained open when the host was killed.
After restart, its Intelligent Thigh was loose on Central, but its Vital Waist
was absent from both live normal inventories and from canonical stock under the
donation transaction. This is retained as unresolved custody evidence rather
than misclassified as stored. No assistant-side compilation or live AO test was
run; Kavey owns Release build and live validation.

## 2026-09-11 — Internal AddItem acknowledgement retry

A completed player donation produced a two-item Artillery batch. Kbarty opened
the matching internal trade, but at the 20-second deadline Kbarty reported the
first failure and Central timed out immediately afterward. The recovery bridge
then found both expected items back in Central and requeued them, proving safe
custody and isolating the failure before AO trade completion.

`[ROOT-CAUSE]` Incremental staging repaired burst loss by sending one occurrence
at a time, but each `Trade.AddItem` was still a single-shot request. If AO did
not reflect that request in Central's `PlayerWindowCache`, Central waited forever
for `_outgoingAwaitingOfferCount`; it never added another item or accepted, so
the worker's trusted fallback could not activate either.

`[IMPLEMENTED]` Central now resends only the same pending slot after a 1.2-second
unacknowledged interval, capped at four total attempts. An acknowledgement
clears the pending retry before the next exact occurrence is selected. Existing
multiset validation, timeout, serialization, AO completion, and physical storage
proofs are unchanged. Manager-channel delivery logging now retains diagnostic
message text, ensuring any later internal failure is present in an owner-supplied
console capture. No assistant-side compilation or live AO test was run; Kavey
owns Release build and live validation.

## 2026-09-10 — Authoritative Shade spirit slot catalog

The Spirit stock browser reused symbiant name parsing even though Shade spirit
names describe effects inconsistently, sometimes omit their implant position,
and can contain incidental slot substrings. Live inspection consequently showed
Brain, Leg, and Chest spirits inside the Ear window. Comparing the 654 accepted
AOIDs against the owner-supplied Tinker `items.zip` proved name inference wrong
for 415 entries; for example, Heartsick Spirit of True Seeing contains `ear`
but has Stat `298` wear mask `2`, meaning Eye.

`[IMPLEMENTED]` The shared accepted-item catalog now carries a compact generated
slot entry for every accepted spirit AOID. Its 13 source masks map one-to-one to
Eye, Brain, Ear, Right Arm, Chest, Left Arm, Right Wrist, Waist, Left Wrist,
Right Hand, Thigh, Left Hand, and Feet. Stock filtering and persistent index
updates resolve Spirit slots by AOID, while the existing name parser remains
limited to symbiants. Missing custom Spirit mappings fail closed instead of
guessing from a name. Static data validation confirmed 654 unique sorted AOIDs,
654 generated slot entries, and the expected per-slot counts. No assistant-side
compilation or live AO test was run; Kavey owns the Release build and live stock
window validation.

This is a compact chronological engineering log. It records decisions and verified outcomes that future sessions may need in order to understand why the current code looks the way it does.

## 2026-09-10 — session 53: direct donor progress tells

The shared Banker tell adapter intentionally routes messages addressed to the
bootstrap administrator into Apcmanager's guest channel for operational
visibility. That broad exception also caught player-facing donation progress
when Kavem was the donor, exposing repetitive trade UX to admins and hiding it
from the donor's tell window.

Donation-partner messages now explicitly use the ordinary rotating direct-tell
queue; operational Central/storage notices keep their Manager-channel route.
The former item explanation plus separate trade-count message is one compact
colored line containing trade position, store/delete/reject disposition,
clickable item, QL, projected copy count, and destination banker.

## 2026-09-10 — sessions 45–47: Flipper regression rollback

Five consecutive normal-client Apcflipper logins succeeded, while the
clientless path reproducibly disconnected during zoning and left AO's short
crashed-session lock. The login-only command reproduced without starting
Manager, Buddies, or Bankers, excluding their active worker threads from that
specific attempt but not excluding the unified executable's shared runtime and
assembly context.

Process isolation reproduced the same failure and was therefore removed. A
controlled old-binary test then succeeded against the exact endpoint rejected
by the current binaries, proving both the endpoint theory and the isolation
hypothesis wrong. Flipper host and plugin code were restored to live-proven
commit `548e37a2f126684f58f97e571f331bdc4805dea5`; later Banker functionality was
retained.

Session 48 combined the byte-identical live-proven Flipper host/plugin with a
standalone `Flipper.exe` process boundary. Later evidence showed the incident
was external: the affected host/IP recovered without a code change. Session 52
therefore removed that diagnostic-era process wrapper and returned the same
known-good Flipper lifecycle to the unified host's parallel component runner.
Current Banker and give-item functionality remains intact.

## 2026-09-10 — session 38: per-role Banker credentials

The owner's AOQuickLauncher batches proved that the nine-bank network does not
use one universal password. Extended both normal hosting and the explicit bag
audit so an individual role's `Password` overrides the top-level shared
fallback. Configuration validation now rejects a role only when neither source
provides a real password. Credentials remain private deployment data and are
never committed.

## 2026-09-09 — session 37: Banker expansion compile repair

The first owner Release build exposed two mechanical settings-context mistakes:
the static current-stock builder had no `settingsDir` parameter, and the static
loose-inventory census method referenced its agent's `_settingsDir`. Threaded the
settings directory through every stock-builder caller and made census capture an
instance method. Also corrected its surviving six-client publication threshold
to require all nine configured banker censuses. No runtime behavior was tested by
the assistant; owner rebuild remains authoritative.

## 2026-09-09 — session 36: spirit, dyna and phatz bank expansion

Expanded the unified host from six bankers to nine. Kbspirit, Kbdyna and
Kbphatz now participate in startup, audit/baseline/readiness, health/status,
tell sending, dispatch/storage, ledger, stock and withdrawals. Startup requires
all nine mappings, and a fresh audit must establish all eight worker layouts.

Generalized the compiled symbiant gate into `Bankers.AcceptancePolicy` inside
the existing single runtime JSON. Built-in routes remain deterministic while
admins can override or add any AOID with a destination role and retained-copy
limit. Negative one is never-delete, zero disables, and omitted custom limits
default to never-delete. Policy errors fail before accepting items; configured
routes without configured workers are rejected safely.

Generated and independently counted 654 standard Shade-spirit AOIDs from the
bundled AOSharp item database, exactly matching the owner's supplied tier
census; retention defaults to five. Seeded 418 dyna nano/disc AOIDs from
Nadybot's maintained location and disc mappings at commit
`de9e3b2c8d2f91df87c614a3d9f91bc16c2eacf2`, then verified every AOID exists
in bundled AOSharp data. Dyna is keep-all except Frenzy of Fur crystal/disc at
three each; Grid Armor IV remains keep-all. Phatz is deliberately empty until
the owner supplies trusted AOIDs.

Stock navigation gained Spirit, Dyna/Nano and Phatz families grouped by QL.
Capacity output now uses finite policy demand and explicitly marks unbounded
roles; status warns about theoretical over-capacity. Source/static validation
only; owner build, configuration update, fresh bagaudit and live trade tests
remain pending.

## 2026-09-09 — session 35: four pickup orders and donation coexistence

Implemented a revision-checked withdrawal queue with legacy single-item loading,
four canonical-member orders, three items per order, and shared ready-item
deadlines refreshed by additions and arrivals. Central collects ready subsets
without waiting for every requested item. Waiting reservations no longer block
donations; actual extraction and AO trades remain serialized.

Reservation-aware inventory selection prevents ordinary dispatch or deletion
from consuming pickup copies. The donation handshake recognizes a ready collector
before callback ordering can misclassify a pickup. Worker dispatch fallback
does not arm during extraction. AO Finished persists all delivered pickup IDs
before per-item archival. Expiry queues deterministic return batches.

Replaced independent retries of arbitrary failed withdrawals with one persisted,
Central-scheduled retry restricted to inventory-extraction timeout. Ambiguous
custody and delivery/accounting failures stay held rather than being replayed.
Status and help describe occupied orders, ready/held items, limits and partial
pickup. Source/diff review only; owner Release build and live AO validation remain
outstanding. No runtime was started and no owner data was rewritten by the session.

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

## 2026-09-01 — Two-repository single-writer coordination

`[CORRECTION]` An earlier Session #5 coordination commit misread “same repo” and
temporarily treated City Dwellers and CityBankers as projects inside one Git
repository. Kavey corrected the boundary: they are separate sibling
repositories, `axlslak/citydwellers` on `master` and `axlslak/citybankers` on
`main`. The temporary CityBankers recovery card and checkpoint directory in
City Dwellers were invalid and have been removed.

`[DECISION]` City Dwellers Chat and Work modes share this repository's cursor
and journal. CityBankers retains its own recovery key, cursor, journal, state,
history, and encrypted-memory lineage in its own repository. Kavey guarantees
that only one GPT session writes across both repositories at a time.

`[DECISION]` Validated generic ideas may cross the sibling boundary, but the
receiving repository records the adoption and its compatibility evidence. No
application code or project-specific state is shared merely because the
projects are related.

## 2026-09-02 — Grid endpoint replay replaced with bounded trigger traversal

Kavey returned the monitored Apcr20000 JSONL requested by the previous cursor.
The 424-event trace was contiguous. ICC static-terminal entry succeeded on the
first attempt, Grid navmesh staging succeeded, and every restored full-client
tail command received its corresponding local echo. The character nevertheless
remained at the exact exit in model `152` until the 20-second timeout.

`[CORRECTION]` Adding the four previously omitted turn actions was insufficient.
The failed trace reproduced their captured positions and headings, so repeating
the same sparse endpoint tail cannot distinguish the next hypothesis. The
clientless session also showed no changing observed position during the
uninterrupted two-metre `ForwardStart` leg; its first changed assertion was the
near-exit action 1.2 seconds later.

`[IMPLEMENTED]` The final Grid leg now adds five bounded `Update` assertions,
scheduled every 200 ms and linearly spaced from the exact staging point toward
the captured near-exit point. This gives the server intermediate traversal
samples without changing the general walker or introducing a character-wide
speed constant. After those samples, the exact captured near-exit
`TurnLeftMouse`, exit `ForwardStop`, post-stop turns, headings, relative
delays, deterministic retry state, and 20-second Serenity wait remain intact.

No raw owner trace was committed. No assistant-side build or AO runtime test
was run. Kavey owns the confirming build and monitored test.


## 2026-09-06 — Manager status, help blobs, and explicit diagnostics

After a long-running Apcmanager instance stopped answering organization chat
while tells and its guest channel still worked, a reset restored organization
replies. No conclusive console or log evidence identified the cause. This
failure shape is consistent with a channel-specific receive/dispatch state
problem rather than total Manager death, but that remains a hypothesis until a
recurrence is captured.

[IMPLEMENTED] Published code through commit:

29113bf0c7b530093166e15cc8b91b94d87534b3 — Keep argument syntax illustrative in help

Manager now tracks monotonic uptime and its UTC start time. status is a colored,
paged AO blob containing Manager state, cloak and recent history, raid recovery,
Flipper, Buddies and Buddy activity, active raid work, alts, and roster
freshness. The compact outer line still exposes immediate online/uptime health.

The plaintext help dump was replaced with a topic-oriented AO manual inspired
by the durable interaction patterns used by Nadybot, BeBot, and Tyrbot:
clickable overview and topic links, command-list and syntax pages, focused
command explanations, and access-aware administrator sections. Blob pagination
has independently controlled limits for organization, guest, and tell output.

The guest diagnostic channel no longer replays buffered telemetry when it is
confirmed. New events are timestamped to a rotating disk log and reported live
in concise colored lines. The administrator-only dump command writes a
timestamped diagnostic snapshot with current Manager state and retained log
content. Detailed position telemetry is kept for that file instead of being
sprayed into guest chat.

[OPEN] Kavey owns the C# 7.3 build and live AO verification across tell,
organization, and guest channels. No assistant-side build, test suite, or live
AO runtime test was run.


## 2026-09-06 — Manager presentation C# compatibility correction

Kavey's Release build compiled Manager.exe, Flipper, Buddies, CityFlipper, and
CityBuddies. CityManager failed on five errors in the newly added presentation
partial: the target framework has no IndexOf(char, StringComparison) overload,
and C# using directives do not carry across partial-class source files, leaving
four Logger references unresolved.

[IMPLEMENTED] Commit e6ec02f57cd2105ab7016eba0d199ea4b2c256e9
adds the AOSharp.Clientless.Logging import and uses IndexOf(char). These are
compile-only corrections with no intended runtime behavior change. The CS0649
and obsolete-API messages in the supplied build output are warnings and were
not changed. Kavey owns the confirming rebuild.


## 2026-09-06 — Organization-output failure isolated and bypassed

A supplied console capture established that Apcmanager continued receiving
organization commands after AOSharp repeatedly reported it could not obtain
the LocalPlayer organization stat. The first failure appeared immediately
after login-time packet deserialization and duplicate-dynel exceptions.
Client.SendOrgMessage logged the missing-stat failure internally but returned
without throwing, while Manager's wrapper incorrectly logged a successful
send.

[IMPLEMENTED] Published code through commit:

3bf8f8ce8961cc28501d7f7f2702e3e64bc9ec72 — Try every direct group-send overload

Manager now remembers the concrete organization channel observed on incoming
traffic and uses reflection to invoke the compatible public
Chat.SendGroupMessage overload directly. This avoids depending on the damaged
LocalPlayer organization stat while remaining compatible with the pinned
AOSharp.Clientless API. If no direct route works, the command issuer receives
the reply in a tell and org-output health becomes degraded in status and dump.

A raid interface retains its origin by design. This explained why raid retries
from tell and guest also looked silent: their output continued targeting the
broken org origin. An owner retry from tell or guest now migrates a degraded
org-origin raid to that current route.

The administrator-only restart command acknowledges first, saves state, starts
a delayed replacement of the current Manager executable, and exits the old
process. In-game help and the command list document restart; status and dumps
document org-output health. Kavey owns the build and live verification.


## 2026-09-06 — Direct organization packet route corrected

The first org-resilience runtime test behaved safely but proved
ChatClient.SendGroupMessage is not public in AOSharp.Clientless 1.0.16. Manager
marked organization output degraded and delivered the status privately as
designed.

Inspection of the exact pinned NuGet assembly resolved the API uncertainty.
Client.SendOrgMessage reads LocalPlayer stat 5 and, if present, constructs a
GroupMsgMessage containing GroupMessageType.Org, the integer channel ID, and
the text, then passes it to public Client.Send. Published commit
418abc49ac88f4ff794aac6d14a654853435bd68 now performs that same packet send
with the channel ID learned from incoming org messages, skipping only the
failed LocalPlayer-stat lookup. The speculative reflection path was removed.
Private fallback, status/dump health, raid migration, and admin restart remain
unchanged. Kavey owns the confirming build and live org test.


## 2026-09-07 — Child readiness and clock-independent Flipper freshness

Live raid preflight reported zero of twelve Buddies started and terminal
timeouts for indexes `0..12`; Flipper raid watch also failed to produce a
result. A later `#cloak` continued to show the same 12.1% cached charge as
“last verified just now” despite hours and service resets. The cached
observation itself was several hours ahead of the raid's explicit UTC times.

`[DIAGNOSIS]` Two independent false-fresh/readiness hazards were present.
CityBuddies wrote its host readiness marker only from the plugin's
`CharInPlay` message handler even though its update loop already read
`Client.InPlay`. CityFlipper did not subscribe to updates until that same
packet handler ran. Separately, Flipper considered any negative UTC cache age
fresh, so a VM clock corrected after writing a future-dated record could keep
that record fresh for hours and prevent live probes.

`[IMPLEMENTED]` Both child plugins now use `Client.InPlay` as a fallback
readiness signal while retaining packet-based detection. Flipper cache
freshness and live shield countdown adjustment use process-monotonic elapsed
time. A persisted record has no monotonic freshness anchor after restart and
therefore cannot suppress a live probe; it remains conservative historical
fallback data. Future-dated observations are rejected by Flipper and Manager,
and the UI no longer converts negative age to “just now.”

`[CORRECTION]` Indexes `0..12` do not prove a 13-for-12 counting error. The
configuration deliberately provides thirteen accounts with a twelve-character
raid limit; index 12 is spare capacity and was attempted after all preceding
startups appeared to fail. That retry behavior is preserved.

Kavey owns the build and live verification. No assistant-side build or AO
runtime test was run.


## 2026-09-07 — UTC contract and clock-domain hardening

The poisoned Flipper cache showed `2026-09-07T10:15:36Z` while the same
operation's measured time was about `02:35Z`. Session 11 prevented that record
from being considered fresh, but the wider audit found another machine-zone
hazard: JSON timestamps whose fields were named `Utc` could deserialize with
`DateTimeKind.Unspecified`. Calling `ToUniversalTime()` then interpreted the
value in the current Windows timezone, so moving a settings directory between
Romania, UTC, and a `-07:00` VM could change the represented instant.

`[IMPLEMENTED]` All projects now compile a shared UTC normalizer. Explicit UTC
and local values retain correct round-trip behavior; offset-less persisted
`...Utc` values are treated as UTC by contract rather than as machine-local
time. Manager applies this to cloak state/events, Flipper responses,
membership freshness, alt freshness, Buddy snapshots, raid cooldowns, and
restored raid state. Materially future freshness is discarded. A future
Flipper cache is automatically moved aside under an `.invalid-clock-*` name.

`[IMPLEMENTED]` Process-local durations no longer depend on wall-clock UTC in
the critical paths audited: Flipper result waits, Buddies home waits and
leases, cleanup eligibility, navigation timeout, Manager home monitoring,
guest ID lookup, and org-rank lookup/cache expiry use monotonic stopwatch
time. Alt, membership, and raid scheduler gates detect implausible future
pacing after a backward VM clock correction and re-evaluate instead of
remaining blocked for hours. Implausibly future persisted raid anchors are
rejected and non-active raid deadlines beyond ten minutes are expired safely.

This transaction does not change the fixed 30-second cloak recovery retry
policy. Console timestamps continue to include their explicit numeric local
offset for correlation. Kavey owns build and live verification; no
assistant-side build or AO runtime test was run.


## 2026-09-08 — Portable runtime root and settings/data boundary

Kavey needs City Dwellers to survive unreliable staging machines. Binaries,
administrator configuration, and bot state therefore need one durable,
relocatable runtime rather than a repository-bound `bin\Release` plus a
separate repository `settings` directory.

`[IMPLEMENTED]` Every executable and plugin project now emits directly into
the repository-relative `release` or `debug` directory. The generic `bin`
ignore was removed and explicit portable runtime paths remain ignored.

`[DECISION]` The executable directory is the settings location. At that stage,
`manager.json`, `flipper.json`, and `buddies.json` were the
administrator-supplied component settings files beside the executables. Session
22 later superseded them with one `citydwellers.json`.
Bot-owned mutable files now live beneath `data`, including lists, caches,
cloak/raid/membership state, coordination markers, diagnostics, dumps, and
navigation traces. `GameData` and `NavMeshes` remain static runtime assets.

`[IMPLEMENTED]` A conservative first-run bridge copies the old repository
`settings` content and mutable files from old `bin` outputs into the new
layout. It never overwrites a new destination and never deletes legacy files.
After migration, runtime path resolution uses only the executable directory
and is independent of Git.

`[OPERATIONAL]` Windows mapped drive letters are scoped to logon sessions and
must not be assumed visible to a service. A network-backed service deployment
needs a UNC-capable directory symbolic link and a service identity with share
permissions; this constraint belongs to the upcoming unified-host/service
design.

No assistant-side build or AO runtime test was run. Kavey owns the Visual
Studio Release build and migration verification.


## 2026-09-08 — One executable, Windows service, and trusted-time startup

The three-console deployment was replaced by one `CityDwellers.exe`. The old
Manager, Flipper, and Buddies host source remains recognizable but is compiled
into one supervised process alongside the three plugin DLLs. Separate host
project definitions and executable configuration files were removed. Release
and Debug still produce the portable runtime roots established in session 17.

The process starts Flipper and Buddies as idle request services, then Manager
as the persistent AO client. It owns coordinated shutdown and converts an
unexpected component exit into a process failure so Service Control Manager
can restart it. The administrator restart operation now hands a request to the
coordinator, which gracefully recycles Manager only; the earlier PowerShell
self-launch and whole-process exit design is superseded.

`CityDwellers.exe install-service` creates a delayed-automatic Windows service
with TCP/IP and Workstation dependencies and restart-on-failure actions. The
service runs without a desktop, writes combined output to a rotating data log,
and requests enough stop time for AO client-domain cleanup. Network-backed
storage requires a UNC-resolvable directory link and a service account with
permissions on the share.

Before AO code starts, the host proves the data directory writable and queries
administrator-selected NTP servers directly. It compares independent UTC to
the system clock, requests Windows Time rediscovery/resynchronization when
needed, and retries using a monotonic clock. Until trust is established, log
entries carry only monotonic uptime. A broken or blocked time source leaves the
host alive and visibly waiting rather than starting AO with false time; bypass
requires an explicit administrator setting.

No assistant-side compilation, Windows service execution, or AO test was run.
Kavey owns the authoritative Visual Studio Release build and deployment tests.


## 2026-09-08 — Non-destructive runtime inventory

The first portable deployment was populated conservatively by copying more
legacy content than City Dwellers needs into both settings and data. Startup
now reports that ambiguity instead of silently ignoring it. The unified host
classifies its administrator settings, runtime artifacts, static directories,
mutable data, transient files, preserved invalid records, traces, and dumps.
Known entries found on the wrong side say where they belong; all other entries
are explicitly reported as unused.

The requested policy was revised from deletion to warnings before
implementation. No file is opened for selection, deleted, moved, or
quarantined by the inventory check. The documented location remains the only
location each subsystem reads.


## 2026-09-08 — AOSharp child AppDomain path correction

The first unified-host live run proved that process consolidation alone did
not make `AppDomain.CurrentDomain.BaseDirectory` uniform. The coordinator used
`release\data`, while CityManager explicitly logged the legacy repository
`settings` path. CityFlipper repeatedly completed a controller observation,
but its producer wrote through the child-domain path and the loader timed out
waiting through the host path.

The host now establishes the executable's absolute runtime root in
process-scoped state before it starts Flipper, Buddies, or Manager. Every copy
of the shared path resolver checks that state, so all child domains agree on
the same settings and data directories regardless of plugin/AppDomain base.
No retry interval or operation timeout changed.


## 2026-09-08 — Plugin paths removed from administrator settings

The mixed deployment retained explicit Manager and Flipper plugin paths while
Buddies already had an implied default. Under a single fixed runtime those
paths were unnecessary and could continue loading stale DLLs from the old
repository `settings` directory.

All three components now imply exactly one sibling DLL and ignore legacy
`Plugins` JSON properties. New templates no longer emit the property. This
closes the configuration path that allowed a current unified host to load old
plugin code with obsolete path behavior.


## 2026-09-08 — One settings file for the unified executable

City Dwellers now has one administrator configuration surface. The existing
trusted-time settings and the Manager, Flipper, and Buddies settings are all
top-level parts of `citydwellers.json`. Every component reads only its own
section from that file. The three old component JSON files are ignored and
reported as obsolete, preventing copied credentials or stale values from
silently competing with the active configuration.

The owner's supplied component files were merged into a private, untracked
`citydwellers.json` handoff. No usernames or passwords were added to Git.


## 2026-09-08 — CityBankers imported into the unified host

Kavey established the new product boundary: CityBankers is a City Dwellers
subsystem. The projects remain different in-game characters with different
functions, but they no longer require separate executables or administrator
configuration files.

`[IMPLEMENTED]` The current compiled CityBankers runtime from sibling
`main` at `eadf5a3dce028ba83f3930ce83f96b2e41f91137` was imported. The
solution now builds `CityBankers.dll` beside the existing three plugins.
`CityDwellers.exe` supervises the six banker client domains as a fourth
component while preserving Central-first startup, banker readiness barriers,
trade/storage behavior, and physical-state authority. No `Banker.exe` project
or output was imported.

`[IMPLEMENTED]` The sole `citydwellers.json` gained a required `Bankers`
section using the former shared-password/six-role schema. The private handoff
uses Kavey's supplied mappings, including the corrected Extermination login
username, without committing credentials.

`[IMPLEMENTED]` Imported banker code reads no `banker.json`. All banker
mutable state and coordination artifacts resolve beneath the unified
executable-adjacent `data` directory. The explicit physical audit is retained
as `CityDwellers.exe bankers-bagaudit` and remains behind trusted-time
readiness.

`[DEFERRED]` Shared administrator, member, and alt semantics are intentionally
not redesigned in this import. The first boundary is executable, lifecycle,
configuration, and portable state location; functional sharing follows after
Kavey's next requirements.

No assistant-side compilation or live AO runtime test was run. Kavey owns the
authoritative Release build and live one-process validation.


## 2026-09-08 — Manager-coordinated outbound tell queue

The always-online bankers make outbound chat capacity a fleet resource rather
than a property of Kbcentral alone. All Manager and CityBankers application
tells now enter one durable, sequence-ordered queue. The first configured
Manager character schedules one message at a time to a configured client that
is in play, has a fresh heartbeat, is outside an AO trade, and has satisfied
its own pacing interval. Sender acknowledgement releases the next job, stale
assignments are recovered, and repeated failures are preserved for diagnosis.

Ordinary messages can rotate through Apcmanager and all six bankers. The alt
lookup remains pinned to Apcmanager because the external bot replies to the
character that asked; its 30-second response deadline begins only after the
queued send is acknowledged. Flipper and Buddies remain outside this pool
because their idle lifecycle has no logged-in AO character.

No assistant-side compilation or live AO test was run. Kavey owns the
authoritative Release build and live chat-rate verification.


## 2026-09-09 — Banker shared-state collision and partial-placement recovery

The first sustained unified live run proved both the imported baseline and a
previously hidden file-sharing race. Kbexte placed an extermination item into
its AO storage bag, then failed while replacing `current-stock.json` because a
concurrent stock reader held the destination file. The worker stopped before
moving another item, and Central correctly retained the batch as a failed
post-transfer hold.

The runtime-state store now gives readers and atomic writers the same
path-specific named mutex. Replacement retries transient Windows or network
share access failures for up to five seconds; failure preserves the prior good
file instead of deleting it as an immediate fallback.

The existing startup write-front pass already proves and imports physical bag
contents before dispatch readiness. After that proof, worker-local recovery may
now handle the exact placement-then-persistence failure only when every
original transfer item has a distinct usable AO identity and those identities
partition exactly between persisted storage on the destination worker and
loose destination-worker inventory. It resumes only the loose subset, carries
forward the already-stored count, and emits full-batch success only after all
original identities are durably accounted for. Central's existing exact worker
success reconciler remains the only authority that removes the failed queue
entry. Any missing, duplicated, or cross-location identity leaves the batch on
hold.

No assistant-side compilation or live AO test was run. Kavey owns the Release
build and live recovery of the held extermination batch.


## 2026-09-09 — Transaction-bound Banker proof and Flipper lifecycle repair

The first partial-placement recovery build correctly refused to act on held
extermination batch `07c904d1`: at least one queued occurrence lacked a
distinct usable AO identity. No item moved. This established that identity-only
proof was too strict for the imported trade snapshot format, while its refusal
behavior remained safe.

The replacement proof uses evidence already committed before the original
`current-stock.json` failure. Each successful `storage-state.json` placement
retains the dispatch transaction id. Recovery selects only persisted
occurrences on the intended destination worker with that exact transaction,
subtracts them from the original expected batch, and then requires the entire
remaining multiset to be loose on the same worker. Usable identities are
matched exactly. Occurrences without one use AO id, high id, and QL with list
removal preserving multiplicity. Only the proven loose remainder is placed;
the final worker result carries the complete original count for Central's
existing exact-success reconciler.

The same live restart exposed an independent Flipper lifecycle defect. A probe
timed out and unloaded locally while AO retained Apcflipper's session, so a
later login received `AlreadyLoggedIn`. A request already accepted by the pipe
could also proceed through domain creation and call `Start` after unified-host
shutdown began. Flipper now observes shutdown before accepting work, again
after plugin loading, and throughout result waiting. Shutdown waits briefly for
an active probe to unload. Any unsuccessful probe that actually started an AO
client begins a 90-second monotonic cooldown; retry requests during that window
return without creating a client. This gives AO time to release the character
without relying on the untrusted wall clock or changing Manager's recovery
cadence.

No assistant-side compilation or live AO test was run. Kavey owns the Release
build and the next stopped-host validation.

## 2026-09-09 — Recovery idempotence and Flipper cache trust

Kavey's next live run proved the transaction-bound Banker repair: the held
extermination batch was reconciled as one already-persisted item and no loose
remainder, emitted exact full-batch success, and was removed by Central's
worker-success reconciler. The worker could nevertheless repeat that zero-item
completion while the failed queue entry remained briefly visible. Partial
placement recovery now treats an already-published, exact successful result as
terminal and waits for Central to remove the queue entry. A same-batch success
with mismatched role, character, or counts blocks instead of being trusted.

The same run proved Flipper's failed-probe cooldown but exposed a trust-boundary
error: an old `Flipper.Cache` observation shown after a zoning disconnect could
mark Manager's pending cloak recovery complete and cancel further live retries.
Cached observations may still support status display, but can no longer settle
pending recovery. Fresh probes, live events, and trigger-qualified ensure
responses retain their existing authority.

No assistant-side compilation or live AO test was run. Kavey owns the Release
build and the next live validation after the cooldown.

## 2026-09-09 — One public command identity

The CityBankers import temporarily left two public vocabularies: Apcmanager
owned City Dwellers commands while Kbcentral parsed stock and donor tells and
generated navigation links back to itself. That also bypassed Manager's
canonical admin/member/alt authorization for Banker information.

Apcmanager now owns the only stock and donor information processor. The
existing stock engine is linked into CityManager and reads the Bankers' live
state directly; its chatcmd tree targets the configured Manager character with
`#stock`. `#donor` reads distinct active provenance from the same ledger.
Organization requests are AP-member requests by channel origin, while guest
and tell requests must pass Manager's canonical membership and alt resolution.
Kbcentral redirects the obsolete public tell forms but keeps physical trading
and private operator functions.

Banker operational messages previously queued as tells to Kavem now use a
separate durable Manager-channel lane beneath the existing tell-queue data
root. Cross-AppDomain producers allocate sequence numbers under a named mutex;
Apcmanager sends the messages in order to its confirmed guest channel and only
removes each job after the AO send call succeeds. The ordinary rotating tell
pool remains unchanged for player-directed replies.

No assistant-side compilation or live AO test was run. Kavey owns the Release
build and org/guest/tell validation.

Kavey's first Release build compiled CityDwellers, CityBankers, CityFlipper,
and CityBuddies, but CityManager failed because its new Banker partial omitted
the AOSharp.Clientless namespace needed to resolve `Client.CharacterName`.
The partial now imports that namespace; no command, authorization, queue, or
runtime behavior changed.

## 2026-09-09 — Donor history windows

The initial unified `#donor` command counted only distinct donors represented
by items still in the active ledger. It could not answer who contributed the
most, show recent donated items, or preserve credit after an item left stock.

Manager now builds one all-time donation view by combining active
`ledger.json` items with the copied item records in monthly
`history/history-*.jsonl` archives. Stable ledger item IDs remove any overlap.
Symbiant index metadata restores item names, QLs, and AO item links. Donor
names are canonicalized through the current Manager alt graph at query time,
so donations made across known alts aggregate under the main.

`#donor top` presents the ranked all-time leaderboard, `#donor last` presents
the latest 25 item receipts with absolute UTC time and donor, and
`#donor <member>` presents the canonical member's all-time total plus latest
10 items. Bare `#donor` opens a navigation overview. All views use Manager's
existing channel-aware links, blob pagination, colors, and AP-members-only
authorization.

No assistant-side compilation or live AO test was run. Kavey owns the Release
build and command presentation validation.

## 2026-09-09 — Member withdrawals through Kbcentral

CityBankers previously accepted donations and answered stock questions but had
no safe path for an AP member to take an item back out. Apcmanager now accepts
`#get <AOID>` and `#withdraw <AOID>`, resolves the requester through the shared
member/alt policy, and durably reserves one exact active-ledger and audited
physical location. Duplicate AO item IDs remain interchangeable publicly, while
the internal transaction follows the chosen copy's ledger ID, character, bag,
inner slot, and usable AO identity.

The source worker uses the proven audited-bag extraction sequence and returns
the reserved item to Kbcentral. Kbcentral starts a three-minute pickup window
only after physical receipt. A completed AO player trade archives the exact
active item with reason `withdrawn`, canonical recipient, and UTC departure
time. A timeout queues the still-owned item through the existing serialized
Central-to-worker storage path; it is marked expired only after that batch has
left the queue and physical stock shows the item stored again. Failure states
remain blocking holds so neither accounting nor physical custody is guessed.

Stock item views and active donation rows now display GET links aimed at
Apcmanager; departed history has no GET action. No assistant-side compilation
or live AO trade was run. Kavey owns the Release build and staged live test.

## 2026-09-09 — Restart-orphaned dispatch trading state

A dispatch could persist a batch as `trading` and then lose Central's in-memory
owner during a host restart. Normal dispatch selects only `queued`, while the
post-login reconciler previously selected only `failed`, leaving that batch to
block player trades indefinitely. Live evidence showed artillery batch
`68b9ee5d` in this exact gap after five attempts.

Post-login reconciliation now recognizes a `trading` batch only when its update
predates the current process. It still requires every expected item loose on
Central and rejects any same-batch worker receipt/storage evidence. Only with
that proof does it clear stale same-batch sidecars and requeue normally. A trade
started by the current process cannot match the restart-orphan predicate. No
assistant-side build or live AO test was run; Kavey owns the controlled restart.

## 2026-09-10 — Family stock commands, Phatz policy, and occupancy status

The single overloaded `#stock [family ...]` tree obscured the new Banker
modules and made Phatz additions depend on editing private configuration.
`#stock` now presents the full module overview, while `#symb`, `#spirit`,
`#dyna`, and `#phatz` own their respective search trees. Spirit deliberately
uses the symbiant-style slot and target-QL flow; Dyna covers both nano crystals
and instruction discs.

Phatz acceptance can now be administered in game from an actual AO item link.
The runtime overlay records link IDs, QL, display name, copy limit, actor, and
time beneath `data`; configured items remain bootstrap inputs and can be
suppressed through durable AOID tombstones. Every acceptance lookup observes
the overlay, so Kbcentral routes a newly added item to Kbphatz without source or
private-config edits.

The member-facing status window now gives every configured banker a distinct,
color-coded inventory occupancy line: consumed slots, total audited slots,
percentage used, and remaining free slots. Existing online/readiness and work
diagnostics remain separate. No assistant-side compilation or live AO test was
run; Kavey owns the Release build and AO validation.

## 2026-09-10 — City office building bank terminal fallback

Moving the banker clients into the city office building exposed a visibility
gap: the in-game Info Manager saw a real Rubi-Ka bank terminal, but AOSharp's
clientless dynel manager returned zero static dynels. The existing diagnostic
therefore never attempted to open the bank, leaving every storage-dependent
agent correctly blocked behind `Inventory.Bank.IsOpen`.

The ordinary name-based `StaticDynel.Use()` route remains first choice. When it
has no candidate, bankers in playfield model `6152312` and within eight metres
of the verified terminal position may send the same low-level GenericCmd Use
shape already proven by CityFlipper, targeted at terminal identity instance
`1478048485`. This is deliberately an evidence-specific bridge rather than a
general interaction guess. The existing one-attempt and eight-second bank-open
confirmation behavior remains intact. No assistant-side compilation or live
AO test was run; Kavey owns the Release build and terminal validation.

## 2026-09-10 — Automatic startup storage enrollment

Once the city office terminal opened successfully for all nine bankers, normal
startup still remained blocked because the three new workers were absent from
`storage-state.json`. Although each client could see its own 120 bags, the
previous recovery boundary required an operator to stop normal service and run
a separate full `bankers-bagaudit` process.

Normal startup can now repair that authority gap itself. Central waits for
fresh bank-open diagnostics from every banker, allows the ordinary live layout
agents to settle, and then audits only workers whose maps are missing, empty,
or rejected by current live reconciliation. Audits are sequential across
workers and staged one bag at a time. Readiness has an explicit enrollment hold
and cannot release trades or dispatch during the operation.

Only a complete audit with every bag opened and every bank bag verified
returned may be merged. The merge runs under both the live-layout and canonical
runtime-state locks, replaces only the affected worker, preserves every other
worker and the ledger, and carries forward known item transaction provenance
by unique identity. Failure archives the evidence, stops before another bag is
touched, and leaves readiness closed. Manual full audit mode remains available
for deliberate operator diagnostics. No assistant-side compilation or live AO
test was run; Kavey owns the Release build and sequential enrollment test.
## 2026-09-10 — Bounded Flipper zoning reconnect

The first normal-startup enrollment run proved that automatic Banker recovery
and Flipper execute independently. Spirit, Dyna, and Phatz each audited all
120 bags and returned all 102 staged bank bags. During the same run,
Apcflipper authenticated but twice transitioned directly from zoning to
disconnected; the attempt between them received `AlreadyLoggedIn` from the
abandoned session. No Banker failure stopped or delayed the Flipper service.

The Flipper plugin was the only child plugin without AOSharp AutoReconnect.
It now permits one reconnect only when no cloak action has been sent, discards
partial observations from the abandoned session, and disables reconnect after
a second disconnect or any post-action disconnect. The loader's minimum probe
window is 45 seconds so this recovery can complete, while the established
90-second cooldown still follows a failed started probe. Cloak safety and
Banker readiness boundaries are unchanged.

Live validation disproved that approach. AO retained the first disconnected
session, so AOSharp's ten-second same-domain reconnect received
`AlreadyLoggedIn`; changing AutoReconnect from inside that callback also
collided with AOSharp's internal Stop transition. The reconnect and 45-second
minimum were removed. A disconnect now publishes a specifically failed result
after handler detachment, which causes prompt host unload and the existing
90-second cooldown without caching an unknown observation. This limits the
damage and preserves the original failure for diagnosis; it does not claim to
repair the underlying server-side zoning disconnect.

Because normal full-client login remained healthy while the probe itself
created a half-open server session during zoning, an explicit
`flipper-login-test` diagnostic now separates AOSharp/ClientDomain login from
CityFlipper's operational behavior. It loads the same account and assembly but
ignores all packets except the local character's `CharInPlay`, sends no game
action, and reports only login success or disconnect failure. A passing result
is deliberately excluded from cloak cache because it contains no city-state
observation.

## 2026-09-10 — Live banker inventory and withdrawal recovery

A live extermination withdrawal proved that AO had moved the exact reserved
item out of its audited bag while the withdrawal matcher continued waiting for
normal inventory. The worker timed out, and Central's one automatic replay
then checked the old inner slot and stopped because that slot was correctly
empty. Manual inspection found the item loose in Kbexte inventory, establishing
that physical extraction succeeded and observation—not custody—failed.

Banker health snapshots now publish the complete ordinary inventory census.
The administrator-only `inventory`/`inv` command shows every banker's used,
free, and loose counts, then opens a per-role or per-character slot list with
clickable AO items. This makes clientless inventory directly observable without
stopping the fleet.

Withdrawal extraction now records the pre-move inventory slots, retains the
post-move live identity once recognized, and uses a unique exact-name/new-slot
fallback only after exact identity and template matching fail. Central may
resume a failed extraction from a fresh worker heartbeat only when exactly one
exact-name loose item exists. Central persists that heartbeat entry's slot and
identity into the withdrawal, and the worker consumes the same proof before its
historical matcher. This path has its own single anchored attempt; the worker
then returns the staged bag and proceeds to Central without touching the emptied
source slot. Ambiguous or absent
inventory remains a hard stop. No assistant-side compilation or live AO test
was run; Kavey owns the Release build and recovery validation.

## 2026-09-10 — Banker readiness UTC token repair

A startup run in local timezone `+03:00` proved that every worker completed
layout and write-front reconciliation and wrote a valid current-run readiness
marker. Central nevertheless rejected all eight markers indefinitely. The
copied marker archive ruled out missing files, stale runs, character mismatch,
baseline mismatch, and zero capacity.

The remaining fault was a JSON token conversion boundary. `JObject.Parse`
already recognizes an ISO `...Z` value as a Date token. The readiness readers
converted that token back to text, reparsed it, and called
`ToUniversalTime()`. On the positive-offset host that round trip could lose
the UTC kind and subtract the local offset a second time, turning a fresh
marker into an apparently three-hour-old marker. Moving runtime data between
timezones exposed the defect but did not cause corrupt marker data; workers
delete and freshly rewrite their own readiness markers during startup.

`[IMPLEMENTED]` CityBankers now has one JToken-to-UTC reader that extracts
Date tokens directly and applies the repository's existing `UtcTimestamp`
normalization contract. All startup enrollment and readiness freshness gates
use it for bank diagnostics, repair requests, layout markers, and write-front
markers. Comparison thresholds, concurrency, and fail-closed readiness rules
are unchanged. Flipper was healthy and reached InPlay during the evidentiary
run, so this transaction makes no Flipper change. No assistant-side build or
live AO test was run; Kavey owns Release build and `+03:00` startup validation.

## 2026-09-10 — Withdrawal return finalization repair

The durable runtime files resolved the apparently missing Xan Feet symbiant.
Both it and an earlier returned Right Wrist were present in canonical Kbexte
stock at their original transaction identities and bag slots; the dispatch
queue was empty and neither item was loose. Two `return-queued` withdrawal
rows sharing one order nevertheless remained active, so status counted those
same rows as reservations at both Central and Kbexte and stock presentation
continued excluding the items.

`[ROOT-CAUSE]` The ordinary Central dispatch consumer removes a successfully
stored batch and deletes the worker's transient storage-result file. On a
later tick, withdrawal finalization required that deleted result as well as
the durable queue and stock evidence. It could therefore wait forever after a
fully successful return.

`[IMPLEMENTED]` A return is now finalized when its batch is absent and the
exact AOID, original donation transaction, and source worker are restored in
canonical stock. A matching same-batch result, if still present, must not say
the placement failed or stored a count other than one. Queued work and absent
stock evidence still fail closed. No user data is edited by this transaction;
the two existing stale rows will self-finalize on the next corrected startup.
No assistant-side compilation or live AO test was run; Kavey owns Release
build and stale-order cleanup validation.

## 2026-09-10 — Central inventory status census

Central's administrator inventory view proved its live heartbeat contained
three loose items while the Banker status window displayed `Inventory census
unavailable`. This was a presentation-source mismatch: the status renderer
used canonical storage bags for every role, but Central intentionally owns no
storage-worker bag map.

`[IMPLEMENTED]` Central now reports its ordinary inventory occupancy from the
heartbeat's item census plus free-slot count. Storage workers continue to use
their complete canonical bag capacities, and Central's trade inventory is not
added to the aggregate storage figure. No inventory item is moved or deleted.
No assistant-side compilation or live AO test was run; Kavey owns Release
build and status-window validation.

## 2026-09-10 — Incremental internal trade staging

A nine-item donation split into a three-item Phatz batch and a six-item Spirit
batch. Phatz completed and physically recorded all three items, but Kbspirit
timed out three times before receiving anything. Its preserved same-batch
result said it never observed the complete expected Central trade contents;
the durable failed batch and all six loose Central items proved custody was
safe.

The outgoing dispatcher previously called `Trade.AddItem` for every item in a
single update. It then waited for its local window to expose the full multiset.
Workers likewise waited for an exact full remote cache, while the established
trusted fallback deliberately stopped whenever that cache was non-empty—even
if it was incomplete. A partially published multi-item window could therefore
make neither normal acceptance nor fallback possible.

`[IMPLEMENTED]` Central now stages one occurrence at a time, waiting for the
local trade-window count to acknowledge each request before adding another.
It detects foreign or complete-but-mismatched contents and fails closed. After
Central has accepted the exact persisted batch, the configured destination's
command-bound fallback may accept an incomplete remote cache; a complete cache
continues through ordinary exact matching. AO Finished plus physical worker
inventory and bag-placement observation remain required. No assistant-side
compilation or live AO test was run; Kavey owns Release build and the preserved
six-item Spirit batch retry.

## 2026-09-10 — Six-item internal dispatch ceiling

Live validation separated incremental cache publication from capacity. The
previous failed six-Spirit batch recovered and physically stored all six after
one-at-a-time staging was introduced. The immediately following donation sent
ten Spirits to one destination; that batch timed out three times with the
worker reporting `ExpectedCount=10`, `StoredCount=0`, while Central verified
all ten occurrences returned. Six succeeds and ten does not, establishing a
six-item effective capacity for these internal AO trades.

`[IMPLEMENTED]` The ten-item player donation limit is unchanged. Routed items
for each worker are now emitted as ordered dispatch chunks containing at most
six occurrences, and the existing serialized queue processes them separately.
Recovery also upgrades an already-failed oversized pre-transfer batch: only
after verifying the complete expected multiset on Central, it retains the
original batch ID for the first six and inserts additional same-transaction
chunks for the remainder. No item is guessed, moved during splitting, or
declared stored without the existing AO Finished and physical placement
proofs. No assistant-side compilation or live AO test was run; Kavey owns the
Release build and live recovery of the preserved ten-Spirit batch as six plus
four.

## 2026-09-10 — Quiet successful internal storage

The successful ten-Spirit recovery validated both dispatch chunks, but also
showed that routine progress confirmations were each routed through the
Manager channel. Manager consequently logged many opaque `MANAGER CHANNEL
delivered` lines during an otherwise normal six-plus-four storage pass. The
owner had previously suppressed this locally; overlapping repository work had
reintroduced the calls.

`[IMPLEMENTED]` Ordinary internal trade-opened, transfer-completed,
worker-received, compatibility-fallback-accepted, per-item-stored, and
batch-stored events no longer enqueue operator tells. Their structured ledger
and activity records remain intact. Failures and exceptional recovery notices
remain routed to the operator, and donor-facing progress is unchanged. No
assistant-side compilation or live AO test was run; Kavey owns Release build
and normal-storage log verification.

## 2026-09-10 — Busy tell-sender handback

A live ten-item donation exposed an ordered-queue stall without any send
failure. The first progress tell was assigned to Kbcentral just after the
player opened a trade. Central then correctly refused outbound tells while
`Trade.IsTrading`, but the coordinator permits only one outstanding assignment,
so every later progress tell waited behind it until the trade completed.

`[IMPLEMENTED]` A banker that becomes trade-busy now atomically returns its
assigned tell to the pending queue before declining sender work. The handback
does not count as a failed delivery attempt, retains the original sequence,
and lets the coordinator assign that same head message to another idle banker.
Required-sender jobs remain pending until their required character is eligible.
The existing 45-second timeout still covers genuinely abandoned assignments.
No assistant-side compilation or live AO test was run; Kavey owns Release build
and repeated donation timing validation.
# Session 64 — unresolved custody isolation (2026-09-11)

- Live restart evidence showed the readiness barrier completing normally while
  one retained Artillery failure had only 1/2 expected items on Central.
- Identified the remaining global outage as queue gating: any retained batch,
  including an irreconcilable failed evidence row, blocked all new donations.
- Added a durable `custody-hold` state for incomplete startup custody. It keeps
  the original evidence and affected-worker isolation while allowing unrelated
  donations, dispatch, and withdrawals to proceed.
- No item was marked stored or removed from the custody record.

# Session 66 — full bagaudit difference report (2026-09-11)

- The supplied incident archive did not contain a post-incident full open/read
  audit of every Artillery bag. Startup layout and write-front readiness were
  therefore insufficient to establish whether the disputed item was stored.
- Enhanced the manual full audit to compare the complete pre-run persisted
  storage snapshot with every live result and print exact, reusable
  differences rather than merely recording a new observed state.
- Added explicit audit-mode stops to operational agents that could move items,
  reconcile storage, recover queue work, or seed the audit result into live
  state while evidence is being collected.
- Session 65's speculative old-donation recovery is not validated by the
  available physical evidence and remains blocked pending this audit.
- No assistant-side compilation or live AO test was run; Kavey owns both.
