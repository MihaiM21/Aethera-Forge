# ADR 0013: Two-factor authentication and deploying a pinned image tag

- Status: accepted
- Date: 2026-11-27
- Scope: TOTP two-factor authentication for UI sign-ins, the organization requirement, the host reset; `imageTag` on `POST /applications/{id}/deployments`
- Related: [ADR 0003](./0003-api-conventions.md) (sessions, tokens, errors), [ADR 0004](./0004-jobs-and-deployments.md) (deployments, snapshots), [ADR 0006](./0006-trust-model.md) (trust model), [ADR 0007](./0007-build-and-deploy.md) (deploy engine)
- Guides: [Two-factor authentication](../two-factor.md), [Deploying from CI](../ci-deploys.md)

## Context

The first production install puts the control plane's UI on the public internet behind a password only. That is an accepted risk until Aethera has a second factor, so two-factor authentication became the next priority. In the same move, CI builds images in GitHub Actions, pushes them to GHCR and calls the deploy API on merge. The API could not name a tag, so CI had to deploy a moving `:master` tag: two merges close together could deploy the same image twice, and a rollback re-ran "whatever `:master` was then", which the server may no longer have.

## Part 1: two-factor authentication

### Decision summary

1. **TOTP only** (RFC 6238: HMAC-SHA1, 6 digits, 30-second steps, one step of drift either side). Every authenticator app supports it; no SMS, no email, no WebAuthn yet.
2. **The secret is encrypted with the master key**, by the same AES-256-GCM envelope as secrets (`ISecretProtector`), with associated data `user-2fa:{userId}`. It leaves the database decrypted only inside `TwoFactorService` and once in the enrolment answer.
3. **Sign-in has two steps tied by a stateless challenge.** `POST /auth/login` with the right password answers `401 auth.two_factor_required` with a `challenge` and `expiresIn`, and no session. `POST /auth/login/two-factor` takes the challenge and a code or a recovery code and creates the session.
4. **Codes are rate limited three ways**: the per-IP limiter of the login endpoint, the account lockout (a wrong code counts like a wrong password, and only a correct code clears the counter), and replay protection (a code's time step must be later than the last accepted one). The lockout is re-read, the code checked and a failure counted in one transaction under the per-user advisory lock, so parallel guesses cannot get more tries than the lockout allows.
5. **Ten one-time recovery codes**, 80 random bits each, stored as SHA-256.
6. **The owner can require two-factor authentication** for the organization. A member without it gets a session that can only enrol an authenticator.
7. **API tokens are not affected**: they cannot enrol or turn two-factor authentication off, and the requirement does not apply to them.
8. **`aethera reset-2fa <email>`** on the host is the way back in.

### Data (migration `AddTwoFactorAndImageTag`)

| Table / column | What |
|---|---|
| `user_two_factors` | One row per user (key `user_id`, cascade on user delete): `secret_ciphertext`, `secret_nonce`, `wrapped_data_key`, `wrapped_data_key_nonce`, `master_key_version`; `confirmed_at` (null while an enrolment is pending); `last_used_step` (replay protection). |
| `user_recovery_codes` | `id`, `user_id`, `code_hash` (32 bytes, check constraint), `used_at`, `created_at`; unique `(user_id, code_hash)`. |
| `organizations.require_two_factor` | `bool`, default false. |

A master key rotation keeps old rows readable as long as the old key version stays in the keyring, as for secrets.

### The challenge

`base64url(payload) "." base64url(HMAC-SHA256(key, "v1." payload))`, payload = user id, expiry (5 minutes), "remember me", master key version, and the first 8 bytes of `SHA-256("aethera:2fa-challenge:" + password hash)`. The key is `HKDF-SHA256(master key, info "aethera/2fa-challenge/v1")`.

- Stateless: no table, nothing to clean up, works with several API instances.
- A password change or reset kills outstanding challenges (the stamp no longer matches).
- It is not a credential: it proves only that the password was right a few minutes ago. A challenge can be replayed within its 5 minutes, but each try still needs a fresh, correct code and counts towards the lockout.
- Alternatives: a `user_sessions` row in a "half signed-in" state would have touched every session lookup; a table of pending challenges would give per-challenge attempt counts, which the account lockout already covers.

The password step does **not** clear the failure counters for an account with two-factor authentication (it would let a known password reset the lockout between code guesses). A password hash upgrade is still stored.

### Endpoints

| Endpoint | Who | What |
|---|---|---|
| `POST /auth/login/two-factor` | anonymous, rate limited | Second step; `{challenge, code}` or `{challenge, recoveryCode}`. 401 `auth.two_factor_invalid_code` / `auth.two_factor_challenge_invalid`, 423 `auth.locked_out`. |
| `GET /auth/two-factor` | any caller | `enabled`, `enabledAt`, `enrolmentPending`, `recoveryCodesRemaining`, `requiredByOrganization`. |
| `POST /auth/two-factor/enrolment` | session, password | New secret and `otpauth://` URI, stored as pending. 409 `auth.two_factor_already_enabled` while on: turn it off first, so a stolen session cannot swap the authenticator. |
| `POST /auth/two-factor/enrolment/confirm` | session | A code from the new secret turns it on, returns the recovery codes and signs out the user's other sessions. |
| `POST /auth/two-factor/disable` | session, password + code or recovery code | Deletes the secret and codes. With only a pending enrolment, the password cancels it. |
| `POST /auth/two-factor/recovery-codes` | session, password + code | Replaces the recovery codes. |
| `PUT /auth/two-factor/policy` | Owner (`admin` scope for a token) | `{required}`. Turning it on needs two-factor authentication on the caller's own account (409 `auth.two_factor_not_enabled`). |

`GET /auth/me` gains `twoFactorEnabled` and `twoFactorEnrolmentRequired`; `UserResponse` gains `twoFactorEnabled` (the members list shows who has it).

Settings operations need the current password: a stolen session must not be able to turn two-factor authentication off or enrol its own authenticator. Wrong passwords and codes count towards the lockout. The QR code is drawn in the browser (`qrcode` package) from the URI; the secret is never sent to a third party.

### The organization requirement

`SessionStore.ResolveAsync` already joins the user and membership on every request; it now also reads `organizations.require_two_factor` and whether a confirmed authenticator exists. When the organization requires it and the user has none, the session handler adds the claim `aethera:2fa_enrolment_required`. Two layers then keep that session to enrolment:

- `RoleScopeAuthorizationHandler` fails role requirements with the reason `two_factor_enrolment_required` unless the endpoint carries `AllowDuringTwoFactorEnrolment`. This covers SignalR hubs, which have no endpoint filters.
- `TwoFactorEnrolmentFilter` on the `/api/v1` group catches endpoints without a role requirement.

Both answer `403 auth.two_factor_enrolment_required`. Allowed: `GET /auth/me`, `GET /auth/csrf`, `POST /auth/logout`, `GET /auth/two-factor`, enrolment start and confirm, disable. API tokens never get the claim. The rule is evaluated per request, so turning the requirement on restricts the next request of an existing session, and confirming an enrolment lifts it at once. A live-update (SignalR) connection opened before the change stays open until it reconnects, as with a revoked session. The UI sends such a session to `/two-factor`, which runs the enrolment outside the app shell.

### Audit

`auth.two_factor.enrolment_started`, `.enabled`, `.disabled`, `.enrolment_cancelled`, `.recovery_code_used` (with `remaining`), `.recovery_codes_regenerated`, `.code_failed`, `.password_failed`; `auth.login.two_factor_challenged`; `auth.login.succeeded` with `twoFactor: totp | recovery_code`; `auth.login.failed` with reason `invalid_two_factor_code`; `organization.two_factor_policy_changed`; `users.two_factor_reset` (system actor). No code, secret or recovery code is ever in metadata.

### Host reset

`aethera reset-2fa <email>` runs `dotnet Aethera.Api.dll reset-2fa <email>` in the control-plane container, like `reset-password`: it deletes the secret and the recovery codes, clears the lockout, revokes every session and records `users.two_factor_reset`. The password is unchanged. Whoever can run it can already read `.env` and the database, so it is not an API endpoint.

### Known limits

- No WebAuthn / passkeys, no "remember this browser", no admin endpoint to reset another member's 2FA (the host command does it).
- The challenge is replayable within its lifetime (see above).
- Wrong codes while confirming an enrolment count towards the lockout too (a typo costs one of the five tries).
- An owner who turns their own two-factor authentication off while the organization requires it is restricted to enrolment like anyone else, and must enrol again before changing the policy.

## Part 2: deploying a pinned image tag

### Decision

`DeployRequest` gets `imageTag` (and `reason` is now recorded in the audit event, cut at 500 characters). For an application whose source is a Docker image:

- Accepted: a tag (`3f2a9c1`, Docker's grammar), a digest (`sha256:<64 hex>`, stored as `@sha256:...`), or a full reference of the application's own repository (`ghcr.io/owner/app:3f2a9c1`; `nginx`, `library/nginx` and `docker.io/library/nginx` are the same repository). Parsed by `ImageTagRequest` in the domain.
- Refused with 422 on `/imageTag`: `image_tag.invalid`, `image_tag.repository_mismatch`, and `image_tag.not_image_source` for git and compose applications and services. A different repository is a configuration change (role and `write` scope), not a deploy: a `deploy`-scoped CI token must not be able to run an arbitrary image.
- The tag is stored on the deployment (`deployments.image_tag`, returned as `imageTag`) and applied when the snapshot is frozen (`DeploymentSnapshotFactory.ApplyImageTag`), like a webhook's pushed commit. The frozen snapshot and `imageRef` carry it, so a **rollback** to that deployment runs exactly that image; the rollback row repeats the tag.
- The application's configured tag does not change. A deploy or redeploy without `imageTag` runs the configured tag again; moving the application to another server keeps the pinned tag of the live deployment.
- The API token needs `deploy` to queue; polling `GET /deployments/{id}` needs `read` as well (`deploy` does not imply `read`).

### Alternatives

- Changing the application's configured tag on each pinned deploy: a deploy-scoped token would then edit configuration, and the "Redeploy" button would quietly follow CI. Rejected for now; the configured tag stays the default for manual deploys.
- Carrying the tag only in the job payload: invisible in the deployment until the job starts, and lost for the rollback row. A column is cheap.
