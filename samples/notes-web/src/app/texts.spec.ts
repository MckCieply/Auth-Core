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
  it('words the settings sentences of the confirmation, connect and disconnect flows', () => {
    const s = texts.settings;
    expect(s.connectedNow('GitHub')).toBe('GitHub connected.');
    expect(s.disconnectedNow('GitHub')).toBe('GitHub disconnected.');
    expect(s.lastLogin).toBe('Set a password or connect another provider first.');
    expect(s.providerInUse('Google')).toBe('You already have a Google account connected. Disconnect it first.');
    expect(s.providerAlreadyLinked('Google')).toBe('This Google account belongs to another account.');
    expect(s.addressMismatch('Google', 'a@b.example')).toBe('This Google account has another address. Connect the one with a@b.example.');
    expect(s.emailNotVerified('Google')).toBe('Google has not verified this address.');
    expect(s.noEmail('Google')).toBe('This Google account has no email address we can use.');
    expect(s.invalidTicket).toBe('This took too long. Start again.');
    expect(s.reauthFailed).toBe('The confirmation did not work or took too long. Confirm again.');
    expect(s.wrongPassword).toBe('The password is wrong.');
    expect(s.notFound).toBe('This sign-in method is no longer connected.');
    expect(s.connectAgain('Google')).toBe('Confirmed. Connect Google again.');
    expect(s.disconnectAgain('Google')).toBe('Confirmed. Disconnect Google again.');
    expect(s.confirmTitle).toBe("Confirm it's you");
    expect(s.confirm).toBe('Confirm');
    expect(texts.common.cancel).toBe('Cancel');
    expect(texts.common.passwordRequired).toBe('Enter your password.');
    expect(s.noMethod).toBe('None of your sign-in methods can confirm here. Set a password first.');
  });

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
