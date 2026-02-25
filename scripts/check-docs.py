#!/usr/bin/env python3
"""Checks the documents of spec 0008 against the repository: no Docker, no network, the standard library only.

Usage, from the repository root:  python scripts/check-docs.py [file.md ...]
                                  python scripts/check-docs.py --self-test
With no file it checks the documents of the release (the list below). Exit status 0 when everything resolves, 1 otherwise.
`--self-test` runs every rule on a good and a bad sample (no repository needed) and exits 0 when each behaves.

What it checks in each document:
  1. every relative link ends at a file or a directory of the repository;
  2. every code span that is a path of the repository (it starts with src/, tests/, docs/, deploy/, scripts/, samples/ or clients/)
     is one;
  3. every code span that is a bare file name (`token-from-url.ts`, `e2e/02-session.spec.ts`, `docker-compose.prod.yml`, `Caddyfile`)
     is a path of the repository or the end of the path (after a slash) of at least one file;
  4. every code span that names a test class (`SecurityHeadersTests`) has a file of that name under tests/ or declares that class, and
     `Class.Method` has a method of that name declared in it (`void Name(` or `Task Name(`); a span that starts with a dot and is a
     test method name (`.Some_method`) is checked against the class named last on the same line;
  5. every setting or variable that looks like one: `AUTH_...` and `POSTGRES_...` are in deploy/.env.prod.example or
     deploy/docker-compose.prod.yml, and `Auth:Section:Key` ends in a name that appears in a .cs file under src/;
  6. no emoji or pictograph.

Every file and path is looked up in the list of files that git knows (tracked and untracked, `git ls-files -co --exclude-standard`),
not on disk, so the check is case-exact on a case-insensitive file system (Windows, macOS) and sees new files before they are committed.
"""
import pathlib
import posixpath
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
CONFIG_FILES = ("deploy/.env.prod.example", "deploy/docker-compose.prod.yml")
FILE_NAME = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_./@-]*\.(ts|tsx|cs|py|sh|yml|yaml|md|json|mjs|toml|csproj|props|slnx)$")
BARE_NAMES = {"Caddyfile", "Dockerfile"}
TEST_CLASS = re.compile(r"^([A-Z]\w*Tests)(?:\.(\w+))?$")
TEST_METHOD = re.compile(r"^\.([A-Z][A-Za-z0-9]*_\w+)$")
VARIABLE = re.compile(r"^(AUTH|POSTGRES)_[A-Z0-9_]+$")
SETTING = re.compile(r"^Auth(:[A-Za-z0-9]+)+$")
SKIPPED_SPAN = re.compile(r"[\s*<>{}=$()|,:\\]")
EMOJI = re.compile("[\U00002600-\U000027BF" "\U00002B00-\U00002BFF" "\U0001F000-\U0001FAFF]")


class Repo:
    """What the checks look things up in: the files git knows, the test sources, the configuration and the server sources."""

    def __init__(self, files, sources, config_text, server_text):
        self.files = set(files)
        self.test_sources = {name: text for name, text in sources.items() if name.startswith("tests/") and name.endswith(".cs")}
        self.config_text = config_text
        self.server_text = server_text

    def has(self, relative):
        """A file of the repository, or a directory that holds one: the exact spelling, case included."""
        relative = relative.rstrip("/")
        return relative in self.files or any(f.startswith(relative + "/") for f in self.files)

    def class_source(self, name):
        """The text of the file that declares the test class, or None."""
        exact = [text for path, text in self.test_sources.items() if path.rsplit("/", 1)[-1] == name + ".cs"]
        if exact:
            return exact[0]
        for text in self.test_sources.values():
            if re.search(r"\bclass\s+" + re.escape(name) + r"\b", text):
                return text
        return None

    def declares(self, source, method):
        """The source declares a test method of that name: a return type of void, Task or ValueTask, then the name and a bracket."""
        return re.search(r"\b(?:void|Task|ValueTask)(?:<[^>\n]*>)?\s+" + re.escape(method) + r"\s*\(", source) is not None


def check_text(repo, name, text):
    """The problems of one document. `name` is its path from the repository root, with forward slashes."""
    problems = []
    folder = posixpath.dirname(name)
    for target in re.findall(r"\]\(([^)#\s]+)(?:#[^)]*)?\)", text):
        if target.startswith(("http://", "https://", "mailto:")):
            continue
        relative = posixpath.normpath(posixpath.join(folder, target))
        if relative.startswith("..") or not repo.has(relative):
            problems.append(f"{name}: the link {target} does not resolve")
    for number, line in enumerate(text.splitlines(), start=1):
        last_class = None
        for span in re.findall(r"`([^`\n]+)`", line):
            span = span.strip()
            if VARIABLE.match(span):
                if span not in repo.config_text:
                    problems.append(f"{name}:{number}: the variable {span} is in neither deploy/.env.prod.example nor deploy/docker-compose.prod.yml")
                continue
            if SETTING.match(span):
                last = span.rsplit(":", 1)[-1]
                if not re.search(r"\b" + re.escape(last) + r"\b", repo.server_text):
                    problems.append(f"{name}:{number}: the setting {span} names {last}, which no source under src/ has")
                continue
            if SKIPPED_SPAN.search(span):
                continue
            if span.startswith(ROOTS):
                if not repo.has(span):
                    problems.append(f"{name}:{number}: the path {span} does not exist")
                continue
            if FILE_NAME.match(span) or span in BARE_NAMES:
                if span not in repo.files and not any(f.endswith("/" + span) for f in repo.files):
                    problems.append(f"{name}:{number}: the file {span} is no file of the repository")
                continue
            found = TEST_CLASS.match(span)
            if found:
                last_class = found.group(1)
                source = repo.class_source(last_class)
                if source is None:
                    problems.append(f"{name}:{number}: the test class {last_class} does not exist")
                elif found.group(2) and not repo.declares(source, found.group(2)):
                    problems.append(f"{name}:{number}: {last_class} has no test {found.group(2)}")
                continue
            method = TEST_METHOD.match(span)
            if method and last_class:
                source = repo.class_source(last_class)
                if source is not None and not repo.declares(source, method.group(1)):
                    problems.append(f"{name}:{number}: {last_class} has no test {method.group(1)}")
    if EMOJI.search(text):
        problems.append(f"{name}: an emoji")
    return problems


