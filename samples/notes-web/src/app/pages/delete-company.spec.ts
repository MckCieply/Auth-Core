import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { AuthService, Me } from '../auth/auth.service';
import { adminMe, clickOn, has, pageText, providerMe, settle, signedInAs, submitForm, typeInto } from '../../testing/helpers';
import { takeReauth } from './provider-buttons';
import { TeamPage } from './team';

const status = (code: number) => ({ status: code, statusText: String(code) });
const deleter: Me = { ...adminMe, permissions: [...adminMe.permissions, 'org:delete'] };
const deleterWithoutPassword: Me = { ...deleter, has_password: false, linked_providers: ['github'] };
const TICKET = 'TICKET-abcdefghijklmnopqrstuvwxyz-0123456789A';

/**
 * Opens /team through a real navigation, with the state that /provider-return hands on: the page reads the ticket while that navigation is
 * current and passes it to the section, which is created later, when the page is ready and the navigation is over. (A mocked
 * `currentNavigation` for the whole run would hide a section that reads it too late.)
 */
async function open(me: Me, state: Record<string, unknown> | null = null) {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'team', component: TeamPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const router = TestBed.inject(Router);
  const harness = await RouterTestingHarness.create();
  await router.navigateByUrl('/team', state === null ? undefined : { state });
  const fixture = harness.fixture;
  await settle(fixture);
  await settle(fixture); // the navigation that takes the ticket out of the history entry lands here
  const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true); // from here on, the way out to /login goes nowhere
  return { fixture, ctrl, auth, navigateByUrl };
}

type Opened = Awaited<ReturnType<typeof open>>;

async function deleteWith(opened: Opened, name: string, password?: string): Promise<void> {
  if (!has(opened.fixture, '#company-name')) {
    clickOn(opened.fixture, '[data-testid="delete-company"]');
    opened.fixture.detectChanges();
  }
  typeInto(opened.fixture, '#company-name', name);
  if (password !== undefined) {
    typeInto(opened.fixture, '#company-password', password);
  }
  submitForm(opened.fixture, 'form[data-testid="delete-company-form"]');
  await settle(opened.fixture);
}

/** The password confirmation and the person read again that follows it, answered; the data deletion is left to the test. */
async function confirmed(opened: Opened): Promise<void> {
  opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
  await settle(opened.fixture);
  opened.ctrl.expectOne('/auth/me').flush(deleter); // settingsCall reads the person again
  await settle(opened.fixture);
}

