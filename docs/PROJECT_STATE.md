## Session 171 — split request header and uncertain-operation isolation

- [VERIFIED-LIVE] Owner log completes all seven remaining merges,55 through61, each with matched action53, local response application and conserved total61. Final merge verifies at18:57:08.959. Repeated consolidation is live-confirmed.
- [OBSERVED FAILURE] Two #cru requests at19:00/19:01 attempt one-unit splits from inventory/72 quantity61, each timing out after10s. No split CharacterAction response, add-template or quantity update is logged; local inventory remains61. No physical split result is established, and absence of these logs is not proof the server could not change inventory.
- [CORRECTION / LIVE UNVERIFIED] Split still inherited N3Message.Unknown=1 while working merge explicitly uses0. Uploaded AOSharp split helper establishes action52 (SplitItem), source Target, Parameter2=count; Clientless sets local Identity but does not change Unknown. Split now explicitly sends Unknown=0/local identity, preserving action/slot/count, and logs all outgoing fields using the same logger as merge. Applying the zero header to splitting is a source/evidence-informed correction to try, not a proven explanation of both failures. No invented split response mapping, source decrement or new Item is added.
- [SAFETY] Validate known CRU quantity and a unique live normal-inventory source before split. The shared runtime uncertainty latch now covers both split and merge. Only verified completion clears it; timeout/exception/actor interruption prevents further new CRU preparation or automatic merges until runtime restart. Existing timeout expires its request and now explains the pause instead of encouraging another split against potentially stale quantities. Ordinary banking and previously reserved unaffected objects retain their existing paths.
- [OWNER NEXT] Rebuild/restart for fresh login quantities, then one #cru request. Provide STACK SENT/RECV/add-template/quantity/verification and inventory outcome. Intended result is60+one reserved unit if fresh supply is61; split response/cache behavior and pickup remain unproven. Do not claim the full CRU service complete or repeat the old failed packet variants.
- Validation: uploaded AOSharp Split/PacketFactory/N3 constructor and Clientless send source, owner log chronology, source/API and split/merge success/timeout/lifecycle review, symbol scan and Git whitespace. No assistant compilation, test suite or live AO operations. Private material remains outside Git.

## Session 170 — continue only after a verified CRU merge

- [VERIFIED-LIVE] Owner's18:46-18:49 run starts with53 plus eight singles. At18:47:12.087 the matched action53 updates survivor inventory/65 to54 and consumes inventory/64; at18:47:12.436 the actor verifies total61. All nine bankers reach ready. Session169's local response handling is live-confirmed for this merge.
- [RESOLVED] EnableAutomaticCruStacking=true now consolidates compatible unreserved CRU pairs sequentially. Only verified completion clears the runtime attempt latch; a two-second idle interval follows each success, returning control to ordinary banking. Requested CRU preparation remains ahead of background consolidation; reserved pickup objects remain excluded. Same template/QL and positive known counts are required, with pairs limited to the adapter's unsigned16-bit quantity representation. No claim that this proves arbitrary server stack limits.
- [FAILURE] A timeout, send failure or interrupted actor leaves the runtime latch set, surviving census readmission. No repeated automatic merge; new CRU proposals/preparation pause with an explicit owner-contact reply because quantities may be stale. Existing separately reserved pickup objects were never part of the merge. Other banking remains available after the operation retires. Normal runtime restart is required to clear the latch and obtain fresh login quantities.
- [UNCHANGED] Proven action53 send/header, exact-response application, survivor verification, total conservation, existing trade/dispatch exclusions, config opt-in (default false) and split packet/verification are preserved. No new quota, worker routing, stock/donor entry or in-game changelog text.
- [OWNER NEXT] Rebuild/restart with EnableAutomaticCruStacking=true. With unchanged supply and no pickup, seven more verified merges should produce one61-unit stack, visible without relog. Then #cru exercises the existing split path: expected60 in supply plus one reserved unit, followed by pickup. Split/collection success has not yet been established and must not be inferred from merging.
- Validation: focused static scheduling, latch/timeout/actor-readmission, quantity-bound and reservation review; symbol scan and Git whitespace checks. No assistant compilation, test suite or live AO operations. Owner supplies consolidation/split/pickup logs.

## Session 169 — proven CRU merge, missing local response handling

- [VERIFIED-LIVE] Owner enabled one diagnostic merge with nine singles plus52 (61 units). At18:30:18 action53 with Unknown=0 was sent and received for Target inventory/64 and parameter slot inventory/73; the unchanged cache timed out. Fresh login at18:37:06 reports53 at inventory/64 plus eight singles (61 total), with the former inventory/73 entry gone. The merge succeeded; do not replace its proven send packet or revive combine/use/move guesses.
- [SOURCE] Uploaded ICE uses the same action53 shape. Uploaded Clientless OnCharacterAction ignores53; full-character registration preserves wire Placement rather than locally compacting slots. Full-client AOSharp delegates physical inventory to the game and provides no managed53 cache handler to copy. The owner evidence supports retaining packet.Target and consuming the parameter-addressed stack for this CRU transition.
- [RESOLVED] Added an exact pending-merge response binding in StackableItems. Register before send; match local character, header and both frozen slots; apply after native handling only while the captured live objects, endpoint slots and all CRU quantities are unchanged. A matching response removes the consumed object and adds its known count to the surviving object. Duplicate/unmatched/expired responses do not mutate inventory. Disconnect/full login, send failure, timeout and actor teardown retire the binding. Verification now expects the survivor and conserved total supported by the live result. No send-side optimistic quantity mutation.
- [BOUNDARY] Same-template/QL CRU normal-inventory pairs only, positive observed quantities and sum within the unsigned16-bit representation. This does not establish arbitrary stack limits or split-response behavior. Default automatic merging remains false; true permits one diagnostic merge per banker runtime. The attempt flag now survives census actor replacement, preventing a new attempt without a runtime restart. No unattended consolidation loop enabled.
- [OWNER NEXT] Rebuild with the normal owner workflow. For the next single enabled attempt, unchanged supply should become54 plus seven singles (61 total), visible immediately without relog, with STACK action53 applied and STACK merge verified. Remaining work after that observation is normal repeated consolidation policy and separate split/pickup confirmation. These are not claimed complete by this response fix.
- [OTHER LIVE EVIDENCE] Owner confirms organization replies echoed at2487,4873 and1713 bytes after session168. Org-size cleanup is live-confirmed. Latest supplied run reaches all nine BANKER READY, including Artillery120 and Support110; no repeated replacement-banking loop appears in the supplied interval.
- Validation: owner before/response/relogin evidence, private uploaded reference source review, exact request-field preservation, duplicate/timeout/disconnect/teardown/census lifecycle review, symbol/API and Git whitespace checks. No assistant compilation, test suite or live AO actions. Private sources/logs not committed; no owner changelog wording invented.

## Session 168 — restore owner-controlled organization blob size

- [OWNER DIRECTION] Owner reports the login/logout flood originated in server behavior and the temporary organization blob restriction has lifted. Keep the organization size directly editable in Presentation and retire the automatic experiment.
- [RESOLVED] Restored `OrgBlobPageSize = 5200` in `CityManager.Presentation.cs`. Organization pagination uses that constant; removed learned bounds, automatic budget selection, delivery-driven calibration, startup restoration and persistence of calibration state.
- [PRESERVED] Exact sender/channel/text echo confirmation, delivery-health reporting, transport routing and UTF-8 pagination remain. Guest/tell budgets remain 8000/7200. Existing `citymanager-org-size.json` is ignored and no longer written; it remains recognized solely to avoid an alien-file warning on upgrades.
- Validation: focused source/diff review, removed-symbol reference scan and Git whitespace checks. No compilation, test suite or live AO operations; owner owns builds/testing. No in-game changelog entry added. Recovered and completed the existing session168 transaction; no unrelated banker changes.

## Session 167 — replacement already received; false bank capacity caused replay

- [VERIFIED-LIVE] Latest one-bag run proves session166's read/receipt correction: Central used an incoming contents snapshot, banked the bag and queued Artillery; Artillery used source empty proof plus exact receipt at17:41:46. The failure moved to phase banking at17:42:06. Subsequent complete audits twice establish Artillery120 bags,102 bank18 inventory; Central registered the replacement. Nevertheless worker restart recovery tried banking that same bag again at17:45:54 and17:48:44, causing repeated global census cycles. No second donation or manual movement is needed for Artillery.
- [ROOT CAUSE] Supplied SDK Bank.cs declares INVENTORY_CAPACITY=104 and NumFreeSlots=104 minus records. At the observed full102 bank, our new reserve code therefore believed two slots remained. The session165 fallback conditional never selected inventory placement. Session164's post-census worker rebanking then repeatedly retried an already completed replacement. This supersedes the claim that checking SDK free slots alone handled the full-bank case.
- [CORRECTION] A verified replacement is always registered in worker normal inventory. Removed the post-census reserve worker rebanking actor entirely: startup census already establishes current worker custody/topology. Central alone banks the reserve. Reserve donation admission, initial banking and bag-only read-refresh staging use a conservative102-slot capacity bound, additionally capped by SDK free slots and requiring open bank state. Ordinary non-bag bank items count against this limit. No SDK binary patch or repository-wide unrelated inventory rewrite.
- [OWNER OPERATIONS] Advised stopping the repeated audit loop until rebuilding. Existing Artillery bag is already present and should stay in inventory after normal startup; both missing bags have now been physically replaced in owner logs. Spirit reserve exclusion, fresh contents/source certificate guards and Central reserve bank policy remain intact.
- Validation: owner log chronology and repeated complete audit counts; supplied SDK Bank.cs capacity and slot allocation review; focused source diff, references and Git whitespace checks. No assistant build, test suite or live AO operations. Correction published for normal owner rebuild/start; smooth subsequent reserve operation remains to be observed. Original duplicate-container producer remains unknown.

## Session 166 — reserve read evidence and bag-only retries

- [VERIFIED-LIVE] Owner's one-bag run donated one Small Backpack, Central eventually banked/dispatched it to Support, and a final audit/readmission registered Support110 bags. Artillery remained119; Spirit remained120 with no reserve transfer. All bankers returned ready. Central and Support each timed out in reserve phase opening, causing two broad census/reconnect cycles and nearly six minutes from donation receipt to replacement registration. This is successful recovery with a reserve-read defect, not a smooth successful reserve test.
- [OWNER DIRECTION] Leave deployed bots running; pause further bag donations. Publish the correction for one additional bag to Artillery before attempting a larger batch. No additional uploads or deliberate fault reproduction requested. No live operations or assistant builds.
- [CORRECTION] Reserve handling now captures the incoming-container observation boundary before donation/internal receipt and before bank extraction. A read-only view of the already-installed wire observer admits snapshots after that boundary, including responses that arrived before the actor's next tick. A server-reported empty contents list does not depend on a nonzero SDK handle or yet another Container object. Existing exact empty census reuse remains limited to its matching live object and empty contents. New diagnostics show addressed slot, pre/current handle and observation boundary. The old log lacks these details, so it does not establish whether the missing proof was an early automatic response, handle interpretation, or absent response.
- [CUSTODY] Central publishes an empty-source certificate bound to the reserve bag and dispatch batch only after verifying emptiness. The existing serialized source check and trade retain exclusive control of the closed container; confirmed cancellation may rebind the same certificate to its retry batch. A worker may use that source proof only after its exact-identity physical receipt and matching transaction/destination/batch. It need not toggle the newly received bag open again. Any observed nonempty contents contradicting the certificate still stop placement. Donor assurances, template matches and receipt alone do not establish emptiness. Older queue state without a certificate cannot manufacture one.
- [SCOPED RETRY] A read-only timeout at the unchanged unique inventory slot retains this actor's operation and retries only that bag through bank/inventory with10–640s backoff. Late incoming contents can satisfy the hold without moving again. It does not disconnect anyone or request a roster census for missing contents. No bank space means hold without replay; newer certified worker receipts avoid that path on full-bank Artillery. Genuine contradictory layout, unverified movement or nonempty contents retain the existing reconciliation safeguards; no universal claim that every future recovery is local.
- [INVARIANTS] One reserve transfer at a time, explicit Spirit exclusion, repeated Central donations limited by bank capacity, full-bank worker inventory placement, and no spirit overflow remain. Empty-source proof is carried across custody, not inferred from an arbitrary default SDK Items list. Operation writes precede movement. Connection-scoped observations/arrival proofs are not resurrected from an old process; restart census remains authoritative.
- Validation: latest owner log chronology, supplied SDK Inventory.OnContainerUpdate/Container semantics, static source/cancellation/receipt ordering, full-bank placement, retry and old-record compatibility review, project inclusion and Git whitespace checks. No assistant compilation, tests or live AO actions. New correction remains unverified until the owner's next one-bag run; original duplicated-container producer remains unknown.

## Session 165 — normal startup verified; reserve capacity policy corrected

- [VERIFIED-LIVE] Owner run at 2026-09-19 16:23–16:27 local time completes all nine audits with zero failed bags and all nine BANKER READY/USABLE. Artillery119, Support109, Spirit120; no queued transfers or active withdrawals. Admission applications explicitly retain healthy peers. This resolves the post-recovery admission outcome; it does not live-test the later reserve code. The status revision13a2f44 was the latest-known marker, not proof of binary ancestry; source fingerprint is 7f1b524c91b8. A separate Manager packet-decoder error remains outside this banker finding.
- [OWNER POLICY] No reserve bags go to the Spirit role. Both dispatch selection and receiving readiness now explicitly exclude it; interrupted reserve banking on that role is also excluded. Central can fill its bank through repeated donations, at most ten items per trade. Donation validation accounts for free bank slots and already-received reserve bags waiting to be banked, allowing a smaller final batch rather than accepting bags that cannot fit. Ten is not a total-stock limit.
- [CORRECTION] The new reserve implementation previously required a free worker bank slot. The owner's new audit shows Artillery already has102 bank bags and17 inventory bags, so that requirement would block its replacement. A worker now retains and registers a freshly verified empty replacement in normal inventory when its bank is full; otherwise it banks it. The same mutex, identity proof and receipt completion apply. Central reserve donations still belong in bank.
- [RECOVERY IMPROVEMENT] A first contents-read timeout records the connection being refreshed. Once a new login and bank snapshot agree with native outer layout, recovery can retry fresh contents immediately instead of idling through the leftover read cooldown. No contents, move, quantity or empty-shell proof is weakened; repeated read failures keep escalating backoff. A successful read clears the marker, and mutation no-effect backoff explicitly cancels it. Older journals load without the optional marker. This reduces redundant waiting, not the requirement for verification reconnects.
- [DECISION] Spirit overflow into Central is conditional owner interest, not enabled by this change: the present storage routing, dispatch, ledger and withdrawal paths assume a separate destination worker. Safely adding Central as a second Spirit store is a separate feature, not simply using reserved empty bags. Keep the reserve empty and retain the existing Spirit capacity policy.
- Validation: owner log audit/readmission/status evidence; focused source review of full-bank placement, donation capacity, Spirit exclusion, read-refresh versus mutation retry semantics and serialization compatibility; Git diff checks. No assistant compilation, test suite or live AO actions. These capacity/retry changes are implemented but not yet live-tested. Original repeated-container producer remains unknown.

## Session 164 — Central empty Small Backpack reserve

- [OWNER DIRECTION] Kavem may donate ten Small Backpacks (99228) to Kbcentral for a banked replacement reserve. Automatically replace missing worker bags; no spirit overflow expansion. No manual in-game repair steps beyond the normal donation.
- [IMPLEMENTED] Donation validation and the player handshake admit these bags only from Kavem, within the existing ten-item trade limit. Accepted offers durably enroll distinct container identities before receipt. Ordinary physical receipt proof remains required. Bags bypass item retention/deletion, stock-ledger anchors and ordinary inner-bag placement. Nonempty bags are retained and excluded from the reserve; their contents are never discarded.
- [IMPLEMENTED] Central verifies empty contents, banks the donated bags, and sends one replacement at a time through existing attempt-bound dispatch, cancellation and physical-receipt machinery. A worker rechecks its actual bag count and bank headroom, verifies emptiness and banks the received container itself. Exact container identity is required throughout, with no same-template fallback. Storage topology updates use the shared storage mutex. Logs use EMPTY BAG RESERVE.
- [DECISION] Replacement targets preserve each ready worker's observed physical bag-count high-water mark, rather than filling theoretical retention capacity. On first enrollment, completed shared-bag recovery history contributes removed distinct containers, capped at retention capacity for that initial correction. This supplies the just-lost Artillery/Support bags while leaving existing spirit-capacity shortfalls alone. Later census-confirmed count losses draw against the saved target. Unknown historical losses without a baseline are not guessed. Smallest deficits are serviced first; only nearby ready workers are selected.
- [RECOVERY] Central owns the durable reserve membership/assignment file in bag-recovery; each actor journals outer-operation intent before sending. Census quiescence retires live operations. Following restart/census, exact physical ownership settles assignments and resumes unfinished banking without sending another copy. The existing paired/full census owns disputed transfers; bags do not require symbiant ledger occurrence IDs. Already-open inventory bags may reuse only a matching empty census record and live handle/contents, avoiding another Use toggle. New opens require a fresh container object. Movement ambiguity/timeouts retain evidence and request reconciliation.
- Validation: focused static donation/dispatch/cancellation/storage/census, restart and serialization review; project inclusion and Git whitespace checks. No assistant build, test suite or live AO actions, per owner boundary. Implementation is not yet live-tested. Owner rebuilds normally, waits for ready service, then trades up to ten empty Small Backpacks from Kavem to Kbcentral; bots perform banking and replacement handoffs.

## Session 163 — physical recovery completed; refresh before admission audit

- [OWNER LIVE LOGS] September19 runs now verify all21 Artillery and11 Support items moved into distinct destination identities with exact source/destination deltas. Support's empty-shell deletion removed both original outer references, followed by a destination read retaining11 items and recovery completion15:53:07. Artillery's deletion likewise removed both references at16:03:05, followed by a destination read retaining21 items and completion16:03:10. One original shared container was removed per banker: remaining Small Backpack counts119/120 and109/110. Shortage warnings are expected capacity deficits, not evidence of another duplicated destination. Original duplication producer remains unknown.
- [VERIFIED FAILURE] Artillery's first subsequent audit opens118/119; the already-open evacuation destination retains its old handle/object and fails. Support retries fail all18 inventory bags after earlier scans opened them. Repeated same-connection audits expand the already-open set and repeat the failure. Final census application/ledger reconciliation and operational readmission are NOT yet verified. Owner stopped the run after this observation; no manual item handling is requested.
- [IMPLEMENTED] Both coordinated-startup and excluded-worker readmission paths now check for previously opened normal-inventory Small Backpacks after quiescence, settlement and shared-bag recovery, but before issuing an audit command. If any exist, request one labeled native-session reconnect and wait for FullCharacter to reset the SDK container collection. A fresh startup with no opened inventory bags proceeds directly. Existing planned pre-census disconnect isolation covers this refresh so non-central workers do not supersede healthy peer audits. Outstanding commands/results are not treated as successful by the refresh.
- [INVARIANT] BagAuditAgent's fresh-object/open-response requirements remain unchanged. No cached contents are promoted to new physical proof; the change creates a clean connection for the complete scan. Bank-only opened views need no additional preflight refresh because bank bags already stage through inventory. Active paired/local custody protocols are not altered by this narrowly scoped gate correction.
- Validation: latest owner log chronology, static call-order/quiescence/disconnect/re-entry review, supplied SDK FullCharacter container-reset behavior, Git whitespace checks and exact local/published tree equality. No assistant compilation, test suite or live AO actions. Physical evacuation/empty-shell recovery is log-verified; the new audit preflight is implemented but not yet live-tested. Owner rebuilds/starts normally and success requires complete audits plus BANKER READY, not merely recovery completion.

## Session 162 — shared identity is one contents-read target

