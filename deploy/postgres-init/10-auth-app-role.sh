#!/bin/sh
# Creates the database role that Auth-Core connects as (spec 0008, Decision 14). The image of PostgreSQL runs this once, when the volume
# is first initialised, as the superuser POSTGRES_USER and against POSTGRES_DB; on a volume that already holds a database it does not run.
#
# The role may log in and nothing else: no SUPERUSER, CREATEROLE, CREATEDB, REPLICATION or BYPASSRLS. It owns the database, so the
# migrations that Auth-Core runs at its start can make the tables, and it is the only role besides the superuser that may connect to it
# (nor may it connect to the maintenance databases).
# The superuser stays for the operator (backup, restore, password changes).
#
# The name comes from AUTH_DB_APP_USER (lower-case letters, digits and underscores, not an SQL keyword, not public, not starting with
# pg_, not the superuser's name) and the password from AUTH_DB_APP_PASSWORD (letters and digits, at least 16, and not the placeholder of
# .env.prod.example: it is part of a connection string); both reach this container from the compose file. A value that does not fit stops
# the initialisation, and the message never holds the password. The password is read from the environment by psql itself, so it is on no
# command line, and statement logging is switched off for this session so that not even a failing statement writes it to the log of the
# container.
#
# The file works whether the image executes it (it is executable) or reads it into its own shell (it is not): it has no `exit`, and
# the status of its last command is the status of the script.
psql -v ON_ERROR_STOP=1 --no-psqlrc --quiet --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
SET log_statement = 'none';
SET log_min_error_statement = 'panic';

\getenv app_user AUTH_DB_APP_USER
\getenv app_pw AUTH_DB_APP_PASSWORD
SELECT current_database() AS db, current_user AS superuser \gset

SELECT (:'app_user' ~ '^[a-z_][a-z0-9_]{0,62}$'
        AND :'app_user' <> :'superuser'
        AND :'app_user' <> 'public'
        AND :'app_user' NOT LIKE 'pg\_%'
        AND quote_ident(:'app_user') = :'app_user'
        AND :'app_pw' ~ '^[A-Za-z0-9]{16,}$'
        AND :'app_pw' !~ '^CHANGEME') AS fits \gset
\if :fits
\else
  DO $$ BEGIN
    RAISE EXCEPTION 'AUTH_DB_APP_USER must be lower-case letters, digits and underscores, starting with a letter or an underscore, not the superuser, not public, not starting with pg_ and not an SQL keyword, and AUTH_DB_APP_PASSWORD must be at least 16 letters and digits and not the placeholder of .env.prod.example';
  END $$;
\endif

SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEROLE NOCREATEDB NOREPLICATION NOBYPASSRLS PASSWORD %L', :'app_user', :'app_pw') \gexec

ALTER DATABASE :"db" OWNER TO :"app_user";
-- Only the superuser and the owner may connect to the database; the role cannot even connect to the two maintenance databases.
REVOKE CONNECT ON DATABASE :"db" FROM PUBLIC;
REVOKE CONNECT ON DATABASE postgres FROM PUBLIC;
REVOKE CONNECT ON DATABASE template1 FROM PUBLIC;
SQL
