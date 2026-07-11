/**
 * Desplegable compartido "¿A qué factura del cliente corresponde?" (ADR-044 T4, spec
 * sección 2.2). Se usa en LOS TRES lugares donde hace falta elegir la factura destino
 * de un cargo del operador:
 *   - ConfirmarMultaOperadorInline (modo "confirmar"): al confirmar la multa.
 *   - AgregarOtroCargoOperadorInline: al cargar el segundo cargo.
 *   - ElegirFacturaDestinoInline: al corregir un cargo que quedó trabado sin factura.
 *
 * Caso simple (1 sola factura activa): este componente NO RENDERIZA NADA — el sistema
 * usa esa factura sola, sin preguntar (regla "la complejidad se esconde con defaults").
 * Formato de las opciones ya aprobado (2026-07-01, anulación multifactura): número +
 * moneda + monto. Términos fiscales permitidos acá (es facturación de la ficha).
 */

import { hayFacturaDestinoAmbigua, construirOpcionesFacturaDestino } from "../lib/facturaDestinoLogic";

/**
 * Props:
 *   - saleInvoices: BookingCancellationDto.SaleInvoices (con publicId, comprobanteLabel, currency, amount).
 *   - value: publicId de la factura elegida, o "" si todavía no se eligió ninguna.
 *   - onChange: (publicId: string) => void
 *   - disabled: boolean
 *   - testId: data-testid del <select> (cada pantalla usa uno propio y estable).
 */
export function FacturaDestinoSelect({ saleInvoices, value, onChange, disabled, testId }) {
  if (!hayFacturaDestinoAmbigua(saleInvoices)) return null;

  const opciones = construirOpcionesFacturaDestino(saleInvoices);

  return (
    <div data-testid={`${testId}-wrapper`}>
      <label className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5" htmlFor={testId}>
        ¿A qué factura del cliente corresponde? <span className="text-rose-500" aria-hidden="true">*</span>
      </label>
      <select
        id={testId}
        value={value ?? ""}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        data-testid={testId}
        className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-400"
      >
        <option value="" disabled>
          Elegí una factura…
        </option>
        {opciones.map((opcion) => (
          <option key={opcion.value} value={opcion.value}>
            {opcion.label}
          </option>
        ))}
      </select>
    </div>
  );
}
