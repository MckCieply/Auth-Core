"""The package's metadata (spec 0008): MIT, the licence file, a readme, version 0.1.1."""

import tomllib
import zipfile
from pathlib import Path

import pytest

import auth_core_fastapi

PACKAGE = Path(__file__).resolve().parents[1]
REPOSITORY = PACKAGE.parents[1]


def project() -> dict:
    return tomllib.loads((PACKAGE / "pyproject.toml").read_text(encoding="utf-8"))["project"]


def test_the_version_is_0_1_1_and_has_one_source():
    assert auth_core_fastapi.__version__ == "0.1.1"
    assert "version" not in project()
    assert project()["dynamic"] == ["version"]


def test_the_licence_is_mit_and_its_file_is_the_repositorys():
    data = project()
    assert data["license"] == "MIT"
    assert data["license-files"] == ["LICENSE"]
    text = (PACKAGE / "LICENSE").read_text(encoding="utf-8")
    assert text.startswith("MIT License")
    assert text == (REPOSITORY / "LICENSE").read_text(encoding="utf-8")


def test_the_readme_is_in_the_metadata_and_names_the_install_line():
    assert project()["readme"] == "README.md"
    readme = (PACKAGE / "README.md").read_text(encoding="utf-8")
    assert "auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.1#subdirectory=clients/python" in readme
    assert "docs/integration/python-fastapi.md" in readme


def test_the_guide_and_the_sample_name_the_new_tag():
    line = "@python-v0.1.1#subdirectory=clients/python"
    assert line in (REPOSITORY / "docs/integration/python-fastapi.md").read_text(encoding="utf-8")
    for name in ("requirements.txt", "requirements-image.txt"):
        assert "python-v0.1.1" in (REPOSITORY / "samples/notes-api" / name).read_text(encoding="utf-8")


def test_a_wheel_carries_the_licence_the_readme_and_the_version(tmp_path, monkeypatch):
    build = pytest.importorskip("hatchling.build")
    monkeypatch.chdir(PACKAGE)
    name = build.build_wheel(str(tmp_path))
    with zipfile.ZipFile(tmp_path / name) as wheel:
        names = wheel.namelist()
        metadata = wheel.read(next(n for n in names if n.endswith(".dist-info/METADATA"))).decode("utf-8")
        assert any(n.endswith(".dist-info/licenses/LICENSE") for n in names)
    assert "Version: 0.1.1" in metadata
    assert "License-Expression: MIT" in metadata
    assert "Description-Content-Type: text/markdown" in metadata
    assert "auth-core-fastapi" in metadata.split("\n\n", 1)[1]   # the readme is the long description
