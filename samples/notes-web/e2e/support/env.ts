declare const process: { env: Record<string, string | undefined> };

/** A setting that scripts/e2e-web.sh gives the tests. A missing one means the tests were not started by the script. */
export function setting(name: string): string {
  const value = process.env[name];
  if (value === undefined || value === '') {
    throw new Error(`${name} is not set: run the tests with scripts/e2e-web.sh`);
  }
  return value;
}

/** Auth-Core and the notes service over plain HTTP, for what the tests do without a browser. */
export function apiUrl(): string {
  return process.env['E2E_API_URL'] ?? 'http://localhost:8088';
}

export function mailpitUrl(): string {
  return process.env['E2E_MAILPIT_URL'] ?? 'http://localhost:8025';
}
