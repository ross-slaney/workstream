/** Which step of signing in is on screen. */
export type SignInMode = "login" | "signup" | "verify" | "name" | "finishing";

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
