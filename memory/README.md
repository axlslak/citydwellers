# City Dwellers Session Memory

This directory stores durable, conversation-specific development memory so a future AI/coding session can recover historical discussion after the original chat is unavailable.

The repository is public. Therefore recovered conversation memories are stored encrypted; passwords are intentionally NOT committed.

## Recovery procedure for a future agent

When the user says something like:

`Remember conversation #1: AOLite Config JSON Format`

1. Read `memory/MANIFEST.json` and locate the matching conversation entry.
2. Read the referenced `.memory.enc.json` file.
3. Ask for/use the password supplied by the user for that memory file.
4. Decrypt the envelope using the algorithm/parameters stored in the file.
5. Verify the recovered plaintext SHA-256 against `plaintext_sha256` in the envelope.
6. Treat the recovered material as historical conversation context. Current Git/code/test evidence remains authoritative for implementation state.

## File format: citydwellers-session-memory-v1

The encrypted JSON envelope contains:

- `cipher`: AES-256-GCM
- `kdf`: scrypt parameters and salt
- `nonce_b64`
- `aad_utf8`
- `plaintext_sha256`
- `plaintext_bytes`
- `ciphertext_b64`

The AES-GCM ciphertext includes the authentication tag as produced by common AESGCM APIs.

### Python reference decryption

```python
import json, base64, hashlib
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.scrypt import Scrypt

path = "memory/conversations/001-aolite-config-json-format.memory.enc.json"
password = "PASSWORD-SUPPLIED-BY-USER"

env = json.load(open(path, "r", encoding="utf-8"))

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
ciphertext = base64.b64decode(env["ciphertext_b64"])
aad = env["aad_utf8"].encode("utf-8")
plaintext = AESGCM(key).decrypt(nonce, ciphertext, aad)

assert hashlib.sha256(plaintext).hexdigest() == env["plaintext_sha256"]
print(plaintext.decode("utf-8"))
```

## Important

These files are distilled recovery memories, not verbatim transcripts unless an entry explicitly says otherwise. Their provenance/confidence markers should be respected. Do not infer that an old chat's claim of a local commit means the commit exists in Git.