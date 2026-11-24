import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService, Me } from '../auth/auth.service';
import { adminMe, answerOrgs, clickOn, has, pageText, privateMe, settle, signedInAs, submitForm, typeInto, valueOf } from '../../testing/helpers';
import { SettingsPage } from './settings';

const status = (code: number) => ({ status: code, statusText: String(code) });
const warning = 'This deletes your account and all your notes. It cannot be undone.';
const gone = 'Your notes are already gone.';
const wrongPassword = 'The password is wrong, so your account was not deleted. Your notes are already gone.';
const companyMemberText = (names: string): string =>
  `You are still a member of ${names}. Delete those companies or ask their admins to remove you, then delete your account.`;
const personalSpace = { id: privateMe.org_id, name: 'private', kind: 'personal', role: { id: 'r2', name: 'private' }, current: true };
const companySpace = (name: string) => ({ id: `id-${name}`, name, kind: 'organization', role: { id: 'r1', name: 'admin' }, current: false });

/** The settings screen, where "Delete account" lives. `orgs` is the answer of GET /auth/orgs that the section reads when it starts. */
async function open(me: Me, orgs: object[] | 'unreadable' = []) {
  TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(SettingsPage);
  fixture.detectChanges();
  ctrl.expectOne('/auth/options').flush({ signup: 'invite-only', private_signup: 'off', providers: [], org_creation: 'off' });
  ctrl.expectOne('/auth/me').flush(me);
  if (orgs === 'unreadable') {
    ctrl.expectOne('/auth/orgs').flush({ error: 'database_unavailable' }, status(500));
  } else {
    ctrl.expectOne('/auth/orgs').flush({ orgs });
  }
  await settle(fixture);
  return { fixture, ctrl, auth, navigateByUrl };
}

type Opened = Awaited<ReturnType<typeof open>>;

/** The check before the notes are deleted (spec 0016): GET /auth/orgs, answered with the personal space only unless a test says otherwise. */
async function passGuard(opened: { ctrl: HttpTestingController; fixture: ComponentFixture<unknown> }): Promise<void> {
  answerOrgs(opened.ctrl);
  await settle(opened.fixture);
}

async function askToDelete(opened: Opened, password: string): Promise<void> {
  clickOn(opened.fixture, '[data-testid="delete-account"]');
  opened.fixture.detectChanges();
  typeInto(opened.fixture, '#delete-password', password);
  submitForm(opened.fixture, 'form[data-testid="delete-form"]');
  await settle(opened.fixture);
  await passGuard(opened);
}

/** The notes service says 204 and Auth-Core says what `answer` is: the state after the notes are gone. */
async function refusedByAuthCore(opened: Opened, password: string, answer: object, code: number): Promise<void> {
  await askToDelete(opened, password);
  opened.ctrl.expectOne('/api/me').flush(null, status(204));
  await settle(opened.fixture);
  opened.ctrl.expectOne('/auth/account').flush(answer, status(code));
  await settle(opened.fixture);
}

