import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search, ChevronLeft, ChevronRight, Calendar } from "lucide-react";

import { useReservas } from "../hooks/useReservas";
import { Button } from "../../../components/ui/button";
import CreateReservaModal from "../../../components/CreateReservaModal";
import { FilesPageSkeleton } from "../../../components/ui/skeleton";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { ListPageHeader } from "../../../components/ui/ListPageHeader";
import { ListToolbar } from "../../../components/ui/ListToolbar";

import { ReservaKPIs } from "../components/ReservaKPIs";
import { ReservaTable } from "../components/ReservaTable";
import { ReservaMobileList } from "../components/ReservaMobileList";

const tabs = [
  { value: "budget", label: "Presupuestos" },
  { value: "active", label: "Activas" },
  { value: "reserved", label: "Reservadas" },
  { value: "operative", label: "Operativas" },
  { value: "closed", label: "Cerradas" },
  { value: "archived", label: "Archivadas" },
];

export default function ReservasPage() {
  const navigate = useNavigate();
  const [isModalOpen, setIsModalOpen] = useState(false);

  const {
    reservas,
    loading,
    searchTerm,
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
  } = useReservas();

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

  if (loading && reservas.length === 0) {
    return <FilesPageSkeleton />;
  }

  return (
    <div className="animate-in fade-in space-y-4 duration-500 md:space-y-6">
      <ListPageHeader
        title="Reservas"
        subtitle="Administra tus reservas, presupuestos y ventas."
        actions={
          <Button onClick={() => setIsModalOpen(true)} className="w-full shadow-sm sm:w-auto">
            <Plus className="mr-2 h-4 w-4" /> Nueva Reserva
          </Button>
        }
      />

      <ReservaKPIs stats={stats} />

      <ListToolbar
        className="p-2"
        searchSlot={
          <div className="flex self-start overflow-x-auto rounded-lg bg-slate-100 p-1 dark:bg-slate-800 sm:self-auto">
            {tabs.map((tab) => (
              <button
                key={tab.value}
                onClick={() => setViewFilter(tab.value)}
                className={`flex items-center gap-1.5 whitespace-nowrap rounded-md px-3 py-1.5 text-sm font-medium transition-all ${
                  viewFilter === tab.value
                    ? "bg-white text-slate-900 shadow dark:bg-slate-700 dark:text-white"
                    : "text-slate-500 hover:text-slate-700 dark:text-slate-400"
                }`}
              >
                {tab.label}
                <span
                  className={`rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${
                    viewFilter === tab.value
                      ? "bg-indigo-100 text-indigo-700 dark:bg-indigo-900/40 dark:text-indigo-300"
                      : "bg-slate-200 text-slate-500 dark:bg-slate-700 dark:text-slate-400"
                  }`}
                >
                  {tabCounts[tab.value] || 0}
                </span>
              </button>
            ))}
          </div>
        }
        actionSlot={
          <div className="flex w-full flex-col gap-3 sm:w-auto sm:flex-row">
            <div className="flex w-full items-center gap-1 rounded-lg border border-slate-200 bg-slate-50 p-1 dark:border-slate-700 dark:bg-slate-800/50 sm:w-auto">
              <select
                className="w-full rounded bg-transparent p-1.5 text-sm font-medium text-slate-700 focus:outline-none dark:text-slate-200 sm:w-[140px]"
                value={dateRange.preset}
                onChange={(e) => {
                  const preset = e.target.value;
                  const today = new Date();
                  let from = "";
                  if (preset === "90days") {
                    from = new Date(today.getTime() - 90 * 24 * 60 * 60 * 1000).toISOString().split("T")[0];
                  } else if (preset === "365days") {
                    from = new Date(today.getTime() - 365 * 24 * 60 * 60 * 1000).toISOString().split("T")[0];
                  }
                  setDateRange({ from, to: "", preset });
                }}
              >
                <option value="month">Mes a Mes</option>
                <option value="90days">Últimos 90 días</option>
                <option value="365days">Último año</option>
                <option value="all">Todas</option>
                <option value="custom">Personalizado</option>
              </select>
              {dateRange.preset === "month" && (
                <div className="flex w-full items-center justify-between gap-1 rounded-lg bg-white p-1 dark:bg-slate-900 sm:w-auto sm:justify-center">
                  <button onClick={handlePrevMonth} className="rounded p-1 text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-700 dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-white" title="Mes anterior">
                    <ChevronLeft className="h-4 w-4" />
                  </button>
                  <div className="flex items-center gap-1.5 px-1 sm:px-2">
                    <Calendar className="h-3.5 w-3.5 text-indigo-500" />
                    <span className="w-[110px] text-center text-xs font-medium capitalize text-slate-700 dark:text-slate-200">
                      {monthName}
                    </span>
                  </div>
                  <button onClick={handleNextMonth} className="rounded p-1 text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-700 dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-white" title="Mes siguiente">
                    <ChevronRight className="h-4 w-4" />
                  </button>
                </div>
              )}
              {dateRange.preset === "custom" && (
                <div className="flex items-center gap-1">
                  <input
                    type="date"
                    className="rounded border border-slate-200 bg-white p-1 text-xs dark:border-slate-700 dark:bg-slate-900"
                    value={dateRange.from}
                    onChange={(e) => setDateRange((prev) => ({ ...prev, from: e.target.value }))}
                  />
                  <span className="text-xs text-slate-500">-</span>
                  <input
                    type="date"
                    className="rounded border border-slate-200 bg-white p-1 text-xs dark:border-slate-700 dark:bg-slate-900"
                    value={dateRange.to}
                    onChange={(e) => setDateRange((prev) => ({ ...prev, to: e.target.value }))}
                  />
                </div>
              )}
            </div>

            <div className="relative w-full sm:max-w-[200px] md:max-w-[240px]">
              <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input
                className="w-full rounded-lg border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm placeholder:text-slate-500/70 focus:outline-none focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800/50 dark:text-white"
                placeholder="Buscar reserva..."
                value={searchTerm}
                onChange={(event) => setSearchTerm(event.target.value)}
              />
            </div>
          </div>
        }
      />

      {databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : (
        <>
          <div className="hidden md:block">
            <ReservaTable
              reservas={reservas}
              onRowClick={(id) => navigate(`/reservas/${id}`)}
              onArchive={handleArchive}
            />
          </div>

          <div className="md:hidden">
            <ReservaMobileList reservas={reservas} onRowClick={(id) => navigate(`/reservas/${id}`)} />
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
        onClose={() => setIsModalOpen(false)}
        onSuccess={handleCreateSuccess}
      />
    </div>
  );
}

