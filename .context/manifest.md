---
schema_version: "1"
identity:
  service: app                 # placeholder: the product has no name yet (GAP-001 in the knowledge base)
  type: monorepo               # api-service + ui + batch-job + library, in one repository
  owner: cameron
  repo: CameronChambers93/Jiten
  status: active
domains:
  - platform
  - accounts
  - parser
  - library
  - vocabulary
  - sentences
  - media
  - mining
  - anki
  - srs
  - frontend
description: >-
  A web app and API for learning Japanese from media: it parses titles into vocabulary and coverage
  statistics, finds sentences to learn from, turns them into Anki cards with audio and pictures, and
  has a built-in review system.
provides:
  - web-app
  - rest-api
  - api-docs
  - job-dashboard
  - cli
  - parser-library
  - health
consumes:
  - postgresql
  - redis
  - sudachi
  - ffmpeg
  - yt-dlp
  - ankiconnect
  - voicevox
  - translation-providers
  - metadata-providers
  - subtitle-sources
  - object-storage
  - google-sign-in
  - smtp
  - stripe
  - discourse
  - runpod
  - opentelemetry
tags:
  - japanese
  - dotnet
  - nuxt
links:
  knowledge_base: /vault/Tooling/rebrand
  conventions: /vault/Tooling/rebrand/conventions.md
  upstream: https://github.com/Sirush/Jiten
---
# The app

This manifest says what this repository is and what it depends on, for the knowledge base that describes
it. The knowledge base is not in this repository: it is the bundle at `/vault/Tooling/rebrand/` (rules,
decisions, open questions, glossary and technical records), read and checked with
`python3 /vault/Tooling/rebrand/tools/kb.py`. Its `conventions.md` defines the domains listed above.

The repository is a fork of [Jiten](https://github.com/Sirush/Jiten) that is being renamed. Until the
name is chosen, records call it "the app" and `identity.service` is a placeholder.

## Provides

| Name | What | Where |
|---|---|---|
| `web-app` | The Nuxt site (dev: `https://localhost:3000`) | `Jiten.Web/` |
| `rest-api` | The ASP.NET Core API (dev: `https://localhost:7299`); JWT, API key and Google sign-in | `Jiten.Api/` |
| `api-docs` | Swagger at `/swagger` | `Jiten.Api/Program.cs` |
| `job-dashboard` | Hangfire dashboard at `/hangfire` | `Jiten.Api/Program.cs`, `Jiten.Api/Jobs/` |
| `cli` | Batch extraction, parsing, metadata download and dictionary import | `Jiten.Cli/` |
| `parser-library` | Japanese tokenising, deconjugation and dictionary matching, used by the API and the CLI | `Jiten.Parser/` |
| `health` | `/healthz` on the web server | `Jiten.Web/server/routes/healthz.ts` |

## Consumes

| Name | What it is used for | Needed to run? |
|---|---|---|
| `postgresql` | All data (schemas `jiten`, `jmdict`, users) and the Hangfire queue; the PGroonga build in `postgres-pgroonga/` | Yes |
| `redis` | The parser's cache | Yes |
| `sudachi` | Native tokeniser library in `Shared/`, with the SudachiDict file at `DictionaryPath` | Yes |
| `ffmpeg` | Cutting audio, clips and screenshots from video | For media from video |
| `yt-dlp` | YouTube metadata and subtitles | For YouTube titles |
| `ankiconnect` | Creating and updating Anki notes. Called from the user's browser (default `http://127.0.0.1:8765`), not from the API | For mining to Anki |
| `voicevox` | Text-to-speech audio (`VoicevoxUrl`) | Optional |
| `translation-providers` | Sentence translation: Anthropic Claude, Google Gemini | Optional; needs a key |
| `metadata-providers` | Title metadata and covers: AniList, VNDB, TMDB, IGDB, Google Books, a Jikan mirror, Bookmeter, openBD, Syosetu | Per media type; some need keys |
| `subtitle-sources` | Subtitle files: Jimaku, OpenSubtitles, SubDL | Optional; need keys |
| `object-storage` | Bunny CDN for covers and user media; a local folder (`StaticFilesPath`) otherwise | Optional |
| `google-sign-in` | Google sign-in and reCAPTCHA | Optional |
| `smtp` | Account email | For email flows |
| `stripe` | Payments for the paid tier | Optional; to be removed |
| `discourse` | Single sign-on for a forum | Optional |
| `runpod` | Serverless model endpoints | Optional |
| `opentelemetry` | Traces and metrics to an OTLP endpoint | Optional |

Settings for all of these are in `Shared/sharedsettings.example.json`; environment variables override
them. The knowledge base's licences record lists each provider's terms.
