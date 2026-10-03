# Aethera

A self-hosted deployment and infrastructure management platform built around real-world developer needs.

Aethera is intended to replace platforms such as Coolify for personal and production infrastructure while remaining useful as a general-purpose open-source project.

The project originally comes from the needs of the Turn One infrastructure, but Aethera is designed as an independent platform rather than a Turn One-specific product. The main purpose for this app is to move away from coolify app and use this, but it will be open source.

---

## 1. Project Vision

Aethera should make deploying and managing applications on your own servers feel as simple as using a managed deployment platform, while keeping the infrastructure under the user's control. By simple i don't mean with less features/capabilities than Coolify.

The core idea is:

- connect a Git repository, Docker image, Dockerfile, Compose file, or static project
- define how the application should be built
- define where it should run
- configure domains, environment variables, secrets, storage, networking, and resources
- deploy it
- monitor it
- update it
- roll it back
- manage the underlying server
- multiple servers with a master one where the control panel is hosted
- ability to assign to servers role (1. CI/CD pipeline runs, 2. builds, 3. dbs/stoarage/s3, 4. Main server/Master, etc)
- resources (cpu,ram) management and priotization, structure, allocation (per server and per multiple ones)
- secure connection, vpn integration

The platform should work particularly well for:

- personal servers
- VPS infrastructure
- small teams
- self-hosted applications
- APIs
- websites
- static sites
- Docker workloads
- development environments
- staging environments
- production environments
- small distributed infrastructures

Aethera should not attempt to replace Docker, Linux, Git, or the underlying infrastructure. It should act as a control plane that makes those technologies easier to operate.

---

The top-level container for Aethera resources.

An organization can contain:

- projects
- users
- teams
- servers
- registries
- domains
- deployment configurations
- secrets

For a single-user installation, the organization can simply represent the local Aethera installation.

---

## 2.2 Project

A project groups related resources.

Example:

```text
Turn One
├── Website
├── API
├── API Dev
└── Documentation
```

Projects should make it easy to understand which applications and infrastructure belong together.

Users can choose from project template (Storage template(db/static storage/s3), API template, etc)

---

## 2.3 Application

An application is a deployable workload.

Examples:

- Next.js website
- .NET API
- Python FastAPI service
- Node.js backend
- static website
- Docker container
- application built using Nixpacks
- application built using a Dockerfile

An application contains its source, build configuration, runtime configuration, networking configuration, environment variables, and deployment history.

---

## 2.4 Service

A service represents infrastructure that may not come directly from a Git repository.

Examples:

- PostgreSQL
- Redis
- MySQL
- MongoDB
- Grafana
- Prometheus
- InfluxDB
- MinIO
- other Docker-based services

Services should be manageable through the same interface as applications where practical.

---

## 2.5 Server

A server is a machine managed by Aethera.

Examples:

- Hetzner VPS
- dedicated server
- home server
- Raspberry Pi
- cloud VM
- development machine

A server should expose:

- CPU usage
- memory usage
- disk usage
- network usage
- running containers
- deployment activity
- health status
- system information

---

## 2.6 Deployment

A deployment is a specific attempt to build and run an application or service.

Every deployment should have:

- unique ID
- application
- target server
- source revision
- build configuration
- deployment status
- timestamps
- logs
- resulting image/container information
- failure reason when applicable

Deployment history should remain available for troubleshooting and rollback.

---

# 3. Supported Deployment Sources

Aethera should support several deployment methods.

## 3.1 Git Repository

Applications can be deployed from Git repositories.

Initial support should include:

- GitHub
- GitLab
- generic Git repositories

The user should be able to configure:

- repository URL
- branch
- commit
- private repository credentials
- automatic deployment
- deployment trigger

Future integrations can support additional Git providers.

---

## 3.2 Dockerfile

A project containing a Dockerfile can be built directly.

Example:

```text
repository
├── Dockerfile
├── source
└── configuration
```

Aethera should:

1. clone the repository
2. locate the Dockerfile
3. build the image
4. tag the image
5. store or push the image if required
6. deploy the image
7. expose the application through the configured network/domain

