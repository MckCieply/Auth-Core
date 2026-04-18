import { apiUrl, setting } from './env';
import { waitForLink } from './mail';

async function postJson(path: string, body: unknown, token?: string): Promise<Response> {
  return fetch(`${apiUrl()}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(token === undefined ? {} : { Authorization: `Bearer ${token}` }) },
    body: JSON.stringify(body),
  });
}

/** The development admin's access token, by the API (the address and the password come from scripts/e2e-web.sh). */
async function adminToken(): Promise<string> {
  const response = await postJson('/auth/login', { email: setting('E2E_SEED_EMAIL'), password: setting('E2E_SEED_PASSWORD') });
  if (response.status !== 200) {
    throw new Error(`the development admin could not sign in by the API: ${response.status}`);
  }
  const body = (await response.json()) as { access_token?: unknown };
  if (typeof body.access_token !== 'string') {
    throw new Error('the sign-in of the development admin gave no token');
  }
  return body.access_token;
}

/** The development admin invites the address into the development company with the role named, through the company API (the mail goes out at once). */
export async function inviteByApi(email: string, roleName: string): Promise<void> {
  const token = await adminToken();
  const roles = await fetch(`${apiUrl()}/auth/org/roles`, { headers: { Authorization: `Bearer ${token}` } });
  if (roles.status !== 200) {
    throw new Error(`the list of roles answered ${roles.status}, not 200: ${await roles.text()}`);
  }
  const list = (await roles.json()) as { roles: { id: string; name: string }[] };
  const role = list.roles.find((candidate) => candidate.name === roleName);
  if (role === undefined) {
    throw new Error(`the development company has no role ${roleName}`);
  }
  const response = await postJson('/auth/org/invites', { email, role_id: role.id }, token);
  if (response.status !== 202) {
    throw new Error(`the invitation answered ${response.status}, not 202`);
  }
}

/** A private account (spec 0010) made by the API through the mail: the request, the link of the mail, the completion with the password. */
export async function signUpPrivatelyByApi(email: string, password: string, waitMs = 100_000): Promise<void> {
  const requested = await postJson('/auth/signup', { email, kind: 'private' });
  if (requested.status !== 202) {
    throw new Error(`the private sign-up answered ${requested.status}, not 202`);
  }
  const link = await waitForLink(email, 'complete-signup', waitMs);
  const token = new URL(link, 'http://placeholder.invalid').searchParams.get('token');
  if (token === null) {
    throw new Error('the sign-up link holds no token');
  }
  const completed = await postJson('/auth/signup/complete', { token, password });
  if (completed.status !== 200) {
    throw new Error(`the completion answered ${completed.status}, not 200`);
  }
}

/** Whether the address and the password still sign in, by the API: the status of POST /auth/login. */
export async function loginStatus(email: string, password: string): Promise<number> {
  return (await postJson('/auth/login', { email, password })).status;
}
