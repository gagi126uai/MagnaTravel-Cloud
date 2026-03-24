import { useState, useCallback, useEffect, useMemo } from "react";
import Swal from "sweetalert2";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getPublicId } from "../../../lib/publicIds";

const CREDIT_NOTE_TYPES = [3, 8, 13, 53];

const roundMoney = (value) => Math.round((Number(value) || 0) * 100) / 100;

const getInvoiceNetAmount = (invoice) => {
  if (invoice.resultado !== "A") {
    return 0;
  }

  return CREDIT_NOTE_TYPES.includes(invoice.tipoComprobante)
    ? -Number(invoice.importeTotal || 0)
    : Number(invoice.importeTotal || 0);
};

const getAfipStatus = (reserva, pendingAfipAmount) => {
  if (pendingAfipAmount <= 0) {
    return "done";
  }

  if (reserva.status === "Presupuesto" || reserva.status === "Cancelado") {
    return "blocked";
  }

  if (reserva.isEconomicallySettled) {
    return "enabled";
  }

  return reserva.canEmitAfipInvoice ? "override" : "blocked";
};

export function usePayments() {
  const [loading, setLoading] = useState(true);
  const [reservas, setReservas] = useState([]);
  const [payments, setPayments] = useState([]);
  const [invoices, setInvoices] = useState([]);
  const [movements, setMovements] = useState([]);
  const [summary, setSummary] = useState({
    accountsReceivable: 0,
    afipEligiblePending: 0,
    cashInThisMonth: 0,
    cashOutThisMonth: 0,
    netCashThisMonth: 0,
  });
  const [searchTerm, setSearchTerm] = useState("");

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const [reservasRes, invoicesRes, paymentsRes, summaryRes, movementsRes] = await Promise.all([
        api.get("/reservas"),
        api.get("/invoices"),
        api.get("/payments"),
        api.get("/treasury/summary"),
        api.get("/treasury/movements"),
      ]);

      const normalizedReservas = (reservasRes || []).map((reserva) => {
        const reservaInvoices = (invoicesRes || []).filter((invoice) => invoice.reservaPublicId === getPublicId(reserva));
        const totalSale = Number(reserva.totalSale || 0);
        const totalPaid = Number(reserva.totalPaid || 0);
        const approvedInvoiced = reservaInvoices.reduce((acc, invoice) => acc + getInvoiceNetAmount(invoice), 0);
        const pendingCollection = roundMoney(Math.max(Number(reserva.balance ?? totalSale - totalPaid), 0));
        const pendingAfipAmount = roundMoney(Math.max(totalSale - approvedInvoiced, 0));
        const afipStatus = getAfipStatus(reserva, pendingAfipAmount);

        return {
          ...reserva,
          invoices: reservaInvoices,
          computedPaid: roundMoney(totalPaid),
          computedInvoiced: roundMoney(approvedInvoiced),
          pendingCollection,
          pendingAfipAmount,
          afipStatus,
          totalSaleAmount: roundMoney(totalSale),
        };
      });

      const normalizedPayments = (paymentsRes || [])
        .map((payment) => ({
          ...payment,
          reserva:
            normalizedReservas.find((reserva) => getPublicId(reserva) === payment.reservaPublicId) ||
            (payment.reservaPublicId
              ? { publicId: payment.reservaPublicId, numeroReserva: payment.numeroReserva, customerName: "Reserva" }
              : null),
        }))
        .sort((a, b) => new Date(b.paidAt) - new Date(a.paidAt));

      const normalizedInvoices = (invoicesRes || [])
        .map((invoice) => ({
          ...invoice,
          reserva:
            normalizedReservas.find((reserva) => getPublicId(reserva) === invoice.reservaPublicId) ||
            invoice.reserva ||
            null,
        }))
        .sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));

      const normalizedMovements = (movementsRes || []).sort(
        (a, b) => new Date(b.occurredAt) - new Date(a.occurredAt)
      );

      setReservas(normalizedReservas);
      setPayments(normalizedPayments);
      setInvoices(normalizedInvoices);
      setMovements(normalizedMovements);
      setSummary({
        accountsReceivable: Number(summaryRes?.accountsReceivable || 0),
        afipEligiblePending: Number(summaryRes?.afipEligiblePending || 0),
        cashInThisMonth: Number(summaryRes?.cashInThisMonth || 0),
        cashOutThisMonth: Number(summaryRes?.cashOutThisMonth || 0),
        netCashThisMonth: Number(summaryRes?.netCashThisMonth || 0),
      });
    } catch (error) {
      console.error("Error loading payments module data:", error);
      showError("Error al cargar Cobranzas y Facturación");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);

  const handleDownloadPdf = async (invoice) => {
    try {
      const response = await api.get(`/invoices/${getPublicId(invoice)}/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([response]));
      const link = document.createElement("a");
      link.href = url;
      link.setAttribute("download", `Factura-${invoice.tipoComprobante}-${invoice.numeroComprobante}.pdf`);
      document.body.appendChild(link);
      link.click();
      link.remove();
    } catch (error) {
      showError("Error al descargar PDF");
    }
  };

  const handleViewPdf = async (invoice) => {
    try {
      const response = await api.get(`/invoices/${getPublicId(invoice)}/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([response], { type: "application/pdf" }));
      window.open(url, "_blank");
    } catch (error) {
      showError("Error al abrir PDF");
    }
  };

  const handleDownloadReceiptPdf = async (payment) => {
    try {
      const response = await api.get(`/payments/${getPublicId(payment)}/receipt/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([response], { type: "application/pdf" }));
      window.open(url, "_blank");
    } catch (error) {
      showError(error.message || "No se pudo abrir el comprobante.");
    }
  };

  const handleIssueReceipt = async (payment) => {
    try {
      await api.post(`/payments/${getPublicId(payment)}/receipt`);
      showSuccess("Comprobante emitido.");
      loadData();
    } catch (error) {
      showError(error.message || "No se pudo emitir el comprobante.");
    }
  };

  const handleRetryInvoice = async (invoice) => {
    try {
      await api.post(`/invoices/${getPublicId(invoice)}/retry`);
      showSuccess("Reintento encolado.");
      loadData();
    } catch (error) {
      showError("Error al reintentar.");
    }
  };

  const handleAnnulInvoice = async (invoice) => {
    const result = await Swal.fire({
      title: "¿Anular factura?",
      text: "Se generará una Nota de Crédito. ¿Continuar?",
      icon: "warning",
      showCancelButton: true,
      confirmButtonText: "Sí, anular",
      cancelButtonText: "Cancelar",
      confirmButtonColor: "#0f172a",
    });

    if (!result.isConfirmed) {
      return;
    }

    try {
      const response = await api.post(`/invoices/${getPublicId(invoice)}/annul`);
      showSuccess(response?.message || response?.Message || "Anulación encolada.");
      loadData();
    } catch (error) {
      showError(error.message || "Error al anular");
    }
  };

  const handleCreateManualMovement = async (payload) => {
    try {
      await api.post("/treasury/manual-movements", payload);
      showSuccess("Movimiento manual registrado.");
      loadData();
    } catch (error) {
      showError(error.message || "No se pudo registrar el movimiento.");
      throw error;
    }
  };

  const handleUpdateManualMovement = async (id, payload) => {
    try {
      await api.put(`/treasury/manual-movements/${id}`, payload);
      showSuccess("Movimiento manual actualizado.");
      loadData();
    } catch (error) {
      showError(error.message || "No se pudo actualizar el movimiento.");
      throw error;
    }
  };

  const handleDeleteManualMovement = async (movement) => {
    const result = await Swal.fire({
      title: "¿Anular movimiento manual?",
      text: "El movimiento dejará de impactar en caja.",
      icon: "warning",
      showCancelButton: true,
      confirmButtonText: "Sí, anular",
      cancelButtonText: "Cancelar",
      confirmButtonColor: "#0f172a",
    });

    if (!result.isConfirmed) {
      return;
    }

    try {
      await api.delete(`/treasury/manual-movements/${movement.sourceId}`);
      showSuccess("Movimiento anulado.");
      loadData();
    } catch (error) {
      showError(error.message || "No se pudo anular el movimiento.");
    }
  };

  const filteredReservas = useMemo(() => {
    const needle = searchTerm.trim().toLowerCase();
    if (!needle) {
      return reservas;
    }

    return reservas.filter((reserva) => {
      return (
        reserva.name?.toLowerCase().includes(needle) ||
        reserva.numeroReserva?.toLowerCase().includes(needle) ||
        reserva.customerName?.toLowerCase().includes(needle) ||
        reserva.responsibleUserName?.toLowerCase().includes(needle)
      );
    });
  }, [reservas, searchTerm]);

  const filteredMovements = useMemo(() => {
    const needle = searchTerm.trim().toLowerCase();
    if (!needle) {
      return movements;
    }

    return movements.filter((movement) => {
      return (
        movement.description?.toLowerCase().includes(needle) ||
        movement.reference?.toLowerCase().includes(needle) ||
        movement.numeroReserva?.toLowerCase().includes(needle) ||
        movement.supplierName?.toLowerCase().includes(needle) ||
        movement.method?.toLowerCase().includes(needle)
      );
    });
  }, [movements, searchTerm]);

  const stats = useMemo(() => {
    const currentMonth = new Date().getMonth();
    const currentYear = new Date().getFullYear();
    const totalInvoicedMonth = invoices.reduce((acc, invoice) => {
      const createdAt = new Date(invoice.createdAt);
      if (
        invoice.resultado !== "A" ||
        createdAt.getMonth() !== currentMonth ||
        createdAt.getFullYear() !== currentYear
      ) {
        return acc;
      }

      return acc + getInvoiceNetAmount(invoice);
    }, 0);

    return {
      accountsReceivable: roundMoney(summary.accountsReceivable),
      afipEligiblePending: roundMoney(summary.afipEligiblePending),
      cashInThisMonth: roundMoney(summary.cashInThisMonth),
      cashOutThisMonth: roundMoney(summary.cashOutThisMonth),
      netCashThisMonth: roundMoney(summary.netCashThisMonth),
      totalInvoicedMonth: roundMoney(totalInvoicedMonth),
    };
  }, [invoices, summary]);

  return {
    loading,
    reservas,
    filteredReservas,
    payments,
    invoices,
    movements,
    filteredMovements,
    searchTerm,
    setSearchTerm,
    stats,
    loadData,
    handleDownloadPdf,
    handleViewPdf,
    handleDownloadReceiptPdf,
    handleIssueReceipt,
    handleRetryInvoice,
    handleAnnulInvoice,
    handleCreateManualMovement,
    handleUpdateManualMovement,
    handleDeleteManualMovement,
  };
}
