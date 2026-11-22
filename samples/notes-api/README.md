# notes-api: the FastAPI sample

A small "notes" product behind Auth-Core: a FastAPI service with PostgreSQL (SQLAlchemy, Alembic) that lists and adds the notes of
the caller's company. It checks Auth-Core's access tokens with the [`clients/python`](../../clients/python/) package
(`auth-core-fastapi`) and calls Auth-Core only for its signing keys (JWKS). The step-by-step guide that uses it is
[`docs/integration/python-fastapi.md`](../../docs/integration/python-fastapi.md); its Angular frontend is
[`samples/notes-web`](../notes-web/).

## What is here

```
src/notes_api/app.py        the endpoints: GET/POST /api/notes, GET /api/notes/{id}, DELETE /api/me, GET /api/health
src/notes_api/settings.py   AUTH_ISSUER, AUTH_AUDIENCE, AUTH_JWKS_URL (optional) and DATABASE_URL, read from the environment
src/notes_api/db.py         the table, and the migrations applied at startup
auth.yaml                   the manifest: the permissions notes:read and notes:write, the default roles, sign-up, providers
compose.yml, Caddyfile      the overlay for the development stack: this service, its database and a Caddy proxy on :8088
Dockerfile                  the image (built from the repository root)
tests/                      pytest, no Docker, no network: SQLite in memory and signing keys made by the tests
```

## Run it

With the development stack (see the top-level README for `.env`; this overlay also needs `NOTES_DB_PASSWORD` in it, see
`.env.example`):

```bash
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f tests/Auth.FakeOidc/compose.yml \
  --env-file .env up -d --build
```

The last file is the fake Google and Microsoft of the end-to-end checks (development only); `auth.yaml` lists both providers, and
without the fake the stack starts the same, but `/auth/health` says `Degraded`. Open `http://localhost:8088`: `/auth` goes to
Auth-Core and `/api` to this service. The Angular frontend is added with
[`samples/notes-web/compose.yml`](../notes-web/compose.yml).

## Run the tests

Python 3.12. From the repository root:

```bash
python -m venv .venv && . .venv/bin/activate        # on Windows in Git Bash: . .venv/Scripts/activate
pip install -e "clients/python[test]" -r samples/notes-api/requirements.txt
cd samples/notes-api && python -m pytest -q
```

## Multi-tenancy, step by step

One account can belong to several companies (spec 0015), and a personal space is one more space. The sample needs no code for that:
it is written for one company per request, and that is enough.

1. **The token decides the company.** Every endpoint of the sample reads the company from the caller's access token and from nowhere
   else: `_org_id(user)` turns the token's `org_id` into a UUID (a valid token whose `org_id` is not one is a `401`). The
   path, the query string and the body never name a company.
2. **Every query is filtered by it.** `list_notes`, `get_note` (the `Note.org_id == org_id` filter comes with the id), `add_note` (the
   note is stored under the token's company, whatever the body says) and `delete_my_data` all use `Note.org_id`. A note of
   another company is a `404`, the same answer as a note that is not there.
3. **The company can change between two requests of one person.** After `POST /auth/switch` the person's next access token names another
   company. The sample keeps no company in a session or in memory: it reads the principal of each request, so the next request is
   simply for the other company. Nothing in this service knows about switching.
4. **A personal space is a space too.** A private person's notes live under the `org_id` of their personal space, like a
   company's. `delete_my_data` (`DELETE /api/me`) deletes the notes of the token's space only when `org_kind == "personal"`; a token of a company
   (`org_kind` `organization`, or absent) gets `403 forbidden`. So a person who has both spaces can delete their private notes
   only while their session works in the personal space, and the company's notes are never deleted by it. Auth-Core itself refuses `DELETE /auth/account` with `403 forbidden` for a person who has a company
   and a personal space (spec 0015): notes-web runs `DELETE /api/me` first, so for such a person the notes go and the account stays. A product
   should not offer "delete account" there.

### The tests that show it

In [`test_notes.py`](tests/test_notes.py):

- `test_two_companies_keep_their_own_notes`: tokens of the same person for company A and company B list and add notes, and each list holds
  its own company's notes only.
- `test_another_companys_note_is_a_404_and_its_list_is_empty`: a note of A, asked for with a token for B, is the same `404` as a note
  that does not exist, and B's list is empty.
- `test_the_company_of_a_new_note_is_the_tokens_whatever_the_body_says`: a body with `org_id`, `id` and `author_sub` is ignored; the note
  is stored under the token's company.
- `test_every_statement_on_the_notes_table_names_the_company`: no statement that adds, lists or reads a note runs without the company (the delete is not part of this test).
- `test_a_valid_token_whose_company_is_not_a_uuid_is_a_401`.

In [`test_delete_my_data.py`](tests/test_delete_my_data.py):

- `test_a_private_account_deletes_its_notes_and_no_other`: only the personal space's notes go.
- `test_a_company_member_is_403_forbidden_and_nothing_is_deleted`: a company token cannot delete.
