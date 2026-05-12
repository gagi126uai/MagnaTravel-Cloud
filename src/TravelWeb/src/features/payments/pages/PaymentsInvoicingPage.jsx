import { useState } from "react";
import { Loader2, FileText, Receipt } from "lucide-react";
import { useInvoicePolling } from "../hooks/useInvoicePolling";
import { MonthNavigator } from "../../../components/ui/MonthNavigator";
import CreateInvoiceModal from "../../../components/CreateInvoiceModal";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { FinanceMetricsGrid } from "../components/FinanceMetricsGrid";
import { WorkItemSection, InvoiceSection } from "../components/InvoicingTab";
import { useInvoicing } from "../hooks/useInvoicing";
import RequestApprovalModal from "../../approvals/components/RequestApprovalModal";

export default function PaymentsInvoicingPage() {
  const [selectedItem, setSelectedItem] = useState(null);
  const [mainTab, setMainTab] = useState("pending"); // pending | issued
  // B1.15 Fase D (2026-05-11): contexto del modal de aprobación cuando /annul
  // devuelve 409 con requiresApproval. null = modal cerrado.
  const [approvalContext, setApprovalContext] = useState(null);

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
  } = useInvoicing({
    onApprovalRequired: ({ requestType, entityType, entityId, invoice }) => {
      setApprovalContext({
        requestType,
        entityType,
        entityId,
        invoiceLabel: invoice
          ? `Factura ${invoice.tipoComprobante === 1 ? "A" : invoice.tipoComprobante === 6 ? "B" : "C"} ${String(invoice.puntoDeVenta || 0).padStart(5, "0")}-${String(invoice.numeroComprobante || 0).padStart(8, "0")}`
          : null,
      });
    },
  });

  // Polling adaptativo: activo solo cuando hay items/facturas en estado transitorio.
  useInvoicePolling([...workItems, ...invoices], loadData);

  if (loading && workItems.length === 0 && invoices.length === 0) {
    return (
      <div className="flex h-64 items-center justify-center text-slate-400">
        <Loader2 className="h-8 w-8 animate-spin" />
      </div>
    );
  }

  // Convierte el invoicePeriod "YYYY-MM" a Date para MonthNavigator.
  // Usa T00:00:00 local (sin Z) para evitar el salto de día por timezone.
  const currentPeriodDate = invoicePeriod
    ? new Date(invoicePeriod + "-01T00:00:00")
    : (() => { const n = new Date(); return new Date(n.getFullYear(), n.getMonth(), 1); })();

  const handleMonthChange = (date) => {
    setInvoicePeriod(
      `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}`
    );
  };

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
        <div className="space-y-4">
          
                    {/* Main Visual Tabs and Month Filter Selector */}
          <ListToolbar
            className="p-2"
            searchSlot={
              <div className="flex bg-slate-100 p-1 rounded-xl dark:bg-slate-800 w-full sm:w-auto overflow-x-auto">
                <button
                  onClick={() => setMainTab("pending")}
                  className={"flex items-center gap-2 px-4 py-2 text-sm font-semibold rounded-lg transition-all " + (mainTab === "pending" ? "bg-white text-indigo-600 shadow-sm dark:bg-slate-700 dark:text-indigo-400" : "text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200")}
                >
                  <FileText className="h-4 w-4" />
                  Pendientes de Emitir
                </button>
                <button
                  onClick={() => setMainTab("issued")}
                  className={"flex items-center gap-2 px-4 py-2 text-sm font-semibold rounded-lg transition-all " + (mainTab === "issued" ? "bg-white text-indigo-600 shadow-sm dark:bg-slate-700 dark:text-indigo-400" : "text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200")}
                >
                  <Receipt className="h-4 w-4" />
                  Facturas Emitidas
                </button>
              </div>
            }
            actionSlot={
              (mainTab === "issued" || mainTab === "pending") && (
                <MonthNavigator
                  month={currentPeriodDate}
                  onChange={handleMonthChange}
                  disabled={loading}
                />
              )
            }
          />
          {mainTab === "pending" ? (
            <WorkItemSection
              status={worklistStatus}
              onStatusChange={setWorklistStatus}
              items={workItems}
              onInvoice={setSelectedItem}
              searchTerm={worklistSearchTerm}
              onSearchTermChange={setWorklistSearchTerm}
              customerFilter={worklistCustomerFilter}
              onCustomerFilterChange={setWorklistCustomerFilter}
              reservationFilter={worklistReservationFilter}
              onReservationFilterChange={setWorklistReservationFilter}
              loading={loading}
              pagination={
                <PaginationFooter
                  page={worklistPage}
                  totalPages={worklistTotalPages}
                  onPageChange={setWorklistPage}
                  hasNext={worklistHasNextPage}
                  hasPrevious={worklistHasPreviousPage}
                />
              }
            />
          ) : (
            <InvoiceSection
              invoiceKind={invoiceKind}
              onInvoiceKindChange={setInvoiceKind}
              items={invoices}
              onDownloadPdf={handleDownloadPdf}
              onViewPdf={handleViewPdf}
              onRetryInvoice={handleRetryInvoice}
              onAnnulInvoice={handleAnnulInvoice}
              searchTerm={invoiceSearchTerm}
              onSearchTermChange={setInvoiceSearchTerm}
              period={invoicePeriod}
              onPeriodChange={setInvoicePeriod}
              customerFilter={invoiceCustomerFilter}
              onCustomerFilterChange={setInvoiceCustomerFilter}
              reservationFilter={invoiceReservationFilter}
              onReservationFilterChange={setInvoiceReservationFilter}
              voucherNumberFilter={invoiceVoucherNumberFilter}
              onVoucherNumberFilterChange={setInvoiceVoucherNumberFilter}
              resultFilter={invoiceResultFilter}
              onResultFilterChange={setInvoiceResultFilter}
              loading={loading}
              pagination={
                <PaginationFooter
                  page={invoicePage}
                  totalPages={invoiceTotalPages}
                  onPageChange={setInvoicePage}
                  hasNext={invoiceHasNextPage}
                  hasPrevious={invoiceHasPreviousPage}
                />
              }
            />
          )}
        </div>
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

      <RequestApprovalModal
        isOpen={Boolean(approvalContext)}
        onClose={() => setApprovalContext(null)}
        onCreated={() => {
          // El Vendedor recibe confirmacion en el modal (showSuccess interno).
          // Cerramos modal; el reintento de "Anular" lo hace el Vendedor cuando
          // el Admin apruebe (no es automatico).
          setApprovalContext(null);
        }}
        requestType={approvalContext?.requestType}
        entityType={approvalContext?.entityType}
        entityId={approvalContext?.entityId}
        entityLabel={approvalContext?.invoiceLabel}
      />
    </div>
  );
}


