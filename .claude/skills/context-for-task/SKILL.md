---
name: context-for-task
description: Build a context brief from the app's knowledge base before starting a task - the invariants that must hold, the decisions already made, open gaps, glossary terms and the technical records to read. Use at the start of any design or code task on the app (the Jiten fork being rebranded), when asked "what do we know about X", or when another skill says to get context first.
argument-hint: "<task in a sentence> [paths the task will touch]"
---

# Context for a task

The knowledge base is the bundle at `/vault/Tooling/rebrand/`. Its rules are in `conventions.md` there.

```bash
K="python3 /vault/Tooling/rebrand/tools/kb.py"
```

1. **Brief.** `$K brief "<task>" --paths <files or globs the task will touch> -d <domain>`. Domains are listed in `conventions.md`; pass the one or two that fit, or leave it out.
2. **Widen once** if the brief is thin: `$K search "<other words for the same thing>"`, and `$K backlinks <record>` on the most relevant record to find its neighbours.
3. **Read** the invariants and decisions in full, and the top two or three technical records. `$K resolve <ID>` prints one with its related references.
4. **Report** to whoever asked, in this shape, and keep it under a screen:

   - **Must hold:** each invariant, with its ID and severity, in one line.
   - **Already decided:** each decision that constrains the task.
   - **Not known:** open gaps in the area, with their `action`.
   - **Read:** the records that describe what the task changes, with paths.
   - **Warnings:** anything stale, unverified, or still a proposal that the task leans on.

## Rules

- **Not found is an answer.** If `resolve` returns `source: ask_user`, or the knowledge base has nothing on something the task depends on, don't fill the hole from memory or from the old notes without saying so. Ask Cameron, and record it: `$K new-gap "<what is missing>" -d <domain> --action ask`.
- **The code wins.** If a record disagrees with the code you are reading, say so in the report and fix the record (or note it for the merge step).
- A proposal is not a rule yet. An unverified record is a lead.
- `/vault/Tooling/jiten/` is the old bundle, kept as history. Don't design from it; if only it has the answer, say so and record the gap.