describe('DeleteAccountSection on /settings (spec 0010; spec 0012)', () => {
  it('shows the warning, the password and "Delete my account"', async () => {
    const opened = await open(privateMe);
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(pageText(opened.fixture)).toContain(warning);
    expect(has(opened.fixture, '#delete-password')).toBe(true);
    expect(pageText(opened.fixture)).toContain('Delete my account');
  });

  it('moves the focus to the password, which opens far below the button', async () => {
    const opened = await open(privateMe);
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

  it('a failure of the notes service stops there: Auth-Core is not called', async () => {
    const opened = await open(privateMe);
    await askToDelete(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/api/me').flush({ error: 'database_unavailable' }, status(503));
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/account');
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(pageText(opened.fixture)).not.toContain(gone);
    expect(opened.auth.token()).not.toBeNull();
  });

  it('wrong_password: says that the password is wrong and that the notes are already gone', async () => {
    const opened = await open(privateMe);
    await refusedByAuthCore(opened, 'Wrong-Passw0rd', { error: 'wrong_password' }, 403);
    expect(pageText(opened.fixture)).toContain(wrongPassword);
    expect(opened.navigateByUrl).not.toHaveBeenCalled();
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
    await passGuard(opened);
    opened.ctrl.expectOne('/api/me').flush(null, status(204)); // idempotent: there is nothing left to delete
    await settle(opened.fixture);
    const account = opened.ctrl.expectOne('/auth/account');
    expect(account.request.body).toEqual({ password: 'Chosen-Passw0rd' });
    account.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login', { state: { notice: 'deleted' } });
  });

  it('"Cancel" closes the form and keeps the account', async () => {
    const opened = await open(privateMe);
    await refusedByAuthCore(opened, 'Wrong-Passw0rd', { error: 'wrong_password' }, 403);
    clickOn(opened.fixture, '[data-testid="delete-cancel"]');
    opened.fixture.detectChanges();
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(false);
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
    expect(has(opened.fixture, '[data-testid="delete-account"]')).toBe(false); // the form replaces the button: nothing to press twice
    submitForm(opened.fixture, 'form[data-testid="delete-form"]');
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

  it('an account that moved into a company meanwhile: the form is reset, one sentence says so, and the section says the account is a company member', async () => {
    const opened = await open(privateMe);
    await askToDelete(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'permissions_changed' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(adminMe);
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Your account now belongs to a company, so it cannot be deleted here.');
    expect(has(opened.fixture, '[data-testid="account-moved"]')).toBe(true);
    expect(pageText(opened.fixture)).toContain(companyMemberText('Acme'));
    expect(has(opened.fixture, '[data-testid="delete-form"]')).toBe(false);
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(false);
    expect(has(opened.fixture, '[data-testid="delete-account"]')).toBe(false);
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
    const opened = await open(privateMe);
    await askToDelete(opened, password);
    opened.ctrl.expectNone('/api/me');
    expect(pageText(opened.fixture)).toContain('Enter your password.');
  });

  it.each([
    ['nothing', ''],
    ['white space', '   '],
    ['a tab and a line break', String.fromCharCode(9, 10, 13)],
    ['an ideographic space', String.fromCharCode(0x3000, 0x2003, 0x00a0)],
    ['a NUL character', `pass${String.fromCharCode(0)}word`],
  ])('%s as the password sends nothing, and nothing is deleted', async (_name, password) => {
    const opened = await open(privateMe);
    await askToDelete(opened, password);
    opened.ctrl.expectNone('/api/me');
    opened.ctrl.expectNone('/auth/account');
    expect(pageText(opened.fixture)).toContain('Enter your password.');
  });
});

describe('DeleteAccountSection: the companies of the account (spec 0016; spec 0012)', () => {
  /** The password typed and sent; the list check that comes first is left to the test. */
  async function submitPassword(opened: Opened): Promise<void> {
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    typeInto(opened.fixture, '#delete-password', 'Chosen-Passw0rd');
    submitForm(opened.fixture, 'form[data-testid="delete-form"]');
    await settle(opened.fixture);
  }

  it('delete_account_with_a_company_listed_shows_the_company_member_text_and_deletes_no_notes', async () => {
    const opened = await open(privateMe);
    await submitPassword(opened);
    const listed = opened.ctrl.expectOne('/auth/orgs');
    expect(listed.request.method).toBe('GET');
    listed.flush({ orgs: [personalSpace, companySpace('Acme'), companySpace('Beta')] });
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(companyMemberText('Acme, Beta'));
    opened.ctrl.expectNone('/api/me');
    opened.ctrl.expectNone('/auth/account');
    expect(pageText(opened.fixture)).not.toContain(gone);
    expect(opened.auth.token()).not.toBeNull();
  });

  it('delete_account_with_only_the_personal_space_still_deletes_the_notes_first', async () => {
    const opened = await open(privateMe);
    await submitPassword(opened);
    opened.ctrl.expectOne('/auth/orgs').flush({ orgs: [personalSpace] });
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/account');
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/account').flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login', { state: { notice: 'deleted' } });
  });

  it('a list that cannot be read deletes nothing and says something went wrong', async () => {
    const opened = await open(privateMe);
    await submitPassword(opened);
    opened.ctrl.expectOne('/auth/orgs').flush('oops', status(500));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(pageText(opened.fixture)).not.toContain(gone);
    opened.ctrl.expectNone('/api/me');
    opened.ctrl.expectNone('/auth/account');
  });

  it('delete_account_on_403_company_member_shows_the_text_with_the_names', async () => {
    const opened = await open(privateMe);
    await submitPassword(opened);
    opened.ctrl.expectOne('/auth/orgs').flush({ orgs: [personalSpace] }); // a company was created in another tab after this check
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'company_member' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/orgs').flush({ orgs: [personalSpace, companySpace('Gamma')] });
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(companyMemberText('Gamma'));
    expect(pageText(opened.fixture)).toContain(gone);
    expect(opened.auth.token()).not.toBeNull();
    expect(opened.navigateByUrl).not.toHaveBeenCalled();
  });
});


describe('DeleteAccountSection: who sees the button (spec 0012, F11)', () => {
  const oldSentence = "A company account is deleted by the company's admins.";

  it('a_company_member_sees_the_company_member_text_and_no_button', async () => {
    // 'Beta' is not the session's company (Acme): the text can only name it if the list was read.
    const { fixture } = await open(adminMe, [personalSpace, companySpace('Beta')]);
    expect(pageText(fixture)).toContain(companyMemberText('Beta, Acme'));
    expect(has(fixture, '[data-testid="delete-company-member"]')).toBe(true);
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(false);
  });

  it('an_account_with_companies_only_sees_the_same_text_and_no_button', async () => {
    const { fixture } = await open(adminMe, [{ ...companySpace('Beta'), current: true }]);
    expect(pageText(fixture)).toContain(companyMemberText('Beta, Acme'));
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(false);
  });

  it('a list that cannot be read: a private account still sees the button', async () => {
    const { fixture } = await open(privateMe, 'unreadable');
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(true);
    expect(has(fixture, '[data-testid="delete-company-member"]')).toBe(false);
  });

  it('a list that cannot be read: a company-session account still sees the company-member text from its session, and no button', async () => {
    const { fixture } = await open(adminMe, 'unreadable');
    expect(pageText(fixture)).toContain(companyMemberText('Acme'));
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(false);
  });

  it('a_private_only_account_sees_the_button', async () => {
    const { fixture } = await open(privateMe, [personalSpace]);
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(true);
    expect(has(fixture, '[data-testid="delete-company-member"]')).toBe(false);
  });

  it('a private account with companies sees the text with their names and no button, even when the session is in the private space', async () => {
    const { fixture } = await open(privateMe, [personalSpace, companySpace('Acme'), companySpace('Beta')]);
    expect(pageText(fixture)).toContain(companyMemberText('Acme, Beta'));
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(false);
  });

  it('the_old_sentence_is_gone', async () => {
    for (const [me, orgs] of [
      [adminMe, [personalSpace, companySpace('Acme')]],
      [privateMe, [personalSpace]],
    ] as const) {
      const { fixture } = await open(me, [...orgs]);
      expect(pageText(fixture)).not.toContain(oldSentence);
      TestBed.resetTestingModule();
    }
  });
});
