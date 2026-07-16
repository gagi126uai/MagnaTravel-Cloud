using System;

namespace TravelApi.Application.DTOs;

public class InvoiceItemDto
{
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total { get; set; }
    public int AlicuotaIvaId { get; set; } // 3=0%, 4=10.5%, 5=21%, etc.

    // ============================================================
    // Trazabilidad polimorfica al servicio de origen (2026-07-16). Objetivo de negocio: al cancelar
    // UN servicio de una reserva con varias facturas, poder decirle al usuario en cual factura esta.
    // ============================================================

    /// <summary>
    /// En que tabla vive el servicio de origen de esta linea: "Generic" | "Flight" | "Hotel" |
    /// "Transfer" | "Package" | "Assistance" (nombres del enum <c>CancellableServiceTable</c>).
    ///
    /// <para><b>Suggested-items (GET)</b>: siempre viene completo — el armador de sugerencias
    /// (<c>InvoiceSuggestedItemsBuilder</c>) conoce el origen exacto de cada linea.</para>
    ///
    /// <para><b>Crear factura (POST)</b>: OPCIONAL. Es metadata de trazabilidad, no un dato critico:
    /// si el front no lo manda, o manda un valor que no matchea el enum, o lo manda sin su par
    /// <see cref="SourceServicePublicId"/>, el backend simplemente lo ignora (graba <c>null</c> en
    /// ambos campos) — nunca rechaza la factura por esto.</para>
    /// </summary>
    public string? SourceServiceTable { get; set; }

    /// <summary>
    /// <c>PublicId</c> del servicio concreto de origen de esta linea. Va junto con
    /// <see cref="SourceServiceTable"/>: o vienen los dos o se ignoran los dos (ver el comentario
    /// de arriba). El backend NO valida que el <c>PublicId</c> exista de verdad (es informativo,
    /// no se usa para ninguna regla de negocio: el costo de esa validacion no se justifica).
    /// </summary>
    public Guid? SourceServicePublicId { get; set; }
}
