# City Dwellers Repository Coordination

City Dwellers lives in `axlslak/citydwellers` on `master`. CityBankers is a
separate sibling repository, `axlslak/citybankers` on `main`. Do not put one
project's recovery card, state, history, or application code into the other.

Kavey permits sessions to share useful generic engineering knowledge and
guarantees that only one GPT session will write across both repositories at a
time.

## This repository's lock

- `[INVARIANT]` Exactly one session may write City Dwellers at a time. Chat and
  Work mode do not create separate write scopes.
- `[INVARIANT]` `memory/CURSOR.json` locks this entire repository and
  `memory/JOURNAL.jsonl` is its append-only transaction history.
- `[INVARIANT]` `master` is the only City Dwellers integration branch. Never
  force push.
- A session may inspect, analyze, and discuss committed content while the
  cursor is `in_progress`, but it does not edit, commit, or publish unless it
  is recovering that exact interrupted transaction.

CityBankers has its own equivalent cursor and journal. Neither Git cursor can
atomically lock both repositories; the one-writer-across-both guarantee is
Kavey's scheduling rule.

## City Dwellers writer sequence

1. Fetch/rebase `master` and confirm local `HEAD` matches `origin/master`.
2. Read the cursor and newest journal records. Proceed only when the cursor is
   `idle`, or when explicitly recovering its active transaction.
3. Append a journal `BEGIN`, set the cursor to `in_progress`, then commit and
   publish that marker before implementation.
4. Make one focused change and update City Dwellers state/history.
5. Before final publication, fetch again. If the remote moved unexpectedly,
   stop and reconcile rather than overwrite it.
6. Publish the change, append exactly one terminal journal record (`COMMIT`,
   `ABORT`, or `SUPERSEDE`), return the cursor to `idle`, and publish the seal.

If usage limits interrupt a writer after step 3, the committed cursor tells the
next City Dwellers session exactly what to recover.

## Sibling-repository boundary

| Project | Repository | Branch | Recovery key |
|---|---|---|---|
| City Dwellers | `axlslak/citydwellers` | `master` | `CITYDWELLERS-RECOVER-V1` |
| CityBankers | `axlslak/citybankers` | `main` | `CITYBANKERS-RECOVER-V1` |

Validated generic ideas may be recorded in `docs/SHARED_ENGINEERING.md` and
then deliberately adopted in the sibling repository. The receiving project
must cite source evidence and verify compatibility. Never transfer credentials,
private raw logs, private account data, or private third-party material.
