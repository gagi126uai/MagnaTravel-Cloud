import { useState } from "react";
import { Loader2, Search } from "lucide-react";
import CreateInvoiceModal from "../../../components/CreateInvoiceModal";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
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
    searchTerm,
    setSearchTerm,
    worklistStatus,
    setWorklistStatus,
    worklistPage,
    worklistPageSize,
    worklistTotalCount,
    worklistTotalPages,
    worklistHasPreviousPage,
    worklistHasNextPage,
    setWorklistPage,
    setWorklistPageSize,
    invoiceKind,
    setInvoiceKind,
    invoicePage,
    invoicePageSize,
    invoiceTotalCount,
    invoiceTotalPages,
    invoiceHasPreviousPage,
    invoiceHasNextPage,
    setInvoicePage,
    setInvoicePageSize,
    loadData,
    handleDownloadPdf,
    handleViewPdf,
    handleRetryInvoice,
    handleAnnulInvoice,
    databaseUnavailable,
  } = useInvoicing();

  if (loading && workItems.length === 0 && invoices.length === 0) {
    return (
      <div className="flex h-64 items-center justify-center text-slate-400">
        <Loader2 className="h-8 w-8 animate-spin" />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex justify-end">
        <div className="relative min-w-[260px]">
          <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <input
            type="text"
            placeholder="Buscar reserva, cliente o comprobante..."
            value={searchTerm}
            onChange={(event) => setSearchTerm(event.target.value)}
            className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm transition-shadow focus:ring-2 focus:ring-slate-200 dark:border-slate-800 dark:bg-slate-900 dark:text-white"
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

      {databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : (
        <>
          <InvoicingTab
            items={workItems}
            invoices={invoices}
            worklistStatus={worklistStatus}
            onWorklistStatusChange={setWorklistStatus}
            invoiceKind={invoiceKind}
            onInvoiceKindChange={setInvoiceKind}
            onInvoice={setSelectedItem}
            onDownloadPdf={handleDownloadPdf}
            onViewPdf={handleViewPdf}
            onRetryInvoice={handleRetryInvoice}
            onAnnulInvoice={handleAnnulInvoice}
          />

          <div className="space-y-3">
            <div className="text-xs font-semibold uppercase tracking-[0.24em] text-slate-400">
              Worklist AFIP
            </div>
            <PaginationFooter
              page={worklistPage}
              pageSize={worklistPageSize}
              totalCount={worklistTotalCount}
              totalPages={worklistTotalPages}
              hasPreviousPage={worklistHasPreviousPage}
              hasNextPage={worklistHasNextPage}
              onPageChange={setWorklistPage}
              onPageSizeChange={setWorklistPageSize}
            />
          </div>

          <div className="space-y-3">
            <div className="text-xs font-semibold uppercase tracking-[0.24em] text-slate-400">
              Comprobantes AFIP
            </div>
            <PaginationFooter
              page={invoicePage}
              pageSize={invoicePageSize}
              totalCount={invoiceTotalCount}
              totalPages={invoiceTotalPages}
              hasPreviousPage={invoiceHasPreviousPage}
              hasNextPage={invoiceHasNextPage}
              onPageChange={setInvoicePage}
              onPageSizeChange={setInvoicePageSize}
            />
          </div>
        </>
      )}

      <CreateInvoiceModal
        isOpen={Boolean(selectedItem)}
        onClose={() => setSelectedItem(null)}
        reservaPublicId={selectedItem?.reservaPublicId}
        reserva={{
          publicId: selectedItem?.reservaPublicId,
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
