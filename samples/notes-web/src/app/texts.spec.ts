import { texts } from './texts';

function leaves(value: unknown, path: string, into: [string, string][]): [string, string][] {
  if (typeof value === 'string') {
    into.push([path, value]);
  } else if (typeof value === 'object' && value !== null) {
    for (const [key, inner] of Object.entries(value)) {
      leaves(inner, `${path}.${key}`, into);
    }
  }
  return into;
}

describe('texts', () => {
  it('rounds the wait of a lockout up to whole minutes', () => {
    expect(texts.login.tooManyAttempts(1)).toBe('Too many attempts. Try again in 1 minute.');
    expect(texts.login.tooManyAttempts(60)).toBe('Too many attempts. Try again in 1 minute.');
    expect(texts.login.tooManyAttempts(61)).toBe('Too many attempts. Try again in 2 minutes.');
    expect(texts.login.tooManyAttempts(90)).toBe('Too many attempts. Try again in 2 minutes.');
    expect(texts.login.tooManyAttempts(600)).toBe('Too many attempts. Try again in 10 minutes.');
  });

  it('gives the wait before another mail in seconds', () => {
    expect(texts.common.waitSeconds(42)).toBe('Wait 42 seconds before asking again.');
    expect(texts.common.waitSeconds(1)).toBe('Wait 1 second before asking again.');
  });

  it('has one sentence for a used-up link and another for a used-up invitation', () => {
    expect(texts.common.invalidLink).toBe('This link has expired or was already used.');
    expect(texts.invite.invalid).toBe('This invitation has expired or was already used. Ask for a new one.');
  });

  it('says to whom an invitation sets a password', () => {
    expect(texts.invite.setsPassword('a@b.example')).toBe('This sets the password for a@b.example.');
  });

  it('is plain English text: printable ASCII, no markup', () => {
    const all = leaves(texts, 'texts', []);
    expect(all.length).toBeGreaterThan(30);
    for (const [path, text] of all) {
      expect(text, path).toMatch(/^[\x20-\x7E]+$/);
      expect(text, path).not.toMatch(/[<>]/);
    }
  });
});
