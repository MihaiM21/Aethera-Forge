import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { InsecureConnectionHint, isInsecureRemote } from "./insecure-connection-hint";

describe("isInsecureRemote", () => {
  it.each([
    ["http:", "203.0.113.5", true],
    ["http:", "aethera.example.com", true],
    ["https:", "203.0.113.5", false],
    ["http:", "localhost", false],
    ["http:", "app.localhost", false],
    ["http:", "127.0.0.1", false],
    ["http:", "[::1]", false],
  ])("%s//%s -> %s", (protocol, host, expected) => {
    expect(isInsecureRemote(protocol, host)).toBe(expected);
  });
});

describe("InsecureConnectionHint", () => {
  it("is hidden on localhost over http (jsdom default)", () => {
    render(<InsecureConnectionHint />);
    expect(screen.queryByRole("note")).toBeNull();
  });
});
