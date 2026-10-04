import type { ResourcesApi } from "@/lib/resources/api";
import type { ApplicationSummary, Deployment, DeploymentListItem, DeploymentStepDto, Project } from "@/lib/resources/types";

export const NOW = "2026-10-04T10:00:00.000Z";

/** A partial API: the test supplies the calls it expects, anything else is a bug and throws. */
export function fakeResources(partial: Record<string, Record<string, unknown>>): ResourcesApi {
  return new Proxy(partial, {
    get(target, group: string) {
      return new Proxy(target[group] ?? {}, {
        get(inner, name: string) {
          if (name in inner) return inner[name];
          return () => {
            throw new Error(`Unexpected API call: ${group}.${name}`);
          };
        },
      });
    },
  }) as unknown as ResourcesApi;
}

export function makeDeployment(over: Partial<Deployment> = {}): Deployment {
  return {
    id: "d-1",
    applicationId: "app-1",
    number: 1,
    status: "running",
    trigger: "manual",
    strategy: "recreate",
    currentStep: "running",
    failedStep: null,
    failureCode: null,
    failureReason: null,
    ref: "main",
    commitSha: "0123456789abcdef",
    commitMessage: "Fix the thing",
    commitAuthor: "Ada",
    imageRef: "aethera/web:d-1",
    imageDigest: null,
    isRollbackPoint: true,
    canRollbackTo: true,
    rollbackOfDeploymentId: null,
    jobId: "job-1",
    createdAt: NOW,
    startedAt: NOW,
    finishedAt: NOW,
    durationMs: 83_000,
    steps: null,
    ...over,
  };
}

export function makeStep(step: NonNullable<DeploymentStepDto["step"]>, status: DeploymentStepDto["status"], over: Partial<DeploymentStepDto> = {}): DeploymentStepDto {
  return { step, status, startedAt: NOW, finishedAt: NOW, errorCode: null, errorMessage: null, ...over };
}

export function makeListItem(over: Partial<Deployment> = {}, name = "web"): DeploymentListItem {
  return { deployment: makeDeployment(over), applicationName: name, applicationSlug: name, serverId: "srv-1" };
}

export function makeApp(over: Partial<ApplicationSummary> = {}): ApplicationSummary {
  return {
    id: "app-1",
    name: "web",
    slug: "web",
    description: null,
    projectId: "p-1",
    environmentId: "env-1",
    serverId: "srv-1",
    sourceKind: "dockerfile",
    desiredState: "running",
    status: "running",
    statusReason: null,
    currentDeploymentId: "d-1",
    repositoryUrl: "https://github.com/acme/web.git",
    image: null,
    createdAt: NOW,
    updatedAt: NOW,
    ...over,
  };
}

export function makeProject(over: Partial<Project> = {}): Project {
  return {
    id: "p-1",
    name: "Shop",
    slug: "shop",
    description: null,
    templateKey: null,
    environments: [
      { id: "env-1", projectId: "p-1", name: "Staging", slug: "staging", description: null, isProduction: false, createdAt: NOW, updatedAt: NOW },
      { id: "env-2", projectId: "p-1", name: "Production", slug: "production", description: null, isProduction: true, createdAt: NOW, updatedAt: NOW },
    ],
    createdAt: NOW,
    updatedAt: NOW,
    ...over,
  };
}
