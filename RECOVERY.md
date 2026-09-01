# City Dwellers Recovery Entry Point

Recovery key: `CITYDWELLERS-RECOVER-V1`

This file is the canonical front door for a new ChatGPT/Codex session. The
owner should only need to provide the recovery card below and the memory
password separately.

## Recovery card

```text
CITYDWELLERS-RECOVER-V1 | REPO axlslak/citydwellers | BRANCH master | PROJECT citydwellers | READ RECOVERY.md | MEMORY PASSWORD FOLLOWS SEPARATELY
```

The same single line is stored in `RECOVERY_CARD.txt`. It deliberately does not
contain the password.

## Instructions to the receiving session

1. Clone or fetch `axlslak/citydwellers`, branch `master`.
2. Confirm the worktree/ref and inspect current Git history before trusting a
   remembered commit.
3. Read, in this order:
   - `AGENTS.md`
   - `docs/REPOSITORY_COORDINATION.md`
   - `memory/CURSOR.json`
   - `memory/JOURNAL.jsonl`
   - `memory/PROTOCOL.md`
   - `docs/PROJECT_STATE.md`
   - `docs/PROJECT_HISTORY.md`
   - `memory/MANIFEST.json`
   - `memory/README.md`
4. If the cursor says `in_progress`, find the matching journal `BEGIN`. The
   lock is repository-wide. Remain read-only unless the owner says the prior
   writer was interrupted and this session is recovering it. Inspect Git and
   then resume it or record an `ABORT`/`SUPERSEDE` transaction; never start an
   unrelated write.
5. Ask for the password if it was not supplied, decrypt
   boot-required memories in manifest order, and verify byte counts and
   SHA-256 hashes. Never store the password in Git.
6. Current Git/code/reproducible test evidence wins over encrypted recollection.
7. Report the recovered state before making code changes unless the owner
   explicitly asked for an implementation in the same request.

CityBankers is not recovered here. Its independent card is
`CITYBANKERS-RECOVER-V1 | REPO axlslak/citybankers | BRANCH main | READ RECOVERY.md | MEMORY PASSWORD FOLLOWS SEPARATELY`.

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
