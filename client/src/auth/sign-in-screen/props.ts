/**
 * Which step of signing in is on screen. An invitation adds two: `opening` while SqlOS looks it up, and
 * `unusable` for a link that no longer works.
 */
export type SignInMode = "login" | "signup" | "verify" | "name" | "finishing" | "opening" | "unusable";

/**
 * Signing in or creating an account. Whether it is by emailed code or by password is the server's choice
 * (`method`), read from SqlOS's settings; the view shows the fields that go with it and never asks where it
 * is running.
 */
export type SignInScreenProps = {
  mode: SignInMode;
  method: "code" | "password";
  /** Waiting on the server: the button shows it and fields are still. */
  busy: boolean;
  /** The invitation being accepted: the team, and the one address that can accept it. Null for plain sign-in. */
  invitation: { teamName: string; email: string } | null;
  /** The address typed, or the invitation's. */
  email: string;
  name: string;
  password: string;
  code: string;
  /** Why the last step was refused, in words to show. */
  error: string | null;
  onEmailChange: (value: string) => void;
  onNameChange: (value: string) => void;
  onPasswordChange: (value: string) => void;
  onCodeChange: (value: string) => void;
  onSubmit: () => void;
  /** Send a fresh code to the same address. */
  onResend: () => void;
  /** Start over with another address. */
  onRestart: () => void;
  /** Between signing in and creating an account. */
  onSwitchMode: () => void;
  homeHref: string;
};
