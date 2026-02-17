import { texts } from './texts';

// What the texts that carry a number or a name are called with: one, many, a lockout of minutes, an address.
const SAMPLE_ARGUMENTS: readonly (number | string)[] = [1, 2, 61, 'a@b.example'];

function leaves(value: unknown, path: string, into: [string, string][]): [string, string][] {
  if (typeof value === 'string') {
    into.push([path, value]);
  } else if (typeof value === 'function') {
    for (const argument of SAMPLE_ARGUMENTS) {
      const text: unknown = (value as (argument: number | string) => unknown)(argument);
      if (typeof text === 'string') {
        into.push([`${path}(${argument})`, text]);
      }
    }
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

  it('says the limit of a note', () => {
    expect(texts.notes.tooLong(1000)).toBe('A note can be at most 1000 characters.');
  });

  it('is plain English text: printable ASCII, no markup', () => {
    const all = leaves(texts, 'texts', []);
    expect(all.length).toBeGreaterThan(30);
    // The texts that are functions are checked too, not skipped.
    expect(all.map(([path]) => path)).toEqual(
      expect.arrayContaining(['texts.common.waitSeconds(2)', 'texts.login.tooManyAttempts(61)', 'texts.invite.setsPassword(a@b.example)']),
    );
    for (const [path, text] of all) {
      expect(text, path).toMatch(/^[\x20-\x7E]+$/);
      expect(text, path).not.toMatch(/[<>]/);
    }
  });
});
