import { useCallback, useEffect, useMemo, useState } from "react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { getInvoiceLabel, isCreditNote } from "../lib/financeUtils";
import { useFinanceActions } from "./useFinanceActions";
import { getPublicId } from "../../../lib/publicIds";

export function useFinanceHistory() {
  const [loading, setLoading] = useState(true);
  const [payments, setPayments] = useState([]);
  const [invoices, setInvoices] = useState([]);
  const [movements, setMovements] = useState([]);
  const [searchTerm, setSearchTerm] = useState("");

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const [paymentsRes, invoicesRes, movementsRes] = await Promise.all([
        api.get("/payments"),
        api.get("/invoices"),
        api.get("/treasury/movements"),
      ]);

      setPayments((paymentsRes || []).sort((a, b) => new Date(b.paidAt) - new Date(a.paidAt)));
      setInvoices((invoicesRes || []).sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt)));
      setMovements((movementsRes || []).filter((movement) => movement.isManual));
    } catch (error) {
      console.error("Error loading finance history:", error);
      showError("Error al cargar historial.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);

  const actions = useFinanceActions(loadData);

  const timeline = useMemo(() => {
    const paymentItems = payments.map((payment) => ({
      id: `payment-${getPublicId(payment)}`,
      date: payment.paidAt,
      kind: payment.entryType === "CreditNoteReversal" ? "Reversion" : "Cobranza",
      title: payment.entryType === "CreditNoteReversal" ? "Reversion por nota de credito" : "Cobranza recibida",
      subtitle: payment.numeroReserva ? `Reserva ${payment.numeroReserva}` : "Sin reserva",
      amount: payment.amount,
      searchable: `${payment.numeroReserva || ""} ${payment.reference || ""} ${payment.method || ""}`,
      entity: payment,
      entityType: "payment",
    }));

    const invoiceItems = invoices.map((invoice) => ({
      id: `invoice-${getPublicId(invoice)}`,
      date: invoice.createdAt,
      kind: isCreditNote(invoice) ? "Nota de credito" : "Factura AFIP",
      title: getInvoiceLabel(invoice.tipoComprobante),
      subtitle: invoice.reserva?.numeroReserva ? `Reserva ${invoice.reserva.numeroReserva}` : "Sin reserva",
      amount: invoice.importeTotal,
      searchable: `${invoice.reserva?.numeroReserva || ""} ${invoice.numeroComprobante || ""} ${invoice.forceReason || ""}`,
      entity: invoice,
      entityType: "invoice",
    }));

    const manualMovementItems = movements.map((movement) => ({
      id: `movement-${movement.sourceId}`,
      date: movement.occurredAt,
      kind: "Caja",
      title: movement.description || "Movimiento manual",
      subtitle: movement.reference || movement.numeroReserva || movement.supplierName || "Ajuste manual",
      amount: movement.direction === "Expense" ? -Math.abs(Number(movement.amount || 0)) : Number(movement.amount || 0),
      searchable: `${movement.description || ""} ${movement.reference || ""} ${movement.numeroReserva || ""} ${movement.supplierName || ""}`,
      entity: movement,
      entityType: "movement",
    }));

    return paymentItems
      .concat(invoiceItems)
      .concat(manualMovementItems)
      .sort((a, b) => new Date(b.date) - new Date(a.date));
  }, [payments, invoices, movements]);

  const filteredTimeline = useMemo(() => {
    const needle = searchTerm.trim().toLowerCase();
    if (!needle) {
      return timeline;
    }

    return timeline.filter((item) => {
      return (
        item.title?.toLowerCase().includes(needle) ||
        item.subtitle?.toLowerCase().includes(needle) ||
        item.kind?.toLowerCase().includes(needle) ||
        item.searchable?.toLowerCase().includes(needle)
      );
    });
  }, [timeline, searchTerm]);

  return {
    loading,
    timeline: filteredTimeline,
    searchTerm,
    setSearchTerm,
    loadData,
    ...actions,
  };
}
