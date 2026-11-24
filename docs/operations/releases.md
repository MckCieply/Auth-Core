# Publishing a release on GitHub

A tag alone shows nothing on the repository page. The GitHub Release is what people see in the sidebar, and it is what "Watch, Releases" mails them. So every version that gets a tag also gets a release, written from its section of [`CHANGELOG.md`](../../CHANGELOG.md).

Do it after the annotated tag is pushed, and for the service after the image is pushed to GHCR. The section of the changelog must exist before the tag is made: `scripts/release-notes.py` exits 1 when it finds none.

## The service

```bash
python scripts/release-notes.py v0.5.0 > /tmp/notes.md
gh release create v0.5.0 --verify-tag --title "$(python scripts/release-notes.py --title v0.5.0)" --notes-file /tmp/notes.md --latest
```

The script prints the body of the section: the heading is left out (the title carries it), lines that the changelog wraps are joined (GitHub shows a single line break as a break), relative links become links to the tag on GitHub, and the last line points to the whole changelog.

## The Python package

```bash
python scripts/release-notes.py python-v0.1.2 > /tmp/notes.md
gh release create python-v0.1.2 --verify-tag --title "$(python scripts/release-notes.py --title python-v0.1.2)" --notes-file /tmp/notes.md --latest=false
```

`--latest=false` keeps the "Latest" mark of the repository on the service. The badge in the README filters tags that start with `v`, so the package does not move it either.

## Editing a release later

```bash
gh release edit v0.5.0 --notes-file /tmp/notes.md
```

Run the script again first if the changelog changed.