---

## 3.3 Docker Image

Users should be able to deploy an existing Docker image without building it.

Example:

```text
nginx:latest
```

or:

```text
registry.example.com/my-api:1.4.2
```

Configuration should support:

- registry
- image
- tag
- pull policy
- environment variables
- ports
- volumes
- domains
- resource limits

---

## 3.4 Docker Compose

Aethera should support Docker Compose projects.

Users should be able to deploy an existing Compose file rather than translating it manually into Aethera-specific configuration.

Compose deployments should support:

- multiple containers
- networks
- volumes
- environment variables
- secrets
- dependencies
- health checks
- exposed ports
- restart policies

Aethera should provide deployment and lifecycle management around the Compose project without unnecessarily changing the user's Compose configuration.

---

## 3.5 Static Websites

Aethera should support static sites as a first-class deployment type.

Examples:

- HTML/CSS/JavaScript
- Vite
- React static build
- documentation sites
- generated websites

The platform should be able to:

1. obtain the source
2. install dependencies
3. run the configured build command
4. identify the generated output directory
5. serve the resulting files
6. configure a domain and HTTPS

---

## 3.6 Nixpacks

Aethera should support Nixpacks-style automatic application detection and builds.

The platform should detect common application types and generate the appropriate build process.

Examples:

- Python
- Node.js
- Go
- PHP
- Ruby
- Rust
- other supported ecosystems

The user should be able to override automatic detection when necessary.

---

## 3.7 Railpack or Alternative Build Engines (not for the first mvp)

The architecture should not hard-code one build system.

Aethera should have a build-engine abstraction so additional builders can be supported later.

Possible builders:

- Dockerfile
- Nixpacks
- Railpack
- custom build script
- prebuilt Docker image
- static build

The UI should present these as deployment/build strategies rather than exposing unnecessary implementation details.

---

# 4. Build System

Aethera should have a dedicated build subsystem.

## 4.1 Build Configuration

A build can define:

- build engine
- build context
- Dockerfile path
- build command
- install command
- start command
- output directory
- build arguments
- environment variables
- cache settings
- target architecture

---

## 4.2 Build Detection

Aethera should automatically inspect a repository and identify likely build methods.

Examples:

```text
Dockerfile
package.json
requirements.txt
pyproject.toml
go.mod
Cargo.toml
composer.json
```

The user should be able to accept the detected configuration or manually override it.

---

## 4.3 Build Logs

Build logs should be available in real time.

Users should be able to:

- view logs
- search logs
- download logs
- inspect failed builds
- see build duration
- identify the source commit

---

## 4.4 Build Cache

Aethera should support build caching where the underlying build engine allows it.

The goal is to avoid rebuilding unchanged dependencies and layers.

---

# 5. Deployment Engine

The deployment engine is the core of Aethera.

A deployment should follow a predictable lifecycle:

```text
Source
→ Build
→ Image
→ Target Server
→ Container
→ Network
→ Domain
→ Health Check
→ Running
```

The engine should handle:

- stopping old versions
- starting new versions
- health checks
- deployment failures
- cleanup
- rollback
- restart policies
- deployment state

---

## 5.1 Zero or Low-Downtime Deployments

Where possible, Aethera should support deployment strategies that minimize downtime.

A basic strategy can:

1. build the new image
2. start the new container
3. verify health
4. switch traffic
5. stop the previous version

The exact strategy should depend on the application and networking configuration.

---

## 5.2 Rollbacks

Every successful deployment should create a rollback point.

The user should be able to select an earlier deployment and restore it.

Example:

```text
Deployment #104
Deployment #103
Deployment #102
Deployment #101
```

Selecting `#102` should allow the application to return to the corresponding image/configuration.

---

## 5.3 Redeploy

Users should be able to redeploy an existing version without creating a new source revision.

Useful for:

- configuration changes
- server issues
- temporary failures
- manually restarting an application

---

## 5.4 Restart

Applications should be restartable without a new build.

