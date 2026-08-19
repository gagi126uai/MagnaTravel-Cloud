import { useState } from "react";
import { Loader2 } from "lucide-react";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { WorkItemSection } from "../components/WorkItemSection";
import { useInvoicing } from "../hooks/useInvoicing";
import { useInvoicePolling } from "../hooks/useInvoicePolling";
import RequestApprovalModal from "../../approvals/components/RequestApprovalModal";

// B1.15 Fase D'.B (2026-05-11): pestaña "Pendientes de facturar".
// Reorganizada — solo la worklist de reservas que faltan facturar.
// "Facturas emitidas" se mueven a la pestaña "Movimientos" (filtro kind=invoice).
//
// Spec firmada 2026-08-06 (§4.4, P14=A): la fila ahora es un LINK a la ficha de la
// reserva (emitir la factura ya vive en línea ahí, EmitirFacturaInline). Murió el uso
// de CreateInvoiceModal en esta pestaña — sin más consumidores en todo el proyecto,
// el componente se borró (components/CreateInvoiceModal.jsx).
export default function PaymentsPendingPage() {
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

  // Polling adaptativo: activo solo cuando hay items en estado transitorio.
  useInvoicePolling(workItems, loadData);

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
