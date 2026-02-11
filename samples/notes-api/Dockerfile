# syntax=docker/dockerfile:1
# The "notes" sample. Build from the repository root (the compose overlay does):
#   docker build -f samples/notes-api/Dockerfile .

FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PYTHONPATH=/app/src

WORKDIR /app

# The package, from this clone. A product installs it from a tag instead (docs/integration/python-fastapi.md).
COPY clients/python/pyproject.toml /tmp/auth-core-fastapi/pyproject.toml
COPY clients/python/src /tmp/auth-core-fastapi/src
RUN pip install /tmp/auth-core-fastapi && rm -rf /tmp/auth-core-fastapi

COPY samples/notes-api/requirements.txt .
RUN pip install -r requirements.txt

COPY samples/notes-api/alembic.ini .
COPY samples/notes-api/migrations migrations
COPY samples/notes-api/src src

RUN useradd --system --uid 10001 --no-create-home notes
USER notes

EXPOSE 8000
# The service applies its migrations at startup (src/notes_api/db.py).
CMD ["uvicorn", "notes_api.main:app", "--host", "0.0.0.0", "--port", "8000"]
