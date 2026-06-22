namespace TravelApi.Application.DTOs;

/// <summary>
/// "Estado de Cuenta" de la reserva como LIBRO MAYOR (extracto estilo banco). Es un read-model DERIVADO
/// (no se persiste): una linea cronologica por cada comprobante/cobro VIVO, donde factura/ND SUMAN (cargo)
/// y cobro/NC RESTAN (abono), con saldo corriente, SEPARADO por moneda.
///
/// <para><b>SEGURIDAD</b>: el extracto es venta/cobranza PURA. NINGUN campo de costo ni margen viaja aca,
/// por eso este DTO NO pasa por el enmascarado de costo (no hay nada que ocultar). Ver ReservaService.</para>
/// </summary>
public class ReservaAccountStatementDto
{
    /// <summary>PublicId de la reserva del extracto (eco del pedido, para que el front lo correlacione).</summary>
    public Guid ReservaPublicId { get; set; }

    /// <summary>Un bloque por cada moneda presente (ARS/USD/...). Vacio si la reserva no tiene comprobantes ni cobros.</summary>
    public List<AccountStatementCurrencyBlockDto> Currencies { get; set; } = new();
}

/// <summary>
/// Bloque del extracto de UNA moneda: sus lineas en orden cronologico y su saldo de cierre.
/// </summary>
public class AccountStatementCurrencyBlockDto
{
    /// <summary>Moneda del bloque ("ARS"/"USD"), forma canonica.</summary>
    public string Currency { get; set; } = "ARS";

    /// <summary>Lineas del extracto de esta moneda, en orden cronologico (con saldo corriente en cada una).</summary>
    public List<AccountStatementLineDto> Lines { get; set; } = new();

    /// <summary>
    /// Saldo de cierre de la moneda = saldo corriente de la ultima linea (positivo = el cliente debe;
    /// negativo = saldo a favor). Coincide con el saldo POR MONEDA del detalle de la reserva cuando lo
    /// facturado iguala lo confirmado.
    /// </summary>
    public decimal ClosingBalance { get; set; }
}

/// <summary>
/// Una linea del extracto. Estilo banco: <see cref="Charge"/> SUMA a la deuda (factura/ND), <see cref="Credit"/>
/// la RESTA (cobro/NC). Una linea trae uno u otro (el otro en 0). <see cref="RunningBalance"/> es el saldo
/// acumulado de la moneda hasta esta linea inclusive.
/// </summary>
public class AccountStatementLineDto
{
    /// <summary>Fecha del movimiento (emision del comprobante o fecha del cobro).</summary>
    public DateTime Date { get; set; }

    /// <summary>Tipo de movimiento: "Invoice" / "CreditNote" / "DebitNote" / "Payment".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Texto legible del movimiento (nº de recibo o metodo de cobro; descripcion del comprobante).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Referencia del documento (ej. "0001-00000123" para una factura), o null si no aplica.</summary>
    public string? DocumentRef { get; set; }

    /// <summary>Moneda de la linea (igual que la del bloque).</summary>
    public string Currency { get; set; } = "ARS";

    /// <summary>Monto que SUMA a la deuda (factura/ND). 0 si la linea es un abono. Siempre en positivo.</summary>
    public decimal Charge { get; set; }

    /// <summary>Monto que RESTA de la deuda (cobro/NC). 0 si la linea es un cargo. Siempre en positivo.</summary>
    public decimal Credit { get; set; }

    /// <summary>Saldo corriente de la moneda hasta esta linea inclusive (positivo = debe; negativo = a favor).</summary>
    public decimal RunningBalance { get; set; }
}
