## Session 176 — CRU resolved; temporary diagnostics retired

- [RESOLVED / VERIFIED LIVE] Owner tested one-unit split and pickup:61 ->60+1, then AO Finished plus exact inventory delta confirmed delivery, leaving60. A second request split60 ->59+1, remained reserved until the three-minute expiry, then a matched action53 merged back to60. Donation of the collected unit completed at the server and matched action53 restored61. No loss, duplication, stuck reservation or failed operation appears in the supplied capture. Owner approved resolution for now.
- [KNOWN LIMIT] Split preparation reproduces the observed native client's local update; it is not a server acknowledgement. Later trade completion or acknowledged remerge supports the result. A silent rejection could temporarily desynchronize local state; do not claim universal failure coverage. Cancellation/retry and restart during pending pickup remain optional future checks, not closure blockers.
- [CLEANUP] Removed temporary split trace lifecycle, raw packet hex, before/after inventory snapshots, message-type summaries, full SENT/RECV CharacterAction field dumps and raw template field dumps. Removed standalone tools/CruSplitProbe (source, project, local build boundaries, README and ignore file); its history remains recoverable in Git.
- [PRESERVED] CRU login counts, quantity and donation bindings, split request/local-result/readiness logs, merge request/applied/verified logs, pickup/expiry/donation outcomes and every failure/pause warning. Packet construction, quantity mutations, matched merge observation, deferred binding flush, reservation and trade behavior unchanged. No in-game changelog entry added.
- Validation: owner log chronology and conservation; focused diagnostic-only diff/reference scan and whitespace review. No assistant build/test suite/live actions. Next: normal use; reopen on concrete failure. Owner can unload/remove their separately installed probe DLL; removing repository source does not unload a running game plugin.

## Session 175 — local CRU split bookkeeping from native-client evidence

- [VERIFIED LIVE EVIDENCE] Owner full-client probes show one-unit splits locally decrementing the source and allocating the lowest free internal inventory slot. Initial capture preserved49998+1+1 after fresh login. Follow-up capture records two split sends12s apart, source005B and free slots0058/005A; relog preserves49998 at005B and singles at0058/005A. Owner placed source visually fifth and singles second/fourth: visual grid coordinates differ from internal IDs. No split acknowledgement or separate placement message appears in decoded capture. This does not claim raw-network completeness or proof of every possible failure case.
- [DECISION / SUPERSEDES] Earlier blanket prohibition on request-side split bookkeeping is superseded by native before/after and server-restored evidence. Reproduce native local preparation instead of waiting for a response never observed. It is explicitly NOT a server acknowledgement. Merge handling remains matched-response based and delivery remains governed by existing trade confirmation.
- [IMPLEMENTED] Split accepts one unit from a known normal-inventory CRU stack with no unique identity, in-play/no-trade/no-pending-merge, mutable inventory and unique slots. Select lowest unused internal slot0040..005D, construct exact matching Item before send, preserve proven action52 bytes. After send returns, require same connection character, objects/slots/counts before adding new unit and decrementing source. Exceptions preserve actor uncertainty latch and prevent resends. No inferred cache update on changed state/send exception.
- [IMPLEMENTED] Operation binds the exact returned Item and source/result slots; local completion requires conserved total, source decrement, matching template/QL, exact new unit and retained prior CRU objects. Readiness uses existing reservation/pickup flow after500ms, not the old10s acknowledgement wait. Logs say split applied/prepared locally and do not claim server verification. Existing quantity-one offer checks and receipt/delivery handling remain. Current bounded diagnostic trace retained for initial banker test.
- [OWNER NEXT] Pull/rebuild/restart City Dwellers for fresh inventory. If automatic merging enabled, let it consolidate. One #cru should produce one reserved single and remainder, with ready tell; trade and verify exactly one CRU, complete pickup, send split/preparation/trade log and inventory totals. No more full-client probes required. Actual banker pickup live outcome remains unverified.
- Validation: source/API, object binding, send-failure and inventory guards, reservation/trade offer checks, Git whitespace review. No assistant build/test suite/live AO operations. Private captures/SDK remain outside Git; no in-game changelog added.

## Session 174 — isolate standalone probe build

- [OWNER BUILD FAILURE] CruSplitProbe inherited root Directory.Build.props and targets, pulling in runtime sources/packages and the City Dwellers assembly informational-version generator alongside SDK assembly metadata (CS0579). Initial standalone-project isolation was incomplete.
- [RESOLVED] Added local Directory.Build.props and Directory.Build.targets discovery boundaries inside tools/CruSplitProbe. The probe now uses its own SDK defaults and explicit matching AOSharp DLL references; no global build file changes. README requires keeping both files with the project and Clean/Rebuild after updating.
- Validation: import-scope/source and whitespace review only, no compilation/tests. Owner rebuilds probe, then follows the same two manual splits and fresh-login snapshot procedure. Banker split recognition remains open; runtime unchanged.

## Session 173 — observe native split slot allocation

- [VERIFIED EVIDENCE] Owner full-client capture contains two outgoing splits (51 and64) from the same inventory slot without a logged split response, followed by two acknowledged action53 merges from other slots, then acknowledged container moves. Prior Clientless trace reported zero decoded messages during its12s window. This supports silent split behavior but decoded logging cannot rule out an unknown raw packet. Neither absence nor a sent request is server confirmation.
- [SOURCE] Managed full-client Item.Split delegates to Gamecode.dll; uploaded source does not contain its native allocation implementation. Clientless ordinary insertion chooses the first available inventory slot, but that is not sufficient proof for splits. Do not use merge success as evidence for a guessed split allocation rule.
- [IMPLEMENTED] Standalone read-only full-client AOSharp probe under tools/CruSplitProbe, outside the application solution. Owner arms one manual split, records initial and polled normal-inventory slots/template/QL/counts plus free slots, outgoing request and3s result snapshots. Incoming decoded message type totals only. No packet sending, item mutations, banker runtime change, or private source/binaries. Long log output is chunked. Events detached on teardown.
- [NEXT] Owner builds standalone probe against matching installed AOSharp DLLs; manually split one unit twice with a fresh arm/3s wait each time, leaving new stacks in place, then relog and capture /splitprobe snap. Compare initial free slots, resulting new slots/counts and server-restored state before implementing banker cache updates. CRU pickup remains unresolved; banker uncertainty latch remains unchanged. No additional City Dwellers restart/test is required for this probe.
- Validation: source/API review against supplied SDK, passive-event/teardown and diagnostic boundaries, Git whitespace review. No build/test suite/live AO execution. Probe ready for owner build and experiment; no claim of fixed splitting.

## Session 172 — both split headers work; capture the unobserved response

- [VERIFIED-LIVE / CORRECTION] Owner's three-start log establishes both split variants worked. Earlier two requests produced inventory1+1+59 at19:07:40. That run remerged to61, sent action52/Unknown0 at19:10:31, timed out locally, then fresh login at19:11:01 reported1+60. Total remains61. Session171's header hypothesis did not explain the defect; do not alternate packet variants again. Preserve the current physically successful sender.
- [EVIDENCE LIMIT] Between action52 SENT and timeout the uploaded runtime log has no split RECV, add-template or quantity observation. Existing STACK logging only names selected action values and quantity routes, so this does not establish which incoming response is missing from the handler or whether a response is sent. No successful local split recognition or pickup is proven. Do not manufacture an item/source decrement from the outgoing request.
- [IMPLEMENTED DIAGNOSTIC] A split opens a12-second trace, limited to32 inventory-related incoming N3 messages and512 raw bytes per captured message. Captures before/after-native CRU slot/count/identity snapshots and raw inventory-packet prefixes, including all CharacterAction values rather than only the expected52/34/53. Other decoded message bodies contribute type counts only; no chat/auth payloads. Final type-count/inventory summary, fresh login and disconnect end the trace. Packet/observation errors are isolated from normal handlers. No library/private input or captured output committed.
- [NEXT] Owner rebuild/restart, allow existing successful consolidation to finish, then one normal #cru and wait12seconds for SPLIT TRACE END. The trace, not another guessed packet, determines response decoding or the need for authoritative inventory refresh. Existing uncertainty pause prevents repeated requests on stale counts. Working merges and sender bytes are unchanged; splitting remains open. Earlier assertion that no further capture would be needed was too strong: the prior logger did not record the necessary response boundary.
- Validation: three-run chronological correlation, private SDK callback order (MessageReceived then PacketReceived then native dispatch), packet whitelist/bounds/lifecycle and diff review. No compilation, test suite or live AO operations; owner builds/tests. Diagnostic addition published, not a claimed split fix.

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

## Session 104 — full-inventory census staging (resolved on publication)

- [CAUSE] Six workers in the supplied log had 30 inventory bags and failed before opening their first bank bag. Their 60-second presence retries repeatedly started new coordinated censuses, including healthy workers.
- [FIX] After quiescing trades and settling inventory, startup census moves one inventory bag into available bank space. It waits for that identity in bank, absent from inventory, and a free inventory slot before allowing the collector to snapshot the new layout. The bag stays in bank; census reconciles its real location. No content or custody is inferred from the request.
- [FAILURE] No available staging space or an unverified move after 15 seconds parks the worker outside the roster until physical layout changes, a local recovery request arrives, or it reconnects. No timed rejoin for this staging failure. Other audit-error retry policy is unchanged. This preflight applies to coordinated census, not standalone manual audit mode.
- [VALIDATION] Static lifecycle, existing MoveToBank API, membership, settling and diff review only. Owner builds and live-tests. CRU and runtime bankid unchanged.

