---
name: feature
description: Take a Jiten fork feature from request to merge - design in the vault with a mockup or flow for review, build in a jiten-session worktree, review, test, before/after screenshots, commit, follow-ups, and the vault update at merge. Use when Cameron asks to build, resume, merge or reject a feature, or runs /feature with a request, a task from the vault's task list, or "resume <name>".
argument-hint: "<request> | task: <text from tasks.md> | resume <session> | merge <session> | reject <session>"
---

# Feature workflow

Runs a fork feature end to end. Three gates stop for Cameron; everything between them runs without asking.

| Gate | When | What Cameron sees |
|---|---|---|
| **Design** | Before any code | The design note, plus a flow, sketch or mockup |
| **Commit** | After tests and screenshots | The summary, the before/after page, departures from the design |
| **Merge** | When he accepts the work | What will merge, migrations to apply |

Ask at a gate with `AskUserQuestion`. Outside the gates, ask only when blocked on a decision that's his.

## Places

| What | Where |
|---|---|
| Vault bundle (designs, tasks) | `/vault/Tooling/jiten/fork/`; read `../conventions.md` before editing |
| Task list | `/vault/Tooling/jiten/fork/tasks.md` |
| Main checkout (branch `n1miner`) | `/workspaces/anki-mining/jiten`, site at https://localhost:3000, API 7299 |
| Feature sessions | `jiten-session new <name>` makes `/worktrees/<name>` on `session/<name>`; `jiten-session ls` shows its ports |
| Tools (persist across sessions) | `/worktrees/.tools/`; `scripts/setup-tools.sh` installs Playwright and `dotnet-ef` there |
| Run notes for a feature | `/worktrees/.run/<name>-feature.md`: departures, seeded data, checks run, links |

Follow `/worktrees/CLAUDE.md` for session ports, logs and the per-session database.

## Vault rules

The vault's `main` is what Cameron reads in Obsidian, so before a merge only proposals go there:

