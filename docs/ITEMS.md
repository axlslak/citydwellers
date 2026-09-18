# Local items catalogue

Extract `items.json` from the owner's tinkerparser `items.zip` and place it in
`data/items.json` beside the runtime's other data files. Keep the JSON unchanged.
Do not copy the ZIP there and do not commit the dump. The supplied dump contains
120,842 unique AOIDs in 445,001,349 bytes.

The Manager loads in the background. Until ready, requests report loading or a
useful error. Missing/invalid input is retried on demand after 30 seconds. After
replacing a successfully loaded source, restart the runtime. No external item
service, database server, generated embedded catalogue or credentials are used.

## Commands

- `#items combined commando` — all words must occur, case-insensitively.
- `#i combined -headwear` — `i` is an alias; minus excludes a word.
- `#items 300 combined` — restrict to recorded template QL 300.
- `#items combined --page 2` — 30 templates per page, with Previous/Next buttons.
- `#itemid 257110` or `#items 257110` — exact AOID, link and attribute details.
- `#help items` — in-game instructions.

As with other Manager commands, tells may omit `#`; organization/guest chat uses
it. Existing membership and ban checks remain. Search is available to normal bot
members/guests, not administrator-only. Only one search runs at a time; requests
are bounded to 300 characters and output uses existing byte-aware blob pagination.

Results show exact template AOIDs, recorded QLs and clickable item links. Info
shows NoDrop, Unique, Stackable, CantSplit, Splittable and raw Flags/Can masks.
Unknown attributes are explicitly unknown. NoDrop and Unique are not filters:
this is a general item database, not the bankable-item catalogue.

## Source limits

The raw dump does **not** supply Nadybot-style low/high interpolation pairs or an
obtainable/in-game catalogue. Matching names and adjacent IDs are not proof of a
family (the supplied dump includes duplicate named endpoint variants). Thus this
version shows separate templates and uses `itemref://AOID/AOID/recordedQL`.
An explicit QL searches recorded endpoints, not inferred ranges. It does not yet
reproduce Nadybot's arbitrary intermediate-QL links or in-game-only filtering.
These features need an authoritative pairing/availability source. Nothing is
silently dropped as a presumed GM/test item.

## Shared API

`shared/ItemCatalog.cs` is linked into the projects by `Directory.Build.props`.
It follows the repository's shared-source/AppDomain model; each consuming domain
has its own immutable snapshot. Public entry points allow future plugin use:

```csharp
ItemCatalog.StartLoading();            // schedules work; does not block game ticks
var catalog = ItemCatalog.Current;      // null while unavailable/loading
var definition = catalog?.Find(257110);
bool? stackable = definition?.Stackable;
bool? splittable = definition?.Splittable;
// catalog.Search(query, optionalRecordedQl, page, pageSize, out total)
```

`ItemDefinition` exposes AOID, Name, Quality, nullable raw Flags/Can and nullable
boolean interpretations. NoDrop = Flags bit 26; Unique = Flags bit 27;
Stackable = Can bit 9; CantSplit = Can bit 24. Splittable means Stackable and
not CantSplit. It describes a template capability, not proof that a particular
live stack contains enough units or that a server operation succeeded.

Stat numbers are Flags 0, Can 30, Level/QL 54. Signed dump masks are preserved as
32-bit bit patterns. Missing stats remain unknown: the supplied dump has one
missing Can and three missing QLs. Other large sections are skipped while reading;
there is no whole-document JSON object or string in memory.

Bit definitions were cross-checked against AOSharp `CanFlags`/`Stat` and
[Nadybot Flag.php](https://github.com/Nadybot/Nadybot/blob/master/src/Modules/ITEMS_MODULE/Flag.php).
The command conventions were checked against
[Nadybot ItemsController](https://github.com/Nadybot/Nadybot/blob/master/src/Modules/ITEMS_MODULE/ItemsController.php).
All per-item values come from the owner's dump.

## Cache and inventory

The first load writes `data/items.json.index-v1.bin`, containing only the retained
fields. A process-wide mutex serializes cache creation across plugin domains.
Later domains/startups read the compact cache, keyed by schema, source length and
last-write UTC ticks. Truncated/invalid caches rebuild; an unwritable cache does
not prevent loading. Delete the derived cache to force a rebuild (especially if
replacing a source while preserving both size and timestamp). The JSON remains
the authoritative input. Successfully loaded snapshots stay fixed until restart.

Inventory's formerly unknown Stackable attribute now consults the catalogue.
Both low/high endpoints must be known and agree, otherwise the result stays
unknown. Known nonstackables no longer display incidental positive wire counts;
unknown templates retain the observed-count fallback. CRU admission, quantities,
stacking, splitting and movement policy are unchanged. No catalogue lookup
triggers an item movement.

Validation: inspected the complete supplied dump for schema, unique AOIDs and
missing retained stats; static source, integration, project XML and whitespace
review. No assistant build, test suite or live AO run; the owner builds/tests.
