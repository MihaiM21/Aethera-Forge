/**
 * "Install via SSH" adapter (WP2.3 is adding the endpoints).
 *
 * The OpenAPI document does not contain the SSH onboarding endpoints yet, so
 * this module deliberately declares NO HTTP shapes. It defines the small
 * interface the wizard needs; the default implementation refuses to run.
 *
 * TODO(WP2.3): once the endpoints land in openapi/aethera.v1.json and
 * `pnpm gen:api` has been re-run, implement `SshInstallAdapter` on top of the
 * generated types (add-server-by-SSH, host key probe/confirm, install agent job)
 * and replace `unwiredSshAdapter` below. Then flip `NEXT_PUBLIC_AETHERA_SSH_INSTALL=1`
 * (or drop the flag). Until then the wizard hides the method behind the flag and
 * the tests drive it with a mock adapter.
 */

/** Feature flag, compiled into the bundle. Off by default. */
export const SSH_INSTALL_ENABLED = process.env.NEXT_PUBLIC_AETHERA_SSH_INSTALL === "1";

export type SshHostKey = {
  /** `ssh-ed25519`, `ecdsa-sha2-nistp256`, ... */
  algorithm: string;
  /** `SHA256:...` as printed by `ssh-keygen -lf`. */
  fingerprintSha256: string;
};

export type SshTarget = {
  /** Server created by `POST /servers` with transport `ssh`. */
  serverId: string;
  host: string;
  port: number;
  user: string;
};

export type SshInstallRequest = SshTarget & {
  /** The fingerprint the user confirmed; the adapter must pin exactly this key. */
  confirmedFingerprintSha256: string;
};

export interface SshInstallAdapter {
  /** Reads the host key the machine presents (trust-on-first-use; the user confirms it). */
  probeHostKey(target: SshTarget, signal?: AbortSignal): Promise<SshHostKey>;
  /** Starts the agent installation over SSH and returns the job to follow. */
  install(request: SshInstallRequest, signal?: AbortSignal): Promise<{ jobId: string }>;
}

export class SshNotAvailableError extends Error {
  constructor() {
    super("Install via SSH is not available in this build yet.");
    this.name = "SshNotAvailableError";
  }
}

export const unwiredSshAdapter: SshInstallAdapter = {
  async probeHostKey() {
    throw new SshNotAvailableError();
  },
  async install() {
    throw new SshNotAvailableError();
  },
};

/** The adapter the app uses. Replace with the generated-client implementation when WP2.3 lands. */
export const sshAdapter: SshInstallAdapter = unwiredSshAdapter;
