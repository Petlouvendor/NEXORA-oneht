import { act } from "react";
import { createRoot } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  customerStatement, downloadDocument, exportReport, exportScreen, listSupplierDocuments, printDocument,
} from "@/shared/printApi";
import { DataTable, TableToolsContext } from "@/shared/ui";

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const file = (type: string, name: string) =>
  new Response(new Blob(["x"], { type }), { status: 200, headers: { "Content-Type": type, "Content-Disposition": `attachment; filename="${name}"` } });

describe("printApi — reçus et exports en vrais fichiers", () => {
  let anchor: HTMLAnchorElement;
  beforeEach(() => {
    configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} });
    URL.createObjectURL = vi.fn(() => "blob:x");
    URL.revokeObjectURL = vi.fn();
    anchor = document.createElement("a");
    vi.spyOn(anchor, "click").mockImplementation(() => {});
  });
  afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); document.body.innerHTML = ""; });

  const stubAnchor = () => {
    const create = document.createElement.bind(document);
    vi.spyOn(document, "createElement").mockImplementation((tag: string) => (tag === "a" ? anchor : create(tag)));
  };

  it("printDocument réserve l'onglet pendant le clic puis y affiche le ticket 80 mm", async () => {
    const tab = { closed: false, location: { href: "" }, document: { title: "", body: { innerHTML: "" } }, close: vi.fn() };
    const open = vi.spyOn(window, "open").mockReturnValue(tab as unknown as Window);
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      expect(open).toHaveBeenCalledTimes(1);            // onglet ouvert avant l'appel réseau
      expect(url).toContain("/print/customer-payment/pay-1?format=ticket");
      return file("application/pdf", "recu-versement-rgl-1-ticket.pdf");
    }));
    await printDocument("customer-payment", "pay-1", "ticket");
    expect(tab.location.href).toBe("blob:x");
    expect(tab.close).not.toHaveBeenCalled();
  });

  it("customerStatement imprime le relevé au format ticket 80 mm (imprimante de caisse)", async () => {
    const tab = { closed: false, location: { href: "" }, document: { title: "", body: { innerHTML: "" } }, close: vi.fn() };
    vi.spyOn(window, "open").mockReturnValue(tab as unknown as Window);
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      expect(url).toContain("/print/customers/cust-1/statement?format=ticket");
      return file("application/pdf", "releve-cli-00002-ticket.pdf");
    }));
    await customerStatement("cust-1", "ticket", {}, true);
    expect(tab.location.href).toBe("blob:x");
  });

  it("printDocument enregistre le PDF si les fenêtres sont bloquées, et referme l'onglet en cas d'erreur", async () => {
    stubAnchor();
    vi.spyOn(window, "open").mockReturnValue(null);
    vi.stubGlobal("fetch", vi.fn(async () => file("application/pdf", "credit-crd-1.pdf")));
    await printDocument("customer-credit", "c-1");
    expect(anchor.download).toBe("credit-crd-1.pdf");

    const tab = { closed: false, location: { href: "" }, document: { title: "", body: { innerHTML: "" } }, close: vi.fn() };
    vi.spyOn(window, "open").mockReturnValue(tab as unknown as Window);
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ title: "print.forbidden", detail: "Refusé" }), { status: 403 })));
    await expect(printDocument("customer-credit", "c-1")).rejects.toThrow();
    expect(tab.close).toHaveBeenCalled();
  });

  it("reçoit le PDF dans une enveloppe JSON (jamais intercepté) et le reconstitue octet pour octet", async () => {
    stubAnchor();
    const pdf = "%PDF-1.7 corps";
    let accept = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      accept = new Headers(init.headers).get("Accept") ?? "";
      return new Response(JSON.stringify({ fileName: "recu-versement-rgl-1.pdf", contentType: "application/pdf", size: pdf.length, base64: btoa(pdf) }),
        { status: 200, headers: { "Content-Type": "application/vnd.nexora.file+json; charset=utf-8" } });
    }));
    const saved: Blob[] = [];
    URL.createObjectURL = vi.fn((b: Blob) => { saved.push(b); return "blob:x"; });
    await downloadDocument("customer-payment", "pay-1", "pdf");
    expect(accept).toContain("application/vnd.nexora.file+json");
    expect(anchor.download).toBe("recu-versement-rgl-1.pdf");
    expect(saved[0].type).toBe("application/pdf");
    const text = await new Promise<string>((resolve) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.readAsText(saved[0]);
    });
    expect(text).toBe(pdf);
  });

  it("refuse d'enregistrer un fichier vide (corps intercepté par le navigateur)", async () => {
    stubAnchor();
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 204 })));
    await expect(downloadDocument("customer-payment", "pay-1", "pdf")).rejects.toThrow(/vide/);
    expect(anchor.click).not.toHaveBeenCalled();
  });

  it("downloadDocument enregistre le classeur Excel sous le nom donné par le serveur", async () => {
    stubAnchor();
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      expect(url).toContain("/print/supplier-payment/p-9?format=xlsx");
      return file("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "paiement-fournisseur-rgl-9.xlsx");
    }));
    await downloadDocument("supplier-payment", "p-9", "xlsx");
    expect(anchor.download).toBe("paiement-fournisseur-rgl-9.xlsx");
    expect(anchor.click).toHaveBeenCalled();
  });

  it("exportScreen envoie le tableau affiché au serveur (POST JSON) et télécharge le fichier", async () => {
    stubAnchor();
    let body: unknown = null;
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/exports/screen?format=pdf");
      expect(init.method).toBe("POST");
      body = JSON.parse(String(init.body));
      return file("application/pdf", "credits.pdf");
    }));
    await exportScreen("customers-credits", { title: "Crédits", tables: [{ columns: [{ label: "Reste dû", kind: "money" }], rows: [[10]] }] }, "pdf");
    expect(body).toEqual({ screenKey: "customers-credits", document: { title: "Crédits", tables: [{ columns: [{ label: "Reste dû", kind: "money" }], rows: [[10]] }] } });
    expect(anchor.download).toBe("credits.pdf");
  });

  it("exportReport exporte un rapport de pilotage complet côté serveur", async () => {
    stubAnchor();
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/reports/export");
      expect(JSON.parse(String(init.body))).toEqual({ reportKey: "sales_journal", format: "xlsx", filter: { preset: "month" } });
      return file("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "sales_journal.xlsx");
    }));
    await exportReport("sales_journal", "xlsx", { preset: "month" });
    expect(anchor.download).toBe("sales_journal.xlsx");
  });

  it("listSupplierDocuments lit les pièces imprimables d'un fournisseur", async () => {
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      expect(url).toContain("/print/suppliers/s-1/documents");
      return new Response(JSON.stringify([{ kind: "goods-receipt", id: "g", number: "BR-1" }]), { status: 200 });
    }));
    expect((await listSupplierDocuments("s-1"))[0].number).toBe("BR-1");
  });

  it("DataTable affiche l'outil d'export fourni par l'application, sauf s'il est désactivé", async () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    const tools = () => <span data-testid="export-tool">export</span>;
    const columns = [{ key: "a", header: "A", render: (r: { a: string }) => r.a }];
    await act(async () => {
      root.render(
        <TableToolsContext.Provider value={tools}>
          <DataTable columns={columns} rows={[{ a: "1" }]} rowKey={(r) => r.a} />
          <DataTable columns={columns} rows={[{ a: "2" }]} rowKey={(r) => r.a} exportTitle={false} />
        </TableToolsContext.Provider>,
      );
    });
    expect(host.querySelectorAll("[data-testid=export-tool]")).toHaveLength(1);
    await act(async () => root.unmount());
  });
});
