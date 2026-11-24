import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { Me } from '../auth/auth.service';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { clickOn, has, pageText, privateMe, providerMe, settle, signedInAs, submitForm, typeInto, withWindow } from '../../testing/helpers';
import { ASSIGN_LOCATION, rememberPendingConnect, takePending, takePendingConnect, takeReauth } from './provider-buttons';
import { SettingsPage } from './settings';

const status = (code: number) => ({ status: code, statusText: String(code) });
const ALL = ['google', 'microsoft', 'github'];
const open5 = () => new Date(Date.now() + 5 * 60 * 1000);
const TICKET = 'TICKET-abcdefghijklmnopqrstuvwxyz-0123456789A';

async function open(me: Me, state: Record<string, unknown> | null = null) {
  const assign = vi.fn();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      provideHttpClient(withInterceptors([authInterceptor])),
      provideHttpClientTesting(),
      { provide: ASSIGN_LOCATION, useValue: assign },
    ],
  });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const router = TestBed.inject(Router);
  if (state !== null) {
    vi.spyOn(router, 'currentNavigation').mockReturnValue({ extras: { state } } as never);
  }
  vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(SettingsPage);
  fixture.detectChanges();
  ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'self-service', providers: ALL });
  ctrl.expectOne('/auth/orgs').flush({ orgs: [] });
  ctrl.expectOne('/auth/me').flush(me);
  await settle(fixture);
  return { fixture, ctrl, auth, assign };
}

type Opened = Awaited<ReturnType<typeof open>>;

/** The click reads the person again: the test answers with the window it wants. */
async function click(opened: Opened, selector: string, me: Me): Promise<void> {
  clickOn(opened.fixture, selector);
  await settle(opened.fixture);
  opened.ctrl.expectOne('/auth/me').flush(me);
  await settle(opened.fixture);
}

async function confirmWithPassword(opened: Opened, password: string): Promise<void> {
  typeInto(opened.fixture, '#confirm-password', password);
  submitForm(opened.fixture, 'form[data-testid="confirm-form"]');
  await settle(opened.fixture);
}

function text(opened: Opened, testId: string): string | undefined {
  return (opened.fixture.nativeElement as HTMLElement).querySelector(`[data-testid="${testId}"]`)?.textContent?.trim();
}