- [OWNER LIVE LOG] Both affected bankers reach the same deterministic read loop. Artillery receives 21 records through inventory66, then retries inventory69 every15s without another InventoryUpdate. Support eventually stages both references into inventory, receives11 records through inventory64, then retries inventory66 without another update. Both retain zero verified transfers. This establishes our pre-evacuation read-loop defect, not item loss or a new server outage.
- [CORRECTION] InventoryUpdate addresses a container identity, not an independently owned outer icon. Recovery now uses one incoming snapshot per shared identity as its one-container quantity baseline; it does not require or pretend to have a separate response for each alias. Outer actions still resolve exact typed slots. This intentionally supersedes session159's per-alias baseline/empty-response requirement, which the observed shared-window behavior does not satisfy.
- [INVARIANT] Read reuse is restricted to raw incoming snapshots newer than the last mutation boundary. Every item move/disposal/outer action advances that boundary before sending. Native SDK inferred contents remain insufficient. Successful verification retains its actual after-snapshots until another mutation; merely retiring an intent no longer discards valid evidence. Empty-shell actions still require a post-mutation empty shared-identity snapshot plus the complete secured initial quantity budget; surplus disposal keeps its original-budget, per-slot and protected-destination checks. No duplicate inventory stock is manufactured from icon count.
- [RETRY] A Use with no fresh snapshot stops after15s rather than being replayed indefinitely. The actor records per-container failures and bounded30-960s retry deadlines in its journal and requests a labeled verification reconnect to obtain new evidence. A successful read clears that container's failure streak. Reconnects may therefore remain necessary where the server supplies no new snapshot; this is not a claim of reconnect-free recovery. Old journals load with an empty failure map and unfinished actions retain their existing reconciliation rules.
- Validation: owner log chronology, supplied SDK container/update addressing, static mutation-boundary/read/pending-intent and schema review, Git whitespace checks and local/published tree equality. No assistant build, test suite or live AO action. The read-loop correction is implemented; physical evacuation/deletion and final census remain unverified until observed in the owner's run.

## Session 161 — recovery reconnect churn and missing progress visibility

- [OWNER LIVE LOG] The four affected-banker disconnect transitions in the supplied 15:14-15:17 run correspond to our recovery design: both initial confirmations at 15:15:08, then Support staging verification at 15:17:13 and 15:17:58. The initial confirmations published global recovery requests, superseding healthy peer audits. Seven bankers subsequently completed census and became ready at 15:16:59. This is not evidence of AO server instability. The separate flipper disconnect and Manager packet-layout error are not attributed to bag recovery.
- [VERIFIED PROGRESS/LIMIT] Support reported one verified stage after reconnect at 15:17:48: one repeated reference moved from bank into inventory and the second remained in bank. No evacuation receipt or completed repair appears. Artillery enters readmission but the supplied log does not reveal the reason for its lack of further visible progress. Do not claim a resolved physical pair, lost contents, or an established container-open failure from this log.
- [CORRECTION] New incidents start evacuation without another login when the current incoming login/bank address multisets agree with the native layout. Existing confirm journals migrate through the same check. Mismatch still refreshes evidence. Planned verification disconnects from a quiesced non-central banker before it has issued a census withdraw only that participant during collection; independent recovery requests and unexpected disconnect safeguards remain. Healthy peer audits need not restart for that deliberate pre-census reconnect.
- [OBSERVABILITY] Each deliberate reconnect states its verification reason, outer actions state their typed source, container Use/response/timeout is visible, and recovery reports pending phase and verified record count at bounded intervals. Persisted pending actions continue reconciliation after deployment; do not delete journals or ask the owner to pick/move slots.
- [LIMITATION] Outer bag movements still use reconnect verification; this is explicitly retained, not presented as eliminated or as server failure. Removing initial confirmation benefits new incidents and does not erase the current pending moves. The physical recovery outcome and Artillery wait remain unverified. Root cause of original shared references is still unknown.
- Validation: supplied log chronology plus focused source review of reconnect, ownership, snapshot comparison and persisted-journal compatibility; Git whitespace checks. No assistant compilation, test suite or live actions. Reconnect/audit-disruption corrections are implemented; no successful deployed repair is claimed.

## Session 160 — shared-bag recovery compiler correction

- Owner build reports one error: CS0136 in SharedBagRecovery.Resolve; the source-address lambda parameter `a` conflicts with a later local `View a`. Renamed the predicate parameter and both endpoint views descriptively. This is a naming correction with no recovery behavior change.
- [OWNER OBSERVATION] Manual zoning changed nothing: ten empty normal-inventory slots remain, and either of the two suspect icons toggles the same container window. Outcome 1 accepted without requesting screenshots. Owner is ready to run automated recovery after the build correction.
- Validation: supplied compiler log and focused source/diff review. No assistant compilation, test suite or live AO action. Successful rebuild is not claimed.

## Session 159 — autonomous shared-bag evacuation and recovery

- [OWNER DIRECTION] The bot, not the owner in the normal client, must perform recovery and learn from the outcome. Empty suspect references into distinct healthy bags; then try bank, then a free social-back slot, then delete a freshly empty shell as last resort. Proven surplus items may be discarded. This supersedes the earlier no-mutation investigation boundary for this recovery. No further historical captures exist. Owner still owns builds/live testing.
- [IMPLEMENTED] SharedBagRecovery runs automatically under the existing census hold, after operational actors are quiesced and outstanding moves have settled. It confirms duplicate references across an automatic reconnect, uses typed SDK addresses without fabricated packets, stages references and destinations, compares fresh per-alias initial contents, then moves individual records with exact source/destination quantity accounting. Healthy inventory bags can be parked to obtain staging headroom if bank space exists. A shared emptying of both aliases is expected, not itself a failure.
- [INVARIANT] Every mutation has a durable write-ahead intent in data/bag-recovery/<character>/active.json. Unfinished actions are observed after restart; an unchanged fresh pair of endpoints can retire an unapplied intent, whereas an unexplained partial delta retains the hold. Outer moves and empty-shell experiments are independently observed after reconnect. No-effect attempts have persisted bounded backoff. Completed records remain in history; bounded event notes include before/after reference addresses. The native network-session disconnect preserves AutoReconnect and the host update loop.
- [INVARIANT] Empty-shell experiments require fresh empty responses for every remaining alias and evacuation receipts accounting for the entire initial one-container quantity budget. A source disappearing without an observed empty-shell operation is not success. Existing equipment is never displaced. Excess content disposal requires the initially identical shared-container per-slot record, an already secured full quantity budget, freshly verified destination conservation and a known secured record; matching names/templates or old ledger counts alone do not authorize deletion. Unknown layouts, insufficient capacity, inaccessible journals or ambiguous outcomes retain stock and the hold, with automatic retries rather than a request for human slot selection.
- [IMPLEMENTED] Verified relocation receipts preserve a unique original ledger anchor when available. Both global and local census apply these before fallback matching and mark receipts consumed only after durable application, preventing later reuse. Ambiguous historical ownership is not invented. Ordinary full physical census remains responsible for stock reconciliation and reopening service; original claims remain in census history. Recognized safety-data directories include bag-recovery.
- [LIMITATION] This implements recovery and records the results of the owner's experiments. It does not establish the historical creator of the persistent shared references, guarantee that AO accepts each action, restore donor information already lost, or claim a live repair has occurred. It is not a prevention fix for an unproven originating defect. The owner needs only rebuild/deploy through the normal workflow; recovery itself requires no human in-game operations.
- Validation: static state-machine, restart/write-failure, quantity/conservation, census ownership, provenance consumption and SDK member/IL review; project inclusion/XML and Git whitespace checks. No compilation, test suite, exploit execution, private-data publication or live AO operation, in accordance with the owner boundary.

## Session 158 — earlier reconnect failure found in archived local census

- [CORRECTION] The September18 07:51 UTC census is not the earliest saved repeated-reference evidence. Older local-census evidence in the September18 archive records affected identities multiple times at 06:48 UTC. That evidence is not retained in the newer archive, so both supplied archives matter. Prior earliest-occurrence statements are superseded; no original records are rewritten.
- [VERIFIED LOG/CENSUS] At approximately 05:38 UTC (08:38 owner local time), ordinary audits reported 120 Artillery bags and 110 Support bags, all opened/returned successfully according to their then-current checks. Both disconnected at 06:46:42 UTC. Local reconnect audits started at 06:47:20 with 223 Artillery entries (204 bank/19 inventory) and 203 Support entries (184 bank/19 inventory). Bank counts are twice the preceding 102 and 92; each inventory count is one larger than the preceding 18. These are client/audit observations, not independent server snapshots.
- Saved first local-census results at 06:48:18/27 list the affected identity twice at the same bank slot and once in inventory for each banker. Repeated bank rows reuse the same open handle. Inventory rows record a different action slot from their original enumerated outer slot. Reconciliation subsequently rejects multiple items at one physical slot, and retries continue against the bad listing. This demonstrates untrustworthy historical enumeration/action/verification, not three independent bags or proof of the persistent reference-creation operation.
- [DEPLOYED IL] Inventory.ResetContainers clears container contents/handles and bank-open status, but not Bank.Items. Bank.RegisterItems appends full snapshots. Bank return handling selects a locally computed free slot; it does not use the move callback slot for the Bank destination. Container insertion similarly computes a local inner slot. The latter are address-inference hazards when a cache is already wrong, not permission to reinterpret an undocumented callback field as an authoritative bank slot.
- [ALREADY FIXED] Commit 490ffa2 added replacement of the bank cache before native snapshot registration. The supplied deployed CityBankers DLL contains that clear, verified in ClientlessSessionGuard.MessageReceived. Current audit validation rejects repeated storage identities; later action/return verification fixes also remain present. Do not add a second clear, reintroduce arbitrary deduplication, or claim this historical fault is still unpatched.
- [RULED OUT AS EVIDENCE] The two pre-reconnect IOException stacks reach TellQueue heartbeat file deletion in OnUpdate. Their old log text promised packet retry, but the stacks do not prove a replayed bag packet. Current wording correctly states the interrupted callback is not replayed. No causal link to shared bags is established by those exceptions.
- [OPEN] The available sequence narrows the investigation to the last ordinary audit/reconnect and the subsequent invalid local audits. It cannot decide whether those audits created persistent shared references or acted on an already incorrect state; successful cached return checks do not prove server placement. Later incoming snapshots and ordinary-client observation still establish persistent shared references. Initial trigger and safe removal/reconciliation remain unresolved. No source/runtime/data repair, build or live operation was performed in this session; no new capture requested from the owner.

## Session 157 — deployed move review and bounded request evidence

- [VERIFIED] Static IL inspection of the owner-supplied deployed Clientless DLL confirms Item.MoveToInventory forwards the typed Slot; MoveToBank supplies the local Bank destination; both MoveToContainer overloads forward typed Slot and destination to ClientContainerAddItem. No equivalent source-address fabrication was found. This verifies the methods in the supplied binary, not all historical traffic or mutable-cache correctness.
- [EVIDENCE] Inspected the newer September19 archive. It contains the known ambiguous layouts and historical diagnostics but no bag-origin files; the later incoming captures were supplied separately and already reviewed in sessions153-155. Old diagnostic text describing proven stale records is historical and remains retracted. No new proof of the creating operation was found. No extra historical capture exists according to the owner.
- [IMPLEMENTATION] BagOriginTrace now instruments the existing SDK move calls across audit, staging, storage, withdrawal/extraction, route repair and recovery. Each wrapper records typed source and destination, item identity, caller basename/member, timestamp and request sequence, then invokes the same SDK method exactly once. Original SDK exceptions propagate; diagnostics failures cannot prevent the SDK call. No source identity is rebuilt, no new retry is introduced, and no hold or custody check is relaxed.
- A fixed bag-origin-<character>-moves.json retains the last 32 request intents, persisted before invocation, plus generation and assembly identifiers. An SDK exception updates its error type. This is explicitly API intent, NOT packet-capture proof or server acknowledgement; no error is not evidence of success. Incoming observations record the most recent request sequence at receipt, and login/bank/duplicate reports include the bounded request tail. Full-character generations reset the request queue; old files must only be compared using matching generations. Selected inventory evidence remains bounded; chat/authentication are not captured. Startup inventory recognizes bag-origin-*.json as owned diagnostic files.
- [LIMITATION] Persistence adds a small synchronous diagnostic write per instrumented move. This change cannot recover a past missing transition, repair the current shared-reference pair, establish which outer slot is disposable, or reconcile historical donor/stock errors. Existing ambiguity containment remains. Do not request a new run of the affected pair merely to collect more already-duplicated login snapshots; do not delete/move one as an inferred repair.
- Validation: deployed DLL method/visibility inspection, static wrapper/call-site and generation/error-path review, project XML and git diff checks. No compilation, test suite or live AO operations per owner boundary. Instrumentation is implemented; initial creation, safe physical remediation and ledger reconciliation remain separate open problems.

## Session 156 closure — available evidence boundary

- [OWNER DIRECTION] Owner confirms the private reference was supplied to help understand the existing defect, and there are no additional historical captures beyond the supplied files. Do not request unavailable pre-duplication packets again. Owner explicitly authorizes writing and publishing the sanitized recovery record.
- [EVIDENCE SCOPE] The session156 archive/log inspection used the September18 data.zip. A newer September19 data(1).zip and deployed DLLs are also available in the owner's supplied files; those must be used for any fresh current-runtime analysis. Do not mistake the older archive review for examination of all latest evidence. Prior sessions153-155 already recorded their incoming-snapshot findings.
- [NEXT] The private-reference comparison is complete. Initial creation and safe remediation remain open, with no further owner question currently required. Continue from available current evidence and code; if the missing transition cannot be reconstructed, design bounded move diagnostics and safeguards with an explicit evidence limitation. Do not present the reference as proof of cause or ask for a recreation of the exploit. No runtime change or live operation was made by this review.

## Session 156 — shared-container reference review

- [OWNER OBSERVATION] Two ordinary-client inventory icons toggle the same container window and show apparently matching contents. The owner logged out and stopped bots. Together with the independently parsed inbound references, this corroborates shared container addressing; it does not identify the creating operation or a disposable outer slot.
- [REVIEW] A private owner-supplied reference was inspected only, never executed or published. It requires an existing problematic pair according to the owner; it cannot establish the initial trigger. Its defensive lesson is to preserve source address type and location and distinguish an outer bag record from the container it references. No exploit instructions or private source are included here.
- [SOURCE EVIDENCE] In the supplied clientless source, Item.MoveToInventory forwards the complete typed Slot to ClientMoveItemToInventory. MoveToBank forwards that Slot and targets the local player's Bank identity. MoveToContainer forwards Slot and the requested container identity. Container contents use Backpack addresses combining the handle and inner slot. These are source-snapshot findings, not verification of every historically deployed DLL or actual outbound packet.
- [COMPARISON] Reviewed current audit, storage, withdrawal/extraction and recovery move sites, plus the removed Colonist repair at e0f8c2c. No equivalent source-address fabrication was found in those sites. Audits stage outer bags into normal inventory and return them to the bank; normal storage inserts the selected inventory item. The historical Colonist repair explicitly rejected a container as the item being transferred. A focused Git-history search for raw container-add construction and explicit BankByRef construction found only the integrated runtime import, not a later matching implementation. This does not rule out stale SDK state, event ordering, another historical binary, or a server-side fault.
- [EXISTING EVIDENCE] Reused the owner's September 18 data archive rather than requesting it again. Daily banker logs cover September 8-18; those logs contain no textual occurrences of the two affected container IDs. Unified host logs contain September 18 staging attempts for them, but not the outgoing typed-address transition that first created the extra reference. Colonist completion markers for the affected characters are September 16 23:59 UTC; their completion alone does not establish causation. No raw logs or private archive data are published.
- [OPEN] Initial reference creation, safe physical resolution, and stock/donor reconciliation remain unproven/unrepaired. Current BagOriginTrace observes incoming events, not the missing historical outbound request. Repeating login snapshots of the already-duplicated state cannot reconstruct that request. Ask whether the owner has an older packet capture spanning a clean state and first duplication; existing data.zip and normal-client observations need not be supplied again. If none exists, report that limitation and plan narrowly scoped outgoing/incoming move evidence before a future controlled run; do not ask the owner to run the private sample or manipulate the affected pair.
- Runtime and owner data unchanged. Validation: focused static source/history review and offline archive inspection only; no compilation, test suites or live AO operations. This comparison is complete; it is not a root-cause fix.

## Session 155 — original identity sets retained; normal-client observation recorded

- Owner supplied a normal AO client screenshot, counted 102 bank bags and 19 inventory Small Backpacks plus a terminal and ten empty inventory slots, and reported the Colonist worn on the back. Inventory count is visible in the screenshot; the entire bank is not visible simultaneously, so its count is owner-reported and agrees with the captured bank packet. Owner then left all bags unchanged, stopped bots and logged out. Normal client display corroborates the extra outer inventory entry; it is not evidence of two independent physical containers.
- Direct comparison of the September 11 storage-baseline.json Small Backpack identity multisets against the four September 19 wire captures: artillery baseline 120 records/120 distinct IDs, current 121/120; support baseline 110/110, current 111/110. Missing original IDs: zero for both. New distinct IDs: zero for both. Each difference is exactly one additional occurrence of its previously known affected ID. This does not prove physical contents or establish how the additional references arose.
- The artillery baseline recorded 102 bank plus 18 inventory bags. Its affected ID occurred once at bank slot 4, holding 21 items. The support baseline recorded 102 bank plus 8 inventory bags; its affected ID occurred once at bank slot 21 and was empty. The older configured artillery capacity also says 120 Small Backpacks, not 122. The owner's present 122 total counts the 121 displayed Small Backpacks plus worn Colonist; no claim that an original physical bag is missing follows from the identity comparison.
- September 18 07:51 UTC census history independently demonstrates an accounting hazard: artillery inventory slots 66 and 69 were credited with exactly equal 21-item record lists, including the same Backpack slot identities/handle. Those are not independent observations of 42 items. Support bank slots 92 and 1 were credited with equal nine-item template/QL sequences but different handles; old audit target resolution does not independently establish which physical outer slot each action addressed. This supports retaining the ambiguity hold and rejecting inferred extra stock; it does not establish the original reference-creation mechanism. Runtime ledger is not modified in this session.
- [OPEN] Need ordinary-client per-icon observation for the inventory pair. The client UI order need not match protocol slot order (terminal is visually first but packet placement differs), so never tell the owner that the third/sixth displayed icons must be slots 66/69. With bots stopped, owner may inspect inventory bags one at a time without moving items; identify the full 21-item artillery bag by its Enduring Right Arm (QL190), Enduring Waist (QL190), and Awakened Brain (QL180) contents. Determine whether a second inventory icon displays the same complete contents, whether one fails to open, or whether it is different. Screenshots and observed behavior are evidence; do not create a test item, transfer contents, rename, delete or choose an arbitrary slot for repair. No further bot rebuild needed for this observation.
- No runtime source changes, physical actions, data repair, builds or test suites. Offline JSON multiset comparisons, historical record comparisons and source/history inspection only. Root cause, safe physical resolution and ledger/donor reconciliation remain open. Capture next owner observation before proposing a mutation.

## Session 154 — incoming duplicate records independently decoded

