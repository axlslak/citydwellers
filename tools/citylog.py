#!/usr/bin/env python3
"""Filter the documented rsyslog City Dwellers file by real, timezone-aware time."""
import argparse
import datetime as dt
import gzip
import re
import sys


def instant(value):
    if value == 'now':
        return dt.datetime.now(dt.timezone.utc)
    relative = re.fullmatch(r'(\d+)([mhd])', value)
    if relative:
        seconds = int(relative[1]) * {'m': 60, 'h': 3600, 'd': 86400}[relative[2]]
        return dt.datetime.now(dt.timezone.utc) - dt.timedelta(seconds=seconds)
    parsed = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    if parsed.tzinfo is None:
        raise ValueError('Include the timezone, e.g. +03:00 or Z.')
    return parsed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('files', nargs='*', default=['/var/log/citydwellers.log'], help='Files, .gz archives, or - for stdin')
    parser.add_argument('--since', required=True, help='ISO timestamp with timezone, or 15m / 2h / 1d ago')
    parser.add_argument('--until', default='now', help='ISO timestamp with timezone (default: now)')
    parser.add_argument('--bot', help='Exact originating character name')
    parser.add_argument('--event', help='Event name, e.g. census.started')
    args = parser.parse_args()
    try:
        start, end = instant(args.since), instant(args.until)
        if start > end:
            raise ValueError('--since must not be after --until')
    except ValueError as error:
        parser.error(str(error))
    for name in args.files:
        stream = sys.stdin if name == '-' else (gzip.open if name.endswith('.gz') else open)(name, 'rt', encoding='utf-8', errors='replace')
        try:
            for line in stream:
                fields = line.split(None, 1)
                if not fields:
                    continue
                try:
                    stamp = instant(fields[0])
                except ValueError:
                    continue
                if not start <= stamp <= end:
                    continue
                if args.bot and ('(' + args.bot + '[').lower() not in line.lower():
                    continue
                if args.event and '"Event":"' + args.event + '"' not in line:
                    continue
                sys.stdout.write(line)
        finally:
            if stream is not sys.stdin:
                stream.close()


if __name__ == '__main__':
    try:
        main()
    except BrokenPipeError:
        pass
