import { convertToParamMap } from '@angular/router';
import { takeTokenFromUrl } from './token-from-url';

const route = (params: Record<string, string | string[]>) => ({ snapshot: { queryParamMap: convertToParamMap(params) } });

describe('takeTokenFromUrl', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('returns the token and takes it out of the address bar', () => {
    history.replaceState(null, '', '/reset?token=abc123');
    expect(takeTokenFromUrl(route({ token: 'abc123' }))).toBe('abc123');
    expect(window.location.search).toBe('');
    expect(window.location.pathname).toBe('/reset');
  });

  it('keeps the history state the router put there', () => {
    history.replaceState({ navigationId: 7 }, '', '/reset?token=abc123');
    takeTokenFromUrl(route({ token: 'abc123' }));
    expect(history.state).toEqual({ navigationId: 7 });
  });

  it('takes out a fragment too, and everything else in the query', () => {
    history.replaceState(null, '', '/verify?token=abc123&x=1#frag');
    takeTokenFromUrl(route({ token: 'abc123', x: '1' }));
    expect(window.location.href.endsWith('/verify')).toBe(true);
  });

  it.each([[{}], [{ token: '' }]])('is null for %j, and still cleans the address bar', (params) => {
    history.replaceState(null, '', '/reset?token=');
    expect(takeTokenFromUrl(route(params))).toBeNull();
    expect(window.location.search).toBe('');
  });

  it('uses the first of two token parameters', () => {
    expect(takeTokenFromUrl(route({ token: ['first', 'second'] }))).toBe('first');
  });
});