- Owner returned matching-generation login/bank/first-duplicate captures and console output. The observed duplicate already exists in a FullCharacter inventory snapshot for one affected banker and in the first Bank snapshot for the other, before cache processing creates the corresponding duplicate. No initial duplicate is introduced by this run's audit. Startup containment held both ambiguous layouts; the additional running time did not invalidate the first-event captures.
- Added a read-only standard-library Python inspector for the selected raw snapshots. It checks N3 packet type, declared packet length, message type, encoded X3F1 array count, every fixed 32-byte record boundary, and decoded slot/count/template/QL/identity-instance agreement. It consumes the complete bank payload exactly and checks the container type against its numeric value. No runtime SDK or serializer executes. Full-character packets contain additional fields after inventory, so the script does not claim to decode those fields.
- All four supplied login/bank snapshots match the independent parsing. Each duplicated row has the same normal Small Backpack flags, quantity, template IDs, QL and trailing field as normal rows; only its outer slot differs from its paired occurrence. No special flag discriminator or framing/offset error was found. Full-client AOSharp also uses a container identity to retrieve container inventory, supporting the field's interpretation but not proving server uniqueness semantics.
- [OPEN] This establishes repeated container references in the captured inbound snapshots. It does NOT establish two physical duplicated bags, why the state arose, which slot is valid, or a relationship to the Colonist repair. Do not blame a server defect or repair the SDK on this evidence alone. User's statement that the physical bag set is unchanged remains a constraint.
- Next owner observation: with all City Dwellers bankers stopped, log only the inventory-affected banker into the ordinary AO client, capture the complete normal inventory and count Small Backpacks (exclude equipped Colonist). The incoming snapshot reports 19 normal-inventory Small Backpack entries where the earlier baseline reported 18. Do not move, rename, delete, trade or open bags for this first observation. Report any automatic disappearance or login warning and log out normally. This independent client observation is needed before choosing a slot-addressed experiment or any repair; no second banker run is requested yet.
- Runtime code unchanged in this transaction. Existing containment retained. No data repair, bag deletion, builds, test suites, or live AO operation performed. Validation was offline analysis of supplied captures, source review and diff checking. Private captures/output remain outside Git. Root cause and persisted ledger/donor reconciliation remain open.

## Session 153 — duplicate origin remains open; capture the missing boundary

- Owner corrected scope: the physical bag population is fixed; backpack trading is not involved. Priority is the creation of duplicate outer bag records, not another reconnect/audit retry workaround. The supplied deployed DLLs and current data snapshot were inspected read-only. No runtime data or physical items were deleted or repaired.
- Deployed IL confirms that FullCharacter replaces the inventory list, Bank.RegisterItems appends, and CityBankers clears the bank list at MessageReceived before the native bank callback. Adding another bank clear is not an established remedy. Native container updates replace the existing container with the same identity: one container object behind two outer records is therefore NOT independent evidence that one outer record is a phantom. Session 144's stronger inference is retracted. Neither a timeout nor list ordering proves which slot is physically occupied.
- Existing diagnostic snapshots show repeated identities before containers have been opened, but contain no incoming packet/decoded-slot evidence. Initial saved open failures involve different bags from the duplicates. Their relation is unproven; the first failures had nonzero pre-open handles and later failures reused handles. No decoder, server, Colonist repair or local mutation root cause is claimed.
- Added read-only BagOriginTrace before the other CityBankers message observers. It captures decoded full-character/bank snapshots, selected incoming inventory events, bag records before processing and before the next message/update, and raw bytes only for selected inventory snapshot/move messages. The after boundary includes native callbacks and synchronous subscribers, not just native code. First login, first local bank snapshot and first duplicate evidence are saved to three fixed diagnostic-dumps/bag-origin-<character>-{login,bank,duplicate}.json files per character. A generation identifies matching records; stale bank cache is excluded from duplicate detection until a fresh local bank snapshot. A bounded 32-event history accompanies the first duplicate. Authentication and chat are not captured; evidence failures cannot abort message processing.
- Restored rejection of repeated storage identities at existing layout/audit validation gates. Audits cannot choose the lowest-slot record and publish that as physical truth. Removed phantom-slot credit; a repeated identity is not proof of free inventory space. Diagnostic wording no longer calls unproven records stale. This is containment and evidence collection, not a root-cause repair or a ledger repair.
- Only Small Backpacks (99228) already qualify as storage. The old one-time Colonist repair implementation was removed historically; deleting a real equipped bag is not implemented. No contents are discarded and no inventory records are erased to conceal ambiguity.
- Validation: static deployed IL and member metadata review, event ordering/lifecycle and cache transition review, project inclusion/XML and diff checks. No compilation, test suite or live AO run, per owner build boundary.
- [OPEN] Owner rebuild/start once, then return the bag-origin diagnostic JSON for the affected bankers, matching generation where possible. Compare raw and decoded incoming slots against before/after cache to locate first duplication. Only then repair the proven producer and reconcile persisted stock/donor attribution. Do not revive arbitrary deduplication as a root-cause fix. Session 152 readmission remains separate and unchanged.

## Session 152 — failed census readmission no longer restarts healthy bankers

- Owner's 2026-09-19 12:32–12:36 console run reports installed source fingerprint `source-101a4ba7167135f924892d8b5e4ab5e9b46ec9bcf5edc85efc32bcf86bb15f0c` and latest Git revision 8054563; a source fingerprint is not proof of Git ancestry. Four workers each opened all but one bag; all staged bank bags returned. Five bankers became ready, then a rejected worker's 60-second retry caused those healthy peers to start fresh audits. No item-loss inference follows from this log.
- Verified code trigger: released-cycle admission previously replaced the global cycle whenever an online connection was outside its participants. Withdrawal during collection worked, but readmission undid that isolation. Session 151's four fixes did not address this path.
- The coordinator now retains a released cycle while excluded workers obtain individual readmission. A request/grant binds cycle, connection, character and unique run. Central checks pending peer work and reserves the worker before granting a scan. The worker remains closed to operations and trades, settles/stages its own inventory, validates its complete census and publishes the fixed result. Central applies it through the existing local-census application bundle, character-scoped storage/ledger merge and withdrawal reconciliation; only then does it add that connection to the same roster. Worker releases its own reservation and performs the existing ready-token handoff. Healthy members are neither quiesced nor rescanned.
- Explicit recovery requests still supersede globally. Cycle changes/disconnects retire admission attempts; stale run/connection results cannot admit a new connection. Incomplete local scans retain evidence and retry only that worker with the existing bounded backoff. Pending peer custody is not silently replaced by a local census. Existing source-scoped local bundle idempotence handles a completed application awaiting roster publication.
- Bag-open default wait increased from 3 to 15 seconds without replaying Use, changing physical move verification, or accepting cached contents. Every open failure now prints run, bag identity, original and attempted slot, elapsed time, before/current handle, object freshness and exact error. The aggregate console does not identify why the four opens failed: this is a bounded timing allowance and better evidence, not a claimed root-cause decoder/cache fix. Existing saved per-bag failed census JSON is needed to establish that cause; repeated-audit container freshness and persisted duplicate stock/donor attribution remain open.
- Validation: static admission/grant/application/handoff tracing, retry/disconnect/recovery and partial-publication review, compile-inclusion/project XML checks, journal integrity and git diff --check. No assistant build, test suite or live AO run. Raw log and runtime state were not committed or modified.

## Session 151 — corrections from the review of sessions 135–150

- Owner requested fixes for the four reviewed regressions, retaining the useful Claude changes. Reviewed base: `0d64731466884eab1ce8fdedbe20fbc48629a378`. The PowerShell enum correction, census withdrawal/backoff, shared-state read handling and journal restoration remain intact. Buddy chat separation, existing tell queue and owner-authored changelog are unchanged.
- Storage returns now capture per-slot bank occurrence counts, inventory count and the addressed source-slot count immediately before sending. Completion requires exactly one added bank occurrence, unchanged other bank slots, one departed inventory occurrence at the addressed slot, and arrival at the persisted destination slot. An already-present expected-slot record alone cannot commit placement; missing/ambiguous evidence waits and then requests reconciliation through the existing failure path. No snapshot/data repair is performed.
- `PreferredRecord` filters by requested location and identity before sorting candidates. A bank ghost cannot hide an inventory record. The stable preference is an action candidate, not proof of physical location.
- Audits move/open the exact slot selected in their target snapshot and return from the inventory slot actually opened. They no longer resolve action targets through arbitrary first-record helpers. Arrival selection requires one newly observed destination slot and cannot fall back to a pre-existing ghost. Unresolved or contradictory observations still fail closed; this does not claim to repair the SDK cache or prove which of two initial slots is live.
- Org echoes are tracked separately per outbound message/page and registered before sending. Confirmation matches complete text, captured manager sender ID and actual route channel ID; prefix/other-sender matches cannot train calibration. Throws remove only their own pending attempt. The update callback expires outstanding entries even in a quiet channel. Sizes use UTF-8 bytes. Calibration version 2 ignores bounds learned by the old prefix matcher, retains the existing seed/probing policy, and atomically serializes saves. No resend, proxy or tell-routing behavior added.
- Validation: static branch/call-site review of unchanged ghosts, delayed returns, unexpected destination, selected audit slots, bank/inventory duplicates, multipage and out-of-order echoes, failed sends and quiet-channel expiry; project XML/shared-source inclusion and diff whitespace checks. No compilation, test suite or live AO run: owner owns builds and live tests.
- Existing unfinished work remains separate: persisted stock/donor attribution for duplicate bags, SDK duplicate creation, and second-audit inventory container freshness. The source corrections above are resolved when published; they do not assert those other issues are repaired.

## Session 145 — organization page size is learned, not configured

- `[VERIFIED-LIVE 2026-09-18T13:50]` Organization replies deliver and are proven: `ORG DELIVERY CONFIRMED via Client.SendOrgMessage`, twice, for `#help` and `#help bankers`. Org chat works end to end with echo evidence rather than an assumption.
- `[VERIFIED]` 900 was far too small. `#help` emitted eight pages at 435-569 bytes because `BuildBlobLinks` subtracts a roughly 400 byte envelope from the budget, leaving about 500 of content per page. `[OWNER-DIRECTION]` Kavey is right that 5200 was not arbitrary and that a hand-picked constant is the wrong mechanism: the size must heal itself.
- `OrgBlobPageSize` is removed. `OrgPageBudget()` now probes midway between `_orgSafeLength`, the largest length the chat server echoed back, and `_orgFailLength`, the smallest that vanished, converging on the real ceiling by binary search. Seeded from measured evidence: 569 delivered, 2487 dropped, so the first budget is about 1528 rather than 900.
- `RecordOrgDelivery` feeds every echo result back. A confirmed delivery raises the safe bound; a confirmed disappearance lowers the fail bound and pulls the safe bound under it. A delivery confirmed above a recorded failure reopens the upper bound, because that failure was then not a size limit. Bounds persist to `data/citymanager-org-size.json` and are restored at startup, so the ceiling is learned once and survives restarts.
- `[DECISION]` Guest 8000 and tell 7200 are unchanged. They ride the chat connection, which is not subject to the game-connection limit, and there is no evidence they need calibrating.
- `[OPEN]` The true ceiling is still unmeasured; the system now discovers it instead of being told. Convergence needs a few org replies of increasing size, and each step is logged as `ORG SIZE CALIBRATION`.
- Validation: log correlation, source review, brace and parenthesis balance, `git diff --check`. No assistant compilation or live run; owner builds and tests.

## Session 144 — org replies were oversized, not misrouted

- `[VERIFIED-LIVE 2026-09-18T13:43]` The session 143 gate fix worked: `Org reply submitted through AOSharp.Clientless.Client.SendOrgMessage; awaiting echo confirmation. Stat.Clan=4736; Client.OrgId=0; observedChannel=4736; orgName=Athen Paladins; len=2487`. `Stat.Clan` resolves even though `Client.OrgId` is 0, which confirms those are different sources and that gating on `Client.OrgId` was the wrong test.
- `[VERIFIED]` Root cause of missing org replies is payload size, not routing. Organization replies are sent on the game connection as a `GroupMsgMessage`; guest and tell traffic rides the chat connection, which tolerates far larger messages. `OrgBlobPageSize` was 5200, so a 2487 byte `#help` reply was emitted unsplit and silently dropped, while the shorter `#stock` reply on the identical `Reply` to `TrySendOrgMessage` path arrived. That is exactly the discriminator the owner supplied.
- `OrgBlobPageSize` reduced to 900 so organization replies paginate under the AO chat ceiling. Guest 8000 and tell 7200 are unchanged; they are not affected by the game-connection limit. Echo confirmation from session 140 remains, so the next run reports `ORG DELIVERY CONFIRMED` per page rather than assuming success.
- `[VERIFIED]` Ambiguous bag phantom proven from the banker's own reports. Kbarty lists 19 normal-inventory storage entries but only 18 distinct identities, and `Inventory.Containers` holds 18 storage containers plus the Colonist: exactly one container for `(Container:BB49D3F)` against two outer entries at inventory/66 and inventory/69. Kbsupp matches with 111 entries, 110 distinct, `(Container:BB49C56)` at bank/1 and bank/92. The container view has no record for the second occurrence, which is the discriminator session 142 was built to obtain. Identity-level dedupe is now evidence-backed rather than inferred.
- `[OPEN]` Dedupe not yet implemented; it selects a slot used for later physical bag movement and remains owner-tested custody code. The exact AO organization-channel byte ceiling is also not established; 900 is a conservative value chosen to sit well under the commonly observed limit, not a measured one.
- Validation: log correlation and source review, `git diff --check`. No assistant compilation or live run.

## Session 143 — org send corrected from SDK disassembly; session 140 conclusion retracted

- `[SUPERSEDED]` Session 140 concluded that organization chat is sent on the chat-server connection and that the raw `GroupMsgMessage` route used the wrong connection. That is wrong and is retracted. Disassembly of the pinned AOSharp.Clientless 1.0.16 `Client.SendOrgMessage(string, bool)` shows it reads `Stat.Clan` (5) from `LocalPlayer`, logs an error and returns if absent, otherwise builds a `GroupMsgMessage` with `MessageType = 3`, `ChannelId` set to that stat and the text, then calls `Client.Send` on the game connection. `GroupMessageType.Org` is 3, confirmed from the `AOSharp.Common` metadata. The repository's raw route therefore emits a byte-identical packet. Organization chat is sent on the game connection and only received on the chat connection.
- `[VERIFIED]` Real defect found: `TrySendOrgMessage` gated the SDK call on `Client.OrgId`, which is populated by `OnOrgInfoPacket`. `SendOrgMessage` never reads that property; it reads `Stat.Clan` from `LocalPlayer`. A zero `Client.OrgId` therefore skipped the SDK path for the wrong reason. The gate now reads the same source the SDK does.
- `[VERIFIED]` Owner evidence changes the diagnosis: `#stock` reaches org chat while `#help`, `#cloak` and `#status` do not. All four take the identical `Reply` to `TrySendOrgMessage` path with the same org `ReplyTarget`, and `ProcessBankerStockCommand` performs no retargeting. The transport therefore works and the payload is what differs. Every org attempt now records its own `len=`.
- `[VERIFIED]` Not the AOChatProxy update. `Could not obtain LocalPlayer org stat` is recorded in project history on 2026-09-06, twelve days before the proxy change, and inbound org chat is healthy in the same run.
- Diagnostics now report `Stat.Clan`, `Client.OrgId`, `Client.OrgName`, the observed channel id and the payload length on every attempt, so the next run distinguishes a missing organization stat from a payload the channel refuses.
- `[OPEN]` Why a longer or blob-bearing payload is dropped on the organization channel while `#stock` succeeds. Needs one `#stock` and one `#help` on org from the same run with their `ORG SEND` lines, which now carry lengths.
- Method signatures, enum values and visibility were read from the pinned package metadata rather than inferred: `Stat.Clan = 5` confirmed by its enum neighbours `Flags=0, MaxHealth=1, Mass=2, AttackSpeed=3, Breed=4, Team=6`, alongside `RunSpeed=156` and `Health=27` which the repository already uses. `TryGetStat` and `SendOrgMessage` are public; `set_OrgId` is assembly-internal and was not used. No assistant compilation or live run.

## Session 142 — a held banker records its own evidence

- `[OWNER-DIRECTION]` A clientless banker cannot be inspected with the ordinary AO client: this process already holds that character logged in. Any diagnostic plan that depends on an operator looking in game is invalid for these characters. Instrument the bot instead. Kavey's framing: the bot is the other half of the work, so it gathers the evidence and the assistant analyses it.
- Added `AmbiguousBagReport`, written from `WaitForStagingChange` into `data/diagnostic-dumps/ambiguous-bags-<character>-<utc>.json`. It records the banker's own live view: every normal-inventory and bank outer item with slot type, slot instance, masked slot, container identity and type, AOID, high ID, QL, name and the storage-bag and normal-inventory predicates; every entry in `Inventory.Containers` with identity, handle, open state and item count; each duplicated storage identity with all of its occurrences; and summary counts including storage bag entries against distinct identities.
- `[INVARIANT]` The report is read-only. It moves nothing, opens no container, sends no packet, and swallows its own exceptions so a failed report can never turn a diagnosable hold into a crash. It is written once per observed layout, because `WaitForStagingChange` is only reached when the layout has changed.
- `[DECISION]` The container listing is the discriminator being sought. A stale outer-item record should have no live container of its own, so comparing the outer listing against `Inventory.Containers` should show which of the two occurrences the client can still reach. That is a hypothesis this report is designed to test, not an established fact.
- `AmbiguousBagReport.cs` was added to `CityBankers.csproj`; that project lists its sources explicitly, so an unregistered file would have compiled to a missing-type error. `Item` and `Container` member usage and the `AOSharp.Common.GameData` import were verified against existing call sites before use.
- Validation: source review, field-type and signature matching for `_settings`, `_character`, `_role` and `GetDataDirectory`, brace and parenthesis balance, project XML parse, `git diff --check`. No assistant compilation or live run; owner builds and runs.

## Session 141 — census withdrawal confirmed live; ambiguous-bag hold made actionable

- `[VERIFIED-LIVE 2026-09-18T13:09]` The session 137 census withdrawal fix works. Owner run logged `Census 9501064256b24bf489c6d8fedf7145ce withdrew Kbarty, Kbsupp; 7 participants continue without restarting their audits`, followed by seven uninterrupted audits progressing 25/110 and 50/110 with no `Census superseded` and no restart. The livelock diagnosed in session 136 is resolved.
- `[VERIFIED]` The two held bankers each carry exactly one extra bag entry. Kbarty has 19 normal-inventory bags where every other banker reports 18; Kbsupp has 93 bank bags against a baseline of 92. No slot is doubly occupied: two distinct slots each report the same container identity, with identical contents, and it reproduces on every fresh login.
- `[INVARIANT]` A container identity belongs to exactly one physical bag. Two entries sharing one identity therefore cannot be two bags; one is a stale client record. `TryValidatePhysicalLayout` now says so and reports the bag name, QL, both locations, and the storage bag entry count against the distinct identity count, so the discrepancy is visible rather than implied.
- The staging hold previously advised the operator to "request recovery", which names an internal `.recovery.json` file mechanism with no operator-facing command. It now states the action that actually clears the hold: move the affected bag with the game client so the server reports it once, after which the existing observed-layout re-evaluation releases the banker automatically.
- `[OPEN]` No auto-resolution implemented. Deciding which of the two entries is stale would select a slot for later physical bag movement, and an incorrect choice issues a move against a slot that does not hold the bag. That is custody-moving code which cannot be tested here, so it is not written on inference. The owner observation needed first is what the ordinary game client shows at Kbarty inventory 66 and 69 and at Kbsupp bank 1 and 92.
- Diagnostics only; no custody or census logic changed. `Item.Name`, `.Ql`, `.Slot`, `.UniqueIdentity` were verified against existing call sites before use. Validation: source review, brace and parenthesis balance, `git diff --check`. No assistant compilation or live run; owner builds and tests.

## Session 140 — org replies routed over the chat connection

