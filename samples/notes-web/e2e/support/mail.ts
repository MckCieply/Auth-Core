import { mailpitUrl } from './env';

export type LinkKind = 'reset' | 'verify' | 'invite';

const SUBJECT_STARTS: Record<LinkKind, string> = {
  reset: 'Reset your',
  verify: 'Confirm your email',
  invite: 'You are invited',
};

interface MailSummary {
  ID: string;
  Subject: string;
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

async function newestLink(to: string, kind: LinkKind): Promise<string | null> {
  const found = await getJson<{ messages: MailSummary[] }>(
    `${mailpitUrl()}/api/v1/search?query=${encodeURIComponent(`to:${to}`)}`,
  );
  const summary = found.messages.find((message) => message.Subject.startsWith(SUBJECT_STARTS[kind]));
  if (summary === undefined) {
    return null;
  }
  const mail = await getJson<Mail>(`${mailpitUrl()}/api/v1/message/${summary.ID}`);
  const match = new RegExp(`https?://localhost:8088/${kind}\\?token=([A-Za-z0-9_-]+)`).exec(mail.Text);
  if (match === null) {
    throw new Error(`the ${kind} mail for ${to} holds no link of the form /${kind}?token=...`);
  }
  if (!mail.HTML.includes(match[1])) {
    throw new Error(`the HTML part of the ${kind} mail for ${to} does not hold the token of its text part`);
  }
  return `/${kind}?token=${match[1]}`;
}
