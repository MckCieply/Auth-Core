# Project Site Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0018
- **Date:** 2026-11-26
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0018-project-site.md`](../specs/0018-project-site.md) - the plan argues from the spec; where they
  disagree, **the spec wins** and the disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)). The choices the spec leaves
  open are in "Decisions taken in this plan" at the end.
- **Based on:** `main` at `597cc2f` plus the spec commit `0d592eb`. Every anchor below was checked on that tree on 2026-11-26.
- **Acceptance map:** [`0018-acceptance-map.md`](0018-acceptance-map.md).
- **Branch:** docs/0018-project-site, the branch of the spec. Every task commits on it; no PR. After Task 14 and the owner's yes, Task 15
  merges it into `main` locally with `git merge --no-ff`.
- **Spec/code mismatches found while writing the plan (for reviewers; the plan follows the code):**
  1. `withastro/action` v6.1.4 runs `npm install` (not `npm ci`) before `npm run build`. With exact pins and the committed lockfile it
     installs the locked tree; the local `npm ci && npm run build` of the Goal stays the reference build.
  2. `starlight-links-validator` checks the links of Markdown pages only, not the links that components write (the footer, the hero, the
     bento, the Versions list). The plan adds a `postbuild` step, site/scripts/check-links.mjs, that checks every `href` and `src` of the
     built HTML that stays on the site.
  3. Rule 3 of `scripts/check-pins.py` scans `.md` files only (`is_document`, line 64-65); the spec says it "already scans every Markdown
     file". The site's pages are `.md` and `.mdx`, so Task 2 extends `is_document` to `.mdx`.
  4. `README.md` line 229 reads "the specs (0001 to 0009) and the plans and acceptance maps built from them"; the spec quotes it as "the
     specs (0001 to 0009)" becoming "the specs and the plans built from them". The plan removes only "(0001 to 0009) " and keeps "and
     acceptance maps".
  5. `docs/operations/releases.md` line 5 makes the release after the annotated tag is pushed, so the tag is on GitHub before the spec's
     step "Push `main` with the tag". The plan adds the spec's sentence as written; pushing a tag that is already pushed changes nothing.
  6. The root `LICENSE` (line 9) named only `clients/`, `samples/` and `docs/` as outside the Licensed Work, so `site/` fell under BSL
     1.1 there, while the spec makes `site/LICENSE` MIT. Owner decision 2026-11-26 (spec Decision 11): the site is MIT; Task 3 adds
     `site/` to line 9 of `LICENSE` and to line 239 of `README.md`.
  7. `1480843669328-3f7e37d196ae` is the id of the file on images.unsplash.com, not the id of the photo's page on unsplash.com; the author
     and the page are not known yet (spec "To verify" 4). Task 11 establishes them or stops.
  8. `CHANGELOG.md` has no `## Unreleased` heading today; the skip of non-version headings is guarded by a self-test case only.

## Goal

A developer who hears of Auth-Core decides in five minutes whether it fits their product, in English or Polish: five pages (Start, Is it
for me, Comparison, License, Versions) at `https://mckcieply.github.io/Auth-Core/` and `/pl/`, rebuilt by a workflow on every push to
`main` that touches the site, the changelog or its parser. Met when, after the merge and the one-time Pages setting:

```
curl -sI https://mckcieply.github.io/Auth-Core/            # -> 200, the Start page in English
curl -sI https://mckcieply.github.io/Auth-Core/pl/         # -> 200, the Start page in Polish
curl -s  https://mckcieply.github.io/Auth-Core/versions/ | grep -o 'releases/tag/v' | wc -l   # -> 6 (0.4.0 ... 0.1.0)
cd site && npm ci && npm run build                          # -> exit 0 locally, the same checks as the workflow
```

## Architecture

`scripts/release-notes.py --all-json` turns every changelog section into JSON. The site in `site/` is Astro Starlight: a `prebuild` script
runs the parser's self-test, writes site/src/data/versions.json, and checks the version summaries and the page parity of English and
Polish; `astro build` runs `starlight-links-validator`; a `postbuild` script checks the links the components write. Pure functions
(dates, the Upgrading cut, paths) live in `site/src/lib/` and are shared by the Astro components and the `node --test` file. A workflow
builds the same way with `withastro/action` and publishes with `actions/deploy-pages`; `scripts/check-pins.py` gains rules for the
workflow and for the site's package versions.

## Tech Stack

Python 3.12 (standard library) for the parser and the checks; Node 24.19.0 and npm 11.17.0; Astro 7.4.13 with `@astrojs/starlight`
0.43.2 and `starlight-links-validator` 0.27.1; `marked` 18.3.0; `@fontsource/space-grotesk` 5.3.0 and `@fontsource/ibm-plex-mono` 5.3.0;
`sharp` (already a dependency of Astro) for the two photos, run once by hand; GitHub Actions: `actions/checkout` v7.0.1,
`withastro/action` v6.1.4, `actions/deploy-pages` v5.0.1.

## Global Constraints

Values copied from the spec, one line each; every task includes them.

- **Folder:** "Node 24, npm, exact versions in site/package.json (no `^` or `~`), site/package-lock.json committed. The same rules as
  `samples/notes-web`."
- **Pins (latest stable on 2026-11-26, recorded here as the spec asks):** `astro` 7.4.13, `@astrojs/starlight` 0.43.2,
  `starlight-links-validator` 0.27.1, `marked` 18.3.0, `@fontsource/space-grotesk` 5.3.0, `@fontsource/ibm-plex-mono` 5.3.0;
  `actions/checkout` v7.0.1 `3d3c42e5aac5ba805825da76410c181273ba90b1`, `withastro/action` v6.1.4
  `a8b97df764791eb61fbf3338ef713b0e61ae05c5`, `actions/deploy-pages` v5.0.1 `368f82528645a54fb793d4d04e342629a3f51346`.
- **Fonts:** "imported through each package's default `index.css` (or its weight files that include `latin-ext`), so Polish letters
  (ą, ć, ę, ł, ń, ó, ś, ź, ż) use the font and not a fallback." The plan imports the weight files 400/500/700 (Space Grotesk) and
  400/500 (IBM Plex Mono), which carry `latin-ext`.
