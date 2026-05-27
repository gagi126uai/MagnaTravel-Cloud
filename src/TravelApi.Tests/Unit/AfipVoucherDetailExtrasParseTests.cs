using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.F2.2 (sub-tarea A.3.1, RH3-001 / RH4-002 round 4, 2026-05-27).
///
/// Tests del parseo de los campos OPCIONALES nuevos del response de
/// <c>FECompConsultar</c> (<see cref="AfipService.ParseVoucherDetailExtras"/>),
/// en particular el contrato defensivo del array <c>CbtesAsoc</c>:
///   - 1 item  -> mapea su Nro.
///   - 0 items -> CbteAsoc null, sin warning.
///   - N&gt;1   -> CbteAsoc null + warning (no elegir uno a ciegas).
///
/// No tocan ARCA ni la base: arman el XML del response a mano y verifican el mapeo.
/// El metodo es <c>internal static</c> y el assembly de Infrastructure expone
/// <c>InternalsVisibleTo("TravelApi.Tests")</c>, asi que lo llamamos directo.
/// </summary>
public class AfipVoucherDetailExtrasParseTests
{
    private const string ArcaNs = "http://ar.gov.afip.dif.FEV1/";

    /// <summary>
    /// Logger de prueba que acumula los mensajes formateados. Sirve para verificar que
    /// el warning de "multiples CbtesAsoc" se emitio sin depender de un mock complejo.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Construye un nodo <c>ResultGet</c> minimo en el namespace de ARCA. Solo agrega los
    /// nodos hijos cuyo valor no sea null, para poder simular "el nodo no vino en el response".
    /// </summary>
    private static XElement BuildResultGet(
        string? codAutorizacion = null,
        string? cbteFch = null,
        string? monId = null,
        string? monCotiz = null,
        XElement? cbtesAsoc = null)
    {
        var result = new XElement(XName.Get("ResultGet", ArcaNs));

        if (codAutorizacion != null)
            result.Add(new XElement(XName.Get("CodAutorizacion", ArcaNs), codAutorizacion));
        if (cbteFch != null)
            result.Add(new XElement(XName.Get("CbteFch", ArcaNs), cbteFch));
        if (monId != null)
            result.Add(new XElement(XName.Get("MonId", ArcaNs), monId));
        if (monCotiz != null)
            result.Add(new XElement(XName.Get("MonCotiz", ArcaNs), monCotiz));
        if (cbtesAsoc != null)
            result.Add(cbtesAsoc);

        return result;
    }

    /// <summary>Arma un <c>CbtesAsoc</c> con N items <c>CbteAsoc</c>, cada uno con el Nro dado.</summary>
    private static XElement BuildCbtesAsoc(params int[] nros)
    {
        var container = new XElement(XName.Get("CbtesAsoc", ArcaNs));
        foreach (var nro in nros)
        {
            container.Add(new XElement(
                XName.Get("CbteAsoc", ArcaNs),
                new XElement(XName.Get("Tipo", ArcaNs), "6"),
                new XElement(XName.Get("PtoVta", ArcaNs), "1"),
                new XElement(XName.Get("Nro", ArcaNs), nro.ToString())));
        }
        return container;
    }

    [Fact]
    public void Parse_FullResponse_MapsAllOptionalFields()
    {
        var result = BuildResultGet(
            codAutorizacion: "12345678901234",
            cbteFch: "20260527",
            monId: "PES",
            monCotiz: "1.000000",
            cbtesAsoc: BuildCbtesAsoc(4321));
        var details = new AfipVoucherDetails();
        var logger = new CapturingLogger();

        AfipService.ParseVoucherDetailExtras(result, details, logger);

        Assert.Equal("12345678901234", details.Cae);
        Assert.Equal(new DateTime(2026, 5, 27), details.IssuedAt);
        Assert.Equal("PES", details.MonId);
        Assert.Equal(1.000000m, details.MonCotiz);
        Assert.Equal(4321, details.CbteAsoc);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Parse_MissingOptionalNodes_LeavesFieldsNull()
    {
        // Response que solo trae los campos viejos: ninguno de los nodos nuevos presente.
        var result = BuildResultGet();
        var details = new AfipVoucherDetails();
        var logger = new CapturingLogger();

        AfipService.ParseVoucherDetailExtras(result, details, logger);

        Assert.Null(details.Cae);
        Assert.Null(details.IssuedAt);
        Assert.Null(details.MonId);
        Assert.Null(details.MonCotiz);
        Assert.Null(details.CbteAsoc);
    }

    [Fact]
    public void Parse_SingleCbteAsoc_MapsNro()
    {
        var result = BuildResultGet(cbtesAsoc: BuildCbtesAsoc(9999));
        var details = new AfipVoucherDetails();
        var logger = new CapturingLogger();

        AfipService.ParseVoucherDetailExtras(result, details, logger);

        Assert.Equal(9999, details.CbteAsoc);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Parse_EmptyCbtesAsoc_LeavesCbteAsocNullWithoutWarning()
    {
        // Container presente pero sin items. Caso "0 items": null, sin ruido en el log.
        var emptyContainer = new XElement(XName.Get("CbtesAsoc", ArcaNs));
        var result = BuildResultGet(cbtesAsoc: emptyContainer);
        var details = new AfipVoucherDetails();
        var logger = new CapturingLogger();

        AfipService.ParseVoucherDetailExtras(result, details, logger);

        Assert.Null(details.CbteAsoc);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Parse_MultipleCbtesAsoc_LogsWarningAndReturnsNullCbteAsoc()
    {
        // RH4-002: si ARCA devuelve >1 CbteAsoc, NO elegimos uno. null + warning.
        var result = BuildResultGet(
            codAutorizacion: "98765432109876",
            cbtesAsoc: BuildCbtesAsoc(1111, 2222));
        var details = new AfipVoucherDetails();
        var logger = new CapturingLogger();

        AfipService.ParseVoucherDetailExtras(result, details, logger);

        // (a) CbteAsoc null pese a haber items.
        Assert.Null(details.CbteAsoc);
        // (b) warning presente con el texto esperado.
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("multiples CbtesAsoc inesperado"));
        // (c) los demas campos se parsean igual (el CAE no se ve afectado).
        Assert.Equal("98765432109876", details.Cae);
    }

    [Fact]
    public void Parse_MalformedCbteFch_LeavesIssuedAtNull()
    {
        // Fecha que no respeta yyyyMMdd: degradar a null en vez de romper la consulta.
        var result = BuildResultGet(cbteFch: "2026-05-27");
        var details = new AfipVoucherDetails();
        var logger = new CapturingLogger();

        AfipService.ParseVoucherDetailExtras(result, details, logger);

        Assert.Null(details.IssuedAt);
    }

    [Fact]
    public void Parse_NonNumericCbteAsocNro_LeavesCbteAsocNull()
    {
        // Nro no numerico en el unico CbteAsoc: tratado como mismatch -> null.
        var container = new XElement(
            XName.Get("CbtesAsoc", ArcaNs),
            new XElement(
                XName.Get("CbteAsoc", ArcaNs),
                new XElement(XName.Get("Nro", ArcaNs), "abc")));
        var result = BuildResultGet(cbtesAsoc: container);
        var details = new AfipVoucherDetails();
        var logger = new CapturingLogger();

        AfipService.ParseVoucherDetailExtras(result, details, logger);

        Assert.Null(details.CbteAsoc);
    }
}