## Session 105 — worker receiving reserve (resolved on publication)

- [OWNER VERIFIED] Session104 audit repair held: supplied log verifies staging moves and a released nine-banker census. Later two phatz donations remain queued with no trade opening.
- [CAUSE] One staging slot allowed audit completion but failed dispatch preparation's items.Count+1 requirement. Phatz retained 29 inventory bags; two donations needed three slots. Generic busy IPC hid this definite prerequisite failure.
- [FIX] Coordinated census preflight now moves inventory bags into bank one at a time, verifying each as before, until MaxTradeItems+1 (11) slots are free. Enumeration follows the settled final layout. If bank space/bags limit the reserve, audit may still proceed with its minimum staging slot and a capacity warning. All supplied workers have enough bank space for the reserve. No new idle mover or competing operation is added.
- [DIAGNOSTICS] Preparation preserves the exact ready token and passes explicit insufficient-inventory-space and IPC-unavailable replies to Central's existing throttled WAITING report. Other busy prerequisites remain unchanged. Physical capacity is never bypassed.
- [VALIDATION] Static constant/API, task consumer, manifest/readiness and move-settle review plus diff check; no compilation, suites or AO run. Owner rebuild/restart applies reserve and normal census recovery handles the retained donation. No live data edits. CRU unchanged.

## Session 106 — optional reserve timeout and audit diagnostics

- Saved failure evidence identifies artillery bank-to-inventory move timeout at3000ms, bag still in bank. Later121/121 audit succeeded and Arty became ready. No claim of missing items or permanent Arty failure.
- Phatz startup was blocked by failed extra receiving reserve despite9 free slots. After15s, if the selected bag remains exclusively in inventory and the entire observed layout equals the pre-send layout with a free slot, defer extra reserve attempts until reconnect and proceed to settled physical census. Missing/duplicate/moved identities and no staging space retain blocking behavior. Actual dispatch capacity checks remain enforced.
- Coordinated startup audit commands now allow15s per bag move; manual audit settings unchanged. Fatal audit reasons are printed directly, including saved bag-location details, rather than only generic incomplete-census messages.
- Manager packet-deserializer OutOfMemoryException is separate and remains unaddressed; supplied log shows Manager continuing in play. No claim of a whole-host crash or fix to SDK decoding.
- Static state/identity/settling/deadline and diff review only. Owner builds/tests. Private archive not committed. Resolved on publication.

## Session 107 — native city announcements only

- Owner logs prove GoA cloak relay prose polluted AP cloak history, state and recovery deadline. Both Manager and CityRaidCoordinator (in OrgRankAuthorizer.cs) matched text; Manager did so before organization-channel validation.
- Both listeners now require AO system sender0 and successfully decoded extended city category1001 on their organization channel. Plain chat, bot relays and other channels cannot change cloak state or trigger city raid events. Bobsan alt traffic and normal commands retain existing paths. Flipper observations unchanged.
- Native observations use OrgChat.NativeCityEvent. Legacy OrgChat.CloakAnnouncement persisted state is not trusted at boot; existing live assessment establishes state. Old historical rows remain as recorded; no live data or speculative historical deletion.
- Static handler/call-site/source/restore review and diff check only. Owner compiles and tests; no live observation of the new filter claimed. Resolved on publication. Linux compatibility remains discussion only.

## Session 108 — Mono console startup

- Owner Gentoo run of mono CityDwellers.exe printed mono-service instruction. Host routed every Environment.UserInteractive=false process into Windows ServiceBase, regardless of OS.
- Automatic service detection now applies only to Windows. Explicit console command selects terminal operation on either OS; Linux no-argument startup also reaches console. Unix rejects Windows service install/uninstall/service commands with a useful console invocation.
- Owner rebuilds on Windows, copies release to Gentoo and runs mono CityDwellers.exe console from its directory. This resolves entry routing only; full Linux/amd64/arm64 compatibility remains unverified. No assistant build or live account login. Static branches and diff reviewed.

## Session 109 — reproducible portable dependency output

- Gentoo progresses into AO after filename-case and literal-backslash symlink workarounds. New log shows missing item index from GameData backslashes, all bank Use attempts timing out for saved1478799474, and the same Manager packet decode allocation error previously seen on Windows. No bank ID inferred; owner can run existing full-client /bankid near Central.
- Host build now runs Prepare-PortableClientless.ps1 after GameData restore. Build-only pinned Mono.Cecil0.11.6 rewrites six exact clientless string literals in the emitted unsigned DLL: core assembly path and five GameData paths. Portable slashes work on Windows and Unix. NuGet cache/private uploaded sources are untouched. Missing expected literals or a signed dependency fail clearly; already-patched output is accepted.
- Managed output DLL filenames normalize to assembly identity plus lowercase .dll using a two-step Windows rename. Native binaries are skipped. Host project has build-only plugin references, ensuring shared-output plugin builds finish before final transformation. Cecil is excluded from runtime assets.
- Deploy the full newly built release with canonical GameData directory. No manual renames, backslash symlinks or MONO_IOMAP needed for these corrected paths. Existing workarounds in the old target are not automatically deleted; a fresh binary directory can retain the same settings/data. This is a repository build fix, not a claim of publishing a new upstream AOSharp NuGet version.
- Static source-literal, build ordering, XML and script review plus diff check only; no build or live tests. Owner Windows compile and Gentoo execution remain the verification method. Manager deserializer failure and overall Linux/native navigation compatibility are not claimed fixed.

## Session 110 — build-only Cecil lookup correction

- Owner build restored/compiled all projects but failed portable preparation because generated PkgMono_Cecil was empty. Resolve pinned Mono.Cecil0.11.6 using project.assets.json libraries and packageFolders, including custom NuGet caches. Fail with specific restore/content error if absent. No runtime dependency introduced.
- Replaced deprecated Vector3.LengthSquared with SqrMagnitude as SDK diagnostic directs. CS0649 reports JSON-populated DTO fields, not compiler errors; no blanket warning suppression or data-model rewrite. Git-root warning retains source fingerprint fallback; log alone does not establish whether checkout is nested or path comparison differs.
- Static PowerShell lookup/target argument, XML and diff review only. Owner rebuilds; no assistant compilation or live AO. Existing portability transformer behavior unchanged.

## Session 111 — hyphenated character names

- Owner member add Sonstern-1 was rejected by letters/digits-only validation. Member, alt, administrator and ban name validators now permit literal hyphens, preserving the complete character name. Existing trim, length, case handling, authorization and list persistence remain unchanged; add/remove/load share these validators.
- Static four-validator diff and whitespace review only. No live member added by assistant and no compilation/test run. Owner rebuilds then retries #member add Sonstern-1. Resolved on publication.

## Session 112 — late decline during completed-trade verification

- Owner log shows a player trade rejected for pending storage work, worker receipt of the dispatched item, then Central receiving Declined while its earlier Finished receipt was still awaiting inventory verification. The old handler immediately started a global census. Callback attribution is not proven by the log; the ordering is consistent with the unrelated rejection callback crossing the completion window.
- Shared status handling now retains a pending Finished receipt across Declined callbacks for donation, dispatch, withdrawal and return flows. Logs once per receipt; does not reset the verification timer, resend items, infer success, or convert completion into cancellation. Existing exact inventory delta, settling, accounting and mismatch recovery remain authoritative. Declines before Finished keep their existing cancellation handling.
- Static callback ordering, receipt lifecycle and whitespace review only; no compilation or live AO test under owner build policy. Resolved on publication.

## Session 113 — initial froob buffer integration

- Imported public Mali buff engine at eb78c7f460a66dba6b1c8f8cf6cf66f5b231cc03 as CityBuffers. Optional Buffers.Froobs entries load under the unified host using official Clientless AppDomains, existing multithreaded log pipeline and serialized static-data warmup. Configuration uses the existing citydwellers.json; no per-character nano maps. Full setup is in docs/BUFFERS.md; provenance in plugins/CityBuffers/UPSTREAM.md.
- Retained Mali nano discovery, casting/team rules and queues. Added City Dwellers status IPC and Manager buffers command. All tells use the existing shared scheduler; Mali replies stay pinned to their originating buffer. The scheduler selects the oldest deliverable tell so an offline pinned buffer cannot block unrelated output. Existing Manager authorization still applies.
- Portable packaged assets; per-character mutable bans/ranks; empty initial privilege lists; received bans persist locally; direct Log.txt writer replaced by host logging. Missing or disabled Buffers leaves existing services alone. Enabled buffers cannot reuse another configured service account.
- Owner approved SDK1.0.91 trial with official Clientless1.0.16. Previous SDK1.0.84 was a historical downgrade after a later SDK ChatHeader.Size failure, not proof that1.0.91 cannot work. No compatible-runtime claim without owner evidence.
- Static source, project/config and whitespace review only. Owner compiles and performs live testing. Paid account rotation/catalogue and duplicate/composite balancing deferred; paid must never serve froob requests. No accounts logged in by assistant.

## Session 114 — buffer build corrections

