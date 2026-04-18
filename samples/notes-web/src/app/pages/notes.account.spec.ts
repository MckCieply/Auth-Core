import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService, Me } from '../auth/auth.service';
import { adminMe, clickOn, has, pageText, privateMe, settle, signedInAs, submitForm, typeInto, valueOf, viewerMe } from '../../testing/helpers';
import { NotesPage } from './notes';

const status = (code: number) => ({ status: code, statusText: String(code) });
const warning = 'This deletes your account and all your notes. It cannot be undone.';
const gone = 'Your notes are already gone.';
const wrongPassword = 'The password is wrong, so your account was not deleted. Your notes are already gone.';
const aNote = { id: 'n1', text: 'Buy oat milk', author_sub: privateMe.sub, created_at: '2026-04-10T10:00:00Z' };

async function open(me: Me, notes: object[] = []) {
  TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(NotesPage);
  fixture.detectChanges();
  ctrl.expectOne('/api/notes').flush(notes);
  await settle(fixture);
  return { fixture, ctrl, auth, navigateByUrl };
}

type Opened = Awaited<ReturnType<typeof open>>;

async function askToDelete(opened: Opened, password: string): Promise<void> {
  clickOn(opened.fixture, '[data-testid="delete-account"]');
  opened.fixture.detectChanges();
  typeInto(opened.fixture, '#delete-password', password);
  submitForm(opened.fixture, 'form[data-testid="delete-form"]');
  await settle(opened.fixture);
}

/** The notes service says 204 and Auth-Core says what `answer` is: the state after the notes are gone. */
async function refusedByAuthCore(opened: Opened, password: string, answer: object, code: number): Promise<void> {
  await askToDelete(opened, password);
  opened.ctrl.expectOne('/api/me').flush(null, status(204));
  await settle(opened.fixture);
  opened.ctrl.expectOne('/auth/account').flush(answer, status(code));
  await settle(opened.fixture);
}

describe('NotesPage: the account bar (spec 0010)', () => {
  it('a private account sees its address, "Private account" and "Delete account", and no company, role or Team', async () => {
    const { fixture } = await open(privateMe);
    expect(pageText(fixture)).toContain('ola@poczta.example');
    expect(pageText(fixture)).toContain('Private account');
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(true);
    expect(has(fixture, '[data-testid="me-company"]')).toBe(false);
    expect(has(fixture, '[data-testid="me-role"]')).toBe(false);
    expect(has(fixture, '[data-testid="team-link"]')).toBe(false);
  });

  it('a company member who manages members sees "Team", and no "Delete account"', async () => {
    const { fixture } = await open({ ...adminMe, permissions: [...adminMe.permissions, 'members:manage'] });
    expect(has(fixture, 'a[href="/team"][data-testid="team-link"]')).toBe(true);
    expect(has(fixture, '[data-testid="me-company"]')).toBe(true);
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(false);
  });

  it('a company member without members:manage sees no "Team"', async () => {
    const { fixture } = await open(viewerMe);
    expect(has(fixture, '[data-testid="team-link"]')).toBe(false);
  });
});

