# Auth-Core: instructions for Claude

## Release condition

Do not create a version tag until both site summary files carry that version:

- `site/src/data/version-summaries.en.json`
- `site/src/data/version-summaries.pl.json`

One to two sentences per version, written by hand in each language. Without them the site's build fails on the next
push to `main`. The full order of a release is in [`docs/operations/releases.md`](docs/operations/releases.md),
"Before the tag".