- Owner build log reports CS0122 from BufferSettings calling private SettingsPaths.GetRuntimeDirectory in five plugins, and CS0103 for BufferSettings in the host. Replaced the private call with existing public TryEnsureDirectory. Host now explicitly links the shared source in its csproj; the global include excludes that project to avoid duplicate compilation. The host's missing-source cause is not established by the log alone.
- Replaced imported Scriban5.7.0 with exact7.4.0 for the reported vulnerability warnings. Package references now cover both CityBuffers and the host, so host output/binding redirects include the plugin dependency closure. Raised shared direct pins to required floors: System.Buffers4.6.1, Memory4.6.3, Numerics.Vectors4.6.1, Unsafe6.1.2, Tasks.Extensions4.6.3. All projects share the runtime directory; inconsistent versions could overwrite each other. AOSharp versions unchanged.
- Package framework/dependency metadata and Template.Parse/Render API reviewed against official NuGet pages. Source/XML and diff review only, no compilation or live tests. Existing CS0649 JSON-field warnings and Git source-fingerprint fallback are not the reported compile blockers. Owner rebuilds the solution; do not claim an observed successful build.

## Session 115 — one-run Colonist repair and portable bank

- Owner reports session114 build succeeds and tested behavior works; not all features tested. Reports worn Colonist backpacks found in storage holding symbiants, and manual rearrangement on Support. Code evidence: StartupCensusGate staging chose any Inventory.Items container without a normal-inventory slot check; official clientless collection includes equipped items. This is a concrete unintended-unequip path, not proof that all historical audits had this cause.
- Permanent StorageBagPolicy rejects equipped and Colonist296977 containers in audit enumeration, staging, storage/withdrawal/extraction/recovery selectors and cached storage-container lookup. No bag-count/order assumptions are restored. Portable288762 and Colonist equipment are excluded from physical stock/routing reconciliation.
- Temporary ColonistBackpackRepair runs under the coordinated startup pause before census. Opens the source, stages Small Backpacks99228, transfers one item at a time with full source/destination multiset verification, returns staged targets, equips the empty Colonist on the named SDK back slot and verifies. Cannot replace other back gear or use equipped bags for staging. Unverified actions stop without resending or releasing partial results. Persistent per-character completion markers make it one-run; interrupted work uses fresh physical contents. Normal census then reconciles the owner's actual layout. Removal instructions: docs/COLONIST_REPAIR.md. Remove only the temporary helper/hooks after owner reports all completions; retain permanent protection.
- Portable bank opening precedes dynel scan/old office fallback, verifies bank-open within8s, falls back on absence/failure and retries failed portable-backed opening every30s. Existing real-terminal/bankid code retained. While portable is present, failed opening does not advertise BankNeedsId. No automatic item use outside banker runtime and no live action by assistant.
- Static source/API, lifecycle, movement-conservation, project/XML and diff review only. Owner compiles/runs and supplies logs; no assistant build/test suite. Supplied item IDs are owner-authoritative; portable item page also confirms Open Bank on use.

## Session 116 — Small Backpacks only

- Owner clarified that mentioning worn bags as an alternative never authorized their use. StorageBagPolicy now accepts only Small Backpack99228 in normal inventory or bank; equipped bags and all other types are excluded across existing selectors and capacity diagnostics.
- Startup logs SMALL BACKPACK SHORTAGE once per audit run when eligible bags cannot cover the configured finite copy limits. Existing capacity report remains available. Shortages never authorize equipment use. Colonist migration and portable-first opening retained.
- Default symbiant capacity: Artillery251 types/120 bags, Infantry229/110, Control231/110, Support231/110, Extermination235/112; ten copies per type,21 slots per bag. Counts exclude worn Colonist.
- Static review only; owner compiles and tests. No live success claimed.

## Session 117 — portable-item census loop and readable console

- Owner log contains4813 lines, including3417 SDK MoveToBank lines. Central completes26 audits rather than failing for lack of slots. CRU snapshot has nine singles and a52-unit stack; the detector already excludes CRU. Verified code defect: census excludes personal288762, but DetectLocalInventoryDifference included it, producing a persistent difference after every successful census on all portable-equipped bankers.
- Expected/live loose-inventory comparisons now both exclude personal service items. Live detector and census snapshot share normal-inventory bounds; worn items cannot manufacture a mismatch. Ordinary stock discrepancies still trigger recovery. Local census start now reports its reason. Legitimate local holds no longer emit the misleading startup-handoff warning; token ownership checks remain intact. Local bag-move timeout now matches startup15000ms rather than legacy3000ms; no claim that the log proves every partial audit's cause.
- Console retains warnings/errors, audit progress and completion but hides SDK MoveToBank chatter, debug/verbose lines and the repeated audit tutorial. Thread-local line assembly under the existing output lock, short timestamps, duplicate banker-prefix removal and severity colors improve console readability. Full original diagnostics remain in data/citydwellers.log. CITYDWELLERS_VERBOSE_CONSOLE=1 restores uncondensed console detail. This is a focused first logging cleanup, not removal of all old logging paths.
- Owner log verifies portable bank results for all nine; Colonist completion on seven with back equip, Central/Dyna absent. Removed temporary ColonistBackpackRepair and hooks/project entry per prior one-run direction. Permanent Small Backpack99228-only storage policy, personal exclusions, shortage warning and portable fallback remain. Completion markers retained as history.
- Source/diff/project XML review only; owner compiles/live tests. Post-fix absence of repeated audits is not yet live-verified.

## Session 118 — full timestamps retained

- Owner requires full date, milliseconds and timezone in console as well as saved logs. Removed session117 console timestamp shortening; full file timestamps were never changed. Color/noise filtering and duplicate-prefix cleanup remain.
- LAN syslog is discussion only: optional plaintext UDP sender, full timestamps/severity/character labels, background bounded queue and retained local log proposed. Exact server address/port/transport not supplied; no sender implemented or network messages sent.
- Static diff review only; owner builds/tests.

## Session 119 — Manager-owned structured syslog events

- Owner corrected initial raw-console forwarding proposal: banker identifies itself, reports to Central, Central reports to Manager; only Manager has logging authority. Owner explicitly selected structured events rather than forwarding every diagnostic line. Full local diagnostic logs retained. Unpublished raw-tee sender plan abandoned; no tee changes published.
- Optional root Syslog {Enabled:false,Host:"",Port:514,Transport:"tcp"}; supports plaintext TCP (RFC6587 octet counting) and UDP. Only Manager creates sender. Source hostname automatic, APP-NAME citydwellers, host PID preserved, body contains original character/actual Client.LocalDynelId (unknown before available), source UTC time, severity, stable event ID and data. No identity guessed from text.
- New shared ServiceEvents reports asynchronously over process-scoped named pipes through Central. Configured role/source and relay validated; Manager deduplicates recent4096 IDs, writes data/citydwellers-events.jsonl, queues syslog. Initial events cover bank opening/readiness, census start/apply/recovery, existing transfer stages and Manager cloak observations/startup. This is not yet instrumentation of Buffers/Buddies/Flipper or every diagnostic event.
- IPC256/sender1024 queues bound resource use; transient failures retry away from game threads, overflow warns locally. IPC acknowledgment is in-memory receipt, not disk durability. Bounded shutdown/crashes can lose pending reports; TCP retries can duplicate after ambiguous sends. Stable ID permits deduplication. No automatic disk replay. Existing raw diagnostics retained; Manager JSONL retained for server loss. Large UDP messages over60000 bytes are skipped with local notice (persisted JSONL remains); TCP preferred.
- docs/SYSLOG.md includes config, rsyslog source-IP formatting/full event timestamps, retention note, ccze limitations, exact-time commands. tools/citylog.py filters timezone-aware original event intervals, bot/event and rotated gzip files; no dependency beyond Python3. Receiver was not accessed and no logs sent by assistant.
- Static source/API/framing/lifecycle, project XML, Python AST and diff review only; owner compiles/tests. No live/compile success claim.

## Session 120 — nullable ledger HighId compile fix

- Owner build log: CityBankers CS1503 at BankingServiceAgent.LocalCensus.cs344, nullable int HighId passed to int parameter. Five other projects succeeded/up-to-date; no live validation implied.
- Ledger personal-item predicate now uses HighId ?? AoId, matching existing ledger materialization fallback. Low ID remains checked; absent high ID does not invent another item identity. This corrects session117's compile oversight without changing live-item comparison or syslog behavior.
- Static type/call-site and diff review only; owner compiles/tests.

## Session 121 — portable success bypasses legacy diagnostics

- Owner confirms console manageable and log appears settled. New log shows all nine BANKER READY, no repeating local census loop. Syslog configuration accepted; sender reports SocketException, but old log omitted socket code so refused/unreachable cannot be distinguished. Separate AO packet-deserialization ArraySerializer OutOfMemoryException appears once and Manager continues; not evidence that syslog queue exhausted memory.
- Owner clarified: move old diagnostic sequence down fallback order, not merely hide output. Added separate CompleteBankOpen path for portable/already-open success; bypasses CompleteDiagnostic and position/dynel/player snapshots. Writes only bank-open/readiness/capacity data under existing result-file contract, one confirmation and structured event. BankOpenOnly flag prevents host diagnostic presentation on this path. Retained full discovery/diagnostic/reporting for portable absence/Use failure/timeout; no bank-ID code deleted. Disconnect resets mode; late-open publication follows active mode.
- Syslog failures now include transport, configured destination, SocketErrorCode/native code/message (or exception detail); no receiver configuration or connectivity conclusion invented. Owner may test UDP. No live network action performed.
- Static path/call-site/diff review only. Owner compiles and tests.