def load_repo(root):
    listing = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-co", "--exclude-standard"], capture_output=True, text=True, check=True
    ).stdout.splitlines()
    files = {line.strip() for line in listing if line.strip()}

    def read(name):
        return (root / name).read_text(encoding="utf-8", errors="replace")

    sources = {name: read(name) for name in files if name.startswith("tests/") and name.endswith(".cs")}
    config_text = "\n".join(read(name) for name in CONFIG_FILES if name in files)
    server_text = "\n".join(read(name) for name in files if name.startswith("src/") and name.endswith(".cs"))
    return Repo(files, sources, config_text, server_text)


def self_test():
    """Every rule on a good and a bad sample, against a small made-up repository. Returns the list of failures."""
    repo = Repo(
        files={"docs/b.md", "docs/sub/c.md", "src/Auth.Server/Network/ProxySettings.cs", "tests/FooTests.cs", "samples/web/tok.ts", "Caddyfile"},
        sources={
            "tests/FooTests.cs": "class FooTests {\n"
            "  // C_d is only named in a comment\n"
            "  [Fact] public async Task A_b() {}\n"
            "  [Theory] public void E_f(int x) {}\n"
            "}\n"
        },
        config_text="AUTH_PORT=8080\nPOSTGRES_PASSWORD=x\n",
        server_text='public const string KnownProxiesKey = "Auth:Proxy:KnownProxies";',
    )
    cases = [
        # (rule, text, number of problems expected)
        ("link, good", "[a](b.md) and [c](sub/c.md) and [d](sub/)", 0),
        ("link, bad", "[a](nope.md)", 1),
        ("link, wrong case", "[a](B.md)", 1),
        ("link, outside the repository", "[a](../../x.md)", 1),
        ("path, good", "`docs/b.md` and `src/Auth.Server/`", 0),
        ("path, bad", "`docs/nope.md`", 1),
        ("path, wrong case", "`docs/B.md`", 1),
        ("file name, good", "`c.md` and `tok.ts` and `Caddyfile`", 0),
        ("file name, bad", "`nope.ts`", 1),
        ("test class, good", "`FooTests`", 0),
        ("test class, bad", "`BarTests`", 1),
        ("Class.Method, good (async Task)", "`FooTests.A_b`", 0),
        ("Class.Method, good (void, theory)", "`FooTests.E_f`", 0),
        ("Class.Method, bad", "`FooTests.G_h`", 1),
        ("Class.Method, named only in a comment", "`FooTests.C_d`", 1),
        (".Method, good", "`FooTests` then `.A_b`", 0),
        (".Method, bad", "`FooTests` then `.G_h`", 1),
        (".Method, not a test name", "`FooTests` then `.env`", 0),
        ("variable, good", "`AUTH_PORT` and `POSTGRES_PASSWORD`", 0),
        ("variable, bad", "`AUTH_NOPE`", 1),
        ("setting, good", "`Auth:Proxy:KnownProxies`", 0),
        ("setting, bad", "`Auth:Proxy:Nope`", 1),
        ("emoji, none", "plain text with arrows -> and a box "+chr(0x2500), 0),
        ("emoji, a check mark", "done " + chr(0x2705), 1),
        ("emoji, a rocket", "go " + chr(0x1F680), 1),
    ]
    failures = []
    for rule, text, expected in cases:
        got = check_text(repo, "docs/x.md", text)
        if len(got) != expected:
            failures.append(f"self-test {rule}: expected {expected} problem(s), got {len(got)}: {got}")
    return failures


def main(arguments):
    if arguments == ["--self-test"]:
        failures = self_test()
        print("\n".join(failures) or "self-test passed: every rule catches its bad sample and lets its good one through")
        return 1 if failures else 0
    root = pathlib.Path(__file__).resolve().parent.parent
    repo = load_repo(root)
    problems = []
    for document in arguments or DOCUMENTS:
        path = root / document
        if not path.exists():
            problems.append(f"{document}: the document does not exist")
            continue
        problems.extend(check_text(repo, pathlib.PurePath(document).as_posix(), path.read_text(encoding="utf-8")))
    print("\n".join(problems) or "all links, paths, files and tests resolve")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
