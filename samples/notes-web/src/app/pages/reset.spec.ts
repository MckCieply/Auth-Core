import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, settle, submitForm, typeInto } from '../../testing/helpers';
import { ResetPage } from './reset';

const status = (code: number) => ({ status: code, statusText: String(code) });
const invalid = 'This link has expired or was already used.';

async function open(query = '?token=tok-123') {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'reset', component: ResetPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  history.replaceState(null, '', `/reset${query}`);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/reset${query}`, ResetPage);
  return { fixture: harness.fixture, ctrl: TestBed.inject(HttpTestingController) };
}

type Opened = Awaited<ReturnType<typeof open>>;

function choose({ fixture }: Opened, password: string, repeat = password): void {
  typeInto(fixture, '#password', password);
  typeInto(fixture, '#repeat', repeat);
  submitForm(fixture);
}

function ruleLines(opened: Opened): (string | undefined)[] {
  return Array.from(
    (opened.fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="rules"] li'),
    (item) => item.textContent?.trim(),
  );
}

describe('ResetPage', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('takes the token out of the address bar when it opens', async () => {
    await open();
    expect(window.location.search).toBe('');
    expect(window.location.href).not.toContain('tok-123');
  });

  it('sends the token it read and the new password exactly as typed', async () => {
    const opened = await open();
    choose(opened, '  New Passw0rd  ');
    const request = opened.ctrl.expectOne('/auth/password/reset');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-123', new_password: '  New Passw0rd  ' });
    request.flush(null, status(204));
  });

  it('204: "Password changed. Sign in." with a link to /login, and nobody is signed in', async () => {
    const opened = await open();
    choose(opened, 'New-Passw0rd');
    opened.ctrl.expectOne('/auth/password/reset').flush(null, status(204));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Password changed. Sign in.');
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
    expect(has(opened.fixture, 'form')).toBe(false);
    opened.ctrl.expectNone('/auth/login');
  });

  it('weak_password: the rules not met, one line each', async () => {
    const opened = await open();
    choose(opened, 'short');
    opened.ctrl
      .expectOne('/auth/password/reset')
      .flush({ error: 'weak_password', rules: ['too_short', 'requires_upper', 'requires_digit'] }, status(400));
    await settle(opened.fixture);
    expect(ruleLines(opened)).toEqual([
      'The password is too short.',
      'The password needs an uppercase letter.',
      'The password needs a digit.',
    ]);
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('weak_password: a rule it has no text for is still a line, and the next try replaces the lines', async () => {
    const opened = await open();
    choose(opened, 'short');
    opened.ctrl.expectOne('/auth/password/reset').flush({ error: 'weak_password', rules: ['requires_symbol'] }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The password does not meet a rule.');
    choose(opened, 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/password/reset').flush({ error: 'weak_password', rules: ['too_short'] }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).not.toContain('The password does not meet a rule.');
    expect(pageText(opened.fixture)).toContain('The password is too short.');
  });

  it('a rule named like a property of every object is a line with the fallback text, never a function', async () => {
    const opened = await open();
    choose(opened, 'short');
    opened.ctrl
      .expectOne('/auth/password/reset')
      .flush({ error: 'weak_password', rules: ['constructor', 'toString', '__proto__'] }, status(400));
    await settle(opened.fixture);
    expect(ruleLines(opened)).toEqual([
      'The password does not meet a rule.',
      'The password does not meet a rule.',
      'The password does not meet a rule.',
    ]);
  });

  it('invalid_token: "This link has expired or was already used." with a link to /forgot', async () => {
    const opened = await open();
    choose(opened, 'New-Passw0rd');
    opened.ctrl.expectOne('/auth/password/reset').flush({ error: 'invalid_token' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(invalid);
    expect(has(opened.fixture, 'a[href="/forgot"]')).toBe(true);
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, '<html>oops</html>'],
  ])('%s: "Something went wrong. Try again."', async (code, body) => {
    const opened = await open();
    choose(opened, 'New-Passw0rd');
    opened.ctrl.expectOne('/auth/password/reset').flush(body, status(code));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('two different passwords: a message, and no request', async () => {
    const opened = await open();
    choose(opened, 'New-Passw0rd', 'New-Passw0rd2');
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The two passwords are not the same.');
    opened.ctrl.expectNone('/auth/password/reset');
  });

  it.each([['', 'a link without a token'], ['?token=', 'a link with an empty token'], ['?other=1', 'a link with another parameter']])(
    '%s (%s) is an invalid link, with no request',
    async (query) => {
      const opened = await open(query);
      expect(pageText(opened.fixture)).toContain(invalid);
      expect(has(opened.fixture, 'a[href="/forgot"]')).toBe(true);
      expect(has(opened.fixture, 'form')).toBe(false);
      opened.ctrl.expectNone('/auth/password/reset');
    },
  );

  it('sends one request for a double submit', async () => {
    const opened = await open();
    choose(opened, 'New-Passw0rd');
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/password/reset').flush(null, status(204));
  });
});
