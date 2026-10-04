/**
 * Types of the project, application, deployment, service and ops endpoints, taken from the generated OpenAPI
 * schema (`pnpm gen:api`). Nothing here is invented: if the API lacks a field, the UI does not show it.
 */
import type { components } from "@/lib/api/schema";

type S = components["schemas"];

export type Page<T> = { items: T[]; nextCursor: string | null };

export type Project = S["ProjectResponse"];
export type Environment = S["EnvironmentResponse"];
export type ProjectTemplate = S["ProjectTemplate"];
export type ProjectFromTemplate = S["ProjectFromTemplateResponse"];
export type CreateProjectRequest = S["CreateProjectRequest"];
export type CreateEnvironmentRequest = S["CreateEnvironmentRequest"];

export type Application = S["ApplicationResponse"];
export type ApplicationSummary = S["ApplicationSummary"];
export type ApplicationSourceKind = S["ApplicationSourceKind"];
export type CreateApplicationRequest = S["CreateApplicationRequest"];
export type UpdateApplicationRequest = S["UpdateApplicationRequest"];
export type GitSourceRequest = S["GitSourceRequest"];
export type BuildConfigRequest = S["BuildConfigRequest"];
export type RuntimeRequest = S["RuntimeRequest"];
export type PortRequest = S["PortRequest"];
export type HealthCheckRequest = S["HealthCheckRequest"];
export type WorkloadStatus = S["WorkloadStatus"];
export type WorkloadState = S["WorkloadStatusResponse"];
export type BuildDetectResponse = S["BuildDetectResponse"];
export type BuildCandidate = S["BuildCandidateDto"];

export type EnvVar = S["EnvVarResponse"];
export type CreateEnvVarRequest = S["CreateEnvVarRequest"];
export type Volume = S["VolumeResponse"];
export type CreateVolumeRequest = S["CreateVolumeRequest"];
export type Domain = S["DomainResponse"];
export type CreateDomainRequest = S["CreateDomainRequest"];
export type DnsStatus = S["DnsStatus"];

export type Deployment = S["DeploymentDto"];
export type DeploymentStep = S["DeploymentStep"];
export type DeploymentStepDto = S["DeploymentStepDto"];
export type DeploymentStatus = S["DeploymentStatus"];
export type DeploymentTrigger = S["DeploymentTrigger"];
export type DeploymentListItem = S["DeploymentListItemDto"];
/** One stored log chunk (`LogLine` of the API; the schema generator drops it next to the text/plain response). */
export type LogLine = { sequence: number; timestamp: string; stream: "stdout" | "stderr" | number; source: string | number; text: string };
/** `GET /deployments/{id}/logs` (JSON). Hand-written for the same reason as `LogLine`; keep in step with `DeploymentLogPageDto`. */
export type DeploymentLogPage = {
  source: "build" | "deploy";
  streamId: string;
  items: LogLine[];
  nextSequence: number;
  hasMore: boolean;
  ended: boolean;
  sources: Array<"build" | "deploy">;
};
export type RuntimeLogs = S["RuntimeLogsDto"];

export type Webhook = S["WebhookDto"];
export type WebhookDelivery = S["WebhookDeliveryDto"];
export type WebhookSetupRequest = S["WebhookSetupRequest"];
export type GitCredential = S["GitCredentialResponse"];

export type Service = S["ServiceResponse"];
export type ServiceSummary = S["ServiceSummary"];
export type ServiceTemplate = S["ServiceTemplate"];
export type CreateServiceRequest = S["CreateServiceRequest"];
export type UpdateServiceRequest = S["UpdateServiceRequest"];

export type Secret = S["SecretResponse"];
export type CreateSecretRequest = S["CreateSecretRequest"];
export type Registry = S["RegistryResponse"];
export type CreateRegistryRequest = S["CreateRegistryRequest"];
export type User = S["UserResponse"];
export type CreateUserRequest = S["CreateUserRequest"];
export type OrganizationRole = S["OrganizationRole"];
export type ApiToken = S["ApiTokenResponse"];
export type CreatedApiToken = S["CreatedApiTokenResponse"];
export type AuditEvent = S["AuditEventDto"];
export type Job = S["JobDto"];

/** The nine pipeline steps in execution order (spec section 5). */
export const DEPLOYMENT_STEPS: readonly DeploymentStep[] = [
  "source",
  "build",
  "image",
  "targetServer",
  "container",
  "network",
  "domain",
  "healthCheck",
  "running",
] as const;
