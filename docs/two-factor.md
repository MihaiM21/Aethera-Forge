# Two-factor authentication

Aethera can ask for a code from an authenticator app after your password when you sign in to the UI. It uses standard time-based codes (TOTP), so any authenticator works: 1Password, Bitwarden, Google Authenticator, Microsoft Authenticator, Aegis, Raivo. Design and API details: [ADR 0013](architecture/0013-two-factor-and-pinned-image-deploys.md).

If your control plane's UI is reachable from the internet, turn this on for every account and require it for the organization.

## Turn it on

1. Open **Settings > Account** and choose **Set up two-factor authentication**.
2. Enter your current password.
3. Scan the QR code with your authenticator app, or type the key shown next to it.
4. Enter the 6-digit code the app shows and choose **Turn on two-factor authentication**.
5. Save the ten **recovery codes** (copy them into a password manager or download the file). They are shown only once.

Turning it on signs out your other browser sessions. Your API tokens keep working.

## Signing in

After your email and password, Aethera asks for the code from your app. You have five minutes; after that, enter your password again. A code works once, so if a sign-in fails, wait for the next code.

Lost your phone? Choose **Use a recovery code** and enter one of your saved codes. Each recovery code works once. Settings > Account shows how many are left; create a new set with **New recovery codes** (your password and a current code), which makes the old ones stop working.

Wrong codes count towards the same lockout as wrong passwords: after five failures in a row the account is locked for 15 minutes (`Aethera__Auth__MaxFailedLogins`, `Aethera__Auth__LockoutDuration`). Sign-in attempts are also limited per IP address.

## Turn it off

**Settings > Account > Turn off** needs your password and a current code (or a recovery code). The secret and the recovery codes are deleted.

## Require it for everyone (owners)

**Settings > Members > Require two-factor authentication.** You need two-factor authentication on your own account first.

When it is on, a member without an authenticator can still sign in with their password, but lands on a setup screen and can do nothing else until they have enrolled one. This applies at once to sessions that are already open. The members list shows who has two-factor authentication (`2fa` / `no 2fa`). API tokens are not affected, so CI keeps deploying.

## Locked out

Someone who lost both the phone and the recovery codes can be reset on the server by whoever runs it:

```bash
sudo aethera reset-2fa <email>
```

It turns two-factor authentication off for that account, deletes its recovery codes, clears the lockout and signs it out everywhere. The password stays the same (use `aethera reset-password` too if that is also lost). The reset is recorded in the audit log as done by the system. If the organization requires two-factor authentication, the person sets up a new authenticator at the next sign-in.

## Authenticator name

Apps show the account as `Aethera: you@example.com`. To tell several installations apart, set `Aethera__Auth__TwoFactorIssuer` in `/opt/aethera/.env` (for example `Aethera (prod)`) and restart. It changes only how new enrolments are labelled.

## Audit log

Enrolment, turning it on and off, every recovery code used, new recovery codes, failed codes, the organization setting and host resets are all in **Settings > Audit log** (`auth.two_factor.*`, `organization.two_factor_policy_changed`, `users.two_factor_reset`). Codes and secrets are never logged.
