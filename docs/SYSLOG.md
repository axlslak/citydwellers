# Manager-owned syslog events

Add this root section to `citydwellers.json`, replacing `your-syslog-host` with
its actual DNS name or IP. Restart after editing. Missing/disabled means no
reporting pipeline or external connection. No credentials/TLS are used.

```json
"Syslog": {
  "Enabled": true,
  "Host": "your-syslog-host",
  "Port": 514,
  "Transport": "tcp"
}
```

`tcp` is the default; `udp` is also accepted. Sender hostname comes from the
machine automatically; service APP-NAME is always `citydwellers`. TCP uses
RFC6587 octet-counting framing; messages use RFC5424, local0 facility, actual
severity and the original event UTC timestamp with milliseconds. The process
field is the real shared OS PID. The body begins `(Character[AO-character-ID])`;
before the client has an ID it says `unknown`, never an invented ID.

## Ownership and coverage

Bankers create structured reports with event ID, UTC time, name, AO ID, role,
severity, message and event-specific data. Workers send them to Central over
process-scoped named pipes. Central validates the configured source role and
relays the original fields to Manager. Manager validates the route, deduplicates
recent event IDs, appends the SQL `citydwellers-events.jsonl` stream, and alone sends syslog.
Manager's own reports enter its local queue. Invalid logging configuration warns
and disables only external forwarding without stopping internal SQL event capture. The host console is NOT a syslog
source. Complete runtime diagnostics are stored in MySQL; no local runtime log is written.

Initial event coverage:

- `manager.logging`: Manager reporting starts.
- `bank.open`: bank-open success/failure and capacity snapshot.
- `banker.ready`: startup readiness.
- `census.started`, `census.applied`, `census.recovery`: recovery reason/result.
- `bank.transfer`: existing transfer progress stages, including donation queuing,
  dispatch, storage, withdrawals, waits and failures; stage and batch ID in Data.
- `cloak.changed`: Manager's recorded cloak observations.

This is an operational event stream, not every SDK debug line. Buffers, Buddies,
Flipper and additional domain events have not yet been instrumented here.

Each source event is synchronously committed to MySQL before background relay.
IPC queues hold 256 reports and the syslog sender holds 1024. Forwarding retries
use bounded waits; full queues or oversized reports can skip forwarding, while
the original SQL event remains available. IPC receipt acknowledges in-memory
queueing, not delivery to the external syslog server. A crash may lose queued
forwarding but cannot erase an already committed SQL source record.

TCP retries may duplicate a transmitted event; the stable event `Id` allows
deduplication. UDP has no delivery acknowledgment. MySQL records are the durable
source; there is no local diagnostic fallback or automatic disk replay. Plan
SQL backup and retention explicitly; this migration does not delete SQL history.

## Rsyslog receiver file

Your existing TCP/UDP514 listeners can remain as configured. Put this rule in
`/etc/rsyslog.d/30-citydwellers.conf`, ahead of any rule that discards these events:

```rsyslog
# Increase maxMessageSize in your existing global() block if it is lower.
# global(maxMessageSize="64k")
template(name="CityDwellersLine" type="string"
  string="%timereported:::date-rfc3339% %hostname% citydwellers[%fromhost-ip%]: %msg%\n")
if ($app-name == "citydwellers") then {
  action(type="omfile" file="/var/log/citydwellers.log" template="CityDwellersLine")
  stop
}
```

The sender IP is rsyslog's `fromhost-ip`; it is not a fabricated process ID.
An example shape is:

```text
2026-09-17T00:08:11.123Z GAME-PC citydwellers[192.168.1.20]: (Examplebanker[123456789]) warning census.started {"Id":"...",...}
```

The example address/identity is illustrative, not installation configuration.
Validate/reload on the server with `sudo rsyslogd -N1` and your usual service
reload procedure. Include this new file in your server's logrotate policy.
TCP is preferable for large event payloads; size limits also depend on receiver
configuration. UDP messages near maximum size may fragment or be lost.

## Time-aware diagnosis

Copy `tools/citylog.py` to the server. It uses Python3's standard library, compares
actual timezone-aware timestamps, and accepts plain files, .gz archives or stdin.
Time comes from the documented receiver template's original-event first field,
so a delayed delivery is selected by occurrence time, not arrival time.

```sh
python3 citylog.py --since 15m --bot Kbcentral | ccze -A
python3 citylog.py --since '2026-09-17T03:00:00+03:00' --until '2026-09-17T03:10:00+03:00' --event census.started
python3 citylog.py --since 1h | grep -E 'warning|error' | ccze -A
tail -F /var/log/citydwellers.log | grep --line-buffered -F '(Kbcentral[' | ccze -A
```

For future reports: provide a precise interval and original bot. Export that
interval from this dedicated file. Keep all bankers in the interval when the
problem involves a transfer; filtering only Central can hide the peer's report.

CCZE has a syslog plugin and generic word coloring, but it does not know our
event names or JSON schema. Exact coloring depends on its version and your file
format; custom IP-in-brackets and RFC3339 headers may color differently from
traditional syslog. No ANSI escape codes are transmitted or written to syslog.
Use `ccze -l` to see installed plugins. Sources:
https://manpages.debian.org/bookworm/ccze/ccze.1.en.html
https://docs.rsyslog.com/doc/configuration/properties.html
https://www.rfc-editor.org/info/rfc6587/

## MySQL source records

Every service event is committed synchronously to its SQL source stream before
entering the bounded relay. Manager may also record the same event in its
canonical stream. The JSON event `Id` is the deduplication identity when querying
across both streams. Optional syslog failure does not disable source capture.
See [the SQL migration and diagnostics guide](MYSQL_MIGRATION.md).
