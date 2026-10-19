---
name: finish
description: Close out the current PLAN.md iteration
disable-model-invocation: true
---
1. Run /verify.
2. Ask the reviewer subagent to review the uncommitted diff for this iteration.
3. Fix findings that break a Done-when item, the design, or security. List the rest as notes.
4. Tick the iteration in PROGRESS.md with today's date and a one-line summary.
   For a Gate iteration, write the evidence to docs/gates/<milestone>.md.
5. Commit with the iteration's commit message.
