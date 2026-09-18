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
- `#items 250 strong lead` — use an observed low/high range at QL 250; unpaired templates still require their recorded QL.
- `#items combined --page 2` — 30 templates per page, with Previous/Next buttons.
- `#itemid 257110` or `#items 257110` — exact AOID, link and attribute details.
- `#help items` — in-game instructions.

As with other Manager commands, tells may omit `#`; organization/guest chat uses
it. Existing membership and ban checks remain. Search is available to normal bot
members/guests, not administrator-only. Only one search runs at a time; requests
are bounded to 300 characters and output uses existing byte-aware blob pagination.

Results show template AOIDs, observed QL ranges and clickable item links. Info
shows NoDrop, Unique, Stackable, CantSplit, Splittable and raw Flags/Can masks.
Unknown attributes are explicitly unknown. NoDrop and Unique are not filters:
this is a general item database, not the bankable-item catalogue.

## Observed families and QL ranges

The dump contains endpoint stats but no explicit interpolation pairing table.
The shared family index now supplements it with actual low/high pairs from the
local ledger, administrator-supplied Phatz item links and live donation offers.
Equal names and adjacent IDs never create a relationship. Existing v1 policy
files need no manual migration.

For example, Strong Lead Viralbots has an observed pair 247138/247139. The dump
places those templates at QLs 1 and 300. A physical QL 300 copy may instead carry
247139/247139; the shared endpoint connects both representations to one family.
`#items 250 strong lead` can now build a 247138/247139/250 link, and `#itemid`
shows the known family IDs and endpoint QLs. Observed edges can connect multiple
QL segments; only recorded edges with valid increasing endpoint QLs render ranges.
Unpaired templates remain exact. This is not a complete global interpolation or
obtainability database: an unseen relationship remains unknown until observed.

Sanitized pairs (only two numeric IDs) persist in `data/items-pairs.json`, so
withdrawing the last copy does not erase the relationship on restart. Writers
merge under a path-specific cross-domain/process mutex and replace the file
atomically. Explicit Phatz policy edits also retain `KnownPairs`. Source metadata
changes refresh domain-local family snapshots. The large raw catalogue still
loads once per domain as described below.

## Phatz integration

- `#phatz` and its name/QL filters group stock by observed family, with total
  copies and separate exact-QL/template rows. Physical ledger records retain
  their original IDs, QLs, donors and locations; no copies are merged or removed.
- `#phatz list` shows one effective rule per family. Existing duplicate policy
  rows remain intact until an explicit add/update/remove consolidates that family.
- `#phatz add <linked item> [limit]` replaces duplicate dynamic rules for the
  known family. Adding either known endpoint covers all known aliases.
- `#phatz remove <AOID>` disables the known family, retaining its pair evidence.
- Finite limits count all known family variants for **future incoming donations**,
  including projected copies in the same trade. Existing stock is not trimmed.
  Conflicting legacy caps resolve to unlimited until an admin adds the family
  with one explicit limit. Identical caps are applied once across the family.
- Explicit non-Phatz routes/rejections retain precedence over inherited aliases;
  a direct dynamic rule retains its pre-existing override semantics.
- Retention/capacity totals count family rules once; route/index enumeration
  still includes each alias. GET keeps the existing AOID-based selection and
  cannot promise a particular QL when several copies share that AOID.

The owner's supplied snapshot resolves 45 policy rows and 53 exact stock
variants to 39 families, preserving all 129 physical Phatz copies. No owner
attachments are embedded, edited or published by this change.

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
// catalog.Search(query, optionalRecordedQl, page, pageSize, out total) keeps exact semantics.
// catalog.SearchFamilies(query, requestedQl, familyIndex, page, pageSize, out total) uses observed pairs.
// ItemFamilyIndex.Key / Members / PairsFor expose the relationship without changing physical IDs.
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

Validation: compared supplied policy/ledger pair evidence, reviewed family routing,
future retention counts, persistence and exact physical identity boundaries;
inspected the complete supplied dump for schema, unique AOIDs and
missing retained stats; static source, integration, project XML and whitespace
review. No assistant build, test suite or live AO run; the owner builds/tests.
