// Mismo helper que usa construirOpcionesFacturaDestino (facturaDestinoLogic.js) para
// el label "número + moneda + monto" del desplegable. Reusamos la función en vez de
// reimplementar el formato a mano para que los dos lugares SIEMPRE muestren la plata
// exactamente igual (ej. "$ 125.000,50" en ARS, "US$500,00" en USD) — evita que un
// cambio de formato en un lugar se desalinee del otro con el tiempo.
import { formatCurrency } from "../../../lib/utils.js";
import { getApiErrorMessage } from "../../../lib/errors.js";

export const T5_STATE = Object.freeze({
  EMPTY: "empty",
  READY: "ready",
  BLOCKED: "blocked",
  PROCESSING: "processing",
  SUCCEEDED: "succeeded",
  FAILED: "failed",
});

export function resolvePartialCreditNoteEmissionState(cancellation) {
  const summary = cancellation?.partialCreditNoteEmission;
  if (!summary) return T5_STATE.EMPTY;

  // Spec UX 2026-07-17 (T5 "resolver devoluciones VIEJAS"): con la lista de renglones (`Lines`),
  // el panel entero se queda mostrando la LISTA (BLOCKED) mientras falte resolver o emitir
  // CUALQUIER fila — sin importar el estado de la última nota de crédito del array plano de abajo,
  // que podría ser la de OTRA fila que ya terminó. Recién cuando TODAS las filas quedaron con su
  // nota de crédito emitida, el estado sigue el camino de siempre (mira la última NC del array).
  // Con 0 o 1 renglón (DTO legacy sin `Lines`, o compatibilidad hacia atrás) no aplica este atajo:
  // se sigue exactamente el comportamiento de siempre, para no romper el caso normal ya aprobado.
  const lines = Array.isArray(summary.lines) ? summary.lines : [];
  if (lines.length >= 2 && lines.some((line) => line.creditNoteStatus !== "Succeeded")) {
    return T5_STATE.BLOCKED;
  }

  const notes = Array.isArray(cancellation?.creditNotes) ? cancellation.creditNotes : [];
  const latest = notes.at(-1);
  if (latest?.status === "Succeeded") return T5_STATE.SUCCEEDED;
  if (latest?.status === "Failed") return T5_STATE.FAILED;
  if (latest?.status === "Pending" || cancellation?.status === "AwaitingFiscalConfirmation") {
    return T5_STATE.PROCESSING;
  }
  if (!summary.isResolved || summary.hasMultipleTargetInvoices || summary.requiresAccountantSignoffForRi) {
    return T5_STATE.BLOCKED;
  }
  return T5_STATE.READY;
}

export function getLatestPartialCreditNote(cancellation) {
  const notes = Array.isArray(cancellation?.creditNotes) ? cancellation.creditNotes : [];
  return notes.at(-1) ?? null;
}

export function getT5InvariantCode(error) {
  return error?.code ?? error?.payload?.invariantCode ?? error?.payload?.code ?? error?.data?.invariantCode ?? null;
}

export function t5ErrorMessage(error) {
  const code = getT5InvariantCode(error);
  if (code === "INV-T5-EMIT-CAP") return "El saldo de la factura cambió; revisá el monto.";
  if (code === "INV-T5-EMIT-RI-SIGNOFF") return "La devolución necesita la firma de un contador antes de emitirse.";
  if (code === "INV-T5-EMIT-MULTI-INVOICE" || code === "INV-T5-EMIT-UNRESOLVED") {
    return "Falta elegir o validar la factura correspondiente antes de emitir.";
  }
  if (error?.status === 403) return "No tenés permiso para emitir esta devolución.";
  // Resto de invariantes T5 (ej. INV-T5-EMIT-SIBLING-UNRESOLVED "todavía hay servicios sin
  // resolver...", INV-T5-ABORT-ALREADY-EMITTED): el backend ya manda un texto limpio en criollo
  // en el body (BusinessInvariantViolationException, sin jerga ni IDs) — no hace falta mapear cada
  // código acá, alcanza con mostrarlo tal cual.
  //
  // Retoque post-review (2026-07-17): solo mostramos un mensaje "libre" cuando viene del BODY real
  // del servidor (`error.payload`, el JSON que arma parseErrorResponse en api.js). Sin `payload`
  // (falla de red pura, timeout, error crudo de la librería HTTP) nunca leemos `error.message`
  // directo — antes sí, y ese campo puede traer texto técnico en inglés sin traducir (ej. un
  // statusText que ni siquiera esté en la lista de genéricos conocidos de lib/errors.js). Mejor
  // pecar de cauto en el fallback y mostrar SIEMPRE el genérico en criollo en ese caso.
  const backendMessage = error?.payload ? getApiErrorMessage(error, "") : "";
  return backendMessage || "No se pudo emitir la devolución. Intentá de nuevo.";
}

export function getActiveSaleInvoices(invoices) {
  const saleTypes = new Set([1, 6, 11, 51]);
  return (Array.isArray(invoices) ? invoices : [])
    .filter((invoice) => saleTypes.has(Number(invoice?.tipoComprobante)))
    .filter((invoice) => invoice?.resultado === "A" && Boolean(invoice?.cae ?? invoice?.CAE))
    .filter((invoice) => invoice?.annulmentStatus !== "Succeeded")
    .filter((invoice) => Boolean(invoice?.publicId))
    .map((invoice) => {
      const numeroComprobante = `Factura ${invoice.invoiceType || ""} ${String(invoice.puntoDeVenta ?? 0).padStart(4, "0")}-${String(invoice.numeroComprobante ?? 0).padStart(8, "0")}`.replace(/\s+/g, " ").trim();
      const amount = Number(invoice.importeTotal ?? 0);
      // InvoiceDto.Currency (2026-07-16): moneda ISO de la factura ("ARS"/"USD"), con
      // "ARS" como default legacy del propio backend (facturas viejas sin MonId
      // reconocible). Nunca viene null, así que no hace falta un fallback acá.
      const currency = invoice.currency || "ARS";
      return {
        publicId: invoice.publicId,
        // Pedido de Gaston (2026-07-01): número + moneda + monto — mismo formato que
        // construirOpcionesFacturaDestino (facturaDestinoLogic.js), reusando el mismo
        // formatCurrency para que ambos desplegables muestren la plata idéntica.
        label: `${numeroComprobante} — ${formatCurrency(amount, currency)}`,
        amount,
        currency,
        // Trazabilidad de servicios (2026-07-16, InvoiceDto.ServicePublicIds): permite
        // pre-seleccionar esta factura cuando se cancela un servicio que sabemos que
        // está adentro (ver serviceInvoiceMatch.js). Facturas viejas sin trazabilidad
        // traen [] y simplemente no participan de la sugerencia.
        servicePublicIds: Array.isArray(invoice.servicePublicIds) ? invoice.servicePublicIds : [],
      };
    });
}
