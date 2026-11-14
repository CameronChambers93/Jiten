---
name: feature
description: Take a Jiten fork feature from request to merge - context brief from the knowledge base, design in the vault with a mockup or flow for review, build in a jiten-session worktree, review, test, before/after screenshots, a knowledge-base check, commit, follow-ups, and the record updates at merge. Use when Cameron asks to build, resume, merge or reject a feature, or runs /feature with a request, a task from the vault's task list, or "resume <name>".
argument-hint: "<request> | task: <text from tasks.md> | resume <session> | merge <session> | reject <session>"
---

# Feature workflow

Runs a fork feature end to end. Three gates stop for Cameron; everything between them runs without asking.

| Gate | When | What Cameron sees |
|---|---|---|
| **Design** | Before any code | The design note, the invariants it touches and the decisions it proposes, plus a flow, sketch or mockup |
| **Commit** | After tests and screenshots | The summary, the before/after page (desktop, and phone where it applies), departures from the design, the records the change makes stale |
| **Merge** | When he accepts the work | What will merge, migrations to apply, the records to update and the proposals to accept |

Ask at a gate with `AskUserQuestion`. Outside the gates, ask only when blocked on a decision that's his.

## Places

| What | Where |
|---|---|
| Knowledge base | `/vault/Tooling/rebrand/`; read its `conventions.md` before editing. Harness: `K="python3 /vault/Tooling/rebrand/tools/kb.py"` |
| Designs | `/vault/Tooling/rebrand/technical/designs/<name>-design.md` |
| Task list | `/vault/Tooling/rebrand/technical/tasks.md` |
| Old notes (history only) | `/vault/Tooling/jiten/`; designs written before November 2026 are in its `fork/` folder |
| Main checkout (branch `n1miner`) | `/workspaces/anki-mining/jiten`, site at https://localhost:3000, API 7299 |
| Feature sessions | `jiten-session new <name>` makes `/worktrees/<name>` on `session/<name>`; `jiten-session ls` shows its ports |
| Tools (persist across sessions) | `/worktrees/.tools/`; `scripts/setup-tools.sh` installs Playwright and `dotnet-ef` there |
| Android emulator (phone shots) | On the host; adb via `ADB_SERVER_SOCKET=tcp:host.docker.internal:5037`. Vault note `Tooling/anki-mining/android-emulator-testing.md` |
| Run notes for a feature | `/worktrees/.run/<name>-feature.md`: departures, seeded data, checks run, links |

Follow `/worktrees/CLAUDE.md` for session ports, logs and the per-session database.

## Knowledge-base rules

The vault's `main` is what Cameron reads in Obsidian, and the records there are what the next task is designed from. So before a merge, only things marked as not yet true go in:

- **Before merge**, write only the design (a record with `status: open`), the task list, gaps, and proposals staged with `$K propose`. A proposal binds nothing until Cameron accepts it.
- **Departures from the design** found while building go in the run notes and on the review page, not in the vault.
- **At merge**, update the records that `$K drift` names, from the merged code; write a record for new behaviour none covers; record departures in the design; resolve the gaps the work answered; remove finished tasks; add a line to `log.md`.
- **Invariants and decisions** change only through proposals. Run `$K promote <ID>` or `$K reject <ID> --reason "..."` only on Cameron's answer in the conversation. Never set `verified: true`.
- **Rejected** designs get `status: historical` and a line saying why.
- Run `$K index && $K lint` before each vault commit and fix what lint reports. Commit with a short message. Never push or pull: Obsidian Git on the PC does that.

## Arguments

- **A request**: start at step 1.
- **`task: <text>`**: find the task in `tasks.md`, quote it back, and start at step 1.
- **`resume <name>`**: read the run notes, `git -C /worktrees/<name> log n1miner..` and `git status`, and the design note; say which step it's at and carry on. Work started by another session counts: read its diff before changing anything.
- **`merge <name>`** and **`reject <name>`**: go to step 9 or 10.

## 1. Intake

Run the `context-for-task` skill with the request and the paths it is likely to touch. Its brief gives the invariants that must hold, the decisions already made, the open gaps and the records to read; read those records and the task list. A gap with action `block` in the area stops here until Cameron answers it. Check `jiten-session ls` and `git branch --list 'session/*'` for related work in progress.

Restate the request in two or three sentences: what changes for the user. List the invariants the brief returned, by ID. Ask only questions whose answers change the design. Pick a short session name (lowercase, dashes).

## 2. Design, and the design gate

Extend an existing design when the request is a step of one; otherwise create `technical/designs/<name>-design.md`:

- Frontmatter per `conventions.md`: `type: Feature` (or `Workflow`), `status: open`, title ending "design", description starting "Proposed design for", the `domains`, and `paths` for the code it will change.
- Sections as needed: workflow, data model, API, UI, build order, out of scope. Name the tables, endpoints, components and pages it changes, with paths.
- **Invariants touched:** each invariant from the brief that bears on the design, by ID, with how the design keeps it. A design that can't keep one says so at the top: Cameron either changes the design or accepts a decision that supersedes the rule.
- **Decisions:** a choice that outlives this feature (one with a real alternative that a later task could undo by accident) is drafted and staged with the `context-propose` skill, and linked from the design. A new rule the design depends on is staged as an invariant the same way. Smaller choices stay as a short list at the top for Cameron to check.
- Something the design needs and nobody knows is a gap: `$K new-gap "..." -d <domain> --action ask`.

