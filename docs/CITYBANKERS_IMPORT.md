# CityBankers import into City Dwellers

Status: implemented source integration; owner build and live validation pending.

## Source boundary

The imported banker implementation is based on CityBankers `main` at
`eadf5a3dce028ba83f3930ce83f96b2e41f91137`.

Only current compiled application/runtime sources were imported. CityBankers
recovery cards, cursor, journal, encrypted memories, and project history remain
in the sibling repository and are not copied into City Dwellers.

## Unified runtime

`CityDwellers.exe` now supervises four components:

- Flipper idle request service;
- Buddies idle request service;
- six always-online CityBankers client domains;
- Manager persistent client.

The bankers retain Central-first startup and their own six-client readiness
barrier. They load the implied sibling `CityBankers.dll`. No `Banker.exe`
project or output exists in this repository.

The explicit physical audit maintenance command is:

```text
CityDwellers.exe bankers-bagaudit
```

Both normal startup and the audit command remain behind the City Dwellers
trusted-network-time gate.

## One settings file

The sole administrator configuration is `citydwellers.json` beside
`CityDwellers.exe`. Its required `Bankers` section preserves the former
CityBankers schema:

- shared `Password`;
- `MaxParallelLogins`;
- `DiagnosticTimeoutMs`;
- nine `Roles` entries containing `Username` and `Character`: `central`, the
  five symbiant families, `spirit`, `dyna`, and `phatz`. The last three
  default mappings are `kbspirit` / `Kbspirit`, `kbdyna` / `Kbdyna`, and
  `kbphatz` / `Kbphatz`. Override a username if its AO account differs.

`Bankers.AcceptancePolicy` is Central's single acceptance, routing, and
retention policy. It belongs in `citydwellers.json`:

```json
"AcceptancePolicy": {
  "SymbiantMaxCopies": 10,
  "SpiritMaxCopies": 5,
  "Items": {
    "123456": { "Name": "Example rare nano", "Role": "dyna", "MaxCopies": 3 },
    "234567": { "Name": "Example never-delete item", "Role": "phatz", "MaxCopies": -1 }
  }
}
```

The built-in catalogue contains the existing symbiants, exactly 654 standard
Shade-spirit AOIDs, and 418 dyna-nano AOIDs. The dyna set contains 220 crystals
classified as `RK Dyna`, a mixed `RK Dyna` location, or `RK Mob` in Nadybot's
maintained nano data plus 198 matching instruction discs from its disc map.
Every one resolves in the bundled AOSharp item database. Dyna defaults to
keep-all except both Frenzy of Fur forms, which start at three copies per
AOID. Both Grid Armor IV forms remain keep-all. No phatz are guessed or
accepted until an admin lists them.

`Items` overrides a built-in AOID or adds a new accepted AOID. `MaxCopies: -1`
keeps every copy, `0` rejects that AOID, and a positive value is the retained-
copy ceiling. A custom AOID with no `MaxCopies` defaults to keep-all. Policy is
read at process start; restart after editing it. Dyna source snapshot:
Nadybot/Nadybot commit `de9e3b2c8d2f91df87c614a3d9f91bc16c2eacf2`,
`src/Modules/NANO_MODULE/nanos.csv` and
`src/Modules/DISC_MODULE/discs.csv`.

Credentials are private deployment data and are never committed.

## Mutable data

All imported banker state now resolves beneath the same executable-adjacent
`data` directory as other City Dwellers state. This includes storage and
stock state, queue state, active ledger/index, append-only ledger events,
history, activity logs, diagnostics, handoff markers, physical audits, and
baseline archives.

The old CityBankers physical/accounting state must be copied once into the
corresponding unified `data` locations before the first authoritative live
run. Do not start with empty state when the banker characters already hold
physical stock, and do not synthesize or edit stock files by hand.

For the owner's 2026-09-08 cutover, the migration archive is rooted at the
contents of `data`, not at a legacy `settings` or nested `data` wrapper. It
retains the current operational storage, stock, empty dispatch queue, active
ledger/index, latest coherent physical/baseline evidence, completed repair
receipt, and durable history. Legacy `banker.json`, stopped-process readiness
markers, completed repair-plan attempts, redundant older audit snapshots,
diagnostic dumps, and old text logs are not migration inputs.

Every compiled Banker path resolves through the executable-adjacent `data`
directory. Startup readiness files are regenerated there; they are never
restored from the old settings root.

## Deliberately unchanged

This first import does not redesign CityBankers behavior. In particular:

- Kavem remains its bootstrap command administrator;
- public donation admission remains as implemented;
- CityBankers stock, donor, trade, queue, routing, and recovery behavior remain
  separate from Manager commands;
- City Dwellers admin, member, and alt data are not yet wired into CityBankers;
- physical AO inventory remains the final authority.

Those shared concepts are the next deliberate integration layer, not part of
the executable/config import.