- Owner reported `#help` accepted in org chat (`ORG COMMAND [Athen Paladins] Kavem: #help`), logged as `Org reply sent directly to observed channel Athen Paladins`, with nothing delivered in game. The same pattern is present in the earlier 12:25 run, so this predates session 137 and is not a regression from it.
- `[VERIFIED-CODE]` Cause: `TrySendDirectGroupMessage` publishes a `GroupMsgMessage` through `Client.Send`, which is the **game-server** connection — the same call used for `ToggleCloakMessage`, `CharacterActionMessage` and `SocialActionCmdMessage`. Organization chat is carried by the **chat-server** connection: it arrives on `Client.Chat.GroupMessageReceived` and is sent with `Client.SendOrgMessage`. Every working chat path in this repository goes through `Client.Chat`. The game server discards the org packet without error, so the raw write never threw, always returned success, logged a delivered reply, and shadowed the `Client.SendOrgMessage` fallback below it, which was therefore never reached.
- `[DECISION]` The chat route is attempted first whenever `Client.OrgId > 0`. The raw game-connection route is retained as a last resort but is no longer reported as a delivered reply: it sets outbound health to degraded and logs a warning naming it as unverifiable. The observed `Client.OrgId` value is now included in the detail so a future log shows whether the chat route was even eligible.
- `[INVARIANT]` A raw socket write is an attempt, not a delivery. An organization reply is proven delivered only when the chat server sends it back. `NoteOrgEchoPending` records the outbound text and route; `ObserveOrgEcho` is called from the inbound org handler and before each new send, logging `ORG DELIVERY CONFIRMED via <route>` or `ORG DELIVERY UNCONFIRMED: no echo observed within 15s via <route>`. Matching tolerates server-side decoration of blob replies by accepting a 32-character prefix. This reports evidence only; it never retries, blocks or duplicates a reply.
- `[OPEN]` Whether `Client.OrgId` resolves on this build is not yet known; session 51 recorded AOSharp failing to obtain the LocalPlayer organization stat, which is why the raw route was introduced. The next run's log now states the value and which route ran, which settles it either way.
- `[HISTORICAL]` The 2026-09-06 "direct organization packet route" was a workaround for that missing organization stat. It is not deleted, but it is demoted and truthfully labelled rather than presented as a successful send.
- Validation: connection-boundary analysis across every chat and game send in the repository, definite-assignment and lock-scope review, brace and parenthesis balance, `git diff --check`. No assistant compilation or live AO run; owner builds and tests. No owner changelog entry supplied or added.

## Session 139 — packet guard enum combination corrected

- `[VERIFIED]` Owner build log retained the exact throwing line, which session 134's added diagnostics made possible: `build/Protect-ClientlessPacketArrays.ps1:173`, `$attributes = [Mono.Cecil.MethodAttributes]::Assembly -bor [Mono.Cecil.MethodAttributes]::Static -bor [Mono.Cecil.MethodAttributes]::HideBySig`, with `System.InvalidCastException` and a stack frame reading `CallSite.Target(Closure, CallSite, MethodAttributes, MethodAttributes)`.
- `[VERIFIED]` Cause: `Mono.Cecil` attribute enums are ushort-backed. PowerShell routes a bitwise operation on two such operands through a dynamic call site and throws `InvalidCastException: Specified cast is not valid`. Session 134 correctly moved Cecil *operand* mutations into a typed bridge but this flag combination remained a PowerShell expression, so the same class of failure persisted.
- Flag combination now happens in the existing typed C# bridge, which handles `|` on a ushort-backed flags enum natively. The bridge is bumped `CecilOperandsV1` to `CecilOperandsV2` so a behavior change carries a distinct type identity, and all four references were updated together. A stale `CecilOperandsV1::ReplaceAllocation` call at line 271 was found during that sweep and would otherwise have been the next build failure.
- Comments at both the bridge property and the call site record why the expression must not be inlined back into PowerShell.
- `[VERIFIED-CODE]` No other bitwise-on-enum hazard remains in the build scripts. The only other such operation is `Write-BuildIdentity.ps1:55` on `[IO.FileAttributes]`, which is Int32-backed and therefore safe. The three `[Mono.Cecil.ParameterAttributes]::None` uses pass a single value to a constructor and involve no dynamic bitwise operation.
- Guard scope, branch redirection, short-branch widening, stack sizing, bound logic and repeat-build validation are unchanged. The five C# projects already compiled in the owner's run; only the post-build hardening step failed.
- Validation: source review, reference sweep for the renamed bridge, and underlying-type analysis of every bitwise enum operation in `build/`. No assistant compilation, PowerShell execution or live run; Claude Code containers have neither .NET nor PowerShell. Owner rebuilds.

## Session 138 — ambiguous bags are duplicate observations, not duplicate bags

- `[VERIFIED]` From the owner's `data/storage-state.json` (`UpdatedUtc 2026-09-18T08:38:05Z`) measured against the 2026-09-11 baseline: only the two blocked bankers carry an extra bag. Kbarty 121 vs 120 with `(Container:BB49D3F)` repeated at inventory/66 and inventory/69; Kbsupp 111 vs 110 with `(Container:BB49C56)` repeated at bank/92 and bank/1. Kbinfa, Kbcont, Kbexte, Kbphatz, Kbdyna and Kbspirit match their baseline exactly with no repeated identity.
- `[VERIFIED]` Both are repeated observations of one physical bag. Kbarty's two entries share the same handle 316 and hold an identical set of 21 items in identical inner slots. Kbsupp's two entries have different handles (114 and 296) but an identical set of 9 items, matching session 133's note that duplicate containers can present different outer addresses. No item multiset is doubled; the duplication is in the observation, not in physical stock.
- `[VERIFIED]` Not a transient single-session artifact. The persisted state observed at 08:15/08:38 carries the same identities at the same slots that the 12:11 run reported live, so a host restart does not clear it and the condition reappears from live observation each session.
- Owner action recorded in `docs/CENSUS_LIVELOCK.md`: log the affected character in with the ordinary AO client and physically move the bag so a fresh server-side observation is produced. `StartupCensusGate` re-evaluates a staging block when `InventoryLayout()` changes, so the banker retries by itself afterwards. Do not delete data files to clear the hold.
- `[OPEN]` The origin of the aliasing is still unestablished, as in session 133. This records what the evidence shows, not a proven cause.
- Analysis of owner-supplied snapshot only. No assistant compilation, test suite or live AO run. Snapshot analysed and never committed.

## Session 137 — census withdrawal replaces cycle supersession

- Implements the session 136 diagnosis. `Coordinate()` no longer replaces the collecting cycle when a participant loses presence; it removes that member from `cycle.Participants` and keeps the cycle id stable. A banker retires its own audit only when it observes a changed cycle id (`StartupCensusGate.cs:396-402`) and only acts on a cycle that `Includes()` it, so withdrawing one member no longer cancels healthy or already completed peer censuses.
- Guards retained: an explicit recovery request still supersedes, because that is a deliberate instruction rather than an absence; a member that already wrote a census for the cycle is not withdrawn and its physical evidence stands; Central is mandatory, so the cycle is still replaced if Central would be dropped or nothing would remain. `CensusApplication.Apply` requires Central and would otherwise throw.
- `[VERIFIED-CODE]` A smaller roster is safe. `CensusApplication.Apply` scopes reconciliation to the characters that produced a census; anchors outside that scope are skipped rather than reclassified, and `bundle.Storage.Workers` carries every unaudited worker's previous state forward unchanged. A cycle that loses a member is therefore the same condition as one created while that member was offline, which is already the normal path since cycles are built from `online` members only.
- Fail-closed behavior is unchanged. `ReadCensus` still rejects any census with `FailedCount != 0` or `OpenedCount != TotalBagCount`, and an incomplete census is still never applied. This change alters which members a cycle waits for, not what counts as acceptable physical evidence.
- Census rejection backoff now escalates 60/120/240/480/960 seconds instead of a fixed 60, and resets when the worker completes a census or reconnects. A persistently unscannable worker can no longer force a fresh cycle every minute and re-audit the whole roster. Each retry logs its consecutive rejection count.
- `[OPEN]` The two ambiguous bags still need owner action (`Kbarty` `Container:BB49D3F` at inventory/66 and 69; `Kbsupp` `Container:BB49C56` at bank/1 and bank/92). This fix stops them from taking the rest of the roster down; it does not resolve them. The inventory-side bag-open escalation recorded in session 136 also remains open pending per-run files under `data/startup-census/`.
- Validation: source and call-site review, participant/cycle-id lifecycle reasoning, `CensusApplication` scope analysis, brace and whitespace checks, `git diff --check`. No assistant compilation, test suite or live AO run; Claude Code containers have no .NET toolchain. Owner builds and live-tests. No owner changelog entry supplied or added.

## Session 136 — startup census livelock diagnosed

- Owner supplied a full console run (2026-09-18T12:11:35+03:00, build `source-44377b3a2496`) showing seven bankers restarting the startup bag audit indefinitely at roughly 100-145 second intervals and never releasing a census. Diagnosis from log plus source review only; no assistant build, test suite or live AO run.
- `[VERIFIED]` Root trigger: at 12:12:09 `Kbsupp` and `Kbarty` blocked on ambiguous bag identities (`Container:BB49C56` at bank/1 and bank/92; `Container:BB49D3F` at inventory/66 and inventory/69). This is `StorageBagPolicy` refusing ambiguous evidence, which is correct. Neither worker appears again in the run; they never audit and never self-recover.
- `[VERIFIED]` The duplicates are new. The 2026-09-11 baseline `data/storage-baseline.json` (runId `20260911-013435-104b6746`) has zero duplicate bag identities across all eight workers. `[OPEN]` Alias origin remains unestablished, as in session 133; do not assert a cause without fresh evidence.
- `[VERIFIED]` Amplifier is an independent code defect. `PhysicalLedgerReconciliation.ReadCensus` rejects any census with `FailedCount != 0` or `OpenedCount != TotalBagCount`, so one failed bag open out of 110 invalidates a worker. `StartupCensusGate` then requeues the audit and, for non-central roles, sets a 60-second presence retry and deletes its own `.presence.json`. `Coordinate()` supersedes the entire collecting cycle the moment any participant loses presence, and every banker seeing a changed cycle id calls `BagAuditAgent.CancelForRecovery()` (`StartupCensusGate.cs:396-402`), destroying healthy peers' in-flight and completed audits. The stepped-aside worker rejoins 60 seconds later and repeats.
- `[INVARIANT VIOLATED]` A worker intending to withdraw from a cycle instead cancels that cycle for everyone; stepping aside and superseding are the same action. `Kbinfa` completed a clean audit at 12:13:46 (`opened=110 failed=0`) and the result was discarded four seconds later.
- `[VERIFIED]` Escalation signature: in the steady-state loop `opened` equals `bankBagCount` exactly for every worker (92/92, 94/94, 102/102) and `failed` approaches the inventory bag count of 18. All bank bags open; inventory-side bags do not. Round one does not show this, so it follows from repeated aborted rounds. `[OPEN]` Mechanism unproven; `BagAuditAgent.DefaultBagOpenTimeoutMs` remains 3000 ms while session 117 raised the local bag-move timeout to 15000 ms. Per-run files under `data/startup-census/` are needed and were absent from the supplied snapshot.
- Explicitly not the cause: the `ArraySerializer` `OutOfMemoryException` on Manager at 12:11:38 is the longstanding HQ packet variant recorded as non-blocking in session 122; the `data.zip` alien-file warning is an owner archive; the `SMALL BACKPACK SHORTAGE Kbspirit` warning is real capacity feedback but does not block the audit.
- `[OPEN]` No code change published in this transaction. Proposed direction recorded in `docs/CENSUS_LIVELOCK.md`: separate withdrawal from supersession, retain successfully written peer censuses across cycle replacement, and exclude a persistently unscannable worker rather than re-admitting it every 60 seconds. Any change must preserve fail-closed behavior; an incomplete census must still never be applied.

## Session 135 — journal repair and Claude session bootstrap

- First session run by Claude rather than ChatGPT. Read-only inheritance pass first: root docs, `docs/` topic files, protocol/cursor/manifest, architecture and recent-session spans of state/history, plus programmatic validation of `memory/JOURNAL.jsonl`. Owner supplied the live `data` snapshot and the memory password; all four encrypted memories decrypted with `plaintext_bytes` and `plaintext_sha256` verified against the manifest.
- `[VERIFIED]` Recovery archive reconciles with Git. Recovered memory metadata for `6010.Navmesh` (2,087,208 bytes, sha256 `d3bbb491…`, blob `7dee622c…`) matches the committed asset exactly on all three values.
- `[VERIFIED]` Journal corruption found at HEAD and repaired. The committed file carried three non-JSON lines — two pasted tool-output lines at the file head (`Warning: truncated output (original token count: 42943)`, `Total output lines: 152`) and record seq78 truncated mid-string by an inserted `…2943 tokens truncated…` marker — and had lost seq79-86 entirely. Cause: a session wrote the journal back from tool output that had been truncated. First occurred at `4a8b3a6` (2026-09-09), was cleanly repaired at `f888aca` the same day, then recurred at `85aba6e` (2026-09-11) and survived seven days and roughly 48 sessions unnoticed.
- Repair restored seq78-86 from the pre-damage copy at `f888aca` (88 records, zero bad lines) and dropped the three junk lines. Surviving records were carried across as raw line text, not re-serialized. Result: 324 records, seq 1-324 contiguous, strictly increasing, no duplicates, zero unparseable lines, and no record readable at HEAD lost. The damaged state remains preserved in Git history at `d454ace` and earlier; this is restoration of destroyed history, not revision of it.
- `[OWNER-DIRECTION]` Kavey directs that durable state is always saved without asking permission. Continuity writes — journal, cursor, state, history, recovery notes — are routine, not requested. This is recorded because the motivation is prior loss of long AI conversations to context limits.
- Added `CLAUDE.md` so Claude Code sessions inherit the protocol automatically instead of depending on the owner to point at `AGENTS.md`. It carries the mandatory read order, writer-lock sequence, owner standing directions, build/test boundary, safety constraints, status vocabulary, the journal-truncation hazard with a validation snippet, and the shallow-clone caveat.
- `[HAZARD]` Never rewrite a repository file from content a tool may have truncated. Append, or read from disk and validate every line parses before writing.
- `[HAZARD]` Session clones may be shallow. `git log -S` then reports the oldest visible commit as a change origin. Check `git rev-parse --is-shallow-repository` before asserting anything about history; this session initially misattributed the corruption for exactly that reason.
- `[OPEN]` Session 133's 30 displaced donor attributions are still not reconstructed. The duplicate-container signature is absent from the 2026-09-18T08:38Z snapshot — 2,636 stock records with zero `(Character, BagSource, BagOuterSlot, InnerSlot)` collisions, ledger agreeing at 2,636 — but that establishes the defect is not recurring, not that prior attribution was restored.
- Validation: source and data review only, plus programmatic journal integrity checks and hash reconciliation. No compilation or live AO test; Claude Code containers have no .NET toolchain. Owner builds and validates. No owner changelog entry supplied or added. Snapshot analysed and deliberately not committed.

## Session 134 — packet guard build compatibility

- Owner build generated all C# outputs, then Prepare-PortableClientless failed with InvalidCastException/MSB3073. The log did not retain the inner script line or stack, so the exact throwing instruction is not proven.
- Packet guard now performs object-typed Cecil operand mutation inside a small typed C# bridge loaded by the existing owner-side PowerShell build step. This prevents PowerShell object wrappers from being stored where Cecil expects MethodReference/Instruction operands. Branch redirection, short-branch widening, stack sizing, bound logic and repeat-build validation are preserved. No runtime/dependency guard removed.
- Failures now print guard stage, exact script location, script stack and underlying exception; partial output temp is cleaned without replacing the original dependency. No credentials or data changes. Source and diff review only; no assistant build/test suite. Owner rebuilds to establish execution success.

## Session 133 — full-log and data-snapshot reliability review

- Owner supplied the complete current data snapshot and runtime log, then authorized resuming the paused review. Manual in-game work was limited to adding personal bank terminals and moving bags/items the prior day; no manual JSON edits reported. Snapshot kept intact, never committed. Reviewed 61,238 current-log lines plus 173,527 previous-log lines and correlated current state, census applications, receipt histories and ledger archives. No assistant compilation, test suites, live AO or live-data edits.
- Proven native cache fault: Clientless1.0.16 Bank.RegisterItems appends complete BankMessage snapshots to persistent bank items. After reconnect, bank bag counts doubled. Banker compatibility guard now replaces the existing bank list immediately before native local-character BankMessage handling, retaining Bank/events. Unsupported cache shape blocks readiness and rejects append. Existing successful reconnect identity/update-pump guard retained; no watchdog added.
- Snapshot proves duplicate container observations, including different outer addresses, escaped prior item-address-only validation. Latest application replaced30 original claims with60 found claims, inflating stock by30 and losing original attribution. Ledger/stock agreement alone did not detect this. No assertion of physical item destruction. Current snapshot is NOT repaired by this code change; fresh verified game evidence is required, and existing displaced attribution is not automatically restored.
- Audit collector and startup staging now reject duplicate bag identities/outer slots before moving; collector rechecks between bags. Movement verification requires one destination and zero source occurrences. Bag opening requires a new matching Container response rather than old IsOpen cache/handle. Reconciliation validates bag identity and outer slots independently of contents, including empty bags and bag/loose collisions. Failed/canceled/incomplete audit summaries no longer say COMPLETE. Fresh-process alias origin remains unproven; guards reject it rather than silently deduplicate.
- Shared host domain lifetime sponsors replace the finite buddy-only renewal and cover all factories; disposal falls back to direct AppDomain unload with explicit warning, retaining failed cleanup handles. Partially started bankers/auditors are disposed; successful cleanup releases sponsor identity. Manager/Buffer real cleanup errors propagate. Stop-source and shutdown-phase messages distinguish cleanup failures from earlier component failures. Original logs prove expired remoting cleanup failures, not the initiating stop cause.
- FileSnapshot allows readers to share replacement, publishes through unique adjacent temps with only bounded publication retries, and never deletes the last good destination as IOException fallback. Tell heartbeat errors are isolated from sender/scheduler flow with failure/recovery messages. Buddy telemetry and home directives share the helper; snapshot deletion shares its writer lock. Queue assignment, pacing, send and completion policy unchanged. All7748 retained tell acknowledgements succeeded; snapshot has no queued/failed tells.
- Donation admission and ongoing donation processing now use the same persistent dispatch-queue check, fixing welcome-then-immediate-rejection while prior storage acknowledgment is pending. All682 applied custody receipts conserve recorded item multisets;44 withdrawal records are terminal, and completed delivery IDs are absent from live stock. No supported extra CRU/trade-handshake rewrite.
- Runtime inventory recognizes owned Buffer state/assets, items/cache/pairs, events, incidents/traces and historical Colonist migration records. Retained safety-write temps are identified as unpublished evidence, never deleted.
- Deployment preparation adds a shape-validated, repeat-build-checked direct allocation bound for PlayfieldDynelInfo arrays using their five32bit wire fields. Malformed HQ count cannot request an impossible allocation. This is NOT HQ schema decoding; direct serializer path only. Buddy SimpleCharFullUpdate trailing layout remains unsupported; missing nano254847/254848 definitions exist in neither pinned native index nor attached item definitions. References to those IDs are not definitions. Do not fabricate either protocol or metadata, and do not equate these decoder errors with heap exhaustion or missing bank stock.
- Validation: independent read-only source reviews, exact native API/event ordering and IL stack/reference checks, XML/source inclusion and git diff --check; owner builds and live-tests. No owner changelog entry supplied or added. See docs/RUNTIME_RELIABILITY_REVIEW.md for coverage and remaining limits. Keep manager/banker/buffer chat,104 city-buddy game-only mode and working tell queue semantics intact. Optional KWorker integration remains separate.

## Session 125 — transaction incident evidence and Manager dumps