- **Config:** "`site: 'https://mckcieply.github.io'`, `base: '/Auth-Core'`, Starlight `defaultLocale: 'root'`, `locales: { root: { label:
  'English', lang: 'en' }, pl: { label: 'Polski', lang: 'pl' } }`, `editLink.baseUrl: 'https://github.com/MckCieply/Auth-Core/edit/main/site/'`,
  a social link to the repository, Starlight's search (Pagefind, local) and sitemap on."
- **Content:** "English in `site/src/content/docs/`, Polish in `site/src/content/docs/pl/`." Generated data: site/src/data/versions.json.
- **site/.gitignore:** "`node_modules/`, `dist/`, `.astro/`, src/data/versions.json" - check-pins.py and check-docs.py read the files git
  knows, untracked ones included, so an unignored `node_modules` would be scanned.
- **Links:** "Internal links in pages and components go through the `base` path ... so `/fit/` is served as `/Auth-Core/fit/`." In Markdown
  the plan writes them with the base and a title, `[Comparison](/Auth-Core/compare/ "Comparison")`; in components through
  site/src/lib/paths.mjs.
- **Scripts of site/package.json:** "`predev`: `node scripts/prebuild.mjs`; `dev`: `astro dev`; `prebuild`: `node --test scripts/*.test.mjs
  && node scripts/prebuild.mjs`; `build`: `astro build`; `preview`: `astro preview`." Plus `postbuild` (mismatch 2).
- **`site/LICENSE`:** "MIT, for the site's code and text. The photos and fonts keep their own licences."
- **General rule 1:** "One source per fact. A product fact (what it does, price, status, licence terms, what it does not do) is stated on
  the site only if the README at the build commit states it. The one other source is the threat model's list of residual risks, for the
  'Known limits' of the Is it for me page. Versions and dates come from `CHANGELOG.md` through `--all-json`, never typed by hand. A
  competitor fact carries a link to its source and the date it was checked."
- **General rule 2:** "Every English page has a Polish page at the same path under `pl/`, and the other way round. The Polish text is a
  translation of the English one, not a different page."
- **General rule 3:** "Fonts and photos are files of the site. The built HTML loads no script, style sheet, font or image from another
  host. Links to other hosts are ordinary links."
- **General rule 4:** "No cookie, no storage of personal data. Starlight's theme choice in `localStorage` is the only stored value."
- **General rule 5:** "A page names an endpoint, setting or file only as the README or the changelog names it."
- **Pages:** Start `/` and `/pl/` (splash, own components); Is it for me `/fit/`, `/pl/fit/`; Comparison `/compare/`, `/pl/compare/`;
  License `/license/`, `/pl/license/`; Versions `/versions/`, `/pl/versions/` (own component). "The sidebar holds the four inner pages in
  this order. The header holds the language picker, the theme picker (light, dark, auto; auto is the default) and the GitHub link."
- **Copy:** the Start page copy is Appendix A of the spec, verbatim; product facts only from the README at the build commit plus the five
  known limits from the threat model; the Polish pages are translations of the English ones; prose of Markdown pages wrapped at about
  120 columns.
- **Versions date:** "formatted from the `YYYY-MM-DD` string with a month table of the site (English `24 Nov 2026`, Polish `24 lis
  2026`), not with `Intl`"; badge "Latest" / "Najnowsza"; Polish note "Szczegóły poniżej są po angielsku, jak w changelogu."; aside
  titled "Upgrading" / "Aktualizacja".
- **Summaries:** "One to two sentences each, at most 300 characters." The build fails, naming the file and the version, on a missing
  summary, an empty or longer one, and a key that is not a server version.
- **Tokens (light / dark):** background `#f5fafb` / `#04090c`; surface `#e9f3f5` / `#0c171c`; border `#cfe2e6` / `#1a3038`; text
  `#08171b` / `#ecf8fb`; muted text `#3f5a61` / `#9dbac2`; code background `#111216` / `#0c171c`; code text `#f4f4f2`; code muted text
  `#a9abb3`; ok `#1d7348` / `#5fd08f`; not ok `#b3261e` / `#ff8a80`; accent `#22d3ee`; text on accent `#0a0a0a`.
- **Photo:** "a gradient from the background at 35 % to 55 % at 45 % to 100 % at the bottom, over the photo; the photo fills a 1500 px
  high area at the top of the Start page, or the 280 px band, with `background-size: cover`, `no-repeat`, `center top`."
- **Tint:** "a flat layer of `#0891b2` at 70 % opacity, with `background-blend-mode: normal, color, normal`."
- **Glass:** "the background at 80 % opacity, a border of the text colour at 12 %, radius 22 px, `backdrop-filter: blur(18px)`. Where
  `backdrop-filter` is not supported, or `prefers-reduced-transparency: reduce` is set, the card uses the solid surface colour."
- **Photos:** "WebP, 2200 × 1300, at most 400 KB each: `city-dark.webp`: the photo as it is, cropped to cover; `city-light.webp`:
  grayscale, inverted, contrast 1.15."
- **One accent:** "Cyan marks the buttons, the badge, the 'latest' pill, the licence card, the fact dots and the highlighted step of the
  diagram; nothing else is coloured."
- **Accessibility:** "text on every background and card at least 4.5:1, icons and borders that carry meaning at least 3:1 ...; touch
  targets at least 44 × 44 px; the layout works from 320 px wide; every page passes axe with no serious or critical issue."
- **Workflow:** triggers `push` to `main` with `paths: [site/**, CHANGELOG.md, scripts/release-notes.py, .github/workflows/site.yml]` and
  `workflow_dispatch`; `permissions: contents: read` at the top, the deploy job `contents: read`, `pages: write`, `id-token: write`;
  `concurrency: { group: pages, cancel-in-progress: false }`; "Every `uses:` names a full 40-character commit SHA, followed by a comment
  with its tag"; "No secrets. No step that runs on a pull request or a tag."
- **check-pins rules:** "6. Every `uses:` in `.github/workflows/*.yml` names a 40-character hexadecimal commit SHA after `@`; a local
  action (`./...`) is the exception. 7. Every version in `dependencies` and `devDependencies` of site/package.json is exact (`1.2.3`): no
  `^`, `~`, `*`, range, tag or URL."
- **Commits:** `git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "<type>: <subject>"`, the real current date (no
  `GIT_AUTHOR_DATE`), **no trailer of any kind** (no Co-Authored-By, no attribution line), subjects in lower-case conventional commits in
  plain English.
- **Machine:** Windows 10 with Git Bash; use `python`, never `python3` (the Microsoft Store stub); the build must also pass in a Linux
  container (Task 14).
- **Documents:** every Markdown file this plan writes passes `python scripts/check-docs.py <file>`: no emoji, every relative link and
  every code span that names a repository path resolves.

## Checked on this code before the plan was written

- `CHANGELOG.md`: version headings at lines 6 (`## 0.4.0 - 2026-11-24`), 41 (0.3.0), 111 (0.2.0), 208 (0.1.2), 216 (0.1.1), 224 (0.1.0)
  and 270 (`## python-v0.1.1 - 2026-02-24`): 6 server, 1 python. `### Upgrading` at lines 35, 106 and 200, each the last subsection of
  0.4.0, 0.3.0 and 0.2.0. No `## Unreleased`, no `<`, no fenced code, no `releases/tag`.
- Tags: `v0.4.0` b340c02, `v0.3.0` 3b80059, `v0.2.0` 84752b4, `v0.1.2` ab5179b, `v0.1.1` 4680c3f, `v0.1.0` 0606bc2, `python-v0.1.1`
  0606bc2: the 7 tags of criteria 2 and 4.
- `scripts/release-notes.py` (190 lines): `section` splits on `"\n"`; `release_body` ends the body with `Full changelog:
  [CHANGELOG.md](https://github.com/MckCieply/Auth-Core/blob/<tag>/CHANGELOG.md)`; `self_test` (lines 119-157) uses `check(name, ok)`
  and prints exactly `self-test ok`; `main` (lines 160-186) reconfigures stdout to UTF-8 with `"\n"` newlines.
- `scripts/check-pins.py` (257 lines): `is_document` lines 64-65; `check_repository` lines 159-176; `self_test` lines 179-238 (cases list
  182-214, the whole-repository checks 220-237); the success line at 252.
- `scripts/check-docs.py`: rules as in its docstring; it takes any document as an argument.
- `docs/workflow.md` (CRLF): the "Local-only" box at lines 8-11; "## Branch & integration (local, no PR/CI)" at line 159, its second bullet
  at lines 162-163.
- `docs/operations/releases.md` (LF, 31 lines): line 5 "Do it after the annotated tag is pushed ..."; "## The service" at 7; the
  explanation at 14; "## The Python package" at 16.
- `README.md` (CRLF): line 5 the release badge; the repository layout block with `scripts/` at line 220 and `docs/` at 221; line 229 the
  specs line; "## How to use it" at line 62 (anchor `#how-to-use-it`).
- `docs/security/threat-model.md`, residual risks (anchor `#residual-risks`): 3 (30 logins a minute behind one NAT), 4 (an admin with
  every permission deletes the company; no undo but the backup), 13 (up to 15 minutes: 10 of the token and 5 of clock skew), 15 (another
  tab up to 10 minutes after sign-out), 17 (Safari on a real phone; WebKit in Playwright).
- `docs/LICENSE` and `samples/LICENSE`: MIT, "Copyright (c) 2026 Aleksander Torka", identical.
- Unsplash: `https://images.unsplash.com/photo-1480843669328-3f7e37d196ae` answers `HEAD` with 200, `image/jpeg`, 3,993,452 bytes; with
  `?ixlib=rb-4.0.3&fm=jpg&q=90&w=2600` 1,753,529 bytes. Nothing was downloaded.
- Local tools: node v24.19.0, npm 11.17.0, Python 3.12.10.

## Review Focus

Inputs and failure modes the spec implies that a person is most likely to meet and no acceptance criterion names, most likely first;
each has its test in the task named.

1. **A component link written without the base path** (`href="/fit/"` instead of `/Auth-Core/fit/`): it works in `astro dev` on some
   setups and is a 404 on GitHub Pages; the link validator does not see it. The build must fail naming the page and the link
   (site/scripts/prebuild.test.mjs "links: a component link without the base path is reported", Task 4).
2. **The language link of the Start page.** Starlight gives the Start pages the ids `''` and `pl`; a naive prefix swap sends `/pl/` to
   `/pl/pl/` or to nothing. The footer link must move between `/Auth-Core/` and `/Auth-Core/pl/` and between inner pages both ways
   ("language link: Start and inner pages, both ways", Task 4).
3. **Windows Python.** On the owner's machine `python3` is the Store stub (exit 9009), and on others `python` may be missing: the prebuild
   must move on to the next interpreter, and when none passes name all three with their cause ("python: the Windows stub of python3 and
   a missing python move on to py -3", "python: when none passes, all three are named with their cause", Task 4).
4. **A summary whose first sentence holds a version number** ("... from 0.3.0 to 0.4.0 ..."): the Start page's version lines must not cut
   at the dot inside `0.4.0`, and a summary with no full stop is shown whole ("first sentence: a version number does not end it",
   Task 4).
5. **Every month in both languages**, the day without a leading zero, `paź` with its Polish letter: a wrong entry of the month table shows
   only in the month it names ("dates: every month in both languages, the day without a leading zero", Task 4).

## File Structure

| File | Task | Responsibility |
| --- | --- | --- |
| `scripts/release-notes.py` | 1 | `--all-json`: every version section as JSON; self-test cases |
| `scripts/check-pins.py` | 2 | rules 6 and 7; rule 3 also for `.mdx` |
| site/package.json, site/package-lock.json | 3, 4 | exact pins; the scripts (Task 4 adds the checks) |
| site/.gitignore, `site/LICENSE` | 3 | ignored build output; MIT |
| site/astro.config.mjs | 3, 5, 10 | site, base, locales, sidebar, fonts, validator; component overrides |
| site/tsconfig.json, site/src/content.config.ts | 3 | Astro's strict preset; the Starlight docs collection |
| `site/src/styles/theme.css` | 3, 5, 9, 10, 11 | tokens and their Starlight mapping; footer, versions, Start, photo styles |
| `site/public/favicon.svg` | 3 | the icon |
| `site/src/content/docs/` (10 pages) | 3, 6-10 | the five pages in English and, under `pl/`, in Polish |
| site/src/lib/versions.mjs, site/src/lib/paths.mjs | 4 | pure functions: dates, Upgrading cut, first sentence, raw HTML; paths with the base |
| site/scripts/checks.mjs, prebuild.mjs, check-links.mjs, prebuild.test.mjs | 4 | the build checks and their tests |
| site/src/data/version-summaries.pl.json, version-summaries.en.json | 4 | the summaries of Appendix B |
| `site/src/components/Footer.astro` | 5 | the licence line, the edit link, the plain language link |
| `site/src/components/CurrentVersion.astro` | 6 | the newest server version as text |
| `site/src/components/VersionList.astro` | 9 | the Versions page |
| site/src/lib/start-copy.mjs | 10 | Appendix A, English and Polish |
| `site/src/components/Hero.astro`, `site/src/components/StartBento.astro` | 10 | the Start page |
| `site/src/components/PageTitle.astro` | 11 | the inner pages' title below the photo band |
| site/scripts/make-photos.mjs, `site/src/assets/photos/city-dark.webp`, `site/src/assets/photos/city-light.webp` | 11 | the photos |
| site/CREDITS.md | 11 | photo and font credits |
| `.github/workflows/site.yml` | 12 | build and publish |
| `docs/workflow.md`, `docs/operations/releases.md`, `README.md` | 13 | the documents |

## The tasks

| Task | Deliverable |
| --- | --- |
| 1 | `release-notes.py --all-json` with its self-test cases |
| 2 | `check-pins.py` rules 6 and 7, rule 3 for `.mdx` |
| 3 | The site scaffold: pinned packages, config, tokens, ten empty pages, `site/LICENSE` |
| 4 | The build checks: prebuild, postbuild link check, `node --test` file, the summaries |
| 5 | The footer |
| 6 | Is it for me, English and Polish |
| 7 | Comparison, English and Polish, every competitor cell sourced |
| 8 | License, English and Polish |
| 9 | Versions, English and Polish |
| 10 | Start, English and Polish: hero, bento, glass, targets |
| 11 | The photos, the photo band and the credits (owner's yes for the download first) |
| 12 | The workflow |
| 13 | The documents |
| 14 | Local verification (the verifier, not an implementer) |
| 15 | Merge and publish (the orchestrator; owner's yes at each step) |

Tasks run in order. 1 and 2 need nothing; 3 needs 2 (its check-pins run sees site/package.json); 4 needs 1 and 3; 5 to 10 need 4; 10
needs 9 (`.ac-pill`); 11 needs 10; 12 needs 2; 13 needs 11 and 12; 14 needs everything; 15 needs 14 and the owner. Each task ends with
its own verify commands green and one commit. An implementer stops and reports, instead of improvising, when a step's expected output
does not come, when a fix would touch a file outside the task's list, or when the spec and this plan disagree beyond a name.

---

### Task 1: `release-notes.py --all-json`

**Files:**
- Modify: `scripts/release-notes.py` (docstring lines 4-18, imports lines 20-22, new code after `release_body` at line 116, `self_test`
  after line 152, `main` lines 164-171)

**Interfaces:**
- Consumes: `title_for(tag)`, `release_body(text, tag)` (unchanged).
- Produces: `python scripts/release-notes.py --all-json` prints one JSON array (UTF-8, `ensure_ascii=False`, indent 2, a final newline):
  objects with the keys `tag`, `kind` (`"server"` or `"python"`), `version`, `date` (`YYYY-MM-DD`), `title`, `body`, in the order of
  the file (newest first). Exit 1 with `CHANGELOG.md: <reason naming the heading>` on stderr when a version heading breaks the rule.
  Functions `all_sections(text) -> list[dict]` and `class ChangelogError(Exception)`. Task 4's prebuild runs it.

- [ ] **Step 1: Write the failing self-test cases**

In `self_test`, between the line `check("unwrap keeps links", ...)` (line 152) and `for name in failures:` (line 153), insert:

```python
    def sections_or_error(text):
        try:
            return all_sections(text), None
        except ChangelogError as error:
            return None, str(error)

    def rejects(text, named):
        entries, error = sections_or_error(text)
        return entries is None and error is not None and named in error

    entries, error = sections_or_error(sample)
    check("all-json order and kinds", error is None and [(e["tag"], e["kind"], e["version"], e["date"], e["title"]) for e in entries] == [
        ("v0.2.0", "server", "0.2.0", "2026-01-02", "Auth-Core 0.2.0"),
        ("v0.1.0", "server", "0.1.0", "2026-01-01", "Auth-Core 0.1.0"),
        ("python-v0.1.1", "python", "0.1.1", "2026-01-03", "auth-core-fastapi 0.1.1"),
    ])
    check("all-json body", error is None and all(e["body"] == release_body(sample, e["tag"]) for e in entries))
    check("all-json keys", error is None and all(list(e) == ["tag", "kind", "version", "date", "title", "body"] for e in entries))
    with_unreleased = "# Changelog\n\n## Unreleased\n\n- next\n\n" + sample.split("\n", 2)[2]
    entries, error = sections_or_error(with_unreleased)
    check("all-json skips unreleased", error is None and [e["tag"] for e in entries] == ["v0.2.0", "v0.1.0", "python-v0.1.1"]
          and "next" not in entries[0]["body"])
    check("all-json no date", rejects(sample.replace("## 0.1.0 - 2026-01-01", "## 0.1.0"), "## 0.1.0"))
    check("all-json bad date", rejects(sample.replace("2026-01-01", "2026-02-30"), "2026-02-30"))
    check("all-json duplicate", rejects(sample + "\n## 0.2.0 - 2026-01-05\n\nAgain.\n", "v0.2.0"))
    check("all-json no version heading", rejects("# Changelog\n\n## Unreleased\n\n- next\n", "no version heading"))
    check("all-json a heading with words after the date",
          rejects(sample.replace("## 0.1.0 - 2026-01-01", "## 0.1.0 - 2026-01-01 (yanked)"), "(yanked)"))
```

- [ ] **Step 2: Run the self-test and see it fail**

Run: `python scripts/release-notes.py --self-test`
Expected: a traceback ending `NameError: name 'all_sections' is not defined`.

- [ ] **Step 3: Write the code**

Replace the imports (lines 20-22) with:

```python
import datetime
import json
import pathlib
import re
import sys
```

After `BLOCK_START = ...` (line 29) add:

```python
VERSION_HEADING = re.compile(r"^## (python-v)?\d+\.\d+\.\d+( |$)")
DATED_HEADING = re.compile(r"^## ((?:python-v)?)(\d+\.\d+\.\d+) - (\d{4}-\d{2}-\d{2})$")
```

After `release_body` (it ends at line 116) add:

```python
class ChangelogError(Exception):
    """CHANGELOG.md breaks a rule of --all-json; the message names the heading."""


def all_sections(text):
    """One dict per version section, in the order of the file. A version heading is `## X.Y.Z` or `## python-vX.Y.Z` followed by a
    space or the end of the line; it must be `## <version> - YYYY-MM-DD` with a real date and appear once. Other `## ` headings
    (`## Unreleased`) are skipped."""
    entries = []
    seen = set()
    for line in text.split("\n"):
        if not VERSION_HEADING.match(line):
            continue
        dated = DATED_HEADING.match(line)
        if not dated:
            raise ChangelogError(f"the version heading '{line}' is not '## <version> - YYYY-MM-DD'")
        prefix, version, date = dated.groups()
        try:
            datetime.date.fromisoformat(date)
        except ValueError:
            raise ChangelogError(f"the version heading '{line}' has an impossible date") from None
        tag = ("python-v" if prefix else "v") + version
        if tag in seen:
            raise ChangelogError(f"the version heading '{line}' repeats the version {tag}")
        seen.add(tag)
        entries.append({
            "tag": tag,
            "kind": "python" if prefix else "server",
            "version": version,
            "date": date,
            "title": title_for(tag),
            "body": release_body(text, tag),
        })
    if not entries:
        raise ChangelogError("no version heading ('## X.Y.Z - YYYY-MM-DD')")
    return entries
```

In `main`, replace

```python
    if args == ["--self-test"]:
        return self_test()
```

with

```python
    if args == ["--self-test"]:
        return self_test()
    if args == ["--all-json"]:
        changelog = pathlib.Path(__file__).resolve().parent.parent / "CHANGELOG.md"
        try:
            entries = all_sections(changelog.read_text(encoding="utf-8"))
        except ChangelogError as error:
            print(f"CHANGELOG.md: {error}", file=sys.stderr)
            return 1
        sys.stdout.write(json.dumps(entries, ensure_ascii=False, indent=2) + "\n")
        return 0
```

and the usage line (line 170) with

```python
        print("usage: release-notes.py [--title] <tag> | --all-json | --self-test", file=sys.stderr)
```

In the docstring, replace lines 4-8 with:

```
Usage, from anywhere:  python scripts/release-notes.py <tag>           (the release body, on stdout)
                       python scripts/release-notes.py --title <tag>   (the release title only)
                       python scripts/release-notes.py --all-json      (every version section as one JSON array, on stdout)
                       python scripts/release-notes.py --self-test
Exit status 0 on success, 1 when the tag is malformed or has no section in CHANGELOG.md, or (--all-json) when a version heading breaks
rule 5. `--self-test` runs the rules on small inline samples (no repository needed).
```

and after item 4 (line 17) add:

```
  5. `--all-json` prints one object per version section (a heading `## X.Y.Z` or `## python-vX.Y.Z`), in the order of the file, with
     `tag`, `kind` (`server` or `python`), `version`, `date`, `title` and `body` (the text of items 2-4); every version heading must
     be `## <version> - YYYY-MM-DD` with a real date and appear once, and other `## ` headings (`## Unreleased`) are skipped.
```

- [ ] **Step 4: Run the self-test and see it pass**

Run: `python scripts/release-notes.py --self-test`
Expected: exactly `self-test ok`, exit 0.

- [ ] **Step 5: Check the real changelog**

Run:

```bash
python scripts/release-notes.py --all-json | python -c "import json,sys; d=json.loads(sys.stdin.buffer.read().decode('utf-8')); print(len(d), sum(e['kind']=='server' for e in d))"
python - <<'EOF'
import json, subprocess, sys
out = subprocess.run([sys.executable, "scripts/release-notes.py", "--all-json"], capture_output=True, check=True).stdout
entries = json.loads(out.decode("utf-8"))
bad = [e["tag"] for e in entries
       if subprocess.run([sys.executable, "scripts/release-notes.py", e["tag"]], capture_output=True, check=True).stdout.decode("utf-8") != e["body"]]
print("DIFFER: " + ", ".join(bad) if bad else "bodies equal for %d tags" % len(entries))
EOF
```

Expected: `7 6`, then `bodies equal for 7 tags`.

- [ ] **Step 6: The existing usages print the same bytes**

Run:

```bash
tmp=$(mktemp -d)
mkdir -p "$tmp/scripts"
git show main:scripts/release-notes.py > "$tmp/scripts/release-notes.py"
cp CHANGELOG.md "$tmp/CHANGELOG.md"
for tag in v0.4.0 v0.3.0 v0.2.0 v0.1.2 v0.1.1 v0.1.0 python-v0.1.1; do
  python "$tmp/scripts/release-notes.py" "$tag" > "$tmp/old.txt"; python scripts/release-notes.py "$tag" > "$tmp/new.txt"
  cmp -s "$tmp/old.txt" "$tmp/new.txt" && echo "same body $tag" || echo "DIFFERENT body $tag"
  python "$tmp/scripts/release-notes.py" --title "$tag" > "$tmp/old.txt"; python scripts/release-notes.py --title "$tag" > "$tmp/new.txt"
  cmp -s "$tmp/old.txt" "$tmp/new.txt" && echo "same title $tag" || echo "DIFFERENT title $tag"
done
rm -rf "$tmp"
```

Expected: 14 lines, each starting `same`.

- [ ] **Step 7: Teeth**

1. Comment out the two lines `if tag in seen:` and the `raise` under it; run `python scripts/release-notes.py --self-test`; expected
   `self-test FAILED: all-json duplicate`, exit 1. Restore.
2. Replace the `try: datetime.date.fromisoformat(date) ... from None` block with `pass`; run the self-test; expected `self-test FAILED:
   all-json bad date`. Restore, run the self-test again: `self-test ok`.

- [ ] **Step 8: Commit**

```bash
git add scripts/release-notes.py
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "feat: release-notes.py prints every version as json"
```

---

### Task 2: `check-pins.py` rules 6 and 7, rule 3 for `.mdx`

**Files:**
- Modify: `scripts/check-pins.py` (docstring lines 10-22, imports 26-30, constants after line 48, `is_document` 64-65, new functions
  before `check_repository` at line 159, `check_repository` 159-176, `self_test` 179-238, `main` line 252)

**Interfaces:**
- Consumes: nothing new.
- Produces: `check_workflow(name, text) -> list[str]`, `check_site_package(text) -> list[str]`, `is_workflow(path) -> bool`;
  `python scripts/check-pins.py` checks every `.github/workflows/*.yml` and site/package.json once they exist (Tasks 3 and 12 rely
  on it) and scans `.mdx` pages under rule 3.

- [ ] **Step 1: Write the failing self-test cases**

At the top of `self_test`, after `digest = "@sha256:" + "a" * 64` (line 180), add `sha = "a" * 40`. At the end of the `cases` list,
after the line `("package copy, nothing named", ...)` (line 213), add:

```python
        ("workflow, pinned by SHA", 0, check_workflow("w.yml", f"      - uses: actions/checkout@{sha} # v7.0.1\n")),
        ("workflow, quoted and pinned", 0, check_workflow("w.yml", f"        uses: \"withastro/action@{sha}\"  # v6.1.4\n")),
        ("workflow, a local action", 0, check_workflow("w.yml", "      - uses: ./.github/actions/build\n")),
        ("workflow, a tag", 1, check_workflow("w.yml", "      - uses: actions/checkout@v7\n")),
        ("workflow, a short SHA", 1, check_workflow("w.yml", "      - uses: actions/checkout@3d3c42e\n")),
        ("workflow, no ref", 1, check_workflow("w.yml", "      - uses: actions/checkout\n")),
        ("workflow, a branch", 1, check_workflow("w.yml", "      - uses: actions/checkout@main\n")),
        ("workflow, a commented uses", 0, check_workflow("w.yml", "      # uses: actions/checkout@v7\n")),
        ("site package, exact", 0, check_site_package(json.dumps({"dependencies": {"astro": "7.4.13"}, "devDependencies": {"x": "1.0.0"}}))),
        ("site package, a caret", 1, check_site_package(json.dumps({"dependencies": {"astro": "^7.4.13"}}))),
        ("site package, loose versions", 5, check_site_package(json.dumps(
            {"dependencies": {"a": "~1.2.3", "b": "*", "c": ">=1.0.0 <2.0.0", "d": "latest"}, "devDependencies": {"e": "https://x.y/e.tgz"}}))),
        ("site package, no dependencies", 0, check_site_package(json.dumps({"name": "x"}))),
        ("site package, not JSON", 1, check_site_package("{")),
        ("site package, not an object", 1, check_site_package("[]")),
```

After the existing `for odd in (...)` loop (lines 227-231) add:

```python
    for odd, text in (
        (".github/workflows/site.yml", "jobs:\n  b:\n    steps:\n      - uses: actions/checkout@v7\n"),
        (SITE_PACKAGE, json.dumps({"dependencies": {"astro": "^7.4.13"}})),
        ("site/src/content/docs/a.mdx", "Run `postgres:16` somewhere.\n"),
    ):
        bad = {**files, odd: text}
        found = check_repository(set(bad), bad.__getitem__)
        if not any(problem.startswith(odd) for problem in found):
            failures.append(f"self-test {odd}: expected a problem in it, got {found}")
```

After `del files["docs/a.md"]` (line 235) add:

```python
    files[".github/workflows/site.yml"] = f"jobs:\n  b:\n    steps:\n      - uses: actions/checkout@{sha} # v7.0.1\n      - uses: ./local\n"
    files[SITE_PACKAGE] = json.dumps({"dependencies": {"astro": "7.4.13"}, "devDependencies": {}})
```

- [ ] **Step 2: Run the self-test and see it fail**

Run: `python scripts/check-pins.py --self-test`
Expected: a traceback ending `NameError: name 'check_workflow' is not defined`.

- [ ] **Step 3: Write the code**

Imports (lines 26-30) become:

```python
import json
import pathlib
import posixpath
import re
import subprocess
import sys
```

After `PACKAGE_DIRECTORY = "clients/python/"` (line 48) add:

```python
WORKFLOW_USES = re.compile(r"^\s*(?:-\s*)?uses:\s*[\"']?([^\s\"'#]+)")
FULL_SHA = re.compile(r"@[0-9a-f]{40}$")
EXACT_VERSION = re.compile(r"^\d+\.\d+\.\d+$")
SITE_PACKAGE = "site/package.json"
```

`is_document` (lines 64-65) becomes, with a new function after it:

```python
def is_document(path):
    return path.endswith((".md", ".mdx")) and not path.startswith("docs/superpowers/")


def is_workflow(path):
    return path.startswith(".github/workflows/") and path.endswith((".yml", ".yaml"))
```

Before `def check_repository` add:

```python
def check_workflow(name, text):
    """Rule 6: every `uses:` of a workflow names its action by a 40-character commit SHA; a local action (`./...`) is the exception."""
    problems = []
    for number, line in enumerate(text.splitlines(), start=1):
        found = WORKFLOW_USES.match(line)
        if not found:
            continue
        action = found.group(1)
        if action.startswith("./") or FULL_SHA.search(action):
            continue
        problems.append(f"{name}:{number}: the action {action} is not pinned by a 40-character commit SHA (owner/repo@<sha> # vX.Y.Z)")
    return problems


def check_site_package(text):
    """Rule 7: every version in `dependencies` and `devDependencies` of site/package.json is exact (1.2.3)."""
    try:
        package = json.loads(text)
    except json.JSONDecodeError as error:
        return [f"{SITE_PACKAGE}: not valid JSON ({error})"]
    if not isinstance(package, dict):
        return [f"{SITE_PACKAGE}: not a JSON object"]
    problems = []
    for field in ("dependencies", "devDependencies"):
        entries = package.get(field, {})
        if not isinstance(entries, dict):
            problems.append(f"{SITE_PACKAGE}: {field} is not an object")
            continue
        for dependency, version in sorted(entries.items()):
            if not isinstance(version, str) or not EXACT_VERSION.match(version):
                problems.append(f"{SITE_PACKAGE}: {field} {dependency} is {version!r}, not an exact version (1.2.3)")
    return problems
```

In `check_repository`, before `return problems` (line 176) add:

```python
    for path in sorted(p for p in files if is_workflow(p)):
        problems.extend(check_workflow(path, read(path)))
    if SITE_PACKAGE in files:
        problems.extend(check_site_package(read(SITE_PACKAGE)))
```

The success line in `main` (line 252) becomes:

```python
    print("\n".join(problems) or "every image is pinned by digest, every workflow action by commit SHA, the site's packages by exact version, the development compose passes the limiter settings, the package's build files are copied")
```

In the docstring, rule 3 (line 16) reads "an `image:` or `FROM` line in a Markdown or MDX file (`.md`, `.mdx`) outside docs/superpowers/
follows rules", and after rule 5 (line 22) add:

```
  6. every `uses:` of a workflow (.github/workflows/*.yml) names its action by a 40-character commit SHA (`owner/repo@<sha> # vX.Y.Z`);
     a local action (`./...`) is the exception;
  7. every version in `dependencies` and `devDependencies` of site/package.json is exact (`1.2.3`): no `^`, `~`, `*`, range, tag or
     URL. Before the file exists there is nothing to check.
```

- [ ] **Step 4: Run the self-test and the repository check**

Run: `python scripts/check-pins.py --self-test` then `python scripts/check-pins.py`
Expected: `self-test passed: every rule catches its bad sample and lets its good one through`; then the new success line, exit 0
(no workflow and no site/package.json exist yet).

- [ ] **Step 5: Teeth**

1. Change `FULL_SHA` to `re.compile(r"@[0-9a-f]{7,40}$")`; run the self-test; expected `self-test workflow, a short SHA: expected 1
   problem(s), got 0`. Restore.
2. Change `EXACT_VERSION` to `re.compile(r"^\S+$")`; run the self-test; expected a failure line for `site package, a caret`. Restore;
   the self-test passes again.

- [ ] **Step 6: Commit**

```bash
git add scripts/check-pins.py
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "feat: check-pins.py checks workflow actions and the site's packages"
```

---

### Task 3: The site scaffold

**Files:**
- Create: site/package.json, site/package-lock.json (written by npm), site/.gitignore, `site/LICENSE`, site/tsconfig.json,
  site/astro.config.mjs, site/src/content.config.ts, `site/src/styles/theme.css`, `site/public/favicon.svg`, and ten pages:
  `site/src/content/docs/index.mdx`, `fit.mdx`, compare.md (path site/src/content/docs/compare.md), license.md (path
  site/src/content/docs/license.md), `versions.mdx`, and the same five under `site/src/content/docs/pl/`.
- Modify: `LICENSE` lines 9-11 and `README.md` line 239 (Step 4), and nothing else outside `site/`.
- Must not touch: anything else outside `site/`.

**Interfaces:**
- Consumes: rule 7 of `scripts/check-pins.py` (Task 2).
- Produces: a site that builds with `npx astro build` into `site/dist/`; the CSS custom properties `--ac-bg`, `--ac-surface`,
  `--ac-border`, `--ac-text`, `--ac-muted`, `--ac-code-bg`, `--ac-code-text`, `--ac-code-muted`, `--ac-ok`, `--ac-not-ok`,
  `--ac-accent`, `--ac-on-accent`, `--ac-glass`, `--ac-glass-border`, `--ac-radius` (both themes) that every later task uses; the
  page ids `''`, `fit`, `compare`, `license`, `versions` and `pl`, `pl/fit`, ... that Task 4's paths work on.

- [ ] **Step 1: Write site/package.json**

```json
{
  "name": "auth-core-site",
  "version": "0.0.0",
  "private": true,
  "type": "module",
  "license": "MIT",
  "scripts": {
    "dev": "astro dev",
    "build": "astro build",
    "preview": "astro preview"
  },
  "dependencies": {
    "@astrojs/starlight": "0.43.2",
    "@fontsource/ibm-plex-mono": "5.3.0",
    "@fontsource/space-grotesk": "5.3.0",
    "astro": "7.4.13",
    "marked": "18.3.0",
    "starlight-links-validator": "0.27.1"
  }
}
```

- [ ] **Step 2: Write site/.gitignore before the first install**

```
node_modules/
dist/
.astro/
src/data/versions.json
```

- [ ] **Step 3: Install**

Run: `cd site && npm install --no-audit --no-fund`
Expected: exit 0; site/package-lock.json exists; `git status --porcelain site` lists `site/.gitignore`, site/package.json,
site/package-lock.json and no `node_modules`. If npm rewrote a version in site/package.json with a `^`, set it back to the exact
version and run `npm install --no-audit --no-fund` again.

- [ ] **Step 4: Copy the licence**

Run: `cp docs/LICENSE site/LICENSE && cmp docs/LICENSE site/LICENSE && echo same`
Expected: `same` (MIT, "Copyright (c) 2026 Aleksander Torka").

Then name `site/` among the MIT parts (spec Decision 11). In `LICENSE` replace the three lines

```
                      The files under clients/, samples/ and docs/ are not part of
                      the Licensed Work; they are licensed under the MIT License
                      (see the LICENSE file in each of those directories).
```

with these four (the same 22-space indent, no line over 80 columns, LF as the file has):

```
                      The files under clients/, samples/, docs/ and site/ are
                      not part of the Licensed Work; they are licensed under the
                      MIT License (see the LICENSE file in each of those
                      directories).
```

In `README.md` (CRLF: keep it) replace line 239

```
- **MIT:** `clients/`, `samples/` and `docs/` are under the MIT License (see the `LICENSE` file in each).
```

with

```
- **MIT:** `clients/`, `samples/`, `docs/` and `site/` are under the MIT License (see the `LICENSE` file in each).
```

Run: `grep -c 'site/' LICENSE && grep -n 'and .site/. are under the MIT' README.md && git diff --stat LICENSE README.md`
Expected: `1`; README line 239; `LICENSE` 4 insertions and 3 deletions, `README.md` 1 and 1.

- [ ] **Step 5: Write site/tsconfig.json and site/src/content.config.ts**

```json
{
  "extends": "astro/tsconfigs/strict",
  "include": [".astro/types.d.ts", "**/*"],
  "exclude": ["dist"]
}
```

```ts
import { defineCollection } from 'astro:content';
import { docsLoader } from '@astrojs/starlight/loaders';
import { docsSchema } from '@astrojs/starlight/schema';

export const collections = {
  docs: defineCollection({ loader: docsLoader(), schema: docsSchema() }),
};
```

- [ ] **Step 6: Write site/astro.config.mjs**

```js
// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';

export default defineConfig({
  site: 'https://mckcieply.github.io',
  base: '/Auth-Core',
  integrations: [
    starlight({
      title: 'Auth-Core',
      favicon: '/favicon.svg',
      defaultLocale: 'root',
      locales: {
        root: { label: 'English', lang: 'en' },
        pl: { label: 'Polski', lang: 'pl' },
      },
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/MckCieply/Auth-Core' }],
      editLink: { baseUrl: 'https://github.com/MckCieply/Auth-Core/edit/main/site/' },
      pagination: false,
      lastUpdated: false,
      customCss: [
        '@fontsource/space-grotesk/400.css',
        '@fontsource/space-grotesk/500.css',
        '@fontsource/space-grotesk/700.css',
        '@fontsource/ibm-plex-mono/400.css',
        '@fontsource/ibm-plex-mono/500.css',
        './src/styles/theme.css',
      ],
      sidebar: [
        { slug: 'fit', label: 'Is it for me?', translations: { pl: 'Czy to dla mnie?' } },
        { slug: 'compare', label: 'Comparison', translations: { pl: 'Porównanie' } },
        { slug: 'license', label: 'License', translations: { pl: 'Licencja' } },
        { slug: 'versions', label: 'Versions', translations: { pl: 'Wersje' } },
      ],
      plugins: [starlightLinksValidator()],
    }),
  ],
});
```

The weight files (`400.css`, not `latin-400.css`) carry every subset with its `unicode-range`, `latin-ext` included. Search
(Pagefind) and the sitemap are on by default once `site` is set; nothing to add.

- [ ] **Step 7: Write `site/src/styles/theme.css`**

```css
/* Tokens of spec 0018, "Look" (board L4 Noc, palette P4 Cyjan). Dark is Starlight's default (:root), light is
   :root[data-theme='light']. Starlight's own CSS sits in cascade layers, so these unlayered rules win over it. */
:root {
  --ac-bg: #04090c;
  --ac-surface: #0c171c;
  --ac-border: #1a3038;
  --ac-text: #ecf8fb;
  --ac-muted: #9dbac2;
  --ac-code-bg: #0c171c;
  --ac-code-text: #f4f4f2;
  --ac-code-muted: #a9abb3;
  --ac-ok: #5fd08f;
  --ac-not-ok: #ff8a80;
  --ac-accent: #22d3ee;
  --ac-on-accent: #0a0a0a;
  --ac-glass: rgb(4 9 12 / 0.8);
  --ac-glass-border: rgb(236 248 251 / 0.12);
  --ac-radius: 22px;
}

:root[data-theme='light'] {
  --ac-bg: #f5fafb;
  --ac-surface: #e9f3f5;
  --ac-border: #cfe2e6;
  --ac-text: #08171b;
  --ac-muted: #3f5a61;
  --ac-code-bg: #111216;
  --ac-ok: #1d7348;
  --ac-not-ok: #b3261e;
  --ac-glass: rgb(245 250 251 / 0.8);
  --ac-glass-border: rgb(8 23 27 / 0.12);
}

/* Starlight's variables, mapped once: they resolve against the tokens of the current theme. Links and the text accent
   use the text colour, because cyan is kept for the elements of Settled 10. */
:root {
  --sl-font: 'Space Grotesk', system-ui, sans-serif;
  --sl-font-mono: 'IBM Plex Mono', ui-monospace, monospace;
  --sl-color-bg: var(--ac-bg);
  --sl-color-bg-nav: var(--ac-bg);
  --sl-color-bg-sidebar: var(--ac-bg);
  --sl-color-bg-inline-code: var(--ac-surface);
  --sl-color-bg-accent: var(--ac-accent);
  --sl-color-text: var(--ac-text);
  --sl-color-white: var(--ac-text);
  --sl-color-gray-1: var(--ac-text);
  --sl-color-gray-2: var(--ac-text);
  --sl-color-gray-3: var(--ac-muted);
  --sl-color-gray-4: var(--ac-muted);
  --sl-color-gray-5: var(--ac-border);
  --sl-color-gray-6: var(--ac-surface);
  --sl-color-black: var(--ac-bg);
  --sl-color-text-accent: var(--ac-text);
  --sl-color-text-invert: var(--ac-on-accent);
  --sl-color-accent-low: var(--ac-surface);
  --sl-color-accent: var(--ac-accent);
  --sl-color-accent-high: var(--ac-text);
  --sl-color-hairline-light: var(--ac-border);
  --sl-color-hairline: var(--ac-border);
  --sl-color-hairline-shade: var(--ac-border);
}

