#!/usr/bin/env python3
"""Prints the body of a GitHub Release from CHANGELOG.md: the standard library only, no network.

Usage, from anywhere:  python scripts/release-notes.py <tag>           (the release body, on stdout)
                       python scripts/release-notes.py --title <tag>   (the release title only)
                       python scripts/release-notes.py --all-json      (every version section as one JSON array, on stdout)
                       python scripts/release-notes.py --self-test
Exit status 0 on success, 1 when the tag is malformed or has no section in CHANGELOG.md, or (--all-json) when a version heading breaks
rule 5. `--self-test` runs the rules on small inline samples (no repository needed).

What it does:
  1. tag `vX.Y.Z` maps to the heading `## X.Y.Z - <date>`, tag `python-vX.Y.Z` to `## python-vX.Y.Z - <date>`;
  2. the body is everything after that heading up to the next `## ` heading or the end of the file, blank lines at both ends trimmed
     (the heading itself is the release title, so it is not in the body; `### ` subheadings stay); lines that the changelog wraps by
     hand are joined with one space (GitHub shows a single newline as a line break), except inside a fenced block;
  3. every relative link target is rewritten to https://github.com/MckCieply/Auth-Core/blob/<tag>/<target>; a target that starts with
     `#` points into CHANGELOG.md at that tag; absolute links (http://, https://, mailto:) and text in backticks stay as they are;
  4. the body ends with a blank line and a line that links to CHANGELOG.md at that tag.
  5. `--all-json` prints one object per version section (a heading `## X.Y.Z` or `## python-vX.Y.Z`), in the order of the file, with
     `tag`, `kind` (`server` or `python`), `version`, `date`, `title` and `body` (the text of items 2-4); every version heading must
     be `## <version> - YYYY-MM-DD` with a real date and appear once, and other `## ` headings (`## Unreleased`) are skipped.
`--title` gives `Auth-Core X.Y.Z` for `vX.Y.Z` and `auth-core-fastapi X.Y.Z` for `python-vX.Y.Z`, from the tag alone.
"""
import datetime
import json
import pathlib
import re
import sys

REPO_URL = "https://github.com/MckCieply/Auth-Core"
TAG = re.compile(r"^(python-)?v(\d+\.\d+\.\d+)$")
LINK = re.compile(r"\]\(([^)\s]+)((?:\s+\"[^\"]*\")?)\)")
CODE_SPAN = re.compile(r"(``.*?``|`[^`]*`)")
ABSOLUTE = ("http://", "https://", "mailto:")
BLOCK_START = re.compile(r"^(#|[-*+] |\d+\. |> |\|)")
VERSION_HEADING = re.compile(r"^## (python-v)?\d+\.\d+\.\d+( |$)")
DATED_HEADING = re.compile(r"^## ((?:python-v)?)(\d+\.\d+\.\d+) - (\d{4}-\d{2}-\d{2})$")


def parse_tag(tag):
    """Returns (is_python, version) or None for a malformed tag."""
    m = TAG.match(tag)
    return (bool(m.group(1)), m.group(2)) if m else None


def title_for(tag):
    parsed = parse_tag(tag)
    if parsed is None:
        return None
    is_python, version = parsed
    return ("auth-core-fastapi " if is_python else "Auth-Core ") + version


def heading_prefix(tag):
    is_python, version = parse_tag(tag)
    return "## " + ("python-v" if is_python else "") + version + " - "


def section(text, tag):
    """The trimmed lines of the section of `tag`, or None when the file has none."""
    lines = text.split("\n")
    prefix = heading_prefix(tag)
    start = next((i for i, line in enumerate(lines) if line.startswith(prefix)), None)
    if start is None:
        return None
    end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith("## ")), len(lines))
    body = lines[start + 1:end]
    while body and not body[0].strip():
        body.pop(0)
    while body and not body[-1].strip():
        body.pop()
    return body


def unwrap(lines):
    """Joins hard-wrapped lines (a release body shows a single newline as a line break). A line joins the one before it with one space
    unless either is blank, a heading or a fence line, or the line starts a block (`#`, `- `, `* `, `+ `, `1. `, `> `, `|`).
    Inside a fenced block nothing changes."""
    out = []
    in_fence = False
    joinable = False  # the last line of `out` is text that a continuation may be joined to
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("```"):
            in_fence = not in_fence
            out.append(line)
            joinable = False
        elif in_fence:
            out.append(line)
        elif not stripped:
            out.append(line)
            joinable = False
        elif joinable and not BLOCK_START.match(stripped):
            out[-1] = out[-1] + " " + stripped
        else:
            out.append(line)
            joinable = not stripped.startswith("#")
    return out


