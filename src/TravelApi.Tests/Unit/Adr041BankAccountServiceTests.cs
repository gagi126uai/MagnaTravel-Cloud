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

    // La validacion de existencia del dueño exige que el Cliente/Proveedor exista y este activo. Sembramos un
    // set estandar para que los caminos felices puedan crear cuentas.
    private static void SeedStandardOwners(AppDbContext ctx)
    {
        ctx.Suppliers.Add(new Supplier { Id = 1, Name = "Proveedor 1", IsActive = true });
        ctx.Suppliers.Add(new Supplier { Id = 5, Name = "Proveedor 5", IsActive = true });
        ctx.Suppliers.Add(new Supplier { Id = 7, Name = "Proveedor 7", IsActive = true });
        ctx.Customers.Add(new Customer { Id = 7, FullName = "Cliente 7", IsActive = true });
        ctx.SaveChanges();
    }

    private static BankAccountUpsertRequest ValidRequest(
        BankAccountOwnerType ownerType = BankAccountOwnerType.Supplier,
        int ownerId = 1,
        string? cbu = "0123456789012345678901",
        string? alias = null,
        string holderName = "Operador Mayorista SA",
        string currency = "ARS",
        bool isPrimary = false) =>
        new(
            OwnerType: ownerType,
            OwnerId: ownerId,
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
            ValidRequest(ownerId: 7, cbu: "0123456789012345678901"), "user-1", "User", CancellationToken.None);

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
        var request = ValidRequest(ownerType: BankAccountOwnerType.Supplier, ownerId: 999);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(request, "user-1", "User", CancellationToken.None));
    }

    [Fact]
    public async Task Create_rechaza_owner_inactivo()
    {
        using var ctx = NewContext();
        ctx.Customers.Add(new Customer { Id = 42, FullName = "Cliente inactivo", IsActive = false });
        ctx.SaveChanges();
        var service = NewService(ctx);

        var request = ValidRequest(ownerType: BankAccountOwnerType.Customer, ownerId: 42);

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
        var request = ValidRequest(ownerType: BankAccountOwnerType.Agency, ownerId: 999);
        var created = await service.CreateAsync(request, "user-1", "User", CancellationToken.None);

        Assert.Equal(BankAccountOwnerType.Agency, created.OwnerType);
        Assert.Equal(0, created.OwnerId);
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
            ValidRequest(ownerId: 7, currency: "ARS"), "user-1", "User", CancellationToken.None);
        await service.CreateAsync(
            ValidRequest(ownerId: 7, currency: "USD"), "user-1", "User", CancellationToken.None);

        var list = await service.ListAsync(BankAccountOwnerType.Supplier, 7, CancellationToken.None);

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task Lista_solo_devuelve_cuentas_del_dueño_pedido()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        await service.CreateAsync(ValidRequest(ownerType: BankAccountOwnerType.Supplier, ownerId: 7),
            "user-1", "User", CancellationToken.None);
        await service.CreateAsync(ValidRequest(ownerType: BankAccountOwnerType.Customer, ownerId: 7),
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
            ValidRequest(ownerType: BankAccountOwnerType.Supplier, ownerId: 7),
            "user-1", "User", CancellationToken.None);

        // El request de edicion intenta "mover" la cuenta a un cliente distinto: debe IGNORARSE.
        var maliciousEdit = ValidRequest(ownerType: BankAccountOwnerType.Customer, ownerId: 999);
        var updated = await service.UpdateAsync(created.PublicId, maliciousEdit, "user-1", "User", CancellationToken.None);

        Assert.Equal(BankAccountOwnerType.Supplier, updated.OwnerType);
        Assert.Equal(7, updated.OwnerId);
    }

    [Fact]
    public async Task Deactivate_hace_soft_delete_y_la_cuenta_deja_de_listarse()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        var created = await service.CreateAsync(
            ValidRequest(ownerId: 5), "user-1", "User", CancellationToken.None);

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
            ValidRequest(ownerId: 5), "user-1", "User", CancellationToken.None);

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
            ValidRequest(ownerId: 7, currency: "ARS", isPrimary: false), "user-1", "User", CancellationToken.None);

        Assert.True(created.IsPrimary);
    }

    [Fact]
    public async Task Segunda_cuenta_de_la_misma_moneda_no_se_auto_principaliza()
    {
        using var ctx = NewContext();
        SeedStandardOwners(ctx);
        var service = NewService(ctx);

        await service.CreateAsync(
            ValidRequest(ownerId: 7, currency: "ARS", isPrimary: false), "user-1", "User", CancellationToken.None);
        // Ya hay una principal en ARS: la segunda (sin pedir principal) NO debe robar el lugar.
        var second = await service.CreateAsync(
            ValidRequest(ownerId: 7, currency: "ARS", isPrimary: false, cbu: "1111222233334444555566"),
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
            ValidRequest(ownerId: 7, currency: "ARS", isPrimary: true), "user-1", "User", CancellationToken.None);
        var second = await service.CreateAsync(
            ValidRequest(ownerId: 7, currency: "ARS", isPrimary: true, cbu: "1111222233334444555566"),
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
            ValidRequest(ownerId: 7, currency: "ARS", isPrimary: true), "user-1", "User", CancellationToken.None);
        // Marcar principal en USD NO debe tocar la principal de ARS: son monedas distintas.
        var usd = await service.CreateAsync(
            ValidRequest(ownerId: 7, currency: "USD", isPrimary: true, cbu: "9999888877776666555544"),
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
            ValidRequest(ownerId: 7, currency: "ARS", isPrimary: true), "user-1", "User", CancellationToken.None);
        var second = await service.CreateAsync(
            ValidRequest(ownerId: 7, currency: "ARS", isPrimary: false, cbu: "1111222233334444555566"),
            "user-1", "User", CancellationToken.None);

        // Editar la segunda pidiendo principal: debe quedar principal y desmarcar la primera.
        await service.UpdateAsync(
            second.PublicId,
            ValidRequest(ownerId: 7, currency: "ARS", isPrimary: true, cbu: "1111222233334444555566"),
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
            ValidRequest(ownerId: 7, currency: "ARS", isPrimary: true), "user-1", "User", CancellationToken.None);
        var second = await service.CreateAsync(
            ValidRequest(ownerId: 7, currency: "ARS", isPrimary: false, cbu: "1111222233334444555566"),
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
            ValidRequest(ownerId: 5), "user-1", "User", CancellationToken.None);
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
            ValidRequest(ownerId: 7, currency: "ARS"), "user-1", "User", CancellationToken.None);
        var second = await service.CreateAsync(
            ValidRequest(ownerId: 7, currency: "ARS", cbu: "1111222233334444555566"),
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

    private static BankAccountDetailDto OwnedBy(BankAccountOwnerType ownerType, int ownerId = 1) => new(
        PublicId: Guid.NewGuid(),
        OwnerType: ownerType,
        OwnerId: ownerId,
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
        BankAccountOwnerType ownerType, int ownerId = 1, bool isPrimary = false) => new(
        PublicId: Guid.NewGuid(),
        OwnerType: ownerType,
        OwnerId: ownerId,
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

    private static BankAccountUpsertRequest Request(BankAccountOwnerType ownerType, int ownerId = 1) => new(
        OwnerType: ownerType,
        OwnerId: ownerId,
        Cbu: "0123456789012345678901",
        Alias: null,
        HolderName: "Titular",
        Currency: "ARS",
        Bank: null,
        AccountType: null,
        HolderTaxId: null,
        Notes: null);

    [Fact]
    public async Task Admin_bypassa_la_escritura_de_la_agencia()
    {
        var service = new Mock<IBankAccountService>();
        service
            .Setup(s => s.CreateAsync(It.IsAny<BankAccountUpsertRequest>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MaskedItem(BankAccountOwnerType.Agency, 0));

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

        var result = await controller.List((BankAccountOwnerType)99, ownerId: 1, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
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
