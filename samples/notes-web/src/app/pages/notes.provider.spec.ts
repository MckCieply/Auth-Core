import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { AuthService, Me } from '../auth/auth.service';
import { clickOn, has, pageText, privateMe, providerMe, realLocation, settle, signedInAs, submitForm, typeInto } from '../../testing/helpers';
import { NotesPage } from './notes';

const status = (code: number) => ({ status: code, statusText: String(code) });
const TICKET = 'r-ticket-1';
const warning = 'This deletes your account and all your notes. It cannot be undone.';
const reauthFailed =
  'The confirmation did not work or took too long, so your account was not deleted. Your notes are already gone. Confirm again.';
const confirmLink = '/auth/providers/google/start?purpose=reauth';

@Component({ template: '' })
class Elsewhere {}

function configure(): void {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'notes', component: NotesPage },
        { path: 'login', component: Elsewhere },
      ]),
      provideHttpClient(),
      provideHttpClientTesting(),
      realLocation,
    ],
  });
}

/** The notes screen, opened as /provider-return opens it: with `reauthTicket` in the navigation state, or without. */
async function open(me: Me, reauthTicket: string | null = null) {
  history.replaceState(null, '', '/start');
  configure();
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const harness = await RouterTestingHarness.create();
  const router = TestBed.inject(Router);
  await router.navigateByUrl('/notes', reauthTicket === null ? {} : { state: { reauthTicket } });
  harness.detectChanges();
  ctrl.expectOne('/api/notes').flush([]);
  await settle(harness.fixture);
  await settle(harness.fixture); // the navigation that takes the ticket out of the history entry lands here
  const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
  return { fixture: harness.fixture, ctrl, router, auth, navigateByUrl };
}

type Opened = Awaited<ReturnType<typeof open>>;

