# City Banker — Persistent Project History

This is City Banker's compact chronological engineering history. Add verified
decisions and outcomes that a future session needs to understand the current
code. Do not use it as a raw conversation log.

## 2026-09-01 — Durable coordination initialized

Kavey designated City Banker as a cousin project in the same repository as
City Dwellers and authorized two GPT sessions to work on it. Exactly one
session may write across the whole repository at a time.

`[DECISION]` City Banker uses `master`, the shared journal, and the global
cursor lock. It keeps separate state/history so its technical truth is not
mixed with City Dwellers. Cross-project ideas may be curated in
`docs/SHARED_ENGINEERING.md` and require compatibility evidence before reuse.

`[OPEN]` The first City Banker implementation transaction must replace the
empty technical checkpoint with repository-verified architecture and next
work; no application details were invented during coordination setup.