- Owner requests donations AND retrievals, all lost/found-producing events, transaction-only sequences through recovery, and small separate debugging artifacts. Equal lost/found counts are not proof of identical item occurrences. No raw runtime-log extraction or fabricated historical causes.
- Added shared IncidentJournal with per-trace JSONL local evidence, named cross-domain mutex, bounded100ms acquisition, persisted state deduplication, problem markers and best-effort evidence-gap warning. Explicit links join transaction/batch/retrieval/ledger/recovery IDs. Successful structured evidence remains to diagnose later anomalies; no deletion policy. Receipt/phase/queue/withdrawal/ledger/lost transitions and recovery outcomes are instrumented. Old terminal trade handles are cleared to avoid attaching later idle recovery to the previous trade.
- Manager alone renders incident files automatically (15s first,30s interval) regardless of syslog Enabled. Uses shared durable local evidence rather than optional syslog IPC queue; no external sends or raw-console relay. Preserves existing bare dump. Added administrator dump incidents and dump incident-ID, plus lost/found evidence links. Full UTC timestamps, explicit latest states and gap/truncation notices. Exports bounded64 linked traces/5000events with originals retained. Signature includes source length/mtime so concurrent new evidence cannot be hidden by a newer export timestamp.
- Lost before-removal/confirmation/exclusion transitions retain original claim and reason. Committed found/location changes retain before/after records; unmatched claims list same-template candidates without asserting identity. Recovery applied is distinct from proving the original cause. Multiple related problem IDs may refer to one multi-batch donation and follow each other's evidence links. Unresolved/incomplete traces remain available.
- docs/INCIDENTS.md documents commands, paths, coverage and limits. Source/API, XML, locking, path validation, durable-state integration and diff review only; no assistant compilation/test suite/live run. Owner compiles/tests. Existing already-recorded losses cannot acquire missing pre-deployment transaction history.

## Session 124 — banker reconnect identity and update-pump guard

- Owner logs establish repeated TCP success followed by roughly302 seconds in Authenticating, then disconnect/30-second retry. No return to InPlay is shown for affected bankers; Manager correctly reports them offline. Owner reports this did not formerly hang. Logs do not identify the initial cause or prove which source hazard caused these stalls.
- Official Clientless reference has two concrete hazards: Reconnect resets its cookie/message counter but leaves Client.LocalDynelId populated, while login message headers take that old ID; UpdateLoop.Run has no exception boundary and starts an unretained Task, so one escaping Client.Update/plugin/chat exception can permanently stop packet draining while socket reconnect callbacks continue. No claim that a particular exception was observed in owner logs.
- New banker-only ClientlessSessionGuard resets LocalDynelId through its verified internal setter on disconnect; native character selection restores it. It wraps the existing UpdateLoop Action<double> callback using verified private field names, first binding when the SDK exists (initial packet or update). Escaping exceptions are logged by type/stack with30-second throttling and the existing pump continues next tick. No new pump, watchdog reconnect, concurrent game loop, credential mutation or forced audit. Reflection shape changes report an explicit error; teardown restores only its own callback. No automatic resurrection of an already-dead runtime loop; deploy through owner rebuild/restart.
- Login progress exposes ServerSalt/CharacterList/LoginError stage names only. Console now retains SDK game-state transitions and connection/login failures previously hidden by the debug filter; full timestamps and raw file remain. No credential/salt/packet/account-list logging. These observations distinguish successful authentication/InPlay from mere socket connectivity on the next run.
- Idle disconnect/rejoin census policy is preserved. Protection catches escaping exceptions; it does not repair deterministic failing plugin logic or unblock a synchronously hung callback. First already-executing SDK tick cannot retroactively be wrapped. Post-login service recovery still requires owner live evidence; do not claim verified reconnect success.
- Static reference API, field/delegate shape, lifecycle, exception, XML and diff review. No assistant build/test suite/live login; owner compiles/tests.

## Session 123 — idle disconnect availability and lightweight reconnect

- Reopened excessive audit policy: owner log shows Control disconnect alone causing eight other bankers to audit for about80 seconds. Session122 only suppressed callbacks from already-excluded epochs; it did not solve initial idle disconnects. Startup audits remain a separate existing policy.
- Released census no longer restarts solely because a member heartbeat/presence disappears. Existing readiness checks mark absent members unavailable; healthy members retain their cycle. Disconnect preserves a settled idle snapshot only when the banking actor has no retained transfer, receipt, storage, withdrawal, CRU, extraction or recovery work and no pending durable dispatch/census ownership. Actual interrupted work still takes the existing conservative recovery path; this change is not a rewrite of every custody-recovery trigger.
- Same-process idle reconnect waits for bank opening and five seconds of stable fresh inventory, checks retained actor/peer work, and rebinds only that member to a fresh connection epoch under the coordinator mutex. Top-level slot/bag identities, item templates, QL and known CRU counts must match for audit-free resume. Failed reconnect attempts retain the original proof; independent global recovery supersedes it. Readiness and operational heartbeat stay withheld until validation. Rebind publication is retryable after partial file publication.
- A changed idle reconnect snapshot acquires the existing single-banker local census, without forcing other bankers to recount. Unavailable local ownership stays held for retry; exceptions cannot release provisional readiness. Explicit/independent recovery and newly detected unresolved item work still fail closed.
- This trusts previously reconciled contents of unchanged bags across an idle network reconnect; it does not prove unopened bag contents against out-of-band manual edits. Fresh process startup still scans, and existing item-operation validation remains. No broad claim that all audits or all disconnect recovery are removed.
- Static SDK API, state/epoch, mutex/publication, local ownership and exception-path review plus git diff --check. Owner compiles and live tests; no assistant build/test suite/live run.

## Session 122 — excluded disconnects no longer restart the roster

- Owner log shows Artillery/Support disconnect callbacks recurring about331 seconds apart; Support follows Artillery by about14 seconds, superseding healthy audits. Seven remaining bankers repeatedly complete census. Underlying connection failure is not diagnosed from this console log.
- StartupCensusGate now retires local readiness, operational presence and audit ownership on every disconnect, but publishes a new global recovery only if the retired connection epoch belongs to the current cycle. Membership check and request publication share Coordinate's mutex, preventing an excluded reconnect failure from restarting an already reconciled roster. Existing independent recovery requests remain intact; membership/publication failures retain fail-closed global recovery.
- Active-participant disconnects still reconcile interrupted custody. Returning connections still join through the existing fresh census; no stale ready token or audit result can release them. This is a bounded fix for repeated excluded/offline callbacks, not replacement of all global recovery with per-banker recovery.
- Owner confirms UDP syslog reception. Spirit120/156 capacity is knowingly accepted; a second Spirit banker is deferred. HQ-specific packet deserialization warning is longstanding and non-blocking per owner; do not repeatedly present it as a new blocker or infer process memory exhaustion from it. Full timestamps retained.
- Static review of active/excluded disconnects, coordinator ordering, existing requests and reconnection admission; git diff --check. No assistant compilation, test suites or live AO run; owner owns those. Connection failure cause remains unconfirmed.

## Session 121 — portable success bypasses legacy diagnostics

- Owner confirms console manageable and log appears settled. New log shows all nine BANKER READY, no repeating local census loop. Syslog configuration accepted; sender reports SocketException, but old log omitted socket code so refused/unreachable cannot be distinguished. Separate AO packet-deserialization ArraySerializer OutOfMemoryException appears once and Manager continues; not evidence that syslog queue exhausted memory.
- Owner clarified: move old diagnostic sequence down fallback order, not merely hide output. Added separate CompleteBankOpen path for portable/already-open success; bypasses CompleteDiagnostic and position/dynel/player snapshots. Writes only bank-open/readiness/capacity data under existing result-file contract, one confirmation and structured event. BankOpenOnly flag prevents host diagnostic presentation on this path. Retained full discovery/diagnostic/reporting for portable absence/Use failure/timeout; no bank-ID code deleted. Disconnect resets mode; late-open publication follows active mode.
- Syslog failures now include transport, configured destination, SocketErrorCode/native code/message (or exception detail); no receiver configuration or connectivity conclusion invented. Owner may test UDP. No live network action performed.
- Static path/call-site/diff review only. Owner compiles and tests.

## Session 120 — nullable ledger HighId compile fix

- Owner build log: CityBankers CS1503 at BankingServiceAgent.LocalCensus.cs344, nullable int HighId passed to int parameter. Five other projects succeeded/up-to-date; no live validation implied.
- Ledger personal-item predicate now uses HighId ?? AoId, matching existing ledger materialization fallback. Low ID remains checked; absent high ID does not invent another item identity. This corrects session117's compile oversight without changing live-item comparison or syslog behavior.
- Static type/call-site and diff review only; owner compiles/tests.

## Session 119 — Manager-owned structured syslog events

- Owner corrected initial raw-console forwarding proposal: banker identifies itself, reports to Central, Central reports to Manager; only Manager has logging authority. Owner explicitly selected structured events rather than forwarding every diagnostic line. Full local diagnostic logs retained. Unpublished raw-tee sender plan abandoned; no tee changes published.
- Optional root Syslog {Enabled:false,Host:"",Port:514,Transport:"tcp"}; supports plaintext TCP (RFC6587 octet counting) and UDP. Only Manager creates sender. Source hostname automatic, APP-NAME citydwellers, host PID preserved, body contains original character/actual Client.LocalDynelId (unknown before available), source UTC time, severity, stable event ID and data. No identity guessed from text.
- New shared ServiceEvents reports asynchronously over process-scoped named pipes through Central. Configured role/source and relay validated; Manager deduplicates recent4096 IDs, writes data/citydwellers-events.jsonl, queues syslog. Initial events cover bank opening/readiness, census start/apply/recovery, existing transfer stages and Manager cloak observations/startup. This is not yet instrumentation of Buffers/Buddies/Flipper or every diagnostic event.
- IPC256/sender1024 queues bound resource use; transient failures retry away from game threads, overflow warns locally. IPC acknowledgment is in-memory receipt, not disk durability. Bounded shutdown/crashes can lose pending reports; TCP retries can duplicate after ambiguous sends. Stable ID permits deduplication. No automatic disk replay. Existing raw diagnostics retained; Manager JSONL retained for server loss. Large UDP messages over60000 bytes are skipped with local notice (persisted JSONL remains); TCP preferred.
- docs/SYSLOG.md includes config, rsyslog source-IP formatting/full event timestamps, retention note, ccze limitations, exact-time commands. tools/citylog.py filters timezone-aware original event intervals, bot/event and rotated gzip files; no dependency beyond Python3. Receiver was not accessed and no logs sent by assistant.
- Static source/API/framing/lifecycle, project XML, Python AST and diff review only; owner compiles/tests. No live/compile success claim.

## Session 118 — full timestamps retained

- Owner requires full date, milliseconds and timezone in console as well as saved logs. Removed session117 console timestamp shortening; full file timestamps were never changed. Color/noise filtering and duplicate-prefix cleanup remain.
- LAN syslog is discussion only: optional plaintext UDP sender, full timestamps/severity/character labels, background bounded queue and retained local log proposed. Exact server address/port/transport not supplied; no sender implemented or network messages sent.
- Static diff review only; owner builds/tests.

## Session 117 — portable-item census loop and readable console

- Owner log contains4813 lines, including3417 SDK MoveToBank lines. Central completes26 audits rather than failing for lack of slots. CRU snapshot has nine singles and a52-unit stack; the detector already excludes CRU. Verified code defect: census excludes personal288762, but DetectLocalInventoryDifference included it, producing a persistent difference after every successful census on all portable-equipped bankers.
- Expected/live loose-inventory comparisons now both exclude personal service items. Live detector and census snapshot share normal-inventory bounds; worn items cannot manufacture a mismatch. Ordinary stock discrepancies still trigger recovery. Local census start now reports its reason. Legitimate local holds no longer emit the misleading startup-handoff warning; token ownership checks remain intact. Local bag-move timeout now matches startup15000ms rather than legacy3000ms; no claim that the log proves every partial audit's cause.
- Console retains warnings/errors, audit progress and completion but hides SDK MoveToBank chatter, debug/verbose lines and the repeated audit tutorial. Thread-local line assembly under the existing output lock, short timestamps, duplicate banker-prefix removal and severity colors improve console readability. Full original diagnostics remain in data/citydwellers.log. CITYDWELLERS_VERBOSE_CONSOLE=1 restores uncondensed console detail. This is a focused first logging cleanup, not removal of all old logging paths.
- Owner log verifies portable bank results for all nine; Colonist completion on seven with back equip, Central/Dyna absent. Removed temporary ColonistBackpackRepair and hooks/project entry per prior one-run direction. Permanent Small Backpack99228-only storage policy, personal exclusions, shortage warning and portable fallback remain. Completion markers retained as history.
- Source/diff/project XML review only; owner compiles/live tests. Post-fix absence of repeated audits is not yet live-verified.

## Session 116 — Small Backpacks only

- Owner clarified that mentioning worn bags as an alternative never authorized their use. StorageBagPolicy now accepts only Small Backpack99228 in normal inventory or bank; equipped bags and all other types are excluded across existing selectors and capacity diagnostics.
- Startup logs SMALL BACKPACK SHORTAGE once per audit run when eligible bags cannot cover the configured finite copy limits. Existing capacity report remains available. Shortages never authorize equipment use. Colonist migration and portable-first opening retained.
- Default symbiant capacity: Artillery251 types/120 bags, Infantry229/110, Control231/110, Support231/110, Extermination235/112; ten copies per type,21 slots per bag. Counts exclude worn Colonist.
- Static review only; owner compiles and tests. No live success claimed.

## Session 115 — one-run Colonist repair and portable bank

- Owner reports session114 build succeeds and tested behavior works; not all features tested. Reports worn Colonist backpacks found in storage holding symbiants, and manual rearrangement on Support. Code evidence: StartupCensusGate staging chose any Inventory.Items container without a normal-inventory slot check; official clientless collection includes equipped items. This is a concrete unintended-unequip path, not proof that all historical audits had this cause.
- Permanent StorageBagPolicy rejects equipped and Colonist296977 containers in audit enumeration, staging, storage/withdrawal/extraction/recovery selectors and cached storage-container lookup. No bag-count/order assumptions are restored. Portable288762 and Colonist equipment are excluded from physical stock/routing reconciliation.
- Temporary ColonistBackpackRepair runs under the coordinated startup pause before census. Opens the source, stages Small Backpacks99228, transfers one item at a time with full source/destination multiset verification, returns staged targets, equips the empty Colonist on the named SDK back slot and verifies. Cannot replace other back gear or use equipped bags for staging. Unverified actions stop without resending or releasing partial results. Persistent per-character completion markers make it one-run; interrupted work uses fresh physical contents. Normal census then reconciles the owner's actual layout. Removal instructions: docs/COLONIST_REPAIR.md. Remove only the temporary helper/hooks after owner reports all completions; retain permanent protection.
- Portable bank opening precedes dynel scan/old office fallback, verifies bank-open within8s, falls back on absence/failure and retries failed portable-backed opening every30s. Existing real-terminal/bankid code retained. While portable is present, failed opening does not advertise BankNeedsId. No automatic item use outside banker runtime and no live action by assistant.
- Static source/API, lifecycle, movement-conservation, project/XML and diff review only. Owner compiles/runs and supplies logs; no assistant build/test suite. Supplied item IDs are owner-authoritative; portable item page also confirms Open Bank on use.

## Session 114 — buffer build corrections

- Owner build log reports CS0122 from BufferSettings calling private SettingsPaths.GetRuntimeDirectory in five plugins, and CS0103 for BufferSettings in the host. Replaced the private call with existing public TryEnsureDirectory. Host now explicitly links the shared source in its csproj; the global include excludes that project to avoid duplicate compilation. The host's missing-source cause is not established by the log alone.
- Replaced imported Scriban5.7.0 with exact7.4.0 for the reported vulnerability warnings. Package references now cover both CityBuffers and the host, so host output/binding redirects include the plugin dependency closure. Raised shared direct pins to required floors: System.Buffers4.6.1, Memory4.6.3, Numerics.Vectors4.6.1, Unsafe6.1.2, Tasks.Extensions4.6.3. All projects share the runtime directory; inconsistent versions could overwrite each other. AOSharp versions unchanged.
- Package framework/dependency metadata and Template.Parse/Render API reviewed against official NuGet pages. Source/XML and diff review only, no compilation or live tests. Existing CS0649 JSON-field warnings and Git source-fingerprint fallback are not the reported compile blockers. Owner rebuilds the solution; do not claim an observed successful build.

## Session 113 — initial froob buffer integration

- Imported public Mali buff engine at eb78c7f460a66dba6b1c8f8cf6cf66f5b231cc03 as CityBuffers. Optional Buffers.Froobs entries load under the unified host using official Clientless AppDomains, existing multithreaded log pipeline and serialized static-data warmup. Configuration uses the existing citydwellers.json; no per-character nano maps. Full setup is in docs/BUFFERS.md; provenance in plugins/CityBuffers/UPSTREAM.md.
- Retained Mali nano discovery, casting/team rules and queues. Added City Dwellers status IPC and Manager buffers command. All tells use the existing shared scheduler; Mali replies stay pinned to their originating buffer. The scheduler selects the oldest deliverable tell so an offline pinned buffer cannot block unrelated output. Existing Manager authorization still applies.
- Portable packaged assets; per-character mutable bans/ranks; empty initial privilege lists; received bans persist locally; direct Log.txt writer replaced by host logging. Missing or disabled Buffers leaves existing services alone. Enabled buffers cannot reuse another configured service account.
- Owner approved SDK1.0.91 trial with official Clientless1.0.16. Previous SDK1.0.84 was a historical downgrade after a later SDK ChatHeader.Size failure, not proof that1.0.91 cannot work. No compatible-runtime claim without owner evidence.
- Static source, project/config and whitespace review only. Owner compiles and performs live testing. Paid account rotation/catalogue and duplicate/composite balancing deferred; paid must never serve froob requests. No accounts logged in by assistant.

## Session 112 — late decline during completed-trade verification

- Owner log shows a player trade rejected for pending storage work, worker receipt of the dispatched item, then Central receiving Declined while its earlier Finished receipt was still awaiting inventory verification. The old handler immediately started a global census. Callback attribution is not proven by the log; the ordering is consistent with the unrelated rejection callback crossing the completion window.
- Shared status handling now retains a pending Finished receipt across Declined callbacks for donation, dispatch, withdrawal and return flows. Logs once per receipt; does not reset the verification timer, resend items, infer success, or convert completion into cancellation. Existing exact inventory delta, settling, accounting and mismatch recovery remain authoritative. Declines before Finished keep their existing cancellation handling.
- Static callback ordering, receipt lifecycle and whitespace review only; no compilation or live AO test under owner build policy. Resolved on publication.

## Session 111 — hyphenated character names

- Owner member add Sonstern-1 was rejected by letters/digits-only validation. Member, alt, administrator and ban name validators now permit literal hyphens, preserving the complete character name. Existing trim, length, case handling, authorization and list persistence remain unchanged; add/remove/load share these validators.
- Static four-validator diff and whitespace review only. No live member added by assistant and no compilation/test run. Owner rebuilds then retries #member add Sonstern-1. Resolved on publication.

## Session 110 — build-only Cecil lookup correction

- Owner build restored/compiled all projects but failed portable preparation because generated PkgMono_Cecil was empty. Resolve pinned Mono.Cecil0.11.6 using project.assets.json libraries and packageFolders, including custom NuGet caches. Fail with specific restore/content error if absent. No runtime dependency introduced.
- Replaced deprecated Vector3.LengthSquared with SqrMagnitude as SDK diagnostic directs. CS0649 reports JSON-populated DTO fields, not compiler errors; no blanket warning suppression or data-model rewrite. Git-root warning retains source fingerprint fallback; log alone does not establish whether checkout is nested or path comparison differs.
- Static PowerShell lookup/target argument, XML and diff review only. Owner rebuilds; no assistant compilation or live AO. Existing portability transformer behavior unchanged.

## Session 109 — reproducible portable dependency output

