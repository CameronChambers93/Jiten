---
name: context-propose
description: Draft and stage a governance item for the app's knowledge base - an invariant (a rule that must hold), a decision (a choice and its reasons), a glossary term or a persona - or record a gap; and carry out Cameron's review of pending proposals (accept or reject). Use when a design makes a choice that outlives the task, when a rule is discovered or agreed, when Cameron says "record that decision", "make that a rule", "what proposals are pending", or accepts or rejects one.
argument-hint: "<invariant|decision|term|persona|gap> <what> | review | accept <ID> | reject <ID> <reason>"
---

# Proposing to the knowledge base

Bundle: `/vault/Tooling/rebrand/`. Read `conventions.md` there for frontmatter and status.

```bash
K="python3 /vault/Tooling/rebrand/tools/kb.py"
```

## Which kind

| It is | Type | Goes through |
|---|---|---|
| A rule that would be a bug to break | `Invariant` | proposal |
| A choice between alternatives, with reasons | `Decision` | proposal |
| A word that needs one meaning | `Term` | proposal |
| Someone the app is for | `Persona` | proposal |
| Something nobody knows yet | `Gap` | `$K new-gap`, directly |

A fact about how the code works is none of these: it belongs in a technical record, written directly.

## Drafting

1. `$K search "<subject>" --proposals` first. Extend or supersede an existing item instead of adding a near-duplicate; a decision that replaces another sets `supersedes: GDR-NNN`.
2. Write the draft to a scratch file with frontmatter `type`, `title`, `description`, `domains`, and `severity` for an invariant. Leave out `id`, `status`, `verified` and `updated`: the tool sets them.

   **Invariant** body: the rule in one sentence; why it exists; what it applies to; how it is enforced or checked today (a test, a guard, a review step, or "nothing yet"); what breaking it looks like.

   **Decision** body: the decision; why; the alternatives and why they lost; consequences; what would make us revisit it. Mark it "Proposed" in the first line until accepted.

   **Term**: the definition in one or two sentences, what it is not, and the names the code uses for it.

   Link the records it relates to, with relative links written for where the item will live (`governance/<kind>/`).
3. `$K propose <file>`. It assigns the ID and stages the file under `proposals/`.
4. `$K lint`, then commit the vault. Tell Cameron the ID and the one-line description.

## Review

`review`: `$K status` lists what is pending. Show Cameron each proposal as ID, title, the decision or rule in one or two sentences, and your recommendation. Ask with `AskUserQuestion` (accept, reject, change); several proposals can share one call.

- **Accept:** `$K promote <ID>`.
- **Reject:** `$K reject <ID> --reason "<his reason>"`. The item is kept as `historical`, so the question isn't reopened by accident.
- **Change:** edit the file in `proposals/`, and ask again.

Only run `promote` or `reject` on Cameron's explicit answer in this conversation. Never set `verified: true`. Commit the vault after.

## Gaps

`$K new-gap "<title>" -d <domain> --action <ask|mock|skip|block> --body "<what is missing, why it matters, who can answer>"`. When a gap is answered, set its `status: resolved`, say where the answer now lives, and link it.
