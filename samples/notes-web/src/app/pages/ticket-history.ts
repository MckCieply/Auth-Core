import { Router } from '@angular/router';

/**
 * A provider ticket came in the history entry of the current screen (the navigation state of /provider-return). It lives on in the
 * component's memory only: the entry is replaced, same address, with no state, so that a reload, a step back or a step forward does not find
 * the ticket in `history.state` (a reload then goes to /login).
 */
export async function keepTicketOutOfHistory(router: Router): Promise<void> {
  // After the navigation that is building this screen.
  await Promise.resolve();
  try {
    await router.navigateByUrl(router.url, { replaceUrl: true, onSameUrlNavigation: 'reload' });
  } catch {
    // the entry keeps the state; the ticket is still only good for a few minutes and once
  }
}

/**
 * The ticket of a confirmation that /provider-return handed on in the navigation state (spec 0011; "Delete account" and "Delete company"), or
 * null. Only in memory: never in a URL or in storage. It is read by the routed page, in a field initializer or its constructor, while the
 * navigation that brought it is current; a component created later by the page's template finds no navigation, so the page passes it down.
 */
export function reauthTicketOf(router: Router): string | null {
  const ticket: unknown = router.currentNavigation()?.extras.state?.['reauthTicket'];
  return typeof ticket === 'string' && ticket !== '' ? ticket : null;
}