- **Before merge**, edit only the design note (with its line in `fork/index.md`, `log.md` and the vault's `00-Index.md`) and `tasks.md`.
- **Departures from the design** found while building go in the run notes and on the review page, not in the vault.
- **At merge**, write the notes on how things now work, from the merged code; record departures in the design; update index, log and "Recently updated"; remove finished tasks.
- **Rejected** designs get `status: historical` and a line saying why.
- Commit the vault after each change, with a short message. Never push or pull: Obsidian Git on the PC does that.
- Run `python3 /vault/Tooling/jiten/references/okf-check.py` before each vault commit.

## Arguments

- **A request**: start at step 1.
- **`task: <text>`**: find the task in `tasks.md`, quote it back, and start at step 1.
- **`resume <name>`**: read the run notes, `git -C /worktrees/<name> log n1miner..` and `git status`, and the design note; say which step it's at and carry on. Work started by another session counts: read its diff before changing anything.
- **`merge <name>`** and **`reject <name>`**: go to step 9 or 10.

## 1. Intake

Read `fork/index.md`, `tasks.md` and the notes the request touches. Check `jiten-session ls` and `git branch --list 'session/*'` for related work in progress.

Restate the request in two or three sentences: what changes for the user. Ask only questions whose answers change the design. Pick a short session name (lowercase, dashes).

## 2. Design, and the design gate

Extend an existing design note when the request is a step of one; otherwise create `fork/<name>-design.md`:

- Frontmatter per `conventions.md`: `type: Fork Note`, `status: open`, title ending "design", description starting "Proposed design for".
- Sections as needed: workflow, data model, API, UI, build order, out of scope. Name the tables, endpoints, components and pages it changes, with paths.
- Note the decisions Cameron should check, as a short list at the top.

Choose the cheapest review artifact that's accurate enough:

| Change | Artifact | Rough cost |
|---|---|---|
| Backend or process only | A Mermaid flow in the design note, and the list of changes | 1-2k tokens |
| A small UI change | An ASCII sketch in the gate question's `preview` | about 1k |
| A new screen or a large UI change | A screenshot of the current page on main, and a one-file HTML mockup published as an Artifact | 10-15k |

A mockup checks layout and content, not pixels; say so when presenting it. Reuse the app's look: purple accent, PrimeVue-style cards, the real copy.

Commit the vault, then gate: link the note's path and any mockup, list the decisions to check, and offer approve or change. Loop on changes. Nothing is built before approval.

## 3. Build

1. `jiten-session new <name>`, then `jiten-session start <name>`. Work only in `/worktrees/<name>`.
2. Start the run notes file with the design link and the session's URLs.
3. Build to the design and the repo's `CLAUDE.md`. Record every departure in the run notes as you make it, with the reason.
4. Migrations: `/worktrees/.tools/ef/dotnet-ef` with the session's connection string. Apply them to the session database only.
5. After frontend edits, `jiten-session stop <name>` and `start <name>`.

## 4. Review

Invoke the `code-review` skill at `medium` on the session branch's diff against `n1miner`. Check each finding against the code before acting; drop the ones that don't hold up and say why. Fix the rest. A fix that changes behaviour the design specified is a departure: record it.

## 5. Verify

Run, and record in the run notes what passed:

- Backend: the new and touched test classes first, then the full suite (`dotnet test Jiten.Tests`).
- Frontend: `pnpm test`, `npx eslint` on changed files, and `npx vue-tsc --noEmit -p .` filtered to changed files (the repo has existing type errors elsewhere).
- Migrations, when any were added: `has-pending-model-changes` for both contexts, then `database update <previous>` and back up on the session database, checking data moved as intended.

## 6. Screenshots

1. `scripts/setup-tools.sh` (once per machine).
2. Log in on both sites: `node scripts/login.mjs <base> <state.json>`. It reads `JITEN_USER` and `JITEN_PASSWORD` from the environment, else from `/worktrees/.tools/jiten-login.env` (mode 600, outside every repo). If neither has them, ask Cameron and write that file; never put credentials in the repo or vault.
3. Seed the data the screens need on the **session database only**, through the API where possible. Record each seeded row in the run notes.
4. Write a shots spec (see `scripts/shoot.mjs`) and run it against main (`before`) and the session (`after`). Look at every screenshot; fix selectors or the feature and retake if one is wrong.
   - **Anki writes are real, on the `test` profile.** `shoot.mjs` sends the pages' AnkiConnect calls to the host's Anki and refuses every write unless the active profile is `test`. Never fake AnkiConnect to test a write. If Anki is on another profile, ask Cameron to switch. Steps a spec can't express go in a scratch script that does the same routing and profile check.
   - Record the notes added (`shoot.mjs` prints their ids) in the run notes, with the seeded rows.
5. Publish a side-by-side page from `compare-template.html`: a row per screen, a placeholder where main has no such screen, and a "Departures from the design" section from the run notes.

## 7. Commit gate

Remove the seeded data and the notes added in Anki's `test` profile (`deleteNotes`, after checking `getActiveProfile`), and confirm they're gone. Then gate with: what was built, test results, the comparison page link, departures, and anything not verified. On approval, commit on `session/<name>` with the attribution lines from the system reminder.

## 8. Follow-ups

Add follow-ups to `tasks.md` under the feature's heading, checked against the design so agreed behaviour isn't listed as a task. Commit the vault. Tell Cameron the session is ready to merge when he accepts it.

## 9. Merge (`merge <name>`)

1. Merge gate: show `git log n1miner..session/<name>`, the migrations it adds, and what the vault update will change.
2. If the main checkout has uncommitted changes or isn't on `n1miner`, stop and tell him. Otherwise merge there with `git merge --no-ff session/<name>`.
3. Ask before applying migrations to the main `jiten` database, and before restarting main's servers.
4. Vault at merge, per the vault rules: built-behaviour notes from the merged code, departures into the design, the design's `status` (`current` once fully built, `open` while build steps remain), index, log, "Recently updated", finished tasks removed. Check and commit.
5. Ask before `jiten-session rm <name>`.

## 10. Reject (`reject <name>`)

Ask what to keep. Set the design to `historical` with the reason, move any useful parts to `tasks.md`, commit the vault, and ask before `jiten-session rm <name>` and deleting the branch.
