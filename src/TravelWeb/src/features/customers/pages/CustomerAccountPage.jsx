/**
 * Cuenta corriente del cliente — 4 solapas.
 *
 * Layout:
 *   ┌── Encabezado (siempre visible) ──────────────────────────────────────────┐
 *   │  Identidad + tarjetas de resumen (Ventas/Cobrado/Reservas/Facturas)      │
 *   │  + chips de saldo por moneda ("Debe en $" / "Debe en US$")              │
 *   │  + carteles "A FAVOR" con botón "Usar saldo a favor"                    │
 *   │  + aplicaciones de saldo a favor vigentes (revertibles)                 │
 *   └──────────────────────────────────────────────────────────────────────────┘
 *   ┌── Solapas ───────────────────────────────────────────────────────────────┐
 *   │  Reservas  │  Estado de cuenta (default)  │  Facturación  │  Datos bancarios│
 *   └──────────────────────────────────────────────────────────────────────────┘
 *
 * Decisiones de diseño (spec sec.3, 2026-06-28):
 *   P10=A: solapa del dinero se llama "Estado de cuenta".
 *   P11=A: datos bancarios del cliente en su propia solapa "Datos bancarios".
 *   P12=A: tarjetas de resumen fijas arriba del encabezado, visibles en cualquier solapa.
 *   Nombre "Facturación" (no "Facturación AFIP": AFIP/ARCA solo aparece en el chip de cada comprobante).
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import {
  ArrowLeft,
  Loader2,
  Mail,
  Phone,
  Receipt,
  Undo2,
  Wallet,
} from "lucide-react";
import { api } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import { hasPermission } from "../../../auth";
import CustomerPaymentModal from "../../../components/CustomerPaymentModal";
import RequestApprovalModal from "../../approvals/components/RequestApprovalModal";
import { useFinanceActions } from "../../payments/hooks/useFinanceActions";
import { UsarSaldoAFavorInline } from "../components/UsarSaldoAFavorInline";
import { ListaCuentasBancarias } from "../../bank-accounts/components/ListaCuentasBancarias";
import { Button } from "../../../components/ui/button";
import {
  DataGrid,
  DataGridActionCell,
  DataGridBody,
  DataGridCell,
  DataGridEmptyState,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridHeaderRow,
  DataGridRow,
} from "../../../components/ui/DataGrid";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { AccountPageSkeleton } from "../../../components/ui/skeleton";
import { useDebounce } from "../../../hooks/useDebounce";
import { getApiErrorMessage, isDatabaseUnavailableError } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency } from "../../../lib/utils";
import { ReservaStatusBadge } from "../../reservas/components/ReservaStatusBadge";
import { formatTipoComprobante } from "../lib/facturacionFilters";
import { EstadoCuentaClienteTab } from "../components/EstadoCuentaClienteTab";
import { FacturacionClienteTab } from "../components/FacturacionClienteTab";

// ─── Constantes de paginación (solo usadas por la solapa Reservas) ───────────

const emptyPage = {
  items: [],
  page: 1,
  pageSize: 25,
  totalCount: 0,
  totalPages: 0,
  hasPreviousPage: false,
  hasNextPage: false,
};

// ─── Helpers ─────────────────────────────────────────────────────────────────

const formatDate = (dateString) => {
  if (!dateString) return "-";
  return new Date(dateString).toLocaleDateString("es-AR");
};

/**
 * Renderiza el PDF de la factura en una pestaña nueva del navegador.
 * La "cáscara" HTML se abre primero con un spinner (para no ser bloqueado como popup),
 * y luego se reemplaza con el PDF cuando llega.
 *
 * Helpers escapeHtml y renderInvoiceTab son funciones puras (sin React) que se reutilizan
 * para el estado loading y el estado final con iframe.
 */
const escapeHtml = (value) =>
  String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");

const renderInvoiceTab = (previewWindow, { title, body }) => {
  if (!previewWindow || previewWindow.closed) return;
  previewWindow.document.open();
  previewWindow.document.write(`<!doctype html><html lang="es"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" /><title>${escapeHtml(title)}</title><style>:root{color-scheme:light;font-family:Inter,system-ui,sans-serif;background:#e2e8f0;color:#0f172a}*{box-sizing:border-box}body{margin:0;min-height:100vh;background:linear-gradient(180deg,#f8fafc 0%,#e2e8f0 100%)}.shell{min-height:100vh;display:flex;flex-direction:column}.header{padding:16px 20px;border-bottom:1px solid #cbd5e1;background:rgba(255,255,255,.96);backdrop-filter:blur(10px)}.eyebrow{margin:0 0 6px;font-size:11px;font-weight:800;letter-spacing:.14em;text-transform:uppercase;color:#4f46e5}.title{margin:0;font-size:20px;font-weight:700}.subtitle{margin:6px 0 0;font-size:14px;color:#475569}.content{flex:1;padding:20px}.panel{height:calc(100vh - 117px);border:1px solid #cbd5e1;border-radius:18px;overflow:hidden;background:#fff;box-shadow:0 20px 50px rgba(15,23,42,.15)}.state{height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:12px;padding:24px;text-align:center}.state-title{margin:0;font-size:18px;font-weight:700}.state-text{margin:0;max-width:480px;color:#475569;line-height:1.5}.spinner{width:42px;height:42px;border:4px solid #cbd5e1;border-top-color:#4f46e5;border-radius:999px;animation:spin .9s linear infinite}iframe{width:100%;height:100%;border:0;background:#fff}@keyframes spin{to{transform:rotate(360deg)}}</style></head><body>${body}</body></html>`);
  previewWindow.document.close();
};

