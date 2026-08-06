using System;
using System.Linq;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// "Barrido de jerga" (spec firmada 2026-08-06, Parte C): ninguna palabra de la maquinaria puede llegar
/// a una pantalla, un aviso, un PDF ni un mensaje al cliente. Estos tests son el candado: si alguien
/// afloja el saneador o vuelve a poner un número de error en un mensaje, saltan acá.
/// </summary>
public class TcAyudaInvisibleJergaTests
{
    /// <summary>
    /// El organismo devuelve sus rechazos nombrando los campos internos del comprobante ("El campo
    /// MonCotiz debe ser..."). Para el que opera el sistema eso es tan indescifrable como un stack
    /// trace: se reemplaza por el motivo genérico en castellano.
    /// </summary>
    [Theory]
    [InlineData("El campo MonCotiz debe ser menor o igual a la cotizacion oficial")]
    [InlineData("CbteFch fuera de rango permitido")]
    [InlineData("Error en el servicio WSFEv1")]
    [InlineData("El comprobante se emitio en el ambiente de homologacion")]
    [InlineData("AlicIva no coincide con ImpNeto")]
    [InlineData("MonId invalido para este tipo de comprobante")]
    public void LaJergaDelComprobante_NoLlegaAlUsuario(string crudoDelOrganismo)
    {
        var saneado = ArcaErrorSanitizer.SanitizeArcaError(crudoDelOrganismo);

        Assert.Equal(ArcaErrorSanitizer.GenericArcaMessage, saneado);
    }

    /// <summary>
    /// El saneador es una lista de lo PROHIBIDO, no de lo permitido: un motivo de negocio en castellano
    /// limpio (que al vendedor SÍ le sirve) tiene que pasar tal cual. Sin esto, el barrido de jerga
    /// terminaría escondiendo la información útil detrás de un mensaje genérico.
    /// </summary>
    [Theory]
    [InlineData("CUIT del emisor sin habilitacion para emitir comprobantes")]
    [InlineData("El punto de venta no se encuentra habilitado")]
    public void ElMotivoDeNegocioEnCastellano_PasaTalCual(string motivoUtil)
    {
        Assert.Equal(motivoUtil, ArcaErrorSanitizer.SanitizeArcaError(motivoUtil));
    }

    /// <summary>
    /// El nombre del organismo que el dueño firmó para la pantalla de facturación es "ARCA" (decisión
    /// P5=A ajustada, 2026-08-06). Texto fijado literal: si alguien lo cambia, este test lo detecta.
    /// </summary>
    [Fact]
    public void ElMotivoGenerico_NombraAlOrganismoComoARCA()
    {
        Assert.Equal(
            "ARCA rechazó la factura. Revisá los datos fiscales o volvé a intentar.",
            ArcaErrorSanitizer.GenericArcaMessage);
        Assert.DoesNotContain("AFIP", ArcaErrorSanitizer.GenericArcaMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Un código de rechazo que no está en nuestra lista de traducciones NO puede terminar en pantalla
    /// como "Error AFIP [10240]". Antes ese era el comportamiento por defecto: el usuario veía un número
    /// con el que no puede hacer absolutamente nada (spec Parte C: "ni 10240 ni ningún número de error").
    /// </summary>
    [Theory]
    [InlineData("99999", null)]
    [InlineData("99999", "El campo MonCotiz supera el maximo")]
    [InlineData(null, null)]
    [InlineData("", "<soap:Fault><faultstring>boom</faultstring></soap:Fault>")]
    public void UnCodigoDeRechazoDesconocido_NoTerminaComoNumeroEnPantalla(string? codigo, string? crudo)
    {
        var afip = BuildAfipServiceForTextOnly();

        var mensaje = afip.TranslateAfipError(codigo, crudo);

        Assert.False(string.IsNullOrWhiteSpace(mensaje));
        Assert.DoesNotContain("Error AFIP", mensaje, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("99999", mensaje, StringComparison.Ordinal);
        Assert.DoesNotContain("MonCotiz", mensaje, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("soap", mensaje, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// El rechazo por tipo de cambio por encima del máximo (el que esta obra vino a evitar) tiene su
    /// propio motivo accionable: qué pasó y qué hacer, sin número de error.
    /// </summary>
    [Fact]
    public void ElRechazoPorTipoDeCambioAlto_TieneMotivoAccionableSinNumero()
    {
        var afip = BuildAfipServiceForTextOnly();

        var mensaje = afip.TranslateAfipError("10240", "MonCotiz mayor a la cotizacion oficial + 1");

        Assert.Equal(
            "El tipo de cambio de la factura es más alto del que ARCA acepta para ese día. Volvé a emitirla: el sistema lo acomoda solo.",
            mensaje);
        Assert.DoesNotContain("10240", mensaje, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>TranslateAfipError</c> es texto puro: no toca base, ni red, ni certificados. Alcanza con un
    /// <c>AfipService</c> armado con dependencias vacías.
    /// </summary>
    private static AfipService BuildAfipServiceForTextOnly()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var protector = new Mock<ISensitiveDataProtector>();
        return new AfipService(
            new AppDbContext(options),
            NullLogger<AfipService>.Instance,
            new HttpClient(),
            protector.Object);
    }

    /// <summary>
    /// HALLAZGO del gate de exposición: el rastro interno del tipo de cambio (qué número quiso poner el
    /// usuario, cómo llegó el sistema al que quedó) vive en la entidad de la factura y NO puede salir en
    /// ninguna respuesta de la API. Test por reflexión para que no dependa de que alguien se acuerde:
    /// si mañana alguien agrega esas propiedades al DTO, o AutoMapper empieza a mapearlas, esto falla.
    /// </summary>
    [Fact]
    public void ElRastroInternoDelTipoDeCambio_NoSaleEnLaRespuestaDeLaFactura()
    {
        var propiedadesDelDto = typeof(InvoiceDto).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(nameof(Invoice.ExchangeRateOrigin), propiedadesDelDto);
        Assert.DoesNotContain(nameof(Invoice.RequestedExchangeRate), propiedadesDelDto);
        // Los dos existen en la entidad: el test compara contra algo real, no contra un nombre inventado.
        var propiedadesDeLaEntidad = typeof(Invoice).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains(nameof(Invoice.ExchangeRateOrigin), propiedadesDeLaEntidad);
        Assert.Contains(nameof(Invoice.RequestedExchangeRate), propiedadesDeLaEntidad);
    }
}
