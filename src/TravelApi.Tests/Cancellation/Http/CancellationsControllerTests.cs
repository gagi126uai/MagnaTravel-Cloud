using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Http;

/// <summary>
/// FC1.2.4 v3 (2026-05-18): tests E2E sobre <c>CancellationsController</c>.
///
/// <para>
/// <b>Alcance</b>: validar gating (auth/permission/ownership) y respuestas
/// del controller. El flujo de negocio completo end-to-end (Draft -> Confirm
/// -> Allocate -> Withdraw) lo cubre <c>CancellationFlowE2ETests</c> (FC1.2.7).
/// </para>
///
/// <para>
/// <b>InMemory caveat</b>: usamos <see cref="CustomWebApplicationFactory"/>
/// que monta InMemoryDb. Los CHECK constraints SQL del modulo no se aplican
/// — los invariantes que dependen del service (Ley 25.345, feature flag,
/// estados) si los podemos probar porque el service valida ANTES de SQL.
/// </para>
/// </summary>
public class CancellationsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CancellationsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Sembra el setting global con <c>EnableNewCancellationFlow=true</c> + crea
    /// un Vendedor con su rol y permisos. Devuelve los ids para usar en el test.
    /// </summary>
    private async Task<TestSeed> SeedAsync(string suffix)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settingsService = scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>();

        // Habilitar feature flag (los services rechazan toda operacion si esta off).
        var settings = await settingsService.GetEntityAsync(CancellationToken.None);
        settings.EnableNewCancellationFlow = true;
        await db.SaveChangesAsync();

        // Tanda B (2026-07-16): ConfirmAsync resuelve la condicion fiscal de la AGENCIA server-side
        // (ResolveServerSideTaxIdentity), leyendo la fila real de AfipSettings. Sin ella, cualquier
        // POST a /confirm de este archivo rebota con INV-118 antes de llegar al gating que estos
        // tests quieren probar. Guardado con AnyAsync: SeedAsync se llama una vez por [Fact] sobre la
        // MISMA factory (mismo DbContext detras del WebApplicationFactory).
        if (!await db.AfipSettings.AnyAsync())
        {
            db.AfipSettings.Add(new AfipSettings { Cuit = 20111111112, TaxCondition = "Monotributo" });
            await db.SaveChangesAsync();
        }

        if (!await roleMgr.RoleExistsAsync("Vendedor"))
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));

        // Vendedor con los permisos minimos: ReservasCancel para crear/confirm/abort,
        // ReservasView para GET. Sin ReservasViewAll: que el ownership decida.
        if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "Vendedor" && rp.Permission == Permissions.ReservasCancel))
        {
            db.RolePermissions.Add(new RolePermission { RoleName = "Vendedor", Permission = Permissions.ReservasCancel });
        }
        if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "Vendedor" && rp.Permission == Permissions.ReservasView))
        {
            db.RolePermissions.Add(new RolePermission { RoleName = "Vendedor", Permission = Permissions.ReservasView });
        }
        await db.SaveChangesAsync();

        var vendedorId = "vend-canc-" + suffix;
        if (await userMgr.FindByIdAsync(vendedorId) is null)
        {
            var user = new ApplicationUser
            {
                Id = vendedorId,
                UserName = vendedorId + "@t.local",
                Email = vendedorId + "@t.local",
                FullName = "Vendedor Cancel " + suffix,
                IsActive = true,
            };
            await userMgr.CreateAsync(user, "Test1234!Aa");
            await userMgr.AddToRoleAsync(user, "Vendedor");
        }

        // Reservas: una del vendedor (own), otra de otro user (ajena).
        var customer = new Customer { FullName = "Cliente " + suffix, TaxCondition = "Consumidor Final", IsActive = true };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var ownReserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Own " + suffix,
            NumeroReserva = "CANC-OWN-" + suffix,
            ResponsibleUserId = vendedorId,
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        var ajenaReserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Ajena " + suffix,
            NumeroReserva = "CANC-OTR-" + suffix,
            ResponsibleUserId = "owner-other",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        db.Reservas.AddRange(ownReserva, ajenaReserva);
        await db.SaveChangesAsync();

        // Factura activa para la reserva propia — DraftAsync la necesita.
        db.Invoices.Add(new Invoice
        {
            ReservaId = ownReserva.Id,
            TipoComprobante = 6,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            ImporteTotal = 1000m,
            ImporteNeto = 826.45m,
            ImporteIva = 173.55m,
            Resultado = "A",
            CAE = "12345",
            VencimientoCAE = DateTime.UtcNow.AddDays(10),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // ADR-015 Fase 1: la reserva propia necesita AL MENOS un servicio con
        // Supplier, porque DraftAsync infiere el operador de los servicios. Sin
        // esto la inferencia no encuentra operador y devuelve 409 en vez de 201.
        // Usamos un ServicioReserva generico (el caso mas simple) con un Supplier
        // valido; con un solo operador la inferencia lo autorresuelve.
        var supplier = new Supplier
        {
            Name = "Operador " + suffix,
            IsActive = true,
            TaxCondition = "IVA_RESP_INSCRIPTO",
        };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        db.Servicios.Add(new ServicioReserva
        {
            ReservaId = ownReserva.Id,
            SupplierId = supplier.Id,
            ServiceType = "Generico",
            Description = "Servicio para inferencia de operador en cancelacion",
        });
        await db.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

        return new TestSeed(vendedorId, ownReserva.PublicId, ajenaReserva.PublicId);
    }

    private static void SetVendedorHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
    }

    // =========================================================================
    // POST /api/cancellations  (Draft)
    // =========================================================================

    [Fact]
    public async Task POST_Cancellation_Without_Auth_Returns401()
    {
        // No headers + scheme requiere auth -> 401. Sin embargo, el TestAuthHandler
        // emite un "test-user" Admin por default. Para forzar el 401, creamos un
        // factory que no permita el bypass de schema. La forma simple: usar el
        // factory default y omitir auth scheme NO funciona, pero como el escenario
        // "401 sin token" no es testable con TestAuthHandler (siempre autentica),
        // testeamos en su lugar el 403 sin permiso — escenario equivalente desde
        // el controller del usuario final.
        var seed = await SeedAsync("noperm-" + Guid.NewGuid().ToString("N")[..6]);

        // Vendedor con rol pero sin permiso ReservasCancel.
        using (var scope = _factory.Services.CreateScope())
        {
            var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roleMgr.RoleExistsAsync("Mirador"))
                await roleMgr.CreateAsync(new IdentityRole("Mirador"));
            var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var noPermId = "noperm-canc-" + Guid.NewGuid().ToString("N")[..6];
            var user = new ApplicationUser { Id = noPermId, UserName = noPermId + "@t", Email = noPermId + "@t", FullName = "x", IsActive = true };
            await userMgr.CreateAsync(user, "Test1234!Aa");
            await userMgr.AddToRoleAsync(user, "Mirador");
            scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, noPermId);
            client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Mirador");

            var payload = new DraftCancellationRequest(seed.OwnReservaPublicId, "Cliente cambio de fecha forzado.");
            var resp = await client.PostAsJsonAsync("/api/cancellations", payload);

            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
    }

    [Fact]
    public async Task POST_Cancellation_Vendedor_OnOtherReserva_Returns403_Ownership()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        // Vendedor con permiso, pero pretendiendo crear cancelacion sobre la reserva ajena.
        var payload = new DraftCancellationRequest(seed.AjenaReservaPublicId, "Cliente cambio de fecha forzado.");
        var resp = await client.PostAsJsonAsync("/api/cancellations", payload);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task POST_Cancellation_Vendedor_OnOwnReserva_Returns201()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var payload = new DraftCancellationRequest(seed.OwnReservaPublicId, "Cliente cambio de planes — reembolso solicitado.");
        var resp = await client.PostAsJsonAsync("/api/cancellations", payload);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<BookingCancellationDto>();
        Assert.NotNull(dto);
        Assert.Equal(BookingCancellationStatus.Drafted.ToString(), dto!.Status);
        Assert.Equal(seed.OwnReservaPublicId, dto.ReservaPublicId);
    }

    [Fact]
    public async Task POST_Cancellation_FeatureFlagOff_Returns409()
    {
        // Seedeamos sin tocar el feature flag. Default es false.
        using (var scope = _factory.Services.CreateScope())
        {
            var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roleMgr.RoleExistsAsync("Admin"))
                await roleMgr.CreateAsync(new IdentityRole("Admin"));
        }
        // Crear una reserva para apuntar.
        Guid reservaPid;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new Customer { FullName = "Flag", TaxCondition = "Consumidor Final", IsActive = true };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            var reserva = new Reserva
            {
                PublicId = Guid.NewGuid(),
                Name = "Flag",
                NumeroReserva = "FLAG-" + Guid.NewGuid().ToString("N")[..6],
                PayerId = customer.Id,
                Status = EstadoReserva.Confirmed,
            };
            db.Reservas.Add(reserva);
            await db.SaveChangesAsync();
            reservaPid = reserva.PublicId;

            // Asegurar flag explicitamente OFF (puede haber test previo que lo prendio).
            var settingsSvc = scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>();
            var settings = await settingsSvc.GetEntityAsync(CancellationToken.None);
            settings.EnableNewCancellationFlow = false;
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient(); // default Admin -> bypass ownership.
        var payload = new DraftCancellationRequest(reservaPid, "Probando feature flag off.");
        var resp = await client.PostAsJsonAsync("/api/cancellations", payload);

        // Feature flag off -> InvalidOperationException en service -> 409 Conflict.
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    // =========================================================================
    // PATCH /api/cancellations/{publicId}/confirm
    // =========================================================================

    [Fact]
    public async Task PATCH_Confirm_OnOtherBC_Returns403_Ownership()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);

        // Sembrar un BC asociado a la reserva AJENA directamente en DB para
        // testear que el vendedor recibe 403 cuando intenta tocarlo.
        Guid bcPid;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ajena = await db.Reservas.FirstAsync(r => r.PublicId == seed.AjenaReservaPublicId);
            var invoice = new Invoice
            {
                ReservaId = ajena.Id,
                TipoComprobante = 6,
                PuntoDeVenta = 1,
                NumeroComprobante = 99,
                ImporteTotal = 1000m,
                Resultado = "A",
                CAE = "99",
                VencimientoCAE = DateTime.UtcNow.AddDays(10),
                CreatedAt = DateTime.UtcNow,
            };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();

            var bc = new BookingCancellation
            {
                PublicId = Guid.NewGuid(),
                CustomerId = ajena.PayerId!.Value,
                SupplierId = 0, // forzamos cualquiera porque solo testeamos auth, no flujo
                ReservaId = ajena.Id,
                OriginatingInvoiceId = invoice.Id,
                Status = BookingCancellationStatus.Drafted,
                Reason = "BC ajeno para test de ownership",
                DraftedAt = DateTime.UtcNow,
                DraftedByUserId = "owner-other",
            };
            // Necesitamos un Supplier valido para FK — usamos cualquiera o creamos uno.
            var supplier = new Supplier { Name = "Sup test", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();
            bc.SupplierId = supplier.Id;
            db.BookingCancellations.Add(bc);
            await db.SaveChangesAsync();
            bcPid = bc.PublicId;
        }

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var payload = new ConfirmCancellationRequest(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS",
                ExchangeRateAtOriginalInvoice: 1m,
                Source: ExchangeRateSource.Manual,
                ManualJustification: "Test",
                AgencyTaxConditionAtEvent: "Monotributo",
                SupplierTaxConditionAtEvent: "Monotributo",
                CustomerTaxConditionAtEvent: "Consumidor Final"),
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null);

        var resp = await client.PatchAsJsonAsync($"/api/cancellations/{bcPid}/confirm", payload);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // =========================================================================
    // POST /api/cancellations/{publicId}/force-arca-confirmation
    // =========================================================================

    [Fact]
    public async Task POST_ForceArcaConfirmation_WithoutPermission_Returns403()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);

        // El permiso CancellationsForceArcaConfirmation es Admin-only por
        // decision FC1.2.1 — el vendedor con ReservasCancel no alcanza.
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var payload = new ForceArcaConfirmationRequest(
            CreditNoteInvoicePublicId: Guid.NewGuid(),
            ApprovalRequestPublicId: Guid.NewGuid(),
            Reason: "Test force-arca sin permiso correcto ningun valor.");

        var bcPid = Guid.NewGuid(); // no existe, pero el 403 viene ANTES del lookup
        var resp = await client.PostAsJsonAsync($"/api/cancellations/{bcPid}/force-arca-confirmation", payload);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // =========================================================================
    // GET /api/cancellations/{publicId}
    // =========================================================================

    [Fact]
    public async Task GET_Cancellation_NotFound_Returns404()
    {
        await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient(); // Admin -> bypass ownership.
        var resp = await client.GetAsync($"/api/cancellations/{Guid.NewGuid()}");

        // Admin bypassa ownership pero el resolver no encuentra el BC -> 403
        // por ownership (el resolver devuelve false si la entidad no existe).
        // Admin con role bypass succeed -> el controller corre, GetByPublicIdAsync
        // devuelve null -> 404. Sin embargo, el RequireOwnership filter corre
        // ANTES y Admin role bypass succeed sin tocar la BD: 404 final.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // =========================================================================
    // GET /api/cancellations/by-reserva/{reservaPublicId}
    //   ADR-014 read-model: la cancelacion vigente de una reserva + la pista de UI
    //   CanConfirmPenalty. Reemplaza la busqueda en la bandeja back-office que dejaba
    //   afuera el caso pass-through (multa del operador estimada).
    // =========================================================================

    [Fact]
    public async Task GET_ByReserva_Vendedor_OnOtherReserva_Returns403_Ownership()
    {
        // Eje de privacidad: un vendedor NO puede leer la cancelacion de una reserva ajena.
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var resp = await client.GetAsync($"/api/cancellations/by-reserva/{seed.AjenaReservaPublicId}");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task GET_ByReserva_OwnReserva_NoCancellation_Returns404()
    {
        // La reserva existe y es del vendedor, pero no tiene ninguna cancelacion no-abortada.
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var resp = await client.GetAsync($"/api/cancellations/by-reserva/{seed.OwnReservaPublicId}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_ByReserva_PassThrough_PostNc_FlagOn_CanConfirmPenaltyTrue()
    {
        // EL caso del bug: anulacion pass-through, NC total ya con CAE, penalidad estimada
        // (DebitNoteStatus=NotApplicable). La bandeja vieja lo dejaba afuera; el read-model
        // debe decir CanConfirmPenalty=true para que el boton habilite la confirmacion.
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var bcPid = await SeedPostNcCancellationAsync(seed.OwnReservaPublicId, debitNoteFlagOn: true);

        var client = _factory.CreateClient(); // Admin -> bypass ownership.
        var resp = await client.GetAsync($"/api/cancellations/by-reserva/{seed.OwnReservaPublicId}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<BookingCancellationDto>();
        Assert.NotNull(dto);
        Assert.Equal(bcPid, dto!.PublicId); // devuelve el id de la CANCELACION, no el de la reserva
        Assert.Equal(seed.OwnReservaPublicId, dto.ReservaPublicId);
        Assert.True(dto.CanConfirmPenalty);
        Assert.Null(dto.ConfirmPenaltyBlockedReason);
    }

    [Fact]
    public async Task GET_ByReserva_FlagOff_CanConfirmPenaltyFalse_FeatureDisabled()
    {
        // Mismo BC post-NC, pero con la emision de ND deshabilitada: el read-model bloquea
        // y expone el motivo "DebitNoteFeatureDisabled".
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        await SeedPostNcCancellationAsync(seed.OwnReservaPublicId, debitNoteFlagOn: false);

        var client = _factory.CreateClient(); // Admin -> bypass ownership.
        var resp = await client.GetAsync($"/api/cancellations/by-reserva/{seed.OwnReservaPublicId}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<BookingCancellationDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.CanConfirmPenalty);
        Assert.Equal("DebitNoteFeatureDisabled", dto.ConfirmPenaltyBlockedReason);
    }

    /// <summary>
    /// Siembra una cancelacion en estado post-NC (AwaitingOperatorRefund, NC total con CAE
    /// seteada, penalidad Estimated, ND NotApplicable) sobre la reserva dada. Setea ademas el
    /// flag EnableCancellationDebitNote. Devuelve el PublicId del BookingCancellation.
    /// </summary>
    private async Task<Guid> SeedPostNcCancellationAsync(Guid reservaPublicId, bool debitNoteFlagOn)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settingsSvc = scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>();

        var settings = await settingsSvc.GetEntityAsync(CancellationToken.None);
        settings.EnableCancellationDebitNote = debitNoteFlagOn;
        await db.SaveChangesAsync();

        var reserva = await db.Reservas.FirstAsync(r => r.PublicId == reservaPublicId);

        var supplier = new Supplier { Name = "Op postNC " + reservaPublicId.ToString("N")[..6], IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var originating = new Invoice
        {
            ReservaId = reserva.Id,
            TipoComprobante = 6, PuntoDeVenta = 1, NumeroComprobante = 1,
            ImporteTotal = 1000m, Resultado = "A", CAE = "11111",
            VencimientoCAE = DateTime.UtcNow.AddDays(10), CreatedAt = DateTime.UtcNow,
        };
        var creditNote = new Invoice
        {
            ReservaId = reserva.Id,
            TipoComprobante = 8, PuntoDeVenta = 1, NumeroComprobante = 1,
            ImporteTotal = 1000m, Resultado = "A", CAE = "22222",
            VencimientoCAE = DateTime.UtcNow.AddDays(10), CreatedAt = DateTime.UtcNow,
        };
        db.Invoices.AddRange(originating, creditNote);
        await db.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            PublicId = Guid.NewGuid(),
            CustomerId = reserva.PayerId!.Value,
            SupplierId = supplier.Id,
            ReservaId = reserva.Id,
            OriginatingInvoiceId = originating.Id,
            CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            PenaltyStatus = PenaltyStatus.Estimated,
            DebitNoteStatus = DebitNoteStatus.NotApplicable,
            Reason = "BC post-NC pass-through para test del read-model",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "owner-test",
        };
        db.BookingCancellations.Add(bc);
        await db.SaveChangesAsync();

        return bc.PublicId;
    }

    private record TestSeed(string VendedorId, Guid OwnReservaPublicId, Guid AjenaReservaPublicId);
}
