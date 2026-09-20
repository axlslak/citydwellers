# logintry

Build the solution (or CityDwellers project) in **Release**. The independent
probe is emitted as `release/logintry.exe`. Building LoginTry alone also restores
its NuGet dependencies and the pinned GameData files. Requires .NET Framework 4.8.

From the release directory:

```text
logintry <aoaccount> <aopass> <aochar>
```

Quote arguments containing shell-special characters. Use an account/character
that is not already being used by the running bots or another client.

Nine sequential attempts on Rubi-Ka, one client at a time. No production
plugins, audits, membership rules, chat connection, automatic reconnect, added
cooldown or in-world dwell. One calling thread drives the SDK update pump;
the SDK still performs asynchronous socket I/O. Each attempt gets a fresh
AppDomain, which is fully unloaded before the next is created. Login ends on
`CharacterInPlay`; `Client.Disconnect` follows immediately after that update
returns. No extra wait for a world-settling rule. A failed login advances to the
next attempt; the pump has a 120-second failure timeout, not a cooldown.
Ctrl+C cancels further attempts and tears down the current client.

Output goes to the console and a timestamped `logintry-*.log` beside the exe.
Send that file back. UTC timestamps and monotonic millisecond timings include:

- Attempt start, login start, authentication/character-list/full-character milestones.
- Character-in-play or login rejection/error/timeout.
- Disconnect call, observed disconnected event, and completed AppDomain unload.
- Per-cycle setup, login, local disconnect, unload and inter-attempt gap.
- Success/failure totals and min/median/mean/max successful login time.

The measured gap includes logging and domain setup/teardown; it is not zero
just because no delay is imposed. Local disconnect is **not** a server logout
acknowledgement. The next successful login supplies the server-acceptance
observation. These measurements describe full fresh-client cycles, not reuse
of a still-running client session. A stuck synchronous SDK call can outlast the
pump timeout. Unload failure stops the experiment rather than overlapping clients.

Account/password/argument text and raw SDK logs are not written to the result.
The requested command-line credentials remain visible to local process tools;
no credentials are saved to config files. This executable never runs as part
of normal City Dwellers startup; it is a build dependency only.
