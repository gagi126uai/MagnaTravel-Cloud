using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class InvoiceItem
{
    public int Id { get; set; }

    public int InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Quantity { get; set; } = 1;

    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; } // Quantity * UnitPrice

    // AFIP VAT ID (3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%, 9=2.5%)
    public int AlicuotaIvaId { get; set; } 
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal ImporteIva { get; set; } // Calculated VAT amount for this item

    // ============================================================
    // FC1.3 (ADR-009 §2.3.2, 2026-05-21): tres campos nuevos para
    // soportar NC parcial. Default conservador: todo item legacy queda
    // como reintegrable, sin categoria especifica, sin trazabilidad al
    // servicio origen. La logica de defaults segun categoria (G1) vive
    // en el SERVICE que crea el InvoiceItem, NO aca.
    // ============================================================

    /// <summary>
    /// FC1.3 (ADR-009): si <c>false</c>, este item NO entra en el calculo del monto
    /// a acreditar en una NC parcial. Default <c>true</c> (compat backward).
    ///
    /// <para>Ejemplo pelotudo: cliente cancela el hotel y la agencia retiene un cargo
    /// de gestion de $5.000. Ese cargo se factura como item con
    /// <c>IsRefundable=false</c>: cuando se emite la NC parcial, el monto fiscal
    /// acreditado excluye esos $5.000. La factura original sigue viva por $5.000 +
    /// IVA correspondiente.</para>
    ///
    /// <para>INMUTABLE post-emision de factura: cualquier cambio debe pasar por los
    /// <c>Invoice.MutationGuards</c> (la factura ya tiene CAE). Esto se enforce a
    /// nivel service, no a nivel CHECK SQL (la regla depende del status de la factura).</para>
    /// </summary>
    public bool IsRefundable { get; set; } = true;

    /// <summary>
    /// FC1.3 (ADR-009): clasificacion del item. Usada por la UI para alertas y por
    /// el service que crea el item para preseleccionar <c>IsRefundable=false</c>
    /// cuando la categoria es <see cref="InvoiceItemCategory.Insurance"/>,
    /// <see cref="InvoiceItemCategory.AdministrativeFee"/> o
    /// <see cref="InvoiceItemCategory.OperatorAdvance"/> (regla G1 del ADR).
    /// Default <see cref="InvoiceItemCategory.Service"/> (compat backward).
    /// </summary>
    public InvoiceItemCategory ItemCategory { get; set; } = InvoiceItemCategory.Service;

    /// <summary>
    /// FC1.3 (ADR-009): trazabilidad linea de factura -&gt; servicio reservado de
    /// origen. Habilita el calculo "que linea de factura pertenece a que servicio
    /// cancelado". Nullable porque facturas legacy o conceptos sueltos
    /// (cargo de gestion suelto, por ejemplo) no tienen servicio origen.
    /// </summary>
    public int? SourceServicioReservaId { get; set; }
    public ServicioReserva? SourceServicioReserva { get; set; }

    // ============================================================
    // Trazabilidad polimorfica linea de factura -> servicio de origen (2026-07-16).
    // Objetivo de negocio: cuando el cliente cancela UN servicio de una reserva con
    // varias facturas, poder decirle en cual factura esta ese servicio.
    //
    // Por que dos campos nuevos y no ampliar SourceServicioReservaId de arriba: esa FK
    // SOLO puede apuntar a la tabla generica ServicioReserva. Los servicios de la
    // reserva son POLIMORFICOS (viven en 6 tablas distintas: la generica + vuelo,
    // hotel, traslado, paquete, asistencia — ver CancellableServiceTable). Mismo
    // patron ya usado en SupplierPayment.ServicePublicId (ADR-036 4c): en vez de una
    // FK de base de datos (que obligaria a 6 columnas FK nullable, una por tabla), se
    // guarda el PAR (tabla, PublicId) SIN constraint de FK. La integridad no se valida
    // al escribir: es metadata informativa para una sugerencia, no un dato critico de
    // negocio — si el PublicId quedara huerfano, lo peor que pasa es que la sugerencia
    // no aparece, nunca se rompe la factura.
    // ============================================================

    /// <summary>
    /// En que tabla vive el servicio de origen de esta linea (Generic/Flight/Hotel/Transfer/
    /// Package/Assistance). Va junto con <see cref="SourceServicePublicId"/>: o ambos estan
    /// seteados o ambos quedan <c>null</c>. <c>null</c> en items legacy o conceptos sueltos sin
    /// servicio de origen (ej. un cargo de gestion suelto).
    /// </summary>
    public CancellableServiceTable? SourceServiceTable { get; set; }

    /// <summary>
    /// <c>PublicId</c> del servicio concreto de origen de esta linea. Polimorfico (NO es una FK
    /// de base de datos: el servicio puede estar en cualquiera de las 6 tablas de
    /// <see cref="CancellableServiceTable"/>). <c>null</c> si la linea no tiene servicio de
    /// origen puntual.
    /// </summary>
    public Guid? SourceServicePublicId { get; set; }
}
