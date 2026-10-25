import { describe, expect, it } from "vitest";
import {
  applyCandidate,
  envToCreate,
  initialWizardState,
  nextSlug,
  parseDotenv,
  providerOf,
  suggestFrom,
  suggestName,
  toCreateRequest,
  validateStep,
  type WizardState,
} from "./wizard";

const base: WizardState = { ...initialWizardState, environmentId: "env-1", name: "Web", slug: "web", serverId: "srv-1" };

describe("validateStep", () => {
  it("requires an environment, a name and a repository for a repo-based method", () => {
    const e = validateStep("source", { ...initialWizardState });
    expect(Object.keys(e).sort()).toEqual(["environmentId", "name", "repoUrl", "slug"]);
  });

  it("accepts https, ssh and scp-style repository URLs and rejects the rest", () => {
    for (const url of ["https://github.com/o/r.git", "ssh://git@host/o/r", "git@github.com:o/r.git"]) {
      expect(validateStep("source", { ...base, repoUrl: url }).repoUrl).toBeUndefined();
    }
    expect(validateStep("source", { ...base, repoUrl: "file:///etc" }).repoUrl).toBeDefined();
    expect(validateStep("source", { ...base, repoUrl: "--upload-pack=x" }).repoUrl).toBeDefined();
  });

  it("checks the image of a docker image app, not a repository", () => {
    const s = { ...base, method: "dockerImage" as const };
    expect(validateStep("source", s).image).toBeDefined();
    expect(validateStep("source", { ...s, image: "nginx" })).toEqual({});
    expect(validateStep("source", { ...s, image: "bad image" }).image).toBeDefined();
  });

  it("flags duplicate and malformed environment names but skips empty rows", () => {
    const env = [
      { id: 1, key: "A", value: "1", secret: false },
      { id: 2, key: "A", value: "2", secret: false },
      { id: 3, key: "1BAD", value: "x", secret: false },
      { id: 4, key: "", value: "", secret: false },
    ];
    const e = validateStep("env", { ...base, env });
    expect(Object.keys(e).sort()).toEqual(["env.2", "env.3"]);
  });

  it("needs a server and a valid port, and a valid hostname when one is given", () => {
    expect(validateStep("server", { ...base, serverId: "", port: "0" })).toMatchObject({ serverId: expect.any(String), port: expect.any(String) });
    expect(validateStep("server", base)).toEqual({});
    expect(validateStep("domain", { ...base, hostname: "not a host" }).hostname).toBeDefined();
    expect(validateStep("domain", { ...base, hostname: "app.example.com" })).toEqual({});
    expect(validateStep("domain", base)).toEqual({});
  });

  it("static sites need an output directory", () => {
    expect(validateStep("build", { ...base, method: "static" }).outputDirectory).toBeDefined();
    expect(validateStep("build", { ...base, method: "static", outputDirectory: "dist" })).toEqual({});
  });
});

