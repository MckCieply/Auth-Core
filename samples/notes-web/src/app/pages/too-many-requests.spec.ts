import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { has, pageText, settle, submitForm, typeInto } from '../../testing/helpers';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { CompleteSignupPage } from './complete-signup';
import { ForgotPage } from './forgot';
import { InvitePage } from './invite';
import { ResetPage } from './reset';
import { SignupPage } from './signup';
import { VerifyPage } from './verify';

const answer = { error: 'too_many_requests', retry_after_seconds: 17 };
const limited = { status: 429, statusText: '429' };

/**
 * The limit per address (spec 0008) answers 429 too_many_requests to any call. The notice bar says when to try again; the screen
 * must not add "Something went wrong" or a sentence of success, and the button must work again.
 */
async function open<T>(page: Type<T>, url: string) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([{ path: url.split('?')[0].slice(1), component: page }]),
      provideHttpClient(withInterceptors([authInterceptor])),
      provideHttpClientTesting(),
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url, page);
  await settle(); // the navigation that takes a token out of the address bar lands first
  vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  return { fixture: harness.fixture, ctrl: TestBed.inject(HttpTestingController), auth: TestBed.inject(AuthService) };
}

type Opened = Awaited<ReturnType<typeof open>>;

async function expectNoticeOnly(opened: Opened, request: string, ...absent: string[]): Promise<void> {
  opened.ctrl.expectOne(request).flush(answer, limited);
  await settle(opened.fixture);
  expect(opened.auth.notice()).toBe('wait');
  expect(opened.auth.noticeSeconds()).toBe(17);
  expect(pageText(opened.fixture)).not.toContain('Something went wrong');
  for (const text of absent) {
    expect(pageText(opened.fixture)).not.toContain(text);
  }
  expect(opened.fixture.nativeElement.querySelector('button[type="submit"]').disabled).toBe(false);
  opened.ctrl.verify();
}

describe('the mail and link screens when the address has made too many requests', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('SignupPage: leaves the words to the notice bar and does not say it sent anything', async () => {
    const opened = await open(SignupPage, '/signup');
    typeInto(opened.fixture, '#email', 'anna@firma.example');
    typeInto(opened.fixture, '#company', 'Firma');
    submitForm(opened.fixture);
    await expectNoticeOnly(opened, '/auth/signup', 'Check your inbox');
    expect(has(opened.fixture, '[data-testid="signup-sent"]')).toBe(false);
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('ForgotPage: leaves the words to the notice bar and does not say it sent anything', async () => {
    const opened = await open(ForgotPage, '/forgot');
    typeInto(opened.fixture, '#email', 'user@example.test');
    submitForm(opened.fixture);
    await expectNoticeOnly(opened, '/auth/password/forgot', 'we sent a link');
    expect(has(opened.fixture, '[data-testid="forgot-sent"]')).toBe(false);
  });

  it('VerifyPage: the form for a new link leaves the words to the notice bar', async () => {
    const opened = await open(VerifyPage, '/verify');
    typeInto(opened.fixture, '#email', 'user@example.test');
    submitForm(opened.fixture);
    await expectNoticeOnly(opened, '/auth/email/verify/request');
  });

  it('ResetPage: keeps the form, leaves the words to the notice bar', async () => {
    const opened = await open(ResetPage, '/reset?token=tok-123');
    typeInto(opened.fixture, '#password', 'New-Passw0rd');
    typeInto(opened.fixture, '#repeat', 'New-Passw0rd');
    submitForm(opened.fixture);
    await expectNoticeOnly(opened, '/auth/password/reset');
    expect(has(opened.fixture, '#password')).toBe(true);
    expect(has(opened.fixture, '[data-testid="reset-done"]')).toBe(false);
  });

  it('CompleteSignupPage: keeps the form, leaves the words to the notice bar', async () => {
    const opened = await open(CompleteSignupPage, '/complete-signup?token=tok-s');
    opened.ctrl.expectOne('/auth/signup/preview').flush({ email: 'anna@firma.example', company_name: 'Firma' });
    await settle(opened.fixture);
    typeInto(opened.fixture, '#password', 'Chosen-Passw0rd');
    typeInto(opened.fixture, '#repeat', 'Chosen-Passw0rd');
    submitForm(opened.fixture);
    await expectNoticeOnly(opened, '/auth/signup/complete');
    expect(has(opened.fixture, '#password')).toBe(true);
    expect(TestBed.inject(Router).navigate).not.toHaveBeenCalledWith(['/login'], expect.anything());
  });

  it('InvitePage: keeps the form, leaves the words to the notice bar', async () => {
    const opened = await open(InvitePage, '/invite?token=tok-i');
    opened.ctrl.expectOne('/auth/invites/preview').flush({ org_name: 'Acme', email: 'new@acme.example', role: 'viewer' });
    await settle(opened.fixture);
    typeInto(opened.fixture, '#password', 'Joined-Passw0rd');
    typeInto(opened.fixture, '#repeat', 'Joined-Passw0rd');
    submitForm(opened.fixture);
    await expectNoticeOnly(opened, '/auth/invites/accept');
    expect(has(opened.fixture, '#password')).toBe(true);
    expect(TestBed.inject(Router).navigate).not.toHaveBeenCalledWith(['/login'], expect.anything());
  });
});