// B1: el estado de la reserva se muestra con el badge canónico (estados en inglés del backend
// → etiquetas en español de negocio). El StatusBadge local fue eliminado porque usaba
// claves en español que no coincidían con los valores del backend, causando que estados como
// "InManagement", "Confirmed", "Traveling", etc. se mostraran en inglés crudo al usuario.
// ReservaStatusBadge importado de features/reservas/components/ReservaStatusBadge.jsx.

// ─── Componente principal ─────────────────────────────────────────────────────

export default function CustomerAccountPage() {
  const { publicId } = useParams();
  const navigate = useNavigate();

  // ── Estado del overview (resumen del encabezado) ──────────────────────────
  const [overview, setOverview] = useState(null);
  const [loadingOverview, setLoadingOverview] = useState(true);
  const [databaseUnavailable, setDatabaseUnavailable] = useState(false);

  // ── Solapa activa — default "estadoDeCuenta" (decisión UX P12=A) ──────────
  const [activeTab, setActiveTab] = useState("estadoDeCuenta");

  // ── refreshKey para el Estado de cuenta ──────────────────────────────────
  // El padre lo incrementa al registrar/eliminar un cobro para que
  // EstadoCuentaClienteTab se recargue automáticamente (mismo patrón que SupplierExtractoSection).
  const [extractoRefreshKey, setExtractoRefreshKey] = useState(0);

  // ── Paginación y datos de la solapa Reservas (única que tiene paginación propia) ──
  const [reservasPage, setReservasPage] = useState(emptyPage);
  const [reservasPaging, setReservasPaging] = useState({ page: 1, pageSize: 25 });
  const [tabLoadingReservas, setTabLoadingReservas] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
  const debouncedSearch = useDebounce(searchTerm, 300);

  // ── Opciones de reservas para el modal de cobro ──────────────────────────
  const [reservaOptions, setReservaOptions] = useState([]);

  // ── Modal de cobro (CustomerPaymentModal) ────────────────────────────────
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [paymentToEdit, setPaymentToEdit] = useState(null);

  // ── Modal de aprobación ───────────────────────────────────────────────────
  const [approvalContext, setApprovalContext] = useState(null);

  // ── Saldo a favor: ficha inline y aplicaciones revertibles ───────────────
  // monedaFichaUsarSaldo: moneda del cartel que disparó la apertura de la ficha (o null)
  const [monedaFichaUsarSaldo, setMonedaFichaUsarSaldo] = useState(null);
  const [creditApplications, setCreditApplications] = useState([]);
  const [loadingCreditApplications, setLoadingCreditApplications] = useState(false);
  const [deudaClientePorReserva, setDeudaClientePorReserva] = useState(null);

  // ── Estado del formulario inline de reversión de aplicaciones ────────────
  const [revirtiendoAplicacionId, setRevirtiendoAplicacionId] = useState(null);
  const [motivoReversion, setMotivoReversion] = useState("");
  const [guardandoReversion, setGuardandoReversion] = useState(false);
  const [errorReversion, setErrorReversion] = useState(null);

  // ── Carga del overview (encabezado) ──────────────────────────────────────

  const loadOverview = useCallback(async () => {
    setLoadingOverview(true);
    try {
      const response = await api.get(`/customers/${publicId}/account`);
      setOverview(response);
      setDatabaseUnavailable(false);
    } catch (error) {
      setOverview(null);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError("No se pudo cargar la cuenta corriente.");
    } finally {
      setLoadingOverview(false);
    }
  }, [publicId]);

  const loadReservaOptions = useCallback(async () => {
    try {
      const response = await api.get(
        `/customers/${publicId}/account/reservas?page=1&pageSize=100&sortBy=createdAt&sortDir=desc`
      );
      setReservaOptions(response?.items || []);
    } catch (error) {
      setReservaOptions([]);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
    }
  }, [publicId]);

  // Carga deuda por reserva para el picker de "Aplicar saldo a reserva específica"
  const loadDeudaClientePorReserva = useCallback(async () => {
    try {
      const response = await api.get(`/customers/${publicId}/account/debt-by-reserva`);
      setDeudaClientePorReserva(response?.reservas ?? []);
    } catch (error) {
      // No bloquea la pantalla: el picker quedará vacío si falla
      console.warn("[CustomerAccountPage] No se pudo cargar deuda por reserva:", error?.message);
      setDeudaClientePorReserva([]);
    }
  }, [publicId]);

  // Carga aplicaciones VIVAS de saldo a favor (FC4 — las que se pueden revertir)
  const loadCreditApplications = useCallback(async () => {
    setLoadingCreditApplications(true);
    try {
      const creditOverview = await api.get(`/customers/${publicId}/credit`);
      setCreditApplications(Array.isArray(creditOverview?.activeApplications) ? creditOverview.activeApplications : []);
    } catch (error) {
      console.warn("[CustomerAccountPage] No se pudo cargar aplicaciones de saldo:", error?.message);
      setCreditApplications([]);
    } finally {
      setLoadingCreditApplications(false);
    }
  }, [publicId]);

  // ── Carga de la solapa Reservas (paginada) ────────────────────────────────

  const loadReservas = useCallback(async () => {
    setTabLoadingReservas(true);
    try {
      const params = new URLSearchParams({
        page: String(reservasPaging.page),
        pageSize: String(reservasPaging.pageSize),
        sortBy: "createdAt",
        sortDir: "desc",
      });
      if (debouncedSearch.trim()) params.set("search", debouncedSearch.trim());

      const response = await api.get(`/customers/${publicId}/account/reservas?${params.toString()}`);
      setReservasPage({ ...emptyPage, ...(response || {}) });
      setDatabaseUnavailable(false);
    } catch (error) {
      setReservasPage(emptyPage);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError("No se pudieron cargar las reservas.");
    } finally {
      setTabLoadingReservas(false);
    }
  }, [publicId, reservasPaging.page, reservasPaging.pageSize, debouncedSearch]);

  // ── refreshAll: recarga el encabezado + solapa reservas + extracto ────────

  const refreshAll = useCallback(async () => {
    // Incrementar refreshKey hace que EstadoCuentaClienteTab se recargue automáticamente
    // (lo tiene como dependencia de su useEffect de carga).
    setExtractoRefreshKey((prev) => prev + 1);
    await Promise.all([
      loadOverview(),
      loadReservaOptions(),
      loadCreditApplications(),
      loadDeudaClientePorReserva(),
    ]);
  }, [loadOverview, loadReservaOptions, loadCreditApplications, loadDeudaClientePorReserva]);

  // ── useFinanceActions (maneja anular recibo con flujo de aprobación) ──────

  const { handleVoidReceipt } = useFinanceActions(refreshAll, {
    onApprovalRequired: ({ requestType, entityType, entityId }) => {
      setApprovalContext({
        requestType,
        entityType,
        entityId,
        invoiceLabel: "Comprobante de pago",
      });
    },
  });

  // ── Effects de carga ──────────────────────────────────────────────────────

  // Resetea el estado cuando cambia el cliente en la URL
  useEffect(() => {
    setActiveTab("estadoDeCuenta");
    setSearchTerm("");
    setReservasPaging({ page: 1, pageSize: 25 });
    setExtractoRefreshKey(0);
  }, [publicId]);

  // Carga inicial del encabezado y datos auxiliares
  useEffect(() => {
    loadOverview();
    loadReservaOptions();
    loadCreditApplications();
    loadDeudaClientePorReserva();
  }, [loadOverview, loadReservaOptions, loadCreditApplications, loadDeudaClientePorReserva]);

  // Carga de la solapa Reservas cuando es la solapa activa
  useEffect(() => {
    if (activeTab === "reservas") {
      loadReservas();
    }
  }, [activeTab, loadReservas]);

  // Resetea la página de reservas cuando cambia el término de búsqueda
  useEffect(() => {
    if (activeTab === "reservas") {
      setReservasPaging((current) => ({ ...current, page: 1 }));
    }
  }, [activeTab, debouncedSearch]);

  // ── Handlers ──────────────────────────────────────────────────────────────

  const handleOpenModal = (payment = null) => {
    setPaymentToEdit(payment);
    setIsModalOpen(true);
  };

  const handleDeletePayment = async (payment) => {
    // NIT: usar la moneda real del cobro (payment.currency), no asumir ARS
    const monedaPago = payment.currency ?? "ARS";
    const confirmed = await showConfirm(
      "Eliminar cobro",
      `Se anulará el cobro de ${formatCurrency(payment.amount, monedaPago)} y la deuda volverá a la reserva.`,
      "Sí, eliminar",
      "red"
    );
    if (!confirmed || !payment.reservaPublicId) return;

    try {
      await api.delete(`/reservas/${payment.reservaPublicId}/payments/${getPublicId(payment)}`);
      await refreshAll();
      showSuccess("El cobro fue eliminado.");
    } catch (error) {
      console.error(error);
      showError("No se pudo eliminar el cobro.");
    }
  };

  const handleOpenInvoicePreview = async (invoice) => {
    const previewWindow = window.open("", "_blank");
    if (!previewWindow) {
      showError("El navegador bloqueó la apertura de la factura.");
      return;
    }
    previewWindow.opener = null;

    // B2: usa formatTipoComprobante (cubre todos los tipos A/B/C/M, NC, ND).
    // Antes había un tipoMap incompleto con fallback "Tipo ${int}" que exponía el
    // código ARCA al usuario para tipos no mapeados (ND 2/7/12, Factura M 51, etc.).
    const pdv = String(invoice.puntoDeVenta ?? 0).padStart(5, "0");
    const num = String(invoice.numeroComprobante ?? 0).padStart(8, "0");
    const tipo = formatTipoComprobante(invoice.tipoComprobante);
    const invoiceTitle = `${tipo} ${pdv}-${num}`;

    renderInvoiceTab(previewWindow, {
      title: invoiceTitle,
      body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(invoiceTitle)}</h1><p class="subtitle">Preparando la factura para mostrarla en esta pestaña.</p></div><div class="content"><div class="panel"><div class="state"><div class="spinner"></div><p class="state-title">Cargando factura...</p></div></div></div></div>`,
    });

    try {
      const blob = await api.get(`/invoices/${getPublicId(invoice)}/pdf`, { responseType: "blob" });
      if (!(blob instanceof Blob) || blob.size === 0) throw new Error("La factura no devolvió un PDF válido.");

      const pdfUrl = URL.createObjectURL(blob);
      const releaseTimer = window.setInterval(() => {
        if (previewWindow.closed) { URL.revokeObjectURL(pdfUrl); window.clearInterval(releaseTimer); }
      }, 1000);

      renderInvoiceTab(previewWindow, {
        title: invoiceTitle,
        body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(invoiceTitle)}</h1><p class="subtitle">Vista del comprobante emitido.</p></div><div class="content"><div class="panel"><iframe src="${pdfUrl}" title="${escapeHtml(invoiceTitle)}"></iframe></div></div></div>`,
      });
    } catch {
      // E1: NO interpolamos error.message en la ventana del comprobante.
      // Cuando el PDF endpoint falla sin body, api.js asigna error.message = statusText
      // (ej: "Not Found", "Forbidden", "Internal Server Error") — inglés técnico que no
      // debe llegar al usuario. Usamos texto fijo en español para este estado de error.
      renderInvoiceTab(previewWindow, {
        title: invoiceTitle,
        body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(invoiceTitle)}</h1><p class="subtitle">No fue posible abrir el comprobante.</p></div><div class="content"><div class="panel"><div class="state"><p class="state-title">No se pudo cargar el comprobante</p><p class="state-text">No se pudo abrir el comprobante. Probá de nuevo en un momento.</p></div></div></div></div>`,
      });
    }
  };

  const handleRevertirAplicacion = async (applicationPublicId) => {
    setGuardandoReversion(true);
    setErrorReversion(null);
    try {
      await api.post(
        `/customers/${publicId}/credit/applications/${applicationPublicId}/reverse`,
        { reason: motivoReversion.trim() || null }
      );
      setRevirtiendoAplicacionId(null);
      setMotivoReversion("");
      await Promise.all([loadOverview(), loadCreditApplications()]);
    } catch (error) {
      setErrorReversion(getApiErrorMessage(error, "No se pudo revertir la aplicación. Intentá de nuevo."));
    } finally {
      setGuardandoReversion(false);
    }
  };

  // ── Datos derivados ───────────────────────────────────────────────────────

  const summary = overview?.summary || {};
  const customer = overview?.customer;
  const reservas = reservasPage.items || [];

  // Reservas sin cancelar: para el picker del modal de cobro
  const availableReservas = useMemo(
    () => reservaOptions.filter((r) => r.status !== "Cancelled"),
    [reservaOptions]
  );

  // Filtra por moneda para el picker de "aplicar saldo a reserva específica"
  const getReservasConDeudaEnMoneda = useCallback((moneda) => {
    if (!deudaClientePorReserva) return [];
    return deudaClientePorReserva.filter((reserva) => {
      const lineaMoneda = (reserva.debtByCurrency ?? []).find(
        (c) => c.currency === moneda && (c.amount ?? 0) > 0
      );
      return lineaMoneda != null;
    });
  }, [deudaClientePorReserva]);

  // ── Render ────────────────────────────────────────────────────────────────

  if (loadingOverview) return <AccountPageSkeleton />;

  if (!overview && !databaseUnavailable) {
    return <div className="py-12 text-center text-muted-foreground">No se encontraron datos del cliente.</div>;
  }

  // Chips de saldo por moneda (multimoneda, separados).
  // Usa summary.receivableByCurrency del backend (ADR-022 Capa 8).
  // Si está vacío (sin deuda activa), no muestra el bloque rojo.
  const saldoPorMoneda = Array.isArray(summary.receivableByCurrency)
    ? summary.receivableByCurrency.filter((item) => (item.amount ?? 0) > 0)
    : [];

  // Cuando no hay deuda activa, muestra igualmente un chip verde/gris con el escalar legacy
  const sinDeudaEnNingunaMoneda = saldoPorMoneda.length === 0;

  return (
    <div className="space-y-6">
      {/* ── Barra de navegación superior ───────────────────────────────────── */}
      <div className="flex flex-col justify-between gap-4 sm:flex-row sm:items-center">
        <div className="flex items-center gap-4">
          <Button variant="ghost" size="icon" onClick={() => navigate("/customers")}>
            <ArrowLeft className="h-5 w-5" />
          </Button>
          <div>
            <h1 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Cuenta Corriente</h1>
            <p className="text-muted-foreground">{customer?.fullName}</p>
          </div>
        </div>

        {/* Nueva cotización: acción rápida independiente del cobro */}
        <Button
          type="button"
          variant="outline"
          onClick={() => navigate(`/quotes?create=1&customerPublicId=${publicId}`)}
          className="gap-2 border-indigo-200 text-indigo-700 hover:bg-indigo-50 hover:text-indigo-800 dark:border-indigo-800 dark:text-indigo-300 dark:hover:bg-indigo-900/20"
          disabled={databaseUnavailable}
        >
          <Receipt className="h-4 w-4" />
          Nueva cotización
        </Button>
      </div>

      {/* ── Encabezado (siempre visible sobre las solapas) ─────────────────── */}
      <div className="grid gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">

        {/* Tarjeta izquierda: identidad + resumen de actividad */}
        <div className="rounded-xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
          <div className="flex items-start gap-4">
            <div className="flex h-16 w-16 items-center justify-center rounded-full bg-gradient-to-br from-indigo-500 to-cyan-500 text-2xl font-bold text-white shadow-md">
              {customer?.fullName?.charAt(0)?.toUpperCase() || "?"}
            </div>
            <div className="space-y-2">
              <h2 className="text-xl font-bold text-slate-900 dark:text-white">{customer?.fullName}</h2>
              <div className="flex flex-wrap gap-x-4 gap-y-1 text-sm text-slate-500">
                {customer?.email && (
                  <span className="flex items-center gap-1">
                    <Mail className="h-3.5 w-3.5" /> {customer.email}
                  </span>
                )}
                {customer?.phone && (
                  <span className="flex items-center gap-1">
                    <Phone className="h-3.5 w-3.5" /> {customer.phone}
                  </span>
                )}
                {customer?.taxId && <span>CUIT/DNI {customer.taxId}</span>}
              </div>
            </div>
          </div>

          {/* Tarjetas de resumen: Ventas / Cobrado / Reservas / Facturas (P12=A: fijas) */}
          <div className="mt-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <div className="rounded-xl bg-slate-50 p-4 dark:bg-slate-950/60">
              <div className="text-xs font-semibold uppercase tracking-wider text-slate-400">Ventas</div>
              <div className="mt-1 text-lg font-bold text-slate-900 dark:text-white">{formatCurrency(summary.totalSales, "ARS")}</div>
            </div>
            <div className="rounded-xl bg-slate-50 p-4 dark:bg-slate-950/60">
              <div className="text-xs font-semibold uppercase tracking-wider text-slate-400">Cobrado</div>
              <div className="mt-1 text-lg font-bold text-emerald-600 dark:text-emerald-400">{formatCurrency(summary.totalPaid, "ARS")}</div>
            </div>
            <div className="rounded-xl bg-slate-50 p-4 dark:bg-slate-950/60">
              <div className="text-xs font-semibold uppercase tracking-wider text-slate-400">Reservas</div>
              <div className="mt-1 text-lg font-bold text-slate-900 dark:text-white">{summary.reservaCount || 0}</div>
            </div>
            <div className="rounded-xl bg-slate-50 p-4 dark:bg-slate-950/60">
              <div className="text-xs font-semibold uppercase tracking-wider text-slate-400">Facturas</div>
              <div className="mt-1 text-lg font-bold text-slate-900 dark:text-white">{summary.invoiceCount || 0}</div>
            </div>
          </div>
        </div>

        {/*
          Tarjeta derecha: chips de saldo por moneda ("Debe en $" / "Debe en US$").
          Regla multimoneda: NUNCA se suman ARS y USD en un solo número.
          Se usa summary.receivableByCurrency (ADR-022 Capa 8).
          Si no hay deuda activa, se muestra un chip verde/neutro.
        */}
        <div className="flex flex-col gap-3">
          {sinDeudaEnNingunaMoneda ? (
            <div className="rounded-xl border border-emerald-100 bg-emerald-50 p-6 shadow-sm dark:border-emerald-900/30 dark:bg-emerald-900/10 flex-1">
              <div className="text-sm font-medium text-slate-500 dark:text-slate-400">Saldo</div>
              <div className="mt-1 text-3xl font-bold text-emerald-600 dark:text-emerald-400">Al día</div>
              <div className="mt-2 text-xs font-medium text-slate-400">Sin deuda pendiente</div>
            </div>
          ) : (
            saldoPorMoneda.map((item) => (
              <div
                key={item.currency}
                className="rounded-xl border border-rose-100 bg-rose-50 p-5 shadow-sm dark:border-rose-900/30 dark:bg-rose-900/10"
                data-testid={`chip-saldo-${item.currency}`}
              >
                <div className="text-xs font-bold uppercase tracking-wider text-rose-700 dark:text-rose-400">
                  Debe en {item.currency === "USD" ? "US$" : "$"}
                </div>
                <div className="mt-1 text-2xl font-bold text-rose-600 dark:text-rose-400">
                  {formatCurrency(item.amount, item.currency)}
                </div>
              </div>
            ))
          )}
        </div>
      </div>

      {/*
        ── Carteles "A FAVOR" por moneda ───────────────────────────────────────
        Un cartel por moneda con saldo a favor > 0. Regla multimoneda: uno por moneda,
        nunca sumados. El botón "Usar saldo a favor" abre la ficha inline.
      */}
      {Array.isArray(summary.creditBalanceByCurrency) && summary.creditBalanceByCurrency.length > 0 && (
        <div className="flex flex-col gap-3">
          {summary.creditBalanceByCurrency.map((creditEntry) => (
            <div key={creditEntry.currency}>
              <div className="flex items-center justify-between rounded-xl border border-emerald-200 bg-emerald-50 px-5 py-4 dark:border-emerald-900/40 dark:bg-emerald-950/20">
                <div>
                  <div className="text-[11px] font-bold uppercase tracking-wider text-emerald-700 dark:text-emerald-400">
                    A FAVOR EN {creditEntry.currency === "USD" ? "US$" : "$"}
                  </div>
                  <div className="mt-0.5 text-2xl font-bold text-emerald-700 dark:text-emerald-400">
                    {creditEntry.currency === "USD"
                      ? `US$${Number(creditEntry.amount).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
                      : `$${Number(creditEntry.amount).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
                    }
                  </div>
                  <div className="mt-1 text-xs text-emerald-600 dark:text-emerald-500">
                    El cliente pagó de más en {creditEntry.currency === "USD" ? "dólares" : "pesos"}
                  </div>
                </div>
                {hasPermission("cobranzas.edit") && (
                  <button
                    type="button"
                    onClick={() =>
                      setMonedaFichaUsarSaldo(
                        monedaFichaUsarSaldo === creditEntry.currency ? null : creditEntry.currency
                      )
                    }
                    className="flex items-center gap-2 rounded-lg border border-emerald-300 bg-white px-4 py-2 text-sm font-semibold text-emerald-700 shadow-sm hover:bg-emerald-50 dark:border-emerald-800 dark:bg-slate-900 dark:text-emerald-400 dark:hover:bg-emerald-950/30 transition-colors"
                    data-testid={`usar-saldo-btn-${creditEntry.currency}`}
                  >
                    <Wallet className="h-4 w-4" />
                    {monedaFichaUsarSaldo === creditEntry.currency ? "Cerrar" : "Usar saldo a favor"}
                  </button>
                )}
              </div>
              {monedaFichaUsarSaldo === creditEntry.currency && (
                <div className="mt-2">
                  <UsarSaldoAFavorInline
                    publicId={publicId}
                    moneda={creditEntry.currency}
                    saldoDisponible={Number(creditEntry.amount)}
                    reservasConDeuda={getReservasConDeudaEnMoneda(creditEntry.currency)}
                    onConfirmado={() => {
                      setMonedaFichaUsarSaldo(null);
                      Promise.all([loadOverview(), loadCreditApplications(), loadDeudaClientePorReserva()]);
                    }}
                    onCancelar={() => setMonedaFichaUsarSaldo(null)}
                  />
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      {/*
        ── Aplicaciones VIVAS de saldo a favor ─────────────────────────────────
        Muestra saldos que ya se aplicaron a otras reservas y que se pueden revertir.
        Cada fila tiene un formulario inline de reversión (sin modal).
      */}
      {creditApplications.length > 0 && (
        <div className="rounded-xl border border-indigo-200 bg-indigo-50/40 dark:border-indigo-900/40 dark:bg-indigo-950/10 overflow-hidden">
          <div className="px-5 py-3 border-b border-indigo-100 dark:border-indigo-900/30">
            <h3 className="text-sm font-bold text-indigo-800 dark:text-indigo-300">
              Saldo a favor aplicado a otras reservas
            </h3>
          </div>
          <ul className="divide-y divide-indigo-100 dark:divide-indigo-900/20">
            {creditApplications.map((aplicacion) => {
              const estaRevirtiendoEsta = revirtiendoAplicacionId === String(aplicacion.applicationPublicId);
              const simbolo = aplicacion.currency === "USD" ? "US$" : "$";
              const monto = Number(aplicacion.amount).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
              const fechaTexto = aplicacion.appliedAt ? new Date(aplicacion.appliedAt).toLocaleDateString("es-AR") : "—";

              return (
                <li key={String(aplicacion.applicationPublicId)} className="px-5 py-3">
                  <div className="flex items-start justify-between gap-3 flex-wrap">
                    <div className="min-w-0">
                      <p className="text-sm font-semibold text-slate-800 dark:text-slate-200">
                        Saldo a favor aplicado a{" "}
                        <Link
                          to={`/reservas/${aplicacion.targetReservaPublicId}`}
                          className="text-indigo-600 hover:text-indigo-700 dark:text-indigo-400 dark:hover:text-indigo-300 hover:underline"
                        >
                          {aplicacion.targetReservaNumber ?? "la reserva"}
                        </Link>
                        <span className="ml-2 font-bold text-indigo-700 dark:text-indigo-400">
                          −{simbolo}{monto}
                        </span>
                      </p>
                      {aplicacion.targetReservaHolderName && (
                        <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">
                          Titular: {aplicacion.targetReservaHolderName}
                        </p>
                      )}
                      <p className="text-xs text-slate-400 dark:text-slate-500 mt-0.5">
                        Aplicado el {fechaTexto}
                      </p>
                    </div>
                    {hasPermission("cobranzas.edit") && !estaRevirtiendoEsta && (
                      <button
                        type="button"
                        onClick={() => {
                          setRevirtiendoAplicacionId(String(aplicacion.applicationPublicId));
                          setMotivoReversion("");
                          setErrorReversion(null);
                        }}
                        className="flex-shrink-0 flex items-center gap-1.5 rounded-lg border border-indigo-200 bg-white px-3 py-1.5 text-xs font-semibold text-indigo-700 hover:bg-indigo-50 dark:border-indigo-800 dark:bg-slate-900 dark:text-indigo-400 dark:hover:bg-indigo-950/30 transition-colors"
                        data-testid={`revertir-aplicacion-${aplicacion.applicationPublicId}`}
                      >
                        <Undo2 className="h-3.5 w-3.5" />
                        Revertir
                      </button>
                    )}
                  </div>
                  {estaRevirtiendoEsta && (
                    <div className="mt-3 space-y-2 rounded-lg bg-white dark:bg-slate-800 border border-indigo-200 dark:border-indigo-900/40 p-3">
                      <label
                        htmlFor={`motivo-reversion-${aplicacion.applicationPublicId}`}
                        className="text-xs font-semibold text-slate-600 dark:text-slate-400"
                      >
                        Motivo de la reversión (opcional)
                      </label>
                      <textarea
                        id={`motivo-reversion-${aplicacion.applicationPublicId}`}
                        rows={2}
                        value={motivoReversion}
                        onChange={(e) => setMotivoReversion(e.target.value)}
                        disabled={guardandoReversion}
                        placeholder="Indicá el motivo si lo tenés..."
                        className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-slate-700 dark:bg-slate-900 dark:text-white disabled:opacity-50 resize-none"
                        data-testid={`motivo-reversion-${aplicacion.applicationPublicId}`}
                      />
                      {errorReversion && (
                        <p className="text-xs text-rose-600 dark:text-rose-400" role="alert">{errorReversion}</p>
                      )}
                      <div className="flex justify-end gap-2">
                        <button
                          type="button"
                          onClick={() => { setRevirtiendoAplicacionId(null); setMotivoReversion(""); setErrorReversion(null); }}
                          disabled={guardandoReversion}
                          className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 disabled:opacity-50 transition-colors"
                        >
                          Cancelar
                        </button>
                        <button
                          type="button"
                          onClick={() => handleRevertirAplicacion(String(aplicacion.applicationPublicId))}
                          disabled={guardandoReversion}
                          className="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-bold text-white hover:bg-indigo-700 disabled:opacity-50 transition-colors"
                          data-testid={`confirmar-reversion-${aplicacion.applicationPublicId}`}
                        >
                          {guardandoReversion ? (
                            <><Loader2 className="h-3 w-3 animate-spin" />Revirtiendo…</>
                          ) : (
                            <><Undo2 className="h-3 w-3" />Confirmar reversión</>
                          )}
                        </button>
                      </div>
                    </div>
                  )}
                </li>
              );
            })}
          </ul>
          {loadingCreditApplications && (
            <div className="px-5 py-3 text-xs text-slate-400 flex items-center gap-1.5">
              <Loader2 className="h-3.5 w-3.5 animate-spin" />
              Actualizando aplicaciones...
            </div>
          )}
        </div>
      )}

      {/* ── Solapas ─────────────────────────────────────────────────────────── */}
      <div className="flex flex-col gap-4">
        {/* Barra de solapas con data-testid estables */}
        <div className="flex flex-wrap gap-6 border-b border-slate-200 dark:border-slate-800">
          {[
            { key: "reservas", label: "Reservas", count: summary.reservaCount || 0 },
            { key: "estadoDeCuenta", label: "Estado de cuenta", count: null },
            { key: "facturacion", label: "Facturación", count: summary.invoiceCount || 0 },
            { key: "datosBancarios", label: "Datos bancarios", count: null },
          ].map((tab) => (
            <button
              key={tab.key}
              type="button"
              onClick={() => setActiveTab(tab.key)}
              className={`relative pb-4 text-sm font-semibold transition-colors ${
                activeTab === tab.key
                  ? "text-indigo-600 dark:text-indigo-400"
                  : "text-slate-500 hover:text-slate-700 dark:hover:text-slate-300"
              }`}
              data-testid={`tab-${tab.key}`}
            >
              <span className="flex items-center gap-2">
                {tab.label}
                {tab.count !== null && (
                  <span className="rounded-full bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-500 dark:bg-slate-800">
                    {tab.count}
                  </span>
                )}
              </span>
              {activeTab === tab.key && (
                <div className="absolute bottom-0 left-0 right-0 h-0.5 rounded-full bg-indigo-600 dark:bg-indigo-400" />
              )}
            </button>
          ))}
        </div>

        {/* ── Contenido de la solapa Reservas ──────────────────────────────── */}
        {activeTab === "reservas" && (
          <>
            {/* Buscador de reservas */}
            <ListToolbar
              className="p-3"
              searchSlot={
                <div className="relative min-w-[260px]">
                  <Receipt className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                  <input
                    type="text"
                    placeholder="Buscar en reservas..."
                    value={searchTerm}
                    onChange={(e) => setSearchTerm(e.target.value)}
                    className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm transition-shadow focus:ring-2 focus:ring-slate-200 dark:border-slate-800 dark:bg-slate-900 dark:text-white"
                  />
                </div>
              }
            />
            {databaseUnavailable ? (
              <DatabaseUnavailableState />
            ) : tabLoadingReservas && reservas.length === 0 ? (
              <div className="flex h-48 items-center justify-center text-slate-400">
                <Loader2 className="h-8 w-8 animate-spin" />
              </div>
            ) : (
              <>
                <DataGrid density="compact" minWidth="900px">
                  <DataGridHeader>
                    <DataGridHeaderRow>
                      <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                      <DataGridHeaderCell>Reserva</DataGridHeaderCell>
                      <DataGridHeaderCell>Estado</DataGridHeaderCell>
                      <DataGridHeaderCell align="right">Venta</DataGridHeaderCell>
                      <DataGridHeaderCell align="right">Cobrado</DataGridHeaderCell>
                      <DataGridHeaderCell align="right">Saldo</DataGridHeaderCell>
                      <DataGridHeaderCell align="right">Acción</DataGridHeaderCell>
                    </DataGridHeaderRow>
                  </DataGridHeader>
                  <DataGridBody>
                    {reservas.length === 0 ? (
                      <DataGridEmptyState colSpan={7} title="No hay reservas para mostrar." />
                    ) : (
                      reservas.map((reserva) => (
                        <DataGridRow key={getPublicId(reserva)}>
                          <DataGridCell>{formatDate(reserva.startDate || reserva.createdAt)}</DataGridCell>
                          <DataGridCell>
                            <div className="font-semibold text-slate-900 dark:text-white">{reserva.numeroReserva}</div>
                            <div className="text-xs text-slate-500 dark:text-slate-400">{reserva.name}</div>
                          </DataGridCell>
                          <DataGridCell><ReservaStatusBadge status={reserva.status} /></DataGridCell>
                          <DataGridCell align="right" className="font-semibold text-slate-900 dark:text-white">
                            {formatCurrency(reserva.totalSale, "ARS")}
                          </DataGridCell>
                          <DataGridCell align="right" className="font-semibold text-emerald-600 dark:text-emerald-400">
                            {formatCurrency(reserva.paid, "ARS")}
                          </DataGridCell>
                          <DataGridCell align="right" className="font-semibold text-rose-600 dark:text-rose-400">
                            {formatCurrency(reserva.balance, "ARS")}
                          </DataGridCell>
                          <DataGridActionCell>
                            <Link
                              to={`/reservas/${getPublicId(reserva)}`}
                              className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-indigo-600 hover:bg-indigo-50 dark:border-slate-700 dark:hover:bg-slate-800"
                            >
                              Ver
                            </Link>
                          </DataGridActionCell>
                        </DataGridRow>
                      ))
                    )}
                  </DataGridBody>
                </DataGrid>

                {reservas.length === 0 ? (
                  <ListEmptyState
                    title="No hay reservas para mostrar."
                    className="md:hidden rounded-xl border border-dashed border-slate-200 dark:border-slate-800"
                  />
                ) : (
                  <MobileRecordList>
                    {reservas.map((reserva) => (
                      <MobileRecordCard
                        key={getPublicId(reserva)}
                        statusSlot={<ReservaStatusBadge status={reserva.status} />}
                        title={reserva.numeroReserva}
                        subtitle={reserva.name}
                        meta={
                          <>
                            <div className="text-xs text-slate-500 dark:text-slate-400">
                              Fecha: {formatDate(reserva.startDate || reserva.createdAt)}
                            </div>
                            <div className="text-xs text-slate-500 dark:text-slate-400">
                              Venta {formatCurrency(reserva.totalSale, "ARS")} · Cobrado {formatCurrency(reserva.paid, "ARS")}
                            </div>
                          </>
                        }
                        footer={<span className="text-sm font-semibold text-rose-600 dark:text-rose-400">Saldo {formatCurrency(reserva.balance, "ARS")}</span>}
                        footerActions={
                          <Link
                            to={`/reservas/${getPublicId(reserva)}`}
                            className="inline-flex rounded-lg border border-slate-200 px-3 py-2 text-xs font-semibold text-indigo-600 hover:bg-indigo-50 dark:border-slate-700 dark:hover:bg-slate-800"
                          >
                            Ver
                          </Link>
                        }
                      />
                    ))}
                  </MobileRecordList>
                )}

                <PaginationFooter
                  page={reservasPage.page || reservasPaging.page}
                  pageSize={reservasPage.pageSize || reservasPaging.pageSize}
                  totalCount={reservasPage.totalCount || 0}
                  totalPages={reservasPage.totalPages || 0}
                  hasPreviousPage={Boolean(reservasPage.hasPreviousPage)}
                  hasNextPage={Boolean(reservasPage.hasNextPage)}
                  onPageChange={(page) => setReservasPaging((prev) => ({ ...prev, page }))}
                  onPageSizeChange={(pageSize) => setReservasPaging({ page: 1, pageSize })}
                />
              </>
            )}
          </>
        )}

        {/* ── Contenido de la solapa Estado de cuenta ──────────────────────── */}
        {activeTab === "estadoDeCuenta" && (
          <EstadoCuentaClienteTab
            customerPublicId={publicId}
            refreshKey={extractoRefreshKey}
            onVerFactura={handleOpenInvoicePreview}
            onEliminarPago={handleDeletePayment}
            onAnularRecibo={handleVoidReceipt}
            onNuevaCobranza={() => handleOpenModal(null)}
            canRegistrarCobranza={hasPermission("cobranzas.edit")}
          />
        )}

        {/* ── Contenido de la solapa Facturación ───────────────────────────── */}
        {/* Antes se llamaba "Facturación AFIP" — renombrado a "Facturación".
            AFIP/ARCA solo aparece como chip de estado en cada comprobante. */}
        {activeTab === "facturacion" && (
          <FacturacionClienteTab
            customerPublicId={publicId}
            onVerFactura={handleOpenInvoicePreview}
          />
        )}

        {/* ── Contenido de la solapa Datos bancarios ───────────────────────── */}
        {/* Los datos bancarios del cliente (CBU/alias) se movieron del encabezado
            a esta solapa propia (decisión UX P11=A, 2026-06-28). */}
        {activeTab === "datosBancarios" && (
          <ListaCuentasBancarias
            ownerType="Customer"
            ownerId={publicId}
            title="Datos bancarios del cliente"
            canEdit={hasPermission("clientes.edit")}
          />
        )}
      </div>

      {/* ── Modales (solo CustomerPaymentModal y RequestApprovalModal) ────── */}
      {/*
        CustomerPaymentModal: permanece como modal de acción rápida para registrar cobros.
        Se abre desde el botón "Nuevo cobro" dentro de la solapa Estado de cuenta.
        TODO (mejora a futuro): migrar a ficha en línea como el cobro en la reserva
        (RegistrarCobroInline), pero eso es un cambio de mayor envergadura fuera de scope.
      */}
      <CustomerPaymentModal
        isOpen={isModalOpen}
        onClose={() => { setIsModalOpen(false); setPaymentToEdit(null); }}
        paymentToEdit={paymentToEdit}
        customerId={publicId}
        availableReservas={availableReservas}
        onSave={refreshAll}
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
