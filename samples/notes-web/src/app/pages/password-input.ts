// Shared by the screens that send a password to Auth-Core (spec 0010, spec 0012): what it refuses as a malformed request.

// What .NET calls white space, which is what Auth-Core calls a blank password (spec 0010: 400 invalid_request).
const BLANK = /^[\t-\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]*$/;
// A surrogate half with no partner: JSON carries it as an escape, and Auth-Core refuses a body that holds one.
const LONE_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/;
const MAX_BODY_BYTES = 8 * 1024;

/**
 * True for a password that Auth-Core will not refuse as a malformed request: not blank, no NUL, no lone surrogate and a body that fits.
 * The notes are deleted before the account call, so a request that is certain to fail must not get that far.
 */
export function acceptablePassword(password: string): boolean {
  return (
    !BLANK.test(password) &&
    !password.includes('\0') &&
    !LONE_SURROGATE.test(password) &&
    new TextEncoder().encode(JSON.stringify({ password })).length <= MAX_BODY_BYTES
  );
}
