import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { OPTIONS_TIMEOUT_MS } from '../account-api';
import { clickOn, has, pageText, settle, submitForm, typeInto } from '../../testing/helpers';
import { SignupPage } from './signup';

const status = (code: number) => ({ status: code, statusText: String(code) });
const sent = 'Check your inbox. We sent a message to this address.';
const both = { signup: 'self-service', private_signup: 'self-service' };

/** `options`: the answer of GET /auth/options; 'failed': a server error; 'none': no answer yet. */
async function open(options: object | 'failed' | 'none') {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'signup', component: SignupPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/signup', SignupPage);
  const ctrl = TestBed.inject(HttpTestingController);
  if (options === 'failed') {
    ctrl.expectOne('/auth/options').flush('oops', status(500));
  } else if (options !== 'none') {
    ctrl.expectOne('/auth/options').flush(options);
  }
  await settle(harness.fixture);
  return { fixture: harness.fixture, ctrl };
}

function checked(fixture: Parameters<typeof has>[0], selector: string): boolean {
  return (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(selector)?.checked === true;
}

describe('SignupPage: organization or private person (spec 0010)', () => {
  it('offers the choice only when both are on, with the organization chosen first', async () => {
    const { fixture } = await open(both);
    expect(has(fixture, '[data-testid="signup-kind"]')).toBe(true);
    expect(pageText(fixture)).toContain('Organization');
    expect(pageText(fixture)).toContain('Private person');
    expect(has(fixture, '#company')).toBe(true);
    expect(checked(fixture, '[data-testid="kind-organization"]')).toBe(true);
    expect(checked(fixture, '[data-testid="kind-private"]')).toBe(false);
  });

  it('with only private sign-up on there is no choice and no company name', async () => {
    const { fixture } = await open({ signup: 'invite-only', private_signup: 'self-service' });
    expect(has(fixture, '[data-testid="signup-kind"]')).toBe(false);
    expect(has(fixture, '#company')).toBe(false);
    expect(pageText(fixture)).toContain('Enter your email address. We will send you a link to finish.');
  });

  it('with only organization sign-up on there is no choice', async () => {
    const { fixture } = await open({ signup: 'self-service', private_signup: 'off' });
    expect(has(fixture, '[data-testid="signup-kind"]')).toBe(false);
    expect(has(fixture, '#company')).toBe(true);
  });

  it('a private person types only the email: the request says kind private and names no company, and the answer is the usual one', async () => {
    const { fixture, ctrl } = await open(both);
    clickOn(fixture, '[data-testid="kind-private"]');
    fixture.detectChanges();
    expect(has(fixture, '#company')).toBe(false);
    typeInto(fixture, '#email', 'ola@poczta.example');
    submitForm(fixture);
    const request = ctrl.expectOne('/auth/signup');
    expect(request.request.body).toEqual({ email: 'ola@poczta.example', kind: 'private' });
    request.flush(null, status(202));
    await settle(fixture);
    expect(pageText(fixture)).toContain(sent);

    clickOn(fixture, '[data-testid="kind-organization"]');
    fixture.detectChanges();
    expect(pageText(fixture)).not.toContain(sent); // the sentence belongs to the request that was made, not to the other kind
  });

  it('back to the organization: the company name is asked for and sent again', async () => {
    const { fixture, ctrl } = await open(both);
    clickOn(fixture, '[data-testid="kind-private"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="kind-organization"]');
    fixture.detectChanges();
    typeInto(fixture, '#email', 'anna@firma.example');
    typeInto(fixture, '#company', 'Firma');
    submitForm(fixture);
    expect(ctrl.expectOne('/auth/signup').request.body).toEqual({ email: 'anna@firma.example', company_name: 'Firma' });
  });

  it('400 invalid_request on a private request: says to check the email address', async () => {
    const { fixture, ctrl } = await open({ signup: 'invite-only', private_signup: 'self-service' });
    typeInto(fixture, '#email', 'ola@poczta.example');
    submitForm(fixture);
    ctrl.expectOne('/auth/signup').flush({ error: 'invalid_request' }, status(400));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Check the email address.');
  });

  it('draws no form until GET /auth/options has answered, so a private-only instance never shows the organization form first', async () => {
    const { fixture, ctrl } = await open('none');
    expect(has(fixture, '#email')).toBe(false);
    expect(has(fixture, '#company')).toBe(false);
    ctrl.expectOne('/auth/options').flush({ signup: 'invite-only', private_signup: 'self-service' });
    await settle(fixture);
    expect(has(fixture, '#email')).toBe(true);
    expect(has(fixture, '#company')).toBe(false);
  });

  it.each([
    ['both switches off', { signup: 'invite-only', private_signup: 'off' }],
    ['an options call that failed', 'failed'],
  ] as const)('with %s the organization form stays, and Auth-Core answers the request: 403 says that sign-up is closed', async (_name, options) => {
    const { fixture, ctrl } = await open(options);
    expect(has(fixture, '[data-testid="signup-kind"]')).toBe(false);
    expect(has(fixture, '#company')).toBe(true);
    typeInto(fixture, '#email', 'anna@firma.example');
    typeInto(fixture, '#company', 'Firma');
    submitForm(fixture);
    ctrl.expectOne('/auth/signup').flush({ error: 'signup_disabled' }, status(403));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Sign-up is closed.');
    expect(has(fixture, 'form')).toBe(false);
  });

  it('an options call that hangs gives the organization form after the wait, and Auth-Core answers the request', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] }); // the timeout of rxjs runs on setInterval
    try {
      const { fixture, ctrl } = await open('none');
      expect(has(fixture, '#email')).toBe(false);
      vi.advanceTimersByTime(OPTIONS_TIMEOUT_MS + 1);
      await settle(fixture);
      expect(has(fixture, '[data-testid="signup-kind"]')).toBe(false);
      expect(has(fixture, '#email')).toBe(true);
      expect(has(fixture, '#company')).toBe(true);
      typeInto(fixture, '#email', 'anna@firma.example');
      typeInto(fixture, '#company', 'Firma');
      submitForm(fixture);
      expect(ctrl.expectOne('/auth/signup').request.body).toEqual({ email: 'anna@firma.example', company_name: 'Firma' });
    } finally {
      vi.useRealTimers();
    }
  });
});