---

## 5.5 Stop and Start

Users should be able to stop and start workloads manually.

---

# 6. Automatic Deployments

Aethera should support Git-based automatic deployments.

Typical flow:

```text
Git push
→ Webhook
→ Aethera
→ Build
→ Deploy
```

The user should be able to configure:

- enabled/disabled
- branch
- deployment conditions
- build strategy
- target server

---

## 6.1 Webhooks

Aethera should expose webhook endpoints for external Git providers and automation systems.

Webhook processing should:

- validate the request
- identify the application
- identify the source revision
- start the deployment
- report deployment status

---

## 6.2 Manual Deployment

Automatic deployment should never be required.

Users should always be able to manually deploy a selected commit or image.

---

# 7. Domains and Networking

Aethera should make application networking simple.

Users should be able to assign domains such as:

```text
example.com
api.example.com
docs.example.com
```

---

## 7.1 Reverse Proxy

Aethera should integrate with a reverse proxy to route incoming traffic to containers.

The architecture should allow support for technologies such as:

- Traefik
- Caddy
- Nginx
- another compatible proxy

The proxy should be managed by Aethera where practical.

---

## 7.2 HTTPS

Aethera should support automatic TLS certificate provisioning.

The goal should be:

```text
Domain
→ DNS
→ Aethera
→ Automatic certificate
→ HTTPS
→ Application
```

Users should not have to manually configure certificates for normal deployments.

---

## 7.3 Ports

Applications should be able to declare:

- internal ports
- external ports
- TCP/UDP
- HTTP/HTTPS where relevant

Aethera should distinguish between:

- container ports
- server ports
- public ports
- reverse-proxy routes

---

## 7.4 Networks

Applications and services should be able to communicate through managed Docker networks.

Example:

```text
API
  ↓
internal network
  ↓
PostgreSQL
```

The database does not need to be publicly exposed.

---

# 8. Environment Variables

Each application should have environment variables.

Examples:

```text
DATABASE_URL
REDIS_URL
API_KEY
NODE_ENV
PORT
```

The UI should allow:

- adding variables
- editing variables
- deleting variables
- importing variables
- exporting non-secret configuration where appropriate
- environment-specific configuration

---

## 8.1 Environments

Aethera should support separate environments such as:

- development
- staging
- production

An application should be deployable to different servers with different environment variables.

Example:

```text
API
├── Development
├── Staging
└── Production
```

---

# 9. Secrets

Secrets should be treated differently from normal environment variables.

Aethera should provide:

- encrypted secret storage
- restricted access
- masked values in the UI
- secure injection into deployments
- secret rotation

Secrets should not be written to ordinary logs.

---

# 10. Persistent Storage

Aethera should support persistent Docker volumes.

Users should be able to configure:

- volume name
- mount path
- host path when appropriate
- backup configuration
- application association

Example:

```text
PostgreSQL
→ /var/lib/postgresql/data
→ persistent volume
```

Container replacement should not automatically destroy persistent data.

---

# 11. Databases

Databases should be supported as managed services.

Initial templates could include:

- PostgreSQL
- MySQL
- MariaDB
- Redis
- MongoDB

Aethera should provide templates rather than forcing users to manually write Docker configurations for common services.

Each database service should support:

- version selection
- persistent storage
- credentials
- internal networking
- health checks
- restart policies
- basic resource configuration

---

# 12. Server Management

Aethera should manage multiple servers from one interface.

Example:

```text
Servers

Hetzner Production
Home Lab
Development VPS
```

Each server should show:

- online/offline state
- CPU
- RAM
- disk
- network
- Docker status
- running containers
- deployed applications
- last heartbeat
- operating system
- architecture

---

# 13. Server Agent

Aethera should use an agent for remote server management.

The agent is responsible for executing operations requested by the Aethera control plane.

Possible responsibilities:

- Docker operations
- image management
- container management
- Compose operations
- filesystem operations
- deployment execution
- log streaming
- system metrics
- health checks
- cleanup
- server heartbeat