Choose the cheapest review artifact that's accurate enough:

| Change | Artifact | Rough cost |
|---|---|---|
| Backend or process only | A Mermaid flow in the design note, and the list of changes | 1-2k tokens |
| A small UI change | An ASCII sketch in the gate question's `preview` | about 1k |
| A new screen or a large UI change | A screenshot of the current page on main, and a one-file HTML mockup published as an Artifact | 10-15k |

A mockup checks layout and content, not pixels; say so when presenting it. Reuse the app's look: purple accent, PrimeVue-style cards, the real copy.

Commit the vault, then gate: link the design's path and any mockup, list the invariants touched, the proposals staged and the decisions to check, and offer approve or change. Approving the design does not accept its proposals; ask about each, and promote or reject on his answer. Loop on changes. Nothing is built before approval.

## 3. Build

1. `jiten-session new <name>`, then `jiten-session start <name>`. Work only in `/worktrees/<name>`.
2. Start the run notes file with the design link and the session's URLs.
3. Build to the design, the invariants it lists and the repo's `CLAUDE.md`. Record every departure in the run notes as you make it, with the reason.
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
2. Logging in is automatic: `shoot.mjs` logs in to the site first and saves the state to the `<state.json>` path it's given (`scripts/login.mjs` does the same on its own). The credentials are in `/worktrees/.tools/jiten-login.env` (mode 600, outside every repo; `JITEN_USER`/`JITEN_PASSWORD` in the environment override it). If a login fails, check that file with Cameron; never put credentials in the repo or vault.
3. Seed the data the screens need on the **session database only**, through the API where possible. Record each seeded row in the run notes.
4. Write a shots spec (see `scripts/shoot.mjs`) and run it against main (`before`) and the session (`after`). Look at every screenshot; fix selectors or the feature and retake if one is wrong.
   - **Anki writes are real, on the `test` profile.** `shoot.mjs` sends the pages' AnkiConnect calls to the host's Anki and refuses every write unless the active profile is `test`. Never fake AnkiConnect to test a write. If Anki is on another profile, ask Cameron to switch. Steps a spec can't express go in a scratch script that does the same routing and profile check.
   - Record the notes added (`shoot.mjs` prints their ids) in the run notes, with the seeded rows.
5. **Phone shots**, when the feature changes a page Cameron uses on his phone (mining, review, reading, anything with a mobile layout): run the same spec with `--phone` on both sites. It drives real Chrome on the host's Android emulator (AVD `lumix_s23plus`) through `scripts/phone.mjs`, with the same login state and the same AnkiConnect rules, and writes `<name>-<tag>-phone.png`.
   - The emulator must be running on the host with Chrome open; if `adb devices` (with `ADB_SERVER_SOCKET=tcp:host.docker.internal:5037`) is empty, ask Cameron to start it. `jiten_dns.py` isn't needed: the script maps the ports with `adb reverse` and sends `jiten.local` API calls to the API port.
   - Give each `path` shot a `ready` selector: the phone hydrates a dev page in about 5-10 seconds. `fullPage` isn't available on the phone; take several viewport or `element` shots. Mark shots that only make sense on one device with `"device": "phone"` or `"desktop"`.
6. Publish a side-by-side page from `compare-template.html`: a row per screen, a placeholder where main has no such screen, phone rows (class `pair phone`) after the desktop ones, and a "Departures from the design" section from the run notes.

## 7. Knowledge-base check and commit gate

Run the `context-check` skill with `drift n1miner /worktrees/<name>`. Put in the run notes: the records the change makes stale, with the claim and its correction for each; changed files no record covers; and whether the change keeps each invariant that bears on it. A broken invariant stops the gate until Cameron decides. Don't edit the records yet; that happens at merge.

Remove the seeded data and the notes added in Anki's `test` profile (`deleteNotes`, after checking `getActiveProfile`), and confirm they're gone. Then gate with: what was built, test results, the comparison page link, departures, the records to update at merge, and anything not verified. On approval, commit on `session/<name>` with the attribution lines from the system reminder.

## 8. Follow-ups

Add follow-ups to the task list under the feature's heading, checked against the design so agreed behaviour isn't listed as a task. Commit the vault. Tell Cameron the session is ready to merge when he accepts it.

## 9. Merge (`merge <name>`)

1. Merge gate: show `git log n1miner..session/<name>`, the migrations it adds, the records the merge will update (from the run notes), and the proposals still pending.
2. If the main checkout has uncommitted changes or isn't on `n1miner`, stop and tell him. Otherwise merge there with `git merge --no-ff session/<name>`.
3. Ask before applying migrations to the main `jiten` database, and before restarting main's servers.
4. Records at merge, per the knowledge-base rules. Run `$K drift --repo /workspaces/anki-mining/jiten --base <the commit before the merge>` again, on the merged code. For each record it names, correct the claims the change made untrue, set `updated`, and set `verified: false` if a verified claim changed. Write a record for new behaviour none covers. Then: departures into the design; the design's `status` (`historical` once fully built, with a line linking the records that now describe it; `open` while build steps remain); gaps answered; finished tasks removed; a line in `log.md`. `$K index && $K lint`, and commit.
5. Ask before `jiten-session rm <name>`.

## 10. Reject (`reject <name>`)

Ask what to keep. Set the design to `historical` with the reason, reject its pending proposals with the same reason on his word, move any useful parts to the task list, commit the vault, and ask before `jiten-session rm <name>` and deleting the branch.
