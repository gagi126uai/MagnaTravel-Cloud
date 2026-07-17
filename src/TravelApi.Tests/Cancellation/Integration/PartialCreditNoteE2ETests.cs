using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.3 Fase 1 E2E (ADR-009 §6.3 + plan tactico FC1.3.7, 2026-05-22): tests
/// end-to-end del flujo NC parcial Hotel via HTTP (Draft + Confirm + Approve).
///
/// <para>
/// <b>Diferencia con ForceBridgeCallbackEndpointTests</b>: aca testeamos el
/// flow normal de cancelacion FC1.3 desde la UI del vendedor + la aprobacion
/// del admin. ForceBridgeCallback es el escape-hatch para cuando algo se
/// rompe — no es parte del happy path.
/// </para>
///
/// <para>
/// <b>InMemory caveat</b>: <see cref="CustomWebApplicationFactory"/> usa InMemoryDb
/// — no validamos CHECK SQL crudos ni xmin (esos los cubre el bloque
/// <c>BookingCancellationServicePartialCreditNoteIntegrationTests</c> con
/// Postgres real). Aca validamos: HTTP status codes, transiciones del BC,
/// shape de las respuestas, presencia de audit logs, y que el approval
/// queda correctamente vinculado al BC.
/// </para>
///
/// <para>
/// <b>Settings</b>: cada test setea <c>EnableNewCancellationFlow=true</c> +
/// <c>EnablePartialCreditNotes=true</c> via el service de settings ANTES de
/// invocar el endpoint, para que el flow FC1.3 este activo.
/// </para>
/// </summary>
public class PartialCreditNoteE2ETests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PartialCreditNoteE2ETests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // =========================================================================
    // Helpers de seed.
    // =========================================================================

    /// <summary>
    /// Bundle de seed con DOS usuarios distintos: <c>VendorUserId</c> es el vendedor
    /// que dibuja y confirma la cancelacion; <c>AdminUserId</c> es el admin que
    /// aprueba el approval.
    ///
    /// <para>
    /// <b>Por que dos usuarios y no uno solo</b>: el bridge FC1.3 enforce 4-eyes
    /// (INV-FC1.3-004) — el aprobador no puede ser el mismo que el solicitante
    /// del approval (que se setea con <c>DraftedByUserId</c> al momento del Draft).
    /// Si usaramos un solo userId para Draft + Approve, el bridge logueaba
    /// "aprobado por el mismo vendedor, bypass GR-005 no aplica" y dejaba el BC
    /// trabado en <c>ManualReviewPending</c> — el assert <c>AwaitingFiscalConfirmation</c>
    /// fallaria. Ver <c>BookingCancellationService.OnApprovedAsync</c>.
    /// </para>
    /// </summary>
    private record SeedBundle(string AdminUserId, string VendorUserId, Guid ReservaPublicId, int CustomerId, int SupplierId);

    /// <summary>
    /// Crea usuario admin + usuario vendedor con permisos minimos por rol +
    /// customer + supplier (modo configurable) + reserva Hotel + factura segun
    /// <paramref name="tipoComprobante"/>. Tambien activa los flags FC1.2 + FC1.3
    /// del setting global.
    ///
    /// <para>
    /// La reserva queda asignada al <c>VendorUserId</c> (no al admin). Asi el
    /// vendedor puede pasar el guard <c>RequireOwnership</c> al hacer Draft+Confirm,
    /// y despues el admin (que tiene <c>ApprovalsReview</c>) entra a aprobar.
    /// </para>
    /// </summary>
    private async Task<SeedBundle> SeedAsync(
        string suffix,
        int tipoComprobante = 6,
        SupplierInvoicingMode supplierMode = SupplierInvoicingMode.TotalToCustomer,
        decimal importeTotal = 100_000m)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settingsService = scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>();

        // Activar flags FC1.2 + FC1.3.
        var settings = await settingsService.GetEntityAsync(CancellationToken.None);
        settings.EnableNewCancellationFlow = true;
        settings.EnablePartialCreditNotes = true;
        await db.SaveChangesAsync();

        // Tanda B (2026-07-16): ConfirmAsync/ConfirmPartialCancellationEmissionAsync resuelven la
        // condicion fiscal de la AGENCIA server-side (ResolveServerSideTaxIdentity), leyendo la fila
        // real de AfipSettings. Sin ella, Confirm rebota con INV-118 antes de llegar al flujo FC1.3
        // que este test quiere ejercitar. Guardado con AnyAsync porque SeedAsync se llama una vez por
        // [Fact] sobre la MISMA factory (mismo DbContext detras del WebApplicationFactory).
        if (!await db.AfipSettings.AnyAsync())
        {
            db.AfipSettings.Add(new AfipSettings { Cuit = 20111111112, TaxCondition = "Monotributo" });
            await db.SaveChangesAsync();
        }

        if (!await roleMgr.RoleExistsAsync("Admin"))
            await roleMgr.CreateAsync(new IdentityRole("Admin"));
        if (!await roleMgr.RoleExistsAsync("Vendedor"))
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));

        // Permisos por rol. Admin = todo. Vendedor = solo lo necesario para
        // Draft + Confirm + ver su propia reserva. Asi simulamos roles reales
        // de produccion y el guard de 4-eyes del bridge no se rompe.
        var adminPermissions = new[]
        {
            Permissions.ReservasCancel,
            Permissions.ReservasView,
            Permissions.ReservasViewAll,
            Permissions.CobranzasInvoiceAnnul,
            Permissions.ApprovalsReview,
        };
        var vendorPermissions = new[]
        {
            Permissions.ReservasCancel,
            Permissions.ReservasView,
        };

        foreach (var perm in adminPermissions)
        {
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "Admin" && rp.Permission == perm))
            {
                db.RolePermissions.Add(new RolePermission { RoleName = "Admin", Permission = perm });
            }
        }
        foreach (var perm in vendorPermissions)
        {
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "Vendedor" && rp.Permission == perm))
            {
                db.RolePermissions.Add(new RolePermission { RoleName = "Vendedor", Permission = perm });
            }
        }
        await db.SaveChangesAsync();

        var adminId = "admin-e2e-" + suffix;
        if (await userMgr.FindByIdAsync(adminId) is null)
        {
            var user = new ApplicationUser
            {
                Id = adminId,
                UserName = adminId + "@t.local",
                Email = adminId + "@t.local",
                FullName = "Admin E2E " + suffix,
                IsActive = true,
            };
            await userMgr.CreateAsync(user, "Test1234!Aa");
            await userMgr.AddToRoleAsync(user, "Admin");
        }

        var vendorId = "vendor-e2e-" + suffix;
        if (await userMgr.FindByIdAsync(vendorId) is null)
        {
            var vendor = new ApplicationUser
            {
                Id = vendorId,
                UserName = vendorId + "@t.local",
                Email = vendorId + "@t.local",
                FullName = "Vendedor E2E " + suffix,
                IsActive = true,
            };
            await userMgr.CreateAsync(vendor, "Test1234!Aa");
            await userMgr.AddToRoleAsync(vendor, "Vendedor");
        }

        var customer = new Customer
        {
            FullName = "Cliente " + suffix,
            TaxCondition = tipoComprobante == 1 ? "IVA_RESP_INSCRIPTO" : "Consumidor Final",
            IsActive = true,
        };
        var supplier = new Supplier
        {
            Name = "Sup " + suffix,
            IsActive = true,
            TaxCondition = "IVA_RESP_INSCRIPTO",
            InvoicingMode = supplierMode,
        };
        db.Customers.Add(customer);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "E2E " + suffix,
            NumeroReserva = "E2E-" + suffix,
            // Reserva pertenece al vendedor — el ownership guard del Confirm chequea
            // que el caller sea el ResponsibleUserId (o tenga ReservasViewAll, que el
            // vendor NO tiene). Asi simulamos el flujo real: vendor abre, admin aprueba.
            ResponsibleUserId = vendorId,
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        var hotelService = new ServicioReserva
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            ProductType = ServiceTypes.Hotel,
            ServiceType = "Hotel",
            Description = "Hotel E2E",
            DepartureDate = DateTime.UtcNow.AddDays(15),
        };
        db.Set<ServicioReserva>().Add(hotelService);
        await db.SaveChangesAsync();

        var importeNeto = Math.Round(importeTotal / 1.21m, 2);
        var importeIva = importeTotal - importeNeto;
        var invoice = new Invoice
        {
            ReservaId = reserva.Id,
            TipoComprobante = tipoComprobante,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            ImporteTotal = importeTotal,
            ImporteNeto = importeNeto,
            ImporteIva = importeIva,
            Resultado = "A",
            CAE = "12345",
            VencimientoCAE = DateTime.UtcNow.AddDays(10),
            CreatedAt = DateTime.UtcNow,
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        db.Set<InvoiceItem>().Add(new InvoiceItem
        {
            InvoiceId = invoice.Id,
            Description = "Hotel 5 noches",
            Quantity = 1,
            UnitPrice = importeTotal,
            Total = importeTotal,
            AlicuotaIvaId = 5,
            ImporteIva = importeIva,
            IsRefundable = true,
            ItemCategory = InvoiceItemCategory.Service,
            SourceServicioReservaId = hotelService.Id,
        });
        await db.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

        return new SeedBundle(adminId, vendorId, reserva.PublicId, customer.Id, supplier.Id);
    }

    /// <summary>
    /// Setea los headers de autenticacion del <see cref="TestAuthHandler"/> con
    /// el <paramref name="userId"/> + <paramref name="role"/> indicados. Limpia
    /// los valores previos para que llamadas sucesivas no acumulen.
    ///
    /// <para>
    /// El handler emite <c>NameIdentifier = userId</c> + <c>Role = role</c>. Las
    /// claims de permisos se resuelven server-side via <c>IUserPermissionResolver</c>
    /// (no por header) — por eso solo necesitamos pasar userId + role.
    /// </para>
    /// </summary>
    private void SetUserHeaders(HttpClient client, string userId, string role)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, role);
    }

    /// <summary>
    /// Atajo retro-compatible: setea headers como Admin. Equivale a
    /// <c>SetUserHeaders(client, userId, "Admin")</c>.
    /// </summary>
    private void SetAdminHeaders(HttpClient client, string userId) =>
        SetUserHeaders(client, userId, "Admin");

    /// <summary>
    /// Crea un HttpClient con <see cref="IInvoiceService"/> reemplazado por un Mock
    /// no-op SOLO para los tests que ejercitan el happy path del bridge (admin aprueba
    /// approval y el bridge transiciona el BC).
    ///
    /// <para>
    /// <b>Por que el mock per-test y no en el factory global</b>: el factory base se
    /// comparte con tests B1.15 (anulacion de facturas) que dependen del service real
    /// para validar los guards de approval y de tipos fiscales. Mockearlo globalmente
    /// rompe esos guards y los tests pasan en falso. Ver doc explicativa
    /// 2026-05-22 (FC1.3 hang post-merge) y comentario en
    /// PostgresIntegrationFixture sobre la misma estrategia.
    /// </para>
    ///
    /// <para>
    /// <b>Por que solo en el happy path</b>: el flow E2E que pasa por el Approve
    /// dispara el bridge -> <c>InvoiceService.EnqueueAnnulmentAsync</c>. Con
    /// InMemoryDb la cadena sincronica del service real (lectura de settings +
    /// RequiresApprovalAsync + SaveChanges sobre Invoice) se cuelga de forma
    /// deterministica. Los tests que rechazan en Confirm (caso 4 GR-001) NO llegan
    /// al Approve y por eso usan <c>_factory.CreateClient()</c> directo.
    /// </para>
    /// </summary>
    private HttpClient CreateClientWithInvoiceServiceMock()
    {
        var factoryWithMock = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                // Mantenemos Scoped para paridad con la registracion real en
                // TravelApi/Program.cs (IInvoiceService). El Mock es stateless,
                // asi que el scope no afecta correctitud — solo evita divergencias
                // innecesarias en la inspeccion del container.
                var descriptor = services.SingleOrDefault(s => s.ServiceType == typeof(IInvoiceService));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddScoped<IInvoiceService>(_ => new Mock<IInvoiceService>().Object);
            }));
        return factoryWithMock.CreateClient();
    }

    private static ConfirmCancellationRequest BuildValidConfirm() =>
        new ConfirmCancellationRequest(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS",
                ExchangeRateAtOriginalInvoice: 1m,
                Source: ExchangeRateSource.BCRA_A3500,
                ManualJustification: null,
                AgencyTaxConditionAtEvent: "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent: "IVA_RESP_INSCRIPTO",
                CustomerTaxConditionAtEvent: "CONSUMIDOR_FINAL"),
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null);

    // =========================================================================
    // 1) Caso 8 (Factura A) — happy path completo.
    // =========================================================================

    /// <summary>
    /// ADR-009 §6.3 escenario A textual del contador: cliente paga con Factura A,
    /// cancela. El sistema detecta Case8_FacturaA, abre approval, admin distinto
    /// edita (opcional), admin aprueba, BC transiciona a AwaitingFiscalConfirmation.
    ///
    /// Importa al negocio porque es el escenario MAS comun de NC parcial: factura A
    /// va SIEMPRE a manual review.
    /// </summary>
    [Fact]
    public async Task E2E_Case8_FacturaA_HappyPath()
    {
        // ARRANGE — Factura A.
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var seed = await SeedAsync(suffix, tipoComprobante: 1, importeTotal: 100_000m);
        // Happy path -> approve dispara el bridge -> EnqueueAnnulmentAsync.
        // Necesitamos el mock de IInvoiceService para evitar el hang con InMemoryDb
        // (ver helper).
        var client = CreateClientWithInvoiceServiceMock();
        // Primero el vendedor: el Draft + Confirm los hace el "Vendedor" para que
        // DraftedByUserId quede con su id (no con el del admin). Si los hiciera
        // el mismo admin que despues aprueba, el bridge enforce 4-eyes y deja el
        // BC en ManualReviewPending — el assert AwaitingFiscalConfirmation rompe.
        SetUserHeaders(client, seed.VendorUserId, "Vendedor");

        // ACT 1 — Draft (lo emite el vendedor).
        var draftReq = new DraftCancellationRequest(seed.ReservaPublicId, "Cliente cambio de planes por motivos personales");
        var draftResp = await client.PostAsJsonAsync("/api/cancellations", draftReq);
        Assert.Equal(HttpStatusCode.Created, draftResp.StatusCode);
        var draftDto = await draftResp.Content.ReadFromJsonAsync<BookingCancellationDto>();
        Assert.NotNull(draftDto);
        Assert.Equal(BookingCancellationStatus.Drafted.ToString(), draftDto!.Status);

        // ACT 2 — Confirm (lo emite el vendedor). El calculator detecta Factura A -> ManualReviewPending.
        var confirmResp = await client.PatchAsJsonAsync(
            $"/api/cancellations/{draftDto.PublicId}/confirm", BuildValidConfirm());
        Assert.Equal(HttpStatusCode.OK, confirmResp.StatusCode);
        var confirmDto = await confirmResp.Content.ReadFromJsonAsync<BookingCancellationDto>();
        Assert.NotNull(confirmDto);
        Assert.Equal(BookingCancellationStatus.ManualReviewPending.ToString(), confirmDto!.Status);

        // Verificamos el approval que el service creo automaticamente.
        Guid approvalPublicId;
        using (var verifyScope = _factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bc = await db.BookingCancellations.AsNoTracking()
                .Include(b => b.PartialCreditNoteApprovalRequest)
                .FirstAsync(b => b.PublicId == draftDto.PublicId);
            Assert.NotNull(bc.PartialCreditNoteApprovalRequest);
            Assert.Equal(ApprovalRequestType.PartialCreditNoteApproval, bc.PartialCreditNoteApprovalRequest!.RequestType);
            Assert.Equal(ApprovalStatus.Pending, bc.PartialCreditNoteApprovalRequest.Status);
            Assert.True(bc.ReviewRequiredReason.HasFlag(ReviewRequiredReason.CustomerIsRiOrFacturaA));
            approvalPublicId = bc.PartialCreditNoteApprovalRequest.PublicId;
        }

        // Cambiamos identidad al admin para el Approve. Asi el bridge ve
        // approver (admin) != drafter (vendor) y no enforce GR-005.
        SetAdminHeaders(client, seed.AdminUserId);

        // ACT 3 — Admin aprueba el approval via endpoint generico.
        // Comment >= 20 chars (no es threshold-accounting con monto 100k).
        var approveResp = await client.PostAsJsonAsync(
            $"/api/approvals/{approvalPublicId}/approve",
            new { Notes = "Aprobado segun criterio del contador para factura A confirmado por mail" });

        // En produccion, ApproveAsync internamente invoca el bridge que transiciona el BC.
        // El status code esperado del approve es 200.
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        // ASSERT — el BC paso a AwaitingFiscalConfirmation (path FC1.2 Fase 1).
        using (var verifyScope2 = _factory.Services.CreateScope())
        {
            var db = verifyScope2.ServiceProvider.GetRequiredService<AppDbContext>();
            var bcAfter = await db.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == draftDto.PublicId);
            Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bcAfter.Status);
            Assert.NotNull(bcAfter.ManualReviewerUserId);
            Assert.NotNull(bcAfter.ManualReviewedAt);
        }
    }

    // =========================================================================
    // 2) Caso 5 (CommissionOnly) — GR-003.
    // =========================================================================

    /// <summary>
    /// GR-003: Supplier en modo CommissionOnly (intermediario). El calculator
    /// hace early-exit y dispara InvoicingModeCommissionOnly. El admin
    /// aprueba sin modificar (el contador clarifico que en intermediario la
    /// penalty no reduce la NC) y el BC transiciona.
    /// </summary>
    [Fact]
    public async Task E2E_Case5_CommissionOnly_AdminAcceptsAndProcesses()
    {
        // ARRANGE — Factura B + supplier CommissionOnly.
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var seed = await SeedAsync(suffix, tipoComprobante: 6,
            supplierMode: SupplierInvoicingMode.CommissionOnly, importeTotal: 200_000m);
        // Happy path -> approve dispara el bridge -> EnqueueAnnulmentAsync.
        // Necesitamos el mock de IInvoiceService para evitar el hang con InMemoryDb
        // (ver helper).
        var client = CreateClientWithInvoiceServiceMock();
        // Vendedor abre la cancelacion (Draft + Confirm). Asi DraftedByUserId
        // queda con el id del vendor y el admin puede aprobar despues sin que
        // el bridge enforce 4-eyes (GR-005). Ver comentario en SeedBundle.
        SetUserHeaders(client, seed.VendorUserId, "Vendedor");

        // Draft + Confirm.
        var draftReq = new DraftCancellationRequest(seed.ReservaPublicId, "Cancelacion modo intermediario");
        var draftResp = await client.PostAsJsonAsync("/api/cancellations", draftReq);
        var draftDto = (await draftResp.Content.ReadFromJsonAsync<BookingCancellationDto>())!;

        var confirmResp = await client.PatchAsJsonAsync(
            $"/api/cancellations/{draftDto.PublicId}/confirm", BuildValidConfirm());
        var confirmDto = (await confirmResp.Content.ReadFromJsonAsync<BookingCancellationDto>())!;
        Assert.Equal(BookingCancellationStatus.ManualReviewPending.ToString(), confirmDto.Status);

        // Verificar que el flag InvoicingModeCommissionOnly esta puesto.
        Guid approvalPublicId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bc = await db.BookingCancellations.AsNoTracking()
                .Include(b => b.PartialCreditNoteApprovalRequest)
                .FirstAsync(b => b.PublicId == draftDto.PublicId);
            Assert.True(bc.ReviewRequiredReason.HasFlag(ReviewRequiredReason.InvoicingModeCommissionOnly));
            approvalPublicId = bc.PartialCreditNoteApprovalRequest!.PublicId;
        }

        // Cambio de identidad: admin distinto del vendor para el Approve.
        // Asi el bridge ve approver (admin) != drafter (vendor) y no enforce
        // GR-005 — el BC transiciona limpio a AwaitingFiscalConfirmation.
        SetAdminHeaders(client, seed.AdminUserId);

        // ACT — admin aprueba sin modificar.
        var approveResp = await client.PostAsJsonAsync(
            $"/api/approvals/{approvalPublicId}/approve",
            new { Notes = "Operador es intermediario, la penalidad no reduce NC segun criterio contador" });
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        // ASSERT — BC transiciono.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bcAfter = await db.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == draftDto.PublicId);
            Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bcAfter.Status);
        }
    }

    // =========================================================================
    // 3) Caso 4 (TotalPlusNewInvoice) — GR-001 rechazo explicito.
    // =========================================================================

    /// <summary>
    /// GR-001: si el calculator devuelve <c>TotalPlusNewInvoice</c> (caso 4 o 7),
    /// el Confirm DEBE rechazar 409 con un mensaje de NEGOCIO ("requiere revisión manual"),
    /// sin jerga interna en el body (FUGA B2 data-exposure 2026-07-03; el detalle tecnico
    /// va al log). El BC queda en Drafted (rollback EF).
    ///
    /// <para><b>Como forzamos el caso 4</b>: necesitamos que la heuristica
    /// OriginalInvoiceUnclear se dispare. La heuristica esta OFF por default
    /// (RH-008). La activamos via setting <c>GenericDescriptionPatterns</c> +
    /// describir el item con un pattern que matchee.</para>
    /// </summary>
    [Fact]
    public async Task E2E_Case4_TotalPlusNewInvoice_RejectsWithExplicitError()
    {
        // ARRANGE — Factura B + heuristica activada via setting + item con
        // descripcion generica que matchee el regex.
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var seed = await SeedAsync(suffix, tipoComprobante: 6, importeTotal: 200_000m);

        // Activar heuristica OriginalInvoiceUnclear.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settingsSvc = scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>();
            var settings = await settingsSvc.GetEntityAsync(CancellationToken.None);
            // Pattern que matchea "Hotel 5 noches" (palabra "hotel").
            settings.GenericDescriptionPatterns = @"\b(hotel|servicios?)\b";
            // Reseteamos Fc13DeployDate = null defensivamente. El startup validator
            // del factory base lo auto-setea a DateTime.UtcNow cuando se prende
            // EnablePartialCreditNotes — si otros tests previos (Case5 / Case8)
            // disparan ese startup en su factory hija, queda persistido en la
            // InMemoryDb compartida. Acá no usamos LegacyInvoice (la heuristica
            // que mira Fc13DeployDate), pero lo limpiamos para que el test sea
            // autocontenido y no dependa del orden de ejecucion.
            settings.Fc13DeployDate = null;
            await db.SaveChangesAsync();

            // Modificamos el InvoiceItem para que tenga UNA SOLA linea con
            // descripcion que matchea (sub-heuristica 1: 1 item + descripcion generica).
            var reservaEntity = await db.Reservas.FirstAsync(r => r.PublicId == seed.ReservaPublicId);
            var invoice = await db.Invoices.FirstAsync(i => i.ReservaId == reservaEntity.Id);
            var item = await db.Set<InvoiceItem>().FirstAsync(it => it.InvoiceId == invoice.Id);
            item.Description = "Hotel"; // matchea regex
            await db.SaveChangesAsync();
        }

        // Usamos el helper con mock de IInvoiceService aunque ESTE test rechace
        // en Confirm (GR-001). Razon: si por cualquier motivo el calculator NO
        // detecta TotalPlusNewInvoice (por ej. el setting Fc13DeployDate quedo
        // contaminado por un test previo y el item description matchea distinto),
        // el flow cae al path normal FC1.2 ConfirmAsync, que invoca
        // InvoiceService.EnqueueAnnulmentAsync — y ese path con InMemoryDb +
        // service real se cuelga de forma deterministica (mismo hang que motivo
        // el primer fix de este archivo). El mock per-test es seguro: si el
        // GR-001 dispara antes de llegar al bridge (como deberia), el mock NUNCA
        // se invoca; si no dispara, evitamos el hang y el assert de status
        // detecta la regresion. Belt + suspenders.
        var client = CreateClientWithInvoiceServiceMock();
        // El vendedor dibuja la cancelacion; el rechazo por GR-001 ocurre en
        // Confirm antes de llegar al Approve, asi que no necesitamos cambiar a
        // admin en este test. Igual usamos el vendor por consistencia.
        SetUserHeaders(client, seed.VendorUserId, "Vendedor");

        // Draft OK.
        var draftReq = new DraftCancellationRequest(seed.ReservaPublicId, "Test caso 4 factura confusa");
        var draftResp = await client.PostAsJsonAsync("/api/cancellations", draftReq);
        var draftDto = (await draftResp.Content.ReadFromJsonAsync<BookingCancellationDto>())!;

        // ACT — Confirm debe rechazar 409.
        var confirmResp = await client.PatchAsJsonAsync(
            $"/api/cancellations/{draftDto.PublicId}/confirm", BuildValidConfirm());

        // ASSERT
        Assert.Equal(HttpStatusCode.Conflict, confirmResp.StatusCode);
        var body = await confirmResp.Content.ReadAsStringAsync();
        // FUGA B2 data-exposure (2026-07-03): el body ya NO expone jerga interna (CreditNoteKind /
        // EnablePartialCreditNotes / FC1.3 / flujo legacy); al usuario le llega el mensaje de negocio.
        Assert.Contains("requiere revisión manual", body);
        Assert.DoesNotContain("CreditNoteKind", body);
        Assert.DoesNotContain("EnablePartialCreditNotes", body);
        Assert.DoesNotContain("FC1.3", body);

        // El BC sigue en Drafted (rollback EF porque el throw es antes de SaveChanges FC1.3).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bc = await db.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == draftDto.PublicId);
            Assert.Equal(BookingCancellationStatus.Drafted, bc.Status);
            Assert.Null(bc.CreditNoteKind);
            Assert.Null(bc.LiquidationComputedAt);
        }
    }
}