describe('SettingsPage: connect and disconnect (spec 0012)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    sessionStorage.clear();
  });

  it('Connect with an open window: no dialog, the round trip starts with purpose=connect and the connect flag', async () => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-github"]', withWindow(privateMe, open5()));
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(false);
    expect(opened.assign).toHaveBeenCalledWith('/auth/providers/github/start?purpose=connect');
    expect(takeReauth()).toBe('connect');
    expect(sessionStorage.getItem('notes-web.provider')).toBe('github');
  });

  it('Connect with the window closed: the dialog first; after the password the round trip starts', async () => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', privateMe);
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(pageText(opened.fixture)).toContain("Confirm it's you");
    expect(text(opened, 'confirm-submit')).toBe('Confirm');
    expect(text(opened, 'confirm-cancel')).toBe('Cancel');
    expect(opened.assign).not.toHaveBeenCalled();
    expect(takeReauth()).toBeNull();
    await confirmWithPassword(opened, 'Another-Passw0rd');
    const request = opened.ctrl.expectOne('/auth/account/confirm');
    expect(request.request.body).toEqual({ password: 'Another-Passw0rd' });
    request.flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(withWindow(privateMe, open5()));
    await settle(opened.fixture);
    expect(opened.assign).toHaveBeenCalledWith('/auth/providers/google/start?purpose=connect');
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(false);
    expect(takeReauth()).toBe('connect');
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('Another-Passw0rd');
  });

  it('a window whose time has passed is closed, whatever GET /auth/me says it was', async () => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', withWindow(privateMe, new Date(Date.now() - 1000)));
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(opened.assign).not.toHaveBeenCalled();
  });

  it('the dialog without a password: one "Confirm with ..." per linked offered provider, armed for the settings screen', async () => {
    const me: Me = { ...providerMe, linked_providers: ['github', 'google'] };
    const opened = await open(me);
    await click(opened, '[data-testid="connect-microsoft"]', me);
    const links = [...(opened.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLAnchorElement>('[data-testid="confirm-dialog"] a')];
    expect(links.map((a) => a.textContent?.trim())).toEqual(['Confirm with Google', 'Confirm with GitHub']);
    expect(links.map((a) => a.getAttribute('href'))).toEqual([
      '/auth/providers/google/start?purpose=reauth',
      '/auth/providers/github/start?purpose=reauth',
    ]);
    expect(has(opened.fixture, '#confirm-password')).toBe(false);
    links[0].addEventListener('click', (event) => event.preventDefault());
    links[0].click();
    expect(takeReauth()).toBe('confirm');
    expect(takePendingConnect()).toBe('microsoft'); // what the return screen will name: "Connect Microsoft again"
  });

  it('the dialog offers no provider that the app no longer offers', async () => {
    const me: Me = { ...providerMe, linked_providers: ['github', 'microsoft'] };
    const narrowed = await openOffering(me, ['github', 'google']); // Microsoft's login stays, but Microsoft is no longer offered
    await click(narrowed, '[data-testid="connect-google"]', me);
    const links = [...(narrowed.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLAnchorElement>('[data-testid="confirm-dialog"] a')];
    expect(links.map((a) => a.textContent?.trim())).toEqual(['Confirm with GitHub']);
  });

  it('the dialog with no usable method: its sentence and "Send me a link"', async () => {
    // No password and no login of an offered provider (its only login's provider was dropped from the manifest, which the rows read as none).
    const me: Me = { ...providerMe, linked_providers: [] };
    const opened = await open(me);
    await click(opened, '[data-testid="connect-github"]', me);
    expect(pageText(opened.fixture)).toContain('None of your sign-in methods can confirm here. Set a password first.');
    expect(has(opened.fixture, '#confirm-password')).toBe(false);
    clickOn(opened.fixture, '[data-testid="confirm-dialog"] [data-testid="send-link"]');
    await settle(opened.fixture);
    expect(opened.ctrl.expectOne('/auth/password/forgot').request.body).toEqual({ email: me.email });
  });

  it.each([
    [{ error: 'wrong_password' }, 403, 'The password is wrong.'],
    [{ error: 'too_many_attempts', retry_after_seconds: 60 }, 429, 'Too many attempts. Try again in 1 minute.'],
    [{ error: 'boom' }, 500, 'Something went wrong. Try again.'],
  ])('a refused confirmation (%j) keeps the dialog open with its sentence', async (answer, code, sentence) => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', privateMe);
    await confirmWithPassword(opened, 'Wrong-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(answer, status(code));
    await settle(opened.fixture);
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(pageText(opened.fixture)).toContain(sentence);
    expect(opened.assign).not.toHaveBeenCalled();
  });

  it('a blank password is not sent', async () => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', privateMe);
    await confirmWithPassword(opened, '   ');
    expect(text(opened, 'confirm-error')).toBe('Enter your password.');
    expect(opened.assign).not.toHaveBeenCalled();
  });

  it('the dialog\'s buttons are disabled while its confirmation is in flight', async () => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', privateMe);
    await confirmWithPassword(opened, 'Another-Passw0rd');
    const root = opened.fixture.nativeElement as HTMLElement;
    expect(root.querySelector<HTMLButtonElement>('[data-testid="confirm-submit"]')!.disabled).toBe(true);
    expect(root.querySelector<HTMLButtonElement>('[data-testid="confirm-cancel"]')!.disabled).toBe(true);
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error: 'wrong_password' }, status(403));
    await settle(opened.fixture);
    expect(root.querySelector<HTMLButtonElement>('[data-testid="confirm-submit"]')!.disabled).toBe(false);
  });

  it('Cancel closes the dialog, starts nothing and forgets the connect that waited', async () => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', privateMe);
    expect(sessionStorage.getItem('notes-web.pending-connect')).toMatch(/^connect:google:\d+$/);
    clickOn(opened.fixture, '[data-testid="confirm-cancel"]');
    await settle(opened.fixture);
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(false);
    expect(opened.assign).not.toHaveBeenCalled();
    expect(takePendingConnect()).toBeNull();
    expect(takeReauth()).toBeNull();
  });

  it('Disconnect with an open window: the unlink, the new token stored, and its sentence', async () => {
    const me = withWindow({ ...privateMe, linked_providers: ['google'] }, open5());
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    const request = opened.ctrl.expectOne('/auth/account/logins/unlink');
    expect(request.request.body).toEqual({ provider: 'google' });
    request.flush({ status: 'authenticated', access_token: 'renewed' });
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush({ ...me, linked_providers: [] });
    await settle(opened.fixture);
    expect(opened.auth.token()).toBe('renewed');
    expect(pageText(opened.fixture)).toContain('Google disconnected.');
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(false);
  });

  it('Disconnect with the window closed: the dialog first, and no unlink until the confirmation worked', async () => {
    const me: Me = { ...privateMe, linked_providers: ['google'] };
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    opened.ctrl.expectNone('/auth/account/logins/unlink');
  });

  it('Disconnect answered 403 confirmation_required (the window closed meanwhile): the dialog, then the unlink again', async () => {
    const me = withWindow({ ...privateMe, linked_providers: ['google'] }, open5());
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush({ error: 'confirmation_required' }, status(403));
    await settle(opened.fixture);
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    await confirmWithPassword(opened, 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(me);
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush({ status: 'authenticated', access_token: 'renewed' });
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush({ ...me, linked_providers: [] });
    await settle(opened.fixture);
    expect(opened.auth.token()).toBe('renewed');
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(false);
  });

  it.each([
    [409, 'last_login', 'Set a password or connect another provider first.'],
    [404, 'not_found', 'This sign-in method is no longer connected.'],
    [500, 'boom', 'Something went wrong. Try again.'],
  ])('Disconnect answered %i %s: its sentence, and the rows read again', async (code, error, sentence) => {
    const me = withWindow({ ...privateMe, linked_providers: ['google'] }, open5());
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush({ error }, status(code));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(me);
    await settle(opened.fixture);
    expect(text(opened, 'settings-error')).toBe(sentence);
    expect(opened.auth.token()).toBe('tok');
  });

  it('Disconnect answered 401 session_required: no sentence, no read of the person (the interceptor signs the person out)', async () => {
    const me = withWindow({ ...privateMe, linked_providers: ['google'] }, open5());
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush({ error: 'session_required' }, status(401));
    await settle(opened.fixture);
    expect(has(opened.fixture, '[data-testid="settings-error"]')).toBe(false);
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(false);
  });

  it('the buttons are disabled while a disconnect is in flight', async () => {
    const me = withWindow({ ...privateMe, linked_providers: ['google', 'github'] }, open5());
    const opened = await open(me);
    clickOn(opened.fixture, '[data-testid="disconnect-google"]');
    await settle(opened.fixture);
    // The person is being read again: the buttons are already off.
    const buttons = () => [...(opened.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('[data-testid^="disconnect-"], [data-testid^="connect-"]')];
    expect(buttons().length).toBeGreaterThan(0);
    expect(buttons().every((button) => button.disabled)).toBe(true);
    opened.ctrl.expectOne('/auth/me').flush(me);
    await settle(opened.fixture);
    // The unlink itself is in flight.
    expect(buttons().every((button) => button.disabled)).toBe(true);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush({ error: 'last_login' }, status(409));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(me);
    await settle(opened.fixture);
    expect(buttons().every((button) => button.disabled)).toBe(false);
  });

  it('a connect ticket handed back for a closed window: the dialog opens; after the password the ticket is sent again', async () => {
    const opened = await open(privateMe, { connectTicket: TICKET, provider: 'github' });
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(JSON.stringify({ ...sessionStorage })).not.toContain(TICKET);
    await confirmWithPassword(opened, 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(withWindow(privateMe, open5()));
    await settle(opened.fixture);
    const connect = opened.ctrl.expectOne('/auth/providers/connect');
    expect(connect.request.body).toEqual({ ticket: TICKET });
    connect.flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush({ ...privateMe, linked_providers: ['github'] });
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('GitHub connected.');
    expect(pageText(opened.fixture)).not.toContain(TICKET);
    expect(JSON.stringify({ ...sessionStorage })).not.toContain(TICKET);
  });

  it('the ticket sent again and refused: its sentence, the ticket nowhere', async () => {
    const opened = await open(privateMe, { connectTicket: TICKET, provider: 'github' });
    await confirmWithPassword(opened, 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(withWindow(privateMe, open5()));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/providers/connect').flush({ error: 'provider_already_linked' }, status(409));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(privateMe);
    await settle(opened.fixture);
    expect(text(opened, 'settings-error')).toBe('This GitHub account belongs to another account.');
    expect(pageText(opened.fixture)).not.toContain(TICKET);
  });

  it('a connect ticket handed back for a closed window, then "Confirm with ...": the provider is kept for "Connect GitHub again" and the confirmation is armed', async () => {
    const me: Me = { ...providerMe, linked_providers: ['google'] };
    const opened = await open(me, { connectTicket: TICKET, provider: 'github' });
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(sessionStorage.getItem('notes-web.pending-connect')).toMatch(/^connect:github:\d+$/); // the kind, the name and the time, never the ticket
    expect(JSON.stringify({ ...sessionStorage })).not.toContain(TICKET);
    const link = (opened.fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="confirm-dialog"] a')!;
    link.addEventListener('click', (event) => event.preventDefault());
    link.click();
    expect(takeReauth()).toBe('confirm');
    expect(takePendingConnect()).toBe('github'); // what /provider-return reads when the confirmation comes back
  });

  it('a pending connect that was left is not named by the confirmation of a later disconnect', async () => {
    const me: Me = { ...providerMe, linked_providers: ['github', 'google'] };
    rememberPendingConnect('microsoft'); // the dialog of a connect that the person left by navigating away
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me); // the window is closed: the dialog opens for the disconnect
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(takePending()).toEqual({ kind: 'disconnect', provider: 'google' }); // the disconnect replaced the connect that was left
  });

  it('Disconnect with the window closed, then "Confirm with ...": the confirmation is armed and names the disconnect', async () => {
    const me: Me = { ...providerMe, linked_providers: ['github', 'google'] };
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    const link = (opened.fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="confirm-dialog"] a')!;
    link.addEventListener('click', (event) => event.preventDefault());
    link.click();
    expect(takeReauth()).toBe('confirm');
    expect(takePending()).toEqual({ kind: 'disconnect', provider: 'google' });
  });

  it('Disconnect answered 403 confirmation_required: the pending disconnect is kept for the confirmation', async () => {
    const me = withWindow({ ...providerMe, linked_providers: ['github', 'google'] }, open5());
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush({ error: 'confirmation_required' }, status(403));
    await settle(opened.fixture);
    expect(takePending()).toEqual({ kind: 'disconnect', provider: 'google' });
  });

  it('a password confirmation of a handed-back ticket forgets the pending connect', async () => {
    const opened = await open(privateMe, { connectTicket: TICKET, provider: 'github' });
    await confirmWithPassword(opened, 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(withWindow(privateMe, open5()));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/providers/connect').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush({ ...privateMe, linked_providers: ['github'] });
    await settle(opened.fixture);
    expect(takePendingConnect()).toBeNull();
  });

  it('a sentence the return screen handed on is shown', async () => {
    const opened = await open(privateMe, { notice: 'GitHub connected.' });
    expect(text(opened, 'settings-notice')).toBe('GitHub connected.');
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(false);
  });

  it('a state that is not a sentence or a ticket changes nothing', async () => {
    const opened = await open(privateMe, { notice: 5, connectTicket: '', provider: 'gitlab' });
    expect(has(opened.fixture, '[data-testid="settings-notice"]')).toBe(false);
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(false);
  });
});

/** Like `open`, with a manifest that offers only these providers. */
async function openOffering(me: Me, providers: string[]) {
  const assign = vi.fn();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      provideHttpClient(withInterceptors([authInterceptor])),
      provideHttpClientTesting(),
      { provide: ASSIGN_LOCATION, useValue: assign },
    ],
  });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const fixture = TestBed.createComponent(SettingsPage);
  fixture.detectChanges();
  ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'self-service', providers });
  ctrl.expectOne('/auth/orgs').flush({ orgs: [] });
  ctrl.expectOne('/auth/me').flush(me);
  await settle(fixture);
  return { fixture, ctrl, auth, assign };
}

