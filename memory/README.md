# City Dwellers Session Memory

This directory stores durable, conversation-specific development memory so a future AI/coding session can recover historical discussion after the original chat is unavailable.

The repository is public. Conversation memories are therefore encrypted; passwords are intentionally NOT committed.

## Recovery procedure

When the user refers to an old conversation by number/title:

1. Read `memory/MANIFEST.json` and locate the entry.
2. Read its `envelope.json`.
3. Read `ciphertext_parts` in order and concatenate their text exactly.
4. Use the password supplied by the user.
5. Derive a 32-byte key using the scrypt parameters in the envelope.
6. Decode the concatenated ciphertext according to `ciphertext_encoding`:
   - missing / `base64`: Base64-decode it (v1 memories #1/#2).
   - `hex`: `bytes.fromhex(...)` (v2 memory #3 and later when used).
7. AES-256-GCM decrypt with the stored nonce and AAD.
8. If `compression` is `gzip`, gzip-decompress the decrypted bytes.
9. Verify `plaintext_bytes` and `plaintext_sha256`.
10. Use the result as historical context. Current Git/code/test evidence remains authoritative.

## Python reference decryption

```python
import json, base64, hashlib, gzip
from pathlib import Path
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.scrypt import Scrypt

folder = Path("memory/conversations/003-session-3-recovery-bootstrap")
password = "PASSWORD-SUPPLIED-BY-USER"
env = json.loads((folder / "envelope.json").read_text(encoding="utf-8"))
encoded = "".join((folder / p).read_text(encoding="utf-8").strip() for p in env["ciphertext_parts"])

salt = base64.b64decode(env["kdf"]["salt_b64"])
kdf = Scrypt(salt=salt, length=32, n=env["kdf"]["n"], r=env["kdf"]["r"], p=env["kdf"]["p"])
key = kdf.derive(password.encode("utf-8"))
nonce = base64.b64decode(env["nonce_b64"])
aad = env["aad_utf8"].encode("utf-8")

if env.get("ciphertext_encoding", "base64") == "hex":
    ciphertext = bytes.fromhex(encoded)
else:
    ciphertext = base64.b64decode(encoded)

payload = AESGCM(key).decrypt(nonce, ciphertext, aad)
if env.get("compression") == "gzip":
    payload = gzip.decompress(payload)

assert len(payload) == env["plaintext_bytes"]
assert hashlib.sha256(payload).hexdigest() == env["plaintext_sha256"]
print(payload.decode("utf-8"))
```

## Formats currently present

- `citydwellers-session-memory-v1`: AES-256-GCM + scrypt, Base64 ciphertext chunks, no compression. Used by Conversations #1 and #2.
- `citydwellers-session-memory-v2`: optional gzip before AES-256-GCM + scrypt; encoding declared by the envelope. Conversation #3 uses gzip + hex ciphertext chunks.

## Important

These are distilled recovery memories, not verbatim transcripts unless explicitly stated. Respect provenance/confidence markers. A local commit mentioned in an old chat is not real repository history until Git confirms it.