## Session 122 — excluded disconnects no longer restart the roster

- Owner log shows Artillery/Support disconnect callbacks recurring about331 seconds apart; Support follows Artillery by about14 seconds, superseding healthy audits. Seven remaining bankers repeatedly complete census. Underlying connection failure is not diagnosed from this console log.
- StartupCensusGate now retires local readiness, operational presence and audit ownership on every disconnect, but publishes a new global recovery only if the retired connection epoch belongs to the current cycle. Membership check and request publication share Coordinate's mutex, preventing an excluded reconnect failure from restarting an already reconciled roster. Existing independent recovery requests remain intact; membership/publication failures retain fail-closed global recovery.
- Active-participant disconnects still reconcile interrupted custody. Returning connections still join through the existing fresh census; no stale ready token or audit result can release them. This is a bounded fix for repeated excluded/offline callbacks, not replacement of all global recovery with per-banker recovery.
- Owner confirms UDP syslog reception. Spirit120/156 capacity is knowingly accepted; a second Spirit banker is deferred. HQ-specific packet deserialization warning is longstanding and non-blocking per owner; do not repeatedly present it as a new blocker or infer process memory exhaustion from it. Full timestamps retained.
- Static review of active/excluded disconnects, coordinator ordering, existing requests and reconnection admission; git diff --check. No assistant compilation, test suites or live AO run; owner owns those. Connection failure cause remains unconfirmed.

## Session 123 — idle disconnect availability and lightweight reconnect

- Reopened excessive audit policy: owner log shows Control disconnect alone causing eight other bankers to audit for about80 seconds. Session122 only suppressed callbacks from already-excluded epochs; it did not solve initial idle disconnects. Startup audits remain a separate existing policy.
- Released census no longer restarts solely because a member heartbeat/presence disappears. Existing readiness checks mark absent members unavailable; healthy members retain their cycle. Disconnect preserves a settled idle snapshot only when the banking actor has no retained transfer, receipt, storage, withdrawal, CRU, extraction or recovery work and no pending durable dispatch/census ownership. Actual interrupted work still takes the existing conservative recovery path; this change is not a rewrite of every custody-recovery trigger.
- Same-process idle reconnect waits for bank opening and five seconds of stable fresh inventory, checks retained actor/peer work, and rebinds only that member to a fresh connection epoch under the coordinator mutex. Top-level slot/bag identities, item templates, QL and known CRU counts must match for audit-free resume. Failed reconnect attempts retain the original proof; independent global recovery supersedes it. Readiness and operational heartbeat stay withheld until validation. Rebind publication is retryable after partial file publication.
- A changed idle reconnect snapshot acquires the existing single-banker local census, without forcing other bankers to recount. Unavailable local ownership stays held for retry; exceptions cannot release provisional readiness. Explicit/independent recovery and newly detected unresolved item work still fail closed.
- This trusts previously reconciled contents of unchanged bags across an idle network reconnect; it does not prove unopened bag contents against out-of-band manual edits. Fresh process startup still scans, and existing item-operation validation remains. No broad claim that all audits or all disconnect recovery are removed.
- Static SDK API, state/epoch, mutex/publication, local ownership and exception-path review plus git diff --check. Owner compiles and live tests; no assistant build/test suite/live run.

## Session 124 — banker reconnect identity and update-pump guard

- Owner logs establish repeated TCP success followed by roughly302 seconds in Authenticating, then disconnect/30-second retry. No return to InPlay is shown for affected bankers; Manager correctly reports them offline. Owner reports this did not formerly hang. Logs do not identify the initial cause or prove which source hazard caused these stalls.
- Official Clientless reference has two concrete hazards: Reconnect resets its cookie/message counter but leaves Client.LocalDynelId populated, while login message headers take that old ID; UpdateLoop.Run has no exception boundary and starts an unretained Task, so one escaping Client.Update/plugin/chat exception can permanently stop packet draining while socket reconnect callbacks continue. No claim that a particular exception was observed in owner logs.
- New banker-only ClientlessSessionGuard resets LocalDynelId through its verified internal setter on disconnect; native character selection restores it. It wraps the existing UpdateLoop Action<double> callback using verified private field names, first binding when the SDK exists (initial packet or update). Escaping exceptions are logged by type/stack with30-second throttling and the existing pump continues next tick. No new pump, watchdog reconnect, concurrent game loop, credential mutation or forced audit. Reflection shape changes report an explicit error; teardown restores only its own callback. No automatic resurrection of an already-dead runtime loop; deploy through owner rebuild/restart.
- Login progress exposes ServerSalt/CharacterList/LoginError stage names only. Console now retains SDK game-state transitions and connection/login failures previously hidden by the debug filter; full timestamps and raw file remain. No credential/salt/packet/account-list logging. These observations distinguish successful authentication/InPlay from mere socket connectivity on the next run.
- Idle disconnect/rejoin census policy is preserved. Protection catches escaping exceptions; it does not repair deterministic failing plugin logic or unblock a synchronously hung callback. First already-executing SDK tick cannot retroactively be wrapped. Post-login service recovery still requires owner live evidence; do not claim verified reconnect success.
- Static reference API, field/delegate shape, lifecycle, exception, XML and diff review. No assistant build/test suite/live login; owner compiles/tests.

## Session 125 — transaction incident evidence and Manager dumps

- Owner requests donations AND retrievals, all lost/found-producing events, transaction-only sequences through recovery, and small separate debugging artifacts. Equal lost/found counts are not proof of identical item occurrences. No raw runtime-log extraction or fabricated historical causes.
- Added shared IncidentJournal with per-trace JSONL local evidence, named cross-domain mutex, bounded100ms acquisition, persisted state deduplication, problem markers and best-effort evidence-gap warning. Explicit links join transaction/batch/retrieval/ledger/recovery IDs. Successful structured evidence remains to diagnose later anomalies; no deletion policy. Receipt/phase/queue/withdrawal/ledger/lost transitions and recovery outcomes are instrumented. Old terminal trade handles are cleared to avoid attaching later idle recovery to the previous trade.
- Manager alone renders incident files automatically (15s first,30s interval) regardless of syslog Enabled. Uses shared durable local evidence rather than optional syslog IPC queue; no external sends or raw-console relay. Preserves existing bare dump. Added administrator dump incidents and dump incident-ID, plus lost/found evidence links. Full UTC timestamps, explicit latest states and gap/truncation notices. Exports bounded64 linked traces/5000events with originals retained. Signature includes source length/mtime so concurrent new evidence cannot be hidden by a newer export timestamp.
- Lost before-removal/confirmation/exclusion transitions retain original claim and reason. Committed found/location changes retain before/after records; unmatched claims list same-template candidates without asserting identity. Recovery applied is distinct from proving the original cause. Multiple related problem IDs may refer to one multi-batch donation and follow each other's evidence links. Unresolved/incomplete traces remain available.
- docs/INCIDENTS.md documents commands, paths, coverage and limits. Source/API, XML, locking, path validation, durable-state integration and diff review only; no assistant compilation/test suite/live run. Owner compiles/tests. Existing already-recorded losses cannot acquire missing pre-deployment transaction history.

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

## Session 131 — online presence display

- Added #online and bare online in tells, with existing member/guest/ban routing, help, alphabetized names, separate counts and byte-aware blob pagination. Org list reads the existing _onlineCharacters set maintained by configured Bobsan startup snapshots and login/logout announcements; it does not poll, infer online alts or create a second org tracker. Missing complete snapshot is shown as incomplete, including an empty list.
- Guest side previously had no retained roster or join/leave event feed. New in-memory observations capture speakers only in Manager own guest channel; known Bobsan logoffs and successful leave/kick sends remove names. Invites do not imply presence. View explicitly labels observed guests and warns silent joins/departures may be missing. These observations reset at initialization and are not persisted as current presence. An individual can appear in both independent sections.
- Static routing, callback scope, locking, source/API, project XML and diff review only; no assistant builds/test suites/live AO tests. Changelog entries unchanged: owner supplied no new entry.

## Session132 — city buddies use game-only login

- Owner explicitly limits chat removal to the city buddies (104 configured characters); KWorker already owns their chat sessions. Manager, bankers, buffers and the existing working tell queue retain their normal connections. No proxy control interface or tell routing change in this step.
- BuddiesHost now invokes the pinned AOSharp.Clientless1.0.16 non-generic internal ClientDomain.CreateDomain factory with useChat=false. Public Client.CreateInstance does not expose this option. A narrowly scoped reflection adapter validates the full signature and refuses startup if unavailable, never falling back to chat-enabled creation. The switch is passed before child-domain initialization; no chat client or reconnect loop is created. Existing game lifecycle/readiness/reconnect behavior retained.
- Static API/source, call-site, plugin chat-dependency and diff review only. No builds/test suite/live logins. Owner rebuilds/restarts City Dwellers to apply; running old processes are unaffected until restart. Future optional KWorker tell/presence integration remains separate. No owner-authored changelog entry added.

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

## Session 134 — packet guard build compatibility

