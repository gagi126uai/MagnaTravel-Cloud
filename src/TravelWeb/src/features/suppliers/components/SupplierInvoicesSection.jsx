import { useCallback, useEffect, useMemo, useState } from "react";
import { FileText, Loader2, Plus, X } from "lucide-react";
import { api } from "../../../api";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { getPublicId } from "../../../lib/publicIds";
import { getApiErrorMessage } from "../../../lib/errors";
import { showError, showSuccess, showTextPrompt } from "../../../alerts";
import { classifySupplierInvoices, operadorFacturaDirectoAlCliente } from "../lib/supplierInvoiceClassification";

const kindByType = {
  Vuelo: "flight", Hotel: "hotel", Traslado: "transfer",
  Paquete: "package", Asistencia: "assistance", Servicio: "generic",
};

const statusLabel = { pendiente: "Pendiente", pago_parcial: "Pago parcial", pagada: "Pagada", anulada: "Anulada" };

export function SupplierInvoicesSection({ supplierPublicId, overview, canEdit, canApply, invoicingMode }) {
  // Tanda 1 (contrato pantalla-motor, 2026-07-18): si el operador factura directo al
  // cliente, nunca genera cuenta por pagar de la agencia → "Nueva factura" se esconde
  // (no se agrisa con cartel: precedente ADR-036 punto 4, P3=A).
  const puedeCrearFactura = canEdit && !operadorFacturaDirectoAlCliente(invoicingMode);

  const [invoices, setInvoices] = useState([]);
  const [servicesPage, setServicesPage] = useState({ items: [], page: 1, totalPages: 0 });
  const [serviceSearch, setServiceSearch] = useState("");
  const [servicePageNumber, setServicePageNumber] = useState(1);
  const [loadingServices, setLoadingServices] = useState(false);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState({ number: "", currency: "ARS", issuedAt: new Date().toISOString().slice(0, 10), dueDate: new Date().toISOString().slice(0, 10), selected: {} });
  const [application, setApplication] = useState({});
  const [paymentPickers, setPaymentPickers] = useState({});

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const invoiceRows = await api.get(`/suppliers/${supplierPublicId}/invoices`);
      setInvoices(invoiceRows || []);
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudieron cargar las facturas del operador."));
    } finally { setLoading(false); }
  }, [supplierPublicId]);

  useEffect(() => { load(); }, [load]);

  useEffect(() => {
    if (!showCreate || !canEdit) return undefined;
    const timeout = setTimeout(async () => {
      setLoadingServices(true);
      try {
        const response = await api.get(
          `/suppliers/${supplierPublicId}/account/services?page=${servicePageNumber}&pageSize=25&currency=${form.currency}&search=${encodeURIComponent(serviceSearch)}`,
        );
        setServicesPage(response || { items: [], page: 1, totalPages: 0 });
      } catch (error) {
        showError(getApiErrorMessage(error, "No se pudieron buscar los servicios del operador."));
      } finally { setLoadingServices(false); }
    }, 250);
    return () => clearTimeout(timeout);
  }, [showCreate, canEdit, supplierPublicId, servicePageNumber, serviceSearch, form.currency]);

  const classification = useMemo(
    () => classifySupplierInvoices(overview?.balancesByCurrency || [], invoices),
    [overview, invoices],
  );

  const invoicedByService = useMemo(() => {
    const totals = {};
    invoices.filter((invoice) => invoice.status !== "anulada").forEach((invoice) => {
      (invoice.lines || []).forEach((line) => {
        totals[line.servicePublicId] = (totals[line.servicePublicId] || 0) + Number(line.amount || 0);
      });
    });
    return totals;
  }, [invoices]);
  const eligibleServices = (servicesPage.items || [])
    .map((service) => ({
      ...service,
      remainingToInvoice: Math.max(0, Number(service.netCost || 0) - Number(invoicedByService[getPublicId(service)] || 0)),
    }))
    .filter((service) => service.remainingToInvoice > 0);
  const toggleService = (service) => {
    const id = getPublicId(service);
    setForm((current) => {
      const selected = { ...current.selected };
      if (selected[id]) delete selected[id];
      else selected[id] = { serviceRecordKind: kindByType[service.type] || "generic", servicePublicId: id, amount: service.remainingToInvoice };
      return { ...current, selected };
    });
  };

  const updateSelectedAmount = (service, amount) => {
    const id = getPublicId(service);
    setForm((current) => ({
      ...current,
      selected: { ...current.selected, [id]: { ...current.selected[id], amount: Number(amount) } },
    }));
  };

  const createInvoice = async (event) => {
    event.preventDefault();
    setSaving(true);
    try {
      await api.post(`/suppliers/${supplierPublicId}/invoices`, { ...form, lines: Object.values(form.selected), selected: undefined });
      showSuccess("Factura del operador registrada.");
      setShowCreate(false);
      setForm((current) => ({ ...current, number: "", selected: {} }));
      await load();
    } catch (error) { showError(getApiErrorMessage(error, "No se pudo registrar la factura.")); }
    finally { setSaving(false); }
  };

  const applyPayment = async (invoice) => {
    const value = application[invoice.publicId] || {};
    if (!value.paymentId || !Number(value.amount)) return;
    try {
      await api.post(`/suppliers/${supplierPublicId}/invoices/${invoice.publicId}/applications`, {
        supplierPaymentPublicId: value.paymentId, amount: Number(value.amount),
      });
      showSuccess("Pago aplicado a la factura.");
      await load();
    } catch (error) { showError(getApiErrorMessage(error, "No se pudo aplicar el pago.")); }
  };

  const loadPaymentPage = async (invoice, requestedPage = 1) => {
    const current = paymentPickers[invoice.publicId] || { search: "", items: [] };
    try {
      const response = await api.get(
        `/suppliers/${supplierPublicId}/account/payments?page=${requestedPage}&pageSize=25&currency=${invoice.currency}&search=${encodeURIComponent(current.search || "")}`,
      );
      setPaymentPickers((all) => ({ ...all, [invoice.publicId]: { ...current, ...response } }));
    } catch (error) { showError(getApiErrorMessage(error, "No se pudieron buscar los pagos del operador.")); }
  };

  const reverseApplication = async (invoice, applied) => {
    const reason = await showTextPrompt({ title: "Revertir aplicación", text: "La aplicación quedará en el historial.", placeholder: "Motivo de la reversa", confirmText: "Revertir", minLength: 10 });
    if (!reason) return;
    try {
      await api.post(
        `/suppliers/${supplierPublicId}/invoices/${invoice.publicId}/applications/${applied.publicId}/reverse`,
        { reason },
      );
      showSuccess("Aplicación de pago revertida.");
      await load();
    } catch (error) { showError(getApiErrorMessage(error, "No se pudo revertir la aplicación.")); }
  };

  const voidInvoice = async (invoice) => {
    const reason = await showTextPrompt({ title: "Anular factura", text: "La factura quedará anulada en el historial.", placeholder: "Motivo de la anulación", confirmText: "Anular", minLength: 10 });
    if (!reason) return;
    try {
      await api.post(`/suppliers/${supplierPublicId}/invoices/${invoice.publicId}/void`, { reason });
      showSuccess("Factura anulada.");
      await load();
    } catch (error) { showError(getApiErrorMessage(error, "No se pudo anular la factura.")); }
  };

  if (loading) return <div className="flex justify-center py-12"><Loader2 className="h-6 w-6 animate-spin" /></div>;

  return <div className="space-y-5">
    <div className="rounded-xl border bg-card p-4">
      <div className="flex items-start justify-between gap-3">
        <div><h2 className="font-semibold">Facturas recibidas del operador</h2><p className="text-xs text-muted-foreground">Documento comercial, no fiscal AFIP. Reclasifica servicios existentes; no duplica la deuda.</p></div>
        {puedeCrearFactura && <button type="button" onClick={() => setShowCreate((x) => !x)} className="inline-flex items-center gap-2 rounded-lg bg-indigo-600 px-3 py-2 text-sm font-semibold text-white">{showCreate ? <X className="h-4 w-4" /> : <Plus className="h-4 w-4" />} Nueva factura</button>}
      </div>
      {classification.length > 0 && <div className="mt-4 grid gap-3 sm:grid-cols-2">
        {classification.map((row) => <div key={row.currency} className="rounded-lg bg-slate-50 p-3 dark:bg-slate-900">
          <div className="text-xs font-bold">{row.currency}</div>
          <div className="mt-2 flex justify-between text-sm"><span>Comprometido no facturado</span><b>{formatCurrency(row.committedUnbilled, row.currency)}</b></div>
          <div className="flex justify-between text-sm"><span>Facturado pendiente</span><b>{formatCurrency(row.billedPending, row.currency)}</b></div>
          <div className="flex justify-between text-sm"><span>Pagos todavía sin aplicar</span><b>{formatCurrency(row.paymentsUnapplied, row.currency)}</b></div>
        </div>)}
      </div>}
    </div>

    {showCreate && <form onSubmit={createInvoice} className="space-y-4 rounded-xl border bg-card p-4">
      <div className="grid gap-3 md:grid-cols-4">
        <input required placeholder="Número" value={form.number} onChange={(e) => setForm({ ...form, number: e.target.value })} className="rounded-lg border bg-background px-3 py-2" />
        <select value={form.currency} onChange={(e) => { setForm({ ...form, currency: e.target.value, selected: {} }); setServicePageNumber(1); }} className="rounded-lg border bg-background px-3 py-2"><option>ARS</option><option>USD</option></select>
        <input required type="date" value={form.issuedAt} onChange={(e) => setForm({ ...form, issuedAt: e.target.value })} className="rounded-lg border bg-background px-3 py-2" />
        <input required type="date" value={form.dueDate} onChange={(e) => setForm({ ...form, dueDate: e.target.value })} className="rounded-lg border bg-background px-3 py-2" />
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <input value={serviceSearch} onChange={(e) => { setServiceSearch(e.target.value); setServicePageNumber(1); }} placeholder="Buscar por reserva, servicio o confirmación" className="min-w-64 flex-1 rounded-lg border bg-background px-3 py-2 text-sm" />
        <span className="text-xs text-muted-foreground">{Object.keys(form.selected).length} seleccionados</span>
      </div>
      <div className="max-h-64 space-y-2 overflow-auto rounded-lg border p-2">
        {loadingServices && <div className="flex justify-center p-4"><Loader2 className="h-5 w-5 animate-spin" /></div>}
        {eligibleServices.map((service) => { const id = getPublicId(service); return <label key={id} className="flex items-center gap-3 rounded p-2 hover:bg-muted">
          <input type="checkbox" checked={Boolean(form.selected[id])} onChange={() => toggleService(service)} />
          <span className="flex-1 text-sm">{service.type} · Reserva {service.numeroReserva || "—"}</span>
          <span className="text-xs text-muted-foreground">Pendiente {formatCurrency(service.remainingToInvoice, form.currency)}</span>
          {form.selected[id] && <input type="number" min="0.01" max={service.remainingToInvoice} step="0.01" value={form.selected[id].amount} onClick={(e) => e.stopPropagation()} onChange={(e) => updateSelectedAmount(service, e.target.value)} className="w-28 rounded border bg-background px-2 py-1 text-right text-sm font-semibold" aria-label={`Importe a facturar de ${service.type}`} />}
        </label>; })}
        {eligibleServices.length === 0 && <p className="p-3 text-sm text-muted-foreground">No hay servicios disponibles en esta moneda.</p>}
      </div>
      <div className="flex items-center justify-between text-sm">
        <button type="button" disabled={servicePageNumber <= 1} onClick={() => setServicePageNumber((page) => page - 1)} className="rounded border px-3 py-1 disabled:opacity-40">Anterior</button>
        <span>Página {servicesPage.page || 1} de {servicesPage.totalPages || 1}</span>
        <button type="button" disabled={!servicesPage.totalPages || servicePageNumber >= servicesPage.totalPages} onClick={() => setServicePageNumber((page) => page + 1)} className="rounded border px-3 py-1 disabled:opacity-40">Siguiente</button>
      </div>
      <button disabled={saving || Object.keys(form.selected).length === 0} className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-semibold text-white disabled:opacity-50">{saving ? "Guardando…" : "Registrar factura"}</button>
    </form>}

    <div className="space-y-3">
      {invoices.map((invoice) => <div key={invoice.publicId} className="rounded-xl border bg-card p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="flex gap-3"><FileText className="h-5 w-5 text-indigo-500" /><div><div className="font-semibold">Factura {invoice.number}</div><div className="text-xs text-muted-foreground">Emitida {formatDate(invoice.issuedAt)} · vence {formatDate(invoice.dueDate)} · {statusLabel[invoice.status] || invoice.status}</div></div></div>
          <div className="text-right"><div className="font-bold">{invoice.amountsVisible ? formatCurrency(invoice.pending, invoice.currency) : "—"}</div><div className="text-xs text-muted-foreground">pendiente de {invoice.amountsVisible ? formatCurrency(invoice.total, invoice.currency) : "—"}</div></div>
        </div>
        <div className="mt-3 text-xs text-muted-foreground">{invoice.lines.map((x) => `${x.description} · Reserva ${x.reservaNumber || "—"}`).join(" · ")}</div>
        {invoice.applications?.length > 0 && <div className="mt-3 space-y-1 rounded-lg bg-muted/40 p-2 text-xs">
          {invoice.applications.map((applied) => <div key={applied.publicId} className="flex flex-wrap items-center justify-between gap-2">
            <span className={applied.isReversed ? "line-through text-muted-foreground" : ""}>
              {formatDate(applied.createdAt)} · {formatCurrency(applied.amount, invoice.currency)}
              {applied.isReversed ? ` · Revertida: ${applied.reversalReason}` : " · Aplicación activa"}
            </span>
            {canApply && !applied.isReversed && <button type="button" onClick={() => reverseApplication(invoice, applied)} className="rounded border border-amber-300 px-2 py-1 text-amber-700">Revertir aplicación</button>}
          </div>)}
        </div>}
        {(canApply || canEdit) && invoice.status !== "pagada" && invoice.status !== "anulada" && <div className="mt-4 flex flex-wrap gap-2 border-t pt-3">
          {canApply && <><input value={paymentPickers[invoice.publicId]?.search || ""} onChange={(e) => setPaymentPickers((all) => ({ ...all, [invoice.publicId]: { ...all[invoice.publicId], search: e.target.value } }))} placeholder="Buscar pago" className="w-40 rounded-lg border bg-background px-2 py-1.5 text-sm" />
          <button type="button" onClick={() => loadPaymentPage(invoice, 1)} className="rounded-lg border px-3 py-1.5 text-sm">Buscar</button>
          <select value={application[invoice.publicId]?.paymentId || ""} onChange={(e) => setApplication({ ...application, [invoice.publicId]: { ...application[invoice.publicId], paymentId: e.target.value } })} className="rounded-lg border bg-background px-2 py-1.5 text-sm"><option value="">Elegir pago registrado</option>{(paymentPickers[invoice.publicId]?.items || []).filter((p) => !p.isOperatorChargeSettlement).map((p) => <option key={getPublicId(p)} value={getPublicId(p)}>{formatDate(p.paidAt)} · {formatCurrency(p.imputedAmount || p.amount, invoice.currency)}</option>)}</select>
          <input type="number" min="0.01" step="0.01" placeholder="Importe" value={application[invoice.publicId]?.amount || ""} onChange={(e) => setApplication({ ...application, [invoice.publicId]: { ...application[invoice.publicId], amount: e.target.value } })} className="w-32 rounded-lg border bg-background px-2 py-1.5 text-sm" />
          <button type="button" onClick={() => applyPayment(invoice)} className="rounded-lg bg-emerald-600 px-3 py-1.5 text-sm font-semibold text-white">Aplicar pago</button>
          {(paymentPickers[invoice.publicId]?.totalPages || 0) > 1 && <span className="inline-flex items-center gap-1 text-xs">
            <button type="button" disabled={paymentPickers[invoice.publicId].page <= 1} onClick={() => loadPaymentPage(invoice, paymentPickers[invoice.publicId].page - 1)} className="rounded border px-2 py-1 disabled:opacity-40">Anterior</button>
            {paymentPickers[invoice.publicId].page}/{paymentPickers[invoice.publicId].totalPages}
            <button type="button" disabled={paymentPickers[invoice.publicId].page >= paymentPickers[invoice.publicId].totalPages} onClick={() => loadPaymentPage(invoice, paymentPickers[invoice.publicId].page + 1)} className="rounded border px-2 py-1 disabled:opacity-40">Siguiente</button>
          </span>}</>}
          {canEdit && !invoice.applications.some((item) => !item.isReversed) && <button type="button" onClick={() => voidInvoice(invoice)} className="rounded-lg border border-rose-300 px-3 py-1.5 text-sm text-rose-600">Anular</button>}
        </div>}
      </div>)}
      {invoices.length === 0 && <div className="rounded-xl border border-dashed p-10 text-center text-sm text-muted-foreground">Todavía no hay facturas registradas para este operador.</div>}
    </div>
  </div>;
}