/* The current page in the sidebar: surface and text colour, no cyan (Settled 10). */
a[aria-current='page'],
a[aria-current='page']:hover,
a[aria-current='page']:focus {
  background-color: var(--ac-surface);
  color: var(--ac-text);
  font-weight: 700;
  box-shadow: inset 3px 0 0 var(--ac-text);
}

/* The caution aside of the Versions page: neutral, not orange (Settled 10). */
.starlight-aside--caution {
  --sl-color-asides-text-accent: var(--ac-text);
  --sl-color-asides-border: var(--ac-muted);
  background-color: var(--ac-surface);
}

a:focus-visible,
button:focus-visible,
select:focus-visible {
  outline: 2px solid var(--ac-text);
  outline-offset: 2px;
}
```

- [ ] **Step 8: Write `site/public/favicon.svg`**

```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" rx="7" fill="#04090c"/><circle cx="16" cy="16" r="7" fill="#22d3ee"/></svg>
```

- [ ] **Step 9: Write the ten pages, front matter only**

The bodies come in Tasks 6 to 10; each page here has its final front matter.

`site/src/content/docs/index.mdx`:

```mdx
---
title: Sign-in for products used by many companies.
description: "Self-hosted sign-in, companies and roles for B2B products with a backend in any language: one container and its own database, kept apart from your app, with sign-in screens in your own look."
template: splash
hero:
  title: Sign-in for products used by many companies.
---
```

`site/src/content/docs/pl/index.mdx`:

```mdx
---
title: Logowanie dla produktów, z których korzysta wiele firm.
description: "Logowanie, firmy i role dla produktów B2B z backendem w dowolnym języku, na Twoim serwerze: jeden kontener i własna baza, z dala od Twojej aplikacji, z ekranami logowania w Twoim wyglądzie."
template: splash
hero:
  title: Logowanie dla produktów, z których korzysta wiele firm.
