#!/usr/bin/env python3
"""Extract buff discovery metadata from the game's tinkerparser nanos.json dump."""
import argparse
import json
from pathlib import Path


def metadata(nano):
    stats = {s['Stat']: s['RawValue'] for s in nano['StatValues']}
    actions = (nano.get('ActionData') or {}).get('Actions', [])
    use = next((a.get('Criteria', []) for a in actions if a['Action'] == 3), None)
    spells = [s for event in nano.get('SpellData', []) if event['Event'] == 0
              for s in event['Items']]
    # AO's friendly buff flag, a native other-target effect, duration and a use
    # action distinguish service buffs from attacks, self nanos and aura payloads.
    friendly = bool(stats.get(0, 0) & 0x10000) and not (stats.get(0, 0) & 0x8000)
    professions = [c['Value2'] for c in (use or [])
                   if c['Value1'] in (60, 368) and c['Operator'] == 0]
    target_level = 0
    target = False
    for c in use or []:
        if c['Operator'] in (18, 19):
            target = c['Operator'] == 18
        elif target and c['Value1'] == 54 and c['Operator'] in (0, 2):
            target_level = max(target_level, c['Value2'] + (c['Operator'] == 2))
    return dict(Id=nano['AOID'], Name=nano['Name'], Description=nano.get('Description') or '',
                Profession=professions[0] if len(set(professions)) == 1 else 0,
                Level=target_level, Ncu=stats.get(54, 0),
                DurationSeconds=stats.get(8, 0) // 100, NanoLine=stats.get(75, 0),
                StackingOrder=stats.get(551, 0), NanoCost=stats.get(407, 0),
                Range=stats.get(287, 0), AttackMilliseconds=stats.get(294, 0) * 10,
                RechargeMilliseconds=stats.get(210, 0) * 10,
                CanBuffOthers=bool(use is not None and friendly and stats.get(8, 0) > 0
                                   and any(s.get('Target') == 3 for s in spells)))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('destination', type=Path)
    parser.add_argument('--aliases', type=Path, help='Include metadata for special compatibility definitions too')
    args = parser.parse_args()
    rows = [metadata(n) for n in json.loads(args.source.read_text(encoding='utf-8-sig'))]
    compatible = set()
    if args.aliases:
        aliases = json.loads(args.aliases.read_text(encoding='utf-8-sig'))
        compatible = {n['Id'] for entries in aliases.values() for entry in entries for n in entry['LevelToId']}
    rows = [n for n in rows if n['CanBuffOthers'] or n['Id'] in compatible]
    args.destination.write_text(json.dumps(sorted(rows, key=lambda n: n['Id']),
                                          ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
