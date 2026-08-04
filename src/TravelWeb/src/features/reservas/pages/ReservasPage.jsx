import { useEffect, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { Plus, Search, ChevronLeft, ChevronRight, X } from "lucide-react";

import { useReservas } from "../hooks/useReservas";
import { tabCountKey, esSolapaApagada, resolverSolapaVisible, debeSaltarATodas } from "../lib/reservaTabsMapping";
import { Button } from "../../../components/ui/button";
import CreateReservaModal from "../../../components/CreateReservaModal";
import { FilesPageSkeleton } from "../../../components/ui/skeleton";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { ListLoadErrorState } from "../../../components/ui/ListLoadErrorState";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { ListPageHeader } from "../../../components/ui/ListPageHeader";
import { ListToolbar } from "../../../components/ui/ListToolbar";

import { ReservaKPIs } from "../components/ReservaKPIs";
import { ReservaTable } from "../components/ReservaTable";
import { ReservaMobileList } from "../components/ReservaMobileList";

/**
 * Pestanas del ciclo de vida unico (ADR-020).
 * Ya no hay flags: "Vendida" murio, este es el ciclo directo y unico.
 *
 * ADR-036 (2026-06-21): se elimina "A liquidar" (ToSettle) de toda la UI.
 * El estado dejó de existir para el usuario.
 *
 * Tanda 3 (2026-07-24, #38/#40): se migran los "view" legacy del backend ("reserved" →
 * "confirmed", "operative" → "traveling") y se agrega la pestaña "Anuladas" (view=cancelled),
 * separada de "Finalizadas" (antes venian mezcladas — ver docs/ux/2026-07-06-listado-finalizadas-vs-anuladas.md,
 * firmada). El backend de este cambio se está terminando en paralelo: si "cancelled" todavía
 * no está soportado, la pestaña puede mostrar de más hasta que se despliegue junto con esto.
 *
 * "quotation"     → Cotizaciones (borrador)
 * "budget"        → Presupuestos (enviados al cliente)
 * "in-management" → En gestión (el cliente acepto, se solicitan servicios)
 * "active"        → Activas (En gestión + Confirmadas — vista combinada para el dia a dia)
 * "confirmed"     → Confirmadas (todos los servicios resueltos, candado activo)
 * "traveling"     → En viaje
 * "closed"        → Finalizadas (solo cerradas; anuladas viven en su propia pestaña)
 * "cancelled"     → Anuladas (incluye "esperando reembolso del operador")
 * "lost"          → Perdidas (no compraron)
 * "archived"      → Archivadas
 * "all"           → Todas (sin filtro de estado; H20, 2026-07-25, decision firmada 12)
 *
 * Tanda 1 rediseño listado (2026-08-04, plan B2): se elimina la solapa "Borradores
 * anteriores" (view=quotation) — la maqueta firmada la da de baja (P1 quedó = A: la
 * reserva sigue naciendo como Presupuesto, no como Borrador). "Cotización"/"quotation"
 * sigue existiendo como estado interno, solo pierde su solapa propia en este listado.
 */
const TABS = [
  // "Todas" primero (mismo lugar que ya usa el boton "Todas" de Cobranza/Facturacion):
  // sin filtro de estado, la vista completa para buscar cualquier reserva sin adivinar
  // en que pestaña esta.
  { value: "all", label: "Todas" },
  { value: "budget", label: "Presupuestos" },
  { value: "in-management", label: "En gestión" },
  { value: "confirmed", label: "Confirmadas" },
  { value: "traveling", label: "En viaje" },
  { value: "closed", label: "Finalizadas" },
  { value: "cancelled", label: "Anuladas" },
  { value: "lost", label: "Perdidas" },
  { value: "archived", label: "Archivadas" },
];

export default function ReservasPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [initialPayerId, setInitialPayerId] = useState("");

  useEffect(() => {
    const params = new URLSearchParams(location.search);
    if (params.get("create") !== "1") return;
    setInitialPayerId(params.get("customerPublicId") || "");
    setIsModalOpen(true);
    navigate(location.pathname, { replace: true });
  }, [location.pathname, location.search, navigate]);

  // ADR-020: ciclo unico sin flags de ciclo. El array TABS es directo.
  const tabs = TABS;

  const {
    reservas,
    loading,
    searchTerm,
    debouncedSearch,
    setSearchTerm,
    viewFilter,
    setViewFilter,
    page,
    pageSize,
    totalCount,
    totalPages,
    hasPreviousPage,
    hasNextPage,
    setPage,
    setPageSize,
    dateRange,
    setDateRange,
    currentMonth,
    setCurrentMonth,
    loadReservas,
    handleArchive,
    tabCounts,
    stats,
    databaseUnavailable,
    loadError,
  } = useReservas();

  // B3 (Tanda 1, 2026-08-04): mientras hay texto en el buscador, el motor busca en
  // TODAS las reservas ignorando la solapa y el mes (ver useReservas.js). Esto es
  // solo para decidir qué se VE marcado/atenuado — el filtro real (viewFilter) no
  // se toca, así que al borrar el texto todo vuelve solo a como estaba.
  // Se decide con el texto DEBOUNCED (el que realmente viajo al motor), no con searchTerm crudo:
  // asi lo que se ve marcado siempre coincide con lo que la lista esta mostrando.
  const isSearching = Boolean(debouncedSearch.trim());
  const solapaVisible = resolverSolapaVisible(viewFilter, isSearching);

  // B2: si la solapa activa se queda sin resultados (ej. se archivó la última
  // reserva de "En gestión"), salta a "Todas" para no dejar una pantalla vacía sin
  // salida (P-11⭐). No corre mientras se está buscando (ver debeSaltarATodas).
  useEffect(() => {
    // Fix de review (2026-08-04): si la carga fallo (o esta en curso), los contadores estan en 0
    // por el vaciado de emergencia, NO porque la solapa se haya quedado sin reservas — saltar a
    // "Todas" en ese momento le cambiaria la solapa elegida al usuario por un error de red.
    if (loading || loadError || databaseUnavailable) return;
    if (debeSaltarATodas(viewFilter, tabCounts, isSearching)) {
      setViewFilter("all");
    }
  }, [viewFilter, tabCounts, isSearching, setViewFilter, loading, loadError, databaseUnavailable]);

  const handleCreateSuccess = (publicId) => {
    setIsModalOpen(false);
    if (publicId) {
      navigate(`/reservas/${publicId}`);
    } else {
      loadReservas();
    }
  };

  const handlePrevMonth = () => {
    setCurrentMonth(prev => new Date(prev.getFullYear(), prev.getMonth() - 1, 1));
  };
  const handleNextMonth = () => {
    setCurrentMonth(prev => new Date(prev.getFullYear(), prev.getMonth() + 1, 1));
  };
  const monthName = currentMonth ? currentMonth.toLocaleDateString("es-AR", { month: "long", year: "numeric" }) : "";
  const previousMonthDate = currentMonth
    ? new Date(currentMonth.getFullYear(), currentMonth.getMonth() - 1, 1)
    : null;
  const previousMonthName = previousMonthDate
    ? previousMonthDate.toLocaleDateString("es-AR", { month: "long", year: "numeric" })
    : "";

  // Saca el filtro de mes (vuelve a "Todos los meses") — mismo botón que usan tanto
  // el vacío de mes como el vacío de otros períodos (B5).
  const verTodosLosMeses = () => setDateRange((prev) => ({ ...prev, preset: "all", from: "", to: "" }));

  // B5 (P-11⭐: ningún cartel deja al usuario sin salida). Se arma acá (no dentro de
  // ReservaTable) porque solo esta página tiene el mes/período activo para escribir
  // un mensaje útil ("no hay reservas en TAL mes", no un genérico "sin resultados").
  let emptyState = null;
  if (!loading && reservas.length === 0 && !databaseUnavailable && !loadError) {
    if (isSearching) {
      emptyState = (
        <ListEmptyState
          icon={Search}
          title="No encontramos ninguna reserva con ese número o cliente"
          description="Revisá cómo lo escribiste, o limpiá la búsqueda para ver el listado completo."
          action={
            <Button variant="outline" size="sm" onClick={() => setSearchTerm("")}>
              Limpiar búsqueda
            </Button>
          }
        />
      );
    } else if (dateRange.preset === "month") {
      emptyState = (
        <ListEmptyState
          title={`No hay reservas creadas en ${monthName}`}
          description="Probá con otro mes, o buscá por número de reserva o cliente — el buscador mira en todas."
          action={
            <div className="flex flex-wrap items-center justify-center gap-2">
              <Button variant="outline" size="sm" onClick={handlePrevMonth}>
                <ChevronLeft className="mr-1 h-3.5 w-3.5" />
                Ir a {previousMonthName}
              </Button>
              <Button variant="outline" size="sm" onClick={verTodosLosMeses}>
                Ver todos los meses
              </Button>
            </div>
          }
        />
      );
    } else {
      emptyState = (
        <ListEmptyState
          title="No hay reservas para este período"
          description="Probá con otro rango de fechas, o buscá por número de reserva o cliente."
          action={
            <Button variant="outline" size="sm" onClick={verTodosLosMeses}>
              Ver todos los meses
            </Button>
          }
        />
      );
    }
  }

  if (loading && reservas.length === 0) {
    return <FilesPageSkeleton />;
  }

  return (
    <div className="animate-in fade-in space-y-4 duration-500 md:space-y-6">
      <ListPageHeader
        title="Reservas"
        subtitle="Administra tus reservas, presupuestos y ventas."
        actions={
          <Button onClick={() => { setInitialPayerId(""); setIsModalOpen(true); }} className="w-full shadow-sm sm:w-auto">
            <Plus className="mr-2 h-4 w-4" /> Nuevo Presupuesto
          </Button>
        }
      />

      <ReservaKPIs stats={stats} />

      {/* Tabs en su propia fila — ocupan todo el ancho y permiten scroll horizontal sin tapar otros controles */}
      <div className="rounded-xl border border-slate-200 bg-white p-2 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        {/* ADR-020: ciclo unico, tabs directas sin esperar flags.
            B2 (Tanda 1, 2026-08-04): una solapa en 0 queda VISIBLE pero apagada y sin
            poder tocarla ("cero" también es información). Mientras se busca (B3), la
            que se ve marcada pasa a ser "Todas" sin tocar el filtro real. */}
        <div className="flex overflow-x-auto scrollbar-hide rounded-lg bg-slate-100 p-1 dark:bg-slate-800">
          {tabs.map((tab) => {
            const count = tabCounts[tabCountKey(tab.value)] || 0;
            const apagada = esSolapaApagada(tab.value, count);
            const activa = solapaVisible === tab.value;
            return (
              <button
                key={tab.value}
                type="button"
                disabled={apagada}
                onClick={() => setViewFilter(tab.value)}
                data-testid={`tab-reservas-${tab.value}`}
                className={`flex items-center gap-1.5 whitespace-nowrap rounded-md px-3 py-1.5 text-sm font-medium transition-all disabled:cursor-not-allowed ${
                  activa
                    ? "bg-white text-slate-900 shadow dark:bg-slate-700 dark:text-white"
                    : apagada
                      ? "text-slate-300 dark:text-slate-700"
                      : "text-slate-500 hover:text-slate-700 dark:text-slate-400"
                }`}
              >
                {tab.label}
                <span
                  className={`rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${
                    activa
                      ? "bg-indigo-100 text-indigo-700 dark:bg-indigo-900/40 dark:text-indigo-300"
                      : apagada
                        ? "bg-transparent text-slate-300 dark:text-slate-700"
                        : "bg-slate-200 text-slate-500 dark:bg-slate-700 dark:text-slate-400"
                  }`}
                >
                  {count}
                </span>
              </button>
            );
          })}
        </div>
      </div>

      {/* Filtros (buscar + rango fecha + tipo de fecha) en su propia fila.
          B3 (Tanda 1, 2026-08-04): el buscador pasa a ser lo más ancho de la fila
          (va en searchSlot, que ListToolbar estira con flex-1); los filtros de
          fecha pasan a actionSlot, con ancho fijo. Antes era al revés. */}
      <ListToolbar
        className="p-1.5"
        searchSlot={
          <div className="flex flex-col gap-1">
            <div className="relative">
              <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400" />
              <input
                className="w-full rounded-lg border border-slate-200 bg-slate-50 py-2 pl-9 pr-9 text-sm placeholder:text-slate-500/70 focus:outline-none focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800/50 dark:text-white"
                placeholder="Buscar por N° de reserva o cliente…"
                aria-label="Buscar por número de reserva o cliente en todas las reservas"
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                data-testid="reservas-search-input"
              />
              {/* El "×" responde a lo TIPEADO (searchTerm crudo), no al debounced: apenas hay
                  texto tiene que poder borrarse, sin esperar los 300 ms. */}
              {searchTerm ? (
                <button
                  type="button"
                  onClick={() => setSearchTerm("")}
                  aria-label="Limpiar búsqueda"
                  className="absolute right-2.5 top-1/2 -translate-y-1/2 rounded-full p-0.5 text-slate-400 hover:bg-slate-200 hover:text-slate-600 dark:hover:bg-slate-700"
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              ) : null}
            </div>
            {isSearching ? (
              <p className="pl-1 text-[11px] text-slate-400 dark:text-slate-500">
                Buscando en todas las reservas, sin filtro de mes.
              </p>
            ) : null}
          </div>
        }
        actionSlot={
          <div
            className={`flex flex-wrap items-center gap-2 transition-opacity ${isSearching ? "pointer-events-none opacity-40" : ""}`}
            aria-disabled={isSearching}
            title={isSearching ? "El buscador ignora el mes mientras hay texto" : undefined}
          >
            <div className="flex items-center gap-1 rounded-lg border border-slate-200 bg-slate-50 px-2 py-1 dark:border-slate-700 dark:bg-slate-800/50">
              <span className="text-[10px] font-bold uppercase text-slate-500">Por</span>
              <select
                className="rounded bg-transparent p-1 text-xs font-bold text-slate-700 focus:outline-none dark:text-slate-200"
                value={dateRange.field}
                onChange={(e) => setDateRange((prev) => ({ ...prev, field: e.target.value }))}
                title="Campo de fecha sobre el que filtrar"
                disabled={isSearching}
              >
                <option value="created">creación</option>
                <option value="travel">viaje</option>
              </select>
            </div>

            <div className="flex flex-wrap items-center gap-1 rounded-lg border border-slate-200 bg-slate-50 p-1 dark:border-slate-700 dark:bg-slate-800/50">
              <select
                className="rounded bg-transparent p-1.5 text-xs font-bold text-slate-700 focus:outline-none dark:text-slate-200"
                value={dateRange.preset}
                disabled={isSearching}
                onChange={(e) => {
                  const preset = e.target.value;
                  const today = new Date();
                  let from = "";
                  if (preset === "90days") {
                    from = new Date(today.getTime() - 90 * 24 * 60 * 60 * 1000).toISOString().split("T")[0];
                  } else if (preset === "365days") {
                    from = new Date(today.getTime() - 365 * 24 * 60 * 60 * 1000).toISOString().split("T")[0];
                  }
                  setDateRange((prev) => ({ ...prev, from, to: "", preset }));
                }}
              >
                <option value="month">Mes a Mes</option>
                <option value="90days">Últimos 90 días</option>
                <option value="365days">Último año</option>
                <option value="all">Todas</option>
                <option value="custom">Personalizado</option>
              </select>
              {dateRange.preset === "month" && (
                <div className="flex items-center gap-0.5 rounded-lg bg-white p-0.5 dark:bg-slate-900">
                  <button onClick={handlePrevMonth} disabled={isSearching} className="rounded p-1 text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-700 disabled:pointer-events-none dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-white" title="Mes anterior">
                    <ChevronLeft className="h-4 w-4" />
                  </button>
                  <div className="flex items-center px-1">
                    <span className="w-[90px] text-center text-[10px] font-black capitalize text-slate-700 dark:text-slate-200">
                      {monthName}
                    </span>
                  </div>
                  <button onClick={handleNextMonth} disabled={isSearching} className="rounded p-0.5 text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-700 disabled:pointer-events-none dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-white" title="Mes siguiente">
                    <ChevronRight className="h-4 w-4" />
                  </button>
                </div>
              )}
              {dateRange.preset === "custom" && (
                <div className="flex items-center gap-1 rounded-md bg-white p-0.5 dark:bg-slate-900">
                  <input
                    type="date"
                    disabled={isSearching}
                    className="w-[120px] bg-transparent px-1 text-xs font-medium text-slate-700 focus:outline-none dark:text-slate-200"
                    value={dateRange.from}
                    onChange={(e) => setDateRange((prev) => ({ ...prev, from: e.target.value }))}
                  />
                  <span className="text-xs text-slate-400">→</span>
                  <input
                    type="date"
                    disabled={isSearching}
                    className="w-[120px] bg-transparent px-1 text-xs font-medium text-slate-700 focus:outline-none dark:text-slate-200"
                    value={dateRange.to}
                    onChange={(e) => setDateRange((prev) => ({ ...prev, to: e.target.value }))}
                  />
                </div>
              )}
            </div>
          </div>
        }
      />

      {databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : loadError ? (
        <ListLoadErrorState message={loadError} onRetry={loadReservas} />
      ) : (
        <>
          <div className="hidden md:block">
            <ReservaTable
              reservas={reservas}
              onRowClick={(id) => navigate(`/reservas/${id}`)}
              onArchive={handleArchive}
              emptyState={emptyState}
            />
          </div>

          <div className="md:hidden">
            <ReservaMobileList
              reservas={reservas}
              onRowClick={(id) => navigate(`/reservas/${id}`)}
              onArchive={handleArchive}
              emptyState={emptyState}
            />
          </div>

          <PaginationFooter
            page={page}
            pageSize={pageSize}
            totalCount={totalCount}
            totalPages={totalPages}
            hasPreviousPage={hasPreviousPage}
            hasNextPage={hasNextPage}
            onPageChange={setPage}
            onPageSizeChange={setPageSize}
          />
        </>
      )}

      <CreateReservaModal
        isOpen={isModalOpen}
        onClose={() => { setIsModalOpen(false); setInitialPayerId(""); }}
        onSuccess={handleCreateSuccess}
        initialPayerId={initialPayerId}
      />
    </div>
  );
}
