namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1.3 Fase 2 (RH-005 / pregunta F1 al contador, 2026-05-22): modo de prorrateo
/// del IVA cuando emitimos una nota de credito parcial sobre una factura origen.
///
/// <para><b>Por que existe este enum</b>: cuando una NC parcial cubre solo una parte
/// de la factura, hay que decidir como repartir el IVA acreditado. El contador
/// puede elegir uno de dos criterios fiscales:</para>
///
/// <list type="bullet">
///   <item><see cref="ProportionalToNet"/> (default conservador): por cada alicuota
///   distinta presente en la factura, se calcula que porcion del neto cubre la NC
///   parcial, y el IVA acreditado de esa alicuota se prorratea con la misma
///   proporcion. La suma de los IVA prorrateados tiene que coincidir con el
///   <c>FiscalAmountToCredit</c> dentro de la tolerancia configurada en
///   <see cref="OperationalFinanceSettings.PartialCreditNoteRoundingTolerance"/>.
///   Si se va por mas que la tolerancia, throw + log error (no mandamos XML
///   inconsistente al ARCA).</item>
///
///   <item><see cref="PerItem"/>: cada item de la factura lleva su propio IVA
///   calculado individualmente segun la alicuota de ese item especifico. Solo
///   usar si el contador (respuesta F1 round 3) lo confirma explicitamente —
///   es mas preciso pero introduce dispersion en facturas con muchos items
///   y distintas alicuotas.</item>
/// </list>
///
/// <para>El default <see cref="ProportionalToNet"/> sigue la guia del contador
/// pre-respuesta F1 round 3. Si el contador pide cambiar a <see cref="PerItem"/>,
/// se modifica el setting via panel admin sin redeploy.</para>
/// </summary>
public enum IvaProrrateoMode
{
    /// <summary>
    /// Prorrateo proporcional al neto por grupo de alicuota. Default. La suma
    /// de los IVAs prorrateados debe igualar el fiscal a acreditar dentro de la
    /// tolerancia configurada (PartialCreditNoteRoundingTolerance).
    /// </summary>
    ProportionalToNet = 0,

    /// <summary>
    /// Cada item lleva su propio IVA calculado individualmente. Mas preciso pero
    /// disperso. Activar solo si el contador lo confirma (respuesta F1 round 3).
    /// </summary>
    PerItem = 1,
}