- Gentoo progresses into AO after filename-case and literal-backslash symlink workarounds. New log shows missing item index from GameData backslashes, all bank Use attempts timing out for saved1478799474, and the same Manager packet decode allocation error previously seen on Windows. No bank ID inferred; owner can run existing full-client /bankid near Central.
- Host build now runs Prepare-PortableClientless.ps1 after GameData restore. Build-only pinned Mono.Cecil0.11.6 rewrites six exact clientless string literals in the emitted unsigned DLL: core assembly path and five GameData paths. Portable slashes work on Windows and Unix. NuGet cache/private uploaded sources are untouched. Missing expected literals or a signed dependency fail clearly; already-patched output is accepted.
- Managed output DLL filenames normalize to assembly identity plus lowercase .dll using a two-step Windows rename. Native binaries are skipped. Host project has build-only plugin references, ensuring shared-output plugin builds finish before final transformation. Cecil is excluded from runtime assets.
- Deploy the full newly built release with canonical GameData directory. No manual renames, backslash symlinks or MONO_IOMAP needed for these corrected paths. Existing workarounds in the old target are not automatically deleted; a fresh binary directory can retain the same settings/data. This is a repository build fix, not a claim of publishing a new upstream AOSharp NuGet version.
- Static source-literal, build ordering, XML and script review plus diff check only; no build or live tests. Owner Windows compile and Gentoo execution remain the verification method. Manager deserializer failure and overall Linux/native navigation compatibility are not claimed fixed.

## Session 108 — Mono console startup

- Owner Gentoo run of mono CityDwellers.exe printed mono-service instruction. Host routed every Environment.UserInteractive=false process into Windows ServiceBase, regardless of OS.
- Automatic service detection now applies only to Windows. Explicit console command selects terminal operation on either OS; Linux no-argument startup also reaches console. Unix rejects Windows service install/uninstall/service commands with a useful console invocation.
- Owner rebuilds on Windows, copies release to Gentoo and runs mono CityDwellers.exe console from its directory. This resolves entry routing only; full Linux/amd64/arm64 compatibility remains unverified. No assistant build or live account login. Static branches and diff reviewed.

## Session 107 — native city announcements only

- Owner logs prove GoA cloak relay prose polluted AP cloak history, state and recovery deadline. Both Manager and CityRaidCoordinator (in OrgRankAuthorizer.cs) matched text; Manager did so before organization-channel validation.
- Both listeners now require AO system sender0 and successfully decoded extended city category1001 on their organization channel. Plain chat, bot relays and other channels cannot change cloak state or trigger city raid events. Bobsan alt traffic and normal commands retain existing paths. Flipper observations unchanged.
- Native observations use OrgChat.NativeCityEvent. Legacy OrgChat.CloakAnnouncement persisted state is not trusted at boot; existing live assessment establishes state. Old historical rows remain as recorded; no live data or speculative historical deletion.
- Static handler/call-site/source/restore review and diff check only. Owner compiles and tests; no live observation of the new filter claimed. Resolved on publication. Linux compatibility remains discussion only.

## Session 106 — optional reserve timeout and audit diagnostics

- Saved failure evidence identifies artillery bank-to-inventory move timeout at3000ms, bag still in bank. Later121/121 audit succeeded and Arty became ready. No claim of missing items or permanent Arty failure.
- Phatz startup was blocked by failed extra receiving reserve despite9 free slots. After15s, if the selected bag remains exclusively in inventory and the entire observed layout equals the pre-send layout with a free slot, defer extra reserve attempts until reconnect and proceed to settled physical census. Missing/duplicate/moved identities and no staging space retain blocking behavior. Actual dispatch capacity checks remain enforced.
- Coordinated startup audit commands now allow15s per bag move; manual audit settings unchanged. Fatal audit reasons are printed directly, including saved bag-location details, rather than only generic incomplete-census messages.
- Manager packet-deserializer OutOfMemoryException is separate and remains unaddressed; supplied log shows Manager continuing in play. No claim of a whole-host crash or fix to SDK decoding.
- Static state/identity/settling/deadline and diff review only. Owner builds/tests. Private archive not committed. Resolved on publication.

## Session 105 — worker receiving reserve (resolved on publication)

- [OWNER VERIFIED] Session104 audit repair held: supplied log verifies staging moves and a released nine-banker census. Later two phatz donations remain queued with no trade opening.
- [CAUSE] One staging slot allowed audit completion but failed dispatch preparation's items.Count+1 requirement. Phatz retained 29 inventory bags; two donations needed three slots. Generic busy IPC hid this definite prerequisite failure.
- [FIX] Coordinated census preflight now moves inventory bags into bank one at a time, verifying each as before, until MaxTradeItems+1 (11) slots are free. Enumeration follows the settled final layout. If bank space/bags limit the reserve, audit may still proceed with its minimum staging slot and a capacity warning. All supplied workers have enough bank space for the reserve. No new idle mover or competing operation is added.
- [DIAGNOSTICS] Preparation preserves the exact ready token and passes explicit insufficient-inventory-space and IPC-unavailable replies to Central's existing throttled WAITING report. Other busy prerequisites remain unchanged. Physical capacity is never bypassed.
- [VALIDATION] Static constant/API, task consumer, manifest/readiness and move-settle review plus diff check; no compilation, suites or AO run. Owner rebuild/restart applies reserve and normal census recovery handles the retained donation. No live data edits. CRU unchanged.

## Session 104 — full-inventory census staging (resolved on publication)

- [CAUSE] Six workers in the supplied log had 30 inventory bags and failed before opening their first bank bag. Their 60-second presence retries repeatedly started new coordinated censuses, including healthy workers.
- [FIX] After quiescing trades and settling inventory, startup census moves one inventory bag into available bank space. It waits for that identity in bank, absent from inventory, and a free inventory slot before allowing the collector to snapshot the new layout. The bag stays in bank; census reconciles its real location. No content or custody is inferred from the request.
- [FAILURE] No available staging space or an unverified move after 15 seconds parks the worker outside the roster until physical layout changes, a local recovery request arrives, or it reconnects. No timed rejoin for this staging failure. Other audit-error retry policy is unchanged. This preflight applies to coordinated census, not standalone manual audit mode.
- [VALIDATION] Static lifecycle, existing MoveToBank API, membership, settling and diff review only. Owner builds and live-tests. CRU and runtime bankid unchanged.

## Session 94 — CRU build correction

- Owner build reported CS0136 in the CRU timeout handler. Renamed its outer local to expiredRequest, avoiding the nested callback row declaration. No behavior change. Reviewed the reported diagnostic and focused diff; owner handles rebuild.

## Session 93 — Central CRU supply (implemented; resolved on publication)

- [OWNER SCOPE] AOID 257110 is a normal pickup item with permanent Central inventory storage. Donations merge into a stack; #cru prepares one unit. Existing four orders/three items per order, linked collectors, three-minute monotonic pickup window, normal handshake and backend delivery evidence are reused. No new member quotas or administrative controls.
- [IMPLEMENTATION] Quantity/split/merge adapter retains server inventory Count, AddTemplate.Count and MultipleCount beside Clientless Item objects; operations use the uploaded AOSharp packet-only SplitItem and UseItemOnItem forms. Only CRU is opted in; packet/quantity primitives are reusable. Reserved units stay separate; uncollected units return to the idle stack. Central IPC admits requests against observed quantity and shared reservations, without a storage-worker journey.
- [REPORTING] CRU is absent from ordinary active ledger, stock/donor/lost/found reports and worker routing. Normal delivery writes backend withdrawal evidence and the existing completed-pickup event. Donation event diagnostics retain quantity; no donor credit is fabricated for a merged stack.
- [RECOVERY] Ordinary item census ignores the Central supply. A new actor releases unconfirmed staged CRU requests; physical supply remains. Split/merge waits for observed quantities and never replays a split merely because a subsequent write failed. Unverified CRU-only pickup records no delivery and does not request a bank-wide item census. Mixed orders retain ordinary-item reconciliation.
- [SOURCE EVIDENCE] Owner supplied AO#.zip and aosharp.clientless.zip. Read only; not vendored/published. Source exposes Count and MultipleCount but TemplateAction quantity fields are unnamed. Do not guess which Unknown field is count: raw CRU template fields are logged. If a trade lacks known quantity, it is declined with an explanation; live logs will establish any additional mapping needed. Merge response identity/count sequence is source-informed, not live-verified.
- [VALIDATION] Focused static review of command authorization/IPC, project includes, shared compilation scope, reservation/pickup/expiry paths, donation accounting, and census/report exclusions; git diff --check. No compilation, tests or live AO run per owner policy. Owner is first live tester; report actual failures with STACK/CRU and pickup logs. No standing test gate or deferred extra feature.

## Session 92 — verified pickup completion (resolved)

- [CONFIRMED CAUSE] Owner collected two identical items successfully. Uploaded withdrawal census preserved the error "Pickup completion lacks accepted offer evidence." The throwing callback runs after AO Finished and exact settled inventory verification. Missing intermediate confirmation flags prevented recording delivery; subsequent census removed absent claims and reported two false losses.
- [FIX] Completion no longer requires the intermediate player-confirmed/final-accept flags. It still requires AO Finished, exact settled outgoing inventory delta, Central's accepted offer, a nonempty pickup order, matching offered request IDs and no requested pickup decline. Initial Accept still requires an exact settled offer to the authorized collector. Handshake-driving behavior is unchanged.
- [BOUNDARIES] Confirmed delivery is persisted for the entire pickup before ordinary archival. Actual inventory mismatch, missing offer evidence and conflicting Declined paths retain recovery behavior. No absence-only delivery inference, automatic historical repair or changes to genuine loss detection.
- [DIAGNOSTICS] Log successful verified completion with incomplete intermediate callbacks; withdrawal census now logs its triggering reason at entry.
- [OWNER CLEANUP] Owner chose to stop bots after updating, delete lost.json containing only the two known false incidents, and donate the two collected items back normally. Do not edit their live data, replay old census files, or reconstruct missing historical delivery records automatically.
- [OTHER OWNER EVIDENCE] Running bot logged a newer repository revision during the periodic check; owner confirmed that live behavior. lost/found windows also exercised: zero losses initially, existing donorless stock visible.
- [VALIDATION] Focused static review of exact-offer acceptance, Finished-to-inventory callback, completion guard, declined/mismatch paths and confirmed-delivery archival exclusions; diff check. No compilation/test suites/live runs under owner policy. Resolved on publication; no pending testing gate.

## Session 91 — raid status naming clarification (resolved)

- [OWNER CLARIFICATION] raid status is the sole raid-details command. Removed the newly introduced raid progress alias and its help references.
- [SCOPE] Generic status retains its existing raid information; only the owner name display changes to main via original requesting alt. The detailed raid status window remains read-only. This supersedes session90 alias documentation.
- [VALIDATION] Small command-shape/help diff reviewed; no build or live tests. Resolved on publication.

## Session 90 — alt-aware raid controls and on-demand status (resolved)

- [FIX] Raid ownership recognizes the initiating character or a character linked to the same canonical main. Existing token/stage checks remain; linked alts can configure, start, reopen and cancel their raid. The persisted OwnerName/OwnerId remain the original requesting character; changing controller alts never overwrites that origin.
- [DISPLAY] Status, raid windows and completion announcements display canonical main via original requester when different. Main is cyan and requester lavender; ordinary text remains distinct. Starting on the main shows its name once. Display resolves the existing alt cache; no new alt storage or raid-state schema.
- [COMMANDS] raid status and raid progress are equivalent read-only current-raid windows, usable through existing command-source authorization. Idle replies have no window. Read-only views omit setup/assistance/cancel controls; explicit owner setup remains available through raid and its tokenized buttons.
- [CHAT] Automatic windows remain for setup/assistance choices only. Informational active-stage, cloak-wait and cleanup windows no longer repost automatically. CT-fill instructions remain a short visible message, as do completion/failure notices. Explicit refresh/reopen/status requests respond to their requesting channel; manual recovery still returns a requested status window.
- [OWNER VERIFIED] Owner observed the ten-second buddy delay during a raid and reports it worked well. Owner also reports the existing alts component has been reliable. No timer or alt-storage changes in this task.
- [VALIDATION] Focused static review of ownership/token/source checks, preserved requester persistence, all window call sites, status markup and completion formatting. No build/test suite/live AO runs under owner policy. Resolved on publication; no pending testing gate.

## Session 89 — all current work resolved; standing closure policy

- [OWNER DIRECTION] Completed committed/published work is resolved immediately; no separate owner confirmation, build, deployment or testing is required to close it. Reopen on an actual reported issue. Preserve accurate validation evidence; resolved is not a claim of live testing.
- [RESOLVED] Session88 lost/found bookkeeping and commands are closed. All earlier accepted changes remain resolved. The owner may deliberately delay deployment during a raid or observe the hourly revision refresh; neither is an outstanding task.
- [DEFERRED BY CHOICE] Buddy movement, delivery-history command and CRU distribution remain unimplemented future ideas, not bugs or required follow-ups.
- [CURRENT STATUS] No active work or blocking validation. Await the next owner request. Documentation only; no runtime changes or tests.

## Session 88 — missing-item bookkeeping and public lost/found

- [IMPLEMENTED] Central writes data/lost.json before an existing reconciliation removes an unmatched ledger claim. The complete original ledger row preserves donor/received time, identifiers and last location; the record adds item name, discovery time, reason and census evidence path. The actual loss time/cause are not invented. No history trimming or historical backfill.
- [OWNER DEFINITION] Intentional over-cap deletion is not loss: donors were warned and knowingly accepted deletion. Both verified delivery and deleted_overcap removals are excluded, including confirmed deliveries resolved by startup/withdrawal census. Failures, refusals and retries alone do not create loss records.
- [PERSISTENCE] Strict old-ledger/history reads and write-before-removal; failure to save the loss prevents removal. Pending records deduplicate by ledger ID on retry and are hidden while their claim remains active. Completion marking preserves historical losses; absent-ledger-row fallback handles interruption after ledger commit. A pending attempt superseded by verified delivery/deletion is marked excluded before removal.
- [COMMANDS] Public member commands lost [item-name words] and found [item-name words] are read-only. Count all matching incidents, display newest 25 with item links, donor/time, location and IDs in colored channel-sized windows; zero has no link. Found reads current donorless ledger entries, not a fabricated donor match or separate historical journal. Lost preserves evidence paths for investigation/correlation.
- [SCOPE] Existing census/repair decisions remain unchanged. Ambiguous unmatched claims can correlate with donorless physical rows in found; neither command deletes or repairs stock. Buddy movement, delivery-history command and CRU remain intentionally deferred. Prior owner-accepted fixes remain closed.
- [VALIDATION] Focused static review of all four census call sites, archive exclusions, sole ledger writer, retry/failure ordering, command access/search/count/pagination, shared project includes and diff whitespace. No compilation/test suite/live AO runs under the owner build boundary. Owner rebuilds and checks the new commands.

## Session 87 — owner acceptance: current work resolved

This checkpoint supersedes earlier OPEN/pending-validation notes for current fixes. Preserve those notes as historical provenance, not an active checklist. The owner explicitly accepts all current work as resolved until an actual bug report. Do not reopen speculative tests or cosmetic cleanup on recovery.

- [OWNER VERIFIED / ACCEPTED] Git revisions and update visibility work. Owner deliberately ran an older build, observed the outdated result and its status presentation, and accepts version/update behavior. Hourly refresh was not separately waited for; network-failure handling was not outage-tested. Both are accepted as nonblocking, with no further validation required now. Notifications are log/status based; absence of chat notifications is not a bug.
- [OWNER VERIFIED / ACCEPTED] Symb/spirit offer windows are satisfactory. Current aliases/Ocular corrections and other presentation work are accepted under the owner's all-resolved direction. Cloak history is correct and pretty; guest-channel status fit is accepted even though org remains multipage. No active item-name parser cleanup task remains.
- [OWNER ACCEPTED] Ten-second buddy delay is resolved. Owner has not run a dedicated raid timing test and does not require one now; do not mislabel this as measured timing proof.
- [OWNER VERIFIED / ACCEPTED] Owner performed withdrawal checks with duplicate and nonduplicate items and various other scenarios; everything worked. All prior withdrawal validation follow-ups are closed by owner acceptance. Do not invent a detailed scenario-by-scenario transcript or require repeated tests without a concrete defect.
- [OPERATING REASON] Compiled output is copied from the repository release directory into a separate deployed runtime (C:/release) so the server can keep running a working build while compilation errors are fixed. Preserve this deliberate build/deployment separation; it is not redundant copying.

### Deliberately deferred development — not current bugs

1. Buddy movement/navigation work.
2. Central erroneous-transaction/loss reporting: a command showing what was lost over time.
3. Delivery-history command showing what was given to whom over time.
4. CRU distribution service.

The owner expects underlying loss/transaction and delivery information to remain in data; those reporting commands have deliberately not been developed. This checkpoint does not independently audit data completeness. Inspect the existing persisted records when that future development is requested. Do not implement deferred work automatically.

[CURRENT STATUS] Happy, accepted, no active reported bug or required validation task. Await the next owner request or an actual bug report. Documentation-only checkpoint; no code/runtime/data changes or tests.

## Session 86 — Ocular item-name classification

- [CAUSE] Stock DetectSlot searched for eye but not ocular; Active Ocular Symbiant, Extermination Unit Aban therefore resolved to no slot. This classifier serves all five symbiant families. Session85 command-input aliases did not change stored item-name classification.
- [FIX] Recognize ocular as eye while preserving eye matching. Family slot listings, eye/ocular/occular searches and nearby-QL offers now include stored Ocular symbiants through the same shared classifier. Spirit AOID-based slot mapping is unchanged. No stock migration, inventory movement or ledger edit.
- [VALIDATION] Focused static example/control-flow/diff review only. Owner rebuild and check Eye in the five family views. No assistant build/live tests.

## Session 85 — symbiant/spirit offers in windows

- [ALIASES] Shared stock dictionary adds arti -> artillery and ocular/occular -> eye. Bare family shorthand (e.g. arti head 150) normalizes to symb before ordinary Manager command recognition/access checks; explicit symb syntax remains supported. Canonical navigation commands remain unchanged.
- [OFFERS] QL lookup always selects nearest stocked lower tier, exact requested tier and nearest stocked higher tier, ordered lower/exact/higher. Include every distinct AOID/highID/QL template at those tiers; duplicate copies count on each row. Exact matches no longer suppress neighboring alternatives. Chat shows context and N offers plus View offers; item/GET/back commands live inside its text window. Empty symbiant roots, slotted families/slots and QL searches return zero offers without links.
- [BOUNDARIES] Existing Manager reservation hiding, spirit catalog slot mapping, item identity grouping, GET AOID semantics, membership checks and channel-sized row pagination remain intact. No stock, custody or acceptance-policy mutations. Nearest tiers are chosen from currently available stock, not fictional catalog offers.
- [VALIDATION] Focused static parsing/access-routing/lower-exact-higher/grouping/empty-result/markup and diff review only. No assistant builds/tests/live actions. Owner rebuild; inspect arti head 150, symb arti ocular 150, symb arty occular 150 and spirit slot/QL queries; zero offers has no window. Owner accepted status fitting guest but not org; no further status shortening requested.

## Session 84 — compact status window

- [OWNER DIRECTION] One repository revision is sufficient; no separate component versioning requested. Shorten the visible status only by the explicitly named sections.
- [CHANGE] Single coloured heading: City Dwellers Status - running host revision - online revision. Removed component build list, introductory title/subtitle, recent five cloak diagnostic observations, Useful commands and refresh footer. Manager, current cloak/recovery, workers/activity, bankers and current operations remain intact. Cloak command history remains available. Detailed component diagnostics/startup logs and background update checks are preserved.
- [PAGING] Status uses a custom heading through the existing blob helper, including heading byte allowance and page numbering if still needed. Channel limits unchanged. Do not promise a fixed page count for variable runtime data or remove more fields without owner direction.
- [VALIDATION] Focused source diff/markup/call-site/whitespace review only. No assistant build/live tests. Owner rebuild and inspect actual status page count.

## Session 83 — version discovery, colour and HTTPS update visibility