- Owner build generated all C# outputs, then Prepare-PortableClientless failed with InvalidCastException/MSB3073. The log did not retain the inner script line or stack, so the exact throwing instruction is not proven.
- Packet guard now performs object-typed Cecil operand mutation inside a small typed C# bridge loaded by the existing owner-side PowerShell build step. This prevents PowerShell object wrappers from being stored where Cecil expects MethodReference/Instruction operands. Branch redirection, short-branch widening, stack sizing, bound logic and repeat-build validation are preserved. No runtime/dependency guard removed.
- Failures now print guard stage, exact script location, script stack and underlying exception; partial output temp is cleaned without replacing the original dependency. No credentials or data changes. Source and diff review only; no assistant build/test suite. Owner rebuilds to establish execution success.

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

## Session 136 — startup census livelock diagnosed

- Owner supplied a full console run (2026-09-18T12:11:35+03:00, build `source-44377b3a2496`) showing seven bankers restarting the startup bag audit indefinitely at roughly 100-145 second intervals and never releasing a census. Diagnosis from log plus source review only; no assistant build, test suite or live AO run.
- `[VERIFIED]` Root trigger: at 12:12:09 `Kbsupp` and `Kbarty` blocked on ambiguous bag identities (`Container:BB49C56` at bank/1 and bank/92; `Container:BB49D3F` at inventory/66 and inventory/69). This is `StorageBagPolicy` refusing ambiguous evidence, which is correct. Neither worker appears again in the run; they never audit and never self-recover.
- `[VERIFIED]` The duplicates are new. The 2026-09-11 baseline `data/storage-baseline.json` (runId `20260911-013435-104b6746`) has zero duplicate bag identities across all eight workers. `[OPEN]` Alias origin remains unestablished, as in session 133; do not assert a cause without fresh evidence.
- `[VERIFIED]` Amplifier is an independent code defect. `PhysicalLedgerReconciliation.ReadCensus` rejects any census with `FailedCount != 0` or `OpenedCount != TotalBagCount`, so one failed bag open out of 110 invalidates a worker. `StartupCensusGate` then requeues the audit and, for non-central roles, sets a 60-second presence retry and deletes its own `.presence.json`. `Coordinate()` supersedes the entire collecting cycle the moment any participant loses presence, and every banker seeing a changed cycle id calls `BagAuditAgent.CancelForRecovery()` (`StartupCensusGate.cs:396-402`), destroying healthy peers' in-flight and completed audits. The stepped-aside worker rejoins 60 seconds later and repeats.
- `[INVARIANT VIOLATED]` A worker intending to withdraw from a cycle instead cancels that cycle for everyone; stepping aside and superseding are the same action. `Kbinfa` completed a clean audit at 12:13:46 (`opened=110 failed=0`) and the result was discarded four seconds later.
- `[VERIFIED]` Escalation signature: in the steady-state loop `opened` equals `bankBagCount` exactly for every worker (92/92, 94/94, 102/102) and `failed` approaches the inventory bag count of 18. All bank bags open; inventory-side bags do not. Round one does not show this, so it follows from repeated aborted rounds. `[OPEN]` Mechanism unproven; `BagAuditAgent.DefaultBagOpenTimeoutMs` remains 3000 ms while session 117 raised the local bag-move timeout to 15000 ms. Per-run files under `data/startup-census/` are needed and were absent from the supplied snapshot.
- Explicitly not the cause: the `ArraySerializer` `OutOfMemoryException` on Manager at 12:11:38 is the longstanding HQ packet variant recorded as non-blocking in session 122; the `data.zip` alien-file warning is an owner archive; the `SMALL BACKPACK SHORTAGE Kbspirit` warning is real capacity feedback but does not block the audit.
- `[OPEN]` No code change published in this transaction. Proposed direction recorded in `docs/CENSUS_LIVELOCK.md`: separate withdrawal from supersession, retain successfully written peer censuses across cycle replacement, and exclude a persistently unscannable worker rather than re-admitting it every 60 seconds. Any change must preserve fail-closed behavior; an incomplete census must still never be applied.

## Session 137 — census withdrawal replaces cycle supersession

- Implements the session 136 diagnosis. `Coordinate()` no longer replaces the collecting cycle when a participant loses presence; it removes that member from `cycle.Participants` and keeps the cycle id stable. A banker retires its own audit only when it observes a changed cycle id (`StartupCensusGate.cs:396-402`) and only acts on a cycle that `Includes()` it, so withdrawing one member no longer cancels healthy or already completed peer censuses.
- Guards retained: an explicit recovery request still supersedes, because that is a deliberate instruction rather than an absence; a member that already wrote a census for the cycle is not withdrawn and its physical evidence stands; Central is mandatory, so the cycle is still replaced if Central would be dropped or nothing would remain. `CensusApplication.Apply` requires Central and would otherwise throw.
- `[VERIFIED-CODE]` A smaller roster is safe. `CensusApplication.Apply` scopes reconciliation to the characters that produced a census; anchors outside that scope are skipped rather than reclassified, and `bundle.Storage.Workers` carries every unaudited worker's previous state forward unchanged. A cycle that loses a member is therefore the same condition as one created while that member was offline, which is already the normal path since cycles are built from `online` members only.
- Fail-closed behavior is unchanged. `ReadCensus` still rejects any census with `FailedCount != 0` or `OpenedCount != TotalBagCount`, and an incomplete census is still never applied. This change alters which members a cycle waits for, not what counts as acceptable physical evidence.
- Census rejection backoff now escalates 60/120/240/480/960 seconds instead of a fixed 60, and resets when the worker completes a census or reconnects. A persistently unscannable worker can no longer force a fresh cycle every minute and re-audit the whole roster. Each retry logs its consecutive rejection count.
- `[OPEN]` The two ambiguous bags still need owner action (`Kbarty` `Container:BB49D3F` at inventory/66 and 69; `Kbsupp` `Container:BB49C56` at bank/1 and bank/92). This fix stops them from taking the rest of the roster down; it does not resolve them. The inventory-side bag-open escalation recorded in session 136 also remains open pending per-run files under `data/startup-census/`.
- Validation: source and call-site review, participant/cycle-id lifecycle reasoning, `CensusApplication` scope analysis, brace and whitespace checks, `git diff --check`. No assistant compilation, test suite or live AO run; Claude Code containers have no .NET toolchain. Owner builds and live-tests. No owner changelog entry supplied or added.

## Session 138 — ambiguous bags are duplicate observations, not duplicate bags

- `[VERIFIED]` From the owner's `data/storage-state.json` (`UpdatedUtc 2026-09-18T08:38:05Z`) measured against the 2026-09-11 baseline: only the two blocked bankers carry an extra bag. Kbarty 121 vs 120 with `(Container:BB49D3F)` repeated at inventory/66 and inventory/69; Kbsupp 111 vs 110 with `(Container:BB49C56)` repeated at bank/92 and bank/1. Kbinfa, Kbcont, Kbexte, Kbphatz, Kbdyna and Kbspirit match their baseline exactly with no repeated identity.
- `[VERIFIED]` Both are repeated observations of one physical bag. Kbarty's two entries share the same handle 316 and hold an identical set of 21 items in identical inner slots. Kbsupp's two entries have different handles (114 and 296) but an identical set of 9 items, matching session 133's note that duplicate containers can present different outer addresses. No item multiset is doubled; the duplication is in the observation, not in physical stock.
- `[VERIFIED]` Not a transient single-session artifact. The persisted state observed at 08:15/08:38 carries the same identities at the same slots that the 12:11 run reported live, so a host restart does not clear it and the condition reappears from live observation each session.
- Owner action recorded in `docs/CENSUS_LIVELOCK.md`: log the affected character in with the ordinary AO client and physically move the bag so a fresh server-side observation is produced. `StartupCensusGate` re-evaluates a staging block when `InventoryLayout()` changes, so the banker retries by itself afterwards. Do not delete data files to clear the hold.
- `[OPEN]` The origin of the aliasing is still unestablished, as in session 133. This records what the evidence shows, not a proven cause.
- Analysis of owner-supplied snapshot only. No assistant compilation, test suite or live AO run. Snapshot analysed and never committed.

## Session 139 — packet guard enum combination corrected

- `[VERIFIED]` Owner build log retained the exact throwing line, which session 134's added diagnostics made possible: `build/Protect-ClientlessPacketArrays.ps1:173`, `$attributes = [Mono.Cecil.MethodAttributes]::Assembly -bor [Mono.Cecil.MethodAttributes]::Static -bor [Mono.Cecil.MethodAttributes]::HideBySig`, with `System.InvalidCastException` and a stack frame reading `CallSite.Target(Closure, CallSite, MethodAttributes, MethodAttributes)`.
- `[VERIFIED]` Cause: `Mono.Cecil` attribute enums are ushort-backed. PowerShell routes a bitwise operation on two such operands through a dynamic call site and throws `InvalidCastException: Specified cast is not valid`. Session 134 correctly moved Cecil *operand* mutations into a typed bridge but this flag combination remained a PowerShell expression, so the same class of failure persisted.
- Flag combination now happens in the existing typed C# bridge, which handles `|` on a ushort-backed flags enum natively. The bridge is bumped `CecilOperandsV1` to `CecilOperandsV2` so a behavior change carries a distinct type identity, and all four references were updated together. A stale `CecilOperandsV1::ReplaceAllocation` call at line 271 was found during that sweep and would otherwise have been the next build failure.
- Comments at both the bridge property and the call site record why the expression must not be inlined back into PowerShell.
- `[VERIFIED-CODE]` No other bitwise-on-enum hazard remains in the build scripts. The only other such operation is `Write-BuildIdentity.ps1:55` on `[IO.FileAttributes]`, which is Int32-backed and therefore safe. The three `[Mono.Cecil.ParameterAttributes]::None` uses pass a single value to a constructor and involve no dynamic bitwise operation.
- Guard scope, branch redirection, short-branch widening, stack sizing, bound logic and repeat-build validation are unchanged. The five C# projects already compiled in the owner's run; only the post-build hardening step failed.
- Validation: source review, reference sweep for the renamed bridge, and underlying-type analysis of every bitwise enum operation in `build/`. No assistant compilation, PowerShell execution or live run; Claude Code containers have neither .NET nor PowerShell. Owner rebuilds.

