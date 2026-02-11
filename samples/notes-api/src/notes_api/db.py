import uuid
from datetime import datetime, timezone
from pathlib import Path

from alembic import command
from alembic.config import Config
from sqlalchemy import DateTime, Text, Uuid, create_engine
from sqlalchemy.engine import Engine
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column

# alembic.ini and migrations/ sit next to src/ (in the image: /app)
ROOT = Path(__file__).resolve().parents[2]


class Base(DeclarativeBase):
    pass


class Note(Base):
    """A note belongs to a company: `org_id` is the `org_id` claim of the token that wrote it."""

    __tablename__ = "notes"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    org_id: Mapped[uuid.UUID] = mapped_column(Uuid, nullable=False, index=True)
    author_sub: Mapped[str] = mapped_column(Text, nullable=False)
    text: Mapped[str] = mapped_column(Text, nullable=False)
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=lambda: datetime.now(timezone.utc)
    )


def make_engine(database_url: str) -> Engine:
    # hide_parameters: an error message must not carry the text of a note into the log
    return create_engine(database_url, pool_pre_ping=True, hide_parameters=True)


def upgrade_database(engine: Engine) -> None:
    """Apply the Alembic migrations up to `head`. The service does this at startup."""
    config = Config(str(ROOT / "alembic.ini"))
    config.set_main_option("script_location", str(ROOT / "migrations"))
    config.attributes["configure_logger"] = False  # the service's logging stays as it is
    with engine.connect() as connection:
        config.attributes["connection"] = connection
        command.upgrade(config, "head")
        connection.commit()