- [OWNER EVIDENCE] Session82 binaries reported unknown. The old build script silently converted every exception/failure into unknown, so runtime logs cannot establish the exact failed build step. Do not claim a proven specific cause without the owner's new build output.
- [BUILD FIX] Resolve the repository root explicitly, use a char-array TrimEnd, discover Git on PATH and in common Windows installations, capture exit codes/native stderr explicitly and print the failed reason. Successful Git builds retain HEAD[-modified]. Without usable Git metadata, embed source-<SHA256> from sorted source/build assets (excluding generated/runtime/dependency folders), with a warning that Git ancestry is unavailable. This identifies offline source builds without inventing a commit or fetching current master at build time. Existing ignored generated attribute and incremental-build behavior retained.
- [INSTALLED VS LOADED] Inspect PE ProductVersion metadata in the real runtime root at startup and whenever status/dumps read versions; this does not load assemblies or log actors in. Host uses its actual executable location. Keep loaded self-reported revisions independently; show differing installed/loaded versions and host mismatches. Legacy numeric/unversioned binaries remain explicitly unknown. Inactive but versioned components show installed revisions immediately.
- [COLOURS] Cyan names; green known versions; yellow checking; red unknown/missing; amber modifications/mismatches and newer repository availability. Add GitHub master revision/check result to status and structured update snapshot to dumps.
- [ONLINE] One host-only thread-pool timer starts after the configured trusted-time gate and checks immediately/hourly using bounded HTTPS GETs to public GitHub git/ref/heads/master and compare/local...latest. Required User-Agent/Accept/API-version headers; ten-second per-request timeout, bounded response size, no redirects, overlapping checks, credentials, downloads or updates. Compare ahead means newer master, behind means local ahead, diverged is distinct. Unknown/source fingerprints are uncomparable, not outdated. No component is declared outdated solely from a repository difference. Failures are explicitly unavailable and retry hourly; changes in result/latest are logged once. No dependency on runtime Git or wall-clock scheduling.
- [VALIDATION] Focused XML, script/source/control-flow/API contract/metadata/colour/diff review only. No assistant build/test suite/live AO actions. Owner rebuild solution, inspect Git/fallback build output, copy EXE and DLLs, check unused Flipper/Buddies installed revisions and startup/status online result. Runtime Windows build and network behavior remain owner validation.

## Session 82 — embedded build identity

- [CHANGE] Every EXE/plugin build embeds the full Git HEAD in AssemblyInformationalVersion; uncommitted tracked/untracked changes add -modified. Runtime reads its own loaded assembly, never the surrounding checkout or a DLL inspected on disk. Public status uses 12 hexadecimal characters; logs/dumps preserve full revisions. Existing assembly binding/file versions remain unchanged.
- [BUILD] Directory.Build.targets runs build/Write-BuildIdentity.ps1 before CoreCompile for each project, writes the generated attribute into its ignored intermediate directory only when changed, and registers it for Clean. DisableFastUpToDateCheck lets MSBuild refresh Git metadata even without source edits. Git missing, source archive, mismatched enclosing repository or Git failure yields unknown, replacing any stale generated stamp. Build remains Windows PowerShell-based like the existing dependency restore.
- [RUNTIME] Host resets a process-only build registry before launching clients; each plugin registers its own stamp at Init across AppDomains. Status and Manager/bag-audit dump headers show host and reported plugin revisions, explicitly flag differences from host, and label missing reports as not loaded or older binary. Reports represent components initialized during this process, including transient Flipper/Buddies actors after unload; they are not a liveness indicator. A legacy plugin cannot claim the new host revision.
- [DIAGNOSTICS] Startup logs include host and plugin full revisions. Manager diagnostic lines contain its compact build identity; Flipper result JSON, banker diagnostic JSON and buddy navigation trace rows include BuildRevision. Normal operator/tell message content stays unchanged.
- [VALIDATION] Focused XML/source/attribute uniqueness/generated-file/incremental-build/call-site/serialization/diff review only. No assistant compilation/test suite/live AO activity. Owner rebuild solution, copy EXE plus DLLs, inspect startup/status/dump; verify mixed versions report a mismatch or unreported component. Runtime behavior and custody unchanged.

## Session 81 — compact cloak history and small owner adjustments

- [CHANGE] The existing observe-only cloak response appends Cloak History: newest 25 observed org cloak on/off announcements, one row per event, gold UTC date/time, cyan character, green enabled/red disabled, white separator and cloak. Routine probes/cache reads are excluded because their actor is the observer, not the changer. Existing five-row diagnostic status history stays intact. History is also available with unavailable/busy probe replies. No user flip control added.
- [REFERENCE] Read Nadybot CloakController.php (upstream unstable, blob 258254fffd92f20dca01935b4c419ffb0ecf5915): current status plus historical on/off entries; our compact renderer uses existing channel pagination.
- [BUDGET] Org page limit 5600 -> 5200; guest 6500 -> 8000; tell remains 7200. Full rows stay together, with pagination as fallback.
- [TIMING] Owner requested +10 seconds: Wave8OffsetSeconds 945 -> 955 (assistance deadline); actual login is separately controlled by GeneralBuddyStartOffsetSeconds 975 -> 985, so it also moves +10 seconds. Timing text updated; measured wave milestones and cleanup remain unchanged.
- [WORKFLOW] Based on latest master 092b563, preserving published owner edits; publish only this focused file set and reject non-fast-forward updates. Owner builds/live tests. Static diff/API/call-path/markup budget review only; no assistant build or live actions.

## Session 80 — AO-safe presentation markers

- [OWNER VERIFIED] Owner reports everything seems to work as intended; current work is limited to small cosmetic nitpicks.
- [CHANGE] Status rows use green OK or amber WAIT instead of the unsupported filled-circle glyph. Display em dashes become ASCII hyphens and middle-dot separators become ASCII vertical bars across status, inventory, stock, donor and donation output.
- [VALIDATION] Reviewed the focused source diff and whitespace; existing colours, icons, links and operational behavior are preserved. No assistant build or live test. Owner rebuild and inspect the previously affected windows/messages.

## Session 79 — first consistent presentation pass, icons, help and status

- [OWNER STYLE] Preserve the existing donation/raid presentation. Bright white is the base; cyan/blue names and item links, yellow quantities, muted identifiers/parentheses, green verified success and red failure with amber waiting/retries. Org confirmations stay short; full stock/cloak and rich status windows remain. This is presentation work only, not a trading redesign.
- [IMPLEMENTED] Shared palette formats plain diagnostic/tell tokens without rewriting existing font/link markup. Manager replies and live diagnostics, shared dev delivery and banker service tells use it. Transfer telemetry colours the stage and includes intact item links with parenthesized QL; disk logger content remains readable plaintext. Pickup ready/completion and withdrawal admission link the item. Admin/phatz-add confirmations are concise and highlighted. Existing donation styling is retained.
- [ICONS] Owner Library items.zip was materialized read-only. AOSharp Stat.Icon=79 (0x4F); extracted 106593 AOID/icon pairs into deterministic gzip CSV, embedded in Manager and Bankers. Igoc 275423 maps to icon290363. Source dump is not committed. Palette loads the map lazily and emits native rdb image markup (confirmed in Nadybot ItemsController/Core Text). Windows for phatz, stock item results, donor records, inventory and acceptance show icons; short tells use links without images. Missing IDs omit icons and missing QL is not invented.
- [STATUS] Preserve all existing fields; append storage batches/item counts by phase, active withdrawals with requester/item/phase and recovery count, plus pending/assigned tells and idle sender count. Reads existing persisted snapshots only; no custody/queue actions added.
- [HELP] Correct reservation visibility and player confirmation instructions, document flat phatz/multiple messages, add banker/donation and donor help, and clarify acceptance removal/lowered-limit behavior. CRU stays unimplemented and is not advertised.
- [PAGES] Existing 5600 org/6500 guest/7200 tell constants unchanged. Manager stock blobs now pass through the shared per-channel row-preserving pagination, including icon/colour bytes. Existing one-page-per-message delivery remains. White base also applies inside windows.
- [VALIDATION/NEXT] Focused static markup/API/call-site/resource/field/diff review only; no assistant build/test suites/live sends. Owner rebuild and inspect phatz, donor, inventory, help bankers, help get, status in guest/org/tell; compare colours/icons and page boundaries in AO. Session78 player pickup handshake still needs owner live result. This is the first visual pass for feedback, not a claim that every historical string has been hand-restyled. No raw private files, live settings/stock edits, policy alias changes or CRU implementation. Never repeat historical repair; HQ packet issue remains owner-confirmed harmless.

## Session 78 — player pickup confirmation handshake

- [OWNER EVIDENCE] Withdrawal extraction, Central COPY BOUND/verified receipt/readiness and pickup-opened all appear in supplied log. Owner reports Central accepts but no player confirmation dialog appears. Log alone does not identify the missing packet; existing pickup code ignored Accept and only scheduled Central Confirm after receiving Confirm.
- [FIX] Player pickup now handles its own captured-partner Accept/Confirm state, separate from internal bot confirmation. After complete exact offer and initial Central Accept, observed player Accept permits Central Confirm; observed player Confirm permits final Central Accept after 150 monotonic milliseconds, matching the working donation bridge ordering. Duplicate events cannot resend Confirm/final Accept. Added explicit stage telemetry for initial Accept, player Accept, Central Confirm, player Confirm and final Accept.
- [GUARDS] Validate the complete bound-copy offer and empty player item offer before handshake actions. An early Accept requires a fresh Accept after offer completion; changed offer after Central Confirm declines with an explanation. Reset flags on trade reset. Successful accounting still requires AO Finished plus exact physical receipt, now also confirmed final handshake evidence. Internal worker trades and donation bridge are unchanged. No blanket audit-on-cancel introduced.
- [VALIDATION/NEXT] Focused static event routing, captured partner, exact-copy checks, duplicate event guards, monotonic delay, reset and receipt accounting review only. No assistant build/tests/live run. Owner rebuild and test one Igoc: dialog must appear, player confirms, final Accept and Pickup complete follow. Then two identical copies and cancel/reopen. This patch is not yet live-verified.
- [DEFERRED OWNER REQUEST] Preserve existing rich status and add missing operational facts; audit help for current commands; beautify messages consistently with white base, semantic highlights, linked items and parenthesized QL, coloured debug events, discreet org confirmations and existing stock/cloak visibility. Icons may use items.zip plus NadyAP markup research. No presentation implementation in this transaction; trading takes priority. CRU remains discussion only; no live data repair or historical repair repeat.

## Session 77 — flat phatz stock and separate channel-sized blob messages

- [OWNER REQUEST] Display all available phatz alphabetically with one row per exact item/QL and (xN) repeated-copy counts; no manual categories or QL navigation menu. Supplied private snapshot contains 31 exact ID/QL types and 53 phatz copies. Existing reservation hiding still applies before display. Each row has an item link, QL and existing AOID GET command; this does not change withdrawal selection semantics.
- [FIX] Manager bare phatz/phat now renders the complete counted list through the shared blob helper. Search/explicit QL commands retain their existing behavior. No acceptance-policy or stock data mutations, and no low/high-ID alias implementation in this transaction.
- [PAGINATION] BuildBlobLinks now returns separate pages and the Reply overload sends each through existing org/guest/tell transport. Updated all helper consumers, including donor views, help, status, inventories and phatz acceptance. Existing OrgBlobPageSize=5600, GuestBlobPageSize=6500 and TellBlobPageSize=7200 remain the single channel configuration. Budget includes escaped UTF-8 content with envelope allowance; pages split at complete newline rows instead of arbitrary spaces inside links. An individually oversized row is rejected rather than emitting malformed/oversized markup.
- [VALIDATION/NEXT] Focused static call-site/return-type, row grouping, reservation-filter placement, escaping, transport routing and whitespace review only. No assistant builds/tests/live sends. Owner rebuild then check bare phatz and donor top/last with enough content for multiple pages, especially in org: one numbered window link per separate message, valid item/GET links and no cut rows. Existing duplicate-withdrawal live test from Session76 remains pending.
- [DISCUSSION ONLY] CRU stack service is not authorized for implementation yet. AOSharp source contains packet-only SplitItem and inventory packet Count, but clientless quantity tracking/response handling still needs implementation and live proof. Removing/lowering phatz policies and low/high-ID deduplication were discussed; no changes made to them here. HQ packet exception remains owner-confirmed harmless. Never repeat historical repair.

## Session 76 — identical withdrawal copies and clear extraction attempts

- [EVIDENCE] Two fresh requests by the same collector both reached verified Central receipt. The second then failed unique-template identification, and the first pickup also became ambiguous. These logged reservations belong to the current requests; the excerpt does not establish stale historical reservations. Session75 journey milestones and direct ready tell are visible in owner evidence.
- [FIX] After full withdrawal receipt verification, require every pre-transfer live inventory occurrence to remain present and exactly one new matching occurrence. Bind that Item object to the withdrawal ID; persist its arrival slot in receipt evidence. Pickup lookup and final offer checks use that binding, keeping identical copies distinct. Other requests cannot select a bound copy via template fallback. Completed delivery removes bindings; later arrivals prune terminal requests.
- [LIFETIME] Bindings are actor-local and are never deserialized as object references. Available clientless source preserves Item objects through local offer/removal/cancellation, including changed inventory slots. Missing/replaced objects fail closed; new actors/cache replacement continue through existing census recovery. This is not a persistent slot guessed across reconnects. Full inventory receipt verification and exact ledger-ID archival remain mandatory.
- [LOGGING] Log first extraction move with requester, item, bag and inner slot. Retry states that inventory confirmation is still awaited, rather than implying a proven failed first move. Add COPY BOUND requester/slot evidence. Pickup refusals use a direct requester tell and suppress repeated notices while continuing decline requests until closure.
- [OWNER CONTEXT] Manager HQ-only packet decoding exception was previously tested by owner and is considered harmless. Do not pursue it as an outstanding banking defect; earlier open-status notes are superseded by this clarification.
- [VALIDATION/NEXT] Static receipt callback, clientless object lifecycle, identical-copy selection/offer, cancellation, serialization and diff review only. No assistant compilation/tests/live actions. Owner rebuild: request two identical items before pickup, confirm two separate COPY BOUND events and ready notices, collect both and check two completed records; then repeat donation/withdrawal. Cancellation/reopen and separate three-minute expiry return remain live checks, as do broader interrupted-transfer cases. No live data repair and never repeat the historical repair.

## Session 75 — withdrawal journey telemetry and requester readiness tell

- [OWNER VERIFIED] Actual pickup completed after Session74; requester received the item and completion was reported. A separate expired-return test is still pending.
- [CHANGE] Add dev-channel/console milestones for reserved-item extraction, worker-to-Central transfer opening, physically verified Central receipt, and persisted pickup readiness. Each carries withdrawal ID, source/destination or collector, and item name/QL/AOID through the existing isolated telemetry helper. Opening is not described as completed receipt.
- [DELIVERY] Ready-for-pickup notice now uses TellDirectPlayer to the original RequestedBy character. It remains queued/rate-limited but bypasses bootstrap-admin dev-channel redirection. The independent WITHDRAWAL READY event retains operator visibility.
- [VALIDATION/NEXT] Focused static placement/API/diff/whitespace review only; no assistant build/tests/live run. Owner rebuild and verify dev journey plus requester tell, including bootstrap admin. No trade/custody/data changes. Existing expiry-return and wider recovery/packet decode checks remain open; historical repair must not repeat.

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

# Current session 67 checkpoint — physical reconciliation foundation

- Historical repair is owner-confirmed applied: 2300 active entries, 395 excess claims removed, four loss history entries, old Vital dispatch closed. Earlier pending-repair statements below are superseded.
- Owner keeps the whole service stopped. BankersEnabled=false is saved for future use only.
- PhysicalLedgerReconciliation validates complete per-character census including loose bank/inventory and returned bags, normalizes slots, and proposes one ledger row per observed item. Exact anchors are reserved before unique remaining occurrence matches; ambiguous provenance remains unknown. Unmatched claims are not described as proven physical losses. Unknown items target Central; misplaced/loose worker items appear in routing work.
- Startup writes a durable proposal alongside each census; it DOES NOT apply it or release the existing gate. Ledger metadata now supports optional HighId/QL. Legacy stock import no longer invents a bootstrap-admin donor.
- OPEN: central-owned proposal application/history/storage commit, cross-character scope, unfinished custody disposition, automatic extraction/return/sorting, and replacement of conflicting global guards. This is an intermediate source checkpoint, not runtime completion. Owner compilation and live validation pending.

# City Dwellers — Persistent Project State

## Session 67 safety overhaul — in progress

### Owner revision: availability and automatic routing

- Historical repair package prepared separately from the public repository: active
  ledger 2699 -> 2300, 395 excess claims removed, four owner-authorized loss history
  entries with responsibility Kavey and original donors retained; remaining Vital
  dispatch closed inside its loss history record. Not yet applied to owner's runtime.
  All 2300 retained rows uniquely match supplied stock by transaction/location; the
  eight-worker audit matches those items after inner-slot normalization. All 395
  excess claims duplicate occupied same-AOID slots after the same normalization.
- Package preserves existing history bytes, validates original/replacement SHA-256
  hashes, backs up before replacement and supports resume after partial application.
  Windows installer was statically reviewed, not executed here. Payload counts and
  audit multiset were checked. This is approved historical cleanup, not a fresh
  Central census or completed automatic recovery implementation.

- IPC checkpoint: shared/LocalIpc.cs is the common named-pipe transport for Manager,
  Flipper, Buddies and Banker dispatch requests. Workers reserve inbound capacity on
  their AO update thread and acknowledge preparation before Central opens a trade.
  Primary storage replies use IPC; durable result/command files remain for legacy
  compatibility and recovery, and withdrawal/routing migration is not finished.
- Internal offers, confirmations and verified inventory observations get 500 ms of
  monotonic settling. Central releases its trade slot after sender verification;
  worker storage replies are polled independently and busy destinations are skipped.
  Donation eligibility no longer depends on every persisted batch being resolved.
- Host component exits no longer automatically stop healthy siblings. Optional
  top-level BankersEnabled=false runs Manager/Flipper/Buddies without banker clients.
  This checkpoint requires owner compilation; it is not banker runtime clearance.
- Remaining: replace global census/discrepancy gates with physical reconciliation,
  automatic routing (including alien items to Central), and apply approved ledger
  cleanup/history losses. Do not infer those features from the IPC checkpoint.

- This supersedes the session-67 global discrepancy shutdown design below.
  The active ledger represents physical custody, including unknown-origin items.
  Missing items leave availability and enter history/errors; unrelated work continues.
- Recognized misplaced items go to their designated banker. Alien or unresolved
  items go to Central for owner review, subject to physical movability and space.
  No provenance gap should stop the shop, cloak or Buddies.
- Owner authorized removal of 395 excess historical claims and recording the four
  unmatched claims as lost with responsibility assigned to Kavey. Preserve original
  donor attribution in history. The excess rows span multiple donor/baseline labels;
  do not select them by donor name or invent a single source batch.
- Implement paced, acknowledged IPC coordination and per-banker recovery. Do not
  merely remove the existing global gate while stale transfer actors remain active.
  Service is still stopped for implementation; no restart clearance has been given.

- Owner confirmed candidate 22d4e99 compiles; service remains stopped.
- New accounting candidate commits donation/dispatch/verified-overcap accounting
  directly instead of replaying diagnostic logs. Dispatch journals reserve ledger
  occurrence IDs before trade, and later accounting preserves already-stored copies.
  Stock synchronization reserves existing location matches before unmatched copies.
  Accounting failures close the safety gate. These changes are not yet owner-built.
- Historical repair remains pending: unmatched claims must not become fabricated
  delivery/deletion/loss events. Need owner agreement on retaining an unresolved
  incident archive while removing those claims from active availability/queue work.
  Automatic interrupted-custody replay is still not implemented; do not start.

- Owner compile found CS0103 in CityManager donor availability: banker-only
  TrustedOperators was unavailable. Replaced it with City's existing shared
  readiness-marker check, retaining physical-occurrence matching. Recompile
  pending; service remains stopped.

