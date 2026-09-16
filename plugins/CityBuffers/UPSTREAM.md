# Imported Mali source

Source: https://gitlab.com/Pixelmania/malisbuffbots
Revision: eb78c7f460a66dba6b1c8f8cf6cf66f5b231cc03
Imported for City Dwellers at the owner's request, 2026-09-16.

Original namespace MalisBuffBots, casting engine, models, messages, templates and
nano database are retained. Upstream assembly copyright: Copyright © 2021.
No upstream LICENSE file was present in the inspected revision; this provenance
record does not grant or change upstream rights.

Integration changes: CityBuffers project/output and shared package references;
host lifecycle and logging; named-pipe status bridge; Manager-scheduled tells;
portable asset paths and per-character mutable state; unified behavior overrides;
empty initial rank lists; explicit JSON-load errors; in-play startup guard;
static game-data warmup and shutdown cleanup. Received bans persist per character.
The old shared Log.txt writer now uses the host logger. Whitespace/BOM normalized.

Official clientless source inspected for the loader/API contract:
https://gitlab.com/never-knows-best/aosharp.clientless
Runtime dependencies come from the repository's shared NuGet references, not from
Mali's clientless fork or its standalone TestClient/package configuration.
