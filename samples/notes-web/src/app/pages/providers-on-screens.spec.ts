import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, realLocation, settle, spacedText } from '../../testing/helpers';
import { InvitePage } from './invite';
import { LoginPage } from './login';
import { SignupPage } from './signup';

const both = { signup: 'self-service', private_signup: 'self-service' };
const preview = { org_name: 'Acme', email: 'new@acme.example', role: 'viewer' };

async function open(path: string, page: Type<unknown>, options: object, query = '') {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path, component: page }]), provideHttpClient(), provideHttpClientTesting(), realLocation],
  });
  history.replaceState(null, '', `/${path}${query}`);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/${path}${query}`, page);
  await settle();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne('/auth/options').flush(options);
  await settle(harness.fixture);
  return { fixture: harness.fixture, ctrl };
}

/** The text of the page up to the first form field: what comes first. */
function before(fixture: Parameters<typeof pageText>[0], selector: string): string {
  const root = fixture.nativeElement as HTMLElement;
  const field = root.querySelector(selector)!;
  const range = document.createRange();
  range.setStart(root, 0);
  range.setEndBefore(field);
  return spacedText(range.cloneContents());
}

describe('the provider buttons on the screens (spec 0011)', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('/login: the buttons first, in the order of the options, then "or", then the form', async () => {
    const { fixture } = await open('login', LoginPage, { ...both, providers: ['microsoft', 'google'] });
    expect(before(fixture, '#email')).toBe('Sign in Continue with Microsoft Continue with Google or Email');
    expect(has(fixture, 'a[href="/auth/providers/google/start"]')).toBe(true);
  });

  it('/login: an unknown name draws no button and a duplicated name only one', async () => {
    const { fixture } = await open('login', LoginPage, { ...both, providers: ['github', 'google', 'google', 'Microsoft'] });
    const links = [...(fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="provider-buttons"] a')];
    expect(links.map((a) => a.getAttribute('href'))).toEqual(['/auth/providers/google/start']);
    expect(pageText(fixture)).not.toContain('github');
  });

  it('/login: a failed options call draws no button and the form still works', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'login', component: LoginPage }]), provideHttpClient(), provideHttpClientTesting(), realLocation],
    });
    history.replaceState(null, '', '/login');
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/login', LoginPage);
    await settle();
    TestBed.inject(HttpTestingController).expectOne('/auth/options').flush('oops', { status: 500, statusText: '500' });
    await settle(harness.fixture);
    expect(has(harness.fixture, '[data-testid="provider-buttons"]')).toBe(false);
    expect(has(harness.fixture, '#email')).toBe(true);
  });

  it('/login: no button when providers is empty', async () => {
    const { fixture } = await open('login', LoginPage, { ...both, providers: [] });
    expect(has(fixture, '[data-testid="provider-buttons"]')).toBe(false);
    expect(pageText(fixture)).not.toContain('Continue with');
  });

  it('/signup: "Sign up with ..." first, then the form of spec 0010', async () => {
    const { fixture } = await open('signup', SignupPage, { ...both, providers: ['google', 'microsoft'] });
    expect(before(fixture, '[data-testid="signup-kind"]')).toContain('Sign up with Google Sign up with Microsoft or');
    expect(has(fixture, '#company')).toBe(true);
  });

  it('/signup: a failed options call draws no button, and the form of the organization still draws', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'signup', component: SignupPage }]), provideHttpClient(), provideHttpClientTesting(), realLocation],
    });
    history.replaceState(null, '', '/signup');
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/signup', SignupPage);
    await settle();
    TestBed.inject(HttpTestingController).expectOne('/auth/options').flush('oops', { status: 500, statusText: '500' });
    await settle(harness.fixture);
    expect(has(harness.fixture, '[data-testid="provider-buttons"]')).toBe(false);
    expect(pageText(harness.fixture)).not.toContain('Sign up with');
    expect(has(harness.fixture, '#email')).toBe(true);
    expect(has(harness.fixture, '#company')).toBe(true);
  });

  it('/signup: no button when providers is empty', async () => {
    const { fixture } = await open('signup', SignupPage, { ...both, providers: [] });
    expect(pageText(fixture)).not.toContain('Sign up with');
  });

  it('/invite: "Accept with ..." with the invitation token, before the password form', async () => {
    const { fixture, ctrl } = await open('invite', InvitePage, { ...both, providers: ['google', 'microsoft'] }, '?token=tok-i');
    ctrl.expectOne('/auth/invites/preview').flush({ ...preview, has_private_account: false });
    await settle(fixture);
    expect(before(fixture, '#password')).toContain('Accept with Google Accept with Microsoft or');
    expect(has(fixture, 'a[href="/auth/providers/google/start?invite=tok-i"]')).toBe(true);
    expect(window.location.href).not.toContain('tok-i'); // the token left the address bar; it is only in the link
  });

  it('/invite: the token is encoded in the link, so that no other member rides along', async () => {
    const { fixture, ctrl } = await open('invite', InvitePage, { ...both, providers: ['google'] }, '?token=a%26purpose%3Dreauth');
    ctrl.expectOne('/auth/invites/preview').flush({ ...preview, has_private_account: false });
    await settle(fixture);
    expect(has(fixture, 'a[href="/auth/providers/google/start?invite=a%26purpose%3Dreauth"]')).toBe(true);
  });

  it('/invite: a failed options call draws no button, the password form stays', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'invite', component: InvitePage }]), provideHttpClient(), provideHttpClientTesting(), realLocation],
    });
    history.replaceState(null, '', '/invite?token=tok-i');
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/invite?token=tok-i', InvitePage);
    await settle();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne('/auth/options').flush('oops', { status: 500, statusText: '500' });
    ctrl.expectOne('/auth/invites/preview').flush({ ...preview, has_private_account: false });
    await settle(harness.fixture);
    expect(pageText(harness.fixture)).not.toContain('Accept with');
    expect(has(harness.fixture, '#password')).toBe(true);
  });

  it('/invite: no button for an address with a private account: the screen of spec 0010', async () => {
    const { fixture, ctrl } = await open('invite', InvitePage, { ...both, providers: ['google'] }, '?token=tok-i');
    ctrl.expectOne('/auth/invites/preview').flush({ ...preview, has_private_account: true });
    await settle(fixture);
    expect(has(fixture, '[data-testid="move"]')).toBe(true);
    expect(pageText(fixture)).not.toContain('Accept with');
  });

  it('/invite: no button when providers is empty', async () => {
    const { fixture, ctrl } = await open('invite', InvitePage, { ...both, providers: [] }, '?token=tok-i');
    ctrl.expectOne('/auth/invites/preview').flush({ ...preview, has_private_account: false });
    await settle(fixture);
    expect(pageText(fixture)).not.toContain('Accept with');
  });
});
