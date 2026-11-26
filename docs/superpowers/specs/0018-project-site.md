# Spec 0018 — The project site

- **Status:** Draft (design approved by the owner, 2026-11-26; written spec awaiting review)
- **Date:** 2026-11-26
- **Author:** Alex
- **Milestone:** stage 2 of "versions and changelog". Stage 1 (GitHub Releases written from the changelog,
  `scripts/release-notes.py`, merge `e8f78a8`) is done. The README positioning commit `10592d9` (price, status,
  "Auth-Core or something else") is merged to `main` (`597cc2f`); this spec reads that README as the source of every
  product fact the site states.
- **Context:** [`README.md`](../../../README.md) at `10592d9` (what the product is, price, status, alternatives, licence),
  [`CHANGELOG.md`](../../../CHANGELOG.md) (versions), [`scripts/release-notes.py`](../../../scripts/release-notes.py)
  (the changelog parser of stage 1), [`docs/operations/releases.md`](../../operations/releases.md) (the release steps),
  [`docs/security/threat-model.md`](../../security/threat-model.md) (residual risks),
  [`docs/workflow.md`](../../workflow.md) (local-only flow; this spec adds its first exception),
  [`scripts/check-pins.py`](../../../scripts/check-pins.py) (pin rules; this spec adds two).

## Goal

A developer who hears of Auth-Core decides in five minutes whether it fits their product, in English or Polish, without
reading the whole README. The site answers four questions on five pages: what it is (Start), whether it fits (Is it for
me), how it compares (Comparison), what the licence allows and costs (License), and what changed (Versions).

The site lives at `https://mckcieply.github.io/Auth-Core/` (English) and `https://mckcieply.github.io/Auth-Core/pl/`
(Polish). It rebuilds itself on every push to `main` that changes the site, the changelog or the changelog parser, so a
new version appears on the Versions page without a hand edit. It sets no cookie, loads nothing from another host, and
has no analytics.

This is met when, after the merge and the one-time Pages setting:

```
curl -sI https://mckcieply.github.io/Auth-Core/            # -> 200, the Start page in English
curl -sI https://mckcieply.github.io/Auth-Core/pl/         # -> 200, the Start page in Polish
curl -s  https://mckcieply.github.io/Auth-Core/versions/ | grep -o 'releases/tag/v' | wc -l   # -> 6 (0.4.0 ... 0.1.0)
cd site && npm ci && npm run build                          # -> exit 0 locally, the same checks as the workflow
```

## In scope

- A static site in `site/` built with Astro Starlight: five pages, each in English and Polish.
- The look the owner approved on 2026-11-26 ("L4 Noc" in the colours "P4 Cyjan"): a night-city photo tinted sea blue
  behind the top of the page, glass cards, one cyan accent, a 12-column bento on the Start page, light and dark themes
  that follow the system.
- `scripts/release-notes.py --all-json`: every changelog section as JSON, read by the Versions page and by the version
  badge of the Start page.
- `site/src/data/version-summaries.pl.json` and `site/src/data/version-summaries.en.json`: a summary of one or two
  sentences for every server version, in Polish and in English. A missing summary fails the build.
- Build checks that run the same way locally and in the workflow: the parser self-test, the summaries, the page parity
  of English and Polish, the link validator, the build.
- `.github/workflows/site.yml`: builds the site and publishes it to GitHub Pages. The first workflow of the repository.
- Two new rules in `scripts/check-pins.py`: actions pinned by commit SHA, exact versions in `site/package.json`.
- Documents: the exception in `docs/workflow.md`, the summary step in `docs/operations/releases.md`, a link to the site
  in `README.md`, `site/LICENSE`, the photo and font credits.
- The one-time repository settings: Pages source "GitHub Actions", the repository's "Website" field.

## Out of scope

- A quickstart or integration guide on the site. The Start page links to the README's "How to use it"; the guides stay
  in `docs/`.
- A blog, a newsletter, a contact form, analytics of any kind, a cookie banner.
- A custom domain. The site stays on `mckcieply.github.io/Auth-Core`.
- Competitor prices. The README carries none; prices change too often to keep true.
- Versions of the Python package on the Versions page beyond one line with a link to its releases.
- Any change to the service, its image, its API or its tests.
- A CI that runs tests. The workflow builds the site only; verification of the product stays local.

## Contract

### General rules

1. **One source per fact.** A product fact (what it does, price, status, licence terms, what it does not do) is stated
   on the site only if the README at the build commit states it. The one other source is the threat model's list of
   residual risks, for the "Known limits" of the Is it for me page. Versions and dates come from `CHANGELOG.md` through
   `--all-json`, never typed by hand. A competitor fact carries a link to its source and the date it was checked.
2. **Both languages, every page.** Every English page has a Polish page at the same path under `pl/`, and the other way
   round. The Polish text is a translation of the English one, not a different page.
3. **Nothing from another host at run time.** Fonts and photos are files of the site. The built HTML loads no script,
   style sheet, font or image from another host. Links to other hosts are ordinary links.
4. **No cookie, no storage of personal data.** Starlight's theme choice in `localStorage` is the only stored value.
5. **Claims match the code at the build commit.** A page names an endpoint, setting or file only as the README or the
   changelog names it.