/** The page's own methods, for the guards the buttons' disabled state also holds. */
interface Actions {
  connect(name: string): Promise<void>;
  disconnect(name: string): Promise<void>;
}
const actionsOf = (opened: Opened): Actions => opened.fixture.componentInstance as unknown as Actions;
const rowButtons = (opened: Opened): HTMLButtonElement[] => [
  ...(opened.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('li [data-testid^="disconnect-"], li [data-testid^="connect-"]'),
];

/** A handed-back connect ticket, the password, and the window open: the ticket goes out again and the test answers it. */
async function resendRefused(me: Me, code: number, body: object): Promise<Opened> {
  const opened = await open(me, { connectTicket: TICKET, provider: 'github' });
  await confirmWithPassword(opened, 'Another-Passw0rd');
  opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
  await settle(opened.fixture);
  opened.ctrl.expectOne('/auth/me').flush(withWindow(me, open5()));
  await settle(opened.fixture);
  opened.ctrl.expectOne('/auth/providers/connect').flush(body, status(code));
  await settle(opened.fixture);
  return opened;
}

describe('SettingsPage: the answers of a connect ticket sent again (spec 0012)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    sessionStorage.clear();
  });

  it.each([
    [409, 'provider_in_use', 'You already have a GitHub account connected. Disconnect it first.'],
    [403, 'email_not_verified', 'GitHub has not verified this address.'],
    [403, 'no_email', 'This GitHub account has no email address we can use.'],
    [400, 'invalid_ticket', 'This took too long. Start again.'],
    [403, 'no_membership', 'Your account does not belong to a company.'],
    [409, 'provider_already_linked', 'This GitHub account belongs to another account.'],
    [403, 'confirmation_required', 'The confirmation did not work or took too long. Confirm again.'],
  ])('%i %s: its sentence, and the rows read again', async (code, error, sentence) => {
    const opened = await resendRefused(privateMe, code, { error });
    opened.ctrl.expectOne('/auth/me').flush(privateMe);
    await settle(opened.fixture);
    expect(text(opened, 'settings-error')).toBe(sentence);
    expect(pageText(opened.fixture)).not.toContain(TICKET);
  });

  it('403 address_mismatch: the sentence names the address of the account, read from GET /auth/me', async () => {
    const opened = await resendRefused(privateMe, 403, { error: 'address_mismatch' });
    opened.ctrl.expectOne('/auth/me').flush(privateMe);
    await settle(opened.fixture);
    expect(text(opened, 'settings-error')).toBe(`This GitHub account has another address. Connect the one with ${privateMe.email}.`);
  });

  it.each([
    [429, { error: 'too_many_requests', retry_after_seconds: 5 }],
    [401, { error: 'session_required' }],
  ])('%i: no sentence and no new read of the person (the bar, or the interceptor, speaks)', async (code, body) => {
    const opened = await resendRefused(privateMe, code, body);
    opened.ctrl.expectNone('/auth/me');
    expect(has(opened.fixture, '[data-testid="settings-error"]')).toBe(false);
  });

  it.each([
    [403, { error: 'no_membership' }, 'Your account does not belong to a company.'],
  ])('Disconnect answered %i %j: its sentence, and the rows read again', async (code, body, sentence) => {
    const me = withWindow({ ...privateMe, linked_providers: ['google'] }, open5());
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush(body, status(code));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(me);
    await settle(opened.fixture);
    expect(text(opened, 'settings-error')).toBe(sentence);
  });

  it('Disconnect answered 429 too_many_requests: no sentence and no new read of the person', async () => {
    const me = withWindow({ ...privateMe, linked_providers: ['google'] }, open5());
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush({ error: 'too_many_requests', retry_after_seconds: 5 }, status(429));
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/me');
    expect(has(opened.fixture, '[data-testid="settings-error"]')).toBe(false);
  });
});

