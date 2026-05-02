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
