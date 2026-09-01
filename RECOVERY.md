# City Dwellers / City Banker Recovery Entry Point

Recovery keys: `CITYDWELLERS-RECOVER-V1`, `CITYBANKER-RECOVER-V1`

This file is the canonical front door for a new ChatGPT/Codex session. The
owner should only need to provide the appropriate recovery card. The City
Dwellers memory password is supplied separately when historical encrypted
conversation context is needed.

## Recovery card

```text
CITYDWELLERS-RECOVER-V1 | REPO axlslak/citydwellers | BRANCH master | PROJECT citydwellers | READ RECOVERY.md | MEMORY PASSWORD FOLLOWS SEPARATELY
CITYBANKER-RECOVER-V1 | REPO axlslak/citydwellers | BRANCH master | PROJECT citybanker | READ RECOVERY.md
```

The lines are also stored in `RECOVERY_CARD.txt` and
`CITYBANKER_RECOVERY_CARD.txt`. Neither contains a password.

## Instructions to the receiving session

1. Clone or fetch `axlslak/citydwellers`, branch `master`.
2. Confirm the worktree/ref and inspect current Git history before trusting a
   remembered commit.
3. Identify `PROJECT citydwellers` or `PROJECT citybanker` from the recovery
   card or the owner's current request.
4. Read, in this order:
   - `AGENTS.md`
   - `docs/REPOSITORY_COORDINATION.md`
   - `memory/CURSOR.json`
   - `memory/JOURNAL.jsonl`
   - `memory/PROTOCOL.md`
5. If the cursor says `in_progress`, find the matching journal `BEGIN`. The
   lock is repository-wide. Remain read-only unless the owner says the prior
   writer was interrupted and this session is recovering it. Inspect Git and
   then resume it or record an `ABORT`/`SUPERSEDE` transaction; never start an
   unrelated write.
6. Read the selected project's checkpoint:
   - City Dwellers: `docs/PROJECT_STATE.md` and
     `docs/PROJECT_HISTORY.md`.
   - City Banker: `docs/citybanker/PROJECT_STATE.md` and
     `docs/citybanker/PROJECT_HISTORY.md`.
7. For City Dwellers, also read `memory/MANIFEST.json` and
   `memory/README.md`. Ask for the password if it was not supplied, decrypt
   boot-required memories in manifest order, and verify byte counts and
   SHA-256 hashes. Never store the password in Git.
8. A City Banker recovery does not decrypt City Dwellers conversations unless
   the owner specifically asks for that historical cross-project context.
9. Current Git/code/reproducible test evidence wins over encrypted recollection.
10. Report the recovered state before making code changes unless the owner
   explicitly asked for an implementation in the same request.

## The Heaven Sent rule

Every session is mortal. Before a long, risky, or multi-stage operation, leave
a small durable clue for the next session: a journal `BEGIN` and an
`in_progress` cursor committed and published to `master`. This acquires the
repository-wide writer lock. Finish with `COMMIT`, `ABORT`, or `SUPERSEDE` and
return the cursor to `idle`. A future session must be able to tell where the
previous one stopped without asking the owner to reconstruct it from memory.

## Security boundary

There is no honest mechanism by which a public repository can be made
"understandable only by ChatGPT." ChatGPT has no stable private key shared
between sessions. Confidentiality comes from AES-256-GCM encrypted memories
and the password retained by the owner. Public state, history, cursor, and
journal entries must remain sanitized and must never contain credentials,
private logs, or private third-party material.

The recovery card is an address, not a secret. The password is the secret.
