import { describe, expect, it } from "vitest";
import { qs } from "@/catalog/catalogApi";

describe("qs", () => {
  it("ignores empty, null and undefined values", () => {
    expect(qs({ q: "para", limit: 20, categoryId: undefined, status: null, note: "" }))
      .toBe("?q=para&limit=20");
  });

  it("returns an empty string when nothing is set", () => {
    expect(qs({ q: undefined, limit: null })).toBe("");
  });

  it("encodes values", () => {
    expect(qs({ q: "a b&c" })).toBe("?q=a+b%26c");
  });
});
