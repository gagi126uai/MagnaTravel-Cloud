using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-017 F1.1 (catalogo find-or-create, 2026-06-05): memoria de "ultima venta por producto y
/// operador". Es la fuente VIVA de la sugerencia del buscador ("la ultima vez fue con Ola Mayorista a
/// $X la noche"): una fila por cada combinacion (producto, operador) que se vendio alguna vez.
///
/// <para><b>Por que es una tabla aparte y no campos en Rate</b>: un mismo producto se puede vender con
/// distintos operadores y cada combinacion recuerda SU ultimo precio. Si lo guardaramos en Rate
/// perderiamos la memoria por-operador y corromperiamos el precio curado del tarifario.</para>
///
/// <para><b>Quien la escribe</b>: en F1.1 NADIE todavia. La estructura se crea ahora; el upsert atomico
/// (<c>INSERT ... ON CONFLICT (RateId, SupplierId) DO UPDATE ... SalesCount + 1</c>) que la llena en cada
/// venta es F1.3. Por eso en esta fase la tabla nace vacia y sin escritores.</para>
///
/// <para><b>Unitarizacion</b>: los montos (<see cref="LastNetCost"/>, <see cref="LastTax"/>,
/// <see cref="LastSalePrice"/>) se guardan SIEMPRE como precio UNITARIO segun <see cref="LastPriceUnit"/>
/// (hotel = por noche por habitacion, aereo/paquete = por pasajero, etc. — tabla de unitarizacion del ADR
/// §2.1). Asi la sugerencia se puede re-multiplicar por las noches/pasajeros de la proxima venta.</para>
/// </summary>
public class RateSupplierSale
{
    public int Id { get; set; }

    // Producto (Rate). FK CASCADE: si se borra el Rate, sus filas de venta se van con el.
    public int RateId { get; set; }
    public Rate? Rate { get; set; }

    // Operador con el que se vendio esta vez. FK RESTRICT (red de seguridad C24, igual que los
    // bookings tipados): no permitir borrar un Supplier que tenga historial de ventas asociado.
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    // Momento (UTC) de la ultima venta de esta combinacion. Ordena el "ultimo precio" del dropdown.
    public DateTime LastSoldAt { get; set; }

    /// <summary>Costo neto UNITARIO de la ultima venta (segun <see cref="LastPriceUnit"/>).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal LastNetCost { get; set; }

    /// <summary>
    /// Impuesto UNITARIO incluido en el costo (mismo criterio que <see cref="Rate.Tax"/>). Se guarda
    /// aparte del neto porque la cadena de reposicion de costo (D7, F1.3) necesita reponer ambos.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal LastTax { get; set; }

    /// <summary>Precio de venta UNITARIO de la ultima venta (segun <see cref="LastPriceUnit"/>).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal LastSalePrice { get; set; }

    /// <summary>
    /// Moneda de ESA venta (ARS / USD). Nullable: el path best-effort de la conversion de presupuesto
    /// puede dejarla null cuando el item no traia moneda. La cadena D7 trata null como "no comparable".
    /// </summary>
    [MaxLength(3)]
    public string? LastCurrency { get; set; }

    /// <summary>
    /// Unidad en que estan expresados los montos unitarios (valores cerrados de la tabla §2.1 del ADR):
    /// <c>noche_habitacion</c> (hotel), <c>pasajero</c> (aereo/paquete), <c>servicio</c> (traslado),
    /// <c>pasajero_dia</c> (asistencia). Sin esto la sugerencia seria ambigua al re-multiplicar.
    /// </summary>
    [Required]
    [MaxLength(30)]
    public string LastPriceUnit { get; set; } = string.Empty;

    /// <summary>Cuantas veces se vendio esta combinacion (lo incrementa el upsert atomico en F1.3).</summary>
    public int SalesCount { get; set; }
}
