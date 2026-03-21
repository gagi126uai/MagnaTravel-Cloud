import { useState } from "react";
import {
  Search,
  Banknote,
  ArrowLeftRight,
  FileText,
  Clock,
  Filter,
  Loader2,
} from "lucide-react";

import { isAdmin } from "../../../auth";
import { usePayments } from "../hooks/usePayments";
import PaymentModal from "../../../components/PaymentModal";
import CreateInvoiceModal from "../../../components/CreateInvoiceModal";
import { PaymentKPIs } from "../components/PaymentKPIs";
import { CollectionsTab } from "../components/CollectionsTab";
import { MovementsTab } from "../components/MovementsTab";
import { InvoicingTab } from "../components/InvoicingTab";
import { HistoryTab } from "../components/HistoryTab";

export default function PaymentsPage() {
  const [activeTab, setActiveTab] = useState("collections");
  const [showPaymentModal, setShowPaymentModal] = useState(false);
  const [showInvoiceModal, setShowInvoiceModal] = useState(false);
  const [selectedReserva, setSelectedReserva] = useState(null);
  const adminUser = isAdmin();

  const {
    loading,
    filteredReservas,
    filteredMovements,
    payments,
    invoices,
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
  } = usePayments();

  const handleOpenPayment = (reserva) => {
    setSelectedReserva(reserva);
    setShowPaymentModal(true);
  };

  const handleOpenInvoice = (reserva) => {
    if (reserva.status === "Presupuesto" || reserva.afipStatus === "blocked") {
      return;
    }

    setSelectedReserva(reserva);
    setShowInvoiceModal(true);
  };

  if (loading && filteredReservas.length === 0) {
    return (
      <div className="flex justify-center items-center h-64 text-slate-400">
        <Loader2 className="w-8 h-8 animate-spin" />
      </div>
    );
  }

  return (
    <div className="max-w-7xl mx-auto p-4 sm:p-8 space-y-8 pb-20">
      <div className="flex flex-col md:flex-row md:items-end justify-between gap-6 pb-6 border-b border-slate-100 dark:border-slate-800/50">
        <div>
          <h1 className="text-3xl font-light tracking-tight text-slate-900 dark:text-white mb-1">
            Cobranzas y Facturación
          </h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Gestioná cobranzas, movimientos de caja y facturación AFIP con control económico.
          </p>
        </div>

        <div className="flex items-center gap-3">
          <div className="relative w-full md:w-72">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
            <input
              type="text"
              placeholder="Buscar reservas, clientes, referencias..."
              value={searchTerm}
              onChange={(event) => setSearchTerm(event.target.value)}
              className="pl-9 pr-4 py-2 w-full text-sm bg-slate-50 dark:bg-slate-900 border-none rounded-full focus:ring-2 focus:ring-slate-200 transition-shadow dark:text-white"
            />
          </div>
          <button
            className="p-2 text-slate-400 hover:text-slate-600 bg-slate-50 dark:bg-slate-900 rounded-full transition-colors"
            title="Filtros avanzados"
            type="button"
          >
            <Filter className="w-4 h-4" />
          </button>
        </div>
      </div>

      <PaymentKPIs stats={stats} />

      <div className="flex gap-6 border-b border-slate-100 dark:border-slate-800 overflow-x-auto">
        {[
          { id: "collections", label: "Cobranzas", icon: Banknote },
          { id: "movements", label: "Movimientos", icon: ArrowLeftRight },
          { id: "invoicing", label: "Facturación AFIP", icon: FileText },
          { id: "history", label: "Historial", icon: Clock },
        ].map((tab) => (
          <button
            key={tab.id}
            onClick={() => setActiveTab(tab.id)}
            className={`pb-3 text-sm font-medium transition-colors relative whitespace-nowrap ${
              activeTab === tab.id
                ? "text-slate-900 dark:text-white"
                : "text-slate-400 hover:text-slate-600"
            }`}
            type="button"
          >
            <div className="flex items-center gap-2">
              <tab.icon className="w-4 h-4" />
              {tab.label}
            </div>
            {activeTab === tab.id && (
              <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-slate-900 dark:bg-white rounded-t-full" />
            )}
          </button>
        ))}
      </div>

      <div className="min-h-[400px]">
        {activeTab === "collections" && (
          <CollectionsTab reservas={filteredReservas} onPay={handleOpenPayment} />
        )}
        {activeTab === "movements" && (
          <MovementsTab
            movements={filteredMovements}
            isAdmin={adminUser}
            onCreateManualMovement={handleCreateManualMovement}
            onUpdateManualMovement={handleUpdateManualMovement}
            onDeleteManualMovement={handleDeleteManualMovement}
          />
        )}
        {activeTab === "invoicing" && (
          <InvoicingTab reservas={filteredReservas} onInvoice={handleOpenInvoice} />
        )}
        {activeTab === "history" && (
          <HistoryTab
            payments={payments}
            invoices={invoices}
            onDownloadPdf={handleDownloadPdf}
            onViewPdf={handleViewPdf}
            onDownloadReceiptPdf={handleDownloadReceiptPdf}
            onIssueReceipt={handleIssueReceipt}
            onAnnulInvoice={handleAnnulInvoice}
            onRetryInvoice={handleRetryInvoice}
          />
        )}
      </div>

      <PaymentModal
        isOpen={showPaymentModal}
        onClose={() => {
          setShowPaymentModal(false);
          setSelectedReserva(null);
        }}
        reservaId={selectedReserva?.id}
        maxAmount={selectedReserva?.pendingCollection}
        onSuccess={loadData}
      />

      <CreateInvoiceModal
        isOpen={showInvoiceModal}
        onClose={() => {
          setShowInvoiceModal(false);
          setSelectedReserva(null);
        }}
        reservaId={selectedReserva?.id}
        reserva={selectedReserva}
        initialAmount={selectedReserva?.pendingAfipAmount}
        clientName={selectedReserva?.customerName}
        clientCuit={selectedReserva?.taxId || selectedReserva?.documentNumber}
        onSuccess={loadData}
      />
    </div>
  );
}