describe('ConfirmDialog: the answers of POST /auth/account/confirm (spec 0012)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    sessionStorage.clear();
  });

  async function opened403(answer: object, code: number): Promise<Opened> {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', privateMe);
    await confirmWithPassword(opened, 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(answer, status(code));
    await settle(opened.fixture);
    return opened;
  }

  it.each(['reauth_required', 'password_required'])(
    '403 %s: the person is read once, and the dialog shows what fits the account now (no password: its providers)',
    async (error) => {
      const opened = await opened403({ error }, 403);
      opened.ctrl.expectOne('/auth/me').flush({ ...providerMe, linked_providers: ['google'] });
      await settle(opened.fixture);
      expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
      expect(has(opened.fixture, '#confirm-password')).toBe(false);
      const links = [...(opened.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLAnchorElement>('[data-testid="confirm-dialog"] a')];
      expect(links.map((a) => a.textContent?.trim())).toEqual(['Confirm with Google']);
    },
  );

  it('403 no_membership: its sentence', async () => {
    const opened = await opened403({ error: 'no_membership' }, 403);
    expect(text(opened, 'confirm-error')).toBe('Your account does not belong to a company.');
  });

  it('429 too_many_requests: no sentence, the dialog stays and its buttons are enabled again', async () => {
    const opened = await opened403({ error: 'too_many_requests', retry_after_seconds: 5 }, 429);
    opened.ctrl.expectNone('/auth/me');
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(has(opened.fixture, '[data-testid="confirm-error"]')).toBe(false);
    const root = opened.fixture.nativeElement as HTMLElement;
    expect(root.querySelector<HTMLButtonElement>('[data-testid="confirm-submit"]')!.disabled).toBe(false);
    expect(root.querySelector<HTMLButtonElement>('[data-testid="confirm-cancel"]')!.disabled).toBe(false);
  });

  it('401 session_required: no sentence and no read of the person (the interceptor signs the person out, so the screen goes with the session)', async () => {
    const opened = await opened403({ error: 'session_required' }, 401);
    opened.ctrl.expectNone('/auth/me');
    expect(has(opened.fixture, '[data-testid="confirm-error"]')).toBe(false);
    expect(has(opened.fixture, '[data-testid="settings-error"]')).toBe(false);
    expect(opened.auth.me()).toBeNull();
  });
});

