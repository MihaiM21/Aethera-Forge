# The private network between servers

Aethera can connect your servers with an encrypted private network that it sets up itself, so a database never needs a public port: an application on one server reaches a database on another at a private address, and the internet cannot. It is **off by default**; a one-server install never needs it. Design: [ADR 0012](architecture/0012-multi-server-mesh-previews.md).

## How it works

- **WireGuard, managed by the agent.** Each agent creates one interface called `aethera0` and keeps its own key pair in its state directory (`/var/lib/aethera/mesh.key`, mode 0600). The private key never leaves the server: only the public key is sent to the control plane.
- **One address per server** from the range you choose (default `10.210.0.0/16`). An address is allocated once and kept; a server keeps it until it is deleted.
- **Full mesh.** Every server is a peer of every other server of the organization. The control plane sends each connected agent its whole configuration (address, port and all peers: public key, endpoint, one `/32`, keepalive 25 s) and the agent applies it exactly: a peer that is not listed is removed. When a new key appears the round is repeated, so everybody learns it.
- **What the agent touches.** One interface named `aethera0`. No firewall rule, no other interface, no route outside the mesh range (the kernel adds the connected route of the range with the address). The agent refuses other interface names, ranges that are not private IPv4 `/16` to `/24`, addresses outside the range, and allowed IPs wider than one address per peer. It also refuses a range that overlaps an address another interface already has.
- **Tailscale option.** A server that is already on a tailnet uses the IPv4 address of `tailscale0` as its address. Aethera never logs in to Tailscale or changes it: install and connect Tailscale yourself.

## Turn it on

1. **Every server needs** a Linux kernel with WireGuard (any distribution kernel since 5.6, or the `wireguard` package on older ones) and an agent that may create the interface (`CAP_NET_ADMIN`). New installs have both. **Servers installed before this release:** run the agent installer again on each of them (`install-agent.sh`), which rewrites the systemd unit with the capability, and update the agent (Servers > Update agent). The control plane's own machine runs the agent container from `docker-compose.prod.yml`, which now has `cap_add: NET_ADMIN`; run `aethera update` or `docker compose up -d` so it is recreated.
2. **Open UDP 51820 between the servers** (security group or firewall): each server must accept UDP on the mesh port from the public addresses of the others. Nothing else is needed, and nothing needs to be open to the internet in general. Example for `ufw`: `ufw allow from <other server ip> to any port 51820 proto udp`.
3. In **Settings > Network** choose *WireGuard*, check the range (pick one that does not overlap your hosting provider's private network or Docker's bridges) and the port, and save. Aethera allocates the addresses and applies the configuration; each server shows **up** when every peer had a handshake in the last three minutes.
4. Each server's **Network** tab shows its address, public key, endpoint and peers with their last handshake and traffic.

The control plane's own machine takes part like any other. It is usually reached by name `localhost`, which no other server can dial, so it has no endpoint: the other servers wait for it to dial in. That works as long as the other servers have a public IP or an endpoint set, which they have in the usual setup (a public VPS next to a machine behind NAT).

### Servers behind NAT, other ports

Peers dial a server at `<public IP>:<port>` (the host name when no public IP is set). Change that on the server's Network tab (**Reachability**): an endpoint such as `vpn.example.com:443` and/or another UDP port, for example when a router forwards a different port. At least one side of every pair must be reachable; keepalives keep the path open for the other side.

## Private ports

A published port of an application or service (Networking tab of an application, Settings of a service) can listen on:

| Listens on | Meaning |
|---|---|
| **Public** (default) | every interface of the server, the internet included. The only behaviour before this release. |
| **Private network** | the server's private address only: reachable from your other servers, never from the internet. |
| **This server only** | `127.0.0.1`. |

A database on server A published on the private network is used from an application on server B at `<A's private address>:<published port>`. The service page shows that address in its connection info. A private port needs a published port; a deployment with a private port on a server that has no private address fails at the Network step instead of listening publicly. The scope applies on the next deployment.

## Turning it off, switching and changing the range

- **Off** removes the `aethera0` interface from every connected server and clears the status. Addresses and keys stay, so turning it on again changes nothing. A server that is offline is cleaned up when its agent connects. Workloads with private ports keep their configuration but cannot be redeployed until the network is back (or their port is made public).
- **WireGuard to Tailscale** removes the interfaces and uses the tailnet addresses; **a new range** gives every server a new address. In both cases redeploy the workloads that have private ports and update the clients that use the old addresses.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Status **failed**: "cannot run the private network" | The agent lacks the capability or the kernel has no WireGuard. `journalctl -u aethera-agent` shows `mesh.wireguard is not available` with the reason. Install WireGuard, reinstall the agent so the unit has `AmbientCapabilities=CAP_NET_ADMIN`, restart it. In a container the agent needs `network_mode: host` and `cap_add: NET_ADMIN`. |
| Status **degraded**: no handshake | UDP 51820 is blocked between the servers, or neither side can reach the other: check the firewall and the endpoint on the Network tab. `wg show aethera0` on the server shows the peers and the last handshake. |
| "The mesh range overlaps ..." | Another interface (a LAN, a Docker network) uses part of the range. Choose another one in Settings > Network. |
| Status **failed**: Tailscale | `tailscale0` does not exist or has no IPv4 address: install and connect Tailscale on that server. |
| A server stays **waiting** | Its agent is offline; the network is applied when it connects. |
| Deployment fails with `network.no_mesh_address` | The server has no private address yet: turn the network on and wait until it shows one, or publish the port publicly. |
| Ping works but the database does not answer | The port is published on the **server's** private address: connect to `<private address>:<published port>`, not to the container's port. |

The agent keeps the private key even when the network is turned off. To give a server a new identity, delete `/var/lib/aethera/mesh.key` and apply the network again.
