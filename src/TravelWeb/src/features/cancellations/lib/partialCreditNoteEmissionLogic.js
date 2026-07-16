// Mismo helper que usa construirOpcionesFacturaDestino (facturaDestinoLogic.js) para
// el label "número + moneda + monto" del desplegable. Reusamos la función en vez de
// reimplementar el formato a mano para que los dos lugares SIEMPRE muestren la plata
// exactamente igual (ej. "$ 125.000,50" en ARS, "US$500,00" en USD) — evita que un
// cambio de formato en un lugar se desalinee del otro con el tiempo.
import { formatCurrency } from "../../../lib/utils.js";

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
  return error?.payload?.message ?? error?.message ?? "No se pudo emitir la devolución. Intentá de nuevo.";
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
