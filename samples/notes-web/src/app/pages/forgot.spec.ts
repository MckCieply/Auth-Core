import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, settle, submitForm, typeInto, typeRaw } from '../../testing/helpers';
import { ForgotPage } from './forgot';

const status = (code: number) => ({ status: code, statusText: String(code) });

async function open() {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'forgot', component: ForgotPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/forgot', ForgotPage);
  return { fixture: harness.fixture, ctrl: TestBed.inject(HttpTestingController) };
}

describe('ForgotPage', () => {
  const sent = 'If an account exists for this address, we sent a link.';

  it('asks for an email and links back to the sign-in', async () => {
    const { fixture } = await open();
    expect(has(fixture, '#email')).toBe(true);
    expect(has(fixture, 'a[href="/login"]')).toBe(true);
  });

  it.each(['user@example.test', 'nobody@nowhere.example'])('202 for %s: always the same sentence', async (email) => {
    const { fixture, ctrl } = await open();
    typeRaw(fixture, '#email', ` ${email} `);
    submitForm(fixture);
    const request = ctrl.expectOne('/auth/password/forgot');
    expect(request.request.body).toEqual({ email });
    request.flush(null, status(202));
    await settle(fixture);
    expect(pageText(fixture)).toContain(sent);
  });

  it('429: "Wait N seconds before asking again."', async () => {
    const { fixture, ctrl } = await open();
    typeInto(fixture, '#email', 'user@example.test');
    submitForm(fixture);
    ctrl.expectOne('/auth/password/forgot').flush({ error: 'too_many_attempts', retry_after_seconds: 42 }, status(429));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Wait 42 seconds before asking again.');
    expect(pageText(fixture)).not.toContain(sent);
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, '<html>oops</html>'],
  ])('%s: "Something went wrong. Try again."', async (code, body) => {
    const { fixture, ctrl } = await open();
    typeInto(fixture, '#email', 'user@example.test');
    submitForm(fixture);
    ctrl.expectOne('/auth/password/forgot').flush(body, status(code));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Something went wrong. Try again.');
    expect(pageText(fixture)).not.toContain(sent);
  });

  it('sends nothing for an empty email, and one request for a double submit', async () => {
    const { fixture, ctrl } = await open();
    submitForm(fixture);
    ctrl.expectNone('/auth/password/forgot');
    typeInto(fixture, '#email', 'user@example.test');
    submitForm(fixture);
    submitForm(fixture);
    ctrl.expectOne('/auth/password/forgot').flush(null, status(202));
  });
});