---
```

`site/src/content/docs/fit.mdx` and `site/src/content/docs/pl/fit.mdx`:

```mdx
---
title: Is it for me?
description: When Auth-Core fits a product, when to look elsewhere, what it needs to run, its status and its known limits.
---
```

```mdx
---
title: Czy to dla mnie?
description: Kiedy Auth-Core pasuje do produktu, kiedy szukać gdzie indziej, czego potrzebuje do działania, jego stan i znane ograniczenia.
---
```

site/src/content/docs/compare.md and site/src/content/docs/pl/compare.md:

```md
---
title: Comparison
description: Auth-Core next to Better Auth, Keycloak, Clerk and WorkOS, with a source for every fact.
---
```

```md
---
title: Porównanie
description: Auth-Core obok Better Auth, Keycloak, Clerk i WorkOS, ze źródłem każdego faktu.
---
```

site/src/content/docs/license.md and site/src/content/docs/pl/license.md:

```md
---
title: License
description: What is free, what needs a commercial licence, the price, and what stays MIT.
---
```

```md
---
title: Licencja
description: Co jest za darmo, co wymaga licencji komercyjnej, cena i co zostaje na MIT.
---
```

`site/src/content/docs/versions.mdx` and `site/src/content/docs/pl/versions.mdx`:

```mdx
---
title: Versions
description: Every version of the Auth-Core server, newest first, with its changelog.
tableOfContents: false
---
```

```mdx
---
title: Wersje
description: Każda wersja serwera Auth-Core, od najnowszej, z jej changelogiem.
tableOfContents: false
---
```

- [ ] **Step 10: Build**

Run: `cd site && npx astro build`
Expected: exit 0; the log has the validator's line that all internal links are valid; these files exist: `dist/index.html`,
`dist/pl/index.html`, `dist/fit/index.html`, `dist/pl/fit/index.html`, `dist/compare/index.html`, `dist/pl/compare/index.html`,
`dist/license/index.html`, `dist/pl/license/index.html`, `dist/versions/index.html`, `dist/pl/versions/index.html`,
`dist/sitemap-index.xml`, `dist/pagefind/pagefind.js`. Check the fonts:
`grep -l "latin-ext" dist/_astro/*.css` lists at least one file, and `ls dist/_astro/ | grep -c "space-grotesk-latin-ext"` is at least 3.

- [ ] **Step 11: The repository checks**

Run, from the repository root: `python scripts/check-pins.py` and `git status --porcelain`
Expected: the success line of check-pins; `git status` lists only files under `site/` and none under `site/node_modules/`,
`site/dist/` or `site/.astro/`.

- [ ] **Step 12: Teeth**

In site/package.json change `"astro": "7.4.13"` to `"astro": "^7.4.13"`; run `python scripts/check-pins.py`; expected exit 1 and
`site/package.json: dependencies astro is '^7.4.13', not an exact version (1.2.3)`. Restore; the check passes again.

- [ ] **Step 13: Commit**

```bash
git add site
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "feat: site scaffold with pinned astro starlight, tokens and empty pages"
```

---

### Task 4: The build checks

**Files:**
- Create: site/src/lib/paths.mjs, site/src/lib/versions.mjs, site/scripts/checks.mjs, site/scripts/prebuild.mjs,
  site/scripts/check-links.mjs, site/scripts/prebuild.test.mjs, site/src/data/version-summaries.pl.json,
  site/src/data/version-summaries.en.json
- Modify: site/package.json (the scripts)
- Generated, ignored: site/src/data/versions.json

**Interfaces:**
- Consumes: `python scripts/release-notes.py --self-test` and `--all-json` (Task 1); the pages of Task 3.
- Produces, for Tasks 5 to 10:
  - site/src/lib/paths.mjs: `BASE` (`'/Auth-Core/'`), `withBase(path) -> string`, `pagePath(lang, slug) -> string` (lang `'en'`
    or `'pl'`, slug `''`, `'fit'`, `'compare'`, `'license'`, `'versions'`), `slugOf(id)`, `langOf(id)`, `otherLanguagePath(id)`
    (id: Starlight's route id).
  - site/src/lib/versions.mjs: `formatDate(date, lang) -> '24 Nov 2026'`, `serverVersions(entries) -> entries`,
    `FULL_CHANGELOG`, `splitBody(body) -> { upgrading, rest, fullChangelog }`, `firstSentence(text)`, `findRawHtml(markdown) -> string[]`.
  - site/src/data/versions.json: the array of `--all-json`, written by the prebuild.
  - `npm run build` runs the tests, the prebuild checks, `astro build` and the link check of the built HTML.

- [ ] **Step 1: Write the failing tests, site/scripts/prebuild.test.mjs**

```js
// Tests of the build checks and the pure functions of the site (spec 0018, "Prebuild"). Run: node --test scripts/*.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { checkSummaries, checkParity, checkBodies, pickPython, outcomeOf, brokenLinks, SUMMARY_LIMIT } from './checks.mjs';
import { formatDate, splitBody, firstSentence, findRawHtml, serverVersions } from '../src/lib/versions.mjs';
import { otherLanguagePath, pagePath, withBase, slugOf, BASE } from '../src/lib/paths.mjs';

const PL = 'src/data/version-summaries.pl.json';
const EN = 'src/data/version-summaries.en.json';
const FULL = 'Full changelog: [CHANGELOG.md](https://github.com/MckCieply/Auth-Core/blob/v0.2.0/CHANGELOG.md)';
const BT = String.fromCharCode(96);
const versions = [
  { tag: 'v0.2.0', kind: 'server', version: '0.2.0', date: '2026-01-02', title: 'Auth-Core 0.2.0', body: 'Two.\n\n' + FULL + '\n' },
  { tag: 'v0.1.0', kind: 'server', version: '0.1.0', date: '2026-01-01', title: 'Auth-Core 0.1.0', body: 'One.\n\n' + FULL + '\n' },
  { tag: 'python-v0.1.1', kind: 'python', version: '0.1.1', date: '2026-01-03', title: 'auth-core-fastapi 0.1.1', body: 'Py.\n\n' + FULL + '\n' },
];
const goodPl = { '0.2.0': 'Dwa.', '0.1.0': 'Jeden.' };
const goodEn = { '0.2.0': 'Two.', '0.1.0': 'One.' };
const both = (pl, en) => ({ pl, en });

test('versions: only server versions, in file order', () => {
  assert.deepEqual(serverVersions(versions).map((entry) => entry.version), ['0.2.0', '0.1.0']);
});

test('summaries: complete files pass', () => {
  assert.deepEqual(checkSummaries(versions, both(goodPl, goodEn)), []);
});

test('summaries: a version missing from the Polish file is named', () => {
  assert.deepEqual(checkSummaries(versions, both({ '0.2.0': 'Dwa.' }, goodEn)), [PL + ': no summary for 0.1.0']);
});

test('summaries: a version missing from the English file is named', () => {
  assert.deepEqual(checkSummaries(versions, both(goodPl, { '0.1.0': 'One.' })), [EN + ': no summary for 0.2.0']);
});

test('summaries: a key that is not a server version is named', () => {
  assert.deepEqual(checkSummaries(versions, both({ ...goodPl, '9.9.9': 'X.', '0.1.1': 'Py.' }, goodEn)), [
    PL + ': 9.9.9 is not a server version of CHANGELOG.md',
    PL + ': 0.1.1 is not a server version of CHANGELOG.md',
  ]);
});

test('summaries: 300 characters pass, 301 fail', () => {
  assert.equal(SUMMARY_LIMIT, 300);
  assert.deepEqual(checkSummaries(versions, both({ ...goodPl, '0.1.0': 'a'.repeat(300) }, goodEn)), []);
  assert.deepEqual(checkSummaries(versions, both(goodPl, { ...goodEn, '0.2.0': 'a'.repeat(301) })), [
    EN + ': the summary of 0.2.0 has 301 characters, more than 300',
  ]);
});

test('summaries: 300 Polish letters count as 300 characters', () => {
  assert.deepEqual(checkSummaries(versions, both({ ...goodPl, '0.2.0': 'ą'.repeat(300) }, goodEn)), []);
});

test('summaries: an empty summary is named', () => {
  assert.deepEqual(checkSummaries(versions, both({ ...goodPl, '0.1.0': '  ' }, goodEn)), [PL + ': the summary of 0.1.0 is empty']);
});

test('summaries: a file that is not an object from version to text is named', () => {
  assert.deepEqual(checkSummaries(versions, both(['Dwa.'], { ...goodEn, '0.1.0': 1 })), [
    PL + ': not an object from version to text',
    EN + ': not an object from version to text',
  ]);
});

const pages = ['index.mdx', 'fit.mdx', 'compare.md', 'pl/index.mdx', 'pl/fit.mdx', 'pl/compare.md'];

test('parity: complete pages pass', () => {
  assert.deepEqual(checkParity(pages), []);
});

test('parity: a missing Polish page is named', () => {
  assert.deepEqual(checkParity(pages.filter((path) => path !== 'pl/compare.md')), [
    'src/content/docs/compare.md has no Polish page src/content/docs/pl/compare.md',
  ]);
});

test('parity: a missing English page is named', () => {
  assert.deepEqual(checkParity(pages.filter((path) => path !== 'fit.mdx')), [
    'src/content/docs/pl/fit.mdx has no English page src/content/docs/fit.mdx',
  ]);
});

test('parity: the same page with another extension counts as missing', () => {
  assert.deepEqual(checkParity(['fit.mdx', 'pl/fit.md']), [
    'src/content/docs/fit.mdx has no Polish page src/content/docs/pl/fit.mdx',
    'src/content/docs/pl/fit.md has no English page src/content/docs/fit.md',
  ]);
});

test('parity: files that are not pages are ignored', () => {
  assert.deepEqual(checkParity([...pages, 'pl', 'notes.txt', 'pl/image.webp']), []);
});

test('dates: every month in both languages, the day without a leading zero', () => {
  const en = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  const pl = ['sty', 'lut', 'mar', 'kwi', 'maj', 'cze', 'lip', 'sie', 'wrz', 'paź', 'lis', 'gru'];
  for (let month = 1; month <= 12; month++) {
    const date = '2026-' + String(month).padStart(2, '0') + '-05';
    assert.equal(formatDate(date, 'en'), '5 ' + en[month - 1] + ' 2026');
    assert.equal(formatDate(date, 'pl'), '5 ' + pl[month - 1] + ' 2026');
  }
  assert.equal(formatDate('2026-11-24', 'en'), '24 Nov 2026');
  assert.equal(formatDate('2026-11-24', 'pl'), '24 lis 2026');
  assert.equal(formatDate('2026-10-01', 'pl'), '1 paź 2026');
});

test('dates: a malformed date or language throws', () => {
  assert.throws(() => formatDate('2026-1-5', 'en'));
  assert.throws(() => formatDate('2026-13-01', 'en'));
  assert.throws(() => formatDate('2026-11-24', 'de'));
});

test('upgrading: cut in the middle ends at the next ### heading', () => {
  const body = ['Intro.', '', '### Added', '- a', '', '### Upgrading', '', '- run the migration', '- set X', '', '### Fixed', '- b', '', FULL, ''].join('\n');
  assert.deepEqual(splitBody(body), {
    upgrading: '- run the migration\n- set X',
    rest: 'Intro.\n\n### Added\n- a\n\n### Fixed\n- b',
    fullChangelog: FULL,
  });
});

test('upgrading: cut at the end ends before the Full changelog line', () => {
  const body = ['Intro.', '', '### Upgrading', '', 'Back up first.', '', FULL, ''].join('\n');
  assert.deepEqual(splitBody(body), { upgrading: 'Back up first.', rest: 'Intro.', fullChangelog: FULL });
});

test('upgrading: a body without Upgrading keeps everything', () => {
  assert.deepEqual(splitBody('Intro.\n\n### Added\n- a\n\n' + FULL + '\n'), {
    upgrading: '',
    rest: 'Intro.\n\n### Added\n- a',
    fullChangelog: FULL,
  });
});

test('upgrading: a body without the Full changelog line throws', () => {
  assert.throws(() => splitBody('Intro.\n'), /Full changelog/);
});

test('first sentence: a version number does not end it', () => {
  assert.equal(firstSentence('Moves from 0.3.0 to 0.4.0 without a migration. Then more.'), 'Moves from 0.3.0 to 0.4.0 without a migration.');
  assert.equal(firstSentence('Sign-in with GitHub (spec 0012). A second one.'), 'Sign-in with GitHub (spec 0012).');
  assert.equal(firstSentence('Ends on a version 0.4.0.'), 'Ends on a version 0.4.0.');
  assert.equal(firstSentence('No full stop at all'), 'No full stop at all');
  assert.equal(firstSentence('Poprawka: mail nie podaje roli. Druga.'), 'Poprawka: mail nie podaje roli.');
});

test('raw html: a tag in prose is found', () => {
  assert.deepEqual(findRawHtml('Text <b>bold</b> and <br/>.'), ['<b>', '</b>', '<br/>']);
  assert.deepEqual(findRawHtml('<details open>'), ['<details open>']);
});

test('raw html: code spans, fences, autolinks and a lone < are ignored', () => {
  const fence = BT.repeat(3);
  const text = ['Use ' + BT + '<b>' + BT + ' here, see <https://x.y/z>, 1 < 2 and a -> b.', fence + 'html', '<div>', fence, ''].join('\n');
  assert.deepEqual(findRawHtml(text), []);
});

test('bodies: a raw HTML tag and a missing Full changelog line are named', () => {
  const bad = [{ ...versions[0], body: 'Two <b>x</b>.\n\n' + FULL + '\n' }, { ...versions[1], body: 'One.\n' }, versions[2]];
  assert.deepEqual(checkBodies(bad), [
    'versions.json: v0.2.0: raw HTML <b> in the changelog section',
    'versions.json: v0.2.0: raw HTML </b> in the changelog section',
    'versions.json: v0.1.0: splitBody: the body has no line starting with "Full changelog: "',
  ]);
  assert.deepEqual(checkBodies(versions), []);
});

const STORE = 'Python was not found; run without arguments to install from the Microsoft Store, or disable this shortcut from Settings > Manage App Execution Aliases.';
const missing = { error: Object.assign(new Error('spawnSync ENOENT'), { code: 'ENOENT' }), status: null, stdout: null, stderr: null };
const passing = { status: 0, stdout: 'self-test ok\n', stderr: '' };
function fake(results) {
  const calls = [];
  const run = (command, args) => {
    calls.push([command, ...args].join(' '));
    return results[command];
  };
  return { run, calls };
}

test('python: the Windows stub of python3 and a missing python move on to py -3', () => {
  const { run, calls } = fake({ python3: { status: 9009, stdout: '', stderr: STORE + '\r\n' }, python: missing, py: passing });
  const picked = pickPython(run, 'scripts/release-notes.py');
  assert.deepEqual(picked.python, ['py', '-3']);
  assert.deepEqual(calls, [
    'python3 scripts/release-notes.py --self-test',
    'python scripts/release-notes.py --self-test',
    'py -3 scripts/release-notes.py --self-test',
  ]);
  assert.deepEqual(picked.tried, ['python3: exit 9009: ' + STORE, 'python: not found']);
});

test('python: when none passes, all three are named with their cause', () => {
  const failing = { status: 1, stdout: '', stderr: 'self-test FAILED: all-json body\n' };
  const { run } = fake({ python3: missing, python: failing, py: missing });
  const picked = pickPython(run, 'scripts/release-notes.py');
  assert.equal(picked.python, null);
  assert.deepEqual(picked.tried, ['python3: not found', 'python: exit 1: self-test FAILED: all-json body', 'py -3: not found']);
});

test('python: exit 0 without the self-test ok line does not pass', () => {
  assert.equal(outcomeOf({ status: 0, stdout: 'self-test okay\n', stderr: '' }), 'exit 0: self-test okay');
  assert.equal(outcomeOf({ status: 0, stdout: 'self-test ok\r\n', stderr: '' }), 'ok');
  assert.equal(outcomeOf(passing), 'ok');
});

test('language link: Start and inner pages, both ways', () => {
  assert.equal(otherLanguagePath(''), '/Auth-Core/pl/');
  assert.equal(otherLanguagePath('index'), '/Auth-Core/pl/');
  assert.equal(otherLanguagePath('pl'), '/Auth-Core/');
  assert.equal(otherLanguagePath('pl/index'), '/Auth-Core/');
  assert.equal(otherLanguagePath('fit'), '/Auth-Core/pl/fit/');
  assert.equal(otherLanguagePath('pl/fit'), '/Auth-Core/fit/');
  assert.equal(otherLanguagePath('pl/versions/'), '/Auth-Core/versions/');
});

test('paths: pagePath and withBase add the base path', () => {
  assert.equal(BASE, '/Auth-Core/');
  assert.equal(pagePath('en', ''), '/Auth-Core/');
  assert.equal(pagePath('pl', ''), '/Auth-Core/pl/');
  assert.equal(pagePath('pl', 'license'), '/Auth-Core/pl/license/');
  assert.equal(withBase('/favicon.svg'), '/Auth-Core/favicon.svg');
  assert.equal(slugOf('pl/compare'), 'compare');
});

const built = new Set(['index.html', 'fit/index.html', 'pl/index.html', 'pl/fit/index.html', '_astro/a.css', 'favicon.svg']);
const exists = (path) => built.has(path);

test('links: good links, anchors, other hosts and assets pass', () => {
  const html = '<a href="/Auth-Core/fit/">x</a><a href="/Auth-Core/">s</a><a href="/Auth-Core">s</a><a href="#_top">t</a>'
    + '<a href="https://github.com/MckCieply/Auth-Core">g</a><a href="mailto:kontakt@toviq.pl">m</a>'
    + '<link rel="stylesheet" href="/Auth-Core/_astro/a.css"><img src="/Auth-Core/favicon.svg" alt=""><a href="pl/?x=1&amp;y=2">p</a>';
  assert.deepEqual(brokenLinks({ 'index.html': html }, exists, '/Auth-Core/'), []);
});

test('links: a component link without the base path is reported', () => {
  assert.deepEqual(brokenLinks({ 'pl/fit/index.html': '<a href="/fit/">x</a>' }, exists, '/Auth-Core/'), [
    'pl/fit/index.html: the link /fit/ is outside /Auth-Core/',
  ]);
});

test('links: a link to a missing page is reported', () => {
  assert.deepEqual(brokenLinks({ 'index.html': '<a href="/Auth-Core/nope/">x</a><a href="fit">y</a>' }, exists, '/Auth-Core/'), [
    'index.html: the link /Auth-Core/nope/ leads to no file',
  ]);
});
```

- [ ] **Step 2: Run the tests and see them fail**

Run: `cd site && node --test scripts/*.test.mjs`
Expected: fail with `ERR_MODULE_NOT_FOUND` for scripts/checks.mjs.

- [ ] **Step 3: Write site/src/lib/paths.mjs**

```js
// Paths of the site with the base path of astro.config.mjs ('/Auth-Core'). Pure functions, shared by the components
// and the tests. A component never writes a path by hand: it calls pagePath or withBase.
export const BASE = '/Auth-Core/';

export function withBase(path) {
  return BASE + String(path).replace(/^\/+/, '');
}

// lang: 'en' or 'pl'; slug: '' (Start), 'fit', 'compare', 'license' or 'versions'.
export function pagePath(lang, slug) {
  const parts = [lang === 'pl' ? 'pl' : '', slug].filter(Boolean);
  return parts.length ? withBase(parts.join('/') + '/') : BASE;
}

// Starlight's route id: '' or 'index' for Start, 'pl' or 'pl/index' for the Polish Start, 'fit', 'pl/fit', ...
export function slugOf(id) {
  const parts = String(id).split('/').filter(Boolean);
  if (parts[0] === 'pl') parts.shift();
  if (parts[parts.length - 1] === 'index') parts.pop();
  return parts.join('/');
}

export function langOf(id) {
  return String(id).split('/')[0] === 'pl' ? 'pl' : 'en';
}

// The same page in the other language: the footer's plain link, which works without JavaScript.
export function otherLanguagePath(id) {
  return pagePath(langOf(id) === 'pl' ? 'en' : 'pl', slugOf(id));
}
```

- [ ] **Step 4: Write site/src/lib/versions.mjs**

```js
// Pure functions over versions.json (the output of release-notes.py --all-json). Shared by the components and the tests.
const MONTHS = {
  en: ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'],
  pl: ['sty', 'lut', 'mar', 'kwi', 'maj', 'cze', 'lip', 'sie', 'wrz', 'paź', 'lis', 'gru'],
};
const FENCE = String.fromCharCode(96).repeat(3);
const BT = String.fromCharCode(96);
const CODE_SPAN = new RegExp(BT + BT + '.*?' + BT + BT + '|' + BT + '[^' + BT + ']*' + BT, 'g');

export const FULL_CHANGELOG = 'Full changelog: ';

// '2026-11-24' -> '24 Nov 2026' (en) or '24 lis 2026' (pl): from the string, never through Date or Intl.
export function formatDate(date, lang) {
  const found = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(date));
  const months = MONTHS[lang];
  if (!found || !months) throw new Error('formatDate: expected YYYY-MM-DD and en or pl, got ' + JSON.stringify(date) + ' and ' + JSON.stringify(lang));
  const month = Number(found[2]);
  const day = Number(found[3]);
  if (month < 1 || month > 12 || day < 1 || day > 31) throw new Error('formatDate: no such date ' + date);
  return day + ' ' + months[month - 1] + ' ' + found[1];
}

// The server versions, in the order of the file (newest first).
export function serverVersions(entries) {
  return entries.filter((entry) => entry.kind === 'server');
}

function trimBlank(lines) {
  let start = 0;
  let end = lines.length;
  while (start < end && !lines[start].trim()) start++;
  while (end > start && !lines[end - 1].trim()) end--;
  return lines.slice(start, end);
}

// body: the text release-notes.py prints for a tag. Returns
//   upgrading: the lines after '### Upgrading' up to the next '### ' heading or the Full changelog line ('' when none);
//   rest: the body without the Upgrading subsection and without the Full changelog line;
//   fullChangelog: the Full changelog line.
export function splitBody(body) {
  const lines = String(body).split('\n');
  const last = lines.findLastIndex((line) => line.startsWith(FULL_CHANGELOG));
  if (last < 0) throw new Error('splitBody: the body has no line starting with "' + FULL_CHANGELOG + '"');
  const fullChangelog = lines[last];
  const content = lines.slice(0, last);
  const start = content.findIndex((line) => line.trim() === '### Upgrading');
  if (start < 0) return { upgrading: '', rest: trimBlank(content).join('\n'), fullChangelog };
  let end = content.findIndex((line, index) => index > start && line.startsWith('### '));
  if (end < 0) end = content.length;
  return {
    upgrading: trimBlank(content.slice(start + 1, end)).join('\n'),
    rest: trimBlank([...content.slice(0, start), ...content.slice(end)]).join('\n'),
    fullChangelog,
  };
}

// The first sentence: up to the first '.', '!' or '?' that a space or the end follows, so '0.4.0' does not end it.
export function firstSentence(text) {
  const trimmed = String(text).trim();
  const found = /^.*?(?:\.|!|\?)(?=\s|$)/s.exec(trimmed);
  return found ? found[0] : trimmed;
}

// The raw HTML tags of a changelog body outside fenced blocks, code spans and autolinks; [] when none.
export function findRawHtml(markdown) {
  const found = [];
  let inFence = false;
  for (const line of String(markdown).split('\n')) {
    if (line.trim().startsWith(FENCE)) {
      inFence = !inFence;
      continue;
    }
    if (inFence) continue;
    const prose = line.replace(CODE_SPAN, '').replace(/<(?:https?:|mailto:)[^>\s]*>/g, '');
    for (const tag of prose.matchAll(/<\/?[A-Za-z][A-Za-z0-9-]*(?:\s[^<>]*)?\/?>/g)) found.push(tag[0]);
  }
  return found;
}
```

- [ ] **Step 5: Write site/scripts/checks.mjs**

```js
// The checks of the prebuild and postbuild steps. Pure functions (no file system, no process), tested in prebuild.test.mjs.
import { splitBody, findRawHtml, serverVersions } from '../src/lib/versions.mjs';

export const PYTHONS = [['python3'], ['python'], ['py', '-3']];
export const SUMMARY_LIMIT = 300;
export const SUMMARY_FILES = { pl: 'src/data/version-summaries.pl.json', en: 'src/data/version-summaries.en.json' };

// result: what spawnSync returned. 'ok', or the cause in a few words.
export function outcomeOf(result) {
  if (result.error) return result.error.code === 'ENOENT' ? 'not found' : 'did not start (' + result.error.message + ')';
  const out = String(result.stdout || '');
  if (result.status === 0 && out.split(/\r?\n/).includes('self-test ok')) return 'ok';
  const lines = (String(result.stderr || '') + '\n' + out).split(/\r?\n/).filter((line) => line.trim()).slice(0, 3);
  return 'exit ' + result.status + (lines.length ? ': ' + lines.join(' | ') : '');
}

// run(command, args) -> a spawnSync result. The first interpreter of PYTHONS whose run of script --self-test prints
// 'self-test ok'. Returns { python: ['py', '-3'] or null, tried: ['python3: not found', ...] }.
export function pickPython(run, script) {
  const tried = [];
  for (const candidate of PYTHONS) {
    const outcome = outcomeOf(run(candidate[0], [...candidate.slice(1), script, '--self-test']));
    if (outcome === 'ok') return { python: candidate, tried };
    tried.push(candidate.join(' ') + ': ' + outcome);
  }
  return { python: null, tried };
}

// versions: the entries of versions.json; files: { pl, en }, each the parsed JSON of its file.
export function checkSummaries(versions, files) {
  const server = serverVersions(versions).map((entry) => entry.version);
  const problems = [];
  for (const lang of ['pl', 'en']) {
    const name = SUMMARY_FILES[lang];
    const summaries = files[lang];
    if (summaries === null || typeof summaries !== 'object' || Array.isArray(summaries)
      || Object.values(summaries).some((text) => typeof text !== 'string')) {
      problems.push(name + ': not an object from version to text');
      continue;
    }
    for (const version of server) {
      if (!Object.hasOwn(summaries, version)) problems.push(name + ': no summary for ' + version);
    }
    for (const [version, text] of Object.entries(summaries)) {
      const length = [...text].length;
      if (!server.includes(version)) problems.push(name + ': ' + version + ' is not a server version of CHANGELOG.md');
      else if (!text.trim()) problems.push(name + ': the summary of ' + version + ' is empty');
      else if (length > SUMMARY_LIMIT) problems.push(name + ': the summary of ' + version + ' has ' + length + ' characters, more than ' + SUMMARY_LIMIT);
    }
  }
  return problems;
}

// paths: every entry under src/content/docs/, relative to it, with forward slashes ('fit.mdx', 'pl/fit.mdx', 'pl').
export function checkParity(paths) {
  const pages = paths.filter((path) => /\.mdx?$/.test(path));
  const english = pages.filter((path) => !path.startsWith('pl/'));
  const polish = pages.filter((path) => path.startsWith('pl/')).map((path) => path.slice(3));
  const problems = [];
  for (const path of english) {
    if (!polish.includes(path)) problems.push('src/content/docs/' + path + ' has no Polish page src/content/docs/pl/' + path);
  }
  for (const path of polish) {
    if (!english.includes(path)) problems.push('src/content/docs/pl/' + path + ' has no English page src/content/docs/' + path);
  }
  return problems.sort();
}

// Every server body has its Full changelog line and no raw HTML tag (the bodies are rendered unsanitised).
export function checkBodies(versions) {
  const problems = [];
  for (const entry of serverVersions(versions)) {
    try {
      splitBody(entry.body);
    } catch (error) {
      problems.push('versions.json: ' + entry.tag + ': ' + error.message);
      continue;
    }
    for (const tag of findRawHtml(entry.body)) problems.push('versions.json: ' + entry.tag + ': raw HTML ' + tag + ' in the changelog section');
  }
  return problems;
}

// pages: { 'fit/index.html': html, ... } relative to dist/; exists(path): dist/<path> is a file; base: '/Auth-Core/'.
// Every href and src that stays on the site must stay under the base path and lead to a file.
export function brokenLinks(pages, exists, base) {
  const problems = [];
  const root = base.replace(/\/$/, '');
  for (const [file, html] of Object.entries(pages)) {
    for (const found of html.matchAll(/\s(?:href|src)="([^"]*)"/g)) {
      const target = found[1].replaceAll('&amp;', '&');
      if (!target || target.startsWith('#') || target.startsWith('//') || /^[a-z][a-z0-9+.-]*:/i.test(target)) continue;
      let path = decodeURIComponent(new URL(target, 'https://site.invalid' + base + file).pathname);
      if (path === root) path = base;
      if (!path.startsWith(base)) {
        problems.push(file + ': the link ' + found[1] + ' is outside ' + base);
        continue;
      }
      const rest = path.slice(base.length);
      const candidates = rest === '' || rest.endsWith('/') ? [rest + 'index.html'] : [rest, rest + '/index.html', rest + '.html'];
      if (!candidates.some(exists)) problems.push(file + ': the link ' + found[1] + ' leads to no file');
    }
  }
  return problems;
}
```

- [ ] **Step 6: Run the tests and see them pass**

Run: `cd site && node --test scripts/*.test.mjs`
Expected: `# pass 32`, `# fail 0`.

- [ ] **Step 7: Write site/scripts/prebuild.mjs and site/scripts/check-links.mjs**

```js
// prebuild (spec 0018): the parser's self-test, versions.json, the changelog bodies, the version summaries and the page
// parity. Each failure prints its cause and exits 1. npm runs it before astro dev and astro build (site/package.json).
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkBodies, checkParity, checkSummaries, pickPython, SUMMARY_FILES } from './checks.mjs';

const site = join(dirname(fileURLToPath(import.meta.url)), '..');
const root = join(site, '..');
const parser = 'scripts/release-notes.py';

function run(command, args) {
  return spawnSync(command, args, { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}

function fail(step, problems) {
  console.error('prebuild failed at ' + step + ':\n  ' + problems.join('\n  '));
  process.exit(1);
}

// 1. the parser's self-test, with the first Python that passes it
const { python, tried } = pickPython(run, parser);
if (!python) fail('the parser self-test (tried python3, python, py -3)', tried);

// 2. versions.json, from the same interpreter
const json = run(python[0], [...python.slice(1), parser, '--all-json']);
if (json.error || json.status !== 0) {
  fail('release-notes.py --all-json', [String(json.stderr || (json.error && json.error.message) || 'exit ' + json.status).trim()]);
}
let versions;
try {
  versions = JSON.parse(json.stdout);
} catch (error) {
  fail('release-notes.py --all-json', ['the output is not JSON: ' + error.message]);
}
const bodyProblems = checkBodies(versions);
if (bodyProblems.length) fail('the changelog bodies', bodyProblems);
mkdirSync(join(site, 'src', 'data'), { recursive: true });
writeFileSync(join(site, 'src', 'data', 'versions.json'), JSON.stringify(versions, null, 2) + '\n');

// 3. the version summaries
const files = {};
const readProblems = [];
for (const [lang, name] of Object.entries(SUMMARY_FILES)) {
  if (!existsSync(join(site, name))) {
    readProblems.push(name + ': the file is missing');
    continue;
  }
  try {
    files[lang] = JSON.parse(readFileSync(join(site, name), 'utf8'));
  } catch (error) {
    readProblems.push(name + ': not valid JSON (' + error.message + ')');
  }
}
if (readProblems.length) fail('the version summaries', readProblems);
const summaryProblems = checkSummaries(versions, files);
if (summaryProblems.length) fail('the version summaries', summaryProblems);

// 4. the page parity
const docs = join(site, 'src', 'content', 'docs');
const parityProblems = checkParity(readdirSync(docs, { recursive: true }).map((name) => String(name).split(sep).join('/')));
if (parityProblems.length) fail('the page parity', parityProblems);

const server = versions.filter((entry) => entry.kind === 'server').length;
console.log('prebuild ok: ' + python.join(' ') + ', ' + versions.length + ' versions (' + server + ' server), summaries and page parity checked');
```

```js
// postbuild (spec 0018): every href and src of the built pages that stays on the site leads to a file of dist/. This
// covers the links that components write, which starlight-links-validator does not see (it reads Markdown links only).
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { brokenLinks } from './checks.mjs';
import { BASE } from '../src/lib/paths.mjs';

const dist = join(dirname(fileURLToPath(import.meta.url)), '..', 'dist');
const names = readdirSync(dist, { recursive: true }).map((name) => String(name).split(sep).join('/'));
const pages = {};
for (const name of names.filter((item) => item.endsWith('.html'))) pages[name] = readFileSync(join(dist, name), 'utf8');
const exists = (path) => existsSync(join(dist, path)) && statSync(join(dist, path)).isFile();
const problems = brokenLinks(pages, exists, BASE);
if (problems.length) {
  console.error('check-links failed:\n  ' + problems.join('\n  '));
  process.exit(1);
}
const links = Object.values(pages).reduce((sum, html) => sum + [...html.matchAll(/\s(?:href|src)="/g)].length, 0);
console.log('check-links ok: ' + Object.keys(pages).length + ' pages, ' + links + ' links');
```

- [ ] **Step 8: Write the summaries (Appendix B of the spec, verbatim; owner approves before the merge)**

site/src/data/version-summaries.pl.json:

```json
{
  "0.4.0": "Logowanie przez GitHub i ekran ustawień, w którym osoba podłącza i odłącza metody logowania oraz zmienia hasło.",
  "0.3.0": "Jedno konto w kilku firmach i przełączanie między nimi bez ponownego logowania. Zalogowana osoba może założyć kolejną firmę, gdy produkt to włączy, a przyjęcie zaproszenia zachowuje jej pozostałe firmy.",
  "0.2.0": "Samodzielna rejestracja firmy, konta prywatne dla osób bez firmy oraz logowanie przez Google i Microsoft.",
  "0.1.2": "Poprawka: mail z zaproszeniem nie podaje już roli, bo pokazywał jej kod z manifestu zamiast nazwy.",
  "0.1.1": "Poprawka: ten sam kod z załatanymi pakietami zależnymi, dzięki czemu wersja znowu buduje się ze źródeł.",
  "0.1.0": "Pierwsze wydanie: usługa i jej obraz, pakiet Pythona dla backendów, przykład w Angularze i dokumenty do uruchomienia."
}
```

site/src/data/version-summaries.en.json:

```json
{
  "0.4.0": "Sign-in with GitHub, and a settings screen where a person connects and disconnects sign-in providers and changes the password.",
  "0.3.0": "One account in several companies, switching between them without signing in again. A signed-in person can create another company when the product turns it on, and accepting an invitation keeps the other companies.",
  "0.2.0": "Self-service sign-up for companies, private accounts for people without a company, and sign-in with Google and Microsoft.",
  "0.1.2": "A patch: the invitation mail no longer names the role, which showed the role's key from the manifest instead of a word.",
  "0.1.1": "A patch: the same code with patched transitive packages, so the version builds from source again.",
  "0.1.0": "The first release: the service and its image, a Python package for backends, an Angular sample for frontends, and the documents to run it."
}
```

Write both as UTF-8 without a byte-order mark, with a final newline.

- [ ] **Step 9: Wire the scripts into site/package.json**

The `scripts` object becomes:

```json
  "scripts": {
    "predev": "node scripts/prebuild.mjs",
    "dev": "astro dev",
    "prebuild": "node --test scripts/*.test.mjs && node scripts/prebuild.mjs",
    "build": "astro build",
    "postbuild": "node scripts/check-links.mjs",
    "preview": "astro preview"
  },
```

Node 24 expands the glob of `--test` itself, so the line also works where npm runs scripts through `cmd.exe`.

- [ ] **Step 10: Build**

Run: `cd site && npm run build`
Expected: `# fail 0`; `prebuild ok: python3, 7 versions (6 server), summaries and page parity checked` (on this Windows machine
`python` or `py -3` takes the place of `python3`); the validator's success line; `check-links ok: 11 pages, <n> links` (ten pages
and `404.html`); exit 0. `git status --porcelain` does not list site/src/data/versions.json.

- [ ] **Step 11: Teeth**

1. In checks.mjs change `length > SUMMARY_LIMIT` to `length > SUMMARY_LIMIT + 1`; run `node --test scripts/*.test.mjs`; expected
   `summaries: 300 characters pass, 301 fail` fails. Restore.
2. Delete the second `for` loop of `checkParity`; expected `parity: a missing English page is named` fails. Restore.
3. Delete the `if (!path.startsWith(base))` block of `brokenLinks`; expected `links: a component link without the base path is
   reported` fails. Restore.
4. In `PYTHONS` delete `['py', '-3']`; expected `python: the Windows stub of python3 and a missing python move on to py -3` fails.
   Restore; all tests pass.

- [ ] **Step 12: Commit**

```bash
git add site
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "feat: site build checks summaries, page parity and links"
```

---

### Task 5: The footer

**Files:**
- Create: `site/src/components/Footer.astro`
- Modify: site/astro.config.mjs (a `components` entry), `site/src/styles/theme.css` (append)

**Interfaces:**
- Consumes: `pagePath`, `otherLanguagePath` (Task 4); Starlight's `Astro.locals.starlightRoute` (`id`, `lang`, `editUrl`).
- Produces: the footer of every page, the Start pages included: the licence line of Appendix A with three links, "Edit this page on
  GitHub" / "Edytuj tę stronę na GitHubie", and the plain link to the same page in the other language. It replaces Starlight's footer
  (pagination and "last updated" are off in the config, so nothing of it is lost).

- [ ] **Step 1: Write `site/src/components/Footer.astro`**

```astro
---
// The footer of every page (spec 0018, Start, item 3, and Documents): the licence line, the edit link and a plain link
// to the same page in the other language, which works without JavaScript.
import { otherLanguagePath, pagePath } from '../lib/paths.mjs';

const route = Astro.locals.starlightRoute;
const lang = route.lang === 'pl' ? 'pl' : 'en';
const TEXT = {
  en: {
    server: 'Server: BSL 1.1.',
    open: 'Clients, samples and docs: MIT.',
    photo: 'City photo: Unsplash License.',
    edit: 'Edit this page on GitHub',
    other: 'Polski',
    otherLang: 'pl',
  },
  pl: {
    server: 'Serwer: BSL 1.1.',
    open: 'Klienty, przykłady i dokumentacja: MIT.',
    photo: 'Zdjęcie miasta: licencja Unsplash.',
    edit: 'Edytuj tę stronę na GitHubie',
    other: 'English',
    otherLang: 'en',
  },
};
const t = TEXT[lang];
const REPO = 'https://github.com/MckCieply/Auth-Core/blob/main/';
---

<footer class="ac-footer not-content">
  <p>
    <a href={REPO + 'LICENSE'}>{t.server}</a>
    <a href={pagePath(lang, 'license')}>{t.open}</a>
    <a href={REPO + 'site/CREDITS.md'}>{t.photo}</a>
  </p>
  <p>
    {route.editUrl && <a href={route.editUrl.href}>{t.edit}</a>}
    <a href={otherLanguagePath(route.id)} hreflang={t.otherLang} lang={t.otherLang}>{t.other}</a>
  </p>
</footer>
```

- [ ] **Step 2: Register it**

In site/astro.config.mjs, inside `starlight({ ... })`, after `lastUpdated: false,` add:

```js
      components: {
        Footer: './src/components/Footer.astro',
      },
```

- [ ] **Step 3: Style it (append to `site/src/styles/theme.css`)**

```css
/* Footer: muted text, links as 44 px targets (spec 0018, Accessibility). */
.ac-footer {
  margin-top: 3rem;
  padding-top: 1rem;
  border-top: 1px solid var(--ac-border);
  color: var(--ac-muted);
  font-size: var(--sl-text-sm);
}
.ac-footer p {
  display: flex;
  flex-wrap: wrap;
  gap: 0 1.25rem;
  margin: 0;
}
.ac-footer a {
  display: inline-flex;
  align-items: center;
  min-height: 44px;
  color: var(--ac-muted);
  text-decoration: underline;
  text-underline-offset: 0.2em;
}
.ac-footer a:hover {
  color: var(--ac-text);
}
```

- [ ] **Step 4: Build and read the output**

Run, in `site/`:

```bash
npm run build
grep -c 'class="ac-footer' dist/index.html dist/pl/index.html dist/fit/index.html dist/pl/versions/index.html
grep -o 'href="/Auth-Core/pl/fit/" hreflang="pl" lang="pl"' dist/fit/index.html
grep -o 'href="/Auth-Core/" hreflang="en" lang="en"' dist/pl/index.html
grep -o 'href="https://github.com/MckCieply/Auth-Core/edit/main/site/src/content/docs/pl/license.md"' dist/pl/license/index.html
grep -c 'Edytuj tę stronę na GitHubie' dist/pl/compare/index.html
```

Expected: the build exits 0 with `check-links ok`; each count is 1; each `grep -o` prints its link. If the Start pages (`dist/index.html`,
`dist/pl/index.html`) have no footer, stop and report: the splash template then needs another override, which is outside this task.
If the attribute order in the HTML differs, check the three attributes with three separate `grep` calls instead.

- [ ] **Step 5: Teeth**

In `Footer.astro` replace `pagePath(lang, 'license')` with `'/license/'`; run `npm run build`; expected exit 1 and lines like
`fit/index.html: the link /license/ is outside /Auth-Core/` from check-links. Restore; the build passes.

- [ ] **Step 6: Commit**

```bash
git add site
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "feat: site footer with licence line, edit link and language link"
```

---

### Task 6: Is it for me, English and Polish

**Files:**
- Create: `site/src/components/CurrentVersion.astro`
- Modify: `site/src/content/docs/fit.mdx`, `site/src/content/docs/pl/fit.mdx` (the bodies under the front matter of Task 3)

**Interfaces:**
- Consumes: site/src/data/versions.json and `serverVersions` (Task 4).
- Produces: `CurrentVersion.astro`, the newest server version as plain text (`0.4.0`).

- [ ] **Step 1: Check the five limits at the build commit**

Run: `grep -n "^3\. \|^4\. \|^13\. \|^15\. \|^17\. " docs/security/threat-model.md`
Expected: 3 "Many people behind one address share its limits; 30 logins a minute ...", 4 "A company admin holding every permission can
delete the company ...", 13 "A demoted or removed member keeps the token's permissions ... for up to 15 minutes ...", 15 "Another browser
tab stays usable for up to 10 minutes after sign-out ...", 17 "Safari on a real phone may treat cookies differently ...". If a number or
a figure differs, stop and report.

- [ ] **Step 2: Write `site/src/components/CurrentVersion.astro`**

```astro
---
// The newest server version of versions.json, as text (spec 0018, Is it for me, Status).
import versions from '../data/versions.json';
import { serverVersions } from '../lib/versions.mjs';

const latest = serverVersions(versions)[0];
---

{latest.version}
```

- [ ] **Step 3: Write the English body (append to `site/src/content/docs/fit.mdx` under the front matter)**

```mdx
import CurrentVersion from '../../components/CurrentVersion.astro';

## A good fit if

- **You build a product where companies, their members and roles matter.** Users belong to companies, with members, roles,
  invitations and a permission catalog that your product declares.
- **Your backend can check a JWT, in any language.** Access tokens are standard RS256 JWTs of 10 minutes that carry the user, the
  company, the roles and the permissions; any JWT library that reads a JWKS checks them offline.
- **You can run a Linux server with Docker, a domain and an SMTP relay.** Auth-Core runs on a small x86-64 Linux VPS with Docker, and
  the repository has a production compose file, a deployment guide and runbooks.

## Look elsewhere if

- **You want ready-made login screens.** Auth-Core is headless, a REST API with no pages: your frontend draws the sign-in, password
  reset and invitation screens.
- **You need MFA today.** MFA (TOTP) and passkeys are planned, with no date. Until then sign-in is a password, or Google, Microsoft or
  GitHub.
- **Several products must share one pool of users.** There is no SSO between products: each product has its own instance and its own
  users.
- **You need SAML, SCIM or LDAP.** They are not in scope, and they never will be.
- **You need a multi-realm mode.** It is not in scope, and it never will be.
- **You need fine-grained resource permissions.** "Is this my document?" stays in your product.
- **Your backend is TypeScript and wants auth as a library inside the app.** Auth-Core runs apart from your app, as its own container
  with its own database.
- **You want a managed cloud service.** Auth-Core is self-hosted: it runs on your server.

## What you need to run it

- A Linux x86-64 server with Docker.
- A domain, and a reverse proxy in front of Auth-Core that ends TLS.
- PostgreSQL 16, from the production compose file: one container and one database.
- An SMTP relay with TLS (Auth-Core refuses to start without it) and a sender address on a domain you control.
- A nightly backup, and a restore that you have tried.
- Your own sign-in, password reset and invitation screens.
- Upgrades: read the changelog, back up, set the new version, pull and start. The service migrates its database at start; a rollback
  is a restore of the backup.

## Status

