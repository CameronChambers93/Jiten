---
name: context-check
description: Check the app's knowledge base - lint it against its conventions, find records that a code change has made stale (drift), list pending proposals and open gaps, and score it against the context-collab benchmarks. Read-only unless asked to fix. Use before a commit or merge gate, after changing records, when asked whether the docs are up to date, or for a periodic health check.
argument-hint: "[lint | drift [base ref] [repo path] | score | all] [fix]"
---

# Checking the knowledge base

Bundle: `/vault/Tooling/rebrand/`.

```bash
K="python3 /vault/Tooling/rebrand/tools/kb.py"
```

Default (`all`): lint, drift, status. Report; change nothing unless the argument includes `fix` or the calling skill says to.

## Lint

`$K lint`. Errors must be fixed before a vault commit. Warnings about indexes are fixed by `$K index`.

## Drift

`$K drift --repo <checkout> --base <ref>`: `--repo` is the checkout being worked on (a `/worktrees/<name>` session, or the main one), `--base` the branch it will merge into (default `n1miner`).

It prints two lists:

1. **Records to re-check:** their `paths` match changed files. Open each beside the diff of those files. For each, say one of: still true; needs this change (quote the claim and the correction); can't tell. With `fix`, or at a merge step, make the corrections, set `updated`, and set `verified: false` if a verified claim changed.
2. **Changed files no record covers:** group them by area. New behaviour that a user or a later task would need to know about is a coverage gap: name the record that should exist. Tests, migrations' designer files and generated code need nothing.

Also check the change against the invariants that bear on it: `$K brief "<what the change does>" --paths <changed files> -d <domain>` lists them. Say for each whether the change keeps it. A change that breaks an invariant stops the gate until Cameron decides: fix the code, or propose a decision that supersedes the rule.

A record with no `paths` can't drift; if the change is in an area whose records lack them, say so instead of reporting "no drift".

## Status

`$K status`: pending proposals, open gaps with action `ask` or `block`, the review queue size, stale records. A `block` gap in the area being changed stops the gate.

## Score

`$K score` for the mechanical criteria, `$K score --record <ref>` for one record. The judged criteria (structure, relationship context, coverage, query power, maintainability) need the `rebrand-verifier` agent with the benchmark documents in `/vault/context-collab/`. Targets: 3.5 minimum viable (records 55%, collection 50%), then 3.5 mature (80%, 78%).

## Report

Short: errors, then drift by record with the proposed correction, then uncovered areas, then anything blocking. Give file paths. Don't paste tool output wholesale.