The agent should communicate securely with the control plane.

---

# 14. Control Plane

The Aethera control plane is the central management layer.

It should contain:

- API
- authentication
- authorization
- deployment orchestration
- project management
- application management
- server management
- secret management
- deployment history
- job management
- monitoring data

The control plane should not need to directly run every workload.

---

# 15. Job System

Long-running operations should be handled through a job system.

Examples:

- build application
- deploy application
- restart service
- backup database
- prune Docker images
- provision domain
- obtain TLS certificate

Jobs should have:

- ID
- type
- status
- start time
- finish time
- logs
- error information
- associated resource

Possible states:

```text
Queued
Running
Succeeded
Failed
Cancelled
```

---

# 16. Deployment Queue

Aethera should have a controlled deployment queue.

This prevents multiple deployments of the same application from interfering with each other.

The system should support:

- queued deployments
- concurrent deployments where safe
- per-application locking
- cancellation
- retries
- priority in future versions

---

# 17. Logs

Logs are a first-class feature.

Aethera should expose:

- build logs
- deployment logs
- application logs
- agent logs
- system logs

Users should be able to:

- stream logs live
- search logs
- filter logs
- inspect timestamps
- download logs

---

# 18. Monitoring

Aethera should provide basic infrastructure monitoring.

## Server metrics

- CPU
- RAM
- disk
- network
- load
- uptime

## Container metrics

- CPU
- RAM
- network
- restart count
- status

## Application health

- running/stopped
- healthy/unhealthy
- response status
- last health check
- uptime

---

# 19. Health Checks

Applications should support configurable health checks.

Examples:

```text
GET /health
GET /api/health
TCP port check
Container health check
```

Configuration should include:

- interval
- timeout
- retries
- startup grace period

Health checks should be usable by the deployment engine to determine whether a new deployment is ready.

---

# 20. Alerts

Aethera should eventually support alerts for:

- application down
- server offline
- high CPU
- high memory
- disk nearly full
- failed deployment
- failed health check
- certificate expiration
- backup failure

Notification channels could include:

- email
- Discord
- Slack
- webhooks
- other integrations

---

# 21. Resource Management

Users should be able to configure container resource limits.

Possible settings:

- CPU limit
- CPU reservation
- memory limit
- memory reservation
- process limits where supported

This is especially useful on smaller VPS machines.

---

# 22. Docker Management

Aethera should provide useful Docker management without trying to become a full Docker GUI.

Users should be able to inspect:

- containers
- images
- volumes
- networks

Supported operations should include:

- start
- stop
- restart
- remove
- inspect
- pull
- prune

Dangerous destructive operations should require confirmation.

---

# 23. Image Management

Aethera should manage application images.

Each image should have:

- repository
- tag
- digest
- creation time
- size
- associated deployment

The platform should support image cleanup policies to prevent disks from filling with old builds.

---

# 24. Container Registry

Aethera should support external registries.

Examples:

- Docker Hub
- GitHub Container Registry
- GitLab Container Registry
- private registries

Future versions could include an integrated private registry.

---

# 25. Configuration as Code

Aethera should support declarative configuration.

Example:

```yaml
project: turnone

applications:

  api:
    source:
      type: git
      repository: https://github.com/example/api
      branch: main

    build:
      type: dockerfile
      path: Dockerfile

    server: production

    domains:
      - api.example.com

    environment:
      NODE_ENV: production

services:

  postgres:
    type: postgres
    version: "17"
```

The exact syntax can evolve.

The important concept is that infrastructure configuration should be reproducible and version controlled.

---

# 26. Aethera CLI

A CLI should eventually provide full access to the platform.

Example commands:

```bash
aethera login
aethera projects list
aethera apps list
aethera deploy api
aethera logs api
aethera restart api
aethera rollback api
aethera servers list
```

The CLI should communicate with the same API used by the web interface.

---

# 27. Aethera API

The API should expose the platform functionality programmatically.

Possible API resources:

```text
/auth
/users
/organizations
/projects
/applications
/services
/servers
/deployments
/builds
/domains
/secrets
/volumes
/registries
/monitoring
/jobs
/webhooks
```

The API should support:

- authentication
- authorization
- CRUD operations
- deployment actions
- log streaming
- webhooks
- automation

The API should be documented using OpenAPI.

---

# 28. Authentication

Aethera should support secure authentication.

Initial options:

- email/password
- session authentication
- API tokens

Future options:

- OAuth
- GitHub login
- GitLab login
- OIDC
- SSO

Passwords must never be stored in plaintext.

---

# 29. Authorization

The platform should support permissions.

Possible roles:

- Owner
- Administrator
- Developer
- Viewer

Permissions should eventually cover:

- applications
- deployments
- servers
- secrets
- projects
- users
- infrastructure

A single-user installation should remain simple and should not force users through enterprise permission management.

---

# 30. API Tokens

Users should be able to create API tokens for:

- CI/CD
- CLI
- automation
- external applications

Tokens should support:

- name
- creation date
- expiration
- scopes
- revocation
- last-used information

Tokens should be displayed only when created.

---

# 31. Security

Security should be a core design requirement.

Aethera must carefully control access to:

- Docker
- the host filesystem
- environment variables
- secrets
- server credentials
- deployment operations

Important principles:

- least privilege
- encrypted communication
- secure secret storage
- authenticated agents
- scoped API tokens
- audit logging
- no secrets in normal logs
- explicit confirmation for destructive actions

The Aethera agent should not expose an unrestricted shell interface to the public internet.

---

# 32. Audit Log

Aethera should record important actions.

Examples:

```text
User deployed API
User changed environment variable
User added server
User deleted deployment
User created API token
```

Each audit entry should include:

- user
- action
- resource
- timestamp
- relevant metadata

---

# 33. Backups

Aethera should eventually support backups for persistent data.

Possible targets:

- local storage
- S3-compatible storage
- Backblaze B2
- other object storage

Backup functionality should support:

- schedules
- retention
- manual backups
- restore
- backup status
- failure notifications

Database-specific backup strategies should be supported where possible.

---

# 34. Maintenance

Aethera should help manage infrastructure maintenance.

Possible operations:

- Docker image cleanup
- unused volume detection
- unused network cleanup
- old deployment cleanup
- build cache cleanup
- log cleanup
- backup cleanup

Maintenance should be configurable and preferably schedulable.

---

# 35. Scheduled Jobs

Aethera should eventually support scheduled tasks.

Examples:

- database backup every night
- cleanup every Sunday
- application restart every month
- custom HTTP request
- custom container command

A scheduler should expose:

- schedule
- target
- command/action
- execution history
- logs
- failure state

---

# 36. Notifications

The notification system should be provider-based.

Aethera should be able to notify users about:

- successful deployments
- failed deployments
- application downtime
- server downtime
- backups
- certificate problems
- resource thresholds

The system should support generic webhooks so additional notification providers do not require changes to the core platform.

---

# 37. UI

The web interface should be modern, simple, and infrastructure-focused.

The main navigation should provide access to:

- Dashboard
- Projects
- Applications
- Services
- Servers
- Deployments
- Domains
- Registries
- Secrets
- Monitoring
- Settings