describe('NotesPage: "Delete account" (spec 0010)', () => {
  it('shows the warning, the password and "Delete my account"', async () => {
    const opened = await open(privateMe);
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(pageText(opened.fixture)).toContain(warning);
    expect(has(opened.fixture, '#delete-password')).toBe(true);
    expect(pageText(opened.fixture)).toContain('Delete my account');
  });

  it('moves the focus to the password, which opens far below the button', async () => {
    const opened = await open(privateMe, [aNote]);
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    await settle(opened.fixture);
    expect((document.activeElement as HTMLElement | null)?.id).toBe('delete-password');
  });

  it('deletes the notes first and only after their 204 the account, then opens /login with the notice', async () => {
    const opened = await open(privateMe);
    await askToDelete(opened, 'Chosen-Passw0rd');
    const data = opened.ctrl.expectOne('/api/me');
    expect(data.request.method).toBe('DELETE');
    opened.ctrl.expectNone('/auth/account'); // not before the product's data is gone
    data.flush(null, status(204));
    await settle(opened.fixture);
    const account = opened.ctrl.expectOne('/auth/account');
    expect(account.request.method).toBe('DELETE');
    expect(account.request.body).toEqual({ password: 'Chosen-Passw0rd' });
    account.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.auth.token()).toBeNull();
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login', { state: { notice: 'deleted' } });
  });

  it('a failure of the notes service stops there: Auth-Core is not called, and the notes are still shown', async () => {
    const opened = await open(privateMe, [aNote]);
    await askToDelete(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/api/me').flush({ error: 'database_unavailable' }, status(503));
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/account');
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(pageText(opened.fixture)).not.toContain(gone);
    expect(pageText(opened.fixture)).toContain('Buy oat milk');
    expect(opened.auth.token()).not.toBeNull();
  });

  it('wrong_password: says that the password is wrong and that the notes are already gone', async () => {
    const opened = await open(privateMe);
    await refusedByAuthCore(opened, 'Wrong-Passw0rd', { error: 'wrong_password' }, 403);
    expect(pageText(opened.fixture)).toContain(wrongPassword);
    expect(opened.navigateByUrl).not.toHaveBeenCalled();
  });

  it('once the notes are gone the page shows no notes and takes no new one', async () => {
    const opened = await open(privateMe, [aNote]);
    expect(pageText(opened.fixture)).toContain('Buy oat milk');
    expect(has(opened.fixture, '#text')).toBe(true);
    await refusedByAuthCore(opened, 'Wrong-Passw0rd', { error: 'wrong_password' }, 403);
    expect(pageText(opened.fixture)).not.toContain('Buy oat milk');
    expect(pageText(opened.fixture)).toContain('No notes yet.');
    expect(has(opened.fixture, '#text')).toBe(false);
  });

  it('429 too_many_attempts: shows the wait and that the notes are gone', async () => {
    const opened = await open(privateMe);
    await refusedByAuthCore(opened, 'Chosen-Passw0rd', { error: 'too_many_attempts', retry_after_seconds: 120 }, 429);
    expect(pageText(opened.fixture)).toContain(`Too many attempts. Try again in 2 minutes. ${gone}`);
  });

  it('429 too_many_requests: says that the notes are gone, the bar says when to try again', async () => {
    const opened = await open(privateMe);
    await refusedByAuthCore(opened, 'Chosen-Passw0rd', { error: 'too_many_requests', retry_after_seconds: 30 }, 429);
    expect(pageText(opened.fixture)).toContain(gone);
  });

  it('any other refusal after the notes are gone says so too', async () => {
    const opened = await open(privateMe);
    await refusedByAuthCore(opened, 'Chosen-Passw0rd', { error: 'database_unavailable' }, 500);
    opened.ctrl.expectOne('/auth/me').flush(privateMe); // asked again: is the account still a private one?
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(`Something went wrong. Try again. ${gone}`);
  });

  it('after a wrong password the person can type the right one and the deletion goes through', async () => {
    const opened = await open(privateMe);
    await refusedByAuthCore(opened, 'Wrong-Passw0rd', { error: 'wrong_password' }, 403);
    typeInto(opened.fixture, '#delete-password', 'Chosen-Passw0rd');
    submitForm(opened.fixture, 'form[data-testid="delete-form"]');
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/me').flush(null, status(204)); // idempotent: there is nothing left to delete
    await settle(opened.fixture);
    const account = opened.ctrl.expectOne('/auth/account');
    expect(account.request.body).toEqual({ password: 'Chosen-Passw0rd' });
    account.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login', { state: { notice: 'deleted' } });
  });

  it('"Cancel" closes the form, keeps the account and takes notes again', async () => {
    const opened = await open(privateMe);
    await refusedByAuthCore(opened, 'Wrong-Passw0rd', { error: 'wrong_password' }, 403);
    clickOn(opened.fixture, '[data-testid="delete-cancel"]');
    opened.fixture.detectChanges();
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(false);
    expect(has(opened.fixture, '#text')).toBe(true);
    expect(opened.auth.token()).not.toBeNull();
  });

  it('"Cancel" before anything was sent sends nothing', async () => {
    const opened = await open(privateMe);
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    clickOn(opened.fixture, '[data-testid="delete-cancel"]');
    opened.fixture.detectChanges();
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(false);
    opened.ctrl.expectNone('/api/me');
  });

  it('"Delete account" does nothing while a deletion is on its way: what was typed stays', async () => {
    const opened = await open(privateMe);
    await askToDelete(opened, 'Chosen-Passw0rd');
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(valueOf(opened.fixture, '#delete-password')).toBe('Chosen-Passw0rd');
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
  });

  it('the form closes when the person turns out to belong to a company', async () => {
    const opened = await open(privateMe);
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(true);
    const loaded = opened.auth.loadMe();
    opened.ctrl.expectOne('/auth/me').flush(adminMe);
    await loaded;
    await settle(opened.fixture);
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(false);
    expect(has(opened.fixture, '[data-testid="delete-account"]')).toBe(false);
  });

  it('an account that moved into a company meanwhile: the form is reset, one sentence says so, and the notes of the company are read', async () => {
    const opened = await open(privateMe, [aNote]);
    await askToDelete(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'permissions_changed' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(adminMe);
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/notes').flush([{ ...aNote, id: 'n2', text: 'Company roadmap' }]);
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Your account now belongs to a company, so it cannot be deleted here.');
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(false);
    expect(has(opened.fixture, '[data-testid="delete-account"]')).toBe(false);
    expect(has(opened.fixture, '#text')).toBe(true);
    expect(pageText(opened.fixture)).toContain('Company roadmap');
    expect(pageText(opened.fixture)).not.toContain(gone);
    expect(opened.auth.token()).not.toBeNull();
  });

  it('an account that is still private after a refusal keeps the form and the message', async () => {
    const opened = await open(privateMe);
    await refusedByAuthCore(opened, 'Chosen-Passw0rd', { error: 'permissions_changed' }, 403);
    opened.ctrl.expectOne('/auth/me').flush(privateMe);
    await settle(opened.fixture);
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(true);
    expect(pageText(opened.fixture)).toContain(`Something went wrong. Try again. ${gone}`);
    expect(has(opened.fixture, '[data-testid="account-moved"]')).toBe(false);
  });

  const char = (...codes: number[]) => String.fromCharCode(...codes);
  // Everything the server accepts must get through: padding and inner spaces, non-ASCII letters, a surrogate pair, and the
  // characters .NET does not call white space (zero width space, byte order mark). 8192 bytes of body is the last size it takes.
  it.each([
    ['inner spaces', 'two words  here'],
    ['padding spaces', '  padded  '],
    ['non-ASCII letters', `Z${char(0xf3, 0x142)}w-1`],
    ['a surrogate pair', `pass${char(0xd83d, 0xde00)}`],
    ['only a zero width space', char(0x200b)],
    ['only a byte order mark', char(0xfeff)],
    ['the longest body that fits', 'a'.repeat(8192 - '{"password":""}'.length)],
  ])('%s as the password goes through: notes first, then the account', async (_name, password) => {
    const opened = await open(privateMe);
    await askToDelete(opened, password);
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.ctrl.expectOne('/auth/account').request.body).toEqual({ password });
  });

  it.each([
    ['a lone high surrogate', `pass${char(0xd83d)}`],
    ['a lone low surrogate', `${char(0xde00)}pass`],
    ['a body one byte over 8 KiB', 'a'.repeat(8192 - '{"password":""}'.length + 1)],
    ['a body over 8 KiB in two-byte letters', char(0xe9).repeat(4100)],
  ])('%s as the password sends nothing, and nothing is deleted', async (_name, password) => {
    const opened = await open(privateMe, [aNote]);
    await askToDelete(opened, password);
    opened.ctrl.expectNone('/api/me');
    expect(pageText(opened.fixture)).toContain('Enter your password.');
    expect(pageText(opened.fixture)).toContain('Buy oat milk');
  });

  it.each([
    ['nothing', ''],
    ['white space', '   '],
    ['a tab and a line break', String.fromCharCode(9, 10, 13)],
    ['an ideographic space', String.fromCharCode(0x3000, 0x2003, 0x00a0)],
    ['a NUL character', `pass${String.fromCharCode(0)}word`],
  ])('%s as the password sends nothing, and nothing is deleted', async (_name, password) => {
    const opened = await open(privateMe, [aNote]);
    await askToDelete(opened, password);
    opened.ctrl.expectNone('/api/me');
    opened.ctrl.expectNone('/auth/account');
    expect(pageText(opened.fixture)).toContain('Enter your password.');
    expect(pageText(opened.fixture)).toContain('Buy oat milk');
  });
});
