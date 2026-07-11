/**
 * Pantalla global de Facturación — módulo VENTAS.
 *
 * Lista TODOS los comprobantes emitidos por la agencia con filtros server-side
 * y paginación. Permite abrir el PDF de cualquier comprobante.
 *
 * Permiso requerido: cobranzas.view_all (gateado también en App.jsx y Sidebar.jsx).
 * El backend también aplica el scope del usuario: un admin ve todo; un usuario con
 * cobranzas.view_all también ve todo; sin el permiso, solo ve los suyos.
 *
 * Decisiones de diseño (spec 2026-06-28 §4):
 *   - Período por defecto: últimos 90 días (P13=A).
 *   - Filtro "Estado": solo estados fiscales ARCA + estados de anulación.
 *     SIN "pagada/pendiente" porque ese estado vive en la reserva, no en el comprobante.
 *   - El filtro "Tipo" reutiliza OPCIONES_TIPO_FILTRO (códigos ARCA) pero se convierte
 *     a Document+Letter server-side en buildInvoiceQueryParams().
 *   - Moneda: se muestra por fila; nunca se suman monedas distintas.
 *
 * Columnas de la tabla:
 *   Fecha | Comprobante | Tipo | Cliente | Importe | Estado | Acción
 *
 * Acción "Ver": abre el PDF en nueva pestaña (mismo patrón que CustomerAccountPage).
 */
import { useCallback, useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Eye, Loader2, Receipt, RefreshCw, FileText } from "lucide-react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { hasPermission } from "../../../auth";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
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
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { FacturacionFilters } from "../../customers/components/FacturacionFilters";
import { formatTipoComprobante, calcularPeriodoPorDefecto } from "../../customers/lib/facturacionFilters";
import { OPCIONES_ESTADO_FILTRO_GLOBAL } from "../lib/facturacionGlobalFilters";
import { useFacturacionGlobal } from "../hooks/useFacturacionGlobal";
import { ComprobantesPorResolverTab } from "../components/ComprobantesPorResolverTab";
import CreditNoteReconciliationInboxPage from "../../creditNoteReconciliation/pages/CreditNoteReconciliationInboxPage";
import {
  FACTURACION_TAB_TODOS as TAB_TODOS,
  FACTURACION_TAB_COMPROBANTES as TAB_COMPROBANTES,
  FACTURACION_TAB_RECIBOS as TAB_RECIBOS,
  getAllowedFacturacionTabs,
  resolveInitialFacturacionTab,
  puedeVerFuenteMultas,
  puedeVerFuenteNotasCredito,
} from "../lib/facturacionTabs";

// ─── ADR-044 T4 (2026-07-10): solapas dentro de Facturación ───────────────────
// "Pendientes con AFIP" se desarmó (spec sección 3): el monitor pasivo "Comprobantes
// por resolver" y la bandeja con acciones reales "Recibos por regularizar" pasan a
// vivir ACÁ, como solapas opcionales. La solapa por defecto (sin ?tab= en la URL) es
// la tabla de siempre para quien tiene cobranzas.view_all — CERO cambio para quien no
// toca nada nuevo.
//
// FIX F1 (gate de frontend, 2026-07-10): la resolución de qué solapas se ven y cuál es
// la de arranque vive en `facturacionTabs.js` (lib pura, testeada) — antes esta página
// asumía que TODO usuario que llegaba acá tenía `cobranzas.view_all` (porque el guard
// de la ruta lo exigía), lo que dejaba afuera al Vendedor con SOLO
// `cobranzas.invoice_annul` y al revisor con SOLO `approvals.review` que antes veían
// esta bandeja en /pendientes-afip.

// ─── Helpers de presentación del PDF ──────────────────────────────────────────
// Mismos helpers que CustomerAccountPage: generan el HTML de la ventana preview.
// Se replican localmente porque son módulo-level (no exportados) en ese archivo.

/**
 * Escapa caracteres HTML para insertar texto del usuario en HTML generado.
 * Previene XSS en el número/tipo del comprobante dentro de la preview window.
 */