## Session 140 — org replies routed over the chat connection

- Owner reported `#help` accepted in org chat (`ORG COMMAND [Athen Paladins] Kavem: #help`), logged as `Org reply sent directly to observed channel Athen Paladins`, with nothing delivered in game. The same pattern is present in the earlier 12:25 run, so this predates session 137 and is not a regression from it.
- `[VERIFIED-CODE]` Cause: `TrySendDirectGroupMessage` publishes a `GroupMsgMessage` through `Client.Send`, which is the **game-server** connection — the same call used for `ToggleCloakMessage`, `CharacterActionMessage` and `SocialActionCmdMessage`. Organization chat is carried by the **chat-server** connection: it arrives on `Client.Chat.GroupMessageReceived` and is sent with `Client.SendOrgMessage`. Every working chat path in this repository goes through `Client.Chat`. The game server discards the org packet without error, so the raw write never threw, always returned success, logged a delivered reply, and shadowed the `Client.SendOrgMessage` fallback below it, which was therefore never reached.
- `[DECISION]` The chat route is attempted first whenever `Client.OrgId > 0`. The raw game-connection route is retained as a last resort but is no longer reported as a delivered reply: it sets outbound health to degraded and logs a warning naming it as unverifiable. The observed `Client.OrgId` value is now included in the detail so a future log shows whether the chat route was even eligible.
- `[INVARIANT]` A raw socket write is an attempt, not a delivery. An organization reply is proven delivered only when the chat server sends it back. `NoteOrgEchoPending` records the outbound text and route; `ObserveOrgEcho` is called from the inbound org handler and before each new send, logging `ORG DELIVERY CONFIRMED via <route>` or `ORG DELIVERY UNCONFIRMED: no echo observed within 15s via <route>`. Matching tolerates server-side decoration of blob replies by accepting a 32-character prefix. This reports evidence only; it never retries, blocks or duplicates a reply.
- `[OPEN]` Whether `Client.OrgId` resolves on this build is not yet known; session 51 recorded AOSharp failing to obtain the LocalPlayer organization stat, which is why the raw route was introduced. The next run's log now states the value and which route ran, which settles it either way.
- `[HISTORICAL]` The 2026-09-06 "direct organization packet route" was a workaround for that missing organization stat. It is not deleted, but it is demoted and truthfully labelled rather than presented as a successful send.
- Validation: connection-boundary analysis across every chat and game send in the repository, definite-assignment and lock-scope review, brace and parenthesis balance, `git diff --check`. No assistant compilation or live AO run; owner builds and tests. No owner changelog entry supplied or added.

## Session 141 — census withdrawal confirmed live; ambiguous-bag hold made actionable

- `[VERIFIED-LIVE 2026-09-18T13:09]` The session 137 census withdrawal fix works. Owner run logged `Census 9501064256b24bf489c6d8fedf7145ce withdrew Kbarty, Kbsupp; 7 participants continue without restarting their audits`, followed by seven uninterrupted audits progressing 25/110 and 50/110 with no `Census superseded` and no restart. The livelock diagnosed in session 136 is resolved.
- `[VERIFIED]` The two held bankers each carry exactly one extra bag entry. Kbarty has 19 normal-inventory bags where every other banker reports 18; Kbsupp has 93 bank bags against a baseline of 92. No slot is doubly occupied: two distinct slots each report the same container identity, with identical contents, and it reproduces on every fresh login.
- `[INVARIANT]` A container identity belongs to exactly one physical bag. Two entries sharing one identity therefore cannot be two bags; one is a stale client record. `TryValidatePhysicalLayout` now says so and reports the bag name, QL, both locations, and the storage bag entry count against the distinct identity count, so the discrepancy is visible rather than implied.
- The staging hold previously advised the operator to "request recovery", which names an internal `.recovery.json` file mechanism with no operator-facing command. It now states the action that actually clears the hold: move the affected bag with the game client so the server reports it once, after which the existing observed-layout re-evaluation releases the banker automatically.
- `[OPEN]` No auto-resolution implemented. Deciding which of the two entries is stale would select a slot for later physical bag movement, and an incorrect choice issues a move against a slot that does not hold the bag. That is custody-moving code which cannot be tested here, so it is not written on inference. The owner observation needed first is what the ordinary game client shows at Kbarty inventory 66 and 69 and at Kbsupp bank 1 and 92.
- Diagnostics only; no custody or census logic changed. `Item.Name`, `.Ql`, `.Slot`, `.UniqueIdentity` were verified against existing call sites before use. Validation: source review, brace and parenthesis balance, `git diff --check`. No assistant compilation or live run; owner builds and tests.

## Session 142 — a held banker records its own evidence

- `[OWNER-DIRECTION]` A clientless banker cannot be inspected with the ordinary AO client: this process already holds that character logged in. Any diagnostic plan that depends on an operator looking in game is invalid for these characters. Instrument the bot instead. Kavey's framing: the bot is the other half of the work, so it gathers the evidence and the assistant analyses it.
- Added `AmbiguousBagReport`, written from `WaitForStagingChange` into `data/diagnostic-dumps/ambiguous-bags-<character>-<utc>.json`. It records the banker's own live view: every normal-inventory and bank outer item with slot type, slot instance, masked slot, container identity and type, AOID, high ID, QL, name and the storage-bag and normal-inventory predicates; every entry in `Inventory.Containers` with identity, handle, open state and item count; each duplicated storage identity with all of its occurrences; and summary counts including storage bag entries against distinct identities.
- `[INVARIANT]` The report is read-only. It moves nothing, opens no container, sends no packet, and swallows its own exceptions so a failed report can never turn a diagnosable hold into a crash. It is written once per observed layout, because `WaitForStagingChange` is only reached when the layout has changed.
- `[DECISION]` The container listing is the discriminator being sought. A stale outer-item record should have no live container of its own, so comparing the outer listing against `Inventory.Containers` should show which of the two occurrences the client can still reach. That is a hypothesis this report is designed to test, not an established fact.
- `AmbiguousBagReport.cs` was added to `CityBankers.csproj`; that project lists its sources explicitly, so an unregistered file would have compiled to a missing-type error. `Item` and `Container` member usage and the `AOSharp.Common.GameData` import were verified against existing call sites before use.
- Validation: source review, field-type and signature matching for `_settings`, `_character`, `_role` and `GetDataDirectory`, brace and parenthesis balance, project XML parse, `git diff --check`. No assistant compilation or live run; owner builds and runs.

## Session 143 — org send corrected from SDK disassembly; session 140 conclusion retracted

- `[SUPERSEDED]` Session 140 concluded that organization chat is sent on the chat-server connection and that the raw `GroupMsgMessage` route used the wrong connection. That is wrong and is retracted. Disassembly of the pinned AOSharp.Clientless 1.0.16 `Client.SendOrgMessage(string, bool)` shows it reads `Stat.Clan` (5) from `LocalPlayer`, logs an error and returns if absent, otherwise builds a `GroupMsgMessage` with `MessageType = 3`, `ChannelId` set to that stat and the text, then calls `Client.Send` on the game connection. `GroupMessageType.Org` is 3, confirmed from the `AOSharp.Common` metadata. The repository's raw route therefore emits a byte-identical packet. Organization chat is sent on the game connection and only received on the chat connection.
- `[VERIFIED]` Real defect found: `TrySendOrgMessage` gated the SDK call on `Client.OrgId`, which is populated by `OnOrgInfoPacket`. `SendOrgMessage` never reads that property; it reads `Stat.Clan` from `LocalPlayer`. A zero `Client.OrgId` therefore skipped the SDK path for the wrong reason. The gate now reads the same source the SDK does.
- `[VERIFIED]` Owner evidence changes the diagnosis: `#stock` reaches org chat while `#help`, `#cloak` and `#status` do not. All four take the identical `Reply` to `TrySendOrgMessage` path with the same org `ReplyTarget`, and `ProcessBankerStockCommand` performs no retargeting. The transport therefore works and the payload is what differs. Every org attempt now records its own `len=`.
- `[VERIFIED]` Not the AOChatProxy update. `Could not obtain LocalPlayer org stat` is recorded in project history on 2026-09-06, twelve days before the proxy change, and inbound org chat is healthy in the same run.
- Diagnostics now report `Stat.Clan`, `Client.OrgId`, `Client.OrgName`, the observed channel id and the payload length on every attempt, so the next run distinguishes a missing organization stat from a payload the channel refuses.
- `[OPEN]` Why a longer or blob-bearing payload is dropped on the organization channel while `#stock` succeeds. Needs one `#stock` and one `#help` on org from the same run with their `ORG SEND` lines, which now carry lengths.
- Method signatures, enum values and visibility were read from the pinned package metadata rather than inferred: `Stat.Clan = 5` confirmed by its enum neighbours `Flags=0, MaxHealth=1, Mass=2, AttackSpeed=3, Breed=4, Team=6`, alongside `RunSpeed=156` and `Health=27` which the repository already uses. `TryGetStat` and `SendOrgMessage` are public; `set_OrgId` is assembly-internal and was not used. No assistant compilation or live run.

