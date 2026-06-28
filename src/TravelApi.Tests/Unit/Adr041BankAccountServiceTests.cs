using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-041 (cuentas bancarias polimorficas, 2026-06-27): cobertura del servicio. Cubre: validacion server-side
/// de obligatorios y de EXISTENCIA del dueño, forzado de OwnerId=0 para la Agencia, masking de CBU y alias en
/// la lista, soft-delete, que la edicion NO cambia de dueño, y el mapeo de permiso por dueño.
///
/// <para>NOTA sobre el CHECK SQL <c>chk_BankAccounts_cbu_or_alias</c>: el provider InMemory NO enforcea CHECK
/// constraints, asi que aca se testea que el SERVICIO rechaza el caso (alias+cbu ambos null) ANTES de
/// persistir. El CHECK es defensa en profundidad a nivel BD (Postgres) y se valida en integracion, no aca.</para>
/// </summary>
public class Adr041BankAccountServiceTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static BankAccountService NewService(AppDbContext ctx) =>
        new(ctx, new Mock<IAuditService>().Object);

    // El dueño viaja por la API como su PublicId (GUID), no como el Id interno. Para que los tests sean legibles
    // generamos PublicIds DETERMINISTAS a partir del Id interno: asi "el proveedor 7" tiene siempre el mismo GUID.
    // El cuarto grupo del GUID distingue Proveedor (0001) de Cliente (0002); el ultimo grupo lleva el Id en decimal
    // (los digitos decimales tambien son hex validos).
    private static Guid SupplierPublicId(int internalId) => new($"00000000-0000-0000-0001-{internalId:000000000000}");
    private static Guid CustomerPublicId(int internalId) => new($"00000000-0000-0000-0002-{internalId:000000000000}");
    private static string SupplierToken(int internalId) => SupplierPublicId(internalId).ToString();
    private static string CustomerToken(int internalId) => CustomerPublicId(internalId).ToString();

    // La validacion de existencia del dueño exige que el Cliente/Proveedor exista y este activo. Sembramos un
    // set estandar (con su PublicId determinista) para que los caminos felices puedan crear cuentas.
    private static void SeedStandardOwners(AppDbContext ctx)
    {
        ctx.Suppliers.Add(new Supplier { Id = 1, PublicId = SupplierPublicId(1), Name = "Proveedor 1", IsActive = true });
        ctx.Suppliers.Add(new Supplier { Id = 5, PublicId = SupplierPublicId(5), Name = "Proveedor 5", IsActive = true });
        ctx.Suppliers.Add(new Supplier { Id = 7, PublicId = SupplierPublicId(7), Name = "Proveedor 7", IsActive = true });
        ctx.Customers.Add(new Customer { Id = 7, PublicId = CustomerPublicId(7), FullName = "Cliente 7", IsActive = true });
        ctx.SaveChanges();
    }

    // El token de dueño por defecto: el PublicId del proveedor 1 (el dueño "estandar" de la mayoria de los tests).
    // Para Agencia mandamos un "0" (se ignora). Los tests que usan otro dueño pasan su token explicito.
    private static string DefaultOwnerToken(BankAccountOwnerType ownerType) => ownerType switch
    {
        BankAccountOwnerType.Supplier => SupplierToken(1),
        BankAccountOwnerType.Customer => CustomerToken(7),
        _ => "0",
    };

    private static BankAccountUpsertRequest ValidRequest(
        BankAccountOwnerType ownerType = BankAccountOwnerType.Supplier,
        string? ownerToken = null,
        string? cbu = "0123456789012345678901",
        string? alias = null,
        string holderName = "Operador Mayorista SA",
        string currency = "ARS",
        bool isPrimary = false) =>
        new(
            OwnerType: ownerType,
            OwnerId: ownerToken ?? DefaultOwnerToken(ownerType),
            Cbu: cbu,
            Alias: alias,
            HolderName: holderName,
            Currency: currency,
            Bank: "Banco Nacion",
            AccountType: BankAccountType.CuentaCorriente,
            HolderTaxId: "30123456789",
            Notes: null,
            IsPrimary: isPrimary);

    // ============================================================
    // Validacion server-side (no confiar en el front)
    // ============================================================

    [Fact]
    public async Task Create_rechaza_cuando_cbu_y_alias_son_ambos_null()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var request = ValidRequest(cbu: null, alias: null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(request, "user-1", "User", CancellationToken.None));
    }

    [Fact]
    public async Task Create_acepta_solo_alias_sin_cbu()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var request = ValidRequest(cbu: null, alias: "mi.alias.banco");
        var created = await service.CreateAsync(request, "user-1", "User", CancellationToken.None);

        // La respuesta de la ESCRITURA viene enmascarada (sin CBU, alias tapado). Para verificar lo PERSISTIDO
        // (el alias completo) usamos el GET de detalle, que es la unica via que devuelve el dato en claro.
        Assert.Null(created.CbuMasked);
        var detail = await service.GetByPublicIdAsync(created.PublicId, CancellationToken.None);
        Assert.Null(detail!.Cbu);
        Assert.Equal("mi.alias.banco", detail.Alias);
    }

    [Fact]
    public async Task Create_rechaza_titular_vacio()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var request = ValidRequest(holderName: "   ");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(request, "user-1", "User", CancellationToken.None));
    }

    [Fact]
    public async Task Create_rechaza_moneda_vacia()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var request = ValidRequest(currency: "");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(request, "user-1", "User", CancellationToken.None));
    }

    [Fact]
    public async Task Create_rechaza_moneda_no_soportada()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var request = ValidRequest(currency: "EUR");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(request, "user-1", "User", CancellationToken.None));
    }

    [Fact]
    public async Task Create_normaliza_moneda_a_mayuscula()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(currency: "usd"), "user-1", "User", CancellationToken.None);

        Assert.Equal("USD", created.Currency);
    }

    [Theory]
    [InlineData("12345")]                    // muy corto
    [InlineData("012345678901234567890")]    // 21 digitos
    [InlineData("01234567890123456789012")]  // 23 digitos
    [InlineData("01234567890123456789AB")]   // con letras
    public async Task Create_rechaza_cbu_con_formato_invalido(string badCbu)
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var request = ValidRequest(cbu: badCbu);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(request, "user-1", "User", CancellationToken.None));
    }

    [Fact]
    public async Task Create_acepta_cbu_de_22_digitos()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(cbu: "0123456789012345678901"), "user-1", "User", CancellationToken.None);

        // El CBU completo se verifica por el GET de detalle (la escritura responde enmascarada).
        var detail = await service.GetByPublicIdAsync(created.PublicId, CancellationToken.None);
        Assert.Equal("0123456789012345678901", detail!.Cbu);
    }

    // ============================================================
    // Seguridad: las ESCRITURAS no devuelven el dato bancario en claro
    // ============================================================

    [Fact]
    public async Task Create_responde_con_el_cbu_enmascarado_no_en_claro()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(cbu: "0123456789012345678901"), "user-1", "User", CancellationToken.None);

        // La respuesta del alta NO debe traer el CBU completo (solo los ultimos 4, el resto tapado).
        Assert.NotNull(created.CbuMasked);
        Assert.EndsWith("8901", created.CbuMasked!);
        Assert.DoesNotContain("012345678901234567", created.CbuMasked);
    }

    [Fact]
    public async Task Update_responde_con_el_cbu_enmascarado_no_en_claro()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(cbu: "0123456789012345678901"), "user-1", "User", CancellationToken.None);

        var updated = await service.UpdateAsync(
            created.PublicId,
            ValidRequest(cbu: "0123456789012345678901"),
            "user-1", "User", CancellationToken.None);

        Assert.NotNull(updated.CbuMasked);
        Assert.EndsWith("8901", updated.CbuMasked!);
        Assert.DoesNotContain("012345678901234567", updated.CbuMasked);
    }

    [Fact]
    public async Task SetPrimary_responde_con_el_cbu_enmascarado_no_en_claro()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),cbu: "0123456789012345678901"), "user-1", "User", CancellationToken.None);

        var result = await service.SetPrimaryAsync(created.PublicId, "user-1", "User", CancellationToken.None);

        Assert.NotNull(result.CbuMasked);
        Assert.EndsWith("8901", result.CbuMasked!);
        Assert.DoesNotContain("012345678901234567", result.CbuMasked);
        Assert.True(result.IsPrimary);
    }

    [Theory]
    [InlineData("abc")]            // muy corto (< 6)
    [InlineData(".empieza.punto")] // empieza con punto
    [InlineData("termina.punto.")] // termina con punto
    [InlineData("con espacio")]    // espacio no permitido
    public async Task Create_rechaza_alias_con_formato_invalido(string badAlias)
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var request = ValidRequest(cbu: null, alias: badAlias);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(request, "user-1", "User", CancellationToken.None));
    }

    // ============================================================
    // Validacion de EXISTENCIA del dueño + forzado de Agency
    // ============================================================

    [Fact]
    public async Task Create_rechaza_owner_inexistente()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        // Proveedor 999 no fue sembrado.
        var request = ValidRequest(ownerType: BankAccountOwnerType.Supplier, ownerToken: SupplierToken(999));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(request, "user-1", "User", CancellationToken.None));
    }

    [Fact]
    public async Task Create_rechaza_owner_inactivo()
    {
        using var ctx = NewContext();
        ctx.Customers.Add(new Customer { Id = 42, PublicId = CustomerPublicId(42), FullName = "Cliente inactivo", IsActive = false });
        ctx.SaveChanges();
        var service = NewService(ctx);

        var request = ValidRequest(ownerType: BankAccountOwnerType.Customer, ownerToken: CustomerToken(42));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(request, "user-1", "User", CancellationToken.None));
    }

    [Fact]
    public async Task Create_de_la_agencia_fuerza_owner_id_0_ignorando_el_body()
    {
        using var ctx = NewContext();
        var service = NewService(ctx);

        // La Agencia es singleton: aunque el body mande 999, el OwnerId persistido debe ser 0. No se valida
        // existencia (no hay tabla "Agency"), por eso NO sembramos nada.
        var request = ValidRequest(ownerType: BankAccountOwnerType.Agency, ownerToken: "999");
        var created = await service.CreateAsync(request, "user-1", "User", CancellationToken.None);

        Assert.Equal(BankAccountOwnerType.Agency, created.OwnerType);

        // El OwnerId interno YA NO viaja en la respuesta (hardening 2026-06-28), asi que verificamos el invariante
        // "la Agencia fuerza OwnerId=0 ignorando el body" contra la FILA PERSISTIDA, no contra el DTO de respuesta.
        var persisted = ctx.BankAccounts.Single(a => a.PublicId == created.PublicId);
        Assert.Equal(0, persisted.OwnerId);
    }

    // ============================================================
    // Resolucion del TOKEN PUBLICO (PublicId GUID) -> Id interno (bugfix 2026-06-28)
    // ============================================================

    [Fact]
    public async Task Create_resuelve_el_proveedor_por_su_publicId_al_id_interno()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        // El front manda el PublicId (GUID) del proveedor 7, NO su Id interno. La cuenta debe quedar colgada del
        // Id interno 7 (lo que despues usa el listado por dueño).
        var created = await service.CreateAsync(
            ValidRequest(ownerType: BankAccountOwnerType.Supplier, ownerToken: SupplierToken(7)),
            "user-1", "User", CancellationToken.None);

        Assert.Equal(BankAccountOwnerType.Supplier, created.OwnerType);

        // La resolucion token (PublicId GUID) -> Id interno se verifica contra la FILA PERSISTIDA: el OwnerId
        // interno ya no se expone en la respuesta (hardening 2026-06-28).
        var persisted = ctx.BankAccounts.Single(a => a.PublicId == created.PublicId);
        Assert.Equal(7, persisted.OwnerId);
    }

    [Fact]
    public async Task Create_resuelve_el_cliente_por_su_publicId_al_id_interno()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(ownerType: BankAccountOwnerType.Customer, ownerToken: CustomerToken(7)),
            "user-1", "User", CancellationToken.None);

        Assert.Equal(BankAccountOwnerType.Customer, created.OwnerType);

        // Mismo invariante de resolucion, verificado contra la fila persistida (el OwnerId interno no viaja).
        var persisted = ctx.BankAccounts.Single(a => a.PublicId == created.PublicId);
        Assert.Equal(7, persisted.OwnerId);
    }

    [Theory]
    [InlineData("")]                 // vacio
    [InlineData("   ")]              // en blanco
    [InlineData("no-es-un-guid")]    // basura
    [InlineData("123")]              // numero, no GUID
    public async Task Create_rechaza_token_de_dueño_invalido(string badToken)
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var request = ValidRequest(ownerType: BankAccountOwnerType.Supplier, ownerToken: badToken);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(request, "user-1", "User", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveOwnerInternalId_agencia_devuelve_0_e_ignora_el_token()
    {
        using var ctx = NewContext();
        var service = NewService(ctx);

        var id = await service.ResolveOwnerInternalIdAsync(BankAccountOwnerType.Agency, "cualquier-cosa", CancellationToken.None);

        Assert.Equal(0, id);
    }

    [Fact]
    public async Task ResolveOwnerInternalId_proveedor_traduce_publicId_a_id_interno()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var id = await service.ResolveOwnerInternalIdAsync(
            BankAccountOwnerType.Supplier, SupplierToken(5), CancellationToken.None);

        Assert.Equal(5, id);
    }

    [Fact]
    public async Task ResolveOwnerInternalId_dueño_inexistente_lanza_ArgumentException()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        // GUID con formato valido pero que no corresponde a ningun proveedor sembrado.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolveOwnerInternalIdAsync(BankAccountOwnerType.Supplier, SupplierToken(999), CancellationToken.None));
    }

    [Fact]
    public async Task ResolveOwnerInternalId_token_no_guid_lanza_ArgumentException()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolveOwnerInternalIdAsync(BankAccountOwnerType.Customer, "no-es-guid", CancellationToken.None));
    }

    // ============================================================
    // Listado, masking y multiples cuentas por dueño
    // ============================================================

    [Fact]
    public async Task Varias_cuentas_por_dueño_se_listan_todas()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS"), "user-1", "User", CancellationToken.None);
        await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "USD"), "user-1", "User", CancellationToken.None);

        var list = await service.ListAsync(BankAccountOwnerType.Supplier, 7, CancellationToken.None);

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task Lista_solo_devuelve_cuentas_del_dueño_pedido()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        await service.CreateAsync(ValidRequest(ownerType: BankAccountOwnerType.Supplier, ownerToken: SupplierToken(7)),
            "user-1", "User", CancellationToken.None);
        await service.CreateAsync(ValidRequest(ownerType: BankAccountOwnerType.Customer, ownerToken: CustomerToken(7)),
            "user-1", "User", CancellationToken.None);

        var supplierList = await service.ListAsync(BankAccountOwnerType.Supplier, 7, CancellationToken.None);

        Assert.Single(supplierList);
        Assert.Equal(BankAccountOwnerType.Supplier, supplierList[0].OwnerType);
    }

    [Fact]
    public async Task Lista_enmascara_el_cbu_mostrando_solo_los_ultimos_4()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        await service.CreateAsync(
            ValidRequest(cbu: "0123456789012345678901"), "user-1", "User", CancellationToken.None);

        var list = await service.ListAsync(BankAccountOwnerType.Supplier, 1, CancellationToken.None);

        var masked = list[0].CbuMasked;
        Assert.NotNull(masked);
        Assert.EndsWith("8901", masked);            // ultimos 4 visibles
        Assert.DoesNotContain("012345678901234567", masked); // el resto NO viaja en claro
        Assert.Equal(22, masked!.Length);            // misma longitud, todo lo demas enmascarado
    }

    [Fact]
    public async Task Lista_enmascara_el_alias_dejando_solo_extremos()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        await service.CreateAsync(
            ValidRequest(cbu: null, alias: "mi.alias.banco"), "user-1", "User", CancellationToken.None);

        var list = await service.ListAsync(BankAccountOwnerType.Supplier, 1, CancellationToken.None);

        var masked = list[0].AliasMasked;
        Assert.NotNull(masked);
        Assert.StartsWith("mi", masked);             // primeros 2 visibles
        Assert.EndsWith("co", masked);               // ultimos 2 visibles
        Assert.DoesNotContain("alias", masked!);     // el medio NO viaja en claro
    }

    [Fact]
    public void MaskCbu_enmascara_entero_si_es_mas_corto_que_4()
    {
        Assert.Equal("•••", BankAccountService.MaskCbu("123"));
        Assert.Null(BankAccountService.MaskCbu(null));
        Assert.Null(BankAccountService.MaskCbu(""));
    }

    [Fact]
    public void MaskAlias_enmascara_entero_si_es_corto_y_null_si_vacio()
    {
        Assert.Equal("••••", BankAccountService.MaskAlias("abcd"));
        Assert.Null(BankAccountService.MaskAlias(null));
        Assert.Null(BankAccountService.MaskAlias(""));
    }

    [Fact]
    public async Task Detalle_devuelve_el_cbu_completo()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(cbu: "0123456789012345678901"), "user-1", "User", CancellationToken.None);

        var detail = await service.GetByPublicIdAsync(created.PublicId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("0123456789012345678901", detail!.Cbu);
    }

    // ============================================================
    // Edicion y soft-delete
    // ============================================================

    [Fact]
    public async Task Update_no_cambia_el_dueño_aunque_el_request_traiga_otro()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(ownerType: BankAccountOwnerType.Supplier, ownerToken: SupplierToken(7)),
            "user-1", "User", CancellationToken.None);

        // El request de edicion intenta "mover" la cuenta a un cliente distinto: debe IGNORARSE.
        var maliciousEdit = ValidRequest(ownerType: BankAccountOwnerType.Customer, ownerToken: CustomerToken(999));
        var updated = await service.UpdateAsync(created.PublicId, maliciousEdit, "user-1", "User", CancellationToken.None);

        Assert.Equal(BankAccountOwnerType.Supplier, updated.OwnerType);

        // El dueño persistido NO cambio: lo verificamos contra la fila (el OwnerId interno ya no viaja en la respuesta).
        var persisted = ctx.BankAccounts.Single(a => a.PublicId == updated.PublicId);
        Assert.Equal(7, persisted.OwnerId);
    }

    [Fact]
    public async Task Deactivate_hace_soft_delete_y_la_cuenta_deja_de_listarse()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(5)), "user-1", "User", CancellationToken.None);

        await service.DeactivateAsync(created.PublicId, "user-1", "User", CancellationToken.None);

        var list = await service.ListAsync(BankAccountOwnerType.Supplier, 5, CancellationToken.None);
        Assert.Empty(list);

        // La fila sigue existiendo (soft-delete), solo que inactiva.
        var detail = await service.GetByPublicIdAsync(created.PublicId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.False(detail!.IsActive);
    }

    [Fact]
    public async Task Deactivate_es_idempotente()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(5)), "user-1", "User", CancellationToken.None);

        await service.DeactivateAsync(created.PublicId, "user-1", "User", CancellationToken.None);
        // Segunda desactivacion: no debe lanzar.
        await service.DeactivateAsync(created.PublicId, "user-1", "User", CancellationToken.None);
    }

    [Fact]
    public async Task Update_de_cuenta_inexistente_lanza_KeyNotFound()
    {
        using var ctx = NewContext();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateAsync(Guid.NewGuid(), ValidRequest(), "user-1", "User", CancellationToken.None));
    }

    // ============================================================
    // Cuenta principal por dueño+moneda (ADR-041 TANDA 6)
    // ============================================================

    [Fact]
    public async Task Primera_cuenta_de_un_dueño_moneda_queda_principal_sola()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        // El front NO pide principal (isPrimary=false), pero al ser la primera de este dueño+moneda queda principal.
        var created = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", isPrimary: false), "user-1", "User", CancellationToken.None);

        Assert.True(created.IsPrimary);
    }

    [Fact]
    public async Task Segunda_cuenta_de_la_misma_moneda_no_se_auto_principaliza()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", isPrimary: false), "user-1", "User", CancellationToken.None);
        // Ya hay una principal en ARS: la segunda (sin pedir principal) NO debe robar el lugar.
        var second = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", isPrimary: false, cbu: "1111222233334444555566"),
            "user-1", "User", CancellationToken.None);

        Assert.False(second.IsPrimary);
    }

    [Fact]
    public async Task Marcar_una_principal_desmarca_la_anterior_del_mismo_dueño_y_moneda()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var first = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", isPrimary: true), "user-1", "User", CancellationToken.None);
        var second = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", isPrimary: true, cbu: "1111222233334444555566"),
            "user-1", "User", CancellationToken.None);

        // La segunda quedo principal; la primera debe haber sido desmarcada (una sola principal por dueño+moneda).
        var firstReloaded = await service.GetByPublicIdAsync(first.PublicId, CancellationToken.None);
        var secondReloaded = await service.GetByPublicIdAsync(second.PublicId, CancellationToken.None);

        Assert.False(firstReloaded!.IsPrimary);
        Assert.True(secondReloaded!.IsPrimary);
    }

    [Fact]
    public async Task Las_principales_por_moneda_son_independientes_ARS_y_USD()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var ars = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", isPrimary: true), "user-1", "User", CancellationToken.None);
        // Marcar principal en USD NO debe tocar la principal de ARS: son monedas distintas.
        var usd = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "USD", isPrimary: true, cbu: "9999888877776666555544"),
            "user-1", "User", CancellationToken.None);

        var arsReloaded = await service.GetByPublicIdAsync(ars.PublicId, CancellationToken.None);
        var usdReloaded = await service.GetByPublicIdAsync(usd.PublicId, CancellationToken.None);

        Assert.True(arsReloaded!.IsPrimary);
        Assert.True(usdReloaded!.IsPrimary);
    }

    [Fact]
    public async Task Update_que_pide_principal_toma_el_lugar_de_la_anterior()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var first = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", isPrimary: true), "user-1", "User", CancellationToken.None);
        var second = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", isPrimary: false, cbu: "1111222233334444555566"),
            "user-1", "User", CancellationToken.None);

        // Editar la segunda pidiendo principal: debe quedar principal y desmarcar la primera.
        await service.UpdateAsync(
            second.PublicId,
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", isPrimary: true, cbu: "1111222233334444555566"),
            "user-1", "User", CancellationToken.None);

        var firstReloaded = await service.GetByPublicIdAsync(first.PublicId, CancellationToken.None);
        var secondReloaded = await service.GetByPublicIdAsync(second.PublicId, CancellationToken.None);

        Assert.False(firstReloaded!.IsPrimary);
        Assert.True(secondReloaded!.IsPrimary);
    }

    [Fact]
    public async Task SetPrimary_marca_principal_y_desmarca_la_anterior()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var first = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", isPrimary: true), "user-1", "User", CancellationToken.None);
        var second = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", isPrimary: false, cbu: "1111222233334444555566"),
            "user-1", "User", CancellationToken.None);

        var result = await service.SetPrimaryAsync(second.PublicId, "user-1", "User", CancellationToken.None);

        Assert.True(result.IsPrimary);
        var firstReloaded = await service.GetByPublicIdAsync(first.PublicId, CancellationToken.None);
        Assert.False(firstReloaded!.IsPrimary);
    }

    [Fact]
    public async Task SetPrimary_de_cuenta_inactiva_lanza_ArgumentException()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(5)), "user-1", "User", CancellationToken.None);
        await service.DeactivateAsync(created.PublicId, "user-1", "User", CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SetPrimaryAsync(created.PublicId, "user-1", "User", CancellationToken.None));
    }

    [Fact]
    public async Task SetPrimary_de_cuenta_inexistente_lanza_KeyNotFound()
    {
        using var ctx = NewContext();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.SetPrimaryAsync(Guid.NewGuid(), "user-1", "User", CancellationToken.None));
    }

    [Fact]
    public async Task Lista_trae_la_principal_primero_dentro_de_su_moneda()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        // Primera ARS (auto-principal), segunda ARS no-principal, luego marcamos la SEGUNDA como principal.
        var first = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS"), "user-1", "User", CancellationToken.None);
        var second = await service.CreateAsync(
            ValidRequest(ownerToken: SupplierToken(7),currency: "ARS", cbu: "1111222233334444555566"),
            "user-1", "User", CancellationToken.None);
        await service.SetPrimaryAsync(second.PublicId, "user-1", "User", CancellationToken.None);

        var list = await service.ListAsync(BankAccountOwnerType.Supplier, 7, CancellationToken.None);

        // Dentro de ARS, la principal (la segunda) debe venir primero.
        Assert.Equal(2, list.Count);
        Assert.Equal(second.PublicId, list[0].PublicId);
        Assert.True(list[0].IsPrimary);
        Assert.False(list[1].IsPrimary);
    }

    // ============================================================
    // Mapeo de permisos por dueño (pura, sin HTTP)
    // ============================================================

    [Theory]
    [InlineData(BankAccountOwnerType.Supplier, "proveedores.view")]
    [InlineData(BankAccountOwnerType.Customer, "clientes.view")]
    [InlineData(BankAccountOwnerType.Agency, "configuracion.view")]
    public void RequiredReadPermission_mapea_por_dueño(BankAccountOwnerType ownerType, string expected)
    {
        Assert.Equal(expected, BankAccountAuthorization.RequiredReadPermission(ownerType));
    }

    [Theory]
    [InlineData(BankAccountOwnerType.Supplier, "proveedores.edit")]
    [InlineData(BankAccountOwnerType.Customer, "clientes.edit")]
    public void RequiredWritePermission_mapea_por_dueño(BankAccountOwnerType ownerType, string expected)
    {
        Assert.Equal(expected, BankAccountAuthorization.RequiredWritePermission(ownerType));
    }

    [Fact]
    public void RequiredWritePermission_de_la_agencia_es_null_porque_es_admin_only()
    {
        // Agency no tiene permiso de escritura dedicado: su escritura es Admin-only (el controller hace el
        // bypass por rol). null = "ningun permiso habilita esto salvo ser Admin".
        Assert.Null(BankAccountAuthorization.RequiredWritePermission(BankAccountOwnerType.Agency));
    }
}