function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

/**
 * Renderiza el contenido de la ventana de preview del PDF.
 * Se llama dos veces: primero con spinner de carga, después con el iframe final.
 */
function renderInvoiceTab(previewWindow, { title, body }) {
  if (!previewWindow || previewWindow.closed) return;
  previewWindow.document.open();
  previewWindow.document.write(
    `<!doctype html><html lang="es"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" /><title>${escapeHtml(title)}</title><style>:root{color-scheme:light;font-family:Inter,system-ui,sans-serif;background:#e2e8f0;color:#0f172a}*{box-sizing:border-box}body{margin:0;min-height:100vh;background:linear-gradient(180deg,#f8fafc 0%,#e2e8f0 100%)}.shell{min-height:100vh;display:flex;flex-direction:column}.header{padding:16px 20px;border-bottom:1px solid #cbd5e1;background:rgba(255,255,255,.96);backdrop-filter:blur(10px)}.eyebrow{margin:0 0 6px;font-size:11px;font-weight:800;letter-spacing:.14em;text-transform:uppercase;color:#4f46e5}.title{margin:0;font-size:20px;font-weight:700}.subtitle{margin:6px 0 0;font-size:14px;color:#475569}.content{flex:1;padding:20px}.panel{height:calc(100vh - 117px);border:1px solid #cbd5e1;border-radius:18px;overflow:hidden;background:#fff;box-shadow:0 20px 50px rgba(15,23,42,.15)}.state{height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:12px;padding:24px;text-align:center}.state-title{margin:0;font-size:18px;font-weight:700}.state-text{margin:0;max-width:480px;color:#475569;line-height:1.5}.spinner{width:42px;height:42px;border:4px solid #cbd5e1;border-top-color:#4f46e5;border-radius:999px;animation:spin .9s linear infinite}iframe{width:100%;height:100%;border:0;background:#fff}@keyframes spin{to{transform:rotate(360deg)}}</style></head><body>${body}</body></html>`
  );
  previewWindow.document.close();
}

// ─── Chip de estado fiscal ─────────────────────────────────────────────────────

/**
 * Chip visual del estado de un comprobante.
 *
 * Combina el estado de anulación (AnnulmentStatus) y el resultado fiscal (Resultado)
 * en un único chip legible. Nunca muestra códigos internos ("A", "R", "Pending").
 *
 * A diferencia del chip de la solapa del cliente, este también contempla el estado
 * "Anulada" (AnnulmentStatus.Succeeded), que aparece como opción de filtro en la
 * pantalla global.
 */
function ChipEstadoFiscal({ invoice }) {
  // Prioridad: si el comprobante está en proceso de anulación, ese estado es el más urgente.
  if (invoice.annulmentStatus === "Pending") {
    return (
      <span
        className="rounded-full px-2 py-0.5 text-[10px] font-bold uppercase bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400"
        role="status"
      >
        Anulando…
      </span>
    );
  }

  // Comprobante ya anulado (AnnulmentStatus.Succeeded)
  if (invoice.annulmentStatus === "Succeeded") {
    return (
      <span
        className="rounded-full px-2 py-0.5 text-[10px] font-bold uppercase bg-slate-100 text-slate-500 line-through dark:bg-slate-800 dark:text-slate-400"
        role="status"
      >
        Anulada
      </span>
    );
  }

  // Anulación fallida: caso excepcional, se muestra con rojo discreto
  if (invoice.annulmentStatus === "Failed") {
    return (
      <span
        className="rounded-full px-2 py-0.5 text-[10px] font-bold uppercase bg-rose-50 text-rose-600 dark:bg-rose-900/30 dark:text-rose-400"
        role="status"
      >
        Error anulación
      </span>
    );
  }

  // Estado fiscal ARCA (basado en Resultado)
  const resultado = invoice.resultado ?? invoice.Resultado;
  if (resultado === "A") {
    return (
      <span className="rounded-full px-2 py-0.5 text-[10px] font-bold uppercase bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400">
        Aprobado
      </span>
    );
  }
  if (resultado === "R") {
    return (
      <span className="rounded-full px-2 py-0.5 text-[10px] font-bold uppercase bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-400">
        Rechazado
      </span>
    );
  }
  // Sin resultado definitivo: en proceso de emisión ARCA
  return (
    <span className="rounded-full px-2 py-0.5 text-[10px] font-bold uppercase bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">
      En proceso
    </span>
  );
}