function links(opened: Opened): (string | null)[] {
  return [...(opened.fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="delete-section"] a')].map((a) => a.getAttribute('href'));
}

/** "Delete my account" with the ticket: the notes service says 204, the account call is left to the test. */
async function confirmDeletion(opened: Opened): Promise<void> {
  clickOn(opened.fixture, '[data-testid="delete-confirm"]');
  await settle(opened.fixture);
  opened.ctrl.expectOne('/api/me').flush(null, status(204));
  await settle(opened.fixture);
}

function stored(): string {
  return JSON.stringify({ ...sessionStorage }) + JSON.stringify({ ...localStorage });
}

/** The ticket is in no address, no history entry, no storage and nowhere on the page. */
function expectNoTicketAnywhere(opened: Opened): void {
  expect(window.location.href).not.toContain(TICKET);
  expect(opened.router.url).not.toContain(TICKET);
  expect(JSON.stringify(history.state)).not.toContain(TICKET);
  expect(stored()).not.toContain(TICKET);
  expect(pageText(opened.fixture)).not.toContain(TICKET);
}

describe('NotesPage: "Delete account" without a password (spec 0011)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    history.replaceState(null, '', '/');
    sessionStorage.clear();
    localStorage.clear();
  });

  it('shows the warning and "Confirm with Google" to the reauth start, and no password field', async () => {
    const opened = await open(providerMe);
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(pageText(opened.fixture)).toContain(warning);
    expect(pageText(opened.fixture)).toContain('Confirm with Google');
    expect(links(opened)).toEqual([confirmLink]);
    expect(has(opened.fixture, '#delete-password')).toBe(false);
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(false);
    opened.ctrl.expectNone('/api/me'); // nothing is deleted before the confirmation
    opened.ctrl.expectNone('/auth/account');
    await settle(opened.fixture);
    // The section opens far from the button: the keyboard and a screen reader follow it to the first confirm link.
    expect((document.activeElement as HTMLElement | null)?.getAttribute('href')).toBe(confirmLink);
  });

  it('one button per linked provider, in the order of /auth/me', async () => {
    const opened = await open({ ...providerMe, linked_providers: ['google', 'microsoft'] });
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(links(opened)).toEqual([confirmLink, '/auth/providers/microsoft/start?purpose=reauth']);
  });

  it('an account with a password keeps the form of spec 0010', async () => {
    const opened = await open({ ...privateMe, linked_providers: ['google'] });
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(has(opened.fixture, '#delete-password')).toBe(true);
    expect(pageText(opened.fixture)).not.toContain('Confirm with');
  });

  it('cancel closes the confirm links and sends nothing', async () => {
    const opened = await open(providerMe);
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    clickOn(opened.fixture, '[data-testid="delete-cancel"]');
    opened.fixture.detectChanges();
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(false);
    opened.ctrl.expectNone('/api/me');
    opened.ctrl.expectNone('/auth/account');
  });

  it('back with the ticket: the section is open with the warning and "Delete my account", and the ticket is in no URL', async () => {
    const opened = await open(providerMe, TICKET);
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(true);
    expect(pageText(opened.fixture)).toContain(warning);
    expect(pageText(opened.fixture)).toContain('Delete my account');
    expect(links(opened)).toEqual([]);
    expect(has(opened.fixture, '#delete-password')).toBe(false);
    expect(opened.router.url).toBe('/notes');
    expectNoTicketAnywhere(opened);
    await settle(opened.fixture);
    expect((document.activeElement as HTMLElement | null)?.getAttribute('data-testid')).toBe('delete-confirm');
  });

  it('nothing is deleted until "Delete my account" is pressed', async () => {
    const opened = await open(providerMe, TICKET);
    await settle(opened.fixture);
    await settle(opened.fixture);
    opened.ctrl.expectNone('/api/me');
    opened.ctrl.expectNone('/auth/account');
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(true);
  });

  it('after the screen has read the ticket the history entry no longer holds it (state replaced, address kept)', async () => {
    const opened = await open(providerMe, TICKET);
    expect(history.state).not.toHaveProperty('reauthTicket');
    expect(JSON.stringify(history.state)).not.toContain(TICKET);
    expect(window.location.pathname).toBe('/notes');
    // The ticket still works from memory.
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(true);
    await confirmDeletion(opened);
    const request = opened.ctrl.expectOne('/auth/account');
    expect(request.request.body).toEqual({ provider_ticket: TICKET });
    request.flush(null, status(204));
    await settle(opened.fixture);
  });

  it('a reload of the entry does not open the section again', async () => {
    await open(providerMe, TICKET);
    const entry = history.state;
    const address = window.location.pathname;
    TestBed.resetTestingModule();
    history.replaceState(entry, '', address);
    configure();
    const ctrl = TestBed.inject(HttpTestingController);
    await signedInAs(TestBed.inject(AuthService), ctrl, providerMe);
    const harness = await RouterTestingHarness.create();
    TestBed.inject(Router).initialNavigation();
    await settle(harness.fixture);
    ctrl.expectOne('/api/notes').flush([]);
    await settle(harness.fixture);
    expect(has(harness.fixture, '[data-testid="delete-account"]')).toBe(true);
    expect(has(harness.fixture, '[data-testid="delete-section"]')).toBe(false);
    ctrl.expectNone('/api/me');
    ctrl.expectNone('/auth/account');
  });

  it('deletes the notes first, then the account with the ticket, and opens /login with the notice', async () => {
    const opened = await open(providerMe, TICKET);
    clickOn(opened.fixture, '[data-testid="delete-confirm"]');
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/account'); // not before the notes service said 204
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/account');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.body).toEqual({ provider_ticket: TICKET });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login', { state: { notice: 'deleted' } });
    expect(opened.auth.token()).toBeNull();
    expectNoTicketAnywhere(opened);
  });

  it('while the notes are being deleted the button is off, so a second click sends nothing more', async () => {
    const opened = await open(providerMe, TICKET);
    clickOn(opened.fixture, '[data-testid="delete-confirm"]');
    await settle(opened.fixture);
    clickOn(opened.fixture, '[data-testid="delete-confirm"]');
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/account').flush(null, status(204));
    await settle(opened.fixture);
  });

  it('reauth_failed: the sentence, and the confirm buttons again', async () => {
    const opened = await open(providerMe, TICKET);
    await confirmDeletion(opened);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'reauth_failed' }, status(403));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(reauthFailed);
    expect(links(opened)).toEqual([confirmLink]);
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(false);
    expect(opened.auth.token()).toBe('tok'); // the person is still signed in
    expectNoTicketAnywhere(opened);
  });

  it('reauth_failed drops the ticket by itself (no Cancel): the section offers only the confirm links, and nothing can send the old ticket', async () => {
    const opened = await open(providerMe, TICKET);
    await confirmDeletion(opened);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'reauth_failed' }, status(403));
    await settle(opened.fixture);
    await settle(opened.fixture);
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(true); // still open: nothing closed it
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(false); // the button that carried the ticket is gone
    expect(links(opened)).toEqual([confirmLink]);
    opened.ctrl.expectNone('/api/me');
    opened.ctrl.expectNone('/auth/account');
  });

  it('password_required: /auth/me is asked again and the password form is drawn', async () => {
    const opened = await open(providerMe, TICKET);
    await confirmDeletion(opened);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'password_required' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush({ ...providerMe, has_password: true });
    await settle(opened.fixture);
    expect(has(opened.fixture, '#delete-password')).toBe(true);
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(true);
    expect(links(opened)).toEqual([]);
    expect(pageText(opened.fixture)).toContain('Your notes are already gone.');
    expectNoTicketAnywhere(opened);
  });

  it('password_required with a password then deletes with the password, not with the ticket', async () => {
    const opened = await open(providerMe, TICKET);
    await confirmDeletion(opened);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'password_required' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush({ ...providerMe, has_password: true });
    await settle(opened.fixture);
    typeInto(opened.fixture, '#delete-password', 'Pw-1 x');
    submitForm(opened.fixture, 'form[data-testid="delete-form"]');
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/account');
    expect(request.request.body).toEqual({ password: 'Pw-1 x' });
    request.flush(null, status(204));
    await settle(opened.fixture);
  });

  it('reauth_required after a password (a stale /auth/me): /auth/me is asked again and the confirm buttons are drawn', async () => {
    const opened = await open({ ...providerMe, has_password: true });
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    typeInto(opened.fixture, '#delete-password', 'Pw-1 x');
    submitForm(opened.fixture, 'form[data-testid="delete-form"]');
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'reauth_required' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settle(opened.fixture);
    expect(links(opened)).toEqual([confirmLink]);
    expect(has(opened.fixture, '#delete-password')).toBe(false);
    expect(pageText(opened.fixture)).toContain('Your notes are already gone.');
  });

  it('reauth_required in answer to a ticket: the ticket is dropped, /auth/me is asked again and the section follows the answer', async () => {
    const opened = await open(providerMe, TICKET);
    await confirmDeletion(opened);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'reauth_required' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settle(opened.fixture);
    expect(links(opened)).toEqual([confirmLink]);
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(false);
    expect(pageText(opened.fixture)).toContain('Your notes are already gone.');
  });

  it('a 429 keeps the ticket and the button: the notes are already gone, the bar says when to try again', async () => {
    const opened = await open(providerMe, TICKET);
    await confirmDeletion(opened);
    opened.ctrl
      .expectOne('/auth/account')
      .flush({ error: 'too_many_requests', retry_after_seconds: 30 }, status(429));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Your notes are already gone.');
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(true);
    expect(links(opened)).toEqual([]);
    expect(opened.auth.token()).toBe('tok');
    // another try uses the same ticket
    clickOn(opened.fixture, '[data-testid="delete-confirm"]');
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.ctrl.expectOne('/auth/account').request.body).toEqual({ provider_ticket: TICKET });
  });

  it('any other answer says something went wrong and that the notes are gone, and keeps the ticket', async () => {
    const opened = await open(providerMe, TICKET);
    await confirmDeletion(opened);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'server_error' }, status(500));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again. Your notes are already gone.');
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(true);
    expect(opened.auth.token()).toBe('tok');
  });

  it('a failed DELETE /api/me sends nothing to Auth-Core and keeps the ticket for another try', async () => {
    const opened = await open(providerMe, TICKET);
    clickOn(opened.fixture, '[data-testid="delete-confirm"]');
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/me').flush('oops', status(500));
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/account');
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(true);
    // the same ticket is used for the next try
    clickOn(opened.fixture, '[data-testid="delete-confirm"]');
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.ctrl.expectOne('/auth/account').request.body).toEqual({ provider_ticket: TICKET });
  });

  it('a ticket for an account with a password is not used: the password field is there and the password goes, not the ticket', async () => {
    const opened = await open({ ...privateMe, linked_providers: ['google'] }, TICKET);
    expect(has(opened.fixture, '#delete-password')).toBe(true);
    expect(pageText(opened.fixture)).not.toContain('Confirm with');
    opened.ctrl.expectNone('/api/me');
    typeInto(opened.fixture, '#delete-password', 'Pw-1 x');
    submitForm(opened.fixture, 'form[data-testid="delete-form"]');
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/account');
    expect(request.request.body).toEqual({ password: 'Pw-1 x' });
    request.flush(null, status(204));
    await settle(opened.fixture);
  });

  it('cancel after the ticket arrived drops it: asking again shows the confirm buttons', async () => {
    const opened = await open(providerMe, TICKET);
    clickOn(opened.fixture, '[data-testid="delete-cancel"]');
    opened.fixture.detectChanges();
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(links(opened)).toEqual([confirmLink]);
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(false);
  });

  describe('Cancel is off while a deletion runs', () => {
    const cancelOf = (opened: Opened) =>
      (opened.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="delete-cancel"]');

    it('with the password form: the notes call is open', async () => {
      const opened = await open(privateMe);
      clickOn(opened.fixture, '[data-testid="delete-account"]');
      opened.fixture.detectChanges();
      typeInto(opened.fixture, '#delete-password', 'Pw-1 x');
      submitForm(opened.fixture, 'form[data-testid="delete-form"]');
      await settle(opened.fixture);
      expect(cancelOf(opened)?.disabled).toBe(true);
      cancelOf(opened)?.click();
      await settle(opened.fixture);
      expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(true);
      opened.ctrl.expectOne('/api/me').flush(null, status(204));
      await settle(opened.fixture);
      const request = opened.ctrl.expectOne('/auth/account');
      expect(request.request.body).toEqual({ password: 'Pw-1 x' });
      request.flush(null, status(204));
      await settle(opened.fixture);
    });

    it('with the ticket: during DELETE /api/me and DELETE /auth/account, and the same ticket is still sent', async () => {
      const opened = await open(providerMe, TICKET);
      clickOn(opened.fixture, '[data-testid="delete-confirm"]');
      await settle(opened.fixture);
      expect(cancelOf(opened)?.disabled).toBe(true);
      cancelOf(opened)?.click();
      await settle(opened.fixture);
      expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(true);
      opened.ctrl.expectOne('/api/me').flush(null, status(204));
      await settle(opened.fixture);
      expect(cancelOf(opened)?.disabled).toBe(true);
      cancelOf(opened)?.click();
      await settle(opened.fixture);
      const request = opened.ctrl.expectOne('/auth/account');
      expect(request.request.body).toEqual({ provider_ticket: TICKET });
      request.flush(null, status(204));
      await settle(opened.fixture);
    });

    it('with the confirm links: while /auth/me is asked again after reauth_required', async () => {
      const opened = await open(providerMe, TICKET);
      await confirmDeletion(opened);
      opened.ctrl.expectOne('/auth/account').flush({ error: 'reauth_required' }, status(403));
      await settle(opened.fixture);
      expect(links(opened)).toEqual([confirmLink]); // the links are drawn while /auth/me is still open
      expect(cancelOf(opened)?.disabled).toBe(true);
      cancelOf(opened)?.click();
      await settle(opened.fixture);
      expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(true);
      opened.ctrl.expectOne('/auth/me').flush(providerMe);
      await settle(opened.fixture);
      expect(cancelOf(opened)?.disabled).toBe(false);
    });
  });
});
