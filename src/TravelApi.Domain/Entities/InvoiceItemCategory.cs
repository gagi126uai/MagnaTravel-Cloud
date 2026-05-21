namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1.3 (ADR-009 §2.3.1, 2026-05-21): clasificacion del item de factura.
///
/// Sirve para dos cosas:
///
///  1. <b>UI/alertas</b>: el vendedor ve un badge segun el tipo de item.
///  2. <b>Default de <c>IsRefundable</c></b> (regla G1 del ADR): cuando el vendedor
///     crea un item con categoria <see cref="Insurance"/>, <see cref="AdministrativeFee"/>
///     o <see cref="OperatorAdvance"/>, el service preselecciona
///     <c>IsRefundable=false</c> porque esos conceptos no se devuelven en una NC
///     parcial. El vendedor puede destildar la preseleccion con confirmacion
///     explicita.
///
/// IMPORTANTE: la logica del default vive en el SERVICE que crea el InvoiceItem,
/// NO en la entidad (constructor). La entidad mantiene <c>IsRefundable=true</c> por
/// default para que cargar una factura legacy con <see cref="Service"/> siga andando
/// como antes (compat backward).
/// </summary>
public enum InvoiceItemCategory
{
    /// <summary>Concepto principal del servicio. IsRefundable=true por default.</summary>
    Service = 0,

    /// <summary>Cargo de gestion de la agencia. G1: preseleccionar IsRefundable=false en el service.</summary>
    AdministrativeFee = 1,

    /// <summary>Seguro de cancelacion. G1: preseleccionar IsRefundable=false en el service.</summary>
    Insurance = 2,

    /// <summary>Anticipo no reembolsable. G1: preseleccionar IsRefundable=false en el service.</summary>
    OperatorAdvance = 3,

    /// <summary>Penalidad operador ya facturada (caso raro Fase 1).</summary>
    Penalty = 4,

    /// <summary>Catch-all para conceptos no clasificables.</summary>
    Other = 99,
}
