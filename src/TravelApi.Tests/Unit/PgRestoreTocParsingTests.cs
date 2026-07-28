using System.Linq;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// El indice (TOC) de pg_restore --list imprime los nombres de tabla SIN comillas aunque sean PascalCase.
/// Buscarlos entrecomillados hacia que "Ver que contiene" avisara "podria faltarle alguna parte clave"
/// sobre un resguardo sano (visto en PROD el 2026-07-28). Estos tests fijan el parseo real del TOC.
/// </summary>
public class PgRestoreTocParsingTests
{
    private const string TocReal = """
;
; Archive created at 2026-07-27 22:33:13 UTC
;     dbname: travel
;
215; 1259 16456 TABLE public TravelFiles traveluser
216; 1259 16460 TABLE public Customers traveluser
217; 1259 16470 TABLE public Invoices traveluser
218; 1259 16480 TABLE public AgencySettings traveluser
2201; 2606 16500 CONSTRAINT public TravelFiles PK_TravelFiles traveluser
2202; 1259 16510 INDEX public IX_Customers_TaxId traveluser
""";

    [Fact]
    public void ParseTableNamesFromToc_DevuelveLosNombresSinComillas()
    {
        var nombres = PgDatabaseRestorePort.ParseTableNamesFromToc(TocReal);

        Assert.Contains("TravelFiles", nombres);
        Assert.Contains("Customers", nombres);
        Assert.Contains("Invoices", nombres);
        Assert.Contains("AgencySettings", nombres);
    }

    [Fact]
    public void ParseTableNamesFromToc_NoConfundeConstraintsNiIndicesConTablas()
    {
        var nombres = PgDatabaseRestorePort.ParseTableNamesFromToc(TocReal);

        Assert.Equal(4, nombres.Count);
        Assert.DoesNotContain("PK_TravelFiles", nombres);
        Assert.DoesNotContain("IX_Customers_TaxId", nombres);
    }

    [Fact]
    public void ParseTableNamesFromToc_TocVacioNoRompe()
    {
        Assert.Empty(PgDatabaseRestorePort.ParseTableNamesFromToc(string.Empty));
    }
}
