"""The files that make the image: the pinned lists agree, and the Dockerfile installs from the one with hashes."""

import re
from pathlib import Path

SAMPLE = Path(__file__).resolve().parent.parent


def requirements(name: str) -> dict[str, tuple[str, list[str]]]:
    """name -> (version, hashes) of every requirement in the file; a line that is not an exact pin fails the test."""
    text = (SAMPLE / name).read_text(encoding="utf-8").replace("\\\n", " ")
    found = {}
    for line in text.splitlines():
        line = line.split(" #")[0].strip()
        if not line or line.startswith("#"):
            continue
        match = re.fullmatch(r"([A-Za-z0-9._-]+)(?:\[[a-z,]+\])?==([0-9][A-Za-z0-9.]*)((?:\s+--hash=sha256:[0-9a-f]{64})*)", line)
        assert match, f"{name}: not an exact pin: {line!r}"
        key = re.sub(r"[-_.]+", "-", match.group(1)).lower()
        assert key not in found, f"{name}: {key} twice"
        found[key] = (match.group(2), re.findall(r"sha256:[0-9a-f]{64}", match.group(3)))
    return found


def test_the_list_for_development_is_all_exact_pins_without_hashes_so_that_it_installs_on_any_system():
    pins = requirements("requirements.txt")

    assert len(pins) >= 20  # the sample and everything it needs, not only what it asks for
    assert all(hashes == [] for _, hashes in pins.values())


def test_the_list_for_the_image_has_the_same_pins_and_a_hash_for_every_one():
    development, image = requirements("requirements.txt"), requirements("requirements-image.txt")

    assert {name: version for name, (version, _) in image.items()} == {name: version for name, (version, _) in development.items()}
    assert all(hashes for _, hashes in image.values())


def test_the_list_holds_what_the_sample_asks_for_and_what_that_needs():
    pins = requirements("requirements.txt")

    for name in ("fastapi", "pyjwt", "cryptography", "uvicorn", "sqlalchemy", "alembic", "psycopg", "psycopg-binary"):
        assert name in pins
    for name in ("starlette", "pydantic", "pydantic-core", "h11", "anyio", "cffi"):  # needed by those, not asked for
        assert name in pins


def test_the_dockerfile_installs_the_libraries_from_the_list_with_hashes_and_wheels_only():
    dockerfile = (SAMPLE / "Dockerfile").read_text(encoding="utf-8")

    assert re.search(r"pip install\s+--require-hashes\s+--only-binary=:all:\s+-r requirements-image\.txt", dockerfile)
    assert re.search(r"^COPY samples/notes-api/requirements-image\.txt \.$", dockerfile, re.MULTILINE)


def test_the_build_tool_and_what_it_needs_are_pinned_and_hashed():
    build = requirements("requirements-build.txt")

    for name in ("hatchling", "packaging", "pathspec", "pluggy", "trove-classifiers"):  # hatchling and its dependencies
        assert name in build
    assert all(len(hashes) == 1 for _, hashes in build.values())  # pure Python: one wheel for every system
    assert set(build).isdisjoint(requirements("requirements-image.txt"))  # what is built with is not what runs


def test_the_version_of_the_build_tool_is_the_one_the_package_asks_for():
    pyproject = (SAMPLE.parent.parent / "clients" / "python" / "pyproject.toml").read_text(encoding="utf-8")
    asked = re.search(r'requires = \["hatchling==([0-9.]+)"\]', pyproject)

    assert asked, "the build backend of the package is not pinned to one version"
    assert requirements("requirements-build.txt")["hatchling"][0] == asked.group(1)


def test_the_dockerfile_builds_the_package_from_the_pinned_build_tool_and_fetches_nothing_unpinned():
    dockerfile = (SAMPLE / "Dockerfile").read_text(encoding="utf-8").replace("\\\n", " ")

    assert re.search(r"^COPY samples/notes-api/requirements-build\.txt /tmp/requirements-build\.txt$", dockerfile, re.MULTILINE)
    assert re.search(r"pip install --require-hashes --only-binary=:all: -r /tmp/requirements-build\.txt", dockerfile)
    assert re.search(r"pip install --no-build-isolation --no-deps /tmp/auth-core-fastapi", dockerfile)
    installs = re.findall(r"pip install [^&;\n]*", dockerfile)
    assert len(installs) == 3  # the libraries, the build tool, the package: each one of the above, none other
    for install in installs:
        assert "--require-hashes" in install or "--no-build-isolation" in install, install
    assert re.search(r"pip uninstall --yes hatchling packaging pathspec pluggy tomlkit trove-classifiers", dockerfile)  # not in the image


def test_the_images_of_the_overlay_are_pinned_by_digest():
    compose = (SAMPLE / "compose.yml").read_text(encoding="utf-8")
    dockerfile = (SAMPLE / "Dockerfile").read_text(encoding="utf-8")

    images = re.findall(r"^\s+image: (\S+)$", compose, re.MULTILINE) + re.findall(r"^FROM (\S+)$", dockerfile, re.MULTILINE)
    assert len(images) == 3  # postgres (the init), caddy, python
    for image in images:
        assert re.fullmatch(r"[\w./-]+:[\w.-]+@sha256:[0-9a-f]{64}", image), image
