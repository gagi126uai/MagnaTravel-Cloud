namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1.3 (ADR-009, 2026-05-21): modelo de facturacion al cliente para un operador.
///
/// Esta es la diferencia fiscal critica entre "reseller" e "intermediario" en la
/// jerga del retail de turismo:
///
///  - <see cref="TotalToCustomer"/>: la agencia compra al operador y le revende
///    al cliente. La factura al cliente cubre el total del servicio. Es el modo
///    conservador y compatible con todo lo que ya hace FC1.2.
///  - <see cref="CommissionOnly"/>: la agencia actua como intermediaria; el operador
///    es quien le factura al cliente final (o al menos asi se modela), y la agencia
///    solo factura su comision. La NC parcial en este modo cambia de formula y por
///    eso Fase 1 lo deriva a revision manual (GR-003) hasta que el contador
///    responda la pregunta F2 round 3.
///
/// Default <see cref="TotalToCustomer"/> porque es el comportamiento legacy del repo:
/// asumir reseller no rompe nada que hoy ande. Solo el admin habilita
/// <see cref="CommissionOnly"/> para operadores que efectivamente operan asi.
/// </summary>
public enum SupplierInvoicingMode
{
    /// <summary>Reseller: factura el total del servicio al cliente. Default conservador.</summary>
    TotalToCustomer = 0,

    /// <summary>Intermediario: factura solo la comision. Fase 1 dispara manual review (GR-003).</summary>
    CommissionOnly = 1,
}
