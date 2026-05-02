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
    // A request limit (429 too_many_requests): the number is what the server said.
    tryAgainIn: (seconds: number): string => `Try again in ${seconds} s.`,
  },

  // The provider buttons (spec 0011). The names are the providers' own; the words around them say what the button does.
  providers: {
    names: { google: 'Google', microsoft: 'Microsoft' } as Record<string, string>,
    either: 'Google or Microsoft',
    continueWith: (name: string): string => `Continue with ${name}`,
    signUpWith: (name: string): string => `Sign up with ${name}`,
    acceptWith: (name: string): string => `Accept with ${name}`,
    confirmWith: (name: string): string => `Confirm with ${name}`,
    or: 'or',
  },

  login: {
    title: 'Sign in',
    submit: 'Sign in',
    forgot: 'Forgot your password?',
    wrongCredentials: 'Wrong email or password.',
    notVerified: 'Your email address is not confirmed yet.',
    noMembership: 'Your account does not belong to a company.',
    createAccount: 'Create an account',
    accountDeleted: 'Your account was deleted.',
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
    // The paragraph of the invitation mail (spec 0010), for an address that has a private account.
    privateWarning: (company: string): string =>
      `You already have a private account with this email address. If you accept, you move to ${company}: the space of your private account ` +
      `is deleted, with everything in it. Your account itself stays, with its email address and its password, and becomes a member of ${company}.`,
    move: 'Move me to the company and delete my private account',
    // Not the link texts of the other screens: a person cannot get a new invitation from this app, only from the company.
    invalid: 'This invitation has expired or was already used. Ask for a new one.',
  },

  signup: {
    title: 'Create an account',
    intro: 'Enter your email address and the name of your company. We will send you a link to finish.',
    introPrivate: 'Enter your email address. We will send you a link to finish.',
    kindLegend: 'Create an account for',
    kindOrganization: 'Organization',
    kindPrivate: 'Private person',
    companyName: 'Company name',
    submit: 'Create account',
    // The same sentence for every address: the answer must not tell whether the address has an account.
    sent: 'Check your inbox. We sent a message to this address.',
    closed: 'Sign-up is closed. To join a company, ask it for an invitation.',
    invalidRequest: 'Check the email address and the company name.',
    invalidRequestPrivate: 'Check the email address.',
  },

  completeSignup: {
    title: 'Finish creating your account',
    loading: 'Opening your sign-up...',
    setsPassword: (email: string): string => `This sets the password for ${email}.`,
    submit: 'Create company',
    submitPrivate: 'Create account',
    signUpAgain: 'Sign up again',
    alreadyRegistered: 'An account with this address already exists.',
    companyRequired: 'Enter the name of your company.',
    invalidRequest: 'Check the company name.',
    invalidRequestPrivate: 'Check the password.',
  },

  // Spec 0011: the screen a provider round trip comes back to. The ticket is never shown.
  providerReturn: {
    title: 'Signing in',
    working: 'Signing you in...',
    errors: {
      cancelled: 'Sign-in was cancelled.',
      invalid_state: 'The sign-in took too long or was started in another browser. Try again.',
      provider_unavailable: 'This way to sign in is not available.',
      provider_error: 'Google or Microsoft could not sign you in. Try again.',
    } as Record<string, string>,
    invalidTicket: 'This sign-in took too long. Start again.',
    noAccount: 'You have no account. Ask your company for an invitation.',
    noEmail: 'Your Microsoft or Google account has no email address we can use. Sign in with your password.',
    alreadyRegistered: 'An account with this address exists. Sign in with your password.',
    addressMismatch: 'Use the Google or Microsoft account of the invited address, or open the invitation link again and choose a password.',
    alreadyMember: 'This account already belongs to a company.',
    privateAccountExists: 'You have a private account with this address. Open the invitation link again to move it into the company.',
    providerAlreadyLinked: 'This Google or Microsoft account already signs in to another account.',
    invalidToken: 'This invitation has expired or was already used. Ask for a new one.',
  },

  providerLink: {
    title: 'Connect your account',
    intro: (email: string, provider = 'Google'): string =>
      `An account with ${email} exists. Enter its password once to connect ${provider}. After that ${provider} alone signs you in.`,
    introUnverified: (email: string, provider = 'Microsoft'): string =>
      `If you have an account with ${email}, enter its password once to connect ${provider}. If you do not, create an account with your email address.`,
    createAccount: 'Create an account',
    submit: 'Connect and sign in',
    wrongPassword: "Wrong password. No password yet? Use 'Forgot your password?' to set one.",
  },

  providerFinish: {
    title: 'Finish creating your account',
    submit: 'Create account',
    alreadyRegistered: 'An account with this address already exists. Sign in with your password.',
  },

  notes: {
    title: 'Notes',
    signOut: 'Sign out',
    privateAccount: 'Private account',
    team: 'Team',
    deleteAccount: 'Delete account',
    deleteWarning: 'This deletes your account and all your notes. It cannot be undone.',
    deleteConfirm: 'Delete my account',
    cancel: 'Cancel',
    // The notes are deleted first (spec 0010): a wrong password then leaves the account without them, and the screen says so.
    wrongPassword: 'The password is wrong, so your account was not deleted. Your notes are already gone.',
    // Said next to every refusal that comes after the notes were deleted.
    notesGone: 'Your notes are already gone.',
    // The account moved into a company while the person was deleting it (an invitation was accepted in another tab).
    movedToCompany: 'Your account now belongs to a company, so it cannot be deleted here.',
    passwordRequired: 'Enter your password.',
    // Spec 0011: the provider confirmation of an account without a password did not hold (wrong account, too late, used).
    reauthFailed:
      'The confirmation did not work or took too long, so your account was not deleted. Your notes are already gone. Confirm again.',
    newNote: 'New note',
    add: 'Add note',
    empty: 'No notes yet.',
    tooLong: (max: number): string => `A note can be at most ${max} characters.`,
  },

  team: {
    title: 'Team',
    loading: 'Loading...',
    back: 'Back to the notes',
    remove: 'Remove',
    cancel: 'Cancel',
    // Spec 0010: removing a member deletes the account. A product's own team screen says the same.
    removeConfirm: "This person's account will be deleted.",
    permissionNotHeld: 'You cannot remove someone who holds more permissions than you.',
    lastManager: 'The company needs at least one person who manages its members.',
    cannotChangeSelf: 'You cannot remove yourself.',
    notAllowed: "You don't have access to this.",
  },
};
