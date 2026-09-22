# Public service concurrency review — session 191

Status: source review and fixes completed. Compilation and live AO/load testing belong to the owner and were not performed here. This is not a claim that ten simultaneous users have been tested.

## Capacity and ownership

Central has one physical trade window. Existing admission allows four active pickup orders, each with up to ten items; further orders receive a capacity response. These are orders, not simultaneous trade windows. Ordinary internal dispatch also uses the full ten-item trade window.

Withdrawal admission already uses a named mutex across domains, checks current reservations and live storage again, groups orders by canonical recipient, and prevents additions to an open pickup trade. Saves reject stale revisions. Pickup claims are made against current persisted state under that same lock. Only the order's allowed collectors can claim it; offering items in a pickup trade is declined. Receipt completion requires matching inventory evidence before recording delivery.

AO inventory/trade operations remain on each banker's update thread. Named-pipe handlers submit proposals; they do not move items. Raid creation/ownership uses the existing raid lock and owner checks. Public donation readiness is separate from administrator privilege despite the historical `IsTrustedAdmin` trade-caller exception.

## Concrete defects fixed

| Finding | Change |
|---|---|
| Public commands could create unlimited background jobs | Manager permits at most 24 queued/running public jobs, at most three per canonical member. Stock rendering also moves off the chat/update callback. Status, cloak, buffers, item lookup, stock, donor/pickup/lost history, get and CRU use this boundary. |
| A single sender could continually generate work and replies | Manager public command bucket: burst six, refill one every two seconds; shared burst 64/refill ten per second. Known alts share the member budget across tell/org/guest. Rejection notice at most once per ten seconds, privately. Authenticated admin controls bypass public saturation; unauthorized admin-command spellings do not. |
| Replies could accumulate faster than they are delivered | Public Manager/buffer input pauses at 256 pending tells and resumes below 128, sampled once per second. State changes are logged. During this severe-backlog pause, input is not queued and no extra rejection tells are generated. Already accepted work and durable transaction tells are retained. This is admission backpressure, not a hard disk-queue size guarantee. |
| Two Manager withdrawal tasks could choose the same available copy before either admitted it | Serialize selection plus admission within Manager. The existing cross-domain reservation mutex remains authoritative. A losing request cannot double-reserve a copy; two available copies can be selected successively. |
| IPC timeout could cancel a reply after AO-thread admission had started | Atomic queued/claimed/cancelled state. Only unclaimed work can be cancelled. Claimed work reports pending rather than falsely rejected. CRU messages distinguish pending from not queued. Banker proposal storage is bounded to 64 entries, drained at most 32 per update. |
| A new trade could touch the preceding donation or pickup state before receipt verification finished | Reject incoming trades before changing that state. Donation setup and pickup claim also guard retained receipts. Late Declined cannot reset either a completed-trade verification or a cancellation verification. The donation bridge acts only for the donation owned by the banking actor. |
| An endlessly edited nonempty donation had no total occupancy deadline | Two-minute monotonic deadline, independent of offer edits/acceptance. Timeout declines the trade and gives that donor a 15-second retry backoff. The existing empty-trade timeout remains. This is trade occupancy control, not a relog delay. |
| Buff queue duplicate/capacity checks were not atomic and the queue was unbounded | Locked queue admission, deduplication including the current cast, maximum 32 entries per character and 256 per buffer. Local and IPC-delivered requests use the same admission. One duplicate no longer skips later distinct buffs in the request. |
| One buffer casting exception cleared all users' queued requests | Retain the remaining queue and remove only the current failed request. Explicit administrator clear retains its original behavior. |
| Direct buffer tells bypassed Manager's input controls | Per-buffer sender bucket: burst four, refill one every two seconds, bounded sender bookkeeping and throttled notices. Shared tell-backlog admission applies there too. |

No automatic physical audits were restored. An affected banker can relog outside a trade; remaining uncertainty is reported locally. Manager is not relogged by banker recovery.

## Owner live check after Release rebuild/restart

Start with two players, then use the same scenarios with ten. Keep the resulting logs; avoid deliberately disconnecting a bot mid-trade.

1. Request the same AOID simultaneously. With one copy, only one reservation may succeed; with two copies and capacity, both may succeed. Confirm separate recipients and one removal/history entry per actual delivered copy.
2. Fill four different pickup orders. A fifth new order should be refused without changing the existing four. A fourth item for one member should be refused. An alt of a known main should share that main's order/budget.
3. While A donates five supported items, B requests an item, C requests CRU and others query stock/status. Busy responses are acceptable; altered recipients, duplicate stock, silent acceptance of a rejected mutation, or an audit cascade are not.
4. A cancels or completes a trade; B immediately opens one. B may briefly receive a verification-busy reply. A's receipt, donor identity, pickup ownership and original verification deadline must remain intact.
5. Keep changing a valid donation offer without finishing. It must close by the two-minute deadline; that donor cannot instantly reclaim the slot, while others remain eligible. This does not promise fair scheduling against coordinated accounts repeatedly taking the single game trade slot.
6. Collect A's ready order as B. B must receive none of A's items. Open A's pickup as an allowed alt, add a donation item, cancel, and reopen: no mixed donation/pickup or duplicated delivery.
7. Have one player spam public commands while another makes normal requests. Expect throttling/busy responses for overload, bounded work, and no automatic audit/relog of healthy peers. At a severe tell backlog new public input intentionally pauses until delivery catches up; it is not stored for later execution.
8. Request overlapping buff sets from several users and repeat them. No duplicate pending cast for one character; distinct later buffs survive a duplicate; a failed cast must not clear unrelated requests.

## Limits of the conclusion

No finite source review proves every interleaving or server behavior. These changes improve isolation and overload behavior; they do not increase Central's physical trade concurrency, guarantee FIFO fairness for game trade opens, automatically retry uncertain mutations, or establish sustained ten-user throughput. Exception types, source fingerprints, timings and the affected transaction IDs in live logs are the evidence for any follow-up. A failed operation is not permission to reintroduce automatic audits.

Validation performed: focused source/call-path/interleaving review, project XML and explicit source inclusion check, active C# delimiter/conditional checks, and `git diff --check`. No compilation, automated test suite, or AO session was run by the assistant.
