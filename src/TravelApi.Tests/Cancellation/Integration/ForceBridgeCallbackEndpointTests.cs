using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.3.6b (ADR-009 §2.12 round 3 + plan tactico FC1.3 §FC1.3.6b, 2026-05-22):
/// tests E2E del endpoint <c>POST /api/approvals/{publicId}/force-bridge-callback</c>.
///
/// <para>
/// <b>Escenario operativo</b>: el job <c>PartialCreditNoteBridgeReconciliationJob</c>
/// intento N veces reaplicar el callback del bridge sobre un
/// <c>PartialCreditNoteApproval</c> Approved/Rejected pero el BC quedo huerfano
/// en <c>ManualReviewPending</c>. El job agoto sus reintentos, notifico al
/// admin, y el admin necesita destrabar el caso manualmente.
/// </para>
///
/// <para>
/// <b>Pre-requisito</b>: el admin debe crear un <c>InvariantOverride</c> scoped
/// al target approval (EntityType=ApprovalRequest, EntityId=targetId), aprobarlo
/// por otro admin, y traer su publicId en el body.
/// </para>
///
/// <para>
/// <b>InMemory caveat</b>: usamos <see cref="CustomWebApplicationFactory"/> que
/// monta InMemoryDb. No validamos CHECK SQL crudos aca — esos los cubre el
/// bloque integration con Postgres real. Si que validamos: gating de permisos,
/// transiciones del BC, validacion del override scope, idempotencia no-op, y
/// audit trail.
/// </para>
/// </summary>
public class ForceBridgeCallbackEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ForceBridgeCallbackEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // =========================================================================
    // Helpers de setup (usuarios, permisos, BC en ManualReviewPending).
    // =========================================================================

    /// <summary>
    /// Crea un usuario con el rol "Admin" y los permisos minimos para que pueda
    /// invocar el endpoint: <c>CobranzasInvoiceAnnul</c>.
    /// </summary>
    private async Task<string> SeedAdminUserAsync(string suffix, bool withForcePermission = true)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleMgr.RoleExistsAsync("Admin"))
            await roleMgr.CreateAsync(new IdentityRole("Admin"));
        if (!await roleMgr.RoleExistsAsync("Vendedor"))
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));

        var userId = "admin-fbc-" + suffix;
        if (await userMgr.FindByIdAsync(userId) is null)
        {
            var user = new ApplicationUser
            {
                Id = userId,
                UserName = userId + "@t.local",
                Email = userId + "@t.local",
                FullName = "Admin Force Bridge " + suffix,
                IsActive = true,
            };
            await userMgr.CreateAsync(user, "Test1234!Aa");
            await userMgr.AddToRoleAsync(user, withForcePermission ? "Admin" : "Vendedor");
        }

        if (withForcePermission)
        {
            // El permiso vive en el rol Admin (que ya tiene casi todo) pero
            // por idempotencia lo agregamos explicitamente al rol "Admin" en BD.
            if (!await db.RolePermissions.AnyAsync(rp =>
                rp.RoleName == "Admin" && rp.Permission == Permissions.CobranzasInvoiceAnnul))
            {
                db.RolePermissions.Add(new RolePermission
                {
                    RoleName = "Admin",
                    Permission = Permissions.CobranzasInvoiceAnnul,
                });
                await db.SaveChangesAsync();
            }
        }

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();
        return userId;
    }

    /// <summary>
    /// Crea un BC en <c>ManualReviewPending</c> con un <c>PartialCreditNoteApproval</c>
    /// Approved asociado. Listo para que el bridge lo destrabe.
    /// </summary>
    /// <returns>El publicId del approval target + el id legacy del approval.</returns>
    private async Task<(Guid TargetApprovalPublicId, int TargetApprovalId, Guid BcPublicId)>
        SeedOrphanBcAsync(string suffix, string vendorUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = new Customer { FullName = "Cliente " + suffix, TaxCondition = "Consumidor Final", IsActive = true };
        var supplier = new Supplier { Name = "Sup " + suffix, IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO", InvoicingMode = SupplierInvoicingMode.TotalToCustomer };
        db.Customers.Add(customer);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Orph " + suffix,
            NumeroReserva = "ORPH-" + suffix,
            PayerId = customer.Id,
            ResponsibleUserId = vendorUserId,
            Status = EstadoReserva.Confirmed,
        };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        var invoice = new Invoice
        {
            ReservaId = reserva.Id,
            TipoComprobante = 1, // Factura A para forzar manual review
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            ImporteTotal = 100_000m,
            ImporteNeto = 82_644.63m,
            ImporteIva = 17_355.37m,
            Resultado = "A",
            CAE = "12345",
            VencimientoCAE = DateTime.UtcNow.AddDays(10),
            CreatedAt = DateTime.UtcNow,
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        // Approval ya Approved (el admin lo aprobo + el job de reconciliacion
        // intento reaplicar el callback varias veces sin exito).
        var approval = new ApprovalRequest
        {
            PublicId = Guid.NewGuid(),
            RequestType = ApprovalRequestType.PartialCreditNoteApproval,
            EntityType = "BookingCancellation",
            EntityId = 0, // se llena despues
            RequestedByUserId = vendorUserId,
            RequestedAt = DateTime.UtcNow.AddHours(-2),
            Status = ApprovalStatus.Approved,
            ResolvedByUserId = "admin-aprobador",
            ResolvedAt = DateTime.UtcNow.AddHours(-1),
            ResolverNotes = "Admin aprobo despues de revisar el caso fiscal con el contador en detalle",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Reason = "Manual review aprobada",
            Metadata = "{\"schemaVersion\":1}",
            BridgeRetryCount = 5,
            BridgeLastError = "Simulated: bridge fallo 5 veces antes",
            BridgeLastAttemptAt = DateTime.UtcNow.AddMinutes(-30),
        };
        db.ApprovalRequests.Add(approval);
        await db.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.ManualReviewPending,
            Reason = "Cliente cancela hotel 5 dias antes",
            DraftedAt = DateTime.UtcNow.AddHours(-3),
            DraftedByUserId = vendorUserId,
            DraftedByUserName = "Vendedor",
            AmountPaidAtCancellation = 100_000m,
            EstimatedRefundAmount = 100_000m,
            CreditNoteKind = CreditNoteKind.PartialOnOriginal,
            ReviewRequiredReason = ReviewRequiredReason.CustomerIsRiOrFacturaA,
            LiquidationComputedAt = DateTime.UtcNow.AddHours(-3),
            LiquidationComputedByUserId = vendorUserId,
            PartialCreditNoteApprovalRequestId = approval.Id,
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = "ARS",
                ExchangeRateAtOriginalInvoice = 1m,
                Source = ExchangeRateSource.BCRA_A3500,
                FetchedAt = DateTime.UtcNow.AddHours(-3),
                CustomerTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
                SupplierTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
                AgencyTaxConditionAtEvent = "MONOTRIBUTISTA",
            },
        };
        db.BookingCancellations.Add(bc);
        await db.SaveChangesAsync();

        // EntityId del approval apunta al BC.
        approval.EntityId = bc.Id;
        await db.SaveChangesAsync();

        return (approval.PublicId, approval.Id, bc.PublicId);
    }

    /// <summary>
    /// Crea el InvariantOverride scoped al target approval con reason valido
    /// (>= 50 chars + distinto del ResolverNotes del target).
    /// </summary>
    private async Task<Guid> SeedValidOverrideAsync(int targetApprovalId, string adminUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ovr = new ApprovalRequest
        {
            PublicId = Guid.NewGuid(),
            RequestType = ApprovalRequestType.InvariantOverride,
            EntityType = "ApprovalRequest",
            EntityId = targetApprovalId,
            RequestedByUserId = adminUserId,
            RequestedAt = DateTime.UtcNow,
            Status = ApprovalStatus.Approved,
            ResolvedByUserId = "other-admin",
            ResolvedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            Reason = "Force-callback aprobado para destrabar BC stuck por fallo recurrente del bridge integracion",
        };
        db.ApprovalRequests.Add(ovr);
        await db.SaveChangesAsync();
        return ovr.PublicId;
    }

    private void SetAdminHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Admin");
    }

    // =========================================================================
    // 1) Happy path: admin destraba BC stuck con override valido.
    // =========================================================================

    /// <summary>
    /// Happy path del force-callback: BC stuck en ManualReviewPending + AR
    /// Approved + override scoped correcto + reason >= 50 chars distinto.
    /// Resultado: HTTP 204, BC transiciona, BridgeRetryCount reseteado a 0.
    /// </summary>
    [Fact]
    public async Task ForceCallback_OrphanBCWithValidOverride_ForcesCallbackAndResetsCounter()
    {
        // ARRANGE — admin con permiso + BC stuck + override valido.
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var adminId = await SeedAdminUserAsync(suffix);
        var (targetPid, targetId, bcPid) = await SeedOrphanBcAsync(suffix, vendorUserId: "vendor-stuck");
        var overridePid = await SeedValidOverrideAsync(targetId, adminId);

        var client = _factory.CreateClient();
        SetAdminHeaders(client, adminId);

        // ACT
        var payload = new ForceBridgeCallbackRequest(
            ApprovalRequestOverridePublicId: overridePid,
            Reason: "Destrabamos BC stuck despues de confirmar con AFIP que el callback fallo por timeout transient");
        var resp = await client.PostAsJsonAsync($"/api/approvals/{targetPid}/force-bridge-callback", payload);

        // ASSERT — 204 NoContent + BC transiciono + counter reseteado.
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bc = await db.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == bcPid);
        // El bridge (OnApprovedAsync) deberia haber transicionado el BC. En Fase 1
        // se cae al path FC1.2 y va a AwaitingFiscalConfirmation.
        Assert.NotEqual(BookingCancellationStatus.ManualReviewPending, bc.Status);

        var targetAr = await db.ApprovalRequests.AsNoTracking().FirstAsync(a => a.PublicId == targetPid);
        // Counter reseteado a 0 + LastError limpio.
        Assert.Equal(0, targetAr.BridgeRetryCount);
        Assert.Null(targetAr.BridgeLastError);
        Assert.NotNull(targetAr.BridgeLastAttemptAt);

        // Audit log del force-callback presente.
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActions.BookingCancellationForceApprovalCallback)
            .OrderByDescending(a => a.Timestamp)
            .FirstAsync();
        Assert.Contains(targetPid.ToString(), audit.Changes ?? "");
    }

    // =========================================================================
    // 2) Override mal scoped (otra entidad).
    // =========================================================================

    /// <summary>
    /// INV-FC1.3-008: el override debe estar scoped a EntityType=ApprovalRequest +
    /// EntityId=targetApproval.Id. Si apunta a otra entidad (ej. BookingCancellation),
    /// el service rechaza con 409.
    /// </summary>
    [Fact]
    public async Task ForceCallback_OverrideWrongEntityType_Rejects()
    {
        // ARRANGE
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var adminId = await SeedAdminUserAsync(suffix);
        var (targetPid, targetId, _) = await SeedOrphanBcAsync(suffix, "vendor-x");

        // Override mal scoped: EntityType=BookingCancellation en lugar de ApprovalRequest.
        Guid overridePid;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ovr = new ApprovalRequest
            {
                PublicId = Guid.NewGuid(),
                RequestType = ApprovalRequestType.InvariantOverride,
                EntityType = "BookingCancellation", // <-- mal scoped
                EntityId = targetId,
                RequestedByUserId = adminId,
                Status = ApprovalStatus.Approved,
                ResolvedByUserId = "other-admin",
                ResolvedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
                Reason = "Override mal scoped para test de validacion del service force-callback",
            };
            db.ApprovalRequests.Add(ovr);
            await db.SaveChangesAsync();
            overridePid = ovr.PublicId;
        }

        var client = _factory.CreateClient();
        SetAdminHeaders(client, adminId);

        // ACT
        var payload = new ForceBridgeCallbackRequest(
            ApprovalRequestOverridePublicId: overridePid,
            Reason: "Intento usar override mal scoped para destrabar el flow del bridge fallido");
        var resp = await client.PostAsJsonAsync($"/api/approvals/{targetPid}/force-bridge-callback", payload);

        // ASSERT
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    // =========================================================================
    // 3) Reason del body == ResolverNotes del target.
    // =========================================================================

    /// <summary>
    /// INV-FC1.3-009 anti-copy-paste: el reason del body no puede ser identico
    /// al ResolverNotes del approval target. Fuerza al admin a explicar por que
    /// fuerza el callback, no a repetir el comentario original del approval.
    /// </summary>
    [Fact]
    public async Task ForceCallback_ReasonDuplicate_Rejects()
    {
        // ARRANGE — el ResolverNotes del target es exactamente lo que vamos a
        // mandar en el reason. Anti-copy-paste deberia rechazar.
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var adminId = await SeedAdminUserAsync(suffix);
        var (targetPid, targetId, _) = await SeedOrphanBcAsync(suffix, "vendor-x");
        var overridePid = await SeedValidOverrideAsync(targetId, adminId);

        // Leemos el ResolverNotes que sembramos en el helper.
        string resolverNotes;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            resolverNotes = (await db.ApprovalRequests.AsNoTracking()
                .FirstAsync(a => a.PublicId == targetPid)).ResolverNotes!;
        }

        var client = _factory.CreateClient();
        SetAdminHeaders(client, adminId);

        // ACT — reason copia exactamente el ResolverNotes.
        var payload = new ForceBridgeCallbackRequest(
            ApprovalRequestOverridePublicId: overridePid,
            Reason: resolverNotes);
        var resp = await client.PostAsJsonAsync($"/api/approvals/{targetPid}/force-bridge-callback", payload);

        // ASSERT
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    // =========================================================================
    // 4) BC ya transicionado: no-op idempotente.
    // =========================================================================

    /// <summary>
    /// Si el BC ya transiciono a otro estado (ej. el bridge real corrio bien
    /// despues de los reintentos), el endpoint es no-op idempotente: HTTP 204
    /// + audit BookingCancellationForceApprovalCallbackNoop.
    /// </summary>
    [Fact]
    public async Task ForceCallback_BcAlreadyTransitioned_NoopAudit()
    {
        // ARRANGE
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var adminId = await SeedAdminUserAsync(suffix);
        var (targetPid, targetId, bcPid) = await SeedOrphanBcAsync(suffix, "vendor-x");
        var overridePid = await SeedValidOverrideAsync(targetId, adminId);

        // Forzamos que el BC ya transiciono ANTES de invocar el endpoint
        // (simulando que el bridge real corrio por su cuenta).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bc = await db.BookingCancellations.FirstAsync(b => b.PublicId == bcPid);
            bc.Status = BookingCancellationStatus.ManualReviewApproved;
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        SetAdminHeaders(client, adminId);

        // ACT
        var payload = new ForceBridgeCallbackRequest(
            ApprovalRequestOverridePublicId: overridePid,
            Reason: "Force-callback sobre BC que ya transiciono, esperamos no-op idempotente con audit");
        var resp = await client.PostAsJsonAsync($"/api/approvals/{targetPid}/force-bridge-callback", payload);

        // ASSERT — 204 NoContent (no-op).
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bcAfter = await verifyDb.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == bcPid);
        Assert.Equal(BookingCancellationStatus.ManualReviewApproved, bcAfter.Status);

        // Audit no-op presente.
        var audit = await verifyDb.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActions.BookingCancellationForceApprovalCallbackNoop)
            .OrderByDescending(a => a.Timestamp)
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);
    }

    // =========================================================================
    // 5) Override expirado.
    // =========================================================================

    /// <summary>
    /// INV-FC1.3-008: el override debe estar vigente (ExpiresAt > UtcNow).
    /// </summary>
    [Fact]
    public async Task ForceCallback_OverrideExpired_Rejects()
    {
        // ARRANGE
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var adminId = await SeedAdminUserAsync(suffix);
        var (targetPid, targetId, _) = await SeedOrphanBcAsync(suffix, "vendor-x");

        // Override expirado.
        Guid overridePid;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ovr = new ApprovalRequest
            {
                PublicId = Guid.NewGuid(),
                RequestType = ApprovalRequestType.InvariantOverride,
                EntityType = "ApprovalRequest",
                EntityId = targetId,
                RequestedByUserId = adminId,
                Status = ApprovalStatus.Approved,
                ResolvedByUserId = "other-admin",
                ResolvedAt = DateTime.UtcNow.AddDays(-2),
                ExpiresAt = DateTime.UtcNow.AddHours(-1), // <-- expirado
                Reason = "Override expirado para test de la validacion service force-callback expirado",
            };
            db.ApprovalRequests.Add(ovr);
            await db.SaveChangesAsync();
            overridePid = ovr.PublicId;
        }

        var client = _factory.CreateClient();
        SetAdminHeaders(client, adminId);

        // ACT
        var payload = new ForceBridgeCallbackRequest(
            ApprovalRequestOverridePublicId: overridePid,
            Reason: "Intento usar override expirado para destrabar bridge, esperamos rechazo INV-FC1.3-008");
        var resp = await client.PostAsJsonAsync($"/api/approvals/{targetPid}/force-bridge-callback", payload);

        // ASSERT
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    // =========================================================================
    // 6) Usuario sin permiso.
    // =========================================================================

    /// <summary>
    /// Permiso requerido: <c>cobranzas.invoice_annul</c>. Sin ese permiso, el
    /// endpoint debe devolver 403 ANTES de cualquier procesamiento.
    /// </summary>
    [Fact]
    public async Task ForceCallback_NoPermission_Returns403()
    {
        // ARRANGE — usuario sin permiso force.
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var noPermUserId = await SeedAdminUserAsync(suffix, withForcePermission: false);
        var (targetPid, _, _) = await SeedOrphanBcAsync(suffix, "vendor-x");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, noPermUserId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");

        // ACT
        var payload = new ForceBridgeCallbackRequest(
            ApprovalRequestOverridePublicId: Guid.NewGuid(),
            Reason: "Intento de force-callback sin permiso, deberia devolver 403 sin importar el contenido");
        var resp = await client.PostAsJsonAsync($"/api/approvals/{targetPid}/force-bridge-callback", payload);

        // ASSERT
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // =========================================================================
    // 7) Approval no existe.
    // =========================================================================

    /// <summary>
    /// Si el publicId del approval no existe, el endpoint devuelve 404.
    /// </summary>
    [Fact]
    public async Task ForceCallback_NonExistentApproval_Returns404()
    {
        // ARRANGE
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var adminId = await SeedAdminUserAsync(suffix);

        var client = _factory.CreateClient();
        SetAdminHeaders(client, adminId);

        // ACT — publicId fake.
        var payload = new ForceBridgeCallbackRequest(
            ApprovalRequestOverridePublicId: Guid.NewGuid(),
            Reason: "Reason valido para test de approval que no existe, esperamos 404 directo del service");
        var resp = await client.PostAsJsonAsync($"/api/approvals/{Guid.NewGuid()}/force-bridge-callback", payload);

        // ASSERT
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