def rewrite_links(line, tag):
    base = f"{REPO_URL}/blob/{tag}/"

    def fix(m):
        target, rest = m.group(1), m.group(2)
        if target.startswith(ABSOLUTE):
            return m.group(0)
        if target.startswith("#"):
            target = "CHANGELOG.md" + target
        return f"]({base}{target}{rest})"

    parts = CODE_SPAN.split(line)
    for i in range(0, len(parts), 2):  # the odd parts are code spans
        parts[i] = LINK.sub(fix, parts[i])
    return "".join(parts)


def release_body(text, tag):
    body = section(text, tag)
    if body is None:
        return None
    out = [rewrite_links(line, tag) for line in unwrap(body)]
    out += ["", f"Full changelog: [CHANGELOG.md]({REPO_URL}/blob/{tag}/CHANGELOG.md)"]
    return "\n".join(out) + "\n"


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


def self_test():
    sample = (
        "# Changelog\n\n## 0.2.0 - 2026-01-02\n\nSummary [a](docs/a.md) and [b](https://x.y/z) and `[c](docs/c.md)`.\n\n"
        "### Added\n- see [top](#added) [m](mailto:a@b.c) [t](docs/t.md \"T\")\n\n\n## 0.1.0 - 2026-01-01\n\nOld.\n\n"
        "## python-v0.1.1 - 2026-01-03\n\nPy.\n"
    )
    failures = []

    def check(name, ok):
        if not ok:
            failures.append(name)

    body = release_body(sample, "v0.2.0") or ""
    check("heading not in body", "## 0.2.0" not in body and body.startswith("Summary"))
    check("next section cut", "Old." not in body and "Py." not in body)
    check("subheading stays", "### Added" in body)
    check("relative link", f"]({REPO_URL}/blob/v0.2.0/docs/a.md)" in body)
    check("absolute link", "](https://x.y/z)" in body and "](mailto:a@b.c)" in body)
    check("anchor link", f"]({REPO_URL}/blob/v0.2.0/CHANGELOG.md#added)" in body)
    check("link title kept", "docs/t.md \"T\")" in body)
    check("code span untouched", "`[c](docs/c.md)`" in body)
    check("footer", body.endswith(f"\n\nFull changelog: [CHANGELOG.md]({REPO_URL}/blob/v0.2.0/CHANGELOG.md)\n"))
    check("last section", (release_body(sample, "python-v0.1.1") or "").startswith("Py.\n"))
    check("python tag is not v tag", release_body(sample, "v0.1.1") is None)
    check("missing section", release_body(sample, "v0.9.0") is None)
    check("malformed tag", parse_tag("0.1.0") is None and parse_tag("v1.2") is None and parse_tag("python-0.1.0") is None)
    check("titles", title_for("v0.4.0") == "Auth-Core 0.4.0" and title_for("python-v0.1.2") == "auth-core-fastapi 0.1.2")
    check("wrapped paragraph", unwrap(["one two", "three four", "", "next"]) == ["one two three four", "", "next"])
    check("wrapped list item", unwrap(["- item one", "  both; GET x", "  and more"]) == ["- item one both; GET x and more"])
    check("two list items", unwrap(["- a", "- b", "1. c", "2. d"]) == ["- a", "- b", "1. c", "2. d"])
    check("heading then text", unwrap(["### Added", "text", "more"]) == ["### Added", "text more"])
    check("fence unchanged", unwrap(["intro", "```bash", "a", "  b", "```", "after"]) == ["intro", "```bash", "a", "  b", "```", "after"])
    check("table rows", unwrap(["| a | b |", "| - | - |"]) == ["| a | b |", "| - | - |"])
    check("unwrap keeps links", "](docs/a.md)" in unwrap(["see [a](docs/a.md)", "and `x`"])[0])
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
    for name in failures:
        print(f"self-test FAILED: {name}", file=sys.stderr)
    if not failures:
        print("self-test ok")
    return 1 if failures else 0


def main(argv):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    args = argv[1:]
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
    title_only = bool(args) and args[0] == "--title"
    if title_only:
        args = args[1:]
    if len(args) != 1:
        print("usage: release-notes.py [--title] <tag> | --all-json | --self-test", file=sys.stderr)
        return 1
    tag = args[0]
    title = title_for(tag)
    if title is None:
        print(f"malformed tag '{tag}': expected vX.Y.Z or python-vX.Y.Z", file=sys.stderr)
        return 1
    if title_only:
        sys.stdout.write(title + "\n")
        return 0
    changelog = pathlib.Path(__file__).resolve().parent.parent / "CHANGELOG.md"
    body = release_body(changelog.read_text(encoding="utf-8"), tag)
    if body is None:
        print(f"no section for '{tag}' in CHANGELOG.md", file=sys.stderr)
        return 1
    sys.stdout.write(body)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