describe('SettingsPage: one action at a time (spec 0012)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    sessionStorage.clear();
  });

  it('a row click while the dialog\'s confirmation is in flight starts nothing', async () => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', privateMe);
    await confirmWithPassword(opened, 'Another-Passw0rd'); // in flight
    expect(rowButtons(opened).length).toBeGreaterThan(0);
    expect(rowButtons(opened).every((button) => button.disabled)).toBe(true);
    await actionsOf(opened).connect('github');
    await actionsOf(opened).disconnect('google');
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/me');
    opened.ctrl.expectNone('/auth/account/logins/unlink');
    expect(opened.assign).not.toHaveBeenCalled();
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error: 'wrong_password' }, status(403));
  });

  it('the row buttons are disabled while the dialog is open', async () => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', privateMe);
    expect(rowButtons(opened).every((button) => button.disabled)).toBe(true);
    clickOn(opened.fixture, '[data-testid="confirm-cancel"]');
    await settle(opened.fixture);
    expect(rowButtons(opened).some((button) => button.disabled)).toBe(false);
  });

  it('a row click while the password is being changed starts nothing', async () => {
    const opened = await open(privateMe);
    typeInto(opened.fixture, '#current-password', 'Old-Passw0rd-1');
    typeInto(opened.fixture, '#settings-new-password', 'New-Passw0rd-123');
    typeInto(opened.fixture, '#settings-repeat-password', 'New-Passw0rd-123');
    submitForm(opened.fixture, 'form[data-testid="password-form"]');
    await settle(opened.fixture);
    const change = opened.ctrl.expectOne('/auth/account/password');
    expect(rowButtons(opened).length).toBeGreaterThan(0);
    expect(rowButtons(opened).every((button) => button.disabled)).toBe(true);
    await actionsOf(opened).connect('github');
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/me');
    expect(opened.assign).not.toHaveBeenCalled();
    change.flush({ error: 'wrong_password' }, status(403));
    await settle(opened.fixture);
    expect(rowButtons(opened).some((button) => button.disabled)).toBe(false);
  });

  it('a second connect while the person is being read again starts nothing more', async () => {
    const opened = await open(privateMe);
    clickOn(opened.fixture, '[data-testid="connect-github"]');
    await settle(opened.fixture);
    await actionsOf(opened).connect('github'); // the first one still awaits GET /auth/me
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(withWindow(privateMe, open5()));
    await settle(opened.fixture);
    expect(opened.assign).toHaveBeenCalledTimes(1);
  });
});
