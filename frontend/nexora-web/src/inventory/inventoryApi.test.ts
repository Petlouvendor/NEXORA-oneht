import { describe, expect, it } from "vitest";
import { buildStockQuery } from "@/inventory/inventoryApi";

describe("buildStockQuery", () => {
  it("drops empty, null and undefined params", () => {
    expect(buildStockQuery({ productId: "p1", limit: 50, locationId: undefined, status: null, offset: "" }))
      .toBe("?productId=p1&limit=50");
  });

  it("keeps numeric values including zero", () => {
    expect(buildStockQuery({ limit: 10, offset: 0 })).toBe("?limit=10&offset=0");
    expect(buildStockQuery({ limit: 10, offset: 20 })).toBe("?limit=10&offset=20");
  });

  it("returns empty string when nothing set", () => {
    expect(buildStockQuery({ withinDays: undefined })).toBe("");
  });
});
