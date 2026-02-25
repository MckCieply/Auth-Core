#!/usr/bin/env python3
"""Checks that what the images are built from cannot change under us, and that the build gets what it needs: no Docker, no network, the
standard library only. (A separate script from scripts/check-docs.py, which checks the documents; this one checks the build and the
deployment files, and also the images that the documents name.)

Usage, from the repository root:  python scripts/check-pins.py
                                  python scripts/check-pins.py --self-test
Exit status 0 when everything holds, 1 otherwise. `--self-test` runs every rule on a good and a bad sample (no repository needed).

What it checks:
  1. every `image:` line of a compose file (deploy/*.yml, samples/**/compose.yml, scripts/*.yml) names its image by digest
     (`name:tag@sha256:<64 hex digits>`); the exceptions are the release image of Auth-Core itself (`ghcr.io/mckcieply/auth-core:`, a
     version chosen by the operator) and an image that the same compose file builds (`notes-web-caddy:local`);
  2. every `FROM` of a Dockerfile (any file named Dockerfile*) names its base image by digest; `FROM scratch` and `FROM <earlier stage>`
     are not images;
  3. every image that a document names is pinned: an `image:` or `FROM` line in a Markdown file outside docs/superpowers/ follows rules
     1 and 2, and so does any `name:tag` of an image the repository itself uses (`postgres`, `caddy`, `axllent/mailpit`, ...) in the
     prose of those documents;
  4. `deploy/docker-compose.yml` (development) passes the settings of the rate limiter and of the audit retention from `.env`
     (`Auth__RateLimit__Enabled`, the five `Auth__RateLimit__<Policy>__PermitPerMinute` and `Auth__Audit__RetentionDays`);
  5. the files that clients/python/pyproject.toml names (`readme`, `license-files`) are copied into the build of the package by
     samples/notes-api/Dockerfile (the package's build fails without them).

Files are the ones git knows (tracked and untracked, `git ls-files -co --exclude-standard`).
"""
import pathlib
import posixpath
import re
import subprocess
import sys

DIGEST = re.compile(r"@sha256:[0-9a-f]{64}\b")
IMAGE_LINE = re.compile(r"^\s*image:\s*[\"']?([^\s\"'#]+)")
FROM_LINE = re.compile(r"^\s*FROM\s+(?:--platform=\S+\s+)?(\S+)(?:\s+AS\s+(\S+))?", re.IGNORECASE)
OWN_IMAGES = ("ghcr.io/mckcieply/auth-core:", "notes-web-caddy:local")
LIMITER_SETTINGS = (
    "Auth__RateLimit__Enabled",
    "Auth__RateLimit__Login__PermitPerMinute",
    "Auth__RateLimit__Refresh__PermitPerMinute",
    "Auth__RateLimit__Email__PermitPerMinute",
    "Auth__RateLimit__Invite__PermitPerMinute",
    "Auth__RateLimit__General__PermitPerMinute",
    "Auth__Audit__RetentionDays",
)
DEV_COMPOSE = "deploy/docker-compose.yml"
PACKAGE_METADATA = "clients/python/pyproject.toml"
PACKAGE_DOCKERFILE = "samples/notes-api/Dockerfile"
PACKAGE_DIRECTORY = "clients/python/"


def is_compose(path):
    name = posixpath.basename(path)
    if not name.endswith((".yml", ".yaml")):
        return False
    if path.startswith("deploy/") or path.startswith("scripts/"):
        return True
    return path.startswith("samples/") and "compose" in name


def is_dockerfile(path):
    return posixpath.basename(path).startswith("Dockerfile")


def is_document(path):
    return path.endswith(".md") and not path.startswith("docs/superpowers/")


def unpinned_image(reference):
    """A problem text when the reference is neither pinned nor one of the exceptions, otherwise None."""
    if DIGEST.search(reference) or reference.startswith(OWN_IMAGES):
        return None
    return f"the image {reference} is not pinned by digest (name:tag@sha256:...)"


