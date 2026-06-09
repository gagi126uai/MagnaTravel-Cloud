using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-021 Capa 1 (multimoneda, 2026-06-08): tests de CONFIGURACION/MAPEO del modelo EF.
///
/// <para>Verifican que las entidades y campos nuevos quedaron mapeados como fija el ADR (defaults ARS,
/// precisiones, indices, FK, columnas sin HasColumnName). Se asertan contra el <c>IModel</c> de EF,
/// que es independiente del provider: la metadata (defaultValue, precision, indices, claves) vive en
/// el modelo aunque el provider sea InMemory. Por eso NO hace falta Postgres para validar el mapeo.</para>
///
/// <para>Tambien valida el catalogo de dominio <c>Monedas</c> y los defaults de las entidades nuevas en
/// memoria (sin tocar BD). Lo que requiere Postgres (que el default 'ARS' se aplique fisicamente en un
/// INSERT, que los importes no se muevan) es un test de migracion aparte, marcado como no ejecutado aca.</para>
/// </summary>
public class Adr021MultiCurrencyMappingTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    // ===================== Catalogo de dominio Monedas =====================

    [Fact]
    public void Monedas_Soportadas_es_ARS_y_USD()
    {
        Assert.Equal(new[] { "ARS", "USD" }, Monedas.Soportadas.ToArray());
    }

    [Theory]
    [InlineData("ARS", true)]
    [InlineData("USD", true)]
    [InlineData("usd", true)]   // tolera capitalizacion
    [InlineData("EUR", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Monedas_EsSoportada_responde_segun_catalogo(string? iso, bool esperado)
    {
        Assert.Equal(esperado, Monedas.EsSoportada(iso));
    }

    [Theory]
    [InlineData(null, "ARS")]   // legacy / vacio = ARS
    [InlineData("", "ARS")]
    [InlineData("  ", "ARS")]
    [InlineData("usd", "USD")]  // normaliza a mayuscula
    [InlineData(" ars ", "ARS")]
    public void Monedas_Normalizar_aplica_la_regla_legacy_ARS(string? iso, string esperado)
    {
        Assert.Equal(esperado, Monedas.Normalizar(iso));
    }

    // ===================== Defaults de las entidades (en memoria) =====================

    [Fact]
    public void Payment_nuevo_arranca_en_ARS_no_cruzado()
    {
        var payment = new Payment();

        Assert.Equal("ARS", payment.Currency);
        // El bloque de TC arranca en null = pago no cruzado (imputa su propia moneda).
        Assert.Null(payment.ImputedCurrency);
        Assert.Null(payment.ExchangeRate);
        Assert.Null(payment.ExchangeRateSource);
        Assert.Null(payment.ExchangeRateAt);
        Assert.Null(payment.ImputedAmount);
    }

    [Fact]
    public void SupplierPayment_nuevo_arranca_en_ARS_no_cruzado()
    {
        var payment = new SupplierPayment();

        Assert.Equal("ARS", payment.Currency);
        Assert.Null(payment.ImputedCurrency);
        Assert.Null(payment.ExchangeRate);
        Assert.Null(payment.ExchangeRateSource);
        Assert.Null(payment.ExchangeRateAt);
        Assert.Null(payment.ImputedAmount);
    }

    [Fact]
    public void ServicioReserva_Currency_es_nullable_legacy_ARS()
    {
        // Null = legacy = ARS al leer (no se fuerza un default en el servicio generico, a proposito).
        var servicio = new ServicioReserva();
        Assert.Null(servicio.Currency);
        Assert.Equal("ARS", Monedas.Normalizar(servicio.Currency));
    }

    // ===================== Mapeo EF: Payment / SupplierPayment =====================

    [Fact]
    public void Payment_Currency_mapea_con_default_ARS_y_largo_3()
    {
        using var context = NewContext();
        var currency = context.Model.FindEntityType(typeof(Payment))!.FindProperty(nameof(Payment.Currency))!;

        Assert.Equal("ARS", currency.GetDefaultValue());
        Assert.Equal(3, currency.GetMaxLength());
        Assert.False(currency.IsNullable);
        // Columna = propiedad (sin HasColumnName -> evita el trap M2).
        Assert.Equal("Currency", currency.GetColumnName());
    }

    [Fact]
    public void SupplierPayment_Currency_mapea_con_default_ARS_y_largo_3()
    {
        using var context = NewContext();
        var currency = context.Model.FindEntityType(typeof(SupplierPayment))!.FindProperty(nameof(SupplierPayment.Currency))!;

        Assert.Equal("ARS", currency.GetDefaultValue());
        Assert.Equal(3, currency.GetMaxLength());
        Assert.False(currency.IsNullable);
        Assert.Equal("Currency", currency.GetColumnName());
    }

    [Theory]
    [InlineData(typeof(Payment))]
    [InlineData(typeof(SupplierPayment))]
    public void Bloque_TC_es_nullable_con_precision_fija(Type entityType)
    {
        using var context = NewContext();
        var et = context.Model.FindEntityType(entityType)!;

        var exchangeRate = et.FindProperty("ExchangeRate")!;
        Assert.True(exchangeRate.IsNullable);
        Assert.Equal(18, exchangeRate.GetPrecision());   // convencion ARS por 1 USD (ADR-021 §2.2bis)
        Assert.Equal(6, exchangeRate.GetScale());

        var imputedAmount = et.FindProperty("ImputedAmount")!;
        Assert.True(imputedAmount.IsNullable);
        Assert.Equal(18, imputedAmount.GetPrecision());
        Assert.Equal(2, imputedAmount.GetScale());

        var imputedCurrency = et.FindProperty("ImputedCurrency")!;
        Assert.True(imputedCurrency.IsNullable);
        Assert.Equal(3, imputedCurrency.GetMaxLength());

        Assert.True(et.FindProperty("ExchangeRateAt")!.IsNullable);
        Assert.True(et.FindProperty("ExchangeRateSource")!.IsNullable);
    }

    [Theory]
    [InlineData(typeof(Payment))]
    [InlineData(typeof(SupplierPayment))]
    public void ExchangeRateSource_se_persiste_como_int(Type entityType)
    {
        using var context = NewContext();
        var source = context.Model.FindEntityType(entityType)!.FindProperty("ExchangeRateSource")!;

        // Enum como int (mismo patron que FiscalSnapshot.Source): el proveedor relacional guarda int.
        Assert.Equal(typeof(int), Nullable.GetUnderlyingType(source.GetProviderClrType()!) ?? source.GetProviderClrType());
    }

    // ===================== Mapeo EF: ServicioReserva =====================

    [Fact]
    public void ServicioReserva_Currency_es_nullable_y_largo_3()
    {
        using var context = NewContext();
        var currency = context.Model.FindEntityType(typeof(ServicioReserva))!.FindProperty(nameof(ServicioReserva.Currency))!;

        Assert.True(currency.IsNullable);   // null = legacy = ARS al leer
        Assert.Equal(3, currency.GetMaxLength());
        Assert.Equal("Currency", currency.GetColumnName());
    }

    // ===================== Mapeo EF: tablas hijas =====================

    [Fact]
    public void ReservaMoneyByCurrency_mapea_tabla_FK_y_indices()
    {
        using var context = NewContext();
        var et = context.Model.FindEntityType(typeof(ReservaMoneyByCurrency))!;

        Assert.Equal("ReservaMoneyByCurrency", et.GetTableName());

        // FK a la reserva (tabla TravelFiles) con Cascade.
        var fk = et.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(Reserva));
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);

        AssertChildAmountPrecision(et,
            nameof(ReservaMoneyByCurrency.TotalSale),
            nameof(ReservaMoneyByCurrency.ConfirmedSale),
            nameof(ReservaMoneyByCurrency.TotalCost),
            nameof(ReservaMoneyByCurrency.TotalPaid),
            nameof(ReservaMoneyByCurrency.Balance));

        AssertHasUniqueIndex(et, "ReservaId", "Currency");
        AssertHasIndex(et, "Currency", "Balance");
    }

    [Fact]
    public void SupplierBalanceByCurrency_mapea_tabla_FK_y_indices()
    {
        using var context = NewContext();
        var et = context.Model.FindEntityType(typeof(SupplierBalanceByCurrency))!;

        Assert.Equal("SupplierBalanceByCurrency", et.GetTableName());

        var fk = et.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(Supplier));
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);

        AssertChildAmountPrecision(et,
            nameof(SupplierBalanceByCurrency.ConfirmedPurchases),
            nameof(SupplierBalanceByCurrency.TotalPaid),
            nameof(SupplierBalanceByCurrency.Balance));

        AssertHasUniqueIndex(et, "SupplierId", "Currency");
        AssertHasIndex(et, "Currency", "Balance");
    }

    private static void AssertChildAmountPrecision(Microsoft.EntityFrameworkCore.Metadata.IEntityType et, params string[] amountProps)
    {
        foreach (var name in amountProps)
        {
            var prop = et.FindProperty(name)!;
            Assert.Equal(18, prop.GetPrecision());
            Assert.Equal(2, prop.GetScale());
            Assert.False(prop.IsNullable);
        }
    }

    private static void AssertHasUniqueIndex(Microsoft.EntityFrameworkCore.Metadata.IEntityType et, params string[] columns)
    {
        var index = et.GetIndexes().SingleOrDefault(ix =>
            ix.Properties.Select(p => p.Name).SequenceEqual(columns));
        Assert.NotNull(index);
        Assert.True(index!.IsUnique);
    }

    private static void AssertHasIndex(Microsoft.EntityFrameworkCore.Metadata.IEntityType et, params string[] columns)
    {
        var index = et.GetIndexes().SingleOrDefault(ix =>
            ix.Properties.Select(p => p.Name).SequenceEqual(columns));
        Assert.NotNull(index);
    }
}
