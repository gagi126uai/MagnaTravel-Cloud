import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search } from "lucide-react";

import { useReservas } from "../hooks/useReservas";
import { Button } from "../../../components/ui/button";
import CreateReservaModal from "../../../components/CreateReservaModal";
import { FilesPageSkeleton } from "../../../components/ui/skeleton";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";

import { ReservaKPIs } from "../components/ReservaKPIs";
import { ReservaTable } from "../components/ReservaTable";
import { ReservaMobileList } from "../components/ReservaMobileList";

const tabs = [
  { value: "active", label: "Activas" },
  { value: "reserved", label: "Reservadas" },
  { value: "operative", label: "Operativas" },
  { value: "closed", label: "Cerradas" },
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
    loadReservas,
    handleArchive,
    tabCounts,
    stats,
    databaseUnavailable,
  } = useReservas();

  const refresh = () => {
    setIsModalOpen(false);
    loadReservas();
  };

  if (loading && reservas.length === 0) {
    return <FilesPageSkeleton />;
  }

  return (
    <div className="animate-in fade-in space-y-4 duration-500 md:space-y-6">
      <div className="flex flex-col items-start justify-between gap-3 sm:flex-row sm:items-center">
        <div>
          <h2 className="text-xl font-bold tracking-tight text-slate-900 dark:text-white md:text-2xl">Reservas</h2>
          <p className="text-sm text-slate-500 dark:text-slate-400">Administra tus reservas, presupuestos y ventas.</p>
        </div>
        <Button onClick={() => setIsModalOpen(true)} className="w-full shadow-sm sm:w-auto">
          <Plus className="mr-2 h-4 w-4" /> Nueva Reserva
        </Button>
      </div>

      <ReservaKPIs stats={stats} />

      <div className="flex flex-col items-center justify-between gap-4 rounded-xl border border-slate-200 bg-white p-2 shadow-sm dark:border-slate-800 dark:bg-slate-900/50 sm:flex-row">
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

        <div className="relative w-full sm:max-w-sm">
          <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <input
            className="w-full border-none bg-transparent py-2 pl-9 pr-4 text-sm placeholder:text-slate-500/70 focus:outline-none"
            placeholder="Buscar por reserva, nombre o cliente..."
            value={searchTerm}
            onChange={(event) => setSearchTerm(event.target.value)}
          />
        </div>
      </div>

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
        onSuccess={refresh}
      />
    </div>
  );
}
