import { Component } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { adminMe, clickOn, has, pageText, settle, submitForm, typeInto, typeRaw, valueOf } from '../../testing/helpers';
import { LoginPage } from './login';

@Component({ template: '' })
class Blank {}

const status = (code: number) => ({ status: code, statusText: String(code) });

async function open(url = '/login') {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'login', component: LoginPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url, LoginPage);
  const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  return { fixture: harness.fixture, navigateByUrl, ctrl: TestBed.inject(HttpTestingController) };
}

type Opened = Awaited<ReturnType<typeof open>>;

function signIn({ fixture }: Opened, email: string, password: string): void {
  typeInto(fixture, '#email', email);
  typeInto(fixture, '#password', password);
  submitForm(fixture);
}

describe('LoginPage', () => {
  it('shows the form and a link to the forgot screen', async () => {
    const { fixture } = await open();
    expect(pageText(fixture)).toContain('Sign in');
    expect(has(fixture, '#email')).toBe(true);
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, 'a[href="/forgot"]')).toBe(true);
  });

  it('fills in the email that an invitation passed in the navigation state', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'start', component: Blank },
          { path: 'login', component: LoginPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    const harness = await RouterTestingHarness.create('/start');
    await TestBed.inject(Router).navigateByUrl('/login', { state: { email: 'new@acme.example' } });
    harness.detectChanges();
    expect(valueOf(harness.fixture, '#email')).toBe('new@acme.example');
    expect(TestBed.inject(Router).url).toBe('/login');
  });

  describe('a successful sign-in goes to the page named by returnUrl, else /notes', () => {
    it.each([
      ['/login', '/notes'],
      [`/login?returnUrl=${encodeURIComponent('/notes?x=1')}`, '/notes?x=1'],
      [`/login?returnUrl=${encodeURIComponent('//evil.example')}`, '/notes'],
      [`/login?returnUrl=${encodeURIComponent('/\\evil.example')}`, '/notes'],
      [`/login?returnUrl=${encodeURIComponent('/%2F%2Fevil.example')}`, '/notes'],
      [`/login?returnUrl=${encodeURIComponent('https://evil.example')}`, '/notes'],
      [`/login?returnUrl=${encodeURIComponent('javascript:alert(1)')}`, '/notes'],
    ])('%s goes to %s', async (url, target) => {
      const opened = await open(url);
      signIn(opened, 'admin@example.test', 'pw');
      opened.ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 'tok' });
      await settle();
      opened.ctrl.expectOne('/auth/me').flush(adminMe);
      await settle(opened.fixture);
      expect(opened.navigateByUrl).toHaveBeenCalledTimes(1);
      expect(opened.navigateByUrl).toHaveBeenCalledWith(target);
    });
  });

  it('the button stays off until the next page is open, so a second Enter does not sign in twice', async () => {
    const opened = await open();
    let arrive: (opened: boolean) => void = () => undefined;
    opened.navigateByUrl.mockReturnValue(new Promise<boolean>((resolve) => (arrive = resolve)));
    signIn(opened, 'admin@example.test', 'pw');
    opened.ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 'tok' });
    await settle();
    opened.ctrl.expectOne('/auth/me').flush(adminMe);
    await settle(opened.fixture);
    const button = (opened.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button[type="submit"]');
    expect(button?.disabled).toBe(true);
    submitForm(opened.fixture);
    opened.ctrl.expectNone('/auth/login');
    arrive(true);
    await settle(opened.fixture);
    expect(button?.disabled).toBe(false);
  });

  it('lands on /notes: with the real router, a sign-in from /login ends on the notes screen', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'login', component: LoginPage },
          { path: 'notes', component: Blank },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/login', LoginPage);
    const ctrl = TestBed.inject(HttpTestingController);
    typeInto(harness.fixture, '#email', 'admin@example.test');
    typeInto(harness.fixture, '#password', 'pw');
    submitForm(harness.fixture);
    ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 'tok' });
    await settle();
    ctrl.expectOne('/auth/me').flush(adminMe);
    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/notes'));
  });

  it('401 invalid_credentials: "Wrong email or password."', async () => {
    const opened = await open();
    signIn(opened, 'admin@example.test', 'bad');
    opened.ctrl.expectOne('/auth/login').flush({ error: 'invalid_credentials' }, status(401));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Wrong email or password.');
    expect(has(opened.fixture, '[data-testid="resend"]')).toBe(false);
    expect(opened.navigateByUrl).not.toHaveBeenCalled();
  });

  it('403 no_membership: "Your account does not belong to a company."', async () => {
    const opened = await open();
    signIn(opened, 'admin@example.test', 'pw');
    opened.ctrl.expectOne('/auth/login').flush({ error: 'no_membership' }, status(403));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Your account does not belong to a company.');
  });

  it.each([
    [1, 'Try again in 1 minute.'],
    [60, 'Try again in 1 minute.'],
    [61, 'Try again in 2 minutes.'],
    [600, 'Try again in 10 minutes.'],
  ])('429 with retry_after_seconds %s: "Too many attempts. %s"', async (seconds, sentence) => {
    const opened = await open();
    signIn(opened, 'admin@example.test', 'pw');
    opened.ctrl.expectOne('/auth/login').flush({ error: 'too_many_attempts', retry_after_seconds: seconds }, status(429));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(`Too many attempts. ${sentence}`);
  });

  it.each([
    ['400 invalid_request', { error: 'invalid_request' }, 400],
    ['500 with a page of HTML', '<html>oops</html>', 500],
    ['502 from a proxy', '', 502],
  ])('%s: "Something went wrong. Try again."', async (_name, body, code) => {
    const opened = await open();
    signIn(opened, 'admin@example.test', 'pw');
    opened.ctrl.expectOne('/auth/login').flush(body, status(code));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
  });

  it('no network: "Something went wrong. Try again."', async () => {
    const opened = await open();
    signIn(opened, 'admin@example.test', 'pw');
    opened.ctrl.expectOne('/auth/login').error(new ProgressEvent('error'));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
  });

  describe('403 email_not_verified', () => {
    async function unverified() {
      const opened = await open();
      signIn(opened, 'new@example.test', 'pw');
      opened.ctrl.expectOne('/auth/login').flush({ error: 'email_not_verified' }, status(403));
      await settle(opened.fixture);
      return opened;
    }

    it('shows a message and the button "Send the verification link again"', async () => {
      const { fixture } = await unverified();
      expect(pageText(fixture)).toContain('Your email address is not confirmed yet.');
      expect(pageText(fixture)).toContain('Send the verification link again');
      expect(has(fixture, '[data-testid="resend"]')).toBe(true);
    });

    it('the button sends the address without the spaces around it, like the sign-in did', async () => {
      const opened = await open();
      typeRaw(opened.fixture, '#email', '  new@example.test  ');
      typeInto(opened.fixture, '#password', 'pw');
      submitForm(opened.fixture);
      const login = opened.ctrl.expectOne('/auth/login');
      expect(login.request.body).toEqual({ email: 'new@example.test', password: 'pw' });
      login.flush({ error: 'email_not_verified' }, status(403));
      await settle(opened.fixture);
      clickOn(opened.fixture, '[data-testid="resend"]');
      const request = opened.ctrl.expectOne('/auth/email/verify/request');
      expect(request.request.body).toEqual({ email: 'new@example.test' });
      request.flush(null, status(202));
    });

    it('the button asks for a new link for the address in the form, and says so', async () => {
      const { fixture, ctrl } = await unverified();
      clickOn(fixture, '[data-testid="resend"]');
      const request = ctrl.expectOne('/auth/email/verify/request');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ email: 'new@example.test' });
      request.flush(null, status(202));
      await settle(fixture);
      expect(pageText(fixture)).toContain('If this address needs confirming, we sent a new link.');
    });

    it('a double click on the button sends one request', async () => {
      const { fixture, ctrl } = await unverified();
      clickOn(fixture, '[data-testid="resend"]');
      clickOn(fixture, '[data-testid="resend"]');
      ctrl.expectOne('/auth/email/verify/request').flush(null, status(202));
      await settle(fixture);
      ctrl.expectNone('/auth/email/verify/request');
    });

    it('429 on the button: "Wait N seconds before asking again."', async () => {
      const { fixture, ctrl } = await unverified();
      clickOn(fixture, '[data-testid="resend"]');
      ctrl.expectOne('/auth/email/verify/request').flush({ error: 'too_many_attempts', retry_after_seconds: 42 }, status(429));
      await settle(fixture);
      expect(pageText(fixture)).toContain('Wait 42 seconds before asking again.');
    });

    it('any other answer on the button: "Something went wrong. Try again."', async () => {
      const { fixture, ctrl } = await unverified();
      clickOn(fixture, '[data-testid="resend"]');
      ctrl.expectOne('/auth/email/verify/request').flush('oops', status(500));
      await settle(fixture);
      expect(pageText(fixture)).toContain('Something went wrong. Try again.');
    });
  });

  describe('what is sent', () => {
    it('trims the email and sends the password exactly as typed', async () => {
      const opened = await open();
      typeRaw(opened.fixture, '#email', '  Admin@Example.test ');
      typeInto(opened.fixture, '#password', '  pass word 1  ');
      submitForm(opened.fixture);
      const request = opened.ctrl.expectOne('/auth/login');
      expect(request.request.body).toEqual({ email: 'Admin@Example.test', password: '  pass word 1  ' });
      request.flush({ error: 'invalid_credentials' }, status(401));
    });

    it('sends nothing while a field is empty', async () => {
      const opened = await open();
      signIn(opened, '', 'pw');
      signIn(opened, 'admin@example.test', '');
      opened.ctrl.expectNone('/auth/login');
    });

    it('sends nothing for an email of only spaces', async () => {
      const opened = await open();
      typeRaw(opened.fixture, '#email', '    ');
      typeInto(opened.fixture, '#password', 'pw');
      submitForm(opened.fixture);
      opened.ctrl.expectNone('/auth/login');
    });

    it('sends one request for a double click or a double Enter', async () => {
      const opened = await open();
      signIn(opened, 'admin@example.test', 'pw');
      submitForm(opened.fixture);
      submitForm(opened.fixture);
      opened.ctrl.expectOne('/auth/login').flush({ error: 'invalid_credentials' }, status(401));
      await settle(opened.fixture);
      opened.ctrl.expectNone('/auth/login');
    });

    it('allows another try after a failure', async () => {
      const opened = await open();
      signIn(opened, 'admin@example.test', 'bad');
      opened.ctrl.expectOne('/auth/login').flush({ error: 'invalid_credentials' }, status(401));
      await settle(opened.fixture);
      signIn(opened, 'admin@example.test', 'good');
      opened.ctrl.expectOne('/auth/login').flush({ error: 'invalid_credentials' }, status(401));
    });
  });
});
