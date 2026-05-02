import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AccountApi, PROVIDER_NAMES, isProviderName, providerStartUrl } from './account-api';

describe('AccountApi: the providers of GET /auth/options (spec 0011)', () => {
  let api: AccountApi;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(AccountApi);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => ctrl.verify());

  it('reads the offered providers in the order of the answer', async () => {
    const done = api.options();
    ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'off', providers: ['microsoft', 'google'] });
    expect((await done).providers).toEqual(['microsoft', 'google']);
  });

  it.each([
    [['google', 'github', 'microsoft'], ['google', 'microsoft']],
    [['google', 'google'], ['google']],
    [['Google', 'MICROSOFT'], []],
    [[1, null, 'microsoft'], ['microsoft']],
    ['google', []],
    [null, []],
  ])('keeps only the names it knows, once each: %j -> %j', async (providers, expected) => {
    const done = api.options();
    ctrl.expectOne('/auth/options').flush({ signup: 'self-service', providers });
    expect((await done).providers).toEqual(expected);
  });

  it('a failed call means no provider', async () => {
    const done = api.options();
    ctrl.expectOne('/auth/options').flush('oops', { status: 500, statusText: '500' });
    expect(await done).toEqual({ signup: 'invite-only', private_signup: 'off', providers: [] });
  });

  it('knows Google and Microsoft', () => {
    expect(PROVIDER_NAMES).toEqual(['google', 'microsoft']);
    expect(isProviderName('google')).toBe(true);
    expect(isProviderName('github')).toBe(false);
    expect(isProviderName(undefined)).toBe(false);
  });

  it('builds the start addresses of the buttons: plain, with an invitation, for a confirmation', () => {
    expect(providerStartUrl('google')).toBe('/auth/providers/google/start');
    expect(providerStartUrl('microsoft', { invite: 'abc_DEF-123' })).toBe('/auth/providers/microsoft/start?invite=abc_DEF-123');
    expect(providerStartUrl('google', { invite: 'a b&c' })).toBe('/auth/providers/google/start?invite=a%20b%26c');
    expect(providerStartUrl('google', { reauth: true })).toBe('/auth/providers/google/start?purpose=reauth');
  });

  it.each([
    ['x&purpose=reauth', '/auth/providers/google/start?invite=x%26purpose%3Dreauth'],
    ['x#frag', '/auth/providers/google/start?invite=x%23frag'],
    ['../../evil', '/auth/providers/google/start?invite=..%2F..%2Fevil'],
    ['a=b?c', '/auth/providers/google/start?invite=a%3Db%3Fc'],
  ])('an invitation value %j is one query value: no injection into the address', (invite, expected) => {
    expect(providerStartUrl('google', { invite })).toBe(expected);
    expect(new URL(providerStartUrl('google', { invite }), 'https://app.example').searchParams.get('invite')).toBe(invite);
  });

  it('keeps an empty list as it is', async () => {
    const done = api.options();
    ctrl.expectOne('/auth/options').flush({ signup: 'self-service', providers: [] });
    expect((await done).providers).toEqual([]);
  });

  it('a body that is not an object means no provider', async () => {
    const done = api.options();
    ctrl.expectOne('/auth/options').flush('<html>proxy</html>');
    expect((await done).providers).toEqual([]);
  });
});
