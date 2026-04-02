import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, settle, submitForm, typeInto } from '../../testing/helpers';
import { LoginPage } from './login';

const status = (code: number) => ({ status: code, statusText: String(code) });

async function open(answer: object | string, code = 200) {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'login', component: LoginPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/login', LoginPage);
  const ctrl = TestBed.inject(HttpTestingController);
  const request = ctrl.expectOne('/auth/options');
  expect(request.request.method).toBe('GET');
  request.flush(answer, code === 200 ? {} : status(code));
  await settle(harness.fixture);
  return harness.fixture;
}

describe('LoginPage: the link to sign-up', () => {
  it('offers "Create an account" when Auth-Core says self-service', async () => {
    const fixture = await open({ signup: 'self-service' });
    expect(has(fixture, 'a[href="/signup"][data-testid="create-account"]')).toBe(true);
    expect(pageText(fixture)).toContain('Create an account');
  });

  it('offers nothing when Auth-Core says invite-only', async () => {
    const fixture = await open({ signup: 'invite-only' });
    expect(has(fixture, 'a[href="/signup"]')).toBe(false);
    expect(pageText(fixture)).not.toContain('Create an account');
  });

  it.each([[500], [502], [429]])('offers nothing when the options cannot be read (%s)', async (code) => {
    const fixture = await open('oops', code);
    expect(has(fixture, 'a[href="/signup"]')).toBe(false);
  });

  it('keeps the form and the forgot link whatever the options say', async () => {
    const fixture = await open({ signup: 'self-service' });
    expect(has(fixture, '#email')).toBe(true);
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, 'a[href="/forgot"]')).toBe(true);
  });
});

describe('LoginPage: signing in does not wait for GET /auth/options', () => {
  async function openWithoutAnswer() {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'login', component: LoginPage }]), provideHttpClient(), provideHttpClientTesting()],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/login', LoginPage);
    return { fixture: harness.fixture, ctrl: TestBed.inject(HttpTestingController) };
  }

  async function signIn({ fixture, ctrl }: Awaited<ReturnType<typeof openWithoutAnswer>>): Promise<void> {
    typeInto(fixture, '#email', 'admin@example.test');
    typeInto(fixture, '#password', 'pw');
    submitForm(fixture);
    const request = ctrl.expectOne('/auth/login');
    expect(request.request.method).toBe('POST');
    expect(has(fixture, '[data-testid="login-message"]')).toBe(false); // nothing is said before the login answers
    expect(has(fixture, 'a[href="/signup"]')).toBe(false);
    request.flush({ error: 'invalid_credentials' }, status(401));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Wrong email or password.');
  }

  it('signs in while GET /auth/options has not answered', async () => {
    const opened = await openWithoutAnswer();
    expect(opened.ctrl.expectOne('/auth/options').request.method).toBe('GET'); // asked, never answered
    await signIn(opened);
  });

  it('signs in after GET /auth/options failed', async () => {
    const opened = await openWithoutAnswer();
    opened.ctrl.expectOne('/auth/options').error(new ProgressEvent('error'));
    await settle(opened.fixture);
    expect(has(opened.fixture, 'a[href="/signup"]')).toBe(false);
    await signIn(opened);
  });
});
