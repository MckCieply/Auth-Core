# syntax=docker/dockerfile:1
# The "notes" sample. Build from the repository root (the compose overlay does):
#   docker build -f samples/notes-api/Dockerfile .

# The digest says which bytes the tag is meant to be: the base image cannot change under it.
FROM python:3.12-slim@sha256:dddfd7e07f9d15aeeca61529320492139d21cac7f0070c00609243e51e4e0016

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PYTHONPATH=/app/src

WORKDIR /app

# The libraries first (every one pinned, with the hash of its file: --require-hashes refuses anything else, and
# --only-binary takes wheels only, nothing is built here), then the package, then the code of the sample: a change to the code
# rebuilds the last layers only.
COPY samples/notes-api/requirements-image.txt .
RUN pip install --require-hashes --only-binary=:all: -r requirements-image.txt

# The package, from this clone. A product installs it from a tag instead (docs/integration/python-fastapi.md). Its own
# dependencies are in the list above, so none is fetched here (--no-deps; `pip check` fails the build if one is missing).
# What is still fetched without a hash is its build tool, hatchling (pinned to one version in clients/python/pyproject.toml):
# it is not part of the image afterwards.
COPY clients/python/pyproject.toml /tmp/auth-core-fastapi/pyproject.toml
COPY clients/python/src /tmp/auth-core-fastapi/src
RUN pip install --no-deps /tmp/auth-core-fastapi && pip check && rm -rf /tmp/auth-core-fastapi

COPY samples/notes-api/alembic.ini .
COPY samples/notes-api/migrations migrations
COPY samples/notes-api/src src

RUN useradd --system --uid 10001 --no-create-home notes
USER notes

EXPOSE 8000
# The service applies its migrations at startup (src/notes_api/db.py).
CMD ["uvicorn", "notes_api.main:app", "--host", "0.0.0.0", "--port", "8000"]