## Session 144 — org replies were oversized, not misrouted

- `[VERIFIED-LIVE 2026-09-18T13:43]` The session 143 gate fix worked: `Org reply submitted through AOSharp.Clientless.Client.SendOrgMessage; awaiting echo confirmation. Stat.Clan=4736; Client.OrgId=0; observedChannel=4736; orgName=Athen Paladins; len=2487`. `Stat.Clan` resolves even though `Client.OrgId` is 0, which confirms those are different sources and that gating on `Client.OrgId` was the wrong test.
- `[VERIFIED]` Root cause of missing org replies is payload size, not routing. Organization replies are sent on the game connection as a `GroupMsgMessage`; guest and tell traffic rides the chat connection, which tolerates far larger messages. `OrgBlobPageSize` was 5200, so a 2487 byte `#help` reply was emitted unsplit and silently dropped, while the shorter `#stock` reply on the identical `Reply` to `TrySendOrgMessage` path arrived. That is exactly the discriminator the owner supplied.
- `OrgBlobPageSize` reduced to 900 so organization replies paginate under the AO chat ceiling. Guest 8000 and tell 7200 are unchanged; they are not affected by the game-connection limit. Echo confirmation from session 140 remains, so the next run reports `ORG DELIVERY CONFIRMED` per page rather than assuming success.
- `[VERIFIED]` Ambiguous bag phantom proven from the banker's own reports. Kbarty lists 19 normal-inventory storage entries but only 18 distinct identities, and `Inventory.Containers` holds 18 storage containers plus the Colonist: exactly one container for `(Container:BB49D3F)` against two outer entries at inventory/66 and inventory/69. Kbsupp matches with 111 entries, 110 distinct, `(Container:BB49C56)` at bank/1 and bank/92. The container view has no record for the second occurrence, which is the discriminator session 142 was built to obtain. Identity-level dedupe is now evidence-backed rather than inferred.
- `[OPEN]` Dedupe not yet implemented; it selects a slot used for later physical bag movement and remains owner-tested custody code. The exact AO organization-channel byte ceiling is also not established; 900 is a conservative value chosen to sit well under the commonly observed limit, not a measured one.
- Validation: log correlation and source review, `git diff --check`. No assistant compilation or live run.

## Session 145 — organization page size is learned, not configured

- `[VERIFIED-LIVE 2026-09-18T13:50]` Organization replies deliver and are proven: `ORG DELIVERY CONFIRMED via Client.SendOrgMessage`, twice, for `#help` and `#help bankers`. Org chat works end to end with echo evidence rather than an assumption.
- `[VERIFIED]` 900 was far too small. `#help` emitted eight pages at 435-569 bytes because `BuildBlobLinks` subtracts a roughly 400 byte envelope from the budget, leaving about 500 of content per page. `[OWNER-DIRECTION]` Kavey is right that 5200 was not arbitrary and that a hand-picked constant is the wrong mechanism: the size must heal itself.
- `OrgBlobPageSize` is removed. `OrgPageBudget()` now probes midway between `_orgSafeLength`, the largest length the chat server echoed back, and `_orgFailLength`, the smallest that vanished, converging on the real ceiling by binary search. Seeded from measured evidence: 569 delivered, 2487 dropped, so the first budget is about 1528 rather than 900.
- `RecordOrgDelivery` feeds every echo result back. A confirmed delivery raises the safe bound; a confirmed disappearance lowers the fail bound and pulls the safe bound under it. A delivery confirmed above a recorded failure reopens the upper bound, because that failure was then not a size limit. Bounds persist to `data/citymanager-org-size.json` and are restored at startup, so the ceiling is learned once and survives restarts.
- `[DECISION]` Guest 8000 and tell 7200 are unchanged. They ride the chat connection, which is not subject to the game-connection limit, and there is no evidence they need calibrating.
- `[OPEN]` The true ceiling is still unmeasured; the system now discovers it instead of being told. Convergence needs a few org replies of increasing size, and each step is logged as `ORG SIZE CALIBRATION`.
- Validation: log correlation, source review, brace and parenthesis balance, `git diff --check`. No assistant compilation or live run; owner builds and tests.

## Session 146 — a busy file must not restart the roster

- `[VERIFIED]` The 14:03:31 re-audit was not the bag hold. All seven healthy bankers had reached `BANKER READY` at 14:02:22 and a full player donation had completed. `SymbiantCatalog.GetPhatzFamilies` then read `data/ledger.json` while another banker was publishing it and threw `IOException: the process cannot access the file ... because it is being used by another process`, through `TryGetRule` → `TryGetDestinationRole` → `StartRecoveryExtraction` → `BankingServiceAgent.Tick`. The tick catch-all called `StartupCensusGate.Block`, and the roster restarted every audit at 14:03:34.
- `[VERIFIED]` Two readers of `ledger.json` bypassed the per-file mutex that every atomic writer takes: `SymbiantCatalog.GetPhatzFamilies` opened it with `FileShare.Read | FileShare.Delete`, which denies a concurrent writer and so fails whenever `File.Replace` holds the file, and `WithdrawalState.RequestStillStored` used `File.ReadAllText`. Every other reader (`ReadJson`, `ReadJsonStrict`) is mutex-guarded and was never exposed.
- `RuntimeStateStore.ReadTextShared` reads with `FileShare.ReadWrite | FileShare.Delete` and retries across the replacement window. It takes no mutex, so it is safe under a caller's own lock — `GetPhatzFamilies` holds `PolicySync`. Writers here publish by temporary file and replace, never in place, so a shared read cannot observe a half-written document.
- `[DECISION]` A read that gave up waiting carries no custody information: nothing was sent, opened or moved, so no item's location became unknown. `StateContentionException` marks exactly that case, and the banking tick retries it instead of blocking. The catch-all's escalation is unchanged for every other exception — an unknown fault is still treated as custody doubt.
- `[INVARIANT]` Tolerance is bounded. Eight contention faults inside a minute of each other are no longer a passing overlap and still escalate to a coordinated census; a quiet minute forgives the streak.
- `[OPEN]` The phantom bag hold on Kbarty and Kbsupp is untouched by this and remains the next item.
- Validation: log correlation, source review, reader/writer sharing audit of every `ledger.json` call site, brace and parenthesis balance, `git diff --check`. No assistant compilation or live run; owner builds and tests.

## Session 147 — one bag listed twice is still one bag

- `[VERIFIED]` The hold is a stale client record, proven from the owner's own snapshot rather than inferred. `storage-state.json` lists Kbarty `(Container:BB49D3F)` at Inventory 66 and Inventory 69 with **the same `LastHandle` 316**, the same 21 items and the same inner slots; Kbsupp lists `(Container:BB49C56)` at BankByRef 92 and BankByRef 1 with the same 9 items. A container identity belongs to exactly one physical bag, and the container view holds exactly one container for each.
- `[VERIFIED]` It is recent and the bot made it. The 2026-09-08 and 2026-09-11 baselines and the censuses of 09-11, 09-12 and 09-13 each list BB49D3F **once**, in bank, walking bank slots 4 → 3 → 2 → 0 as the audit moved it. The 09-18T07:51 census is the first to list it twice, at inventory 66 and 69 with `PreviousLocation` null. The audit's own move sequence creates the duplicate when the client misses the removal at the source slot: it appends the record at the destination and keeps the one at the source.
- `[DECISION]` A repeated container identity is no longer a layout error. `TryValidatePhysicalLayout` now fails only on what the client genuinely cannot resolve: two *different* items in one slot, or a storage bag with no container identity. A bag listed twice is one bag, and refusing to proceed over it blocked the roster indefinitely.
- `StorageBagPolicy.DistinctBags()` enumerates one record per container identity and keeps **the last record in the client's own list order**. The client appends the record a move produces, so the newer entry is where the bag now is and the stale one is the entry whose removal was missed. For Kbsupp that selects BankByRef 1, whose handle 296 is newer than 92's handle 114 — consistent with the return move placing it in the lowest free bank slot.
- `[VERIFIED]` The absolute move checks could never pass with a duplicate present. `ProcessMoveToInventory` required exactly one inventory occurrence and zero bank occurrences; a lingering bank record kept the count at one forever and the bag timed out. Both verifications now compare against the counts captured immediately before the move is sent, so they measure what the move changed. `ArrivedItem` prefers the record at a slot the bag did not previously occupy, so a lingering record is not reported as the new location.
- The condition stays visible: `DUPLICATE BAG RECORD` names every repeated identity, its slots and which record is kept, and `AmbiguousBagReport` still writes the full evidence file. `diagnostic-dumps` now expects `ambiguous-bags-*.json`, so that evidence no longer raises an alien-file warning. Bag capacity counts bags, not records.
- `[OPEN]` The duplicate was ingested before this change: each occurrence produced its own `found-cb-*` transaction ids, so the ledger carries 21 extra Kbarty symbiants and 9 extra Kbsupp symbiants that have no physical backing. The next clean census records each bag once, and reconciliation should retire the unbacked entries through the existing lost/found path. That path is owner-tested custody code and is deliberately not pre-empted here.
- `[OPEN]` Root cause of the missed removal is in the client's move handling, not addressed. Duplicates will keep appearing; the system now tolerates them instead of stopping.
- Validation: owner data-snapshot correlation across baselines, censuses and storage state; `AOSharp.Clientless` IL review of `Inventory.OnMoveItemAction`, `RemoveItem`, `OnContainerUpdate`, `ResetContainers` and `Container.GetHandleSlot`; source review; brace and parenthesis balance; `git diff --check`. No assistant compilation or live run.

