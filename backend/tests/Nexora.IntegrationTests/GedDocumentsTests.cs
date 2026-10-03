using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Ged;
using Nexora.Contracts.Ged;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// GED, documents, archivage & signature (Phase 45 §3-6, §13-31, §39, §45). `document`/
/// `document_version` sont distincts d'`issued_document` (Phase 30) et `attachment` (Phase 44) —
/// bibliothèque versionnée des pièces reçues/importées/scannées.
/// </summary>
[Collection("finance")]
public sealed class GedDocumentsTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Compta = Guid.Parse("dce00000-0000-0000-0000-0000000000d2");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static T For<T>(IServiceProvider sp, Guid user) where T : notnull
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(user);
        tc.SetScope([Company], [], Company);
        return sp.GetRequiredService<T>();
    }

    private async Task Sql(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Base64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    // ---- §7, §16, §39 : création, versioning, doublon, OCR ----
    [Fact]
    public async Task Document_create_detects_duplicate_versions_history_and_ocr_stub()
    {
        await using var s = Scope();
        var ged = For<IGedService>(s.ServiceProvider, Direction);
        var ocr = For<IDocumentOcrService>(s.ServiceProvider, Direction);
        var audit = For<IGedAuditQuery>(s.ServiceProvider, Direction);

        var csv = "col1,col2\nval1,val2";
        var a = await ged.CreateAsync(new CreateDocumentRequest(
            "Facture A", "invoice_purchase", "achats", ["fournisseur"], null, null, null, null, null, null, Base64(csv), "text/csv"));
        a.IsSuccess.Should().BeTrue(a.Error?.Message);
        a.Value!.PotentialDuplicateOf.Should().BeNull();

        var b = await ged.CreateAsync(new CreateDocumentRequest(
            "Facture B (copie)", "invoice_purchase", null, null, null, null, null, null, null, null, Base64(csv), "text/csv"));
        b.IsSuccess.Should().BeTrue(b.Error?.Message);
        b.Value!.PotentialDuplicateOf.Should().Be(a.Value.Document.Id, "même contenu — doublon détecté par hash (§39), jamais bloquant");

        var ocrResult = await ocr.ExtractAsync(a.Value.Document.Id, null);
        ocrResult.IsSuccess.Should().BeTrue(ocrResult.Error?.Message);
        ocrResult.Value!.Status.Should().Be("completed");
        ocrResult.Value.Text.Should().Contain("val1,val2");

        var pdf = await ged.CreateAsync(new CreateDocumentRequest(
            "Scan illisible", "scan", null, null, null, null, null, null, null, null, Base64("%PDF-fake-bytes"), "application/pdf"));
        var pdfOcr = await ocr.ExtractAsync(pdf.Value!.Document.Id, null);
        pdfOcr.Value!.Status.Should().Be("unsupported", "aucun moteur OCR tiers embarqué cette phase (§11 backlog)");

        var newCsv = "col1,col2\nval3,val4";
        var v2 = await ged.AddVersionAsync(a.Value.Document.Id, new AddDocumentVersionRequest(Base64(newCsv), "text/csv", "correction"));
        v2.IsSuccess.Should().BeTrue(v2.Error?.Message);
        v2.Value!.Document.CurrentVersionNo.Should().Be(2);

        var versions = await ged.ListVersionsAsync(a.Value.Document.Id);
        versions.Should().HaveCount(2);

        var v1Content = await ged.GetVersionContentAsync(a.Value.Document.Id, 1);
        System.Text.Encoding.UTF8.GetString(v1Content.Value!.Content).Should().Be(csv);
        var currentContent = await ged.GetVersionContentAsync(a.Value.Document.Id, null);
        System.Text.Encoding.UTF8.GetString(currentContent.Value!.Content).Should().Be(newCsv);

        var trail = await audit.RecentAsync(a.Value.Document.Id, 50);
        trail.Should().Contain(e => e.Action == "INSERT", "création journalisée (§45)");
    }

    // ---- §18, §23-24 : verrouillage & workflow à 4 yeux ----
    [Fact]
    public async Task Document_workflow_requires_four_eyes_and_lock_blocks_new_version()
    {
        await using var s = Scope();
        var asDirection = For<IGedService>(s.ServiceProvider, Direction);

        var created = await asDirection.CreateAsync(new CreateDocumentRequest(
            "Contrat fournisseur", "contract", null, null, null, null, null, null, null, null, Base64("contrat v1"), "application/pdf"));
        var id = created.Value!.Document.Id;

        (await asDirection.SubmitForReviewAsync(id)).Value!.Status.Should().Be("pending_review");
        (await asDirection.ApproveAsync(id)).Error!.Code.Should().Be("document.four_eyes");

        var asCompta = For<IGedService>(s.ServiceProvider, Compta);
        var approved = await asCompta.ApproveAsync(id);
        approved.IsSuccess.Should().BeTrue(approved.Error?.Message);
        approved.Value!.Status.Should().Be("approved");

        (await asDirection.LockAsync(id, "en signature")).Value!.Locked.Should().BeTrue();
        (await asDirection.AddVersionAsync(id, new AddDocumentVersionRequest(Base64("contrat v2"), "application/pdf", null)))
            .Error!.Code.Should().Be("document.locked");
        (await asDirection.UnlockAsync(id)).Value!.Locked.Should().BeFalse();
        (await asDirection.AddVersionAsync(id, new AddDocumentVersionRequest(Base64("contrat v2"), "application/pdf", null)))
            .IsSuccess.Should().BeTrue();

        var rejectFlow = await asDirection.CreateAsync(new CreateDocumentRequest(
            "Devis à revoir", "quote", null, null, null, null, null, null, null, null, Base64("devis"), "application/pdf"));
        await asDirection.SubmitForReviewAsync(rejectFlow.Value!.Document.Id);
        var rejected = await asCompta.RejectAsync(rejectFlow.Value.Document.Id, "montant incorrect");
        rejected.IsSuccess.Should().BeTrue(rejected.Error?.Message);
        rejected.Value!.Status.Should().Be("draft");
    }

    // ---- §13-14, §27 : recherche & archivage ----
    [Fact]
    public async Task Document_search_filters_by_type_tags_text_and_archive_restore()
    {
        await using var s = Scope();
        var ged = For<IGedService>(s.ServiceProvider, Direction);
        var marker = Guid.NewGuid().ToString("N")[..8];

        var invoice = await ged.CreateAsync(new CreateDocumentRequest(
            $"Facture {marker}", "invoice_sale", null, ["urgent"], null, null, null, null, null,
            new DateOnly(2026, 1, 15), Base64("f1"), "application/pdf"));
        await ged.CreateAsync(new CreateDocumentRequest(
            $"Contrat {marker}", "contract", null, ["archive"], null, null, null, null, null, null, Base64("c1"), "application/pdf"));

        var byType = await ged.SearchAsync(new DocumentSearchQuery(DocumentType: "invoice_sale", Text: marker));
        byType.Should().ContainSingle(d => d.Name == $"Facture {marker}");

        var byTag = await ged.SearchAsync(new DocumentSearchQuery(Tags: ["urgent"], Text: marker));
        byTag.Should().ContainSingle(d => d.DocumentType == "invoice_sale");

        var byDate = await ged.SearchAsync(new DocumentSearchQuery(DateFrom: new DateOnly(2026, 1, 1), DateTo: new DateOnly(2026, 1, 31), Text: marker));
        byDate.Should().ContainSingle();

        var archived = await ged.ArchiveAsync(invoice.Value!.Document.Id);
        archived.Value!.Status.Should().Be("archived");
        (await ged.SearchAsync(new DocumentSearchQuery(Status: "archived", Text: marker))).Should().ContainSingle();

        var restored = await ged.RestoreAsync(invoice.Value.Document.Id);
        restored.Value!.Status.Should().Be("approved");
    }

    // ---- §6, §31 : liens multiples & partage à lien temporaire ----
    [Fact]
    public async Task Document_links_and_share_enforce_password_expiry_and_revocation()
    {
        await using var s = Scope();
        var ged = For<IGedService>(s.ServiceProvider, Direction);
        var created = await ged.CreateAsync(new CreateDocumentRequest(
            "Justificatif dépense", "expense_proof", null, null, null, null, null, null, null, null, Base64("recu"), "application/pdf"));
        var id = created.Value!.Document.Id;

        var linked = await ged.AddLinkAsync(id, new AddDocumentLinkRequest("sale", Guid.NewGuid()));
        linked.IsSuccess.Should().BeTrue(linked.Error?.Message);
        (await ged.ListLinksAsync(id)).Should().ContainSingle(l => l.LinkedType == "sale");

        var share = await ged.ShareAsync(id, new CreateShareRequest(DateTimeOffset.UtcNow.AddHours(1), true, "secret123"));
        share.IsSuccess.Should().BeTrue(share.Error?.Message);

        (await ged.GetSharedContentAsync(share.Value!.Token, "wrong")).Error!.Code.Should().Be("document_share.password");
        var ok = await ged.GetSharedContentAsync(share.Value.Token, "secret123");
        ok.IsSuccess.Should().BeTrue(ok.Error?.Message);
        System.Text.Encoding.UTF8.GetString(ok.Value!.Content).Should().Be("recu");

        await Sql($"UPDATE nexora.document_share SET expires_at = now() - interval '1 hour' WHERE id = '{share.Value.Id}'");
        (await ged.GetSharedContentAsync(share.Value.Token, "secret123")).Error!.Code.Should().Be("document_share.expired");

        var share2 = await ged.ShareAsync(id, new CreateShareRequest(DateTimeOffset.UtcNow.AddHours(1), true, null));
        (await ged.RevokeShareAsync(share2.Value!.Id)).IsSuccess.Should().BeTrue();
        (await ged.GetSharedContentAsync(share2.Value.Token, null)).Error!.Code.Should().Be("document_share.revoked");
    }

    // ---- §19-22 : signature multi-signataires, ordre respecté, verrouillage final ----
    [Fact]
    public async Task Signature_workflow_enforces_order_locks_document_and_supports_decline()
    {
        await using var s = Scope();
        var ged = For<IGedService>(s.ServiceProvider, Direction);
        var sig = For<ISignatureService>(s.ServiceProvider, Direction);

        var doc = await ged.CreateAsync(new CreateDocumentRequest(
            "Contrat à signer", "contract", null, null, null, null, null, null, null, null, Base64("contrat"), "application/pdf"));
        var docId = doc.Value!.Document.Id;

        var request = await sig.RequestAsync(docId, new CreateSignatureRequestRequest([
            new CreateSignerInput("user", Direction, null, null, "Direction", null),
            new CreateSignerInput("external", null, "Client Externe", "client@example.com", "Client", null),
        ]));
        request.IsSuccess.Should().BeTrue(request.Error?.Message);
        request.Value!.Signers.Should().HaveCount(2);

        (await ged.GetAsync(docId)).Value!.Locked.Should().BeTrue("un document en signature est verrouillé (§18)");

        var first = request.Value.Signers[0];
        var second = request.Value.Signers[1];
        (await sig.SignAsync(request.Value.Id, second.Id, new SignDocumentRequest(null))).Error!.Code.Should().Be("signature.order");

        var afterFirst = await sig.SignAsync(request.Value.Id, first.Id, new SignDocumentRequest("signé au bureau"));
        afterFirst.IsSuccess.Should().BeTrue(afterFirst.Error?.Message);
        afterFirst.Value!.Status.Should().Be("pending");

        var completed = await sig.SignAsync(request.Value.Id, second.Id, new SignDocumentRequest(null));
        completed.IsSuccess.Should().BeTrue(completed.Error?.Message);
        completed.Value!.Status.Should().Be("completed");

        var signedDoc = await ged.GetAsync(docId);
        signedDoc.Value!.Status.Should().Be("signed");
        signedDoc.Value.Locked.Should().BeTrue();

        var doc2 = await ged.CreateAsync(new CreateDocumentRequest(
            "Contrat refusé", "contract", null, null, null, null, null, null, null, null, Base64("c2"), "application/pdf"));
        var request2 = await sig.RequestAsync(doc2.Value!.Document.Id, new CreateSignatureRequestRequest([
            new CreateSignerInput("user", Direction, null, null, null, null),
        ]));
        var declined = await sig.DeclineAsync(request2.Value!.Id, request2.Value.Signers[0].Id, "désaccord sur une clause");
        declined.IsSuccess.Should().BeTrue(declined.Error?.Message);
        declined.Value!.Status.Should().Be("cancelled");
    }

    // ---- §27-29 : rétention documentaire dédiée, legal hold, suppression à 4 yeux ----
    [Fact]
    public async Task Retention_policy_legal_hold_blocks_deletion_and_confirmation_requires_four_eyes()
    {
        await using var s = Scope();
        var ged = For<IGedService>(s.ServiceProvider, Direction);
        var retention = For<IDocumentRetentionService>(s.ServiceProvider, Direction);
        var uniqueType = "internal";   // type couvert par le gabarit système générique (NULL) — testé via surcharge ci-dessous

        var policy = await retention.SavePolicyAsync(new SaveDocumentRetentionPolicyRequest(uniqueType, 1, "Test rétention 1 an"));
        policy.IsSuccess.Should().BeTrue(policy.Error?.Message);
        policy.Value!.IsSystemDefault.Should().BeFalse();
        (await retention.ListPoliciesAsync()).Should().Contain(p => p.DocumentType == uniqueType && !p.IsSystemDefault);

        var doc = await ged.CreateAsync(new CreateDocumentRequest(
            "Note interne", uniqueType, null, null, null, null, null, null, null, null, Base64("note"), "application/pdf"));
        var docId = doc.Value!.Document.Id;

        (await retention.ApplyRetentionAsync()).Should().BeGreaterThanOrEqualTo(1);
        var withRetention = await ged.GetAsync(docId);
        withRetention.Value!.RetentionUntil.Should().NotBeNull();

        var hold = await retention.SetLegalHoldAsync(docId, true, "litige en cours");
        hold.IsSuccess.Should().BeTrue(hold.Error?.Message);
        (await ged.RequestDeletionAsync(docId, "plus nécessaire")).Error!.Code.Should().Be("document.legal_hold");

        await retention.SetLegalHoldAsync(docId, false, null);
        (await ged.RequestDeletionAsync(docId, "plus nécessaire")).Error!.Code.Should().Be("document.retention", "rétention encore active (§28)");

        // Simule une rétention échue (le job planifié ne la ferait qu'après la durée réelle).
        await Sql($"UPDATE nexora.document SET retention_until = current_date - 1, status = 'archived' WHERE id = '{docId}'");
        var eligible = await retention.ListEligibleForDeletionAsync();
        eligible.Should().Contain(d => d.Id == docId);

        var requested = await ged.RequestDeletionAsync(docId, "rétention échue");
        requested.IsSuccess.Should().BeTrue(requested.Error?.Message);
        (await ged.ConfirmDeletionAsync(docId)).Error!.Code.Should().Be("document.four_eyes");

        var confirmedByOther = await For<IGedService>(s.ServiceProvider, Compta).ConfirmDeletionAsync(docId);
        confirmedByOther.IsSuccess.Should().BeTrue(confirmedByOther.Error?.Message);
        (await ged.GetAsync(docId)).Error!.Code.Should().Be("document.not_found");
    }

    // ---- Finalisation phase 10 : document → demande → destinataire → signature / refus → archivage ----
    private static readonly Guid Cashier = Guid.Parse("dce00000-0000-0000-0000-0000000000d3");

    [Fact]
    public async Task Signature_request_rules_turns_and_version_freeze()
    {
        await using var s = Scope();
        var ged = For<IGedService>(s.ServiceProvider, Direction);
        var sig = For<ISignatureService>(s.ServiceProvider, Direction);
        var doc = (await ged.CreateAsync(new CreateDocumentRequest(
            "Bail commercial", "contract", null, null, null, null, null, null, null, null, Base64("bail v1"), "application/pdf"))).Value!.Document;

        (await sig.CandidatesAsync()).Should().Contain(c => c.Id == Compta).And.NotContain(c => c.Id == Cashier);
        (await sig.RequestAsync(doc.Id, new CreateSignatureRequestRequest([new CreateSignerInput("user", Cashier, null, null, null, null)])))
            .Error!.Code.Should().Be("signature.signer_not_allowed");
        (await sig.RequestAsync(doc.Id, new CreateSignatureRequestRequest([
            new CreateSignerInput("user", Compta, null, null, null, null), new CreateSignerInput("user", Compta, null, null, null, null)])))
            .Error!.Code.Should().Be("signature.signer_duplicate");
        (await sig.RequestAsync(doc.Id, new CreateSignatureRequestRequest([
            new CreateSignerInput("user", Compta, null, null, null, DateTimeOffset.UtcNow.AddHours(-1))])))
            .Error!.Code.Should().Be("signature.signer_due");

        var req = await sig.RequestAsync(doc.Id, new CreateSignatureRequestRequest([
            new CreateSignerInput("user", Compta, null, null, "Comptable", null),
            new CreateSignerInput("external", null, "M. Bailleur", "bailleur@example.com", "Bailleur", null),
        ], "Merci de signer avant vendredi"));
        req.IsSuccess.Should().BeTrue(req.Error?.Message);
        req.Value!.VersionNo.Should().Be(1);
        req.Value.DocumentName.Should().Be("Bail commercial");
        req.Value.CurrentSignerId.Should().Be(req.Value.Signers[0].Id);
        req.Value.IsMyTurn.Should().BeFalse("c'est au comptable de signer, pas au demandeur");
        (await sig.RequestAsync(doc.Id, new CreateSignatureRequestRequest([new CreateSignerInput("user", Direction, null, null, null, null)])))
            .Error!.Code.Should().Be("signature.pending_exists");

        // Le document en signature ne se déverrouille pas, ne s'archive pas, ne reçoit pas de nouvelle version.
        (await ged.UnlockAsync(doc.Id)).Error!.Code.Should().Be("document.signing");
        (await ged.ArchiveAsync(doc.Id)).Error!.Code.Should().Be("document.signing");
        (await ged.AddVersionAsync(doc.Id, new AddDocumentVersionRequest(Base64("bail v2"), "application/pdf", "modif"))).Error!.Code.Should().Be("document.locked");

        // Seul le signataire désigné signe pour lui-même ; le demandeur recueille la signature de l'externe.
        (await sig.SignAsync(req.Value.Id, req.Value.Signers[0].Id, new SignDocumentRequest(null))).Error!.Code.Should().Be("signature.not_you");
        await using (var s2 = Scope())
        {
            var asCompta = For<ISignatureService>(s2.ServiceProvider, Compta);
            (await asCompta.ListAsync(null, mine: true)).Should().Contain(r => r.Id == req.Value.Id);
            (await asCompta.SignAsync(req.Value.Id, req.Value.Signers[1].Id, new SignDocumentRequest(null))).Error!.Code.Should().Be("signature.order");
            var first = await asCompta.SignAsync(req.Value.Id, req.Value.Signers[0].Id, new SignDocumentRequest("Lu et approuvé"));
            first.IsSuccess.Should().BeTrue(first.Error?.Message);
            first.Value!.Status.Should().Be("pending");
        }
        var done = await sig.SignAsync(req.Value.Id, req.Value.Signers[1].Id, new SignDocumentRequest("Signé en agence"));
        done.IsSuccess.Should().BeTrue(done.Error?.Message);
        done.Value!.Status.Should().Be("completed");
        done.Value.Signers[1].RecordedByName.Should().NotBeNull("la signature de l'externe est recueillie, et le dossier le dit");

        var signed = (await ged.GetAsync(doc.Id)).Value!;
        signed.Status.Should().Be("signed");
        (await ged.UnlockAsync(doc.Id)).Error!.Code.Should().Be("document.signed");
        (await ged.ArchiveAsync(doc.Id)).IsSuccess.Should().BeTrue("un document signé s'archive");
        (await ged.RestoreAsync(doc.Id)).Value!.Status.Should().Be("signed", "la restauration ne fait pas perdre le statut signé");

        // La preuve porte sur la version demandée.
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT proof->>'mode' || '|' || (proof->>'documentVersion') FROM nexora.document_signer WHERE id = '{req.Value.Signers[1].Id}'", c);
        ((string?)await cmd.ExecuteScalarAsync()).Should().Be("recorded_on_behalf|1");
    }

    [Fact]
    public async Task Declined_cancelled_or_expired_request_releases_the_document()
    {
        await using var s = Scope();
        var ged = For<IGedService>(s.ServiceProvider, Direction);
        var sig = For<ISignatureService>(s.ServiceProvider, Direction);

        var a = (await ged.CreateAsync(new CreateDocumentRequest(
            "Avenant", "contract", null, null, null, null, null, null, null, null, Base64("avenant"), "application/pdf"))).Value!.Document;
        var r1 = (await sig.RequestAsync(a.Id, new CreateSignatureRequestRequest([new CreateSignerInput("user", Direction, null, null, null, null)]))).Value!;
        (await sig.DeclineAsync(r1.Id, r1.Signers[0].Id, "")).Error!.Code.Should().Be("signature.decline_reason");
        var declined = await sig.DeclineAsync(r1.Id, r1.Signers[0].Id, "Clause 4 à revoir");
        declined.Value!.Status.Should().Be("cancelled");
        declined.Value.CancelledReason.Should().Contain("Clause 4");
        (await ged.GetAsync(a.Id)).Value!.Locked.Should().BeFalse("un refus libère le document pour qu'on le corrige");
        (await ged.AddVersionAsync(a.Id, new AddDocumentVersionRequest(Base64("avenant v2"), "application/pdf", "clause 4 corrigée"))).IsSuccess.Should().BeTrue();
        (await sig.SignAsync(r1.Id, r1.Signers[0].Id, new SignDocumentRequest(null))).Error!.Code.Should().Be("signature.closed");

        // Nouvelle demande sur la v2 ; un changement de version entre-temps rend la demande caduque.
        var r2 = (await sig.RequestAsync(a.Id, new CreateSignatureRequestRequest([new CreateSignerInput("user", Direction, null, null, null, null)]))).Value!;
        r2.VersionNo.Should().Be(2);
        (await sig.CancelAsync(r2.Id, "")).Error!.Code.Should().Be("signature.cancel_reason");
        (await sig.CancelAsync(r2.Id, "Mauvais signataire")).IsSuccess.Should().BeTrue();
        (await ged.GetAsync(a.Id)).Value!.Locked.Should().BeFalse();

        // Verrou posé pour une autre raison : il reste après l'annulation.
        var b = (await ged.CreateAsync(new CreateDocumentRequest(
            "Procès-verbal", "internal", null, null, null, null, null, null, null, null, Base64("pv"), "application/pdf"))).Value!.Document;
        (await ged.LockAsync(b.Id, "Contrôle fiscal")).IsSuccess.Should().BeTrue();
        var r3 = (await sig.RequestAsync(b.Id, new CreateSignatureRequestRequest([
            new CreateSignerInput("user", Direction, null, null, null, DateTimeOffset.UtcNow.AddHours(1))]))).Value!;
        await Sql($"UPDATE nexora.document_signer SET due_at = now() - interval '1 hour' WHERE signature_request_id = '{r3.Id}'");
        (await sig.ExpireDueAsync()).Should().BeGreaterThanOrEqualTo(1);
        (await sig.GetAsync(r3.Id)).Value!.Status.Should().Be("expired");
        var still = (await ged.GetAsync(b.Id)).Value!;
        still.Locked.Should().BeTrue();
        still.LockedReason.Should().Be("Contrôle fiscal");

        // Un document en revue doit d'abord être approuvé.
        var c = (await ged.CreateAsync(new CreateDocumentRequest(
            "Note interne", "internal", null, null, null, null, null, null, null, null, Base64("note"), "application/pdf"))).Value!.Document;
        (await ged.SubmitForReviewAsync(c.Id)).IsSuccess.Should().BeTrue();
        (await sig.RequestAsync(c.Id, new CreateSignatureRequestRequest([new CreateSignerInput("user", Direction, null, null, null, null)])))
            .Error!.Code.Should().Be("signature.document_status");
    }
}
