using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-041 (2026-06-28): la funcion pura que decide QUE cuentas bancarias de la agencia se muestran en un
/// comprobante (recibo/presupuesto). Se testea sola porque la generacion del PDF produce un binario no
/// inspeccionable; la regla de seleccion (principal por moneda, fallbacks y omision) si es verificable aca.
/// </summary>
public class AgencyBankAccountSelectorTests
{
    private static BankAccount AgencyAccount(
        string currency,
        bool isPrimary,
        bool isActive = true,
        string holder = "Magna Travel SRL",
        string? cbu = "0000000000000000000001",
        string? bank = "Banco Nacion",
        int id = 0)
    {
        return new BankAccount
        {
            Id = id,
            OwnerType = BankAccountOwnerType.Agency,
            OwnerId = 0,
            Currency = currency,
            IsPrimary = isPrimary,
            IsActive = isActive,
            HolderName = holder,
            Cbu = cbu,
            Bank = bank,
            CreatedAt = DateTime.UtcNow,
        };
    }

    [Fact]
    public void SinCuentas_DevuelveVacio_ParaOmitirLaSeccion()
    {
        var result = AgencyBankAccountSelector.SelectForDocument(new List<BankAccount>(), Monedas.ARS);

        Assert.Empty(result);
    }

    [Fact]
    public void NullDeCuentas_DevuelveVacio()
    {
        var result = AgencyBankAccountSelector.SelectForDocument(null, Monedas.ARS);

        Assert.Empty(result);
    }

    [Fact]
    public void EligeLaPrincipalDeLaMonedaDelDocumento()
    {
        var arsPrimary = AgencyAccount(Monedas.ARS, isPrimary: true, id: 1);
        var arsSecondary = AgencyAccount(Monedas.ARS, isPrimary: false, id: 2);
        var usdPrimary = AgencyAccount(Monedas.USD, isPrimary: true, id: 3);

        var result = AgencyBankAccountSelector.SelectForDocument(
            new[] { arsSecondary, usdPrimary, arsPrimary }, Monedas.ARS);

        var only = Assert.Single(result);
        Assert.Equal(1, only.Id);
        Assert.Equal(Monedas.ARS, only.Currency);
        Assert.True(only.IsPrimary);
    }

    [Fact]
    public void DocumentoEnUsd_EligeLaPrincipalUsd_NoLaArs()
    {
        var arsPrimary = AgencyAccount(Monedas.ARS, isPrimary: true, id: 1);
        var usdPrimary = AgencyAccount(Monedas.USD, isPrimary: true, id: 2);

        var result = AgencyBankAccountSelector.SelectForDocument(
            new[] { arsPrimary, usdPrimary }, Monedas.USD);

        var only = Assert.Single(result);
        Assert.Equal(Monedas.USD, only.Currency);
    }

    [Fact]
    public void IgnoraCuentasInactivas()
    {
        var arsActiveSecondary = AgencyAccount(Monedas.ARS, isPrimary: false, isActive: true, id: 1);
        var arsInactivePrimary = AgencyAccount(Monedas.ARS, isPrimary: true, isActive: false, id: 2);

        var result = AgencyBankAccountSelector.SelectForDocument(
            new[] { arsActiveSecondary, arsInactivePrimary }, Monedas.ARS);

        // La principal estaba inactiva: no se elige. Cae a la red de seguridad (activas sin principal) y muestra
        // la activa secundaria.
        var only = Assert.Single(result);
        Assert.Equal(1, only.Id);
        Assert.True(only.IsActive);
    }

    [Fact]
    public void IgnoraCuentasQueNoSonDeLaAgencia()
    {
        var customerPrimary = AgencyAccount(Monedas.ARS, isPrimary: true, id: 1);
        customerPrimary.OwnerType = BankAccountOwnerType.Customer;
        customerPrimary.OwnerId = 99;

        var result = AgencyBankAccountSelector.SelectForDocument(new[] { customerPrimary }, Monedas.ARS);

        // Una cuenta de cliente NUNCA se exhibe como destino de cobro de la agencia.
        Assert.Empty(result);
    }

    [Fact]
    public void SinPrincipalEnLaMonedaDelDocumento_MuestraLasPrincipalesActivas()
    {
        // Documento en ARS, pero la agencia solo tiene principal en USD.
        var usdPrimary = AgencyAccount(Monedas.USD, isPrimary: true, id: 1);

        var result = AgencyBankAccountSelector.SelectForDocument(new[] { usdPrimary }, Monedas.ARS);

        var only = Assert.Single(result);
        Assert.Equal(Monedas.USD, only.Currency);
    }

    [Fact]
    public void VariasMonedas_SinPrincipalDeLaMonedaDelDocumento_DevuelveTodasLasPrincipales()
    {
        // Documento en una tercera situacion: no hay principal de la moneda del doc (pedimos un doc en ARS pero
        // la principal ARS no existe), hay principales en USD y otra moneda hipotetica representada por una
        // segunda USD secundaria que NO es principal -> solo deberian volver las principales.
        var usdPrimary = AgencyAccount(Monedas.USD, isPrimary: true, id: 1);
        var arsSecondary = AgencyAccount(Monedas.ARS, isPrimary: false, id: 2);

        var result = AgencyBankAccountSelector.SelectForDocument(
            new[] { usdPrimary, arsSecondary }, "ARS");

        // Hay una principal (USD) -> se muestran las principales activas; la ARS secundaria (no principal) no entra.
        var only = Assert.Single(result);
        Assert.Equal(Monedas.USD, only.Currency);
        Assert.True(only.IsPrimary);
    }

    [Fact]
    public void ActivasSinNingunaPrincipal_MuestraTodasLasActivas_MonedaDelDocPrimero()
    {
        var arsSecondary = AgencyAccount(Monedas.ARS, isPrimary: false, id: 1);
        var usdSecondary = AgencyAccount(Monedas.USD, isPrimary: false, id: 2);

        var result = AgencyBankAccountSelector.SelectForDocument(
            new[] { usdSecondary, arsSecondary }, Monedas.ARS);

        Assert.Equal(2, result.Count);
        // La moneda del documento (ARS) debe quedar primera.
        Assert.Equal(Monedas.ARS, result[0].Currency);
        Assert.Equal(Monedas.USD, result[1].Currency);
    }

    [Fact]
    public void ToleraMonedaDelDocumentoEnMinusculas()
    {
        var arsPrimary = AgencyAccount(Monedas.ARS, isPrimary: true, id: 1);

        var result = AgencyBankAccountSelector.SelectForDocument(new[] { arsPrimary }, "ars");

        Assert.Single(result);
    }

    [Fact]
    public void MonedaDelDocumentoNullSeLeeComoArs()
    {
        var arsPrimary = AgencyAccount(Monedas.ARS, isPrimary: true, id: 1);
        var usdPrimary = AgencyAccount(Monedas.USD, isPrimary: true, id: 2);

        var result = AgencyBankAccountSelector.SelectForDocument(new[] { arsPrimary, usdPrimary }, null);

        var only = Assert.Single(result);
        Assert.Equal(Monedas.ARS, only.Currency);
    }
}
