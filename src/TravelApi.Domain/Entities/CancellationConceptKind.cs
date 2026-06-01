namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-013 (2026-06-01, R4 contador matriculado): clasifica la naturaleza fiscal
/// del concepto que se cobra al cancelar. Es la decision MAS sensible del flujo:
/// determina si la agencia emite una ND propia (declara un ingreso propio) o no.
///
/// <para><b>Por que no es un string libre</b> (PROHIBIDO por el contador): un
/// "seguro de cancelacion" escrito a mano podria ser un seguro real (revision),
/// una cobertura de la agencia (otro tratamiento) o una comision (otro mas). La
/// clasificacion controla si se declara o no un ingreso gravado -> tiene
/// consecuencia fiscal directa, no puede ser texto libre.</para>
///
/// <para><b>Quien emite ND en el MVP</b>: SOLO <see cref="AgencyManagementFee"/> y
/// <see cref="AgencyCancellationFee"/> (ingreso propio gravado de la agencia). El
/// resto NO emite ND automatico:
/// <list type="bullet">
/// <item><see cref="OperatorPenaltyPassThrough"/> (default): la plata se la queda el
/// operador. La agencia es el cartero -> NO emite ND (seria declarar ingreso ajeno
/// como propio). Es el comportamiento de hoy.</item>
/// <item>seguros (<see cref="RealInsurancePremium"/>,
/// <see cref="AgencyCancellationCoverage"/>, <see cref="AgencyInsuranceCommission"/>)
/// -> revision manual (tratamiento de IVA distinto, no cerrado para automatizar).</item>
/// </list></para>
///
/// <para>Default <see cref="OperatorPenaltyPassThrough"/> a proposito: es el valor
/// conservador (= NO ND, igual a hoy).</para>
/// </summary>
public enum CancellationConceptKind
{
    /// <summary>
    /// La penalidad la retiene el OPERADOR (pass-through). La agencia NO emite ND
    /// propia. Default conservador = comportamiento actual del sistema.
    /// </summary>
    OperatorPenaltyPassThrough = 0,

    /// <summary>Cargo de gestion propio de la agencia. Ingreso gravado -> emite ND propia.</summary>
    AgencyManagementFee = 1,

    /// <summary>Cargo de cancelacion propio de la agencia. Ingreso gravado -> emite ND propia.</summary>
    AgencyCancellationFee = 2,

    /// <summary>Prima de un seguro real (de una aseguradora). Revision manual (IVA distinto).</summary>
    RealInsurancePremium = 3,

    /// <summary>Cobertura de cancelacion que ofrece la propia agencia. Revision manual.</summary>
    AgencyCancellationCoverage = 4,

    /// <summary>Comision de la agencia por intermediar un seguro. Revision manual.</summary>
    AgencyInsuranceCommission = 5,
}