def check_compose(name, text):
    problems = []
    for number, line in enumerate(text.splitlines(), 1):
        match = IMAGE_LINE.match(line)
        if match and (problem := unpinned_image(match.group(1))):
            problems.append(f"{name}:{number}: {problem}")
    return problems


def check_dockerfile(name, text):
    problems = []
    stages = set()
    for number, line in enumerate(text.splitlines(), 1):
        match = FROM_LINE.match(line)
        if not match:
            continue
        reference, alias = match.group(1), match.group(2)
        if reference.lower() != "scratch" and reference not in stages and (problem := unpinned_image(reference)):
            problems.append(f"{name}:{number}: {problem}")
        if alias:
            stages.add(alias)
    return problems


def image_names(compose_and_docker_texts):
    """The repository names of the images the repository pins (postgres, caddy, axllent/mailpit, ...), without tag or digest."""
    names = set()
    for text in compose_and_docker_texts:
        for line in text.splitlines():
            match = IMAGE_LINE.match(line) or FROM_LINE.match(line)
            if match and DIGEST.search(match.group(1)):
                names.add(match.group(1).split("@", 1)[0].rsplit(":", 1)[0])
    return names


def check_document(name, text, names):
    """A document: `image:` lines anywhere, `FROM` lines inside a block marked dockerfile, and `name:tag` of a known image in the prose."""
    problems = []
    fence = None   # the language of the fenced block we are in, or None outside one
    for number, line in enumerate(text.splitlines(), 1):
        marker = re.match(r"^\s*```(\S*)", line)
        if marker:
            fence = None if fence is not None else marker.group(1).lower()
            continue
        image = IMAGE_LINE.match(line)
        if image and (problem := unpinned_image(image.group(1))):
            problems.append(f"{name}:{number}: {problem}")
        from_line = FROM_LINE.match(line) if fence == "dockerfile" else None
        if from_line and from_line.group(1).lower() != "scratch" and (problem := unpinned_image(from_line.group(1))):
            problems.append(f"{name}:{number}: {problem}")
        if image or from_line:
            continue    # judged above
        for repository in names:
            for found in re.finditer(r"(?<![\w./-])" + re.escape(repository) + r":([A-Za-z0-9][A-Za-z0-9._-]*)(?!@sha256:)(?![\w.-])", line):
                problems.append(f"{name}:{number}: the image {found.group(0)} is named without its digest")
    return problems


def check_limiter_settings(text):
    return [f"{DEV_COMPOSE}: does not pass {setting}" for setting in LIMITER_SETTINGS if not re.search(r"^\s*" + re.escape(setting) + r"\s*:", text, re.MULTILINE)]


def package_files(pyproject_text):
    """The files that pyproject.toml's readme and license-files name, relative to clients/python/."""
    files = []
    readme = re.search(r'^readme\s*=\s*"([^"]+)"', pyproject_text, re.MULTILINE)
    if readme:
        files.append(readme.group(1))
    licences = re.search(r"^license-files\s*=\s*\[(.*?)\]", pyproject_text, re.MULTILINE | re.DOTALL)
    if licences:
        files.extend(re.findall(r'"([^"]+)"', licences.group(1)))
    return files


def check_package_copy(pyproject_text, dockerfile_text):
    """Every file that the package's metadata names is copied by the Dockerfile: a COPY line that holds the path."""
    copies = [line for line in dockerfile_text.splitlines() if line.lstrip().upper().startswith("COPY ")]
    problems = []
    for file in package_files(pyproject_text):
        if not any(PACKAGE_DIRECTORY + file in line.split() for line in copies):
            problems.append(f"{PACKAGE_DOCKERFILE}: {PACKAGE_METADATA} names {file}, but no COPY line copies {PACKAGE_DIRECTORY}{file}")
    return problems


