# City Dwellers Session Memory

This directory stores durable, conversation-specific development memory so a future AI/coding session can recover historical discussion after the original chat is unavailable.

The repository is public. Therefore recovered conversation memories are stored encrypted; passwords are intentionally NOT committed.

## Recovery procedure for a future agent

When the user says something like:

`Remember conversation #1: AOLite Config JSON Format`

1. Read `memory/MANIFEST.json` and locate the matching conversation entry.
2. Read the referenced `envelope.json`.
3. Read the ciphertext chunk files listed in `ciphertext_parts`, in order, and concatenate their text exactly.
4. Ask for/use the password supplied by the user for that memory file.
5. Derive the AES key using the scrypt parameters stored in the envelope.
6. Base64-decode the concatenated ciphertext and decrypt it using AES-256-GCM, the stored nonce, and the stored AAD.
7. Verify the recovered plaintext SHA-256 against `plaintext_sha256` in the envelope.
8. Treat the recovered material as historical conversation context. Current Git/code/test evidence remains authoritative for implementation state.

## File format: citydwellers-session-memory-v1

The envelope contains:

- `cipher`: AES-256-GCM
- `kdf`: scrypt parameters and salt
- `nonce_b64`
- `aad_utf8`
- `plaintext_sha256`
- `plaintext_bytes`
- ordered `ciphertext_parts`
- `ciphertext_b64_chars`

The AES-GCM ciphertext includes the authentication tag as produced by common AESGCM APIs.

### Python reference decryption

```python
import json, base64, hashlib
from pathlib import Path
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.scrypt import Scrypt

folder = Path("memory/conversations/001-aolite-config-json-format")
password = "PASSWORD-SUPPLIED-BY-USER"

env = json.loads((folder / "envelope.json").read_text(encoding="utf-8"))

ciphertext_b64 = "".join(
    (folder / part).read_text(encoding="utf-8").strip()
    for part in env["ciphertext_parts"]
)
assert len(ciphertext_b64) == env["ciphertext_b64_chars"]

salt = base64.b64decode(env["kdf"]["salt_b64"])
kdf = Scrypt(
    salt=salt,
    length=32,
    n=env["kdf"]["n"],
    r=env["kdf"]["r"],
    p=env["kdf"]["p"],
)
key = kdf.derive(password.encode("utf-8"))
nonce = base64.b64decode(env["nonce_b64"])
ciphertext = base64.b64decode(ciphertext_b64)
aad = env["aad_utf8"].encode("utf-8")
plaintext = AESGCM(key).decrypt(nonce, ciphertext, aad)

assert len(plaintext) == env["plaintext_bytes"]
assert hashlib.sha256(plaintext).hexdigest() == env["plaintext_sha256"]
print(plaintext.decode("utf-8"))
```

## Important

These files are distilled recovery memories, not verbatim transcripts unless an entry explicitly says otherwise. Their provenance/confidence markers should be respected. Do not infer that an old chat's claim of a local commit means the commit exists in Git.
