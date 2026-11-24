import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Me } from '../auth/auth.service';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { adminMe, clickOn, has, pageText, privateMe, providerMe, settle, signedInAs, submitForm, typeInto } from '../../testing/helpers';
import { SettingsPage } from './settings';

const status = (code: number) => ({ status: code, statusText: String(code) });
const ALL = ['google', 'microsoft', 'github'];

async function open(me: Me, providers: string[] = ALL) {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
  });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const fixture = TestBed.createComponent(SettingsPage);
  fixture.detectChanges();
  ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'self-service', providers });
  ctrl.expectOne('/auth/me').flush(me);
  await settle(fixture);
  return { fixture, ctrl, auth };
}

function row(fixture: Awaited<ReturnType<typeof open>>['fixture'], name: string): string {
  const element = (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="row-${name}"]`);
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function disabled(fixture: Awaited<ReturnType<typeof open>>['fixture'], selector: string): boolean {
  return (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(selector)!.disabled;
}

describe('SettingsPage: sign-in methods (spec 0012)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('a password and no login: "Set", and "Connect" for each offered provider in the order of the options', async () => {
    const { fixture } = await open(privateMe, ['github', 'google']);
    expect(row(fixture, 'password')).toBe('Password Set');
    expect(row(fixture, 'github')).toBe('GitHub Connect');
    expect(row(fixture, 'google')).toBe('Google Connect');
    expect(has(fixture, '[data-testid="row-microsoft"]')).toBe(false);
    const rows = [...(fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid^="row-"]')].map((r) => r.getAttribute('data-testid'));
    expect(rows).toEqual(['row-password', 'row-github', 'row-google']);
  });

  it('no password and one login: "Not set", "Connected" with a disabled "Disconnect" and its sentence', async () => {
    const { fixture } = await open(providerMe);
    expect(row(fixture, 'password')).toBe('Password Not set');
    expect(row(fixture, 'google')).toContain('Connected');
    expect(disabled(fixture, '[data-testid="disconnect-google"]')).toBe(true);
    expect(pageText(fixture)).toContain('Set a password or connect another provider before you disconnect this one.');
  });

  it('no password and two logins of offered providers: both can be disconnected', async () => {
    const { fixture } = await open({ ...providerMe, linked_providers: ['github', 'google'] });
    expect(disabled(fixture, '[data-testid="disconnect-google"]')).toBe(false);
    expect(disabled(fixture, '[data-testid="disconnect-github"]')).toBe(false);
    expect(has(fixture, '[data-testid="disconnect-blocked"]')).toBe(false);
  });

  it('a password and a login: "Disconnect" is enabled', async () => {
    const { fixture } = await open({ ...privateMe, linked_providers: ['microsoft'] });
    expect(disabled(fixture, '[data-testid="disconnect-microsoft"]')).toBe(false);
    expect(row(fixture, 'google')).toBe('Google Connect');
  });

  it('a login of a provider no longer offered gets a row with "Disconnect" only, after the offered ones', async () => {
    const { fixture } = await open({ ...providerMe, linked_providers: ['google', 'microsoft'] }, ['google']);
    expect(row(fixture, 'microsoft')).toBe('Microsoft Connected Disconnect');
    expect(has(fixture, '[data-testid="connect-microsoft"]')).toBe(false);
    // Google is the only offered way in: it cannot go; Microsoft can, while Google stays.
    expect(disabled(fixture, '[data-testid="disconnect-google"]')).toBe(true);
    expect(disabled(fixture, '[data-testid="disconnect-microsoft"]')).toBe(false);
  });

  it('a failed options call still lists the logins, each with "Disconnect" (no provider is known to be offered)', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    const ctrl = TestBed.inject(HttpTestingController);
    await signedInAs(TestBed.inject(AuthService), ctrl, { ...privateMe, linked_providers: ['google'] });
    const fixture = TestBed.createComponent(SettingsPage);
    fixture.detectChanges();
    ctrl.expectOne('/auth/options').flush('oops', status(500));
    ctrl.expectOne('/auth/me').flush({ ...privateMe, linked_providers: ['google'] });
    await settle(fixture);
    expect(row(fixture, 'google')).toBe('Google Connected Disconnect');
  });
});

describe('SettingsPage: the password (spec 0012)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function change(opened: Awaited<ReturnType<typeof open>>, current: string, next: string, repeat = next): Promise<void> {
    typeInto(opened.fixture, '#current-password', current);
    typeInto(opened.fixture, '#settings-new-password', next);
    typeInto(opened.fixture, '#settings-repeat-password', repeat);
    submitForm(opened.fixture, 'form[data-testid="password-form"]');
    await settle(opened.fixture);
  }

  it('with a password: "Change password" sends the current and the new one, stores the new token and says so', async () => {
    const opened = await open(privateMe);
    await change(opened, 'Old-Passw0rd', 'New-Passw0rd-1');
    const request = opened.ctrl.expectOne('/auth/account/password');
    expect(request.request.body).toEqual({ current_password: 'Old-Passw0rd', new_password: 'New-Passw0rd-1' });
    request.flush({ status: 'authenticated', access_token: 'renewed' });
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(privateMe);
    await settle(opened.fixture);
    expect(opened.auth.token()).toBe('renewed');
    expect(pageText(opened.fixture)).toContain('Password changed.');
  });

  it('two different new passwords: nothing is sent', async () => {
    const opened = await open(privateMe);
    await change(opened, 'Old-Passw0rd', 'New-Passw0rd-1', 'New-Passw0rd-2');
    opened.ctrl.expectNone('/auth/account/password');
    expect(pageText(opened.fixture)).toContain('The two passwords are not the same.');
  });

  it('a blank current password: nothing is sent', async () => {
    const opened = await open(privateMe);
    await change(opened, '   ', 'New-Passw0rd-1');
    opened.ctrl.expectNone('/auth/account/password');
    expect(pageText(opened.fixture)).toContain('Enter your current password.');
  });

  it.each([
    [{ error: 'wrong_password' }, 403, 'The current password is wrong.'],
    [{ error: 'too_many_attempts', retry_after_seconds: 120 }, 429, 'Too many attempts. Try again in 2 minutes.'],
    [{ error: 'weak_password', rules: ['too_short'] }, 400, 'The password is too short.'],
    [{ error: 'no_membership' }, 403, 'Your account does not belong to a company.'],
  ])('the answer %j is said in one sentence', async (answer, code, sentence) => {
    const opened = await open(privateMe);
    await change(opened, 'Old-Passw0rd', 'New-Passw0rd-1');
    opened.ctrl.expectOne('/auth/account/password').flush(answer, status(code));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(sentence);
    expect(opened.auth.token()).toBe('tok');
  });

  it('403 password_not_set redraws the section from a fresh GET /auth/me', async () => {
    const opened = await open(privateMe);
    await change(opened, 'Old-Passw0rd', 'New-Passw0rd-1');
    opened.ctrl.expectOne('/auth/account/password').flush({ error: 'password_not_set' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settle(opened.fixture);
    expect(has(opened.fixture, 'form[data-testid="password-form"]')).toBe(false);
    expect(has(opened.fixture, '[data-testid="send-link"]')).toBe(true);
  });

  it('the button is disabled while the change is in flight', async () => {
    const opened = await open(privateMe);
    await change(opened, 'Old-Passw0rd', 'New-Passw0rd-1');
    expect(disabled(opened.fixture, '[data-testid="password-submit"]')).toBe(true);
    opened.ctrl.expectOne('/auth/account/password').flush({ error: 'wrong_password' }, status(403));
    await settle(opened.fixture);
    expect(disabled(opened.fixture, '[data-testid="password-submit"]')).toBe(false);
  });

  it('without a password: "Send me a link" asks POST /auth/password/forgot with the account\'s address and says what the link does', async () => {
    const opened = await open(providerMe);
    expect(has(opened.fixture, 'form[data-testid="password-form"]')).toBe(false);
    clickOn(opened.fixture, '[data-testid="send-link"]');
    await settle(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/password/forgot');
    expect(request.request.body).toEqual({ email: providerMe.email });
    expect(disabled(opened.fixture, '[data-testid="send-link"]')).toBe(true);
    request.flush(null, status(202));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Check your inbox: the link sets your password. Setting it signs you out everywhere.');
  });

  it('a company member sees the same sections', async () => {
    const opened = await open(adminMe);
    expect(row(opened.fixture, 'password')).toBe('Password Set');
    expect(has(opened.fixture, 'form[data-testid="password-form"]')).toBe(true);
  });
});