### Folder and tooling (`site/`)

- Node 24, npm, exact versions in `site/package.json` (no `^` or `~`), `site/package-lock.json` committed. The same
  rules as `samples/notes-web`.
- Astro and `@astrojs/starlight`: the latest stable versions on the day of implementation, pinned exactly; the plan
  records them. Plugin `starlight-links-validator`, pinned exactly. Fonts from `@fontsource/space-grotesk` and
  `@fontsource/ibm-plex-mono` (SIL Open Font License 1.1), imported through each package's default `index.css` (or
  its weight files that include `latin-ext`), so Polish letters (ą, ć, ę, ł, ń, ó, ś, ź, ż) use the font and not a
  fallback. Package `marked`, pinned exactly, renders the changelog bodies of the Versions page (below).
- `astro.config.mjs`: `site: 'https://mckcieply.github.io'`, `base: '/Auth-Core'`, Starlight `defaultLocale: 'root'`,
  `locales: { root: { label: 'English', lang: 'en' }, pl: { label: 'Polski', lang: 'pl' } }`, `editLink.baseUrl:
  'https://github.com/MckCieply/Auth-Core/edit/main/site/'`, a social link to the repository, Starlight's search
  (Pagefind, local) and sitemap on.
- Content: English in `site/src/content/docs/`, Polish in `site/src/content/docs/pl/`.
- Generated data: `site/src/data/versions.json`, written by the prebuild step.
- `site/.gitignore`: `node_modules/`, `dist/`, `.astro/`, `src/data/versions.json`. This matters beyond tidiness:
  `check-pins.py` and `check-docs.py` read the files git knows, untracked ones included, so an unignored
  `node_modules` would be scanned.
- Internal links in pages and components go through the `base` path (`import.meta.env.BASE_URL`, or Starlight's
  relative links in Markdown), so `/fit/` is served as `/Auth-Core/fit/`; the link validator checks them.
- `site/package.json` scripts:
  - `predev`: `node scripts/prebuild.mjs` (so `versions.json` exists for `npm run dev`);
  - `dev`: `astro dev`;
  - `prebuild`: `node --test scripts/*.test.mjs && node scripts/prebuild.mjs`;
  - `build`: `astro build` (npm runs `prebuild` first);
  - `preview`: `astro preview`.
- `site/LICENSE`: MIT, for the site's code and text. The photos and fonts keep their own licences (below).

### Pages and paths

| Page | English path | Polish path | Template |
|---|---|---|---|
| Start | `/` | `/pl/` | Starlight `splash`, own components |
| Is it for me | `/fit/` | `/pl/fit/` | Starlight doc page |
| Comparison | `/compare/` | `/pl/compare/` | Starlight doc page |
| License | `/license/` | `/pl/license/` | Starlight doc page |
| Versions | `/versions/` | `/pl/versions/` | Starlight doc page, own component |

The sidebar holds the four inner pages in this order. The header holds the language picker, the theme picker (light,
dark, auto; auto is the default) and the GitHub link. The Polish pages use the same paths, so the language picker moves
a reader to the same page.

### Start

The copy is in Appendix A (English and Polish). The layout:

