import { useState } from "react";
import { Loader2 } from "lucide-react";
import CreateInvoiceModal from "../../../components/CreateInvoiceModal";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { WorkItemSection } from "../components/InvoicingTab";
import { useInvoicing } from "../hooks/useInvoicing";
import RequestApprovalModal from "../../approvals/components/RequestApprovalModal";

// B1.15 Fase D'.B (2026-05-11): pestaña "Pendientes de facturar".
// Reorganizada — solo la worklist de reservas que faltan facturar.
// "Facturas emitidas" se mueven a la pestaña "Movimientos" (filtro kind=invoice).
//
// Reusa WorkItemSection y useInvoicing del flow viejo. Cuando Fase D'.D haga
// cleanup, este componente se queda como la pantalla canónica de pendientes.
export default function PaymentsPendingPage() {
  const [selectedItem, setSelectedItem] = useState(null);
  const [approvalContext, setApprovalContext] = useState(null);

  const {
    loading,
    workItems,
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
    loadData,
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

  if (loading && workItems.length === 0) {
    return (
      <div className="flex h-64 items-center justify-center text-slate-400">
        <Loader2 className="h-8 w-8 animate-spin" />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : (
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
          pagination={
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
          }
        />
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
        onCreated={() => setApprovalContext(null)}
        requestType={approvalContext?.requestType}
        entityType={approvalContext?.entityType}
        entityId={approvalContext?.entityId}
        entityLabel={approvalContext?.invoiceLabel}
      />
    </div>
  );
}
