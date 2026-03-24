import { useState } from "react";
import { Loader2, Search } from "lucide-react";
import CreateInvoiceModal from "../../../components/CreateInvoiceModal";
import { FinanceMetricsGrid } from "../components/FinanceMetricsGrid";
import { InvoicingTab } from "../components/InvoicingTab";
import { useInvoicing } from "../hooks/useInvoicing";

export default function PaymentsInvoicingPage() {
  const [selectedItem, setSelectedItem] = useState(null);
  const {
    loading,
    summary,
    workItems,
    invoices,
    issuedInvoices,
    creditNotes,
    searchTerm,
    setSearchTerm,
    loadData,
    handleDownloadPdf,
    handleViewPdf,
    handleRetryInvoice,
    handleAnnulInvoice,
  } = useInvoicing();

  if (loading && workItems.length === 0 && invoices.length === 0) {
    return (
      <div className="flex justify-center items-center h-64 text-slate-400">
        <Loader2 className="w-8 h-8 animate-spin" />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex justify-end">
        <div className="relative min-w-[260px]">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
          <input
            type="text"
            placeholder="Buscar reserva, cliente o comprobante..."
            value={searchTerm}
            onChange={(event) => setSearchTerm(event.target.value)}
            className="pl-9 pr-4 py-2 w-full text-sm bg-slate-50 dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-xl focus:ring-2 focus:ring-slate-200 transition-shadow dark:text-white"
          />
        </div>
      </div>

      <FinanceMetricsGrid
        items={[
          { label: "Listo para facturar", value: summary?.readyAmount || 0 },
          { label: "Reservas listas", value: summary?.readyCount || 0, isCount: true },
          { label: "Bloqueadas por deuda", value: summary?.blockedCount || 0, isCount: true },
          { label: "Facturado en AFIP este mes", value: summary?.invoicedThisMonth || 0 },
          { label: "Emitidas por excepcion", value: summary?.forcedCount || 0, isCount: true },
        ]}
        columns="md:grid-cols-2 xl:grid-cols-5"
      />

      <InvoicingTab
        items={workItems}
        issuedInvoices={issuedInvoices}
        creditNotes={creditNotes}
        onInvoice={setSelectedItem}
        onDownloadPdf={handleDownloadPdf}
        onViewPdf={handleViewPdf}
        onRetryInvoice={handleRetryInvoice}
        onAnnulInvoice={handleAnnulInvoice}
      />

      <CreateInvoiceModal
        isOpen={Boolean(selectedItem)}
        onClose={() => setSelectedItem(null)}
        reservaPublicId={selectedItem?.reservaPublicId || selectedItem?.reservaId}
        reserva={{
          publicId: selectedItem?.reservaPublicId || selectedItem?.reservaId,
          numeroReserva: selectedItem?.numeroReserva,
          customerName: selectedItem?.customerName,
          afipStatus: selectedItem?.fiscalStatus,
          canEmitAfipInvoice:
            selectedItem?.fiscalStatus === "ready" || selectedItem?.fiscalStatus === "override",
          isEconomicallySettled: selectedItem?.fiscalStatus === "ready",
          economicBlockReason: selectedItem?.economicBlockReason,
        }}
        initialAmount={selectedItem?.pendingFiscalAmount}
        clientName={selectedItem?.customerName}
        clientCuit={null}
        onSuccess={async () => {
          setSelectedItem(null);
          await loadData();
        }}
      />
    </div>
  );
}
