import { describe, expect, it } from "vitest";
import {
  DEFAULT_POLICY,
  DESTINATION_PRESETS,
  SCHEDULE_PRESETS,
  describeRetention,
  describeSchedule,
  destinationInput,
  draftOf,
  draftProblem,
  hasPlaceholder,
  inputOf,
  presetByKey,
  schedulePresetOf,
  type DestinationDraft,
} from "./presets";
import { createBackupsApi } from "./api";
import type { ApiClient } from "@/lib/api/client";

describe("destination presets", () => {
  it("fills Cloudflare R2 with the account endpoint pattern and region auto", () => {
    const r2 = presetByKey("r2")!;
    expect(r2.endpoint).toBe("https://<account id>.r2.cloudflarestorage.com");
    expect(r2.region).toBe("auto");
    expect(hasPlaceholder(r2.endpoint)).toBe(true);
    expect(hasPlaceholder("https://abc123.r2.cloudflarestorage.com")).toBe(false);
  });

  it("offers R2, Backblaze B2, AWS S3, MinIO and local", () => {
    expect(DESTINATION_PRESETS.map((p) => p.key)).toEqual(["r2", "b2", "aws", "minio", "local"]);
    expect(presetByKey("minio")!.forcePathStyle).toBe(true);
    expect(presetByKey("local")!.kind).toBe("local");
  });

  const draft: DestinationDraft = {
    presetKey: "r2", name: " R2 ", endpoint: " https://abc.r2.cloudflarestorage.com ", region: "auto", bucket: " bucket ", pathPrefix: "/aethera/", accessKeyId: " key ",
    secretAccessKey: "secret", forcePathStyle: false, isDefault: true,
  };

  it("builds the create body: trimmed, the prefix without slashes, kind s3", () => {
    expect(destinationInput(draft, "s3", false)).toEqual({
      name: "R2", kind: "s3", endpoint: "https://abc.r2.cloudflarestorage.com", region: "auto", bucket: "bucket", pathPrefix: "aethera", accessKeyId: "key",
      secretAccessKey: "secret", forcePathStyle: false, isDefault: true,
    });
  });

  it("an update without a new secret does not send one, and never sends the kind", () => {
    const body = destinationInput({ ...draft, secretAccessKey: "", region: "", pathPrefix: "" }, "s3", true);
    expect(body).not.toHaveProperty("secretAccessKey");
    expect(body).not.toHaveProperty("kind");
    expect(body.region).toBeNull();
    expect(body.pathPrefix).toBeNull();
  });

  it("a local destination sends only its name and default flag", () => {
    expect(destinationInput(draft, "local", false)).toEqual({ name: "R2", kind: "local", isDefault: true });
  });
});

describe("schedules and retention", () => {
  it("knows the presets and calls everything else custom", () => {
    expect(SCHEDULE_PRESETS.map((p) => p.cron)).toEqual(["0 3 * * *", "0 */6 * * *", "0 3 * * 0"]);
    expect(schedulePresetOf("0 3 * * *")).toBe("daily");
    expect(schedulePresetOf(" 0 */6 * * * ")).toBe("6h");
    expect(schedulePresetOf("0 3 * * 0")).toBe("weekly");
    expect(schedulePresetOf("15 2 * * 1-5")).toBe("custom");
  });

  it("describes a schedule with its time zone", () => {
    expect(describeSchedule("0 3 * * *", "Europe/Bucharest")).toBe("Daily at 03:00 (Europe/Bucharest)");
    expect(describeSchedule("15 2 * * 1-5", "UTC")).toBe("15 2 * * 1-5 (UTC)");
    expect(describeRetention({ keepDaily: 7, keepWeekly: 4, keepMonthly: 6 })).toBe("7 daily · 4 weekly · 6 monthly");
  });

  it("starts a new policy with the defaults of the release: daily 03:00 UTC, 7 / 4 / 6", () => {
    const d = draftOf(null, "dest-1");
    expect(d).toMatchObject({ enabled: true, destinationId: "dest-1", schedule: "0 3 * * *", timeZone: "UTC", keepDaily: "7", keepWeekly: "4", keepMonthly: "6" });
    expect(DEFAULT_POLICY.keepDaily).toBe(7);
  });

  it("checks what is obvious before sending", () => {
    const ok = draftOf(null, "dest-1");
    expect(draftProblem(ok)).toBeNull();
    expect(draftProblem({ ...ok, destinationId: "" })).toMatch(/destination/i);
    expect(draftProblem({ ...ok, schedule: " " })).toMatch(/schedule/i);
    expect(draftProblem({ ...ok, keepDaily: "-1" })).toMatch(/whole numbers/);
    expect(draftProblem({ ...ok, keepWeekly: "1.5" })).toMatch(/whole numbers/);
    expect(draftProblem({ ...ok, keepDaily: "0", keepWeekly: "0", keepMonthly: "0" })).toMatch(/at least one/);
    expect(draftProblem({ ...ok, keepDaily: "0", keepWeekly: "0", keepMonthly: "1" })).toBeNull();
  });

  it("turns the draft into the request", () => {
    expect(inputOf({ ...draftOf(null, "d"), schedule: " 0 4 * * * ", timeZone: " ", keepDaily: "3" })).toEqual({
      destinationId: "d", enabled: true, schedule: "0 4 * * *", timeZone: "UTC", keepDaily: 3, keepWeekly: 4, keepMonthly: 6,
    });
  });
});

describe("backups api", () => {
  it("uses the endpoints of ADR 0010", async () => {
    const calls: string[] = [];
    const client = new Proxy({}, {
      get: (_t, method: string) => (path: string, ...rest: unknown[]) => {
        calls.push(`${method.toUpperCase()} ${path}${method === "delete" ? "" : ""}`);
        void rest;
        return Promise.resolve({});
      },
    }) as unknown as ApiClient;
    const api = createBackupsApi(client);
    await api.destinations.list();
    await api.destinations.test("d 1");
    await api.policies.forWorkload("services", "s-1");
    await api.policies.put("applications", "a-1", { destinationId: "d", enabled: true, schedule: "0 3 * * *", timeZone: "UTC", keepDaily: 1, keepWeekly: 0, keepMonthly: 0 });
    await api.policies.run("p-1");
    await api.system.policy();
    await api.runs.restore("r-1", { target: "same", confirm: "db" });
    expect(calls).toEqual([
      "GET /backup-destinations",
      "POST /backup-destinations/d%201/test",
      "GET /services/s-1/backup-policies",
      "PUT /applications/a-1/backup-policies",
      "POST /backup-policies/p-1/run",
      "GET /system/backup-policy",
      "POST /backup-runs/r-1/restore",
    ]);
  });
});
