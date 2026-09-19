#!/usr/bin/env python3
"""Read-only inspection of private bag-origin-v1 captures; requires only Python.

Usage: python tools/inspect-bag-origin.py /path/to/bag-origin-*-login.json ...
Offsets follow AOtomation Header/N3Message/FullCharacterMessage/BankMessage.
This verifies record framing and compares fields independently of the runtime
serializer. It does not prove physical item uniqueness or server semantics.
Never commit the input captures or their private output.
"""
import base64
import collections
import json
from pathlib import Path
import struct
import sys

RECORD = struct.Struct('>iHHiiiiii')
TYPES = {'FullCharacterMessage': (0x29304349, 33),
         'BankMessage': (0x343C287F, 29)}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def inspect(path):
    document = json.loads(path.read_text(encoding='utf-8-sig'))
    require(document.get('format') == 'citybankers-bag-origin-v1', 'Unknown format')
    for observation in document['observations']:
        kind = observation['MessageType']
        if kind not in TYPES:
            continue
        data = base64.b64decode(observation['PacketBase64'], validate=True)
        message_type, count_offset = TYPES[kind]
        require(len(data) >= count_offset + 4, 'Truncated header')
        require(struct.unpack_from('>H', data, 2)[0] == 10, 'Not an N3 packet')
        require(struct.unpack_from('>H', data, 6)[0] == len(data), 'Packet size mismatch')
        require(struct.unpack_from('>I', data, 16)[0] == message_type, 'Wrong N3 type')
        encoded_count = struct.unpack_from('>I', data, count_offset)[0]
        require(encoded_count % 1009 == 0 and encoded_count >= 1009, 'Invalid X3F1 count')
        count = encoded_count // 1009 - 1
        start = count_offset + 4
        end = start + count * RECORD.size
        require(end <= len(data), 'Truncated slot array')
        if kind == 'BankMessage':
            require(end == len(data), 'Unexpected trailing bank bytes')
        incoming = observation['Incoming']
        decoded = incoming if isinstance(incoming, list) else incoming['slots']
        require(count == len(decoded), 'Decoded count mismatch')
        groups = collections.defaultdict(list)
        bags = []
        for i, saved in enumerate(decoded):
            row = RECORD.unpack_from(data, start + i * RECORD.size)
            slot, flags, quantity, identity_type, instance, low, high, ql, unknown = row
            require((slot, quantity, low, high, ql) ==
                    (saved['slot'], saved['count'], saved['lowId'], saved['highId'], saved['ql']),
                    'Decoded fields differ at record ' + str(i))
            # Compare the numeric identity instance without depending on the SDK's
            # enum-name formatting. Check Container's known numeric type separately.
            encoded_instance = saved['identity'].rsplit(':', 1)[1].rstrip(')')
            require(int(encoded_instance, 16) == instance & 0xffffffff, 'Identity instance mismatch')
            if identity_type == 51017:
                require(saved['identity'].startswith('(Container:'), 'Container type mismatch')
                detail = dict(slot=slot, flags=flags, count=quantity, low=low,
                              high=high, ql=ql, unknown=unknown)
                groups[instance].append(detail)
                bags.append(detail)
        print(json.dumps(dict(file=path.name, generation=document['generation'],
            message=kind, records=count, start=start, end=end, packet_bytes=len(data),
            container_records=len(bags),
            duplicates={format(k & 0xffffffff, 'X'): v for k, v in groups.items() if len(v) > 1},
            container_flags=dict(collections.Counter(r['flags'] for r in bags))), indent=2))


if __name__ == '__main__':
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    for argument in sys.argv[1:]:
        inspect(Path(argument))