1. **Hero**, in a glass card over the photo: the version badge, the headline, the subline, two buttons ("Check if it
   fits" to `/fit/`, "Quickstart on GitHub" to the README's "How to use it" section at `main`), and the facts row.
2. **Bento**, 12 columns on a wide screen, one column on a phone, in this order:
   - four benefit cards (3 columns each);
   - the diagram "How it sits next to your product" (8 columns): browser, reverse proxy, `/auth` Auth-Core, `/api` your
     backend, drawn in HTML and CSS, readable as text by a screen reader;
   - the licence and price card on the accent colour (4 columns), linking to `/license/`;
   - "A good fit if" and "Look elsewhere if" (6 columns each), linking to `/fit/`;
   - the three latest versions (7 columns), linking to `/versions/`;
   - the three steps with the YAML example (5 columns).
3. **Footer**: the licence line of Appendix A, "Edit this page on GitHub", and a plain link to the same page in the
   other language ("Polski" / "English"), which works without JavaScript.

**The version badge and the three latest versions are built from `versions.json`:** the newest server version, its date
and the word "latest"; the three newest server versions, each with the first sentence of its summary in the page's
language (`version-summaries.en.json` or `version-summaries.pl.json`).

### Is it for me

Sections, in this order:

1. **A good fit if**: the three items of Appendix A, each with one sentence more.
2. **Look elsewhere if**: every item of the README's "Not in scope" and "Not there yet" and of its line "Auth-Core
   does not fit when ...": ready-made screens; MFA today (TOTP and passkeys are planned, with no date); one pool of users
   for several products (the README's "No SSO between products"); SAML, SCIM or LDAP; multi-realm mode; fine-grained
   resource permissions; a TypeScript backend that wants auth as a library inside the app; and a managed cloud service
   (the README's first line: "Self-hosted").
3. **What you need to run it**: the README's "What it adds to run", in plain words: a Linux x86-64 server with Docker, a
   domain, PostgreSQL 16 from the production compose file, a reverse proxy that ends TLS, an SMTP relay with TLS, a
   nightly backup, your own sign-in screens.
4. **Status**: the README's "Status": version (from `versions.json`), "Auth-Core runs in production in Toviq" (one
   sentence, no link, as the README), one author, no external audit yet, the end-to-end suites.
5. **Known limits**: five accepted risks from the threat model, each in one plain sentence with a link to the residual
   risk list, chosen for what a product owner notices:
   - a removed member keeps the token's permissions for up to 15 minutes (residual risk 13);
   - another open tab stays usable for up to 10 minutes after sign-out (15);
   - many people behind one address share its limits, 30 sign-ins a minute (3);
   - a company admin with every permission can delete the company, and only the backup brings it back (4);
   - Safari on a real phone is not yet covered by the browser tests (17).
   The implementer checks each number against the threat model at the build commit. These five are the only facts of
   the site that come from the threat model and not from the README (General rule 1).

### Comparison

The same alternatives as the README's "Auth-Core or something else", in the same order: Better Auth, Keycloak, Clerk,
WorkOS, then Auth-Core. Two parts:

1. **The README's table**, as it is: "Pick it when" and "Auth-Core instead when", and the line "Auth-Core does not fit
   when ...".
2. **A facts table**, one row per product, columns:
   - how it runs (a service you host, a library inside your app, a hosted service);
   - licence (name only);
   - companies and roles (built in, a plug-in, an add-on, not there);
   - sign-in screens (your own, hosted by the vendor, ready-made components);
   - MFA today (yes, no; for Auth-Core "planned, no date");
   - SSO between products, SAML;
   - how you pay (in words: "free and open source", "per monthly active user", "per connection"; for Auth-Core
     "EUR 490 a year per product, flat").

Every competitor cell has a footnote with the link to the vendor's page it comes from. Under the table: "Checked:
<date>", the day the implementer read the pages. A cell the implementer cannot source is written "not stated" with the
page that was read.

### License

1. **In plain words**, one line per case, each ending "free" or "needs a commercial licence":
   development, testing and evaluation (free); a personal non-commercial project in production (free); a non-profit or
   educational use, non-commercial (free); a company in production, even for internal use (needs one); a company
   evaluating it before buying (free).
2. **Price**: the README's line: EUR 490 a year per product, net of VAT, flat, any number of users; production use,
   security fixes and best-effort email support (a reply usually within two working days). Contact:
   `kontakt@toviq.pl` as a `mailto:` link.
3. **What stays MIT**: `clients/`, `samples/`, `docs/`, and `v0.1.0` with everything published before the licence
   change.
4. **Change to Apache 2.0**: each version three years after it is first published, as the README and the Change Date
   of `LICENSE` say. The page computes no per-version dates (Settled 11).
5. A link to the full [`LICENSE`](../../../LICENSE) at `main`, and the sentence "The licence text decides; this page only
   explains it." (Polish: "Rozstrzyga tekst licencji; ta strona go tylko objaśnia.").

### Versions

- Every server version of `versions.json`, newest first. For each:
  - heading: the version and its changelog date, formatted from the `YYYY-MM-DD` string with a month table of the site
    (English `24 Nov 2026`, Polish `24 lis 2026`), not with `Intl`, so neither the time zone nor the ICU version moves
    it; the newest has the badge "Latest" / "Najnowsza";
  - the summary in the page's language above the entry; on the Polish page also the line "Szczegóły poniżej są po
    angielsku, jak w changelogu.";
  - the `### Upgrading` subsection, when the section has one, shown first in a Starlight `caution` aside titled
    "Upgrading" / "Aktualizacja": from its heading to the next `### ` heading or the "Full changelog: ..." line,
    whichever comes first;
  - the rest of the body, rendered to HTML with `marked` (links are absolute already, from `--all-json`). The body is
    the repository's own changelog, so it is trusted and not sanitised; the plan adds a check that it holds no raw
    HTML tag;
  - the "Full changelog: ..." line of the body, as one link under the entry;
  - links: "GitHub Release" to `https://github.com/MckCieply/Auth-Core/releases/tag/v<version>`, and the image tag
    `ghcr.io/mckcieply/auth-core:<version>` in a code span.
- One line at the end: the Python package `auth-core-fastapi` has its own releases, with a link to
  `https://github.com/MckCieply/Auth-Core/releases?q=python-v`.
- A changelog heading that is not a version (`## Unreleased`) is not shown.

### `scripts/release-notes.py --all-json`

- New usage line: `python scripts/release-notes.py --all-json`, prints one JSON array to stdout (UTF-8,
  `ensure_ascii=False`), exit 0. `main()` takes the branch for `--all-json` before it reads an argument as a tag.
- A **version heading** is a line that matches `^## (python-v)?\d+\.\d+\.\d+( |$)`. Every version heading must be
  `## <version> - YYYY-MM-DD` with a real calendar date (`datetime.date.fromisoformat`). Exit 1, with the heading on
  stderr, when a version heading has no date or a bad one, when one version has two headings, or when the file has no
  version heading. Other `## ` headings (`## Unreleased`) are skipped.
- One object per version section, in the order of the file:

  ```json
  {"tag": "v0.4.0", "kind": "server", "version": "0.4.0", "date": "2026-11-24",
   "title": "Auth-Core 0.4.0", "body": "<the same text as `release-notes.py v0.4.0` prints>"}
  ```

  `kind` is `server` for `v*` and `python` for `python-v*`. `body` is exactly `release_body(text, tag)`, so the
  Releases on GitHub and the site show the same text.
- `--self-test` gains cases: the order of the file, both kinds, `## Unreleased` skipped, `body` equal to
  `release_body`, a heading with no date, a heading with an impossible date (`2026-02-30`), a duplicate version.
- The existing usages and their output do not change.

### Version summaries (`site/src/data/version-summaries.pl.json`, `version-summaries.en.json`)

- Each file is an object from version to text: `{"0.4.0": "...", "0.3.0": "...", ...}`. One to two sentences each, at
  most 300 characters. The first texts are in Appendix B, approved by the owner before the merge.
- The prebuild step fails, naming the file and the version, when a server version of `versions.json` has no summary in
  either file, when a summary is empty or longer than 300 characters, and when a file has a key that is not a server
  version.

### Prebuild (`site/scripts/prebuild.mjs`)

In this order, each failure printed with its cause and exit 1:

1. runs `scripts/release-notes.py --self-test` from the repository root with the first Python that works, trying
   `python3`, `python`, then `py -3`: a candidate that is missing, fails to start (the Windows Store stub of `python3`)
   or does not print `self-test ok` moves on to the next; when none works, the step fails and names the three it
   tried. The same interpreter runs step 2;
2. runs `--all-json` and writes `site/src/data/versions.json`;
3. checks the version summaries, Polish and English (above);
4. checks page parity: every `.md` or `.mdx` under `src/content/docs/` outside `pl/` has a file at the same path under
   `pl/`, and the other way round; it names each missing file.

The functions of steps 3 and 4 are tested with `node --test` in `site/scripts/prebuild.test.mjs`: a missing summary (in
each file), an extra key, a long summary, a missing Polish page and a missing English page each fail with their name;
the Upgrading cut and the date format have cases too. The link
validator runs inside `astro build`.

### Look

The look is the board "L4 Noc" of the design canvas (owner decision 2026-11-26) in the colours of the board "P4 Cyjan"
(Decision 10). The values:

- **Fonts:** Space Grotesk for text and headings, IBM Plex Mono for code and the facts row.
- **Tokens**, set as Starlight CSS custom properties in `site/src/styles/theme.css`:

  | Token | Light | Dark |
  |---|---|---|
  | background | `#f5fafb` | `#04090c` |
  | surface | `#e9f3f5` | `#0c171c` |
  | border | `#cfe2e6` | `#1a3038` |
  | text | `#08171b` | `#ecf8fb` |
  | muted text | `#3f5a61` | `#9dbac2` |
  | code background | `#111216` | `#0c171c` |
  | code text | `#f4f4f2` | `#f4f4f2` |
  | code muted text | `#a9abb3` | `#a9abb3` |
  | ok | `#1d7348` | `#5fd08f` |
  | not ok | `#b3261e` | `#ff8a80` |
  | accent | `#22d3ee` | `#22d3ee` |
  | text on accent | `#0a0a0a` | `#0a0a0a` |

- **Photo behind the top:** on the Start page the whole top of the page; on inner pages a 280 px band above the title.
  CSS: a gradient from the background at 35 % to 55 % at 45 % to 100 % at the bottom, over the photo; the photo fills a
  1500 px high area at the top of the Start page, or the 280 px band, with `background-size: cover`, `no-repeat`,
  `center top`, so it is never stretched out of its proportions (Settled 13).
- **Photo tint:** between the gradient and the photo, a flat layer of `#0891b2` at 70 % opacity, with
  `background-blend-mode: normal, color, normal`. The photo keeps its light and dark and takes the tint's colour: a
  sea-blue night. Both themes use the tint. The photo files stay untinted, so one change of the CSS changes the colour.
- **Glass cards:** the background at 80 % opacity, a border of the text colour at 12 %, radius 22 px,
  `backdrop-filter: blur(18px)`. Where `backdrop-filter` is not supported, or `prefers-reduced-transparency: reduce`
  is set, the card uses the solid surface colour.
- **Photos** (`site/src/assets/photos/`), both from Unsplash photo `1480843669328-3f7e37d196ae`, WebP, 2200 × 1300,
  at most 400 KB each:
  - `city-dark.webp`: the photo as it is, cropped to cover;
  - `city-light.webp`: grayscale, inverted, contrast 1.15 (Open question 1).
  The photos are decorative: CSS backgrounds, no text in them, nothing a screen reader announces.
- **Polish letters:** every page renders ą, ć, ę, ł, ń, ó, ś, ź, ż in Space Grotesk (checked in the verification).
- **Accessibility:** text on every background and card at least 4.5:1, icons and borders that carry meaning at least
  3:1, measured over the brightest part of the photo under the card; touch targets at least 44 × 44 px; the layout
  works from 320 px wide; every page passes axe with no serious or critical issue.

### Workflow (`.github/workflows/site.yml`)

- Triggers: `push` with `branches: [main]` and `paths: [site/**, CHANGELOG.md, scripts/release-notes.py,
  .github/workflows/site.yml]`, and `workflow_dispatch`. A manual run from another branch fails at the `github-pages`
  environment, which allows `main` only; that is intended.
- `permissions`: `contents: read` at the top; the deploy job sets its own in full (`contents: read`, `pages: write`,
  `id-token: write`), since job permissions replace the top-level ones.
- `concurrency: { group: pages, cancel-in-progress: false }`.
- Job `build` on `ubuntu-latest`: `actions/checkout`, then `withastro/action` with `path: site`, `node-version: 24`,
  `package-manager: npm`. The action runs `npm ci` and `npm run build`, so the prebuild checks run.
- Job `deploy`, needs `build`, environment `github-pages`: `actions/deploy-pages`.
- Every `uses:` names a full 40-character commit SHA, followed by a comment with its tag (`# v5.0.0`).
- No secrets. No step that runs on a pull request or a tag.

### `scripts/check-pins.py`: two new rules

6. Every `uses:` in `.github/workflows/*.yml` names a 40-character hexadecimal commit SHA after `@`; a local action
   (`./...`) is the exception.
7. Every version in `dependencies` and `devDependencies` of `site/package.json` is exact (`1.2.3`): no `^`, `~`, `*`,
   range, tag or URL.

Both rules read the files of `git ls-files -co --exclude-standard`, as the other rules do. Before `site/package.json`
exists, rule 7 has nothing to check and passes. Rule 3 already scans every Markdown file outside `docs/superpowers/`,
so the site's pages fall under it too: their prose names images only pinned, like the README. Each new rule has a good
and a bad sample in `--self-test`.

### Repository settings (one time, owner's yes needed)

- Pages source "GitHub Actions": `gh api -X POST repos/MckCieply/Auth-Core/pages -f build_type=workflow`.
- The "Website" field: `gh repo edit MckCieply/Auth-Core --homepage https://mckcieply.github.io/Auth-Core/`.

Both are run at the merge, after the owner says yes to each.

### Documents

- **`docs/workflow.md`**: after the "Local-only" box, one paragraph: "One exception: `.github/workflows/site.yml` builds
  the project site and publishes it to GitHub Pages on a push to `main`. It runs no tests and gates no merge; the
  verification of the product stays local." The section "Branch & integration" names the same exception.
- **`docs/operations/releases.md`**: a step before the tag: "Add the version's summary, in Polish and in English, to
  `site/src/data/version-summaries.pl.json` and `version-summaries.en.json`, or the site's build fails." A step after the release: "Push `main` with the
  tag (`git push origin main v0.5.0`); the site rebuilds on the push and the Versions page shows the version."
- **`README.md`**: a "Site" link next to the release badge line, to `https://mckcieply.github.io/Auth-Core/`; the line
  "the specs (0001 to 0009)" becomes "the specs and the plans built from them"; the repository layout gains
  `site/  the project site (Astro Starlight), published to GitHub Pages`.
- **`site/CREDITS.md`**: the photo (Unsplash id, author, link to the photo page, "Unsplash License", the steps that made
  each file), the two fonts (name, licence, link).
- **Footer of every page**: "Server: BSL 1.1. Clients, samples and docs: MIT. City photo: Unsplash License." with links
  to `LICENSE`, `/license/` and `site/CREDITS.md`.

## Acceptance criteria (Done when)

Parser

1. `python scripts/release-notes.py --all-json` prints a JSON array with one object per version section of
   `CHANGELOG.md` in file order, each with `tag`, `kind`, `version`, `date`, `title`, `body` (guard: `--self-test` case
   "all-json order and kinds"; and on the real file, the length of the array equals the number of version headings,
   6 server and 1 python at `10592d9`).
2. Each `body` equals the output of `release-notes.py <tag>` (guard: `--self-test` case "all-json body"; and a check
   in the plan's verification that runs both for all 7 tags).
3. `## Unreleased` and other non-version headings are skipped; a version heading with no date or a bad date, and a
   duplicate version, fail with exit 1 (guard: `--self-test` cases "all-json skips unreleased", "all-json no date",
   "all-json bad date", "all-json duplicate").
4. The existing usages print the same bytes as before for all 7 tags (guard: a diff in the verification).

Build checks

5. `npm run build` in `site/` fails, naming the file and the version, when a server version has no summary in either
   file, and when a file has an extra key or a summary over 300 characters (guard: `prebuild.test.mjs` cases).
6. `npm run build` fails, naming the file, when an English page has no Polish page or a Polish page has no English
   page (guard: `prebuild.test.mjs` cases).
7. `npm run build` fails when the parser self-test fails (guard: verification runs the build with a broken sample once).
8. `npm run build` fails on a broken internal link (guard: `starlight-links-validator`, verified once with a broken
   link).
9. `python scripts/check-pins.py` refuses a `uses:` without a SHA and a `^` version in `site/package.json`, and passes
   on the repository (guard: `--self-test` cases for rules 6 and 7).

Pages

10. All five pages exist in English and Polish at the paths of the table; the language picker moves between the two
    versions of the same page (guard: the parity check; the verifier opens each pair).
11. The Start page's badge and three latest versions match the top of `CHANGELOG.md` and change when a section is added
    (guard: the verifier adds a fake `## 0.9.0` section and its two summaries locally, builds, sees both, and drops
    them).
12. The Versions page lists 0.4.0, 0.3.0, 0.2.0, 0.1.2, 0.1.1, 0.1.0 with dates, release links and image tags; the
    `Upgrading` subsections of 0.4.0, 0.3.0 and 0.2.0 sit in the aside and end before the next `###` or the
    "Full changelog" line; each page shows the summaries of its language (guard: `prebuild.test.mjs` cases for the
    cut and the dates; the verifier compares the page with the changelog).
13. Every product fact on the site is in the README at the build commit, except the five known limits, which are in
    the threat model (guard: the verifier lists each fact with its README or threat-model line).
14. Every competitor cell of the Comparison page has a source link; the page shows the checked date (guard: the
    verifier opens every source link).
15. The License page states the five cases, the price, the MIT parts and the Apache rule as the README and `LICENSE`
    do (guard: the verifier compares line by line).
16. The five known limits name the threat model's residual risks that say the same (guard: the verifier reads each).

Look and privacy

17. The Start page matches the L4 Noc board in both themes: photo behind the top, glass hero card, bento in the order
    above, the tokens of the table (guard: screenshots at 1440 px and 375 px, both themes, compared with the board by
    the owner).
18. Contrast, touch targets and axe as in "Look", in both themes (guard: axe run in the verification; contrast measured
    on the hero card and the accent card).
19. The built site requests nothing from another host and sets no cookie (guard: the verifier loads each page with the
    network log open; a search of `dist/` for `<script src>`, `<img src>`, `<link rel="stylesheet">`,
    `<link rel="preload">` and CSS `url(` that point at `http` or `https` finds none; `canonical`, `alternate` and
    `sitemap` links to the site's own origin are not requests and are allowed).
20. The site can be read with JavaScript off, apart from search, the theme picker, the language picker and the mobile
    menu; the footer's plain link moves between languages (guard: the verifier loads each page with JavaScript off).

Workflow and documents

21. `.github/workflows/site.yml` has the triggers, permissions and pinned actions above (guard: rule 6 of
    `check-pins.py`; the verifier reads the file).
22. After the merge, the owner's yes and the Pages setting, the workflow run is green and the three `curl` lines of
    the Goal answer as written (guard: the run's log and the `curl` output in the report).
23. `docs/workflow.md`, `docs/operations/releases.md`, `README.md`, `site/LICENSE` and `site/CREDITS.md` carry the text
    above, and `python scripts/check-docs.py` passes both with no argument (its own list) and with
    `docs/workflow.md docs/operations/releases.md site/CREDITS.md` (guard: `check-docs.py`; the verifier reads each).

## Decisions (owner, 2026-11-26)

1. **Purpose: "is it for me".** The site helps a developer decide; it does not teach integration. Quickstart stays a
   link to the README.
2. **Address and languages.** `mckcieply.github.io/Auth-Core`, English at the root, Polish under `/pl/`, every page in
   both.
3. **Astro Starlight**, five pages: Start, Is it for me, Comparison, License, Versions. No blog, newsletter or
   analytics.
4. **Versions from one parser.** `release-notes.py --all-json` feeds the site; the site shows server versions only;
   the Python package gets one line.
5. **Polish summaries, missing fails the build.** One to two sentences per version in a JSON file; the release steps
   gain "add the Polish summary before the tag"; the first summaries are written by Claude and approved by the owner.
6. **Publishing:** `.github/workflows/site.yml` with `withastro/action` and `actions/deploy-pages`, pinned by SHA; it
   builds the site only. The owner sets the Pages source once, or Claude does it with `gh api` after the owner's yes.
7. **Checks:** parser self-test, Polish summaries, page parity, link validator, build.
8. **Look: L4 Noc as built** ("nie komplikujmy"): the night-city photo behind the top, glass bento, headline D, Space
   Grotesk with IBM Plex Mono; no hybrid with other layouts.
9. **Product framing** (owner, 2026-11-26, README commit `10592d9`): Auth-Core is a product; price EUR 490 a year per
   product; MFA and passkeys "planned, no date"; "Auth-Core runs in production in Toviq" may be said in one sentence
   with no link; no external audit yet is stated. The site repeats these as the README does.
10. **Colours: palette "P4 Cyjan"** (owner, 2026-11-26, after six rounds of palettes on the fixed L4 layout): a
    sea-blue night, the photo tinted sea blue, one accent, cyan `#22d3ee`, with near-black text on it. Rejected: the
    board's gold; pairs of two accents (used equally, on frames only, on headings only); one dominant colour with a
    second as a symbol. The generator of the board is `.claude/design-0018/gen9.py` (not committed).

### Settled in this spec

1. **The README is the source of product facts**, so the site cannot promise what the README does not. Rejected:
   writing the site's copy freely and syncing later.
2. **The comparison uses the README's alternatives** (Better Auth, Keycloak, Clerk, WorkOS). The brainstorm's list
   (Keycloak, Auth0, Supabase Auth, ASP.NET Core Identity) came before the README commit and is dropped, so the two
   never disagree.
3. **No competitor prices**, as in the README; "how you pay" in words only.
4. **The Start page's version data comes from `versions.json`**, never typed, so a release cannot leave a stale badge.
5. **English summaries are written by hand too**, in `version-summaries.en.json` beside the Polish file, with the same
   check. A first sentence cut from the changelog was tried on paper and fails: the sections start with a spec link and
   a colon ("GitHub, and managing one's own logins (spec 0012): ..."), and 0.1.x start with "A patch release of". This
   extends owner decision 5 from one file to two.
6. **`backdrop-filter` has a solid fallback**, so text contrast never depends on blur support.
7. **The prebuild runs the checks**, so `npm run build` locally and `withastro/action` in the workflow run the same
   ones. Rejected: separate workflow steps, which a local build would skip.
8. **Rules 6 and 7 of `check-pins.py`** carry the pinning rule of the images to the workflow and the site's packages.
9. **Inner pages show a 280 px photo band**, so the five pages read as one site; the full photo stays on Start.
10. **One accent, no second colour.** Cyan marks the buttons, the badge, the "latest" pill, the licence card, the fact
    dots and the highlighted step of the diagram; nothing else is coloured.
11. **No computed Apache dates.** The licence counts three years from the day a version was first published, which can
    differ from the changelog date (`0.1.0` carries `2026-02-24` and was published later). A wrong date on a licence
    page misleads, so the page states the rule only.
12. **The Versions page shows the changelog date** of each version, labelled as the version's date and never as the
    start of the licence clock, which the License page explains in words.
13. **The photo is placed with `cover`**, not the board's `100% 1500px`, which stretched it on wide screens; the
    screenshots of the verification show the owner the result.

## Open questions for the owner

1. **The light theme's photo.** The approved board uses the dark photo turned gray and inverted. The later gallery had
   real light photos (fog, dawn, "G12 Mgła"). Keep the inverted photo (default), or use a light photo?
2. **The summaries of Appendix B**, Polish and English: approve or correct them.

## Verification notes (for the local verifiers)

- Build from a clean clone: `cd site && npm ci && npm run build`, on Windows (Git Bash) and in the Linux test container.
- Break each check once and see the build fail with the named cause: remove a Polish summary, remove a Polish page, break
  an internal link, break the parser self-test sample.
- Compare `--all-json` bodies with `release-notes.py <tag>` for all 7 tags, byte for byte.
- Screenshots of the Start page at 1440 px and 375 px, light and dark, for the owner's comparison with the L4 board.
- Run axe on every page in both themes; measure contrast on the hero card and the accent card over the brightest part
  of the photo.
- Load every page with the network log open: no request to another host, no cookie.
- Open every competitor source link and check the cell against the page.

## To verify during implementation

1. The latest stable Astro, Starlight and `starlight-links-validator` versions, and that the validator works with the
   `base` path and the `root` locale.
2. The current SHAs and tags of `actions/checkout`, `withastro/action` and `actions/deploy-pages`, and that
   `withastro/action` accepts `node-version: 24` and runs the `prebuild` script.
3. That `python3` is on `ubuntu-latest` and the prebuild's fallback to `python` works in Git Bash on Windows.
4. The author's name and the page of Unsplash photo `1480843669328-3f7e37d196ae`, for the credits. Downloading the
   photo needs the owner's yes.
5. The weight of both WebP files (at most 400 KB) and the contrast over them.
6. The threat model's residual risk numbers 3, 4, 13, 15 and 17 at the build commit.
7. That `prefers-reduced-transparency` is honoured where supported and harmless elsewhere.

## Appendix A: Start page copy

The text of the approved board, checked against the README and the changelog at `e8f78a8` and updated for `10592d9`
(price, MFA). The badge and the version lines are built from `versions.json`; the text here shows them at 0.4.0.

| Element | English | Polish |
|---|---|---|
| Badge | v0.4.0 · 24 Nov 2026 · latest | v0.4.0 · 24 lis 2026 · najnowsza |
| Headline | Sign-in for products used by many companies. | Logowanie dla produktów, z których korzysta wiele firm. |
| Subline | Auth-Core is a .NET service that runs next to your product, at /auth on your domain. It handles sign-in, password reset, invitations, companies and roles. Your backend checks its tokens offline, in any language. | Auth-Core to usługa w .NET, która działa obok Twojego produktu, pod /auth w Twojej domenie. Obsługuje logowanie, reset hasła, zaproszenia, firmy i role. Twój backend sprawdza jej tokeny offline, w dowolnym języku. |
| Buttons | Check if it fits · Quickstart on GitHub | Sprawdź, czy pasuje · Szybki start na GitHubie |
| Facts | .NET 10 · PostgreSQL · Docker, linux/amd64 · RS256 JWT + JWKS · REST API, your own screens | .NET 10 · PostgreSQL · Docker, linux/amd64 · RS256 JWT + JWKS · REST API, Twoje ekrany |
| Benefits heading | What you get | Co dostajesz |
| Benefit 1 | **Login you build once.** One container and a database give you sign-in, sessions, password reset, email verification, invitations, companies and roles. The same in every product. | **Logowanie budujesz raz.** Jeden kontener i baza dają logowanie, sesje, reset hasła, weryfikację e-maila, zaproszenia, firmy i role. Tak samo w każdym produkcie. |
| Benefit 2 | **Safe defaults.** The refresh token sits in an HttpOnly cookie and rotates on every use. The access token lasts 10 minutes and lives in memory only. Lockout, rate limits and an audit log are on from the start. | **Bezpieczne od startu.** Refresh token siedzi w ciasteczku HttpOnly i zmienia się przy każdym użyciu. Access token żyje 10 minut, tylko w pamięci. Blokada konta, limity zapytań i log audytu działają od pierwszego dnia. |
| Benefit 3 | **Out of your API path.** Your backend checks each token against public keys it caches. Your API calls never pass through Auth-Core. | **Poza ścieżką Twojego API.** Twój backend sprawdza każdy token kluczami publicznymi z cache. Wywołania Twojego API nie przechodzą przez Auth-Core. |
| Benefit 4 | **Your data, your server.** Users stay in your PostgreSQL database and you pay no per-user fee. Sign-in with Google, Microsoft and GitHub is there when you turn it on. | **Twoje dane, Twój serwer.** Użytkownicy zostają w Twojej bazie PostgreSQL i nie płacisz za użytkownika. Logowanie przez Google, Microsoft i GitHub włączasz, kiedy chcesz. |
| Diagram heading | How it sits next to your product | Jak to stoi obok Twojego produktu |
| Diagram boxes | Your frontend: draws the login, reset and invitation screens · Your reverse proxy: HTTPS, one domain · /auth · Auth-Core: issues tokens, serves the keys, manages companies · /api · your backend: checks the JWT offline | Twój frontend: rysuje ekrany logowania, resetu i zaproszeń · Twój reverse proxy: HTTPS, jedna domena · /auth · Auth-Core: wydaje tokeny, udostępnia klucze, zarządza firmami · /api · Twój backend: sprawdza JWT offline |
| Licence card heading | License and price | Licencja i cena |
| Licence card | Source-available under the Business Source License 1.1. Free for development, testing and evaluation, for personal non-commercial projects, and for non-commercial use by non-profit or educational organisations. A company running it in production, even internally, needs a commercial licence: EUR 490 a year per product, net of VAT, any number of users. Each version becomes Apache 2.0 three years after it is published. | Kod dostępny na licencji Business Source License 1.1. Za darmo do developmentu, testów i oceny, do projektów osobistych bez celów komercyjnych oraz do niekomercyjnego użytku organizacji non-profit i edukacyjnych. Firma, która uruchamia go produkcyjnie, nawet wewnętrznie, potrzebuje licencji komercyjnej: 490 EUR netto rocznie za produkt, dowolna liczba użytkowników. Każda wersja przechodzi na Apache 2.0 trzy lata po publikacji. |
| Licence link | Read the license in plain words | Licencja prostymi słowami |
| Fit heading | Is it for me? | Czy to dla mnie? |
| Good fit if | you build a product where companies, their members and roles matter · your backend can check a JWT, in any language · you can run a Linux server with Docker, a domain and an SMTP relay | budujesz produkt, w którym liczą się firmy, ich członkowie i role · Twój backend umie sprawdzić JWT, w dowolnym języku · możesz utrzymać serwer z Linuksem, Dockerem, domeną i SMTP |
| Look elsewhere if | you want ready-made login screens: your frontend draws them · you need MFA today: it is planned, with no date · several products must share one pool of users · you need SAML, SCIM or LDAP | chcesz gotowych ekranów logowania: rysuje je Twój frontend · potrzebujesz MFA już dziś: jest w planach, bez daty · kilka produktów ma dzielić jedną pulę użytkowników · potrzebujesz SAML, SCIM albo LDAP |
| Versions heading | Latest versions · All versions and the changelog | Najnowsze wersje · Wszystkie wersje i changelog |
| Steps heading | Three steps to the first sign-in | Trzy kroki do pierwszego logowania |
| Step 1 | **Declare permissions.** List the permissions your code checks and the roles a new company starts with, in one YAML file. | **Zadeklaruj uprawnienia.** W jednym pliku YAML wypisz uprawnienia, które sprawdza Twój kod, i role, z którymi startuje nowa firma. |
| Step 2 | **Run it on your server.** A production compose file and a guide take you from a Linux server with Docker, a domain and an SMTP relay to a first sign-in. | **Uruchom na swoim serwerze.** Gotowy plik compose i przewodnik prowadzą od serwera z Linuksem, Dockerem, domeną i SMTP do pierwszego logowania. |
| Step 3 | **Create the first company.** One command creates the company, a second invites its admin. The admin invites the rest. | **Załóż pierwszą firmę.** Jedno polecenie tworzy firmę, drugie zaprasza jej admina. Admin zaprasza resztę. |
| Footer | Server: BSL 1.1. Clients, samples and docs: MIT. City photo: Unsplash License. · Edit this page on GitHub | Serwer: BSL 1.1. Klienty, przykłady i dokumentacja: MIT. Zdjęcie miasta: licencja Unsplash. · Edytuj tę stronę na GitHubie |
| Navigation | Start · Is it for me? · Comparison · License · Versions | Start · Czy to dla mnie? · Porównanie · Licencja · Wersje |

The YAML of step 1, the README's example as it is:

```yaml
permissions: [documents:read, documents:write, documents:approve]
default_roles:
  admin: ["*"]
  user: [documents:read, documents:write]
```

## Appendix B: version summaries (draft, for the owner's approval)

`version-summaries.pl.json`:

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

`version-summaries.en.json`:

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
