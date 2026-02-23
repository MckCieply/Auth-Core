#!/usr/bin/env python3
"""Checks the documents of spec 0008 against the repository: no Docker, no network, the standard library only.

Usage, from the repository root:  python scripts/check-docs.py [file.md ...]
With no file it checks the documents of the release (the list below). Exit status 0 when everything resolves, 1 otherwise.

What it checks in each document:
  1. every relative link ends at a file or a directory that exists;
  2. every code span that is a path of the repository (it starts with src/, tests/, docs/, deploy/, scripts/, samples/ or clients/)
     exists;
  3. every code span that is a bare file name (`token-from-url.ts`, `e2e/02-session.spec.ts`, `docker-compose.prod.yml`, `Caddyfile`)
     is a path of the repository or the end of the path (after a slash) of at least one file;
  4. every code span that names a test class (`SecurityHeadersTests`) has a file of that name under tests/ or declares that class, and
     `Class.Method` has the method in it; a span that starts with a dot and is a method name (`.Some_method`) is checked against the
     class named last on the same line;
  5. no emoji.
"""
import pathlib
import re
import subprocess
import sys

DOCUMENTS = [
    "README.md",
    "CHANGELOG.md",
    "docs/deployment/vps.md",
    "docs/security/threat-model.md",
    "docs/operations/backup.md",
    "docs/operations/key-rotation.md",
    "clients/python/README.md",
]
ROOTS = ("src/", "tests/", "docs/", "deploy/", "scripts/", "samples/", "clients/")
FILE_NAME = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_./@-]*\.(ts|tsx|cs|py|sh|yml|yaml|md|json|mjs|toml|csproj|props|slnx)$")
BARE_NAMES = {"Caddyfile", "Dockerfile"}
TEST_CLASS = re.compile(r"^([A-Z]\w*Tests)(?:\.(\w+))?$")
TEST_METHOD = re.compile(r"^\.(\w+)$")
EMOJI = re.compile("[\U0001F300-\U0001FAFF]")

root = pathlib.Path(__file__).resolve().parent.parent
listing = subprocess.run(
    ["git", "-C", str(root), "ls-files", "-co", "--exclude-standard"], capture_output=True, text=True, check=True
).stdout.splitlines()
files = {line.strip() for line in listing if line.strip()}
test_sources = {name: (root / name).read_text(encoding="utf-8", errors="replace") for name in files if name.startswith("tests/") and name.endswith(".cs")}


def class_source(name):
    """The text of the file that declares the test class, or None."""
    exact = [text for path, text in test_sources.items() if path.rsplit("/", 1)[-1] == name + ".cs"]
    if exact:
        return exact[0]
    for text in test_sources.values():
        if re.search(r"\bclass\s+" + re.escape(name) + r"\b", text):
            return text
    return None


def check(name):
    path = root / name
    if not path.exists():
        return [f"{name}: the document does not exist"]
    problems = []
    text = path.read_text(encoding="utf-8")
    for target in re.findall(r"\]\(([^)#\s]+)(?:#[^)]*)?\)", text):
        if target.startswith(("http://", "https://", "mailto:")):
            continue
        if not (path.parent / target).resolve().exists():
            problems.append(f"{name}: the link {target} does not resolve")
    for number, line in enumerate(text.splitlines(), start=1):
        last_class = None
        for span in re.findall(r"`([^`\n]+)`", line):
            span = span.strip()
            if re.search(r"[\s*<>{}=$()|,:\\]", span):
                continue
            if span.startswith(ROOTS):
                if not (root / span.rstrip("/")).exists():
                    problems.append(f"{name}:{number}: the path {span} does not exist")
                continue
            if FILE_NAME.match(span) or span in BARE_NAMES:
                if span not in files and not any(f.endswith("/" + span) for f in files):
                    problems.append(f"{name}:{number}: the file {span} is no file of the repository")
                continue
            found = TEST_CLASS.match(span)
            if found:
                last_class = found.group(1)
                source = class_source(last_class)
                if source is None:
                    problems.append(f"{name}:{number}: the test class {last_class} does not exist")
                elif found.group(2) and not re.search(r"\b" + re.escape(found.group(2)) + r"\b", source):
                    problems.append(f"{name}:{number}: {last_class} has no test {found.group(2)}")
                continue
            method = TEST_METHOD.match(span)
            if method and last_class:
                source = class_source(last_class)
                if source is not None and not re.search(r"\b" + re.escape(method.group(1)) + r"\b", source):
                    problems.append(f"{name}:{number}: {last_class} has no test {method.group(1)}")
    if EMOJI.search(text):
        problems.append(f"{name}: an emoji")
    return problems


def main(arguments):
    documents = arguments or DOCUMENTS
    problems = []
    for document in documents:
        problems.extend(check(document))
    print("\n".join(problems) or "all links, paths, files and tests resolve")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