The UI should prioritize information density without becoming visually complicated. More here ([UI references](01_UI.md).

---

# 38. Dashboard

The dashboard should provide an overview of the entire installation.

It should show:

- server status
- application status
- recent deployments
- failed deployments
- resource usage
- alerts
- active jobs

A user should be able to identify infrastructure problems quickly from the dashboard.

---

# 39. Application Page

Each application should have a dedicated management page.

Possible sections:

- Overview
- Deployments
- Logs
- Environment
- Domains
- Storage
- Networking
- Resources
- Build
- Health
- Settings

The overview should show the current deployment and health state.

---

# 40. Deployment Details

Every deployment should have a detailed page containing:

- status
- commit
- branch
- build method
- server
- timestamps
- duration
- build logs
- deployment logs
- resulting image
- health check result

Failed deployments should explain where the process failed whenever possible.

---

# 41. Server Page

A server page should contain:

- server status
- system information
- CPU usage
- RAM usage
- disk usage
- network usage
- containers
- applications
- services
- recent jobs
- agent status

It should also provide server maintenance actions.

---

# 42. Multi-Server Architecture

Aethera should be designed from the beginning to support multiple servers.

A single-server installation should remain completely valid.

Example:

```text
Aethera Control Plane

Server A
Production

Server B
Development

Server C
Home Lab
```

Applications can be assigned to a specific server.

Future scheduling can allow Aethera to select a suitable server automatically.

---

# 43. Agent Communication

The agent should maintain a secure relationship with the control plane.

The communication layer should support:

- authentication
- heartbeats
- commands
- job updates
- log streaming
- metrics
- deployment status

The system should handle temporary network failures gracefully.

---

# 44. Offline and Failure Handling

Aethera should expect infrastructure failures.

Examples:

- server temporarily unreachable
- Docker stopped
- deployment interrupted
- build failed
- container crashed
- network failure
- disk full

The platform should distinguish between:

```text
Control plane unavailable
Server unavailable
Agent unavailable
Docker unavailable
Application unavailable
```

This distinction is important for troubleshooting.

---

# 45. Development and Production Environments

Aethera itself should support development and production installations.

The platform should be easy to run locally for development.

Possible development stack:

- Aethera API
- Aethera UI
- PostgreSQL
- Redis
- local Docker
- local agent

Production should be deployable using Docker.

---

# 46. Self-Hosting

Aethera should be designed primarily for self-hosting.

A new installation should ideally require only:

- a server
- Docker
- a domain if public access is desired

The installation process should be simple.

Possible installation methods:

```text
Docker Compose
Install script
Prebuilt container images
```

---

# 47. Updates

Aethera should be able to update itself.

The update system should:

1. detect a new Aethera version
2. notify the user
3. pull the new version
4. restart the platform
5. preserve configuration and data

Database migrations must be handled safely.

---

# 48. Configuration

Aethera should keep configuration separate from application data.

Configuration may include:

- database connection
- authentication settings
- server configuration
- proxy settings
- storage
- registry configuration
- notification providers

Sensitive configuration must be protected.

---

# 49. Observability

Aethera should expose its own health and metrics.

Possible endpoints:

```text
/health
/ready
/metrics
```

This allows Aethera to be monitored by systems such as:

- Prometheus
- Grafana
- external monitoring services

Aethera should be able to monitor itself.

---

# 50. Extensibility

The platform should use modular components.

Potential extension points:

- build engines
- deployment strategies
- Git providers
- container registries
- reverse proxies
- notification providers
- backup providers
- authentication providers
- monitoring providers

The goal is to prevent the core application from becoming tightly coupled to one provider.

---

# 51. Templates

Aethera should eventually provide templates for common deployments.

Examples:

- PostgreSQL
- Redis
- MySQL
- Grafana
- Prometheus
- InfluxDB
- MinIO
- WordPress
- static website
- Next.js
- FastAPI
- ASP.NET
- Node.js

Templates should be editable after creation.

---

# 52. Project Templates

A complete project could contain multiple services.

Example:

```text
Web Application
├── Frontend
├── Backend
├── PostgreSQL
└── Redis
```

Aethera should make it possible to deploy the complete project consistently.

---

# 53. Environment Promotion

Future versions should support promotion between environments.

Example:

```text
Development
→ Staging
→ Production
```

The promoted artifact should ideally be the same built image rather than rebuilding different code for every environment.

This improves deployment consistency.

---

# 54. Deployment Strategies

The architecture should allow multiple deployment strategies.

Initial strategy:

```text
Recreate
```

Future strategies:

```text
Rolling
Blue/Green
Canary
```

These should be optional because not every application requires advanced deployment mechanisms.

---

# 55. Resource Discovery

When a server is added, Aethera should inspect it and discover:

- Docker installation
- CPU
- memory
- storage
- architecture
- operating system
- running containers
- Docker networks
- Docker volumes

This reduces manual configuration.

---

# 56. Domain Management

Aethera should maintain a central list of domains.

A domain should be associated with:

- application
- server
- proxy route
- TLS certificate
- configuration

The platform should warn about:

- duplicate domains
- invalid configuration
- missing DNS
- certificate problems

---

# 57. DNS Awareness

Aethera does not necessarily need to become a DNS provider.

Instead, it should verify DNS configuration where possible.

For example:

```text
api.example.com
→ expected server IP
```

If the record does not point to the expected server, the UI should clearly report the mismatch.

---

# 58. Developer Experience

The platform should optimize for a short path from code to running application.

The ideal flow should be:

```text
Create Application
→ Select Repository
→ Select Build Method
→ Configure Environment
→ Select Server
→ Add Domain
→ Deploy
```

Advanced settings should remain available without overwhelming first-time users.

---

# 59. API-First Design

The UI should not contain deployment logic that cannot also be performed through the API.

The architecture should follow:

```text
Web UI
   ↓
Aethera API
   ↓
Control Plane
   ↓
Agent
   ↓
Server
```

The CLI should also use the same API.

This keeps the system consistent and automatable.

---

# 60. Open Source

Aethera should be open source.

The project should be developed publicly with:

- source code
- documentation
- issue tracker
- contribution guidelines
- releases
- changelog
- roadmap

The project should clearly document its license and the licenses of important dependencies.

Aethera should remain independent from Turn One even though Turn One is one of its original use cases.

Suggested positioning:

> Aethera is an open-source self-hosted deployment and infrastructure management platform originally built from the real-world needs of the Turn One infrastructure.

---

# 61. Relationship With Turn One

Turn One should be the first real production environment for Aethera. Aethera is build for Turn One as the main purpose to switch from Coolify and have full control on our own infrastructure.

Aethera can manage infrastructure such as:

- Turn One frontend
- Turn One backend
- T1API production
- T1API development
- documentation
- PostgreSQL
- Redis
- InfluxDB
- Grafana
- Prometheus
- other services

This gives the project a real environment in which to test deployments, monitoring, failures, updates, and recovery.

However, Turn One-specific logic should not be embedded into the core platform.

---

# 62. Suggested Technical Architecture

The exact stack can evolve, but the platform should be separated into clear components.

## Control Plane

Responsible for:

- API
- authentication
- authorization
- database
- projects
- applications
- deployments
- jobs
- configuration
- orchestration

## Web UI

Responsible for:

- dashboard
- application management
- server management
- deployment management
- logs
- monitoring
- settings

## Agent

Responsible for:

- Docker operations
- server operations
- deployments
- logs
- metrics
- health
- communication with the control plane

## Database

Stores:

- users
- projects
- applications
- servers
- deployments
- configuration
- metadata
- audit events

## Queue

Handles:

- builds
- deployments
- scheduled jobs
- background operations

---

# 63. Suggested Initial Technology Direction

A possible stack based on the project requirements is:

## Backend

.NET / ASP.NET Core
or python

Reasons:

- strong API support
- good async capabilities
- background services
- mature ecosystem
- good fit with the existing developer experience

## Frontend

Next.js / React

Reasons:

- modern web UI
- good component ecosystem
- server/client rendering options
- easy API integration

## Database

PostgreSQL

## Queue / Cache

Redis

## Runtime

Docker

## Agent

A small dedicated service communicating with the control plane.

The final technology choices should remain implementation decisions rather than hard requirements of the product specification.


## installation
Should be as simple as coolify, run a command that downloads/installs Aethera.
---



# 71. Product Positioning

Aethera should not be positioned simply as a Coolify clone.

A better description is:

> **Aethera is an open-source self-hosted deployment and infrastructure management platform for running applications, services, and containers on your own servers.**

A shorter description:

> **Self-hosted infrastructure. Simple deployments. Full control.**

The project should be able to stand independently from Turn One while retaining the credibility of being built and tested against a real production infrastructure.

---
