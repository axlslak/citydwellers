> Retired in session117 after owner logs verified completion for all nine bankers
> (seven equipped, Central and Dyna absent). The following describes the historical
> migration. Permanent storage restrictions and portable-bank support remain.

# One-run Colonist backpack repair

This release contains a temporary migration for AOID296977. The storage exclusion
and portable-bank preference are separate from the migration and remain afterward.

Before starting, put a Portable Bank Terminal (AOID288762) in each banker's **normal
inventory**, not inside a bag. Build and deploy the complete solution output.
Start normally; no bank ID from a player is required when the portable opens bank.
Do not rearrange items during the repair/census.

Bank opening calls the portable item's `Use()` first, then verifies `Bank.IsOpen`.
After an eight-second failure (or a Use exception), the existing cached/office
terminal fallback remains available. If all attempts fail and the portable is
still present, retries run every thirty seconds. Without a portable, the old
terminal configuration/lookup remains the fallback. Nothing deletes that code.
The portable is personal service equipment, excluded from stock and routing.

The temporary migration runs under the startup census custody pause, before bag
collection. It identifies the Colonist backpack by AOID and container identity,
not name or remembered bag order. It opens it and transfers remaining contents,
one item at a time, into Small Backpacks (AOID99228), verifying exact source and
destination contents after each command. It can stage bags between bank and
normal inventory. Equipped bags are never used to make staging space.
Bank-staged destinations are returned to bank; their bank slot/order may change.
After the Colonist is empty, the repair equips it on the back and verifies the
actual equipment slot. An already worn Colonist is not moved out of equipment.

The repair stops with `COLONIST REPAIR BLOCKED` if space is unavailable, the back
slot contains another item, multiple Colonist backpacks make selection ambiguous,
or a move/open/equip cannot be verified. It does not discard items, replace back
gear, infer success, resend an unverified move, or let census publish partial
repair results. Keep the log and physical state for diagnosis in that case.

Success prints `COLONIST REPAIR COMPLETE` and saves:

`data/colonist-backpack-repair-v1/<character>.done.json`

This is once per banker, persisted across restarts. A banker without a Colonist
gets a no-backpack completion marker. An interrupted, uncompleted repair resumes
from freshly observed contents on restart; already moved items are not replayed.
The adjacent progress file records the last issued action and transfer evidence.
After completion, the normal startup census re-reads actual bag locations and
reconciles storage/ledger state. Old bag order is not assumed. Ambiguous old donor
claims retain the existing reconciliation treatment; no attribution is invented.

After the owner confirms completion on all bankers, remove
`ColonistBackpackRepair.cs`, its project entry, and its lifecycle/call sites in
`StartupCensusGate`. Keep `StorageBagPolicy`, its selector checks, and the personal
item exclusions. Those permanently prevent Colonist or equipped containers from
entering storage rotation again. Leave the completed markers as historical proof.

This change was statically reviewed. The owner performs compilation and live AO
verification; no assistant account login or live item movement was performed.

Storage rotation accepts only Small Backpacks (AOID99228) in normal inventory or
bank. Other bag types and all equipped bags are excluded. Startup reports
`SMALL BACKPACK SHORTAGE` against configured finite copy limits; a shortage does
not authorize use of another bag type or equipped bag.
