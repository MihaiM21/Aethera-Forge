import type { Metadata } from "next";
import { DesignGallery } from "./design-gallery";

export const metadata: Metadata = {
  title: "Design",
  description: "Aethera component and motif gallery",
};

export default function DesignPage() {
  return <DesignGallery />;
}