- The manual census now commands all nine bankers (including Central) and prints loose
  bank/inventory observations in its durable report. Missing loose snapshots fail the run.
  Central is explicitly observation-only because storage-state has no Central baseline.
  The automatic gate now reuses the census collector for all nine normal-mode clients.

- Owner has stopped service. Do not deploy intermediate checkpoints as a safe runtime.
- Foundation changes normalize audit inner slots and absent identities, capture loose
  inventory/bank items in audit JSON, and disable additional accounting/handshake/queue
  writers in manual audit mode.
- Implemented candidate: deferred operational initialization, generation-bound census
  readiness, disconnect invalidation and recensus without automatic resumption, flushed
  before/after custody evidence, exact inventory-delta checks for donations, dispatch,
  withdrawal transfers and pickups, and physically constrained donor GET availability.
- Unfinished custody, managed items outside verified storage, and the old custody hold
  intentionally block startup. No speculative loss partition or legacy replay is authorized.
- Still required: owner compilation and controlled live validation; historical attribution
  repair and automatic interrupted-transaction reconciliation remain separate unfinished
  work. See docs/BANKER_CUSTODY_VALIDATION.md. No assistant-side build/live test was run.

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
- `AOSharpSDK` pinned exactly to `1.0.91` for the owner-approved session113 trial; previous baseline `1.0.84`. Not live-verified.
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

## 2026-09-12 — Office bank instance correction (session 95, resolved)

Owner Info Manager evidence confirms the office terminal instance changed from
`1478048485` to `1478332417` at the same position. All nine startup diagnostics
used the old identity and timed out. The fallback default now uses the current
instance. Optional positive integer `Bankers.CityOfficeBankTerminalInstance` in
`citydwellers.json` overrides it at startup, permitting future corrections with
a configuration edit and restart instead of another build. Fallback timeouts
explain the Info Manager comparison and setting. Existing static discovery,
playfield/distance guards and bank confirmation remain. Automatic discovery of
the omitted office terminal is not implemented. Static source/diff review only;
no assistant build, test suite or live AO run.

## 2026-09-12 — CRU donation quantity binding (session 97)

Owner confirmed a 13-unit CRU offer produced TemplateAction.Unknown1=13;
clientless drops this field when constructing the target trade Item. The adapter
now mirrors the native local-recipient/inventory/action-code route and binds
the positive quantity to the one newly created matching offer object after native
handling. No template-wide or slot-reuse quantity inference is used. Native trade
completion moves that same object into inventory, preserving the count binding.
Single-unit intake uses the same field; no live confirmation of that wire value
yet. Static source/diff review only; owner builds/tests. Bank discovery deferred.

## 2026-09-12 — CRU merge interaction correction (session 98)

Owner logs confirm four single-unit CRU donations bind count=1 and arrive. Two
merge attempts using CharacterAction UseItemOnItem (AOSharp CombineWith) timed
out with unchanged individual inventory objects; owner also sees them unstacked.
Merge now mirrors the separate uploaded AOSharp Item.UseItemOnItem helper:
GenericCmd UseItemOnItem with local User, Source slot and Target slot. This is
the next source-supported interaction candidate, not a live-proven merge.
Existing observed quantity/inventory verification remains; no success or counts
are inferred from sending. Split packet unchanged. Static review and diff check
only; owner builds/tests.

## 2026-09-12 — CRU stacking is an inventory move (session 99)

Owner corrected manual action after GenericCmd also timed out: left-click pickup
and place onto the other stack, with no use/right-click/modifier. Both prior
combine/use merge requests are superseded. Merge now calls the existing native
clientless Item.MoveToInventory with the occupied target Slot.Instance, matching
the uploaded AOSharp move helper. Distinct normal inventory slots and compatible
templates are required. Observed quantity/inventory completion remains unchanged;
no counts are inferred from the move request. Static source/diff review only;
owner tests physical stacking and response handling.

## 2026-09-12 — ICE supplies explicit CRU stack action (session 100)

Owner restart restored five separate physical slots after move attempts had made
clientless report duplicate occupied slots. No successful merge was established.
Owner uploaded ICE plugin contains an explicit stacking branch for this exact
Upgraded Controller Recompiler Unit: CharacterAction 53 (0x35), source slot in
Target, destination type/instance in Parameter1/Parameter2. This action is absent
from the uploaded enum; it differs from UseItemOnItem 0x51 and inventory move.
Merge now sends that action and logs incoming action53 responses. All three prior
request shapes are superseded. Server-observed completion remains required;
response cache handling still needs live evidence. ICE source stays private and
is not committed. Display quantities deferred at owner's request; bank-to-inventory
auto-stack idea remains an unused fallback. Static source/diff review only.

## 2026-09-13 — Pause background CRU merging (session 101)

Owner reports repeated player refusals during CRU preparation, followed by
worker offer-ack timeouts. Source confirms pending stack operations reject both
incoming player trades and general worker IPC; the worker failures' exact causal
chain is not established because active dispatch also normally prevents new
stack starts. Background merging now defaults off; optional Bankers setting
EnableAutomaticCruStacking=true explicitly restores diagnostic attempts after
restart. Donations, existing-single pickup and explicitly requested splits remain
enabled. This removes unsolicited merge retry windows rather than deleting the
operation guard around actual item mutation. No settings edit is required.
CRU automatic stacking is unfinished pending packet/response evidence; display
quantities remain deferred. Worker timeout diagnosis remains unconfirmed. Static
source/diff review only; owner builds/tests.

## 2026-09-13 — Captured stack header correction (session 102)

Owner verified a successful manual merge emits and receives CharacterAction53
with Unknown=0. Uploaded N3Message constructor defaults Unknown=1; clientless
NetworkSession sets local character Identity but does not alter Unknown. Merge
now explicitly matches the captured zero header and logs all action/header fields
after send and on receipt. Clientless OnCharacterAction lacks action53 cache
handling. No speculative echo-based quantity or removal mutation is added.
EnableAutomaticCruStacking remains default false; true now permits only ONE
merge attempt per process restart, avoiding repeated trade-blocking diagnostics.
Split unchanged. Owner needs to check physical result and send STACK packet lines;
merge response cache/stack limits remain unfinished. Static review only.

## 2026-09-13 — Runtime bank terminal management (session 103)

#bankid shows the saved decimal terminal Instance; #bankid NUMBER saves it
atomically to data/citybankers-bank-terminal.json with a fresh revision. Initial
seed is owner-supplied 1477725977, persisted on first Manager startup. The prior
Bankers.CityOfficeBankTerminalInstance configuration override is superseded.
Admins may write directly; officer authorization follows the existing raid-assist
flow, including highest cached officer authority in reliable alt groups, targeted
alt refresh when needed, and existing rank lookup fallback. Commander/General/
President qualify. Existing command source and ban gates still apply.
Every banker polls the shared record once per second outside startup readiness
gates; changed revision retries a closed bank even for the same posted number.
An open bank stays open. Losing an open bank triggers a fresh attempt. Failed
diagnostics set BankNeedsId in heartbeat; #status prioritizes need new bankid
over queued-work failure text. Existing office location guards remain. New help
topic explains operation; no future rebuild is needed to update the Instance.
Static routing, authority, persistence and lifecycle review only; owner builds
and runs live validation. CRU diagnostic state remains unchanged.

## Session 126 — inventory quantities, attribute lookup deferred

- Inventory heartbeats carry nullable observed Quantity and IsStackable; Manager appends x<count> for positive server counts, including x1. A known stack without quantity renders x?. No fabricated one-unit fallback.
- Generalized server count capture across item types (login, container, add-template, trade-template and MultipleCount). Existing CRU service admission, merging and split policies remain unchanged.
- Owner explicitly deferred Stackable/CantSplit classification to the upcoming #items plugin. StackableAttribute intentionally returns unknown; no item catalogue is generated or embedded. Until connected, positive counts are displayed without claiming they establish stackability; other count-bearing items may also show a count.
- Static source/API and diff review only. Owner builds/tests. Display work resolved on publication; attribute data remains intentionally unfinished.

## Session 127 — shared item catalogue and search

- Implemented Manager #items / #i name search with word exclusions, exact recorded-QL filter, bounded pages and clickable template links; #itemid/exact numeric lookup exposes raw masks and nullable NoDrop/Unique/Stackable/CantSplit/Splittable. Existing membership/ban routing and blob limits remain.
- Owner may copy the unmodified extracted dump to runtime data/items.json. Streaming reader retains only ID/name/QL/Flags/Can; background loading, compact source-metadata-keyed disk cache and per-process cache mutex avoid full JSON materialization and concurrent raw parsing. Independent plugin AppDomains hold compact immutable snapshots. Missing/invalid source stays unknown with delayed retry; loaded snapshots refresh on restart.
- Supplied dump inspected:445001349 bytes,120842 distinct AOIDs, no duplicates; one missing Can and three missing QL stats. Signed flag masks preserved. No owner dump published. docs/ITEMS.md records API, setup and limits.
- Explicit source limitation: no low/high family relationships or in-game availability field. Search returns exact templates/QLs, not guessed intermediate-QL families; equal names/adjacent IDs are insufficient. Arbitrary interpolated QL links remain unsupported pending authoritative pairing data.
- Inventory Stackable seam now consults both endpoint attributes, requiring agreement; unknown remains unknown. Existing CRU movement/admission/split policies unchanged. Buddy warning work remains deferred.
- Static source/API, reader/cache/concurrency/routing review, source-data inspection, project XML and git diff --check only. No compilation, test suite or live AO run; owner builds/tests.

## Session 128 — observed item families shared with Phatz

- Owner explains QL endpoint AOIDs represent the same item and asks items knowledge to teach Phatz duplicates. Attached snapshot:45 policy entries,53 exact stock variants,129 physical Phatz copies; observed low/high edges resolve both views to39 families. Six duplicated rule groups:Strong/Supple/Arithmetic Lead Viralbots, Overheated Compiler, Viral Data Storage, Disk of Cloudy Crystal. All supplied limits unlimited. No raw attachments or donor data published/edited.
- Shared ItemFamilyIndex uses only observed low/high pairs, never equal names/adjacent IDs. Policy links, existing ledger and live donation offers feed sanitized data/items-pairs.json; atomic replacement and path mutex merge concurrent domain/process evidence. KnownPairs retained on explicit policy edits too. Relations survive last-copy withdrawal/restart. Immutable snapshots refresh on policy/ledger/evidence metadata changes; ledger read allows atomic replacement without taking ledger mutex under policy lock.
- #items range search now uses observed pair IDs plus endpoint QLs from dump; #itemid shows family IDs and pairs. Unseen families stay exact; no full global interpolation/availability claim. Original exact Search API retained, family-aware SearchFamilies added.
- Phatz list and stock group known variants; each QL/template keeps its original links and physical records. Add/update applies to family and consolidates duplicate dynamic rules, remove disables known aliases. No automatic policy-file rewrite merely to display grouping. Existing AOID GET semantics remain; no promised exact-QL retrieval.
- Future donation retention counts aggregate Phatz variants, including projected same-trade counts. Conflicting old caps keep all until explicit admin update; equal caps apply once per family. Existing stock is never retroactively trimmed. Other-role/explicit rejection precedence preserved for inherited aliases. Route enumeration includes aliases; capacity/type summaries deduplicate family retention rules. CRU policy unchanged.
- Static source/API/routing/counting/mutex/cache/publication review, attachment data analysis, XML and diff checks only. No assistant compilation, test suites or live AO test. Owner builds/tests. See docs/ITEMS.md.

## Session 129 — stock summary build correction

- Owner build log reports CityManager CS0122 in linked StockCommandEngine.cs620: banker SettingsPaths.GetSettingsDirectory was private. Five projects succeeded/up-to-date; Manager failed.
- Changed that existing accessor to internal so linked code in the same assembly can call it. Path resolution and runtime behavior unchanged. Static accessibility/call-site and diff review only; owner rebuilds.

## Session 130 — owner-authored changelog

- Added #changelog (also bare changelog in tells) under existing member/guest and ban checks. Source array in CityManager.Changelog.cs retains complete chronological history in Git; displays last25, oldest-to-newest within that window, with existing byte-aware blob pagination. No runtime JSON/data-file reads or writes.
- Exactly two owner-supplied entries: "created changelog." and "now we have items." No inferred features, dates or release prose. Future entries only when owner supplies wording; durable rule added to AGENTS.md. Help and project compile include added.
- Static routing, accessibility, boundary calculation, XML and diff review only. Owner builds/tests; no assistant compilation or test suite.

## Session 131 — online presence display

- Added #online and bare online in tells, with existing member/guest/ban routing, help, alphabetized names, separate counts and byte-aware blob pagination. Org list reads the existing _onlineCharacters set maintained by configured Bobsan startup snapshots and login/logout announcements; it does not poll, infer online alts or create a second org tracker. Missing complete snapshot is shown as incomplete, including an empty list.
- Guest side previously had no retained roster or join/leave event feed. New in-memory observations capture speakers only in Manager own guest channel; known Bobsan logoffs and successful leave/kick sends remove names. Invites do not imply presence. View explicitly labels observed guests and warns silent joins/departures may be missing. These observations reset at initialization and are not persisted as current presence. An individual can appear in both independent sections.
- Static routing, callback scope, locking, source/API, project XML and diff review only; no assistant builds/test suites/live AO tests. Changelog entries unchanged: owner supplied no new entry.

## Session132 — city buddies use game-only login

- Owner explicitly limits chat removal to the city buddies (104 configured characters); KWorker already owns their chat sessions. Manager, bankers, buffers and the existing working tell queue retain their normal connections. No proxy control interface or tell routing change in this step.
- BuddiesHost now invokes the pinned AOSharp.Clientless1.0.16 non-generic internal ClientDomain.CreateDomain factory with useChat=false. Public Client.CreateInstance does not expose this option. A narrowly scoped reflection adapter validates the full signature and refuses startup if unavailable, never falling back to chat-enabled creation. The switch is passed before child-domain initialization; no chat client or reconnect loop is created. Existing game lifecycle/readiness/reconnect behavior retained.
- Static API/source, call-site, plugin chat-dependency and diff review only. No builds/test suite/live logins. Owner rebuilds/restarts City Dwellers to apply; running old processes are unaffected until restart. Future optional KWorker tell/presence integration remains separate. No owner-authored changelog entry added.

## Session 146 — shared-state contention is retried, not escalated

- The census restart at 14:03:31 came from file sharing, not from the bags. `GetPhatzFamilies` read `data/ledger.json` with `FileShare.Read | FileShare.Delete` and without the per-file mutex, so a concurrent `File.Replace` by another banker threw `IOException` into `BankingServiceAgent.Tick`, whose catch-all requested a roster-wide recovery. Seven bankers that had been ready for seventy seconds restarted their audits.
- `RuntimeStateStore.ReadTextShared` added: shares write and delete, retries twelve times at 25ms across the replacement window, and takes no mutex so it can be called under `PolicySync`. Used by `SymbiantCatalog.GetPhatzFamilies` and `WithdrawalState.RequestStillStored`, the only two unguarded `ledger.json` readers. All other readers already went through the mutex-guarded `ReadJson`/`ReadJsonStrict`.
- `StateContentionException` is raised only by that reader and only after it gave up. The banking tick catches it separately, records `CONTENTION tick n/8` and returns, retrying on the next tick. Any other exception still blocks the census exactly as before. Eight faults within a minute of each other escalate; a quiet minute resets the streak.
- Static review only: no .NET toolchain in this container, so no compilation and no live run. Owner rebuilds and runs.

## Session 147 — the duplicate bag record no longer stops the census

- Evidence, not inference: `storage-state.json` shows Kbarty's `(Container:BB49D3F)` at Inventory 66 and 69 sharing `LastHandle` 316 and 21 identical items, and Kbsupp's `(Container:BB49C56)` at BankByRef 92 and 1 sharing 9 identical items. Baselines and censuses through 09-13 list each once; 09-18T07:51 is the first to list them twice. The audit's own bag moves create the duplicate when the client misses the removal at the source slot.
- `TryValidatePhysicalLayout` now fails only on two different items in one slot or a bag with no container identity. `StorageBagPolicy.DistinctBags()` returns one record per identity, keeping the last in the client's own order because the client appends the record a move produced.
- `BagAuditAgent` audits each identity once and verifies moves by the change in occurrence counts captured just before the move, instead of requiring exactly one here and none there — a condition a lingering record can never satisfy. `ArrivedItem` prefers a record at a slot the bag did not occupy before the move.
- Duplicates are logged as `DUPLICATE BAG RECORD` with the slots and the record kept, and `AmbiguousBagReport` still writes the evidence file, now accepted by the data-directory scanner.
- `[OPEN]` The ledger carries 21 Kbarty and 9 Kbsupp entries created from the duplicate reads. A clean census plus the existing reconciliation should retire them; that is custody code and is left to run rather than pre-empted.
- Static review only: no .NET toolchain in this container, so no compilation and no live run. Owner rebuilds and runs.

## Session 148 — staging acts on a bag that exists

- Moves and uses are slot-addressed: `Item.Use` puts `this.Slot` in the packet. Acting on the stale record of a duplicated bag is a no-op, which is exactly what Kbarty's `inInventory=True; inBank=False` means.
- `PrepareAuditStagingSlot` now takes its staging bag from `DistinctBags()` excluding duplicated identities, and verifies the move against counts captured before it. The old `bankCopies == 1 && inventoryCopies == 0` test could never pass for a bag listed twice.
- The dedupe choice is deterministic again: unresponsive last, bank before inventory, lowest slot. The previous last-in-list-order rule flip-flopped between `bank/92` and `bank/1` on Kbsupp across three audits of the same layout.
- A record that swallows a slot-addressed move without changing either listing is remembered as unresponsive for the session, and the other record of that bag is preferred everywhere after.
- `citymanager-org-size.json` registered as a known data file.
- `[OPEN]` Second audit on one connection fails all inventory bags (`failed=18` on every banker, bank bags unaffected; a freshly relogged Kbinfa passed). Suspected: `ProcessOpen` requires a new Container object, and a container already open from the first audit keeps `IsOpen` true with the same object. Needs the audit result file's per-bag error text before changing custody code.
- Static review only: no .NET toolchain in this container, so no compilation and no live run.

## Session 149 — the hold retries, and headroom is counted in slots

- A staging hold was permanent: it withdraws presence so the running cycle can drop it, but presence is only republished *after* the hold check clears, and later rosters are built from presence files alone. The hold now has an escalating 60s–960s cool-off that clears it, republishes presence and rejoins.
- `Inventory.NumFreeSlots` is `30 - record count` (SDK IL), so a bag listed twice subtracts two slots for one. Kbarty saw 10 against a requirement of 11 while physically holding 11 — the phantom was the only reason the staging step ran. `StorageBagPolicy.FreeInventorySlots()` adds the duplicates back and is used by every free-slot test in the staging path.
- Both findings were confirmed independently by multiple verifiers in a 112-agent sweep; the `NumFreeSlots` root cause was reached by four separate lenses.
- Static review only: no .NET toolchain in this container, so no compilation and no live run.

## Session 150 — one bag, one lookup

- `[VERIFIED-LIVE]` Nine of nine bankers audited and ready. Kbarty needed no staging move and stored a donation end to end. The alien-file notice is gone and the dedupe choice is stable across reorderings.
- Kbsupp's `unexpected outer slot 92 instead of 1` was a lookup fault: `FindBankBagByIdentity` was a `FirstOrDefault` over the bank listing and answered with the bag's other record. A record was at the expected slot, so the bag had returned.
- `StorageBagPolicy.PreferredRecord` / `AllRecordsFor` added. Live lookups resolve to the deduplicated record; the storage return check asks every record and accepts a match at the expected slot, naming all of them when none matches.
- `Extraction`'s `SingleOrDefault` over the live bag list threw on a duplicated identity and would have restarted the roster the moment such a banker went operational. Replaced.
- Static review only: no .NET toolchain in this container, so no compilation and no live run.
