"use client";

/** Server > Network: this server's mesh address, key, endpoint and peers with their last handshake (R2.2, ADR 0012). */
export function ServerNetworkTab({ serverId }: { serverId: string }) {
  return (
    <p className="text-sm text-muted-foreground" data-server-id={serverId}>
      The private network between servers is coming in this release.
    </p>
  );
}