def check_repository(files, read):
    """files: the paths git knows; read(path) -> text. Returns the list of problems."""
    problems = []
    build_texts = {path: read(path) for path in files if is_compose(path) or is_dockerfile(path)}
    for path, text in sorted(build_texts.items()):
        problems.extend(check_dockerfile(path, text) if is_dockerfile(path) else check_compose(path, text))
    names = image_names(build_texts.values())
    for path in sorted(p for p in files if is_document(p)):
        problems.extend(check_document(path, read(path), names))
    if DEV_COMPOSE in build_texts:
        problems.extend(check_limiter_settings(build_texts[DEV_COMPOSE]))
    else:
        problems.append(f"{DEV_COMPOSE}: the development compose file is missing")
    if PACKAGE_METADATA in files and PACKAGE_DOCKERFILE in build_texts:
        problems.extend(check_package_copy(read(PACKAGE_METADATA), build_texts[PACKAGE_DOCKERFILE]))
    else:
        problems.append(f"{PACKAGE_METADATA} or {PACKAGE_DOCKERFILE} is missing")
    return problems


def self_test():
    digest = "@sha256:" + "a" * 64
    pyproject = 'readme = "README.md"\nlicense = "MIT"\nlicense-files = ["LICENSE"]\n'
    cases = [
        # (rule, number of problems expected, problems found)
        ("compose, pinned", 0, check_compose("c.yml", f"    image: postgres:16-alpine{digest}\n")),
        ("compose, quoted and pinned", 0, check_compose("c.yml", f"    image: \"caddy:2{digest}\"  # x\n")),
        ("compose, unpinned", 1, check_compose("c.yml", "    image: postgres:16-alpine\n")),
        ("compose, truncated digest", 1, check_compose("c.yml", "    image: postgres:16@sha256:abc\n")),
        ("compose, the release image", 0, check_compose("c.yml", "    image: ghcr.io/mckcieply/auth-core:${AUTH_CORE_VERSION:?x}\n")),
        ("compose, the image it builds", 0, check_compose("c.yml", "    image: notes-web-caddy:local\n")),
        ("compose, a service named like an image", 0, check_compose("c.yml", "  caddy:\n    build: .\n")),
        ("Dockerfile, pinned", 0, check_dockerfile("D", f"FROM node:24-alpine{digest} AS build\nFROM caddy:2{digest}\n")),
        ("Dockerfile, unpinned", 1, check_dockerfile("D", "FROM node:24-alpine AS build\n")),
        ("Dockerfile, an earlier stage and scratch", 0, check_dockerfile("D", f"FROM node:24{digest} AS build\nFROM build AS test\nFROM scratch\n")),
        ("Dockerfile, a stage name that is not an earlier stage", 1, check_dockerfile("D", f"FROM node:24{digest} AS build\nFROM runtime\n")),
        ("document, a pinned image line", 0, check_document("d.md", f"    image: caddy:2{digest}\n", {"caddy"})),
        ("document, an unpinned image line", 1, check_document("d.md", "    image: caddy:2\n", {"caddy"})),
        ("document, prose with the digest", 0, check_document("d.md", f"Use `caddy:2.11{digest}` here.\n", {"caddy"})),
        ("document, prose without the digest", 1, check_document("d.md", "Use `caddy:2.11` here.\n", {"caddy"})),
        ("document, prose with a service key", 0, check_document("d.md", "  caddy:\n    ports: []\n", {"caddy"})),
        ("document, the release image in prose", 0, check_document("d.md", "The image `ghcr.io/mckcieply/auth-core:0.1.0` is public.\n", {"caddy"})),
        ("document, FROM in SQL is not an image", 0, check_document("d.md", "```sql\nSELECT 1\nFROM audit_events\n```\n", {"caddy"})),
        ("document, FROM in a dockerfile block, pinned", 0, check_document("d.md", f"```dockerfile\nFROM caddy:2{digest}\n```\n", {"caddy"})),
        ("document, FROM in a dockerfile block, unpinned", 1, check_document("d.md", "```dockerfile\nFROM caddy:2\n```\n", {"caddy"})),
        ("document, another image's name inside a path", 0, check_document("d.md", "see my-caddy:x and docs/caddy:y\n", {"caddy"})),
        ("limiter settings, all there", 0, check_limiter_settings("".join(f"      {s}: ${{X:-}}\n" for s in LIMITER_SETTINGS))),
        ("limiter settings, one missing", 1, check_limiter_settings("".join(f"      {s}: ${{X:-}}\n" for s in LIMITER_SETTINGS[1:]))),
        ("limiter settings, all missing", len(LIMITER_SETTINGS), check_limiter_settings("services: {}\n")),
        ("package copy, both copied", 0, check_package_copy(pyproject, "COPY clients/python/README.md clients/python/LICENSE /tmp/p/\n")),
        ("package copy, copied one by one", 0, check_package_copy(pyproject, "COPY clients/python/README.md /tmp/p/\nCOPY clients/python/LICENSE /tmp/p/\n")),
        ("package copy, the licence missing", 1, check_package_copy(pyproject, "COPY clients/python/README.md /tmp/p/\n")),
        ("package copy, both missing", 2, check_package_copy(pyproject, "COPY clients/python/pyproject.toml /tmp/p/\n")),
        ("package copy, a file only in a comment", 1, check_package_copy(pyproject, "# clients/python/LICENSE\nCOPY clients/python/README.md /tmp/p/\n")),
        ("package copy, nothing named", 0, check_package_copy('name = "x"\n', "COPY a b\n")),
    ]
    failures = [
        f"self-test {rule}: expected {expected} problem(s), got {len(found)}: {found}"
        for rule, expected, found in cases
        if len(found) != expected
    ]
    files = {
        DEV_COMPOSE: "".join(f"      {s}: ${{X:-}}\n" for s in LIMITER_SETTINGS) + f"    image: postgres:16{digest}\n",
        PACKAGE_METADATA: pyproject,
        PACKAGE_DOCKERFILE: f"FROM python:3.12{digest}\nCOPY clients/python/README.md clients/python/LICENSE /tmp/p/\n",
        "docs/a.md": "Run `postgres:16` somewhere.\n",
        "docs/superpowers/plans/old.md": "image: postgres:9\n",
    }
    for odd in ("samples/x/docker-compose.yml", "samples/x/compose.prod.yaml", "scripts/y/stack-compose.yml"):
        bad = {**files, odd: "services:\n  web:\n    image: nginx:latest\n"}
        found = check_repository(set(bad), bad.__getitem__)
        if not any(odd in problem for problem in found):
            failures.append(f"self-test {odd}: expected an unpinned image to be reported, got {found}")
    found = check_repository(set(files), files.__getitem__)
    if len(found) != 1 or "docs/a.md" not in found[0]:
        failures.append(f"self-test whole repository: expected one problem in docs/a.md (and none from docs/superpowers/), got {found}")
    del files["docs/a.md"]
    if check_repository(set(files), files.__getitem__):
        failures.append("self-test whole repository: expected a clean sample to pass")
    return failures


def main(arguments):
    if arguments == ["--self-test"]:
        failures = self_test()
        print("\n".join(failures) or "self-test passed: every rule catches its bad sample and lets its good one through")
        return 1 if failures else 0
    root = pathlib.Path(__file__).resolve().parent.parent
    listing = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-co", "--exclude-standard"], capture_output=True, text=True, check=True
    ).stdout.splitlines()
    files = {name for name in listing if (root / name).is_file()}
    problems = check_repository(files, lambda name: (root / name).read_text(encoding="utf-8", errors="replace"))
    print("\n".join(problems) or "every image is pinned by digest, the development compose passes the limiter settings, the package's build files are copied")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
