using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// M1 (obra "anular sin factura", 2026-07-23): guarda los valores NUMÉRICOS de
/// <see cref="BookingCancellationStatus"/> que <c>AppDbContext</c> usa DENTRO de un string SQL crudo
/// (<c>HasFilter</c> de los índices únicos parciales de <c>BookingCancellations</c>):
///
/// <code>
/// "OriginatingInvoiceId" IS NOT NULL AND "Status" NOT IN (4, 6)   // por OriginatingInvoiceId
/// "Status" NOT IN (4, 6)                                          // por ReservaId
/// </code>
///
/// <para><b>Por qué existe este test</b>: ese <c>4</c> y ese <c>6</c> son literales SQL — el compilador NO los
/// conecta con <see cref="BookingCancellationStatus.Closed"/>/<see cref="BookingCancellationStatus.Aborted"/>.
/// Si alguien reordena o inserta un valor nuevo en el enum (cambiando los números sin querer), el índice de
/// Postgres seguiría filtrando por "4" y "6" — pero esos números ya no significarían Closed/Aborted. El
/// resultado sería un bug SILENCIOSO (el UNIQUE dejaría de excluir los estados correctos) que ningún test de
/// compilación detectaría. Este test SÍ lo detecta: si los números cambian, falla acá, con un mensaje que
/// apunta directo a los 2 lugares que hay que actualizar en conjunto (AppDbContext + la migración SQL cruda
/// de <c>tools/sql/b1-partial-unique-index-prevalidation.sql</c>).</para>
/// </summary>
public class BookingCancellationStatusIndexFilterEnumGuardTests
{
    [Fact]
    public void Closed_Es4_ComoEspera_ElFiltroSqlDeLosIndicesUnicosParciales()
    {
        Assert.Equal(4, (int)BookingCancellationStatus.Closed);
    }

    [Fact]
    public void Aborted_Es6_ComoEspera_ElFiltroSqlDeLosIndicesUnicosParciales()
    {
        Assert.Equal(6, (int)BookingCancellationStatus.Aborted);
    }
}
