import { apiUrl } from './env';

/** Accepts an invitation by the API, without a browser: the account the test then forgets the password of. */
export async function acceptInviteByApi(invitePath: string, password: string): Promise<void> {
  const token = new URL(invitePath, 'http://placeholder.invalid').searchParams.get('token');
  if (token === null) {
    throw new Error('the invitation link holds no token');
  }
  const response = await fetch(`${apiUrl()}/auth/invites/accept`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, password }),
  });
  if (response.status !== 204) {
    throw new Error(`accepting the invitation by the API answered ${response.status}, not 204`);
  }
}
