import { useState } from "react";
import { Loader2 } from "lucide-react";
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
    worklistStatus,
    setWorklistStatus,
    worklistSearchTerm,
    setWorklistSearchTerm,
    worklistCustomerFilter,
    setWorklistCustomerFilter,
    worklistReservationFilter,
    setWorklistReservationFilter,
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
    invoiceSearchTerm,
    setInvoiceSearchTerm,
    invoicePeriod,
    setInvoicePeriod,
    invoiceCustomerFilter,
    setInvoiceCustomerFilter,
    invoiceReservationFilter,
    setInvoiceReservationFilter,
    invoiceVoucherNumberFilter,
    setInvoiceVoucherNumberFilter,
    invoiceResultFilter,
    setInvoiceResultFilter,
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
      <FinanceMetricsGrid
        items={[
          { label: "Importe listo para emitir", value: summary?.readyAmount || 0 },
          { label: "Listas para emitir", value: summary?.readyCount || 0, isCount: true },
          { label: "Requieren autorizacion", value: summary?.overrideCount || 0, isCount: true },
          { label: "Bloqueadas", value: summary?.blockedCount || 0, isCount: true },
          { label: "Facturado este mes", value: summary?.invoicedThisMonth || 0 },
          { label: "Emitidas por excepcion", value: summary?.forcedCount || 0, isCount: true },
        ]}
        columns="md:grid-cols-2 xl:grid-cols-6"
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
            worklistSearchTerm={worklistSearchTerm}
            onWorklistSearchTermChange={setWorklistSearchTerm}
            worklistCustomerFilter={worklistCustomerFilter}
            onWorklistCustomerFilterChange={setWorklistCustomerFilter}
            worklistReservationFilter={worklistReservationFilter}
            onWorklistReservationFilterChange={setWorklistReservationFilter}
            invoiceKind={invoiceKind}
            onInvoiceKindChange={setInvoiceKind}
            invoiceSearchTerm={invoiceSearchTerm}
            onInvoiceSearchTermChange={setInvoiceSearchTerm}
            invoicePeriod={invoicePeriod}
            onInvoicePeriodChange={setInvoicePeriod}
            invoiceCustomerFilter={invoiceCustomerFilter}
            onInvoiceCustomerFilterChange={setInvoiceCustomerFilter}
            invoiceReservationFilter={invoiceReservationFilter}
            onInvoiceReservationFilterChange={setInvoiceReservationFilter}
            invoiceVoucherNumberFilter={invoiceVoucherNumberFilter}
            onInvoiceVoucherNumberFilterChange={setInvoiceVoucherNumberFilter}
            invoiceResultFilter={invoiceResultFilter}
            onInvoiceResultFilterChange={setInvoiceResultFilter}
            onInvoice={setSelectedItem}
            onDownloadPdf={handleDownloadPdf}
            onViewPdf={handleViewPdf}
            onRetryInvoice={handleRetryInvoice}
            onAnnulInvoice={handleAnnulInvoice}
          />

          <div className="space-y-3">
            <div className="text-xs font-semibold uppercase tracking-[0.24em] text-slate-400">Pendientes de emitir</div>
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
            <div className="text-xs font-semibold uppercase tracking-[0.24em] text-slate-400">Facturas emitidas</div>
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
          canEmitAfipInvoice: selectedItem?.fiscalStatus === "ready" || selectedItem?.fiscalStatus === "override",
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