- Version <CurrentVersion />. Auth-Core runs in production in Toviq. One author.
- There has been no external audit yet. What the design defends against, and the risks that are accepted, are in the
  [threat model](https://github.com/MckCieply/Auth-Core/blob/main/docs/security/threat-model.md).
- The features have end-to-end suites that run against a real stack.

## Known limits

Five of the accepted risks in the threat model's
[list of residual risks](https://github.com/MckCieply/Auth-Core/blob/main/docs/security/threat-model.md#residual-risks):

- A member who is removed or demoted keeps the permissions of their token at your endpoints for up to 15 minutes: the 10 minutes of
  the token and 5 minutes of clock skew (residual risk 13).
- Another open browser tab stays usable for up to 10 minutes after sign-out (residual risk 15).
- Many people behind one address share its limits: 30 sign-ins a minute is the ceiling for an office behind one NAT (residual risk 3).
- A company admin who holds every permission can delete the company, and only the backup brings it back (residual risk 4).
- Safari on a real phone may treat cookies differently from the WebKit of the browser tests; the test on a real phone is still to come
  (residual risk 17).

## Next

The [comparison](/Auth-Core/compare/ "Comparison") with Better Auth, Keycloak, Clerk and WorkOS, the
[license and the price](/Auth-Core/license/ "License"), and [every version](/Auth-Core/versions/ "Versions").
```

- [ ] **Step 4: Write the Polish body (append to `site/src/content/docs/pl/fit.mdx`)**

```mdx
import CurrentVersion from '../../../components/CurrentVersion.astro';

## Pasuje, jeśli

- **Budujesz produkt, w którym liczą się firmy, ich członkowie i role.** Użytkownicy należą do firm, z członkami, rolami,
  zaproszeniami i katalogiem uprawnień, który deklaruje Twój produkt.
- **Twój backend umie sprawdzić JWT, w dowolnym języku.** Access tokeny to standardowe JWT RS256 ważne 10 minut, które niosą
  użytkownika, firmę, role i uprawnienia; każda biblioteka JWT, która czyta JWKS, sprawdza je offline.
- **Możesz utrzymać serwer z Linuksem, Dockerem, domeną i SMTP.** Auth-Core działa na małym VPS z Linuksem x86-64 i Dockerem, a
  repozytorium ma produkcyjny plik compose, przewodnik wdrożenia i runbooki.

## Szukaj gdzie indziej, jeśli

- **Chcesz gotowych ekranów logowania.** Auth-Core jest headless, to REST API bez stron: ekrany logowania, resetu hasła i zaproszeń
  rysuje Twój frontend.
- **Potrzebujesz MFA już dziś.** MFA (TOTP) i passkeys są w planach, bez daty. Do tego czasu logowanie to hasło albo Google, Microsoft
  lub GitHub.
- **Kilka produktów ma dzielić jedną pulę użytkowników.** Nie ma SSO między produktami: każdy produkt ma własną instancję i własnych
  użytkowników.
- **Potrzebujesz SAML, SCIM albo LDAP.** Nie są w zakresie i nigdy nie będą.
- **Potrzebujesz trybu wielu realmów.** Nie jest w zakresie i nigdy nie będzie.
- **Potrzebujesz szczegółowych uprawnień do zasobów.** „Czy to mój dokument?” zostaje w Twoim produkcie.
- **Twój backend jest w TypeScripcie i chce auth jako biblioteki w aplikacji.** Auth-Core działa osobno, jako własny kontener z własną
  bazą.
- **Chcesz usługi zarządzanej w chmurze.** Auth-Core hostujesz sam: działa na Twoim serwerze.

## Czego potrzebujesz, żeby go uruchomić

- Serwera z Linuksem x86-64 i Dockerem.
- Domeny i reverse proxy przed Auth-Core, który kończy TLS.
- PostgreSQL 16 z produkcyjnego pliku compose: jeden kontener i jedna baza.
- Serwera SMTP z TLS (bez niego Auth-Core nie wystartuje) i adresu nadawcy w domenie, którą zarządzasz.
- Nocnej kopii zapasowej i sprawdzonego odtwarzania.
- Własnych ekranów logowania, resetu hasła i zaproszeń.
- Aktualizacji: przeczytaj changelog, zrób kopię, ustaw nową wersję, pobierz i uruchom. Usługa migruje bazę przy starcie; powrót do
  poprzedniej wersji to odtworzenie kopii.

## Stan

- Wersja <CurrentVersion />. Auth-Core działa produkcyjnie w Toviq. Jeden autor.
- Nie było jeszcze zewnętrznego audytu. Przed czym broni projekt i jakie ryzyka są zaakceptowane, opisuje
  [model zagrożeń](https://github.com/MckCieply/Auth-Core/blob/main/docs/security/threat-model.md).
- Funkcje mają testy end-to-end, które działają na prawdziwym stosie.

## Znane ograniczenia

Pięć zaakceptowanych ryzyk z
[listy ryzyk rezydualnych](https://github.com/MckCieply/Auth-Core/blob/main/docs/security/threat-model.md#residual-risks) modelu
zagrożeń:

- Usunięty albo zdegradowany członek zachowuje uprawnienia swojego tokenu w Twoich endpointach do 15 minut: 10 minut ważności tokenu i
  5 minut tolerancji zegara (ryzyko 13).
- Inna otwarta karta przeglądarki działa do 10 minut po wylogowaniu (ryzyko 15).
- Wiele osób za jednym adresem dzieli jego limity: 30 logowań na minutę to sufit dla biura za jednym NAT (ryzyko 3).
- Admin firmy, który ma wszystkie uprawnienia, może usunąć firmę, a przywraca ją tylko kopia zapasowa (ryzyko 4).
- Safari na prawdziwym telefonie może traktować ciasteczka inaczej niż WebKit w testach przeglądarkowych; test na prawdziwym telefonie
  jest jeszcze przed nami (ryzyko 17).

## Dalej

[Porównanie](/Auth-Core/pl/compare/ "Porównanie") z Better Auth, Keycloak, Clerk i WorkOS,
[licencja i cena](/Auth-Core/pl/license/ "Licencja") oraz [wszystkie wersje](/Auth-Core/pl/versions/ "Wersje").
```

- [ ] **Step 5: Build and read the output**

Run, in `site/`: `npm run build && grep -c "Version 0.4.0" dist/fit/index.html && grep -c "Wersja 0.4.0" dist/pl/fit/index.html`
Expected: the build exits 0 (validator and check-links ok); both counts at least 1. If the version renders with spaces around it
(`Version  0.4.0`), keep the component on one line and check with `grep -c "0.4.0"` plus a look at the page in `npm run preview`.

- [ ] **Step 6: Teeth**

Change the English link `/Auth-Core/compare/` to `/Auth-Core/comparison/`; run `npm run build`; expected exit 1, the validator naming
`fit` and the link. Restore; the build passes.

- [ ] **Step 7: Commit**

```bash
git add site
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "feat: site page is it for me, english and polish"
```

---

### Task 7: Comparison, English and Polish

**Files:**
- Modify: site/src/content/docs/compare.md, site/src/content/docs/pl/compare.md (the bodies)

**Interfaces:**
- Consumes: the README's "Auth-Core or something else" (lines 47-58) at the build commit; the vendors' pages.
- Produces: the Comparison page; its "Checked" date is the day the implementer read the sources.

- [ ] **Step 1: Read every source and settle each cell**

The facts table below is a draft written from the README and the vendors' public descriptions; the source URLs were not opened when
the plan was written. Open every URL (Better Auth 5, Keycloak 3, Clerk 6, WorkOS 6). For each cell: when the page states it, keep it
(or correct it to what the page says); when the URL has moved, use the vendor page that now states the fact and change the footnote;
when no page of the vendor states it, write `not stated (read: <the page read>)` in the cell. Keep "how you pay" in words, never a
price (Settled 3). Note the date of reading; it replaces `26 Nov 2026` below. If a cell needs more than a short phrase to be true,
stop and report.

- [ ] **Step 2: Write the English body (under the front matter of Task 3)**

```md
The same alternatives as the README, in the same order.

## When to pick which

|  | Pick it when | Auth-Core instead when |
|---|---|---|
| **Better Auth** | Your backend is TypeScript and you want auth as a library inside the app, with 2FA and passkeys. | Your backend is in another language, or you want auth apart from the app, with its own database. |
| **Keycloak** | You need SSO across products, SAML or LDAP, or the broadest set of standards. | You want a small service for one product, with companies and roles built in. |
| **Clerk** | You want ready-made sign-in screens and MFA today, as a service. | You want to host it yourself, keep the users' data on your server, and pay a flat yearly price. |
| **WorkOS** | You sell to enterprises and need SSO and directory sync per customer. | Your customers sign in with a password or Google, Microsoft or GitHub, and you want to host it yourself. |
| **Auth-Core** | You want one self-hosted service per product, isolated from the app, any backend language. | |

Auth-Core does not fit when you need MFA today, SSO across products, SAML or SCIM, ready-made screens, or a TypeScript backend that
wants auth inside the app.

## The facts

|  | How it runs | Licence | Companies and roles | Sign-in screens | MFA today | SSO between products, SAML | How you pay |
|---|---|---|---|---|---|---|---|
| **Better Auth** | a library inside your app (TypeScript)[^ba-docs] | MIT[^ba-licence] | a plug-in[^ba-org] | your own[^ba-docs] | yes, a plug-in[^ba-2fa] | a plug-in; SAML in it[^ba-sso] | free and open source[^ba-licence] |
| **Keycloak** | a service you host[^kc-home] | Apache 2.0[^kc-licence] | built in[^kc-admin] | hosted by the service you run, with themes[^kc-admin] | yes[^kc-admin] | yes; yes[^kc-home] | free and open source[^kc-licence] |
| **Clerk** | a hosted service[^cl-docs] | proprietary[^cl-terms] | built in[^cl-org] | ready-made components, or hosted pages[^cl-docs] | yes[^cl-mfa] | enterprise connections; SAML in them[^cl-sso] | per monthly active user[^cl-pricing] |
| **WorkOS** | a hosted service[^wo-docs] | proprietary[^wo-terms] | built in[^wo-org] | hosted by the vendor (AuthKit)[^wo-docs] | yes[^wo-mfa] | yes; yes[^wo-sso] | per monthly active user, and per connection[^wo-pricing] |
| **Auth-Core** | a service you host | BSL 1.1 | built in | your own | planned, no date | no; no | EUR 490 a year per product, flat |

The Auth-Core row is the README at the build commit.

Checked: 26 Nov 2026.

[^ba-docs]: Better Auth, documentation: <https://www.better-auth.com/docs/introduction>
[^ba-licence]: Better Auth, licence: <https://github.com/better-auth/better-auth/blob/main/LICENSE.md>
[^ba-org]: Better Auth, organization plug-in: <https://www.better-auth.com/docs/plugins/organization>
[^ba-2fa]: Better Auth, two-factor plug-in: <https://www.better-auth.com/docs/plugins/2fa>
[^ba-sso]: Better Auth, SSO plug-in: <https://www.better-auth.com/docs/plugins/sso>
[^kc-home]: Keycloak, home page: <https://www.keycloak.org/>
[^kc-licence]: Keycloak, licence: <https://github.com/keycloak/keycloak/blob/main/LICENSE.txt>
[^kc-admin]: Keycloak, Server Administration Guide: <https://www.keycloak.org/docs/latest/server_admin/index.html>
[^cl-docs]: Clerk, documentation: <https://clerk.com/docs>
[^cl-terms]: Clerk, terms of service: <https://clerk.com/legal/terms>
[^cl-org]: Clerk, organizations: <https://clerk.com/docs/organizations/overview>
[^cl-mfa]: Clerk, sign-up and sign-in options: <https://clerk.com/docs/authentication/configuration/sign-up-sign-in-options>
[^cl-sso]: Clerk, enterprise connections: <https://clerk.com/docs/authentication/enterprise-connections/overview>
[^cl-pricing]: Clerk, pricing: <https://clerk.com/pricing>
[^wo-docs]: WorkOS, AuthKit: <https://workos.com/docs/authkit>
[^wo-terms]: WorkOS, terms of service: <https://workos.com/legal/terms-of-service>
[^wo-org]: WorkOS, organizations: <https://workos.com/docs/authkit/organizations>
[^wo-mfa]: WorkOS, multi-factor authentication: <https://workos.com/docs/authkit/mfa>
[^wo-sso]: WorkOS, single sign-on: <https://workos.com/docs/sso>
[^wo-pricing]: WorkOS, pricing: <https://workos.com/pricing>

[Is it for me?](/Auth-Core/fit/ "Is it for me?") lists what Auth-Core needs and where it stops.
```

- [ ] **Step 3: Write the Polish body (under the front matter of Task 3)**

The same table cells as Step 2 after Step 1, translated; the footnote labels and URLs are the same.

```md
Te same alternatywy co w README, w tej samej kolejności.

## Kiedy co wybrać

|  | Wybierz, gdy | Auth-Core zamiast tego, gdy |
|---|---|---|
| **Better Auth** | Twój backend jest w TypeScripcie i chcesz auth jako biblioteki w aplikacji, z 2FA i passkeys. | Twój backend jest w innym języku albo chcesz auth osobno od aplikacji, z własną bazą. |
| **Keycloak** | Potrzebujesz SSO między produktami, SAML lub LDAP albo najszerszego zestawu standardów. | Chcesz małej usługi dla jednego produktu, z wbudowanymi firmami i rolami. |
| **Clerk** | Chcesz gotowych ekranów logowania i MFA już dziś, jako usługi. | Chcesz hostować sam, trzymać dane użytkowników na swoim serwerze i płacić stałą roczną cenę. |
| **WorkOS** | Sprzedajesz dużym firmom i potrzebujesz SSO oraz synchronizacji katalogu dla każdego klienta. | Twoi klienci logują się hasłem albo przez Google, Microsoft lub GitHub, a Ty chcesz hostować sam. |
| **Auth-Core** | Chcesz jednej usługi hostowanej u siebie na produkt, odseparowanej od aplikacji, z backendem w dowolnym języku. | |

Auth-Core nie pasuje, gdy potrzebujesz MFA już dziś, SSO między produktami, SAML lub SCIM, gotowych ekranów albo backendu w
TypeScripcie, który chce auth w aplikacji.

## Fakty

|  | Jak działa | Licencja | Firmy i role | Ekrany logowania | MFA dziś | SSO między produktami, SAML | Jak płacisz |
|---|---|---|---|---|---|---|---|
| **Better Auth** | biblioteka w Twojej aplikacji (TypeScript)[^ba-docs] | MIT[^ba-licence] | wtyczka[^ba-org] | Twoje własne[^ba-docs] | tak, wtyczka[^ba-2fa] | wtyczka; SAML w niej[^ba-sso] | darmowy i otwarty[^ba-licence] |
| **Keycloak** | usługa, którą hostujesz[^kc-home] | Apache 2.0[^kc-licence] | wbudowane[^kc-admin] | serwowane przez usługę, którą uruchamiasz, z motywami[^kc-admin] | tak[^kc-admin] | tak; tak[^kc-home] | darmowy i otwarty[^kc-licence] |
| **Clerk** | usługa hostowana[^cl-docs] | własnościowa[^cl-terms] | wbudowane[^cl-org] | gotowe komponenty albo strony hostowane[^cl-docs] | tak[^cl-mfa] | połączenia enterprise; SAML w nich[^cl-sso] | za aktywnego użytkownika miesięcznie[^cl-pricing] |
| **WorkOS** | usługa hostowana[^wo-docs] | własnościowa[^wo-terms] | wbudowane[^wo-org] | hostowane przez dostawcę (AuthKit)[^wo-docs] | tak[^wo-mfa] | tak; tak[^wo-sso] | za aktywnego użytkownika miesięcznie i za połączenie[^wo-pricing] |
| **Auth-Core** | usługa, którą hostujesz | BSL 1.1 | wbudowane | Twoje własne | w planach, bez daty | nie; nie | 490 EUR rocznie za produkt, stała cena |

Wiersz Auth-Core to README z commita, z którego zbudowano stronę.

Sprawdzono: 26 lis 2026.

[^ba-docs]: Better Auth, dokumentacja: <https://www.better-auth.com/docs/introduction>
[^ba-licence]: Better Auth, licencja: <https://github.com/better-auth/better-auth/blob/main/LICENSE.md>
[^ba-org]: Better Auth, wtyczka organization: <https://www.better-auth.com/docs/plugins/organization>
[^ba-2fa]: Better Auth, wtyczka two-factor: <https://www.better-auth.com/docs/plugins/2fa>
[^ba-sso]: Better Auth, wtyczka SSO: <https://www.better-auth.com/docs/plugins/sso>
[^kc-home]: Keycloak, strona główna: <https://www.keycloak.org/>
[^kc-licence]: Keycloak, licencja: <https://github.com/keycloak/keycloak/blob/main/LICENSE.txt>
[^kc-admin]: Keycloak, Server Administration Guide: <https://www.keycloak.org/docs/latest/server_admin/index.html>
[^cl-docs]: Clerk, dokumentacja: <https://clerk.com/docs>
[^cl-terms]: Clerk, warunki korzystania: <https://clerk.com/legal/terms>
[^cl-org]: Clerk, organizacje: <https://clerk.com/docs/organizations/overview>
[^cl-mfa]: Clerk, opcje rejestracji i logowania: <https://clerk.com/docs/authentication/configuration/sign-up-sign-in-options>
[^cl-sso]: Clerk, połączenia enterprise: <https://clerk.com/docs/authentication/enterprise-connections/overview>
[^cl-pricing]: Clerk, cennik: <https://clerk.com/pricing>
[^wo-docs]: WorkOS, AuthKit: <https://workos.com/docs/authkit>
[^wo-terms]: WorkOS, warunki korzystania: <https://workos.com/legal/terms-of-service>
[^wo-org]: WorkOS, organizacje: <https://workos.com/docs/authkit/organizations>
[^wo-mfa]: WorkOS, uwierzytelnianie wieloskładnikowe: <https://workos.com/docs/authkit/mfa>
[^wo-sso]: WorkOS, single sign-on: <https://workos.com/docs/sso>
[^wo-pricing]: WorkOS, cennik: <https://workos.com/pricing>

[Czy to dla mnie?](/Auth-Core/pl/fit/ "Czy to dla mnie?") mówi, czego Auth-Core potrzebuje i gdzie się kończy.
```

- [ ] **Step 4: Build and read the output**

Run, in `site/`: `npm run build && grep -o 'data-footnote-ref' dist/compare/index.html | wc -l && grep -c "Checked:" dist/compare/index.html`
Expected: exit 0; 30 footnote references (one per competitor cell: 4 rows of 7 cells, plus 2 cells that carry two facts, adjusted to the
final table: the count equals the number of `[^` references in the English table rows); `Checked:` once. If the validator reports the
footnote anchors (`#user-content-fn-...`) as invalid links, stop and report.

- [ ] **Step 5: Teeth**

Delete the Polish file's last link title (`"Czy to dla mnie?"`) and change its target to /Auth-Core/pl/fitt/; run
`npm run build`; expected exit 1, the validator naming `pl/compare`. Restore; the build passes.

- [ ] **Step 6: Commit**

```bash
git add site
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "feat: site page comparison with sourced facts, english and polish"
```

---

### Task 8: License, English and Polish

**Files:**
- Modify: site/src/content/docs/license.md, site/src/content/docs/pl/license.md (the bodies)

**Interfaces:**
- Consumes: the README's "License" section and line 9, and the Change Date rule of `LICENSE`, at the build commit.
- Produces: the License page, which the footer and the Start page's licence card link to.

- [ ] **Step 1: Write the English body (under the front matter of Task 3)**

```md
Auth-Core's server is source-available under the Business Source License 1.1. It is not open source.

## In plain words

- Development, testing and evaluation: free.
- A personal, non-commercial project in production: free.
- A non-profit organisation or an educational institution, for non-commercial purposes: free.
- A company in production, even for internal use: needs a commercial licence.
- A company evaluating Auth-Core before buying: free.

## Price

EUR 490 a year per product, net of VAT, flat, for any number of users. It covers production use, security fixes and best-effort email
support, with a reply usually within two working days. To buy a licence, write to [kontakt@toviq.pl](mailto:kontakt@toviq.pl).

## What stays MIT

- `clients/`, `samples/`, `docs/` and `site/` are under the MIT License, with a `LICENSE` file in each.
- `v0.1.0` and everything published before the licence change stay under the MIT License.

## Change to Apache 2.0

Each version becomes available under the Apache License 2.0 three years after it is first published. The day a version was first
published can differ from the date that the [Versions](/Auth-Core/versions/ "Versions") page shows, so this page states the rule and
no dates.

## The licence text

Read the [full LICENSE](https://github.com/MckCieply/Auth-Core/blob/main/LICENSE). The licence text decides; this page only explains it.
```

- [ ] **Step 2: Write the Polish body (under the front matter of Task 3)**

```md
Serwer Auth-Core ma kod dostępny na licencji Business Source License 1.1. To nie jest open source.

## Prostymi słowami

- Development, testy i ocena: za darmo.
- Osobisty projekt bez celów komercyjnych, produkcyjnie: za darmo.
- Organizacja non-profit albo instytucja edukacyjna, w celach niekomercyjnych: za darmo.
- Firma produkcyjnie, nawet do użytku wewnętrznego: potrzebuje licencji komercyjnej.
- Firma, która ocenia Auth-Core przed zakupem: za darmo.

## Cena

490 EUR netto rocznie za produkt, stała cena, dowolna liczba użytkowników. Obejmuje użycie produkcyjne, poprawki bezpieczeństwa i
wsparcie mailowe w miarę możliwości, z odpowiedzią zwykle w ciągu dwóch dni roboczych. Licencję kupisz, pisząc na
[kontakt@toviq.pl](mailto:kontakt@toviq.pl).

## Co zostaje na MIT

- `clients/`, `samples/`, `docs/` i `site/` są na licencji MIT, każdy z własnym plikiem `LICENSE`.
- `v0.1.0` i wszystko, co wydano przed zmianą licencji, zostaje na licencji MIT.

## Przejście na Apache 2.0

Każda wersja przechodzi na Apache License 2.0 trzy lata po pierwszej publikacji. Dzień pierwszej publikacji może się różnić od daty,
którą pokazuje strona [Wersje](/Auth-Core/pl/versions/ "Wersje"), więc ta strona podaje regułę, a nie daty.

## Tekst licencji

Przeczytaj [pełny LICENSE](https://github.com/MckCieply/Auth-Core/blob/main/LICENSE). Rozstrzyga tekst licencji; ta strona go tylko
objaśnia.
```

- [ ] **Step 3: Compare with the sources**

Run: `grep -n "Change Date\|Change License" LICENSE && sed -n '231,245p' README.md`
Expected: the Change Date of `LICENSE` is the rule "three years after ... first publicly distributed" (or its wording) and the README's
six bullets match the page line by line. If `LICENSE` states a fixed date instead of the rule, stop and report.

- [ ] **Step 4: Build**

Run, in `site/`: `npm run build && grep -c 'href="mailto:kontakt@toviq.pl"' dist/license/index.html dist/pl/license/index.html`
Expected: exit 0; 1 for each file.

- [ ] **Step 5: Teeth**

In the English body change `/Auth-Core/versions/` to `/versions/`; run `npm run build`; expected exit 1, the validator naming the link
(a Markdown link without the base). Restore; the build passes.

- [ ] **Step 6: Commit**

```bash
git add site
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "feat: site page license in plain words, english and polish"
```

---

### Task 9: Versions, English and Polish

**Files:**
- Create: `site/src/components/VersionList.astro`
- Modify: `site/src/content/docs/versions.mdx`, `site/src/content/docs/pl/versions.mdx` (the bodies), `site/src/styles/theme.css`
  (append)

**Interfaces:**
- Consumes: site/src/data/versions.json (written by the prebuild, Task 4), both summary files, `formatDate`, `serverVersions`,
  `splitBody` (Task 4), `marked` 18.3.0, Starlight's `Aside`.
- Produces: `VersionList.astro` with the prop `lang` (`'en'` or `'pl'`); the heading ids `v0-4-0`, `v0-3-0`, ...; the CSS class
  `.ac-pill` (the "latest" pill, also used by the Start page in Task 10).

- [ ] **Step 1: Write `site/src/components/VersionList.astro`**

```astro
---
// The Versions page (spec 0018, Versions): every server version of versions.json, newest first. The bodies are the
// repository's own changelog (absolute links from --all-json; the prebuild refuses raw HTML in them), so they are
// rendered with marked and not sanitised.
import { Aside } from '@astrojs/starlight/components';
import { marked } from 'marked';
import versions from '../data/versions.json';
import summariesEn from '../data/version-summaries.en.json';
import summariesPl from '../data/version-summaries.pl.json';
import { formatDate, serverVersions, splitBody } from '../lib/versions.mjs';

interface Props {
  lang: 'en' | 'pl';
}

const { lang } = Astro.props;
const TEXT = {
  en: {
    latest: 'Latest',
    upgrading: 'Upgrading',
    release: 'GitHub Release',
    image: 'Image',
    python: 'The Python package auth-core-fastapi has its own releases:',
    pythonLink: 'the python-v releases on GitHub',
    note: '',
  },
  pl: {
    latest: 'Najnowsza',
    upgrading: 'Aktualizacja',
    release: 'Wydanie na GitHubie',
    image: 'Obraz',
    python: 'Pakiet Pythona auth-core-fastapi ma własne wydania:',
    pythonLink: 'wydania python-v na GitHubie',
    note: 'Szczegóły poniżej są po angielsku, jak w changelogu.',
  },
};
const t = TEXT[lang];
const summaries: Record<string, string> = lang === 'pl' ? summariesPl : summariesEn;
const entries = serverVersions(versions).map((entry, index) => {
  const parts = splitBody(entry.body);
  return {
    version: entry.version,
    id: 'v' + entry.version.replaceAll('.', '-'),
    date: formatDate(entry.date, lang),
    latest: index === 0,
    summary: summaries[entry.version],
    upgrading: parts.upgrading ? marked.parse(parts.upgrading, { async: false }) : '',
    rest: marked.parse(parts.rest, { async: false }),
    fullChangelog: marked.parseInline(parts.fullChangelog, { async: false }),
  };
});
---

<div class="ac-versions">
  {entries.map((entry) => (
    <section class="ac-version" aria-labelledby={entry.id}>
      <h2 id={entry.id}>
        {entry.version} · {entry.date}
        {entry.latest && <span class="ac-pill">{t.latest}</span>}
      </h2>
      <p class="ac-summary">{entry.summary}</p>
      {t.note && <p class="ac-note">{t.note}</p>}
      {entry.upgrading && (
        <Aside type="caution" title={t.upgrading}>
          <div lang={lang === 'pl' ? 'en' : undefined} set:html={entry.upgrading} />
        </Aside>
      )}
      <div lang={lang === 'pl' ? 'en' : undefined} set:html={entry.rest} />
      <p lang={lang === 'pl' ? 'en' : undefined} set:html={entry.fullChangelog} />
      <p class="ac-version-links">
        <a href={'https://github.com/MckCieply/Auth-Core/releases/tag/v' + entry.version}>{t.release}</a>
        <span>{t.image} <code>{'ghcr.io/mckcieply/auth-core:' + entry.version}</code></span>
      </p>
    </section>
  ))}
  <p class="ac-python">
    {t.python} <a href="https://github.com/MckCieply/Auth-Core/releases?q=python-v">{t.pythonLink}</a>
  </p>
</div>
```

- [ ] **Step 2: Write both bodies (under the front matter of Task 3)**

`site/src/content/docs/versions.mdx`:

```mdx
import VersionList from '../../components/VersionList.astro';

<VersionList lang="en" />
```

`site/src/content/docs/pl/versions.mdx`:

```mdx
import VersionList from '../../../components/VersionList.astro';

<VersionList lang="pl" />
```

- [ ] **Step 3: Style it (append to `site/src/styles/theme.css`)**

```css
/* Versions page and the "latest" pill (spec 0018, Versions; Settled 10: the pill is cyan). */
.ac-pill {
  display: inline-block;
  margin-inline-start: 0.5em;
  padding: 0.1em 0.6em;
  border-radius: 999px;
  background-color: var(--ac-accent);
  color: var(--ac-on-accent);
  font-family: var(--sl-font-mono);
  font-size: 0.7em;
  font-weight: 500;
  vertical-align: middle;
}
.ac-version {
  padding-top: 1.5rem;
  border-top: 1px solid var(--ac-border);
}
.ac-version:first-child {
  border-top: 0;
}
.ac-summary {
  font-size: var(--sl-text-lg);
}
.ac-note,
.ac-python {
  color: var(--ac-muted);
}
.ac-version-links {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0 1.5rem;
}
.ac-version-links a {
  display: inline-flex;
  align-items: center;
  min-height: 44px;
}
```

- [ ] **Step 4: Build and read the output**

Run, in `site/`:

```bash
npm run build
grep -o 'releases/tag/v' dist/versions/index.html | wc -l
grep -o 'id="v[0-9-]*"' dist/versions/index.html
grep -c 'starlight-aside--caution' dist/versions/index.html
grep -o 'ghcr.io/mckcieply/auth-core:0.1.0' dist/pl/versions/index.html
grep -c 'Szczegóły poniżej są po angielsku, jak w changelogu.' dist/pl/versions/index.html
grep -o '24 lis 2026\|24 Nov 2026' dist/versions/index.html dist/pl/versions/index.html
grep -c 'Unreleased' dist/versions/index.html
```

Expected: exit 0 with `check-links ok`; `6` release links (the bodies hold none, checked on the 7 release notes when the plan was
written); the ids `v0-4-0`, `v0-3-0`, `v0-2-0`, `v0-1-2`, `v0-1-1`, `v0-1-0` in that order; `3` caution asides (0.4.0, 0.3.0, 0.2.0
have `### Upgrading`); the image tag; `6` notes on the Polish page; `24 Nov 2026` in the English file and `24 lis 2026` in the Polish
one; `0` for `Unreleased`. Open `dist/versions/index.html` in `npm run preview` and check that each aside ends before the next `###`
heading of its entry (criterion 12).

- [ ] **Step 5: Teeth**

In `VersionList.astro` replace `formatDate(entry.date, lang)` with `formatDate(entry.date, 'de')`; run `npm run build`; expected the
build fails with `formatDate: expected YYYY-MM-DD and en or pl`. Restore; the build passes.

- [ ] **Step 6: Commit**

```bash
git add site
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "feat: site page versions from the changelog, english and polish"
```

---

### Task 10: Start, English and Polish

**Files:**
- Create: site/src/lib/start-copy.mjs, `site/src/components/Hero.astro`, `site/src/components/StartBento.astro`
- Modify: `site/src/content/docs/index.mdx`, `site/src/content/docs/pl/index.mdx` (the bodies), site/astro.config.mjs (the `Hero`
  override), `site/src/styles/theme.css` (append), site/scripts/prebuild.test.mjs (append two tests)

**Interfaces:**
- Consumes: versions.json, both summary files, `formatDate`, `serverVersions`, `firstSentence` (Task 4), `pagePath` (Task 4),
  `.ac-pill` (Task 9), the tokens (Task 3).
- Produces: `START_COPY` (`en`, `pl`), `QUICKSTART_URL`, `STEP_YAML` in start-copy.mjs; the classes `.ac-glass`, `.ac-hero`, `.ac-bento`,
  `.ac-card`, `.ac-button`; the `data-block` attributes `benefits`, `diagram`, `licence`, `fit`, `versions`, `steps` in this order.
  Task 11 puts the photo behind them.

- [ ] **Step 1: Write the failing tests (append to site/scripts/prebuild.test.mjs)**

An import declaration may stand anywhere at the top level of a module, so the two imports go with the tests.

```js
import { readFileSync as readText } from 'node:fs';
import { START_COPY, STEP_YAML } from '../src/lib/start-copy.mjs';

function shapeOf(value) {
  if (Array.isArray(value)) return value.map(shapeOf);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, shapeOf(value[key])]));
  }
  return typeof value;
}

test('start copy: English and Polish have the same keys and list lengths', () => {
  assert.deepEqual(shapeOf(START_COPY.pl), shapeOf(START_COPY.en));
});

test('start copy: the headline is the title of the Start page in both languages', () => {
  for (const [lang, file] of [['en', 'src/content/docs/index.mdx'], ['pl', 'src/content/docs/pl/index.mdx']]) {
    const title = /^title: (.*)$/m.exec(readText(file, 'utf8'));
    assert.equal(title && title[1], START_COPY[lang].headline, file);
  }
});

test('start copy: the YAML of step 1 is the README example', () => {
  assert.equal(STEP_YAML, [
    'permissions: [documents:read, documents:write, documents:approve]',
    'default_roles:',
    '  admin: ["*"]',
    '  user: [documents:read, documents:write]',
  ].join('\n'));
});
```

Run: `cd site && node --test scripts/*.test.mjs`
Expected: fail with `ERR_MODULE_NOT_FOUND` for site/src/lib/start-copy.mjs.

- [ ] **Step 2: Check the YAML against the README**

Run: `grep -n -A3 "^permissions: \[documents:read" README.md`
Expected: the four lines of `STEP_YAML` above, as they are. If they differ, the README wins: change the test and Step 3 to the README
text and list it under Deviations.

- [ ] **Step 3: Write site/src/lib/start-copy.mjs (Appendix A of the spec)**

```js
// The Start page copy of spec 0018, Appendix A, in English and Polish. The badge and the version lines are not here:
// they come from versions.json and the summary files (Settled 4).
export const QUICKSTART_URL = 'https://github.com/MckCieply/Auth-Core/blob/main/README.md#how-to-use-it';

export const STEP_YAML = [
  'permissions: [documents:read, documents:write, documents:approve]',
  'default_roles:',
  '  admin: ["*"]',
  '  user: [documents:read, documents:write]',
].join('\n');

export const START_COPY = {
  en: {
    latest: 'latest',
    headline: 'Sign-in for products used by many companies.',
    subline:
      'Auth-Core is a .NET service that runs next to your product, at /auth on your domain. It handles sign-in, password reset, ' +
      'invitations, companies and roles. Your backend checks its tokens offline, in any language.',
    fitButton: 'Check if it fits',
    quickstartButton: 'Quickstart on GitHub',
    factsLabel: 'Facts',
    facts: ['.NET 10', 'PostgreSQL', 'Docker, linux/amd64', 'RS256 JWT + JWKS', 'REST API, your own screens'],
    benefitsHeading: 'What you get',
    benefits: [
      {
        title: 'Login you build once.',
        text:
          'One container and a database give you sign-in, sessions, password reset, email verification, invitations, companies ' +
          'and roles. The same in every product.',
      },
      {
        title: 'Safe defaults.',
        text:
          'The refresh token sits in an HttpOnly cookie and rotates on every use. The access token lasts 10 minutes and lives in ' +
          'memory only. Lockout, rate limits and an audit log are on from the start.',
      },
      {
        title: 'Out of your API path.',
        text: 'Your backend checks each token against public keys it caches. Your API calls never pass through Auth-Core.',
      },
      {
        title: 'Your data, your server.',
        text:
          'Users stay in your PostgreSQL database and you pay no per-user fee. Sign-in with Google, Microsoft and GitHub is there ' +
          'when you turn it on.',
      },
    ],
    diagramHeading: 'How it sits next to your product',
    diagram: {
      frontend: { name: 'Your frontend', text: 'draws the login, reset and invitation screens' },
      proxy: { name: 'Your reverse proxy', text: 'HTTPS, one domain' },
      auth: { path: '/auth', name: 'Auth-Core', text: 'issues tokens, serves the keys, manages companies' },
      api: { path: '/api', name: 'your backend', text: 'checks the JWT offline' },
    },
    licenceHeading: 'License and price',
    licenceText:
      'Source-available under the Business Source License 1.1. Free for development, testing and evaluation, for personal ' +
      'non-commercial projects, and for non-commercial use by non-profit or educational organisations. A company running it in ' +
      'production, even internally, needs a commercial licence: EUR 490 a year per product, net of VAT, any number of users. Each ' +
      'version becomes Apache 2.0 three years after it is published.',
    licenceLink: 'Read the license in plain words',
    fitHeading: 'Is it for me?',
    goodFitHeading: 'A good fit if',
    goodFit: [
      'you build a product where companies, their members and roles matter',
      'your backend can check a JWT, in any language',
      'you can run a Linux server with Docker, a domain and an SMTP relay',
    ],
    elsewhereHeading: 'Look elsewhere if',
    elsewhere: [
      'you want ready-made login screens: your frontend draws them',
      'you need MFA today: it is planned, with no date',
      'several products must share one pool of users',
      'you need SAML, SCIM or LDAP',
    ],
    versionsHeading: 'Latest versions',
    versionsLink: 'All versions and the changelog',
    stepsHeading: 'Three steps to the first sign-in',
    steps: [
      {
        title: 'Declare permissions.',
        text: 'List the permissions your code checks and the roles a new company starts with, in one YAML file.',
      },
      {
        title: 'Run it on your server.',
        text:
          'A production compose file and a guide take you from a Linux server with Docker, a domain and an SMTP relay to a first ' +
          'sign-in.',
      },
      {
        title: 'Create the first company.',
        text: 'One command creates the company, a second invites its admin. The admin invites the rest.',
      },
    ],
  },
  pl: {
    latest: 'najnowsza',
    headline: 'Logowanie dla produktów, z których korzysta wiele firm.',
    subline:
      'Auth-Core to usługa w .NET, która działa obok Twojego produktu, pod /auth w Twojej domenie. Obsługuje logowanie, reset ' +
      'hasła, zaproszenia, firmy i role. Twój backend sprawdza jej tokeny offline, w dowolnym języku.',
    fitButton: 'Sprawdź, czy pasuje',
    quickstartButton: 'Szybki start na GitHubie',
    factsLabel: 'Fakty',
    facts: ['.NET 10', 'PostgreSQL', 'Docker, linux/amd64', 'RS256 JWT + JWKS', 'REST API, Twoje ekrany'],
    benefitsHeading: 'Co dostajesz',
    benefits: [
      {
        title: 'Logowanie budujesz raz.',
        text:
          'Jeden kontener i baza dają logowanie, sesje, reset hasła, weryfikację e-maila, zaproszenia, firmy i role. Tak samo w ' +
          'każdym produkcie.',
      },
      {
        title: 'Bezpieczne od startu.',
        text:
          'Refresh token siedzi w ciasteczku HttpOnly i zmienia się przy każdym użyciu. Access token żyje 10 minut, tylko w ' +
          'pamięci. Blokada konta, limity zapytań i log audytu działają od pierwszego dnia.',
      },
      {
        title: 'Poza ścieżką Twojego API.',
        text: 'Twój backend sprawdza każdy token kluczami publicznymi z cache. Wywołania Twojego API nie przechodzą przez Auth-Core.',
      },
      {
        title: 'Twoje dane, Twój serwer.',
        text:
          'Użytkownicy zostają w Twojej bazie PostgreSQL i nie płacisz za użytkownika. Logowanie przez Google, Microsoft i GitHub ' +
          'włączasz, kiedy chcesz.',
      },
    ],
    diagramHeading: 'Jak to stoi obok Twojego produktu',
    diagram: {
      frontend: { name: 'Twój frontend', text: 'rysuje ekrany logowania, resetu i zaproszeń' },
      proxy: { name: 'Twój reverse proxy', text: 'HTTPS, jedna domena' },
      auth: { path: '/auth', name: 'Auth-Core', text: 'wydaje tokeny, udostępnia klucze, zarządza firmami' },
      api: { path: '/api', name: 'Twój backend', text: 'sprawdza JWT offline' },
    },
    licenceHeading: 'Licencja i cena',
    licenceText:
      'Kod dostępny na licencji Business Source License 1.1. Za darmo do developmentu, testów i oceny, do projektów osobistych bez ' +
      'celów komercyjnych oraz do niekomercyjnego użytku organizacji non-profit i edukacyjnych. Firma, która uruchamia go ' +
      'produkcyjnie, nawet wewnętrznie, potrzebuje licencji komercyjnej: 490 EUR netto rocznie za produkt, dowolna liczba ' +
      'użytkowników. Każda wersja przechodzi na Apache 2.0 trzy lata po publikacji.',
    licenceLink: 'Licencja prostymi słowami',
    fitHeading: 'Czy to dla mnie?',
    goodFitHeading: 'Pasuje, jeśli',
    goodFit: [
      'budujesz produkt, w którym liczą się firmy, ich członkowie i role',
      'Twój backend umie sprawdzić JWT, w dowolnym języku',
      'możesz utrzymać serwer z Linuksem, Dockerem, domeną i SMTP',
    ],
    elsewhereHeading: 'Szukaj gdzie indziej, jeśli',
    elsewhere: [
      'chcesz gotowych ekranów logowania: rysuje je Twój frontend',
      'potrzebujesz MFA już dziś: jest w planach, bez daty',
      'kilka produktów ma dzielić jedną pulę użytkowników',
      'potrzebujesz SAML, SCIM albo LDAP',
    ],
    versionsHeading: 'Najnowsze wersje',
    versionsLink: 'Wszystkie wersje i changelog',
    stepsHeading: 'Trzy kroki do pierwszego logowania',
    steps: [
      {
        title: 'Zadeklaruj uprawnienia.',
        text: 'W jednym pliku YAML wypisz uprawnienia, które sprawdza Twój kod, i role, z którymi startuje nowa firma.',
      },
      {
        title: 'Uruchom na swoim serwerze.',
        text:
          'Gotowy plik compose i przewodnik prowadzą od serwera z Linuksem, Dockerem, domeną i SMTP do pierwszego logowania.',
      },
      {
        title: 'Załóż pierwszą firmę.',
        text: 'Jedno polecenie tworzy firmę, drugie zaprasza jej admina. Admin zaprasza resztę.',
      },
    ],
  },
};
```

Run: `cd site && node --test scripts/*.test.mjs`
Expected: `# fail 0`; the three new tests pass.

- [ ] **Step 4: Write `site/src/components/Hero.astro`**

```astro
---
// The Start page hero (spec 0018, Start, item 1): a glass card with the version badge, the headline, the subline, two
// buttons and the facts row. Replaces Starlight's Hero, which renders only on pages with "hero" in the front matter.
import versions from '../data/versions.json';
import { formatDate, serverVersions } from '../lib/versions.mjs';
import { pagePath } from '../lib/paths.mjs';
import { QUICKSTART_URL, START_COPY } from '../lib/start-copy.mjs';

const lang = Astro.locals.starlightRoute.lang === 'pl' ? 'pl' : 'en';
const c = START_COPY[lang];
const latest = serverVersions(versions)[0];
---

<div class="ac-hero not-content">
  <div class="ac-glass ac-hero-card">
    <p class="ac-badge">
      <span>{'v' + latest.version + ' · ' + formatDate(latest.date, lang) + ' ·'}</span>
      <span class="ac-pill">{c.latest}</span>
    </p>
    <h1 id="_top" data-page-title>{c.headline}</h1>
    <p class="ac-subline">{c.subline}</p>
    <div class="ac-buttons">
      <a class="ac-button ac-button-primary" href={pagePath(lang, 'fit')}>{c.fitButton}</a>
      <a class="ac-button" href={QUICKSTART_URL}>{c.quickstartButton}</a>
    </div>
    <ul class="ac-facts" aria-label={c.factsLabel}>
      {c.facts.map((fact) => <li>{fact}</li>)}
    </ul>
  </div>
</div>
```

- [ ] **Step 5: Write `site/src/components/StartBento.astro`**

```astro
---
// The Start page bento (spec 0018, Start, item 2), in the order of the spec. 12 columns from 64rem, one column below.
import versions from '../data/versions.json';
import summariesEn from '../data/version-summaries.en.json';
import summariesPl from '../data/version-summaries.pl.json';
import { firstSentence, formatDate, serverVersions } from '../lib/versions.mjs';
import { pagePath } from '../lib/paths.mjs';
import { START_COPY, STEP_YAML } from '../lib/start-copy.mjs';

interface Props {
  lang: 'en' | 'pl';
}

const { lang } = Astro.props;
const c = START_COPY[lang];
const summaries: Record<string, string> = lang === 'pl' ? summariesPl : summariesEn;
const latestThree = serverVersions(versions).slice(0, 3).map((entry) => ({
  label: 'v' + entry.version + ' · ' + formatDate(entry.date, lang),
  sentence: firstSentence(summaries[entry.version]),
}));
const d = c.diagram;
---

<div class="ac-bento not-content">
  <h2 class="ac-span-12" data-block="benefits">{c.benefitsHeading}</h2>
  {c.benefits.map((benefit) => (
    <div class="ac-card ac-glass ac-span-3">
      <h3>{benefit.title}</h3>
      <p>{benefit.text}</p>
    </div>
  ))}

  <div class="ac-card ac-glass ac-span-8" data-block="diagram">
    <h2>{c.diagramHeading}</h2>
    <ol class="ac-diagram">
      <li class="ac-node"><strong>{d.frontend.name}</strong>: {d.frontend.text}</li>
      <li class="ac-node">
        <strong>{d.proxy.name}</strong>: {d.proxy.text}
        <ul class="ac-routes">
          <li class="ac-node ac-node-accent">
            <code class="ac-path ac-path-accent">{d.auth.path}</code> <strong>{d.auth.name}</strong>: {d.auth.text}
          </li>
          <li class="ac-node">
            <code class="ac-path">{d.api.path}</code> <strong>{d.api.name}</strong>: {d.api.text}
          </li>
        </ul>
      </li>
    </ol>
  </div>

  <div class="ac-card ac-card-accent ac-span-4" data-block="licence">
    <h2>{c.licenceHeading}</h2>
    <p>{c.licenceText}</p>
    <a class="ac-target" href={pagePath(lang, 'license')}>{c.licenceLink}</a>
  </div>

  <h2 class="ac-span-12" data-block="fit">{c.fitHeading}</h2>
  <div class="ac-card ac-glass ac-span-6">
    <h3><a class="ac-target" href={pagePath(lang, 'fit')}>{c.goodFitHeading}</a></h3>
    <ul class="ac-list ac-list-ok">{c.goodFit.map((item) => <li>{item}</li>)}</ul>
  </div>
  <div class="ac-card ac-glass ac-span-6">
    <h3><a class="ac-target" href={pagePath(lang, 'fit')}>{c.elsewhereHeading}</a></h3>
    <ul class="ac-list ac-list-not-ok">{c.elsewhere.map((item) => <li>{item}</li>)}</ul>
  </div>

  <div class="ac-card ac-glass ac-span-7" data-block="versions">
    <h2>{c.versionsHeading}</h2>
    <ul class="ac-latest">
      {latestThree.map((entry) => (
        <li>
          <span class="ac-latest-label">{entry.label}</span>
          <span>{entry.sentence}</span>
        </li>
      ))}
    </ul>
    <a class="ac-target" href={pagePath(lang, 'versions')}>{c.versionsLink}</a>
  </div>

  <div class="ac-card ac-glass ac-span-5" data-block="steps">
    <h2>{c.stepsHeading}</h2>
    <ol class="ac-steps">
      {c.steps.map((step, index) => (
        <li>
          <strong>{step.title}</strong> {step.text}
          {index === 0 && <pre class="ac-code" lang="en"><code>{STEP_YAML}</code></pre>}
        </li>
      ))}
    </ol>
  </div>
</div>
```

- [ ] **Step 6: Write both bodies (under the front matter of Task 3)**

`site/src/content/docs/index.mdx`:

```mdx
import StartBento from '../../components/StartBento.astro';

<StartBento lang="en" />
```

`site/src/content/docs/pl/index.mdx`:

```mdx
import StartBento from '../../../components/StartBento.astro';

<StartBento lang="pl" />
```

- [ ] **Step 7: Register the Hero override**

In site/astro.config.mjs the `components` entry of Task 5 becomes:

```js
      components: {
        Footer: './src/components/Footer.astro',
        Hero: './src/components/Hero.astro',
      },
```

- [ ] **Step 8: Style it (append to `site/src/styles/theme.css`)**

```css
/* Start page (spec 0018, Start and Look). The splash page gets a wider content column for the 12-column bento. */
html[data-has-hero] {
  --sl-content-width: 76rem;
}

/* Glass: the solid surface first, the blur only where it is supported and transparency is not reduced (Settled 6). */
.ac-glass,
.header {
  background-color: var(--ac-surface);
}
.ac-glass {
  border: 1px solid var(--ac-glass-border);
  border-radius: var(--ac-radius);
}
@supports ((backdrop-filter: blur(18px)) or (-webkit-backdrop-filter: blur(18px))) {
  .ac-glass,
  .header {
    background-color: var(--ac-glass);
    -webkit-backdrop-filter: blur(18px);
    backdrop-filter: blur(18px);
  }
}
@media (prefers-reduced-transparency: reduce) {
  .ac-glass,
  .header {
    background-color: var(--ac-surface);
    -webkit-backdrop-filter: none;
    backdrop-filter: none;
  }
}

.ac-hero {
  padding-block: 4rem 2rem;
}
.ac-hero-card {
  max-width: 48rem;
  padding: clamp(1.25rem, 4vw, 2.5rem);
}
.ac-badge {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.25rem;
  margin: 0 0 1rem;
  padding: 0.25rem 0.75rem;
  border: 1px solid var(--ac-border);
  border-radius: 999px;
  color: var(--ac-text);
  font-family: var(--sl-font-mono);
  font-size: var(--sl-text-sm);
}
.ac-hero h1 {
  margin: 0;
  color: var(--ac-text);
  font-size: clamp(2rem, 6vw, 3.5rem);
  font-weight: 700;
  line-height: 1.1;
}
.ac-subline {
  margin: 1rem 0 0;
  color: var(--ac-text);
  font-size: var(--sl-text-lg);
}
.ac-buttons {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem;
  margin-top: 1.5rem;
}
.ac-button {
  display: inline-flex;
  align-items: center;
  min-height: 44px;
  padding: 0 1.25rem;
  border: 1.5px solid var(--ac-text);
  border-radius: 999px;
  color: var(--ac-text);
  font-weight: 500;
  text-decoration: none;
}
.ac-button-primary {
  border-color: var(--ac-accent);
  background-color: var(--ac-accent);
  color: var(--ac-on-accent);
}
.ac-facts {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem 1.25rem;
  margin: 1.5rem 0 0;
  padding: 0;
  list-style: none;
  color: var(--ac-muted);
  font-family: var(--sl-font-mono);
  font-size: var(--sl-text-sm);
}
.ac-facts li {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
}
.ac-facts li::before {
  content: '';
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background-color: var(--ac-accent);
}

/* Bento: one column, 12 from 64rem. */
.ac-bento {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 1rem;
  padding-bottom: 2rem;
}
.ac-bento > h2 {
  margin: 1.5rem 0 0;
}
@media (min-width: 64rem) {
  .ac-bento {
    grid-template-columns: repeat(12, minmax(0, 1fr));
  }
  .ac-span-3 { grid-column: span 3; }
  .ac-span-4 { grid-column: span 4; }
  .ac-span-5 { grid-column: span 5; }
  .ac-span-6 { grid-column: span 6; }
  .ac-span-7 { grid-column: span 7; }
  .ac-span-8 { grid-column: span 8; }
  .ac-span-12 { grid-column: span 12; }
}
.ac-card {
  padding: 1.25rem;
  color: var(--ac-text);
}
.ac-card h2,
.ac-card h3 {
  margin: 0 0 0.75rem;
  color: inherit;
}
.ac-card p {
  margin: 0 0 0.75rem;
}
.ac-card-accent {
  border-radius: var(--ac-radius);
  background-color: var(--ac-accent);
  color: var(--ac-on-accent);
}
.ac-card-accent a {
  color: var(--ac-on-accent);
}
.ac-card-accent a:focus-visible {
  outline-color: var(--ac-on-accent);
}
.ac-target {
  display: inline-flex;
  align-items: center;
  min-height: 44px;
  color: inherit;
  text-decoration: underline;
  text-underline-offset: 0.2em;
}

/* The diagram: lists, so a screen reader reads it as text. */
.ac-diagram,
.ac-routes {
  display: grid;
  gap: 0.75rem;
  margin: 0;
  padding: 0;
  list-style: none;
}
.ac-routes {
  margin-top: 0.75rem;
}
@media (min-width: 40rem) {
  .ac-routes {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
.ac-node {
  padding: 0.75rem;
  border: 1px solid var(--ac-border);
  border-radius: calc(var(--ac-radius) / 2);
  background-color: var(--ac-bg);
}
.ac-path {
  padding: 0.1em 0.5em;
  border-radius: 6px;
  background-color: var(--ac-surface);
  color: var(--ac-text);
}
.ac-path-accent {
  background-color: var(--ac-accent);
  color: var(--ac-on-accent);
}

.ac-list,
.ac-latest,
.ac-steps {
  display: grid;
  gap: 0.5rem;
  margin: 0 0 0.75rem;
  padding-inline-start: 1.25rem;
}
.ac-list-ok li::marker {
  color: var(--ac-ok);
}
.ac-list-not-ok li::marker {
  color: var(--ac-not-ok);
}
.ac-latest {
  padding: 0;
  list-style: none;
}
.ac-latest li {
  display: grid;
  gap: 0.125rem;
}
.ac-latest-label {
  font-family: var(--sl-font-mono);
  font-size: var(--sl-text-sm);
  color: var(--ac-muted);
}
.ac-code {
  margin: 0.5rem 0 0;
  padding: 0.75rem;
  overflow-x: auto;
  border-radius: calc(var(--ac-radius) / 2);
  background-color: var(--ac-code-bg);
  color: var(--ac-code-text);
  font-family: var(--sl-font-mono);
  font-size: var(--sl-text-xs);
}

/* Starlight's own controls as 44 px targets (spec 0018, Accessibility); Task 14 measures every page. */
.sidebar-content a,
.social-icons a,
starlight-toc a,
starlight-theme-select select,
starlight-lang-select select,
site-search button,
starlight-menu-button button,
mobile-starlight-toc summary {
  min-height: 44px;
}
.sidebar-content a,
.social-icons a,
starlight-toc a {
  display: flex;
  align-items: center;
}
.social-icons a {
  min-width: 44px;
  justify-content: center;
}
```

- [ ] **Step 9: Build and read the output**

Run, in `site/`:

```bash
npm run build
grep -o '<title>[^<]*</title>' dist/index.html dist/pl/index.html
grep -o 'data-block="[a-z]*"' dist/index.html | tr '\n' ' '
grep -c '24 Nov 2026' dist/index.html
grep -c '24 lis 2026' dist/pl/index.html
grep -o 'href="https://github.com/MckCieply/Auth-Core/blob/main/README.md#how-to-use-it"' dist/index.html
grep -c 'id="_top"' dist/index.html
```

Expected: exit 0 with `check-links ok`; the titles `Sign-in for products used by many companies. | Auth-Core` and the Polish
headline; the blocks `benefits diagram licence fit versions steps` in that order; `24 Nov 2026` and `24 lis 2026` at least twice each
(the badge and the first of the latest versions); the Quickstart link once; `1` heading with `id="_top"`. Open
`http://localhost:4321/Auth-Core/` in `npm run preview` at 1440 px and 375 px: twelve columns on the wide screen, one column on the
narrow one, nothing wider than the screen at 320 px. If the content column of the splash page stays narrow (the
`--sl-content-width` override does not take), stop and report.

- [ ] **Step 10: Teeth**

Delete the last item of `START_COPY.pl.elsewhere`; run `npm run build`; expected exit 1 with the test
`start copy: English and Polish have the same keys and list lengths` failing. Restore; the build passes.

- [ ] **Step 11: Commit**

```bash
git add site
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "feat: site start page with hero, bento and glass, english and polish"
```

---

### Task 11: The photos, the photo band and the credits

**Owner gate first.** The download of the photo needs the owner's yes. The orchestrator asks, before it starts this task, for two
things: (1) yes to download one file, `photo-1480843669328-3f7e37d196ae` from `images.unsplash.com`, about 1.75 MB (1,753,529 bytes
when the spec was written), JPEG; (2) the photo's page on Unsplash and its author. The id `1480843669328-3f7e37d196ae` is the id of the
file on Unsplash's CDN, not of the photo page (mismatch 7), so the page cannot be derived from it. Without both answers the
implementer does not start.

**Files:**
- Create: site/scripts/make-photos.mjs, `site/src/assets/photos/city-dark.webp`, `site/src/assets/photos/city-light.webp`,
  `site/src/components/PageTitle.astro`, site/CREDITS.md
- Modify: site/astro.config.mjs (the `PageTitle` override), `site/src/styles/theme.css` (append)

**Interfaces:**
- Consumes: the downloaded file (outside the repository); `sharp`, which Astro installs as its own dependency (not added to
  site/package.json); Starlight's `PageTitle`.
- Produces: the photo behind the Start page (1500 px) and the 280 px band above the title of every inner page; site/CREDITS.md,
  which the footer links to.

- [ ] **Step 1: Download (after the owner's yes) into an empty folder outside the repository**

```bash
DL="$(mktemp -d)"
curl -fL -o "$DL/city.jpg" "https://images.unsplash.com/photo-1480843669328-3f7e37d196ae?ixlib=rb-4.0.3&fm=jpg&q=90&w=2600"
ls -l "$DL/city.jpg"
file "$DL/city.jpg"
echo "$DL"
```

Expected: about 1,753,529 bytes; `JPEG image data`. Keep the folder name for Step 3. If the size is far off (under 1 MB) or the file is
not a JPEG, stop and report.

- [ ] **Step 2: Check that sharp is there**

Run: `cd site && node -e "import('sharp').then((m) => console.log('sharp', typeof m.default))"`
Expected: `sharp function`. If the import fails, stop and report (adding sharp to site/package.json is a change of the pinned set).

- [ ] **Step 3: Write site/scripts/make-photos.mjs and run it**

```js
// Makes the two photos of spec 0018 (Look) from the downloaded Unsplash file, run once by hand:
//   node scripts/make-photos.mjs <downloaded.jpg>
// city-dark.webp: the photo as it is, cropped to cover 2200 x 1300. city-light.webp: the same, grayscale, inverted and
// contrast 1.15 around the middle grey (1.15 * x - 19.2). WebP quality from 82 down in steps of 4 until the file is at
// most 400 KB. The files stay untinted: the CSS tints them.
import { mkdirSync, statSync } from 'node:fs';
import sharp from 'sharp';

const LIMIT = 400 * 1000;
const OUT = 'src/assets/photos/';
const source = process.argv[2];
if (!source) {
  console.error('usage: node scripts/make-photos.mjs <downloaded.jpg>');
  process.exit(2);
}
mkdirSync(OUT, { recursive: true });

const cropped = () => sharp(source).rotate().resize(2200, 1300, { fit: 'cover', position: 'centre' });

async function write(name, pipeline) {
  for (let quality = 82; quality >= 50; quality -= 4) {
    await pipeline().webp({ quality }).toFile(OUT + name);
    const size = statSync(OUT + name).size;
    if (size <= LIMIT) {
      console.log(name + ': 2200 x 1300, quality ' + quality + ', ' + size + ' bytes');
      return;
    }
  }
  throw new Error(name + ': more than 400 KB even at quality 50');
}

await write('city-dark.webp', cropped);
await write('city-light.webp', () => cropped().grayscale().negate({ alpha: false }).linear(1.15, -19.2));
```

Run: `cd site && node scripts/make-photos.mjs "<the folder of Step 1>/city.jpg"`
Expected: two lines, each with a quality and a size of at most 400000 bytes. Then
`node -e "import('sharp').then(async (m) => { for (const f of ['city-dark', 'city-light']) console.log(f, JSON.stringify(await m.default('src/assets/photos/' + f + '.webp').metadata().then((x) => [x.format, x.width, x.height]))); })"`
prints `["webp",2200,1300]` for both. Note both qualities and sizes for site/CREDITS.md. Delete the folder of Step 1 afterwards.

- [ ] **Step 4: Write `site/src/components/PageTitle.astro`**

```astro
---
// The title of the inner pages, moved below the 280 px photo band (spec 0018, Look; Settled 9). The Start page has a
// hero, so Starlight does not render a PageTitle there.
import Default from '@astrojs/starlight/components/PageTitle.astro';
---

<div class="ac-title-band">
  <Default />
</div>
```

- [ ] **Step 5: Register it**

In site/astro.config.mjs the `components` entry becomes:

```js
      components: {
        Footer: './src/components/Footer.astro',
        Hero: './src/components/Hero.astro',
        PageTitle: './src/components/PageTitle.astro',
      },
```

- [ ] **Step 6: Style it (append to `site/src/styles/theme.css`)**

```css
/* The photo (spec 0018, Look): the whole top of the Start page (1500 px), a 280 px band on inner pages. From the top
   layer down: the gradient of the background (35 %, 55 % at 45 %, 100 %), the sea-blue tint #0891b2 at 70 % blended as
   "color", the photo. cover, no-repeat, center top: never stretched (Settled 13). Decorative: no text, no alt. */
html {
  background-color: var(--ac-bg);
}
body {
  position: relative;
  background-color: transparent;
}
body::before {
  content: '';
  position: absolute;
  inset: 0 0 auto 0;
  z-index: -1;
  height: 280px;
  pointer-events: none;
  background-image:
    linear-gradient(
      color-mix(in srgb, var(--ac-bg) 35%, transparent) 0%,
      color-mix(in srgb, var(--ac-bg) 55%, transparent) 45%,
      var(--ac-bg) 100%
    ),
    linear-gradient(rgb(8 145 178 / 0.7), rgb(8 145 178 / 0.7)),
    url('../assets/photos/city-dark.webp');
  background-blend-mode: normal, color, normal;
  background-size: cover;
  background-repeat: no-repeat;
  background-position: center top;
}
:root[data-theme='light'] body::before {
  background-image:
    linear-gradient(
      color-mix(in srgb, var(--ac-bg) 35%, transparent) 0%,
      color-mix(in srgb, var(--ac-bg) 55%, transparent) 45%,
      var(--ac-bg) 100%
    ),
    linear-gradient(rgb(8 145 178 / 0.7), rgb(8 145 178 / 0.7)),
    url('../assets/photos/city-light.webp');
}
html[data-has-hero] body::before {
  height: 1500px;
}
.ac-title-band {
  padding-top: 12rem;
}
```

- [ ] **Step 7: Write site/CREDITS.md**

Fill the author, the page and the two quality and size pairs from Step 3 and the owner's answer; nothing else changes.

```md
# Credits

## City photo

- Photo: by <author, from the owner>, on Unsplash: <the photo page, from the owner>. File id on Unsplash's CDN:
  `1480843669328-3f7e37d196ae`.
- Licence: the [Unsplash License](https://unsplash.com/license).
- The files, made with site/scripts/make-photos.mjs from the JPEG at 2600 px wide:
  - city-dark.webp: the photo as it is, cropped to cover 2200 x 1300, WebP quality <q> (<n> bytes);
  - city-light.webp: the same crop, grayscale, inverted, contrast 1.15, WebP quality <q> (<n> bytes).
- The site tints both in CSS (`#0891b2` at 70 %, blended as colour) and lays the background gradient over them; the files
  themselves are not tinted.

## Fonts

- Space Grotesk, by Florian Karsten, under the SIL Open Font License 1.1:
  <https://github.com/floriankarsten/space-grotesk>. Served from the site through the npm package `@fontsource/space-grotesk`.
- IBM Plex Mono, by IBM, under the SIL Open Font License 1.1: <https://github.com/IBM/plex>. Served from the site through the npm
  package `@fontsource/ibm-plex-mono`.
```

Run: `grep -c "<author\|<the photo page\|<q>\|<n>" site/CREDITS.md`
Expected: `0`. Then, from the repository root: `python scripts/check-docs.py site/CREDITS.md`; expected
`all links, paths, files and tests resolve`.

- [ ] **Step 8: Build and look**

Run, in `site/`: `npm run build && ls dist/_astro/ | grep -c "city-dark.*\.webp\|city-light.*\.webp"`
Expected: exit 0 with `check-links ok`; `2`. Then `npm run preview` and open `http://localhost:4321/Auth-Core/` and
`http://localhost:4321/Auth-Core/fit/` in both themes: the Start page has the photo behind the top, the inner page a band above
the title, both sea-blue; the light theme shows the inverted photo. Take the four screenshots (Start and Is it for me, light and
dark, 1440 px) for the owner. If the band is hidden behind an opaque layer of Starlight, stop and report which element covers it.

- [ ] **Step 9: Teeth**

Change `url('../assets/photos/city-dark.webp')` to `url('../assets/photos/city-darkk.webp')`; run `npm run build`; expected exit 1,
Vite unable to resolve the file. Restore; the build passes.

- [ ] **Step 10: Owner gate: the light photo**

The orchestrator shows the owner the light screenshots (Open question 1: keep the inverted photo, the default, or use a light
photo). A change of photo is a new task, not a change here.

- [ ] **Step 11: Commit**

```bash
git add site
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "feat: site photo band, tinted city photo and credits"
```

---

### Task 12: The workflow

**Files:**
- Create: `.github/workflows/site.yml`

**Interfaces:**
- Consumes: rule 6 of `scripts/check-pins.py` (Task 2); `npm run build` in `site/` with its prebuild and postbuild (Task 4).
- Produces: the workflow `site`, which publishes `site/dist/` to GitHub Pages on a push to `main` that changes the site, the
  changelog, the parser or the workflow.

- [ ] **Step 1: Check the pinned SHAs against their tags**

```bash
gh api repos/actions/checkout/git/ref/tags/v7.0.1 --jq .object.sha
gh api repos/withastro/action/git/ref/tags/v6.1.4 --jq .object.sha
gh api repos/actions/deploy-pages/git/ref/tags/v5.0.1 --jq .object.sha
```

Expected: `3d3c42e5aac5ba805825da76410c181273ba90b1`, `a8b97df764791eb61fbf3338ef713b0e61ae05c5`,
`368f82528645a54fb793d4d04e342629a3f51346`. If a line shows a different SHA, or the object is a tag object (annotated tag: run
`gh api repos/<owner>/<repo>/git/tags/<sha> --jq .object.sha` to get the commit), use the commit SHA and list it under Deviations.

- [ ] **Step 2: Write `.github/workflows/site.yml`**

```yaml
# Builds the project site (site/) and publishes it to GitHub Pages (spec 0018, Workflow). The only workflow of the
# repository: it runs no tests and gates no merge (docs/workflow.md). withastro/action runs npm install and
# npm run build in site/, so the prebuild and postbuild checks run here as they do locally.
name: site

on:
  push:
    branches: [main]
    paths:
      - 'site/**'
      - CHANGELOG.md
      - scripts/release-notes.py
      - .github/workflows/site.yml
  workflow_dispatch:

permissions:
  contents: read

concurrency:
  group: pages
  cancel-in-progress: false

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - uses: withastro/action@a8b97df764791eb61fbf3338ef713b0e61ae05c5 # v6.1.4
        with:
          path: site
          node-version: 24
          package-manager: npm

  deploy:
    needs: build
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    permissions:
      contents: read
      pages: write
      id-token: write
    steps:
      - id: deployment
        uses: actions/deploy-pages@368f82528645a54fb793d4d04e342629a3f51346 # v5.0.1
```

- [ ] **Step 3: The repository checks**

Run, from the repository root: `python scripts/check-pins.py --self-test && python scripts/check-pins.py`
Expected: the self-test line, then the success line, exit 0. Then read the file once against the spec's "Workflow" list: the four
paths, `workflow_dispatch`, `contents: read` at the top, the deploy job's three permissions, the concurrency group, no secret, no
`pull_request` and no tag trigger.

- [ ] **Step 4: Teeth**

Change the checkout line to `- uses: actions/checkout@v7`; run `python scripts/check-pins.py`; expected exit 1 and
`.github/workflows/site.yml:27: the action actions/checkout@v7 is not pinned by a 40-character commit SHA (owner/repo@<sha> # vX.Y.Z)`
(the line number is the line of the `uses:`). Restore; the check passes.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/site.yml
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "ci: site workflow builds and publishes the project site to pages"
```

---

### Task 13: The documents

**Files:**
- Modify: `docs/workflow.md`, `docs/operations/releases.md`, `README.md`

**Interfaces:**
- Consumes: the spec's "Documents"; the files of Tasks 4, 11 and 12, which the new text names.
- Produces: the text of criterion 23.

The three files are stored with LF in the index and checked out with CRLF (`core.autocrlf` is `true`). The script below keeps the
line ending it finds in each file, so `git diff` shows only the changed lines.

- [ ] **Step 1: Check the lines the edits anchor on**

Run:

```bash
sed -n '11p;163p' docs/workflow.md
sed -n '5p' docs/operations/releases.md
sed -n '5p;220p;229p' README.md
```

Expected: workflow.md line 11 ends the "Local-only" box (`> run as CI gates, we run **locally as agents** before the merge.`) and
line 163 ends the "Integration is a local merge" bullet (`  pull request, no CI checks, no protected-branch required checks.`);
releases.md line 5 starts `Do it after the annotated tag is pushed`; README.md line 5 is the release badge, line 220 starts
`scripts/` in the layout block and line 229 holds `the specs (0001 to 0009) and the plans`. If a line differs, stop and report.

- [ ] **Step 2: Apply the edits**

The backtick is written as `chr(96)` (Q below) so that this plan passes `check-docs.py` before the files it names exist.

```bash
python - <<'EOF'
import pathlib

Q = chr(96)

def edit(name, pairs):
    path = pathlib.Path(name)
    data = path.read_bytes().decode("utf-8")
    eol = "\r\n" if "\r\n" in data else "\n"
    text = data.replace("\r\n", "\n")
    for old, new in pairs:
        if text.count(old) != 1:
            raise SystemExit(name + ": expected the anchor once: " + old[:60])
        text = text.replace(old, new)
    path.write_bytes(text.replace("\n", eol).encode("utf-8"))
    print(name + ": " + str(len(pairs)) + " edits")

workflow_file = Q + ".github/workflows/site.yml" + Q
edit("docs/workflow.md", [
    (
        "> run as CI gates, we run **locally as agents** before the merge.\n",
        "> run as CI gates, we run **locally as agents** before the merge.\n\n"
        "One exception: " + workflow_file + " builds the project site and publishes it to\n"
        "GitHub Pages on a push to " + Q + "main" + Q + ". It runs no tests and gates no merge; the\n"
        "verification of the product stays local.\n",
    ),
    (
        "  pull request, no CI checks, no protected-branch required checks.\n",
        "  pull request, no CI checks, no protected-branch required checks.\n"
        "- **One exception: the project site.** " + workflow_file + " builds the site and\n"
        "  publishes it to GitHub Pages on a push to " + Q + "main" + Q + "; it runs no tests and gates no\n"
        "  merge.\n",
    ),
])

summaries = (Q + "site/src/data/version-summaries.pl.json" + Q + " and "
             + Q + "site/src/data/version-summaries.en.json" + Q)
edit("docs/operations/releases.md", [
    (
        "## The service\n",
        "## Before the tag\n\n"
        "Add the version's summary, in Polish and in English, to " + summaries + ", or the site's build fails.\n\n"
        "## The service\n",
    ),
    (
        "and the last line points to the whole changelog.\n",
        "and the last line points to the whole changelog.\n\n"
        "Then push " + Q + "main" + Q + " with the tag (" + Q + "git push origin main v0.5.0" + Q + "); the site rebuilds on the push "
        "and the Versions page shows the version.\n",
    ),
])

edit("README.md", [
    (
        "(https://github.com/MckCieply/Auth-Core/releases/latest)\n",
        "(https://github.com/MckCieply/Auth-Core/releases/latest) · [Site](https://mckcieply.github.io/Auth-Core/)\n",
    ),
    (
        "scripts/                      dev keys and the e2e checks of every slice\n",
        "scripts/                      dev keys and the e2e checks of every slice\n"
        "site/                         the project site (Astro Starlight), published to GitHub Pages\n",
    ),
    (
        "the specs (0001 to 0009) and the plans",
        "the specs and the plans",
    ),
])
EOF
```

Expected: `docs/workflow.md: 2 edits`, `docs/operations/releases.md: 2 edits`, `README.md: 3 edits`. Then `git diff --stat` shows the
three files and only added or changed lines (no whole-file change); `git diff README.md` shows line 229 as
`... the specs and the plans and acceptance maps built from them.` (mismatch 4: the README line names the plans already, so only
`(0001 to 0009) ` goes).

- [ ] **Step 3: The document checks**

Run, from the repository root:

```bash
python scripts/check-docs.py
python scripts/check-docs.py docs/workflow.md docs/operations/releases.md site/CREDITS.md
python scripts/check-pins.py
```

Expected: `all links, paths, files and tests resolve` twice; the success line of check-pins (rule 3 reads the new Markdown).

- [ ] **Step 4: Teeth**

In `docs/operations/releases.md` change the English file name site/src/data/version-summaries.en.json to
site/src/data/version-summary.en.json; run
`python scripts/check-docs.py docs/operations/releases.md`; expected exit 1 and a line naming the missing file. Restore; it passes.

- [ ] **Step 5: Commit**

```bash
git add docs/workflow.md docs/operations/releases.md README.md
git -c user.name=Alex -c user.email=mwppl123@gmail.com commit -m "docs: workflow, releases and readme name the project site"
```

---

### Task 14: Local verification

Run by a verifier, not by an implementer. Everything happens in a clean clone outside the worktree; every break is undone with
`git checkout -- .` in the clone, and the clone is deleted at the end. Nothing is committed. Each step names the criterion it
guards; the report lists each step with PASS or FAIL and the decisive output line.

**Files:** none in the repository. **Interfaces:** the branch docs/0018-project-site after Task 13.

- [ ] **Step 1: Clean clone, Windows (criteria 5 to 9, the Goal's last line)**

```bash
CLONE="$(mktemp -d)/repo"
git clone --no-local --branch docs/0018-project-site "$(git rev-parse --show-toplevel)" "$CLONE"
cd "$CLONE/site" && npm ci && npm run build; echo "exit $?"
```

Expected: `# fail 0`; `prebuild ok: python, 7 versions (6 server), summaries and page parity checked` (or `py -3` in place of `python`
on this machine); the validator's success line; `check-links ok: 11 pages, <n> links`; `exit 0`.

- [ ] **Step 2: Clean clone, Linux container**

```bash
MSYS_NO_PATHCONV=1 docker run --rm -v "$(cygpath -w "$CLONE")":/repo -w /repo/site node:24-bookworm \
  bash -c "rm -rf node_modules dist .astro && npm ci && npm run build"; echo "exit $?"
```

Expected: the same lines as Step 1 with `python3` as the interpreter; `exit 0`. The image's Python 3 runs the parser self-test.

- [ ] **Step 3: Break the summaries (criterion 5)**

In `$CLONE/site` delete the `"0.1.0"` line of site/src/data/version-summaries.pl.json (and the comma before it); run
`npm run build`; expected exit 1 and `src/data/version-summaries.pl.json: no summary for 0.1.0`. `git checkout -- .`. Then make the
English summary of 0.2.0 301 characters long; expected `src/data/version-summaries.en.json: the summary of 0.2.0 has 301 characters,
more than 300`. `git checkout -- .`.

- [ ] **Step 4: Break the page parity (criterion 6)**

`git rm -q src/content/docs/pl/compare.md && npm run build`; expected exit 1 and
`src/content/docs/compare.md has no Polish page src/content/docs/pl/compare.md`. `git checkout HEAD -- .`.

- [ ] **Step 5: Break a link, twice (criterion 8 and Review Focus 1)**

1. In site/src/content/docs/compare.md change the last link target `/Auth-Core/fit/` to `/Auth-Core/fitt/`; `npm run build`;
   expected exit 1, the validator naming `compare` and `/Auth-Core/fitt/`. `git checkout -- .`.
2. In `site/src/components/Footer.astro` change `pagePath(lang, 'license')` to `'/license/'`; `npm run build`; expected exit 1 and
   `check-links failed:` with lines `<page>: the link /license/ is outside /Auth-Core/`. `git checkout -- .`.

- [ ] **Step 6: Break the parser self-test (criterion 7)**

Run `cd "$CLONE" && sed -i '0,/## 0.2.0 - 2026-01-02/s//## 0.2.1 - 2026-01-02/' scripts/release-notes.py` (the first sample
heading of `self_test`, line 121 at `main`), then `cd site && npm run build`; expected exit 1, the prebuild naming the parser
self-test and its output (`self-test FAILED: ...`). `git checkout -- .`.

- [ ] **Step 7: The pin rules (criterion 9)**

In `$CLONE`: `python scripts/check-pins.py --self-test && python scripts/check-pins.py`; expected the self-test line and the success
line. Then change the checkout line of `.github/workflows/site.yml` to `actions/checkout@v7`, run `python scripts/check-pins.py`,
expected exit 1 with the rule 6 line; `git checkout -- .`; change `"astro": "7.4.13"` to `"astro": "^7.4.13"` in site/package.json,
expected exit 1 with the rule 7 line; `git checkout -- .`.

- [ ] **Step 8: A new version reaches the Start page (criterion 11)**

```bash
cd "$CLONE"
python - <<'EOF'
import json, pathlib, re
changelog = pathlib.Path("CHANGELOG.md")
text = changelog.read_text(encoding="utf-8")
first = re.search(r"^## \d", text, re.M).start()
changelog.write_text(text[:first] + "## 0.9.0 - 2026-12-01\n\nA test section for the verification.\n\n" + text[first:], encoding="utf-8")
for lang, summary in (("pl", "Sekcja testowa weryfikacji."), ("en", "A test section for the verification.")):
    path = pathlib.Path("site/src/data/version-summaries." + lang + ".json")
    data = json.loads(path.read_text(encoding="utf-8"))
    path.write_text(json.dumps({"0.9.0": summary, **data}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
EOF
cd site && npm run build && grep -c "v0.9.0 · 1 Dec 2026" dist/index.html && grep -c "v0.9.0 · 1 gru 2026" dist/pl/index.html \
  && grep -c 'id="v0-9-0"' dist/versions/index.html && grep -o "Sekcja testowa weryfikacji." dist/pl/index.html
cd "$CLONE" && git checkout -- .
```

Expected: the build passes; the badge and the first latest version show `v0.9.0 · 1 Dec 2026` (count 2) and `v0.9.0 · 1 gru 2026`
(count 2); the Versions page has `v0-9-0`; the Polish summary is on the Polish Start page. Then the checkout restores 0.4.0.

- [ ] **Step 9: The parser on the real changelog (criteria 1, 2, 4)**

Run, in `$CLONE`, Step 5 and Step 6 of Task 1 as they are written there. Expected: `7 6`; `bodies equal for 7 tags`; 14 lines starting
`same`.

- [ ] **Step 10: The pages against their sources (criteria 10, 12 to 16)**

Run `npm run preview` in `$CLONE/site` and open `http://localhost:4321/Auth-Core/`.

1. Criterion 10: on each of the ten pages, the language picker and the footer link open the same page in the other language.
2. Criterion 12: the Versions page against `CHANGELOG.md`: six versions, dates `24 Nov 2026`, `23 Nov 2026`, `21 Nov 2026`,
   `28 Oct 2026` and the two 0.1.x dates of the changelog; each release link and image tag; the asides of 0.4.0, 0.3.0 and 0.2.0 end
   before the next `###` heading or the Full changelog line; the Polish page shows the Polish summaries.
3. Criterion 13: a table in the report, one row per product fact of the Start, Is it for me and License pages, with its `README.md`
   line at the build commit (`git show HEAD:README.md | grep -n "<words>"`) or, for the five known limits, its
   `docs/security/threat-model.md` line.
4. Criterion 14: open every footnote URL of the Comparison page (20) and check the cell it backs; note any cell the page does not
   state. Every competitor cell has a footnote; the page shows `Checked:`.
5. Criterion 15: the License page against the README's "License" section and the Change Date clause of `LICENSE`, line by line.
6. Criterion 16: each known limit against residual risks 3, 4, 13, 15 and 17 of the threat model.

- [ ] **Step 11: Screenshots and the look (criterion 17, Polish letters)**

With `npm run preview` running, take eight screenshots: Start in English at 1440 px and 375 px, light and dark; Is it for me in Polish
at 1440 px, light and dark; Versions at 375 px, dark; License at 320 px, light. The theme is set with
`localStorage.setItem('starlight-theme', 'light')` (or `'dark'`) and a reload. Check: the photo behind the top of Start and the band
on inner pages, the glass hero card, the bento order, one column at 375 px and no horizontal scroll at 320 px
(`document.documentElement.scrollWidth <= innerWidth` is `true`). For the Polish letters run, on a Polish page,
`document.fonts.check('16px "Space Grotesk"', 'ąćęłńóśźż')`; expected `true`, and the network log shows a `latin-ext` font file.
The screenshots go to the owner with the report.

- [ ] **Step 12: Accessibility (criterion 18)**

1. **axe (owner's yes first):** the download is the npm package `axe-core` (one tarball from registry.npmjs.org, about 0.5 MB; the
   version is the one `npm view axe-core version` prints, named in the report). After the yes:

```bash
AXE="$(mktemp -d)"
(cd "$AXE" && npm pack "axe-core@$(npm view axe-core version)" && tar -xzf axe-core-*.tgz)
python -m http.server 8765 --bind 127.0.0.1 --directory "$AXE/package"
```

   (the server runs in the background). On each of the ten pages, in each theme, run in the page:

```js
await new Promise((ok, no) => { const s = document.createElement('script'); s.src = 'http://127.0.0.1:8765/axe.min.js'; s.onload = ok; s.onerror = no; document.head.append(s); });
(await axe.run(document, { resultTypes: ['violations'] })).violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => v.id + ': ' + v.nodes.length);
```

   Expected `[]` on all 20 runs. The axe script is the only request to another origin here, and it is the verifier's, not the site's.

2. **Contrast over the photo:** axe leaves text over a background image as "incomplete", so compute it. Over the darkest (black) and
   brightest (white) pixel the photo can have, the card colour is the glass (80 % background) over the gradient's top layer (35 %
   background) over the pixel:

```bash
python - <<'EOF'
def lum(h):
    c = [int(h[i:i + 2], 16) / 255 for i in (1, 3, 5)]
    c = [x / 12.92 if x <= 0.03928 else ((x + 0.055) / 1.055) ** 2.4 for x in c]
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]
def ratio(a, b):
    hi, lo = sorted([lum(a), lum(b)], reverse=True)
    return (hi + 0.05) / (lo + 0.05)
def mix(fg, bg, a):
    f = [int(fg[i:i + 2], 16) for i in (1, 3, 5)]
    b = [int(bg[i:i + 2], 16) for i in (1, 3, 5)]
    return "#" + "".join("%02x" % round(a * x + (1 - a) * y) for x, y in zip(f, b))
for theme, bg, text, muted in (("dark", "#04090c", "#ecf8fb", "#9dbac2"), ("light", "#f5fafb", "#08171b", "#3f5a61")):
    for pixel in ("#000000", "#ffffff"):
        card = mix(bg, mix(bg, pixel, 0.35), 0.8)
        print(theme, pixel, "text %.2f" % ratio(text, card), "muted %.2f" % ratio(muted, card))
print("accent card %.2f" % ratio("#0a0a0a", "#22d3ee"))
EOF
```

   Expected (computed when the plan was written): dark 18.59 / 9.81 and 13.54 / 7.15; light 12.95 / 5.22 and 17.52 / 7.07; accent
   card 10.96. Every value is at least 4.5. Then check on the 1440 px dark and light screenshots that the hero card sits where the
   gradient is at least 35 % (the top of the page).

3. **Touch targets:** on each page at 375 px run:

```js
[...document.querySelectorAll('a, button, select, summary, input')].filter((e) => e.offsetParent !== null).filter((e) => !(e.closest('.sl-markdown-content') && e.closest('p, li, td, th') && !e.matches('.ac-target, .ac-button, .ac-version-links a'))).map((e) => [e.textContent.trim().slice(0, 30) || e.getAttribute('aria-label'), Math.round(e.getBoundingClientRect().width), Math.round(e.getBoundingClientRect().height)]).filter(([, w, h]) => w < 44 || h < 44);
```

   Expected `[]`. Links inside a sentence of the page text are exempt (Decision 21). If Starlight elements are listed, report them
   with their selector: the fix is a CSS line in `site/src/styles/theme.css` on this branch, made by an implementer before the merge.

- [ ] **Step 13: Privacy and JavaScript off (criteria 19, 20)**

1. In `$CLONE/site`:

```bash
python - <<'EOF'
import pathlib, re
problems = []
for path in pathlib.Path("dist").rglob("*"):
    if path.suffix == ".html":
        for tag in re.findall(r"<(?:script|img|link)\b[^>]*>", path.read_text(encoding="utf-8"), re.I):
            link = re.search(r"\b(?:src|href)=\"(https?:)?//", tag)
            rel = re.search(r"\brel=\"([^\"]*)\"", tag)
            if link and (not tag.lower().startswith("<link") or (rel and set(rel.group(1).split()) & {"stylesheet", "preload", "modulepreload", "icon"})):
                problems.append(str(path) + ": " + tag[:120])
    elif path.suffix == ".css":
        problems += [str(path) + ": " + m for m in re.findall(r"url\(\s*['\"]?(?:https?:)?//[^)]*\)", path.read_text(encoding="utf-8"))]
print("\n".join(problems) or "no request to another host")
EOF
curl -sI http://localhost:4321/Auth-Core/ | grep -ci "set-cookie"
```

   Expected: `no request to another host`; `0`. Then load each page with the browser's network log open: every request goes to
   `localhost:4321`; `document.cookie` is `''`.
2. JavaScript off: the pages are static HTML, so the text a reader gets without JavaScript is the HTML the server sends. For each of
   the ten pages, `curl -s http://localhost:4321/Auth-Core/<page>` holds the page's `h1`, its body text (one sentence of each section,
   checked by `grep`) and the footer's language link. Search, the theme picker, the language picker and the mobile menu are the
   parts that need JavaScript. If the owner wants a browser check too, they turn JavaScript off for `http://localhost:4321` in their
   own browser settings; the verifier does not change browser settings.

- [ ] **Step 14: The licence of `site/`**

Spec Decision 11: `site/LICENSE` is MIT, line 9 of `LICENSE` names `site/` beside `clients/`, `samples/` and `docs/`, the README's
License section says the same, and the License page lists `site/` under "What stays MIT" in both languages.

Run: `grep -n 'site/' LICENSE; grep -n 'and .site/. are under the MIT' README.md; grep -c 'site/' site/src/content/docs/license.md site/src/content/docs/pl/license.md`
Expected: one line of `LICENSE`, one line of `README.md`, at least 1 for each page.

- [ ] **Step 15: Report and clean up**

`rm -rf "$CLONE" "$AXE"`; stop the preview and the `http.server`. The report has one line per step 1 to 13 (PASS or FAIL with the
decisive output), the fact table of Step 10.3, the comparison notes of Step 10.4, the eight screenshots, the axe version, and the
owner questions: the light photo (Task 11) and the summaries (Appendix B).

---

### Task 15: Merge and publish

Run by the orchestrator. **Every step needs the owner's yes, asked for that step alone.** Before Step 1 the owner has answered the
three questions of Task 14, Step 15, and any change they asked for is merged into this branch and verified.

- [ ] **Step 1: Merge into `main` (owner yes needed)**

The main checkout is `C:\Users\mwppl\code\Auth-Core` (on `main`). With the real date:

```bash
git -C /c/Users/mwppl/code/Auth-Core status --porcelain
git -C /c/Users/mwppl/code/Auth-Core merge --no-ff docs/0018-project-site \
  -m "merge: project site on GitHub Pages, english and polish"
git -C /c/Users/mwppl/code/Auth-Core log --oneline -3
```

The merge commit is made as Alex: run it with `-c user.name=Alex -c user.email=mwppl123@gmail.com` after `git` (as in the task
commits), and no trailer. Expected: an empty status before; the merge commit on top of `main`. Then, in the main checkout:
`python scripts/check-docs.py && python scripts/check-pins.py && (cd site && npm ci && npm run build)`, all green.

- [ ] **Step 2: Pages source "GitHub Actions" (owner yes needed)**

Run before the push, so the first run of the workflow can deploy:

```bash
gh api -X POST repos/MckCieply/Auth-Core/pages -f build_type=workflow
gh api repos/MckCieply/Auth-Core/pages --jq '.build_type + " " + .html_url'
```

Expected: `workflow https://mckcieply.github.io/Auth-Core/`. If the POST answers 409 (Pages exists already), run
`gh api -X PUT repos/MckCieply/Auth-Core/pages -f build_type=workflow` instead (owner yes covers both).

- [ ] **Step 3: Push `main` (owner yes needed)**

Run: `git -C /c/Users/mwppl/code/Auth-Core push origin main`
Expected: `main -> main`. The push starts the workflow `site` (it changes `site/**`).

- [ ] **Step 4: The "Website" field (owner yes needed)**

Run: `gh repo edit MckCieply/Auth-Core --homepage https://mckcieply.github.io/Auth-Core/`, then
`gh repo view MckCieply/Auth-Core --json homepageUrl --jq .homepageUrl`.
Expected: `https://mckcieply.github.io/Auth-Core/`.

- [ ] **Step 5: The first run (criterion 22)**

```bash
RUN=$(gh run list -R MckCieply/Auth-Core --workflow site.yml --branch main --limit 1 --json databaseId --jq '.[0].databaseId')
gh run watch "$RUN" -R MckCieply/Auth-Core --exit-status
```

The watch runs as one background command (the Bash tool with `run_in_background: true`), never in a loop of turns. Expected: both
jobs green. If `build` fails, read `gh run view "$RUN" -R MckCieply/Auth-Core --log-failed`, take the first failing check line, and
report it; a fix is a new branch, not a change on `main`.

- [ ] **Step 6: The Goal's lines (criterion 22)**

```bash
curl -sI https://mckcieply.github.io/Auth-Core/ | head -1
curl -sI https://mckcieply.github.io/Auth-Core/pl/ | head -1
curl -s https://mckcieply.github.io/Auth-Core/versions/ | grep -o 'releases/tag/v' | wc -l
curl -sI https://mckcieply.github.io/Auth-Core/ | grep -ci "set-cookie"
```

Expected: `HTTP/2 200` twice, `6`, `0`. The output goes into the report and into the merge note in the run-state.

---

## Decisions taken in this plan

Choices the spec leaves open, taken here so that no implementer reopens them.

1. **The footer's three licence links:** "Server: BSL 1.1." to `LICENSE` at `main`; "Clients, samples and docs: MIT." to the site's
   License page; "City photo: Unsplash License." to site/CREDITS.md at `main` (the spec lists the three targets in this order).
2. **Internal Markdown links carry a title** (`[Comparison](/Auth-Core/compare/ "Comparison")`), with the base path written out. The
   title keeps `check-docs.py`, which reads repository paths, off the site's URLs; the base path is what GitHub Pages serves.
3. **The Polish labels of the two fit lists** are "Pasuje, jeśli" and "Szukaj gdzie indziej, jeśli", on the Start page and on Czy to
   dla mnie.
4. **Starlight's "text invert" is the text on accent** (`#0a0a0a`); the sidebar's current item and the caution aside are neutral
   (surface and text colour), because Settled 10 keeps cyan off everything not on its list.
5. **Cyan on the primary button only.** The second button is outlined in the text colour: a cyan outline on the light background is
   1.8:1, under the 3:1 of "Look". The badge's border is the border colour and its "latest" pill is cyan.
6. **`--all-json`** prints its array with `indent=2` and `ensure_ascii=False`, and the usage line names the new mode.
7. **Rule 7 is strict:** an exact version is `^\d+\.\d+\.\d+$`; a prerelease or a URL is refused.
8. **Page parity is by exact path**, extension included: compare.md and `pl/compare.mdx` do not pair.
9. **The Polish note "Szczegóły poniżej są po angielsku, jak w changelogu."** stands under the summary of every entry, and the
   English parts of the Polish page carry `lang="en"` for screen readers.
10. **The sidebar has the four inner pages**; Start is the site title's link, as in Starlight.
11. **The table of contents is off on Versions** (six long entries; the headings are the versions themselves).
12. **The bento breakpoint is 64rem** (1024 px): twelve columns from there, one column below; the routes of the diagram go side by
    side from 40rem.
13. **The photo CSS comes with the photos in Task 11**, so no build before Task 11 points at a missing file.
14. **The favicon** is a dark rounded square with a cyan dot, as an SVG in `site/public/`.
15. **tsconfig** extends Astro's strict preset; no `astro check` step in the build (it would add a package outside the pinned set).
16. **The link check of components is a `postbuild` script** (mismatch 2), so `npm run build` fails on it locally and in the workflow.
17. **The head title of Start is the headline**, and a test keeps the front matter's title equal to `START_COPY.<lang>.headline`.
18. **The site's pure functions live in `site/src/lib/`** and are shared by the components and the tests; `BASE` is the literal
    `'/Auth-Core/'`, the same string as `base` in the config, and the link tests check every built page against it.
19. **The inner pages' title band is 12rem of padding** above the title, so the title sits below the 280 px photo band.
20. **On Start, the fit cards link from their headings** ("A good fit if", "Look elsewhere if") to the Is it for me page.
21. **Links inside a sentence are exempt from the 44 px rule** (as WCAG's inline exception); every link that stands alone, the
    footer's, the buttons, the card links, the release links and Starlight's controls, is at least 44 px high.
22. **The summary files are written as files directly** (UTF-8, no byte-order mark, final newline), never through a shell heredoc,
    so the Polish letters survive on Windows.
23. **Sidebar entries are given by `slug`** with `translations`, so Starlight builds the Polish links from the same list.
24. **`sharp` comes from Astro**, used once by site/scripts/make-photos.mjs; it is not added to site/package.json.
25. **The releases document names both summary files by their full path** (the spec writes the second as a bare file name,
    version-summaries.en.json, which `check-docs.py` would look for at the repository root).
26. **The Pages setting comes before the push** in Task 15, so the first run of the workflow deploys; the brief's order (push, then
    settings) would leave the first run red at the deploy job.