## Session 148 — the census staged a bag that was not there

- `[VERIFIED]` Actions are addressed by **slot**, not by identity. `Item.Use` in the pinned `AOSharp.Clientless` compiles to `ldarg.0; ldfld Slot; callvirt set_Target` — the outgoing packet carries the item's slot. So an action aimed at the stale record of a duplicated bag asks the server to act on a slot it considers empty, and nothing happens. That is the whole of Kbarty's hold.
- `[VERIFIED]` `PrepareAuditStagingSlot` chose `Inventory.Items.FirstOrDefault(...)`, which for Kbarty is the `inventory/66` record that session 147's dedupe had already rejected in favour of `inventory/69`. It moved the record the bot itself had judged stale. The log shows the consequence exactly: `inInventory=True; inBank=False; freeSlots=10` fifteen seconds later.
- `[VERIFIED]` Its acceptance test was the same absolute-count pattern fixed inside `BagAuditAgent` in session 147 and missed here: `bankCopies == 1 && inventoryCopies == 0`. With two inventory records a successful move leaves one and one, so it can never pass. The 15-second fallback requires `inventoryCopies == 1` and was skipped for the same reason, so the banker fell through to an indefinite wait every cycle.
- Staging now selects from `DistinctBags()` and skips any identity the client lists twice. Kbarty has one duplicated bag and seventeen clean ones. Verification compares against the bank and inventory counts captured immediately before the move.
- `[VERIFIED]` Session 147's keep-the-last-record rule is **not stable**. Kbsupp logged `keeping bank/92` at 14:28:56, `keeping bank/1` at 14:30:49 and `keeping bank/92` at 14:34:16: the client's list order changes as the audit moves bags, so the choice flip-flopped between audits of the same layout. Replaced with a deterministic order — a record proven unresponsive last, then bank before inventory, then lowest slot.
- `[DECISION]` Nothing in the client's listing distinguishes a live record from a stale one, so the only thing that earns a preference is having answered. When a slot-addressed move produces no change at all to either listing and the bag is duplicated, `NoteUnresponsiveRecord` remembers that record and `DistinctBags()` stops choosing it for the rest of the session. A fresh login rebuilds the listing from the server and retires the question, so the memory is deliberately session-scoped.
- `citymanager-org-size.json`, written by the session 145 org calibration, is registered as a known data file; the startup `Alien file in data` warning was self-inflicted.
- `[OPEN]` **A second audit on one connection fails every inventory bag.** In the 14:28 run each banker's second audit reported `opened=92 failed=18`, `opened=102 failed=18`, `opened=94 failed=18` — in every case exactly its inventory-bag count, with all bank bags fine. Kbinfa, which had disconnected and relogged and was therefore on its first audit, passed 110/110. `Container.IsOpen` is `Handle != 0` and `ProcessOpen` requires a Container object different from the one seen before `Use()`, so a container already open from the previous audit cannot satisfy it. Bank bags escape because they are moved out and back. Not yet fixed: the audit result file carries the per-bag error text and the pre/post handles, and that evidence decides between re-opening and reading the open container in place.
- Validation: SDK IL (`Item.Use`, `Container.get_IsOpen`, `Container.GetHandleSlot`, `Inventory.OnMoveItemAction`, `Inventory.RemoveItem`, `Inventory.ResetContainers`), owner log correlation across three census cycles, source review, brace and parenthesis balance, `git diff --check`. No assistant compilation or live run.

## Session 149 — a held banker must come back, and a phantom must not cost a slot

Both findings come from a 112-agent adversarial sweep over six lenses; 21 findings were confirmed by every verifier and 32 were refuted.

- `[VERIFIED]` **A staging hold was permanent.** `WaitForStagingChange` deletes `.presence.json` for non-central roles so the running cycle can drop the member and finish. But `Tick` returns at the `_stagingFailureLayout` check *before* the presence heartbeat is rewritten, and `Coordinate` builds every later cycle's roster from presence files alone — so the member was never invited again. The escalating 60/120/240/480/960s `_censusRejections` backoff is a different branch, reached only after an audit produced a result; a worker held before it ever issued the audit command never touches it. Nothing was scheduled, and the log's promise that the banker "re-evaluates automatically when the observed layout changes" was the only exit. Kbarty sat out eleven minutes and three cycles; Kbinfa recovered solely because it disconnected and relogged.
- The hold now carries its own escalating cool-off, 60s to 960s, after which the member clears the hold, republishes presence and rejoins a later cycle with a fresh candidate bag. It still withdraws presence while held, because a member that stays present but produces no result would stall the cycle instead of being dropped by it. The log line states the retry interval instead of promising something else.
- `[VERIFIED]` **The phantom record is the only reason Kbarty staged at all.** IL of the pinned SDK: `Inventory.get_NumFreeSlots` is `30 - _items.Count(i => i.Slot.Type == 104)` — a count of *records*, not of occupied slots. A bag listed twice occupies one slot and subtracts two. Kbarty reported `freeSlots=10` against `requiredSlots = MaxTradeItems + 1 = 11` while physically holding eleven. Had headroom been measured in slots, the staging step would have returned immediately and Kbarty would have gone straight to the audit.
- `StorageBagPolicy.FreeInventorySlots()` adds back the duplicate inventory records, and every free-slot test in the staging path now uses it. The failure line reports both numbers, so a future log shows the correction rather than hiding it.
- `[DECISION]` Four independent lenses reached the `NumFreeSlots` finding separately, which is why it is treated as the root trigger rather than a contributing factor.
- Validation: SDK IL (`Inventory.get_NumFreeSlots` and its predicate lambda, `INVENTORY_CAPACITY`/`START`/`END`, `IdentityType` constants), code trace of the presence and roster paths, owner log correlation across cycles `5a624ae4`, `0f865699` and `0f061478`, brace and parenthesis balance, `git diff --check`. No assistant compilation or live run.

## Session 150 — a lookup by identity must resolve to one bag

- `[VERIFIED-LIVE 2026-09-19T11:59:56]` Sessions 146-149 work. Census `65920325` released with **`audited bankers=9`** — all nine — and every banker reached `BANKER READY`. Kbarty completed its first audit ever, `bags=120 opened=120 failed=0`, and **issued no staging move at all**, because `FreeInventorySlots()` reported the eleven slots it physically had. `Runtime inventory contains no alien entries`. Kbarty then stored a donated symbiant end to end: `STORED VERIFIED ... bank:0/inner:2`, `STORAGE BATCH COMPLETE destination=Kbarty; verified stored=1/1`. The deterministic dedupe order also held: Kbsupp logged `keeping bank/1` from a listing ordered `bank/1, bank/92` and again from one ordered `bank/92, bank/1`.
- `[VERIFIED]` The remaining failure is a lookup, not a misplaced bag. At 12:00:06 Kbsupp reported `Bank bag returned to unexpected outer slot 92 instead of 1`. `FindBankBagByIdentity` was `Inventory.Bank.Items.FirstOrDefault(...)`, so for `(Container:BB49C56)` it answered with whichever record the client listed first — `bank/92` at that moment, confirmed by the 12:00:09 audit line ordering the pair `bank/92, bank/1`. The job expected `bank/1`, and a record for the bag **did** sit at `bank/1`. The bag had returned; the bot compared the wrong one of its two records and stopped the banker.
- `StorageBagPolicy.PreferredRecord` resolves an identity to the record the deduplication kept, and `AllRecordsFor` returns every record of it. `FindBankBagByIdentity` and `FindInventoryBagByIdentity` now use the former, so no live action is ever aimed at a stale record. `ProcessStorageBagReturn` uses the latter and accepts the return when **any** record of the bag sits at the expected slot; when none does, it still fails and now names every record and its slot.
- `[VERIFIED]` `BankingServiceAgent.Extraction.cs` resolved its bag with `SingleOrDefault` over the live listing, which throws `InvalidOperationException` on a duplicated identity. The tick catch-all converts that into `StartupCensusGate.Block`, a published recovery request and a roster-wide restart. It was reachable only once a banker with a duplicate was operational — which is exactly what this run achieved. Now resolved through `PreferredRecord`, which cannot throw.
- `[DECISION]` Accepting a match on any record is not a weakened custody check. The old code was not checking "is the bag where it belongs"; it was checking "is one arbitrary record's slot the expected slot". A record at the expected slot is the client saying the bag is there, which is the same evidence the check always wanted.
- `[OPEN]` Unchanged from session 149: `BuildCurrentStock` and `FindNextFreeBag` count a duplicated bag twice in persisted state (`#stock` reports 42 symbiants where 21 exist); `CensusApplication` drops the donor anchor for every item in a bag the persisted state lists twice; the SDK's `Bank.RegisterItems` appends without clearing, which is how the duplicate bank records are manufactured; and a second audit on one connection fails every inventory bag.
- Validation: owner log correlation across the 11:57-12:00 run, source review of the three lookup sites, brace and parenthesis balance, `git diff --check`. No assistant compilation or live run.