// ─── Helper de formato ─────────────────────────────────────────────────────────

/** Formatea número de comprobante en estilo "00001-00012345". */
function formatNumeroComprobante(invoice) {
  const pdv = String(invoice.puntoDeVenta ?? 0).padStart(5, "0");
  const num = String(invoice.numeroComprobante ?? 0).padStart(8, "0");
  return `${pdv}-${num}`;
}

// ─── Helpers de estado inicial ────────────────────────────────────────────────

/**
 * Construye el objeto de filtros inicial / de reset.
 * Se llama con useState lazy init y en handleReset para que la fecha
 * de "hace 90 días" tome el momento actual, no el de primera carga.
 */
function getInitialFilters() {
  return {
    ...calcularPeriodoPorDefecto(),
    tipo: "",
    estado: "",
    moneda: "",
    buscarNumero: "",
  };
}

// ─── Componente principal ──────────────────────────────────────────────────────

export default function FacturacionPage() {
  // ── Solapas (ADR-044 T4, 2026-07-10 + fix F1) ────────────────────────────────
  // Cada solapa respeta SU propio permiso (mismo criterio que /pendientes-afip antes
  // de la fusión) — la resolución vive en facturacionTabs.js (lib pura, testeada), no
  // acá, para no repetir la matriz de permisos en cada componente. Si la URL trae un
  // ?tab= que el usuario no puede ver (o que no existe), cae con gracia a la primera
  // solapa permitida — nunca rompe.
  const [searchParams, setSearchParams] = useSearchParams();
  const tabParam = searchParams.get("tab");
  const allowedTabs = getAllowedFacturacionTabs(hasPermission);
  const activeTab = resolveInitialFacturacionTab(tabParam, hasPermission);
  // Dentro de "Comprobantes por resolver", cada FUENTE respeta su propio permiso — un
  // Vendedor con solo cobranzas.invoice_annul ve la parte de multas pero NO se le
  // fetchean las NC (evita el 403 documentado en usePendingCreditNoteReviewList).
  const puedeVerMultas = puedeVerFuenteMultas(hasPermission);
  const puedeVerNotasCredito = puedeVerFuenteNotasCredito(hasPermission);

  // ── Filtros activos ──────────────────────────────────────────────────────────
  // Período por defecto: últimos 90 días (decisión UX P13=A, 2026-06-28).
  // `filters` refleja lo que el usuario VE en los inputs (actualización inmediata).
  const [filters, setFilters] = useState(getInitialFilters);

  // `filtersParaServidor` es lo que realmente se envía al backend.
  // Para todos los campos se sincroniza de inmediato; para `buscarNumero` se aplica
  // un debounce de 350ms para no llamar al endpoint en cada tecla.
  const [filtersParaServidor, setFiltersParaServidor] = useState(getInitialFilters);

  // Debounce de buscarNumero: espera 350ms de inactividad antes de disparar el fetch.
  // El efecto devuelve la función de limpieza que cancela el timer si el usuario
  // sigue escribiendo antes de que el tiempo expire.
  useEffect(() => {
    const timerId = setTimeout(() => {
      setFiltersParaServidor((prev) => {
        // Si el valor ya coincide, no actualizamos (evitamos re-render innecesario).
        if (prev.buscarNumero === filters.buscarNumero) return prev;
        return { ...prev, buscarNumero: filters.buscarNumero };
      });
    }, 350);
    return () => clearTimeout(timerId);
  }, [filters.buscarNumero]);

  // ── Paginación ───────────────────────────────────────────────────────────────
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);

  // ── Datos del servidor ───────────────────────────────────────────────────────
  // Pasamos filtersParaServidor (con buscarNumero debounceado) en lugar de filters.
  const { items, cargando, error, totalCount, totalPages, hasPreviousPage, hasNextPage, reload } =
    useFacturacionGlobal({ filters: filtersParaServidor, page, pageSize });

  // ── Callbacks de filtro y paginación ─────────────────────────────────────────

  /**
   * Al cambiar cualquier filtro, volver a página 1.
   * Si el usuario estaba en página 3 y cambia un filtro, los resultados
   * de la nueva búsqueda deben arrancar desde la primera página.
   *
   * Para campos distintos a buscarNumero, sincronizamos filtersParaServidor de inmediato.
   * Para buscarNumero, el efecto de debounce de arriba se encarga de la sincronización.
   */
  const handleFiltersChange = useCallback((nuevosFiltros) => {
    setFilters(nuevosFiltros);
    setPage(1);
    // Sincronizar inmediatamente todos los campos EXCEPTO buscarNumero.
    // buscarNumero lo sincroniza el efecto de debounce de arriba.
    setFiltersParaServidor((prev) => {
      const buscarNumeroNoCambio = prev.buscarNumero === nuevosFiltros.buscarNumero;
      if (buscarNumeroNoCambio) {
        // Solo cambiaron otros campos (tipo, estado, moneda, fechas): aplicar ya.
        return nuevosFiltros;
      }
      // buscarNumero cambió: sincronizar el resto inmediatamente y dejar que el debounce
      // actualice buscarNumero cuando el usuario deje de escribir.
      return { ...nuevosFiltros, buscarNumero: prev.buscarNumero };
    });
  }, []);

  /** Limpia todos los filtros y vuelve al período por defecto. */
  const handleReset = useCallback(() => {
    const estadoInicial = getInitialFilters();
    setFilters(estadoInicial);
    // En reset, sincronizamos ambos estados de inmediato (sin debounce).
    setFiltersParaServidor(estadoInicial);
    setPage(1);
  }, []);

  const handlePageChange = useCallback((nuevaPagina) => {
    setPage(nuevaPagina);
  }, []);

  const handlePageSizeChange = useCallback((nuevoTamano) => {
    setPageSize(nuevoTamano);
    setPage(1);
  }, []);

  // ── PDF preview (mismo patrón que CustomerAccountPage) ───────────────────────
  /**
   * Abre el PDF del comprobante en una nueva pestaña del navegador.
   * Muestra spinner mientras carga y error amigable si falla.
   * No se interpola el mensaje del error porque el statusText es en inglés.
   */
  const handleVerFactura = useCallback(async (invoice) => {
    const previewWindow = window.open("", "_blank");
    if (!previewWindow) {
      // El navegador bloqueó la ventana emergente (popup blocker activo).
      // Le avisamos al usuario en lugar de fallar silenciosamente.
      showError(
        "Revisá que el navegador no esté bloqueando las ventanas emergentes e intentá de nuevo.",
        "No se pudo abrir el comprobante"
      );
      return;
    }
    previewWindow.opener = null;

    const pdv = String(invoice.puntoDeVenta ?? 0).padStart(5, "0");
    const num = String(invoice.numeroComprobante ?? 0).padStart(8, "0");
    const tipo = formatTipoComprobante(invoice.tipoComprobante);
    const tituloFactura = `${tipo} ${pdv}-${num}`;

    // Mostrar spinner mientras se descarga el PDF
    renderInvoiceTab(previewWindow, {
      title: tituloFactura,
      body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(tituloFactura)}</h1><p class="subtitle">Preparando el comprobante para mostrar en esta pestaña.</p></div><div class="content"><div class="panel"><div class="state"><div class="spinner"></div><p class="state-title">Cargando comprobante...</p></div></div></div></div>`,
    });

    try {
      const blob = await api.get(`/invoices/${getPublicId(invoice)}/pdf`, { responseType: "blob" });
      if (!(blob instanceof Blob) || blob.size === 0) {
        throw new Error("El comprobante no devolvió un PDF válido.");
      }

      const pdfUrl = URL.createObjectURL(blob);

      // Liberar el objeto URL cuando el usuario cierre la pestaña
      const liberarTimer = window.setInterval(() => {
        if (previewWindow.closed) {
          URL.revokeObjectURL(pdfUrl);
          window.clearInterval(liberarTimer);
        }
      }, 1000);

      renderInvoiceTab(previewWindow, {
        title: tituloFactura,
        body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(tituloFactura)}</h1><p class="subtitle">Vista del comprobante emitido.</p></div><div class="content"><div class="panel"><iframe src="${pdfUrl}" title="${escapeHtml(tituloFactura)}"></iframe></div></div></div>`,
      });
    } catch {
      // Texto fijo en español: el error del endpoint suele ser statusText en inglés
      renderInvoiceTab(previewWindow, {
        title: tituloFactura,
        body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(tituloFactura)}</h1><p class="subtitle">No fue posible abrir el comprobante.</p></div><div class="content"><div class="panel"><div class="state"><p class="state-title">No se pudo cargar el comprobante</p><p class="state-text">Probá de nuevo en un momento.</p></div></div></div></div>`,
      });
    }
  }, []);

  // ── Render ────────────────────────────────────────────────────────────────────

  return (
    <div className="space-y-6">

      {/* ─ Encabezado de la pantalla ────────────────────────────────────────── */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className="rounded-lg bg-indigo-100 p-2 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300">
            <FileText className="h-5 w-5" aria-hidden="true" />
          </div>
          <div>
            <h1 className="text-2xl font-bold text-slate-900 dark:text-white">
              Facturación
            </h1>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Todos los comprobantes emitidos por la agencia.
            </p>
          </div>
        </div>

        {/* Botón de actualización manual — solo tiene sentido en la solapa "Todos". */}
        {activeTab === TAB_TODOS && (
          <button
            type="button"
            onClick={reload}
            disabled={cargando}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 dark:border-slate-600 px-3 py-1.5 text-xs font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50 transition-colors"
            aria-label="Actualizar lista de comprobantes"
            data-testid="facturacion-global-refresh"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${cargando ? "animate-spin" : ""}`} aria-hidden="true" />
            Actualizar
          </button>
        )}
      </div>

      {/* ─ Solapas (ADR-044 T4, 2026-07-10 + fix F1) ─────────────────────────────
          Solo se listan las solapas que `allowedTabs` (facturacionTabs.js) permite
          para ESTE usuario — nunca se ofrece una solapa que después 403ee. */}
      {allowedTabs.length > 0 && (
        <div className="flex flex-wrap gap-6 border-b border-slate-200 dark:border-slate-800">
          {allowedTabs.map((tab) => (
            <button
              key={tab.key}
              type="button"
              onClick={() => setSearchParams(tab.key === TAB_TODOS ? {} : { tab: tab.key }, { replace: true })}
              className={`relative pb-3 text-sm font-semibold transition-colors ${
                activeTab === tab.key
                  ? "text-indigo-600 dark:text-indigo-400"
                  : "text-slate-500 hover:text-slate-700 dark:hover:text-slate-300"
              }`}
              data-testid={`tab-facturacion-${tab.key}`}
            >
              {tab.label}
              {activeTab === tab.key && (
                <div className="absolute bottom-0 left-0 right-0 h-0.5 rounded-full bg-indigo-600 dark:bg-indigo-400" />
              )}
            </button>
          ))}
        </div>
      )}

      {/* Resguardo defensivo: sin ninguna solapa permitida (no debería pasar — el
          guard de la ruta en App.jsx ya exige al menos uno de los 3 permisos). */}
      {allowedTabs.length === 0 && (
        <div className="rounded-2xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-500 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-400">
          No tenés permiso para ver ninguna sección de Facturación.
        </div>
      )}

      {/* Solapa "Comprobantes por resolver": lista pasiva, sin filtros ni paginación. */}
      {activeTab === TAB_COMPROBANTES && (
        <ComprobantesPorResolverTab puedeVerMultas={puedeVerMultas} puedeVerNotasCredito={puedeVerNotasCredito} />
      )}

      {/* Solapa "Recibos por regularizar": la bandeja existente, TAL CUAL (conserva su
          nombre y su funcionalidad — spec sección 3.2, "Gastón no lo cambió"). */}
      {activeTab === TAB_RECIBOS && <CreditNoteReconciliationInboxPage />}

      {/* ─ Panel principal (solapa "Todos los comprobantes") ────────────────── */}
      {activeTab === TAB_TODOS && (
      <>
      <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">

        {/* Barra de filtros */}
        <div className="border-b border-slate-100 dark:border-slate-800 p-4">
          <FacturacionFilters
            filters={filters}
            onChange={handleFiltersChange}
            onReset={handleReset}
            totalResultados={cargando ? undefined : totalCount}
            isLoading={cargando}
            estadoOpciones={OPCIONES_ESTADO_FILTRO_GLOBAL}
          />
        </div>

        {/* Estado de carga */}
        {cargando && (
          <div className="flex items-center justify-center gap-2 px-6 py-12 text-sm text-slate-400">
            <Loader2 className="h-5 w-5 animate-spin" aria-hidden="true" />
            Cargando comprobantes...
          </div>
        )}

        {/* Estado de error — con opción de reintento */}
        {!cargando && error && (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <p className="text-sm text-rose-600 dark:text-rose-400" role="alert">
              {error}
            </p>
            <button
              type="button"
              onClick={reload}
              className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
            >
              <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />
              Reintentar
            </button>
          </div>
        )}

        {/* Tabla de comprobantes (desktop) */}
        {!cargando && !error && (
          <>
            {/*
              Regla multimoneda dura: cada fila muestra su propia moneda con CurrencyBadge.
              NO hay fila de total porque las monedas no se pueden sumar.
              El filtro "Moneda" de arriba ya separa; aquí mostramos lo que devolvió el servidor.
            */}
            <DataGrid density="compact" minWidth="980px" data-testid="facturacion-global-tabla">
              <DataGridHeader>
                <DataGridHeaderRow>
                  <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                  <DataGridHeaderCell>Comprobante</DataGridHeaderCell>
                  <DataGridHeaderCell>Tipo</DataGridHeaderCell>
                  <DataGridHeaderCell>Cliente</DataGridHeaderCell>
                  <DataGridHeaderCell>Moneda</DataGridHeaderCell>
                  <DataGridHeaderCell align="right">Importe</DataGridHeaderCell>
                  <DataGridHeaderCell align="center">Estado</DataGridHeaderCell>
                  <DataGridHeaderCell align="right">Acción</DataGridHeaderCell>
                </DataGridHeaderRow>
              </DataGridHeader>
              <DataGridBody>
                {items.length === 0 ? (
                  <DataGridEmptyState
                    colSpan={8}
                    title="No hay comprobantes para mostrar."
                    data-testid="facturacion-global-empty"
                  />
                ) : (
                  items.map((invoice) => {
                    // Fallback defensivo: facturas legacy pueden no traer currency
                    const moneda = invoice.currency ?? "ARS";
                    return (
                      <DataGridRow key={getPublicId(invoice)}>
                        <DataGridCell>{formatDate(invoice.createdAt)}</DataGridCell>
                        <DataGridCell className="font-mono font-semibold text-slate-900 dark:text-white">
                          {formatNumeroComprobante(invoice)}
                        </DataGridCell>
                        <DataGridCell>
                          <div className="flex items-center gap-2">
                            <Receipt className="h-4 w-4 text-indigo-400 flex-shrink-0" aria-hidden="true" />
                            {/* Nunca el int crudo: siempre texto legible del mapa ARCA */}
                            <span>{formatTipoComprobante(invoice.tipoComprobante)}</span>
                          </div>
                        </DataGridCell>
                        <DataGridCell>
                          {invoice.customerName ? (
                            <div>
                              <div className="text-sm text-slate-800 dark:text-slate-200">
                                {invoice.customerName}
                              </div>
                              {invoice.numeroReserva && (
                                <div className="text-xs text-slate-400 dark:text-slate-500">
                                  {invoice.numeroReserva}
                                </div>
                              )}
                            </div>
                          ) : (
                            <span className="text-slate-400 dark:text-slate-500">—</span>
                          )}
                        </DataGridCell>
                        <DataGridCell>
                          <CurrencyBadge currency={moneda} />
                        </DataGridCell>
                        <DataGridCell align="right" className="font-semibold text-slate-900 dark:text-white">
                          {formatCurrency(invoice.importeTotal, moneda)}
                        </DataGridCell>
                        <DataGridCell align="center">
                          <ChipEstadoFiscal invoice={invoice} />
                        </DataGridCell>
                        <DataGridActionCell>
                          <button
                            type="button"
                            onClick={() => handleVerFactura(invoice)}
                            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800 dark:hover:text-white"
                            data-testid={`ver-factura-${getPublicId(invoice)}`}
                            aria-label={`Ver comprobante ${formatNumeroComprobante(invoice)}`}
                          >
                            <Eye className="h-4 w-4" aria-hidden="true" />
                            Ver
                          </button>
                        </DataGridActionCell>
                      </DataGridRow>
                    );
                  })
                )}
              </DataGridBody>
            </DataGrid>

            {/* Cards mobile */}
            {items.length === 0 ? (
              <ListEmptyState
                title="No hay comprobantes para mostrar."
                className="md:hidden rounded-xl border border-dashed border-slate-200 dark:border-slate-800 mx-4 mb-4"
                data-testid="facturacion-global-empty-mobile"
              />
            ) : (
              <MobileRecordList>
                {items.map((invoice) => {
                  const moneda = invoice.currency ?? "ARS";
                  return (
                    <MobileRecordCard
                      key={getPublicId(invoice)}
                      statusSlot={<ChipEstadoFiscal invoice={invoice} />}
                      accentSlot={<CurrencyBadge currency={moneda} />}
                      title={formatNumeroComprobante(invoice)}
                      subtitle={formatTipoComprobante(invoice.tipoComprobante)}
                      meta={
                        <>
                          <div className="text-xs text-slate-500 dark:text-slate-400">
                            Fecha {formatDate(invoice.createdAt)}
                          </div>
                          {invoice.customerName && (
                            <div className="text-xs text-slate-500 dark:text-slate-400">
                              {invoice.customerName}
                            </div>
                          )}
                          <div className="text-xs text-slate-500 dark:text-slate-400">
                            Importe {formatCurrency(invoice.importeTotal, moneda)}
                          </div>
                        </>
                      }
                      footerActions={
                        <button
                          type="button"
                          onClick={() => handleVerFactura(invoice)}
                          className="inline-flex rounded-lg border border-slate-200 px-3 py-2 text-xs font-semibold text-slate-600 hover:bg-slate-100 dark:border-slate-700 dark:hover:bg-slate-800"
                          aria-label={`Ver comprobante ${formatNumeroComprobante(invoice)}`}
                          data-testid={`ver-factura-mobile-${getPublicId(invoice)}`}
                        >
                          Ver
                        </button>
                      }
                    />
                  );
                })}
              </MobileRecordList>
            )}
          </>
        )}
      </div>

      {/* ─ Paginación ────────────────────────────────────────────────────────── */}
      {!cargando && !error && totalCount > 0 && (
        <PaginationFooter
          page={page}
          pageSize={pageSize}
          totalCount={totalCount}
          totalPages={totalPages}
          hasPreviousPage={hasPreviousPage}
          hasNextPage={hasNextPage}
          onPageChange={handlePageChange}
          onPageSizeChange={handlePageSizeChange}
        />
      )}
      </>
      )}
    </div>
  );
}
