// Every text the person sees. A product changes the wording of the screens here, in one place.
// Texts that carry a number or a name are functions. Plain printable ASCII: texts.spec.ts checks it.

function minutesFrom(seconds: number): number {
  return Math.max(1, Math.ceil(seconds / 60));
}

function counted(count: number, one: string, many: string): string {
  return count === 1 ? `${count} ${one}` : `${count} ${many}`;
}

export const texts = {
  common: {
    email: 'Email',
    password: 'Password',
    newPassword: 'New password',
    repeatPassword: 'Repeat the password',
    passwordsDiffer: 'The two passwords are not the same.',
    somethingWrong: 'Something went wrong. Try again.',
    tryAgain: 'Try again',
    invalidLink: 'This link has expired or was already used.',
    askForNewLink: 'Ask for a new link',
    sendVerificationAgain: 'Send the verification link again',
    verificationSent: 'If this address needs confirming, we sent a new link.',
    waitSeconds: (seconds: number): string => `Wait ${counted(seconds, 'second', 'seconds')} before asking again.`,
    signInLink: 'Sign in.',
    backToSignIn: 'Back to sign in',
  },

  // The bar above every screen. The keys are the notices of AuthService.
  notices: {
    unreachable: "Can't reach the server. Try again shortly.",
    tryLater: 'Try again shortly.',
    forbidden: "You don't have access to this.",
    dismiss: 'Dismiss',
  },

  login: {
    title: 'Sign in',
    submit: 'Sign in',
    forgot: 'Forgot your password?',
    wrongCredentials: 'Wrong email or password.',
    notVerified: 'Your email address is not confirmed yet.',
    noMembership: 'Your account does not belong to a company.',
    tooManyAttempts: (seconds: number): string =>
      `Too many attempts. Try again in ${counted(minutesFrom(seconds), 'minute', 'minutes')}.`,
  },

  forgot: {
    title: 'Forgot your password?',
    intro: 'Enter your email address and we will send you a link to choose a new password.',
    submit: 'Send the link',
    sent: 'If an account exists for this address, we sent a link.',
  },

  reset: {
    title: 'Choose a new password',
    submit: 'Change password',
    done: 'Password changed.',
    rules: {
      too_short: 'The password is too short.',
      requires_upper: 'The password needs an uppercase letter.',
      requires_lower: 'The password needs a lowercase letter.',
      requires_digit: 'The password needs a digit.',
    } as Record<string, string>,
    ruleOther: 'The password does not meet a rule.',
  },

  verify: {
    title: 'Confirm your email',
    working: 'Confirming your email...',
    done: 'Email confirmed.',
  },

  invite: {
    title: 'Join a company',
    loading: 'Opening your invitation...',
    joinPrefix: 'Join',
    joinMiddle: 'as',
    setsPassword: (email: string): string => `This sets the password for ${email}.`,
    submit: 'Join',
    alreadyMember: 'This account already belongs to a company.',
    // Not the link texts of the other screens: a person cannot get a new invitation from this app, only from the company.
    invalid: 'This invitation has expired or was already used. Ask for a new one.',
  },

  notes: {
    title: 'Notes',
    signOut: 'Sign out',
    newNote: 'New note',
    add: 'Add note',
    empty: 'No notes yet.',
    loadFailed: 'The notes could not be loaded. Try again.',
    addFailed: 'The note could not be saved. Try again.',
    tooLong: (max: number): string => `A note can be at most ${max} characters.`,
  },
};
