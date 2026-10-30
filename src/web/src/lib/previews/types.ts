// The preview API (R2.3, ADR 0012). Shapes follow openapi/aethera.v1.json (PreviewDto, PreviewSettingsDto...).
import type { components } from "@/lib/api/schema";
import type { Deployment, Page } from "@/lib/resources/types";

type S = components["schemas"];

export type { Deployment, Page };

export type Preview = S["PreviewDto"];
export type PreviewDeployment = S["PreviewDeploymentDto"];
export type PreviewStatus = S["PreviewStatus"];
export type PreviewSettings = S["PreviewSettingsDto"];
export type PreviewSettingsInput = S["UpdatePreviewSettingsRequest"];
