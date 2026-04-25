import { useState } from "react";
import { Loader2, Calendar, ChevronLeft, ChevronRight, FileText, Receipt } from "lucide-react";
import CreateInvoiceModal from "../../../components/CreateInvoiceModal";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { FinanceMetricsGrid } from "../components/FinanceMetricsGrid";
import { WorkItemSection, InvoiceSection } from "../components/InvoicingTab";
import { useInvoicing } from "../hooks/useInvoicing";

export default function PaymentsInvoicingPage() {
  const [selectedItem, setSelectedItem] = useState(null);
  const [mainTab, setMainTab] = useState("pending"); // pending | issued

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

  // Month navigation logic mapped to invoicePeriod ("YYYY-MM")
  const parsePeriod = (periodStr) => periodStr ? new Date(periodStr + "-01T00:00:00") : new Date();
  const currentPeriodDate = parsePeriod(invoicePeriod);
  
  const handlePrevMonth = () => {
    const prev = new Date(currentPeriodDate.getFullYear(), currentPeriodDate.getMonth() - 1, 1);
    setInvoicePeriod(`${prev.getFullYear()}-${String(prev.getMonth() + 1).padStart(2, '0')}`);
  };
  
  const handleNextMonth = () => {
    const next = new Date(currentPeriodDate.getFullYear(), currentPeriodDate.getMonth() + 1, 1);
    setInvoicePeriod(`${next.getFullYear()}-${String(next.getMonth() + 1).padStart(2, '0')}`);
  };

  const monthName = currentPeriodDate.toLocaleDateString("es-AR", { month: "long", year: "numeric" });

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
                <div className="flex w-full items-center justify-between gap-1 rounded-lg border border-slate-200 bg-white p-1 dark:border-slate-700 dark:bg-slate-800/50 sm:w-auto sm:justify-center shadow-sm">
                  <button onClick={handlePrevMonth} className="rounded p-1.5 text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-700 dark:text-slate-400 dark:hover:bg-slate-700 dark:hover:text-white" title="Mes anterior">
                    <ChevronLeft className="w-4 h-4" />
                  </button>
                  <div className="flex items-center gap-1.5 px-1 sm:px-2">
                    <Calendar className="w-3.5 h-3.5 text-indigo-500" />
                    <span className="w-[110px] text-center text-sm font-medium capitalize text-slate-700 dark:text-slate-200">
                      {monthName}
                    </span>
                  </div>
                  <button onClick={handleNextMonth} className="rounded p-1.5 text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-700 dark:text-slate-400 dark:hover:bg-slate-700 dark:hover:text-white" title="Mes siguiente">
                    <ChevronRight className="w-4 h-4" />
                  </button>
                </div>
              )
            }
          />
          {mainTab === "pending" ? (
            <WorkItemSection
              items={workItems}
              searchTerm={worklistSearchTerm}
              onSearchChange={setWorklistSearchTerm}
              onSelectItem={setSelectedItem}
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
              items={invoices}
              searchTerm={invoiceSearchTerm}
              onSearchChange={setInvoiceSearchTerm}
              loading={loading}
              onViewPdf={handleViewPdf}
              onDownloadPdf={handleDownloadPdf}
              onRetry={handleRetryInvoice}
              onAnnul={handleAnnulInvoice}
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
    </div>
  );
}


