using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real") — fix detalle #10 (revision
/// post-implementacion): guardia BARATA que protege la regla T-4/§5.3 mejor que un test que
/// intenta fijar el reloj (ver la nota metodologica en <c>ExchangeRateResolverTests</c>: este repo
/// no tiene un reloj inyectable para <c>ArgentinaTime</c>). En vez de simular las 23:30 ART, este
/// test lee el CODIGO FUENTE de los archivos NUEVOS de esta obra y falla si aparece
/// <c>DateTime.Today</c> o <c>UtcNow.Date</c> — los dos patrones prohibidos (usan el huso del
/// SERVIDOR, no el de Argentina) — sin importar en que momento del dia corra el CI.
///
/// <para><b>Por que solo los archivos NUEVOS</b> (no un grep de TODO el repo): archivos
/// PRE-EXISTENTES que esta obra apenas tocó (ej. <c>BookingCancellationService.cs</c>,
/// <c>AfipService.cs</c>) tienen usos LEGITIMOS y anteriores de <c>DateTime.UtcNow.Date</c> para
/// comparaciones de antigüedad que no son fechas de comprobante/pantalla (ej. "cuantos dias
/// pasaron desde que el operador confirmo"). Escanear el archivo COMPLETO daria falsos positivos
/// de deuda tecnica preexistente que no es responsabilidad de esta obra arreglar. Los archivos de
/// esta lista son 100% nuevos: cualquier match aca es 100% atribuible a ADR-011.</para>
/// </summary>
public class Adr011ArgentinaTimeGuardTests
{
    private static readonly string[] ArchivosNuevosDeLaObra =
    {
        "TravelApi.Domain/Entities/ExchangeRateQuote.cs",
        "TravelApi.Application/Interfaces/IExchangeRateResolver.cs",
        "TravelApi.Application/DTOs/ExchangeRateSuggestionResponse.cs",
        "TravelApi.Infrastructure/Services/ExchangeRateResolver.cs",
        "TravelApi.Infrastructure/Services/ExchangeRateSyncJob.cs",
        "TravelApi/Controllers/ExchangeRatesController.cs",
    };

    private static readonly string[] PatronesProhibidos = { "DateTime.Today", "UtcNow.Date" };

    [Fact]
    public void ArchivosNuevosDeLaObra_NoUsanDateTimeTodayNiUtcNowDate()
    {
        var srcRoot = FindSrcRoot();
        var offenders = new List<string>();

        foreach (var relativePath in ArchivosNuevosDeLaObra)
        {
            var fullPath = Path.Combine(srcRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"No se encontro el archivo esperado de la obra: {fullPath}");

            var content = File.ReadAllText(fullPath);
            foreach (var patron in PatronesProhibidos)
            {
                if (content.Contains(patron, StringComparison.Ordinal))
                {
                    offenders.Add($"{relativePath} (contiene '{patron}')");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Estos archivos de la obra ADR-011 usan DateTime.Today o UtcNow.Date en vez de " +
            "ArgentinaTime.GetArgentinaToday()/GetArgentinaNow() (regla T-4/§5.3): entre las 21:00 " +
            "y las 24:00 hora argentina, esos patrones calculan mal el dia calendario. Archivos: " +
            string.Join("; ", offenders));
    }

    /// <summary>
    /// Sube desde el directorio de ejecucion de los tests (<c>bin/Debug/net8.0/...</c>) hasta
    /// encontrar la carpeta que contiene <c>MagnaTravel.sln</c> (la carpeta <c>src/</c> del repo).
    /// </summary>
    private static string FindSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MagnaTravel.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "No se pudo ubicar MagnaTravel.sln subiendo desde " + AppContext.BaseDirectory +
            " — este test guardia necesita leer el codigo fuente de los archivos de la obra.");
    }
}