describe("toCreateRequest", () => {
  it("builds a dockerfile application with git source, build config and an http port", () => {
    const req = toCreateRequest({ ...base, repoUrl: "https://github.com/o/r.git", credentialId: "", healthPath: "/health", port: "8080" });
    expect(req.sourceKind).toBe("dockerfile");
    expect(req.gitSource).toMatchObject({ provider: "gitHub", repositoryUrl: "https://github.com/o/r.git", branch: "main", gitCredentialId: null, autoDeploy: true });
    expect(req.build).toMatchObject({ engine: "dockerfile", dockerfilePath: "Dockerfile", context: "." });
    expect(req.runtime?.ports).toEqual([{ containerPort: 8080, isHttp: true }]);
    expect(req.runtime?.healthCheck).toMatchObject({ type: "http", path: "/health", port: 8080 });
    expect(req.image).toBeUndefined();
  });

  it("builds an image application and a compose application without a runtime port", () => {
    const img = toCreateRequest({ ...base, method: "dockerImage", image: "nginx", tag: "1.27" });
    expect(img.image).toMatchObject({ image: "nginx", tag: "1.27" });
    expect(img.gitSource).toBeUndefined();
    const compose = toCreateRequest({ ...base, method: "compose", composeSource: "inline", composeContent: "services: {}" });
    expect(compose.compose).toEqual({ inlineContent: "services: {}" });
    expect(compose.runtime).toBeUndefined();
  });

  it("builds a compose application from the repository: git source and path, no build config, no inline file", () => {
    const req = toCreateRequest({
      ...base, method: "compose", composeSource: "repo", composePath: " deploy/docker-compose.yml ", repoUrl: "git@github.com:acme/shop.git", branch: "main", credentialId: "cred-1",
    });
    expect(req.sourceKind).toBe("compose");
    expect(req.compose).toEqual({ filePath: "deploy/docker-compose.yml" });
    expect(req.gitSource).toMatchObject({ repositoryUrl: "git@github.com:acme/shop.git", branch: "main", gitCredentialId: "cred-1", provider: "gitHub" });
    expect(req.build).toBeUndefined();
    expect(req.runtime).toBeUndefined();
  });

  it("validates the compose source: a path inside the repository, or pasted content", () => {
    const repo = { ...base, method: "compose" as const, composeSource: "repo" as const, repoUrl: "https://github.com/a/b.git" };
    expect(validateStep("source", { ...repo, composePath: "docker-compose.yml" })).toEqual({});
    for (const bad of ["", "/etc/compose.yml", "../x.yml", "a/../../x.yml", "a\\b.yml", "-f"]) expect(validateStep("source", { ...repo, composePath: bad }).composePath).toBeDefined();
    expect(validateStep("source", { ...repo, repoUrl: "", composePath: "x.yml" }).repoUrl).toBeDefined();
    const inline = { ...base, method: "compose" as const, composeSource: "inline" as const };
    expect(validateStep("source", inline).composeContent).toBeDefined();
    expect(validateStep("source", { ...inline, composeContent: "services: {}" })).toEqual({});
    expect(validateStep("domain", { ...inline, hostname: "not a host" })).toEqual({}); // compose domains are added per service later
  });

  it("uses a tcp health check when no path is given", () => {
    expect(toCreateRequest({ ...base, repoUrl: "https://x.io/a/b" }).runtime?.healthCheck).toMatchObject({ type: "tcp" });
  });
});

describe("helpers", () => {
  it("detects the git provider and suggests a name", () => {
    expect(providerOf("https://github.com/o/r")).toBe("gitHub");
    expect(providerOf("git@gitlab.com:o/r.git")).toBe("gitLab");
    expect(providerOf("https://git.example.com/o/r")).toBe("generic");
    expect(suggestName("https://github.com/o/my-app.git")).toBe("my-app");
  });

  it("derives the slug from the name until the user edits it", () => {
    expect(nextSlug(initialWizardState, "My App!").slug).toBe("my-app");
    expect(nextSlug({ ...initialWizardState, slugTouched: true, slug: "custom" }, "Other")).toEqual({ name: "Other" });
  });

  it("suggests a name from the repository only until the user typed one", () => {
    expect(suggestFrom(initialWizardState, "my-app")).toMatchObject({ name: "my-app", slug: "my-app" });
    expect(suggestFrom({ ...initialWizardState, nameTouched: true }, "other")).toEqual({});
    expect(suggestFrom(initialWizardState, "")).toEqual({});
  });

  it("applies a detection candidate to the build step", () => {
    const next = applyCandidate(base, {
      engine: "static", confidence: 0.9, reason: "vite", dockerfilePath: null, installCommand: "pnpm i", buildCommand: "pnpm build", startCommand: null,
      outputDirectory: "dist", language: "node", suggestedPorts: [80],
    });
    expect(next).toMatchObject({ method: "static", outputDirectory: "dist", installCommand: "pnpm i", port: "80" });
  });

  it("parses dotenv text and ignores comments", () => {
    expect(parseDotenv('# c\nA=1\nexport B="two words"\n\nC=\'x\'\nbad line')).toEqual([
      { key: "A", value: "1" },
      { key: "B", value: "two words" },
      { key: "C", value: "x" },
    ]);
    expect(envToCreate({ ...base, env: [{ id: 1, key: " K ", value: "v", secret: true }, { id: 2, key: "", value: "", secret: false }] })).toEqual([{ key: "K", value: "v", secret: true }]);
  });
});
