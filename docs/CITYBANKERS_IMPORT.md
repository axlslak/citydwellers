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
- six `Roles` entries containing `Username` and `Character`.

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
