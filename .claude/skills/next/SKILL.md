---
name: next
description: Start the next PLAN.md iteration
disable-model-invocation: true
argument-hint: [iteration-id]
---
1. Read PROGRESS.md. Work on iteration $ARGUMENTS if given, otherwise the first unticked one.
2. Read that iteration's block in PLAN.md and only the docs/design.md sections it lists.
3. If any "Depends on" iteration is unticked, stop and say which.
4. Write a short plan: files to create or change, tests to write first, and how you will
   prove each "Done when" item. Ask blocking questions with AskUserQuestion.
   Wait for approval before editing anything.
5. After approval: tests first, then code. Run /verify. Show evidence for each "Done when" item.
6. If the iteration has 👤 steps, list them and stop until the user confirms them.
