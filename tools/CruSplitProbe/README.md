# Real-client split slot probe

Standalone AOSharp **full-game-client** plugin, not a City Dwellers plugin.
Records native inventory slots, template IDs, QL and quantities around one
manual split. It sends no packets and changes no items. Source-reviewed only;
the owner builds and runs it. No private SDK source or binaries are included.

## Build

Open CruSplitProbe.csproj in Visual Studio. Supply your matching AOSharp.Core.dll
and AOSharp.Common.dll in this directory's `lib` folder, or set the MSBuild
property `AOSharpDir` to their directory. Build for .NET Framework 4.8/x86.
Load the resulting CruSplitProbe.dll through your usual AOSharp plugin loader.
This project is deliberately outside the City Dwellers solution.

## Capture

1. Enable the game's System/chat log (the same Funcom log used for N3Inspector).
2. With a consumable stack in normal inventory and at least two free slots,
   type `/splitprobe`. Manually split **one unit**, then leave inventory alone
   until `[SPLITPROBE] END` (three seconds). No banker restart is needed.
3. Type `/splitprobe` again and split one more unit from the original stack.
   Again wait for END; leave both new singles where the game put them.
4. Relog without merging/moving those stacks. Load the probe again and run
   `/splitprobe snap`. Send the log from ARM through this final SNAP.

ARM records the initial inventory. LAST POLL and SEND CALLBACK may already
reflect native changes because network events are dispatched from a queue. AFTER/END reveal the result. Free-slot lists
allow comparison with the first-free-slot hypothesis, and the fresh-login SNAP
checks server persistence. RECV totals are decoded-message counts, not a raw
network capture and not proof that unrecognized packets were absent.

Keep other inventory automation idle during the experiment. The probe accepts
any manually split item; CRU is not required. It does not activate automatic
stacking, offer trades, or install inferred quantities in City Dwellers.
