# CRU supply

`#cru` requests one Upgraded Controller Recompiler Unit (AOID 257110).
`get 257110` and `withdraw 257110` use the same path. Member access and linked
alts follow existing bank commands. Wait for the ready tell and trade with
Kbcentral within three minutes. Ready CRU and ordinary items share one pickup.

Donate CRU through an ordinary donation trade. It stays in Central inventory;
idle unreserved CRU is merged into one stack. A pickup reserves an existing
single unit or splits one from a larger stack. Expired units rejoin supply.
CRU does not appear in stock, donor totals or lost/found. Backend completed
withdrawal events remain available.

## First owner run

Build/deploy normally. No new settings or data migration is needed.
Donate CRU, then another stack; observe consolidation. Request `#cru`, collect
one unit and check the remaining stack. Request again and allow expiry to
observe restacking. A mixed ordinary-item/CRU pickup uses the same order.

Diagnostics start with `STACK`, `CRU`, or existing `PICKUP` labels. They record
initial quantities, add-template counts, stat updates, merge/split requests and
verification. If quantity is unavailable or an operation does not verify, send
those log lines; unnamed protocol fields are logged rather than guessed.
The first implementation has had static review only, not a build or live run.