describe('DeleteCompanySection on /team (spec 0012)', () => {
  afterEach(() => sessionStorage.clear());

  it('is shown to a holder of org:delete and to nobody else', async () => {
    const shown = await open(deleter);
    expect(has(shown.fixture, '[data-testid="delete-company"]')).toBe(true);
    TestBed.resetTestingModule();
    const hidden = await open(adminMe);
    expect(has(hidden.fixture, '[data-testid="delete-company"]')).toBe(false);
  });

  it('the_warning_names_the_members_who_lose_the_account', async () => {
    const opened = await open(deleter);
    clickOn(opened.fixture, '[data-testid="delete-company"]');
    opened.fixture.detectChanges();
    expect(pageText(opened.fixture)).toContain(
      'This deletes the company and all its notes. A member who has no other space loses the account too. It cannot be undone.',
    );
    expect(pageText(opened.fixture)).not.toContain("every member's account");
  });

  it("a name that is not the company's exactly: the sentence, and nothing is sent", async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'acme', 'Another-Passw0rd');
    opened.ctrl.expectNone('/auth/account/confirm');
    opened.ctrl.expectNone('/api/org');
    expect(pageText(opened.fixture)).toContain("Type the company's name exactly.");
  });

  it('with a password: confirm first, then DELETE /api/org, then DELETE /auth/org, then /login saying so', async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Another-Passw0rd');
    opened.ctrl.expectNone('/api/org'); // not before the confirmation
    await confirmed(opened);
    opened.ctrl.expectNone('/auth/org'); // not before the company's notes are gone
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/org');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.body).toEqual({ name: 'Acme', password: 'Another-Passw0rd' });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login', { state: { notice: 'company_deleted' } });
    expect(opened.auth.token()).toBeNull();
  });

  it('a wrong password at the confirmation: nothing is deleted', async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Wrong-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error: 'wrong_password' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectNone('/api/org');
    opened.ctrl.expectNone('/auth/org');
    expect(pageText(opened.fixture)).toContain('The password is wrong.');
  });

  it('a failed DELETE /api/org sends nothing to Auth-Core', async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Another-Passw0rd');
    await confirmed(opened);
    opened.ctrl.expectOne('/api/org').flush('oops', status(500));
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/org');
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
  });

  it.each([
    [{ error: 'invalid_request' }, 400, "Type the company's name exactly. The company's notes are already gone."],
    [
      { error: 'permission_not_held' },
      403,
      "A member has permissions you do not have, so you cannot delete the company. The company's notes are already gone.",
    ],
    [{ error: 'too_many_attempts', retry_after_seconds: 60 }, 429, "Too many attempts. Try again in 1 minute. The company's notes are already gone."],
  ])('DELETE /auth/org answered %j: its sentence', async (answer, code, sentence) => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Another-Passw0rd');
    await confirmed(opened);
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/org').flush(answer, status(code));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(sentence);
    expect(opened.auth.token()).toBe('tok');
  });

  it('without a password: "Confirm with GitHub", armed for the team screen, and no password field', async () => {
    const opened = await open(deleterWithoutPassword);
    clickOn(opened.fixture, '[data-testid="delete-company"]');
    opened.fixture.detectChanges();
    expect(has(opened.fixture, '#company-password')).toBe(false);
    const link = (opened.fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="provider-github"]')!;
    expect(link.getAttribute('href')).toBe('/auth/providers/github/start?purpose=reauth');
    link.addEventListener('click', (event) => event.preventDefault());
    link.click();
    expect(takeReauth()).toBe('delete_company');
  });

  it('back with the ticket: the name, then DELETE /api/org and DELETE /auth/org with the ticket, and no confirmation call', async () => {
    const opened = await open(deleterWithoutPassword, { reauthTicket: TICKET });
    await deleteWith(opened, 'Acme');
    opened.ctrl.expectNone('/auth/account/confirm');
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/org');
    expect(request.request.body).toEqual({ name: 'Acme', provider_ticket: TICKET });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login', { state: { notice: 'company_deleted' } });
    expect(JSON.stringify({ ...sessionStorage })).not.toContain(TICKET);
    expect(JSON.stringify(history.state)).not.toContain(TICKET);
  });

  it('403 reauth_failed: the sentence, and the confirm button again', async () => {
    const opened = await open(deleterWithoutPassword, { reauthTicket: TICKET });
    await deleteWith(opened, 'Acme');
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/org').flush({ error: 'reauth_failed' }, status(403));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(
      'The confirmation did not work or took too long, so the company was not deleted. Its notes are already gone. Confirm again.',
    );
    expect(has(opened.fixture, '[data-testid="provider-github"]')).toBe(true);
  });

  it.each(['password_required', 'reauth_required'])('403 %s redraws from a fresh GET /auth/me', async (error) => {
    const opened = await open(deleterWithoutPassword, { reauthTicket: TICKET });
    await deleteWith(opened, 'Acme');
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/org').flush({ error }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(deleter);
    await settle(opened.fixture);
    expect(has(opened.fixture, '#company-password')).toBe(true);
  });

  it('403 permissions_changed signs the person out to /login', async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Another-Passw0rd');
    await confirmed(opened);
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/org').flush({ error: 'permissions_changed' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/logout').flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login');
    expect(opened.auth.token()).toBeNull();
  });

  it('the button is disabled while the deletion is in flight', async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Another-Passw0rd');
    const confirm = (opened.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="delete-company-confirm"]')!;
    expect(confirm.disabled).toBe(true);
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error: 'wrong_password' }, status(403));
    await settle(opened.fixture);
  });

  it('an account without a password and no ticket cannot send anything: the confirm button is not there', async () => {
    const opened = await open({ ...providerMe, org_kind: 'organization', org_name: 'Acme', permissions: ['org:delete'] });
    clickOn(opened.fixture, '[data-testid="delete-company"]');
    opened.fixture.detectChanges();
    expect(has(opened.fixture, '[data-testid="delete-company-confirm"]')).toBe(false);
  });
});
