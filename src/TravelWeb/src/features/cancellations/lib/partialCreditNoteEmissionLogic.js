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
    .map((invoice) => ({
      publicId: invoice.publicId,
      label: `Factura ${invoice.invoiceType || ""} ${String(invoice.puntoDeVenta ?? 0).padStart(4, "0")}-${String(invoice.numeroComprobante ?? 0).padStart(8, "0")}`.replace(/\s+/g, " ").trim(),
      amount: Number(invoice.importeTotal ?? 0),
    }));
}