/// <summary>
/// ADR-041: autorizacion del CONTROLLER (no del helper puro). Verifica el bypass de Admin, la escritura por
/// dueño, la lectura del detalle (con auditoria) y que Update/Delete autorizan contra el dueño PERSISTIDO (no
/// el del body). Usa mocks del servicio y del resolver de permisos; el User se arma a mano via ClaimsPrincipal.
/// </summary>
public class Adr041BankAccountControllerAuthorizationTests
{
    private static BankAccountsController NewController(
        Mock<IBankAccountService> service,
        bool isAdmin,
        string? userId,
        params string[] permissions)
    {
        var resolver = new Mock<IUserPermissionResolver>();
        if (!string.IsNullOrEmpty(userId))
        {
            resolver
                .Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HashSet<string>(permissions));
        }

        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(userId))
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        if (isAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var identity = new ClaimsIdentity(claims, authenticationType: "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);

        return new BankAccountsController(service.Object, resolver.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
    }

    private static BankAccountDetailDto OwnedBy(BankAccountOwnerType ownerType) => new(
        PublicId: Guid.NewGuid(),
        OwnerType: ownerType,
        Cbu: "0123456789012345678901",
        Alias: null,
        HolderName: "Titular",
        Currency: "ARS",
        Bank: null,
        AccountType: null,
        HolderTaxId: null,
        Notes: null,
        IsActive: true,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: null);

    // Shape ENMASCARADO que devuelven las ESCRITURAS (Create/Update/SetPrimary). El controller solo reenvia lo
    // que devuelve el servicio, asi que para los tests de autorizacion alcanza con un item con CBU tapado.
    private static BankAccountListItemDto MaskedItem(
        BankAccountOwnerType ownerType, bool isPrimary = false) => new(
        PublicId: Guid.NewGuid(),
        OwnerType: ownerType,
        CbuMasked: "••••••••••••••••••8901",
        AliasMasked: null,
        HolderName: "Titular",
        Currency: "ARS",
        Bank: null,
        AccountType: null,
        HolderTaxId: null,
        Notes: null,
        IsActive: true,
        CreatedAt: DateTime.UtcNow,
        IsPrimary: isPrimary);

    // El owner token del body no se ejercita en estos tests de AUTORIZACION (CreateAsync esta mockeado y la
    // resolucion token->Id vive en el servicio real). Mandamos un GUID cualquiera con el formato correcto.
    private static BankAccountUpsertRequest Request(BankAccountOwnerType ownerType, string? ownerToken = "11111111-1111-1111-1111-111111111111") => new(
        OwnerType: ownerType,
        OwnerId: ownerToken,
        Cbu: "0123456789012345678901",
        Alias: null,
        HolderName: "Titular",
        Currency: "ARS",
        Bank: null,
        AccountType: null,
        HolderTaxId: null,
        Notes: null);

    // Los 400 de este controller devuelven un objeto anonimo { message = "..." }. Para no acoplar los tests a la
    // forma del objeto, leemos la propiedad "message" por reflexion.
    private static string? MessageOf(BadRequestObjectResult badRequest)
    {
        var value = badRequest.Value;
        var messageProperty = value?.GetType().GetProperty("message");
        return messageProperty?.GetValue(value) as string;
    }

    [Fact]
    public async Task Admin_bypassa_la_escritura_de_la_agencia()
    {
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.CreateAsync(It.IsAny<BankAccountUpsertRequest>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MaskedItem(BankAccountOwnerType.Agency));

        var controller = NewController(service, isAdmin: true, userId: "admin-1");

        var result = await controller.Create(Request(BankAccountOwnerType.Agency), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        service.Verify(s => s.CreateAsync(It.IsAny<BankAccountUpsertRequest>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task No_admin_no_puede_escribir_cuenta_de_la_agencia()
    {
        var service = new Mock<IBankAccountService>();
        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ConfiguracionView);

        var result = await controller.Create(Request(BankAccountOwnerType.Agency), CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        service.Verify(s => s.CreateAsync(It.IsAny<BankAccountUpsertRequest>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Permiso_de_clientes_no_habilita_escribir_cuenta_de_proveedor()
    {
        var service = new Mock<IBankAccountService>();
        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ClientesEdit);

        var result = await controller.Create(Request(BankAccountOwnerType.Supplier), CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        service.Verify(s => s.CreateAsync(It.IsAny<BankAccountUpsertRequest>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Permiso_de_clientes_no_habilita_escribir_cuenta_de_la_agencia()
    {
        var service = new Mock<IBankAccountService>();
        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ClientesEdit);

        var result = await controller.Create(Request(BankAccountOwnerType.Agency), CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Detalle_sin_permiso_de_lectura_del_dueño_es_forbidden_y_no_audita()
    {
        var existing = OwnedBy(BankAccountOwnerType.Supplier);
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.GetByPublicIdAsync(existing.PublicId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        // Usuario con permiso de clientes, pero la cuenta es de un PROVEEDOR -> sin proveedores.view.
        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ClientesView);

        var result = await controller.GetByPublicId(existing.PublicId, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        service.Verify(s => s.AuditDetailViewedAsync(It.IsAny<BankAccountDetailDto>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Detalle_con_permiso_de_lectura_audita_el_acceso()
    {
        var existing = OwnedBy(BankAccountOwnerType.Supplier);
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.GetByPublicIdAsync(existing.PublicId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ProveedoresView);

        var result = await controller.GetByPublicId(existing.PublicId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        service.Verify(s => s.AuditDetailViewedAsync(existing, "user-1", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_autoriza_contra_el_dueño_persistido_no_el_del_body()
    {
        // La cuenta es de un PROVEEDOR. El usuario tiene clientes.edit y manda en el body ownerType=Customer.
        // Debe dar Forbid: la autorizacion mira el dueño PERSISTIDO (Supplier), no el del body.
        var existing = OwnedBy(BankAccountOwnerType.Supplier);
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.GetByPublicIdAsync(existing.PublicId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ClientesEdit);

        var result = await controller.Update(existing.PublicId, Request(BankAccountOwnerType.Customer), CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        service.Verify(s => s.UpdateAsync(It.IsAny<Guid>(), It.IsAny<BankAccountUpsertRequest>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_autoriza_contra_el_dueño_persistido_no_el_del_body()
    {
        var existing = OwnedBy(BankAccountOwnerType.Supplier);
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.GetByPublicIdAsync(existing.PublicId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ClientesEdit);

        var result = await controller.Delete(existing.PublicId, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        service.Verify(s => s.DeactivateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OwnerType_fuera_de_rango_es_bad_request()
    {
        var service = new Mock<IBankAccountService>();
        var controller = NewController(service, isAdmin: true, userId: "admin-1");

        // "99" parsea como numero pero NO es un valor definido del enum -> conserva el guard de Enum.IsDefined.
        var result = await controller.List("99", ownerId: "1", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        // No resuelve el dueño: el rechazo es por tipo invalido, antes de tocar la BD.
        service.Verify(s => s.ResolveOwnerInternalIdAsync(It.IsAny<BankAccountOwnerType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]      // ausente
    [InlineData("")]        // vacio
    [InlineData("   ")]     // en blanco
    [InlineData("abc")]     // basura (el caso que el framework devolveria con su string interno)
    public async Task List_con_ownerType_malformado_es_bad_request_con_mensaje_amable(string? badOwnerType)
    {
        var service = new Mock<IBankAccountService>();
        var controller = NewController(service, isAdmin: true, userId: "admin-1");

        var result = await controller.List(badOwnerType, ownerId: "1", CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        // El mensaje es el amable de la app, NUNCA el string interno del framework ni el valor recibido.
        Assert.Equal("Tipo de dueño inválido.", MessageOf(badRequest));
        service.Verify(s => s.ResolveOwnerInternalIdAsync(It.IsAny<BankAccountOwnerType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("2")]          // numerico (lo que envia el front: OWNER_TYPE.Supplier = 2)
    [InlineData("Supplier")]   // nombre del enum
    [InlineData("supplier")]   // nombre sin distinguir mayusculas
    public async Task List_acepta_ownerType_numerico_y_por_nombre(string ownerTypeRaw)
    {
        var ownerToken = Guid.NewGuid().ToString();
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.ResolveOwnerInternalIdAsync(BankAccountOwnerType.Supplier, ownerToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        service
            .Setup(s => s.ListAsync(BankAccountOwnerType.Supplier, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MaskedItem(BankAccountOwnerType.Supplier) });

        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ProveedoresView);

        var result = await controller.List(ownerTypeRaw, ownerToken, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        // Cualquiera de las formas debe resolver al MISMO tipo de dueño (Supplier).
        service.Verify(s => s.ListAsync(BankAccountOwnerType.Supplier, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_resuelve_el_token_del_dueño_y_devuelve_las_cuentas()
    {
        // El front manda el PublicId (GUID) del proveedor. El controller debe resolverlo al Id interno y listar.
        var ownerToken = Guid.NewGuid().ToString();
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.ResolveOwnerInternalIdAsync(BankAccountOwnerType.Supplier, ownerToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        service
            .Setup(s => s.ListAsync(BankAccountOwnerType.Supplier, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MaskedItem(BankAccountOwnerType.Supplier) });

        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ProveedoresView);

        var result = await controller.List("Supplier", ownerToken, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        service.Verify(s => s.ListAsync(BankAccountOwnerType.Supplier, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_con_token_invalido_es_bad_request()
    {
        // Token vacio/no-GUID: el servicio lanza ArgumentException al resolver -> el controller lo mapea a 400.
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.ResolveOwnerInternalIdAsync(BankAccountOwnerType.Supplier, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("El identificador del proveedor es inválido."));

        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ProveedoresView);

        var result = await controller.List("2", "no-es-guid", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        service.Verify(s => s.ListAsync(It.IsAny<BankAccountOwnerType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task List_sin_permiso_de_lectura_del_dueño_es_forbidden_y_no_resuelve()
    {
        // La autorizacion ocurre ANTES de resolver el dueño: un usuario sin permiso no debe poder distinguir
        // (por la respuesta) un dueño existente de uno inexistente.
        var service = new Mock<IBankAccountService>();
        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ClientesView);

        var result = await controller.List("Supplier", Guid.NewGuid().ToString(), CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        service.Verify(s => s.ResolveOwnerInternalIdAsync(It.IsAny<BankAccountOwnerType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetPrimary_autoriza_contra_el_dueño_persistido_no_el_del_body()
    {
        // La cuenta es de un PROVEEDOR. El usuario solo tiene clientes.edit -> sin proveedores.edit -> Forbid.
        var existing = OwnedBy(BankAccountOwnerType.Supplier);
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.GetByPublicIdAsync(existing.PublicId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ClientesEdit);

        var result = await controller.SetPrimary(existing.PublicId, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        service.Verify(s => s.SetPrimaryAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetPrimary_con_permiso_de_escritura_del_dueño_marca_principal()
    {
        var existing = OwnedBy(BankAccountOwnerType.Supplier);
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.GetByPublicIdAsync(existing.PublicId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        service
            .Setup(s => s.SetPrimaryAsync(existing.PublicId, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MaskedItem(BankAccountOwnerType.Supplier, isPrimary: true));

        var controller = NewController(service, isAdmin: false, userId: "user-1", Permissions.ProveedoresEdit);

        var result = await controller.SetPrimary(existing.PublicId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        service.Verify(s => s.SetPrimaryAsync(existing.PublicId, "user-1", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetPrimary_de_cuenta_inexistente_es_not_found()
    {
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.GetByPublicIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BankAccountDetailDto?)null);

        var controller = NewController(service, isAdmin: true, userId: "admin-1");

        var result = await controller.SetPrimary(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}

/// <summary>
/// Bugfix 2026-06-28: el body de alta/edicion trae <c>ownerId</c> POLIMORFICO — un numero (0) para la Agencia y
/// un texto (PublicId GUID) para Cliente/Proveedor. Estos tests bloquean el comportamiento del
/// <see cref="OwnerReferenceJsonConverter"/> deserializando como lo hace la API real (System.Text.Json con
/// <see cref="System.Text.Json.JsonSerializerDefaults.Web"/>). Sin el converter, el GUID-string rompia el binding
/// (y la cuenta no se podia listar ni crear para clientes/proveedores).
/// </summary>
public class Adr041OwnerReferenceJsonConverterTests
{
    // Mismas opciones que usa la API (camelCase, case-insensitive). El converter va atado a la PROPIEDAD del DTO.
    private static readonly System.Text.Json.JsonSerializerOptions WebOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void Deserializa_ownerId_GUID_como_texto_para_cliente_o_proveedor()
    {
        var guid = Guid.NewGuid();
        var json = $"{{\"ownerType\":2,\"ownerId\":\"{guid}\",\"cbu\":\"0123456789012345678901\"," +
                   "\"holderName\":\"Titular\",\"currency\":\"ARS\"}";

        var request = System.Text.Json.JsonSerializer.Deserialize<BankAccountUpsertRequest>(json, WebOptions);

        Assert.NotNull(request);
        Assert.Equal(BankAccountOwnerType.Supplier, request!.OwnerType);
        Assert.Equal(guid.ToString(), request.OwnerId);
    }

    [Fact]
    public void Deserializa_ownerId_numerico_de_la_agencia_sin_romper()
    {
        // El front manda ownerId: 0 (numero) para la Agencia. Antes esto NO se podia leer en un string -> 400.
        var json = "{\"ownerType\":0,\"ownerId\":0,\"cbu\":\"0123456789012345678901\"," +
                   "\"holderName\":\"Titular\",\"currency\":\"ARS\"}";

        var request = System.Text.Json.JsonSerializer.Deserialize<BankAccountUpsertRequest>(json, WebOptions);

        Assert.NotNull(request);
        Assert.Equal(BankAccountOwnerType.Agency, request!.OwnerType);
        Assert.Equal("0", request.OwnerId);
    }

    [Fact]
    public void Deserializa_ownerId_null_como_null()
    {
        var json = "{\"ownerType\":0,\"ownerId\":null,\"cbu\":\"0123456789012345678901\"," +
                   "\"holderName\":\"Titular\",\"currency\":\"ARS\"}";

        var request = System.Text.Json.JsonSerializer.Deserialize<BankAccountUpsertRequest>(json, WebOptions);

        Assert.NotNull(request);
        Assert.Null(request!.OwnerId);
    }
}
