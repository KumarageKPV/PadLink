---
name: verify
description: Run all checks for the parts of the repo that exist. Use before every commit and to prove an iteration's Done-when items.
---
Run in order, skipping steps whose project doesn't exist yet. Show each command and the end of its output.
1. `dotnet build pc/PadLink.sln -warnaserror`
2. `dotnet test pc/PadLink.sln --filter "Category!=RequiresViGEm"`
3. If pc/src/PadLink.Core changed and `python tools/padprobe.py --check-driver` succeeds:
   `dotnet test pc/PadLink.sln --filter "Category=RequiresViGEm"`
4. `python tools/check_vectors.py`
5. In mobile/packages/padlink_protocol: `dart test`
6. In mobile/app: `flutter analyze`, then `flutter test`
Stop at the first failure, fix the root cause, re-run from that step.
Never skip, weaken or delete a test to make it pass.
