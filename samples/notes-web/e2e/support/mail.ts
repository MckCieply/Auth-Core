import { Page } from '@playwright/test';
import { MAIL_LINK_ORIGIN, mailpitUrl } from './env';

export type LinkKind = 'reset' | 'verify' | 'invite' | 'complete-signup';

const SUBJECT_STARTS: Record<LinkKind, string> = {
  reset: 'Reset your',
  verify: 'Confirm your email',
  invite: 'You are invited',
  'complete-signup': 'Complete your sign-up',
};

// The mail an address with an account gets when someone signs up with it: no link, no token.
const ACCOUNT_EXISTS_SUBJECT = 'You already have an account';

interface MailSummary {
  ID: string;
  Subject: string;
  To: { Address: string }[] | null;
}

interface Mail {
  Text: string;
  HTML: string;
}

async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`the mail catcher answered ${response.status} for ${new URL(url).pathname}`);
  }
  return (await response.json()) as T;
}

/**
 * Waits for the newest mail of a kind to an address and returns its link as a path on the test origin. The link in the mail
 * points at http://localhost:8088; the tests run on https://localhost:8443, so the path and the token are taken over.
 * The result holds a token: never log it.
 */
export async function waitForLink(to: string, kind: LinkKind, timeoutMs = 150_000): Promise<string> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const link = await newestLink(to, kind);
    if (link !== null) {
      return link;
    }
    if (Date.now() > deadline) {
      throw new Error(`no ${kind} mail for ${to} within ${timeoutMs / 1000} seconds`);
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}

/** The text and the HTML part of the newest "you already have an account" mail to an address. The caller checks that they hold no link. */
export async function waitForAccountExistsMail(to: string, timeoutMs = 150_000): Promise<{ text: string; html: string }> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const mail = await newestMail(to, ACCOUNT_EXISTS_SUBJECT);
    if (mail !== null) {
      return { text: mail.Text, html: mail.HTML };
    }
    if (Date.now() > deadline) {
      throw new Error(`no "you already have an account" mail for ${to} within ${timeoutMs / 1000} seconds`);
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}

/**
 * Opens a link from waitForLink. A failed navigation must not print the link (Playwright puts the URL in its error, and the URL
 * holds a token): the error here says only that it failed.
 */
export async function openMailLink(page: Page, link: string): Promise<void> {
  try {
    await page.goto(link);
  } catch {
    throw new Error('opening the link of a mail failed (not printed: the link holds a token)');
  }
}

/** The newest mail to exactly this address whose subject starts so, or null. */
async function newestMail(to: string, subjectStart: string): Promise<Mail | null> {
  const found = await getJson<{ messages: MailSummary[] }>(
    `${mailpitUrl()}/api/v1/search?query=${encodeURIComponent(`to:${to}`)}`,
  );
  // The search matches part of an address: the mail must be to this address and no other.
  const summary = found.messages.find(
    (message) =>
      message.Subject.startsWith(subjectStart) &&
      (message.To ?? []).some((recipient) => recipient.Address.toLowerCase() === to.toLowerCase()),
  );
  return summary === undefined ? null : await getJson<Mail>(`${mailpitUrl()}/api/v1/message/${summary.ID}`);
}

async function newestLink(to: string, kind: LinkKind): Promise<string | null> {
  const mail = await newestMail(to, SUBJECT_STARTS[kind]);
  if (mail === null) {
    return null;
  }
  const match = new RegExp(`(https?://[^\\s/]+)/${kind}\\?token=([A-Za-z0-9_-]+)`).exec(mail.Text);
  if (match === null) {
    throw new Error(`the ${kind} mail for ${to} holds no link of the form /${kind}?token=...`);
  }
  if (match[1] !== MAIL_LINK_ORIGIN) {
    throw new Error(`the ${kind} mail for ${to} points at ${match[1]}, not at ${MAIL_LINK_ORIGIN}`);
  }
  if (!mail.HTML.includes(match[2])) {
    throw new Error(`the HTML part of the ${kind} mail for ${to} does not hold the token of its text part`);
  }
  return `/${kind}?token=${match[2]}`;
}
