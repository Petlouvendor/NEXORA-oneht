import { describe, expect, it } from "vitest";
import { decodeScaleLabel } from "@/pos/scaleLabel";

describe("étiquette de balance", () => {
  it("lit l'article et le poids d'un EAN-13 à usage interne", () => {
    expect(decodeScaleLabel("2012345012509")).toEqual({ prefix: "2012345", kilograms: 1.25 });
  });

  it("refuse une clé fausse, un poids nul ou un code ordinaire", () => {
    expect(decodeScaleLabel("2012345012508")).toBeNull();
    expect(decodeScaleLabel("3017620422003")).toBeNull();
    expect(decodeScaleLabel("2012345000001")).toBeNull();
  });
});
