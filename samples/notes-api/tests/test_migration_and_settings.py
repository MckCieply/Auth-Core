"""The migration makes what the model says, and the settings come from the environment."""

import pytest
from alembic.autogenerate import compare_metadata
from alembic.migration import MigrationContext
from sqlalchemy import create_engine, inspect

from notes_api.db import Base, upgrade_database
from notes_api.settings import Settings


def test_the_migration_creates_the_table_of_the_spec(tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'notes.db'}")

    upgrade_database(engine)

    columns = {column["name"]: column for column in inspect(engine).get_columns("notes")}
    assert list(columns) == ["id", "org_id", "author_sub", "text", "created_at"]
    assert not columns["org_id"]["nullable"]
    assert not columns["author_sub"]["nullable"]
    assert not columns["text"]["nullable"]
    assert [index["column_names"] for index in inspect(engine).get_indexes("notes")] == [["org_id"]]
    assert inspect(engine).get_pk_constraint("notes")["constrained_columns"] == ["id"]


def test_the_migration_and_the_model_agree(tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'notes.db'}")
    upgrade_database(engine)

    with engine.connect() as connection:
        differences = compare_metadata(MigrationContext.configure(connection), Base.metadata)

    assert differences == []


def test_migrating_twice_changes_nothing(tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'notes.db'}")

    upgrade_database(engine)
    upgrade_database(engine)

    assert inspect(engine).get_table_names() == ["alembic_version", "notes"]


def test_the_settings_are_read_from_the_environment():
    settings = Settings.from_env(
        {"AUTH_ISSUER": "http://localhost:8088/auth", "AUTH_AUDIENCE": "notes-api", "DATABASE_URL": "postgresql+psycopg://u:p@db/notes"}
    )

    assert settings.auth_jwks_url is None
    assert settings.database_url == "postgresql+psycopg://u:p@db/notes"
    assert Settings.from_env(
        {"AUTH_ISSUER": "i", "AUTH_AUDIENCE": "a", "AUTH_JWKS_URL": "http://auth:8080/auth/.well-known/jwks.json", "DATABASE_URL": "d"}
    ).auth_jwks_url == "http://auth:8080/auth/.well-known/jwks.json"


@pytest.mark.parametrize("missing", ["AUTH_ISSUER", "AUTH_AUDIENCE", "DATABASE_URL"])
def test_a_missing_setting_is_named_and_its_value_is_not(missing):
    env = {"AUTH_ISSUER": "issuer-value", "AUTH_AUDIENCE": "audience-value", "DATABASE_URL": "postgresql://user:password-value@db/notes"}
    del env[missing]

    with pytest.raises(RuntimeError) as error:
        Settings.from_env(env)

    assert missing in str(error.value)
    assert "password-value" not in str(error.value)
