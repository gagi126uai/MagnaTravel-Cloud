import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search } from "lucide-react";

import { useReservas } from "../hooks/useReservas";
import { Button } from "../../../components/ui/button";
import CreateReservaModal from "../../../components/CreateReservaModal";
import { FilesPageSkeleton } from "../../../components/ui/skeleton";

import { ReservaKPIs } from "../components/ReservaKPIs";
import { ReservaTable } from "../components/ReservaTable";
import { ReservaMobileList } from "../components/ReservaMobileList";

export default function ReservasPage() {
    const navigate = useNavigate();
    const [isModalOpen, setIsModalOpen] = useState(false);

    const {
        loading,
        searchTerm,
        setSearchTerm,
        viewFilter,
        setViewFilter,
        loadReservas,
        handleArchive,
        sortedReservas,
        tabCounts,
        stats
    } = useReservas();

    const refresh = () => {
        setIsModalOpen(false);
        loadReservas();
    };

    if (loading && sortedReservas.length === 0) {
        return <FilesPageSkeleton />;
    }

    return (
        <div className="space-y-4 md:space-y-6 animate-in fade-in duration-500">
            {/* Header section */}
            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-3">
                <div>
                    <h2 className="text-xl md:text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Reservas</h2>
                    <p className="text-sm text-slate-500 dark:text-slate-400">Administra tus reservas, presupuestos y ventas.</p>
                </div>
                <Button onClick={() => setIsModalOpen(true)} className="w-full sm:w-auto shadow-sm">
                    <Plus className="h-4 w-4 mr-2" /> Nueva Reserva
                </Button>
            </div>

            {/* Quick KPI Strip */}
            <ReservaKPIs stats={stats} />

            {/* Toolbar */}
            <div className="flex flex-col sm:flex-row gap-4 items-center justify-between bg-white dark:bg-slate-900/50 p-2 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm">
                {/* Status Filter Tabs */}
                <div className="flex p-1 bg-slate-100 dark:bg-slate-800 rounded-lg self-start sm:self-auto overflow-x-auto">
                    {["all", "Reservado", "Operativo", "Cerrado"].map((filter) => (
                        <button
                            key={filter}
                            onClick={() => setViewFilter(filter)}
                            className={`px-3 py-1.5 text-sm font-medium rounded-md transition-all whitespace-nowrap flex items-center gap-1.5 ${viewFilter === filter ? 'bg-white text-slate-900 shadow dark:bg-slate-700 dark:text-white' : 'text-slate-500 hover:text-slate-700 dark:text-slate-400'}`}
                        >
                            {filter === "all" ? "Activos" : filter + "s"}
                            <span className={`text-[10px] font-semibold rounded-full px-1.5 py-0.5 ${viewFilter === filter ? 'bg-indigo-100 text-indigo-700 dark:bg-indigo-900/40 dark:text-indigo-300' : 'bg-slate-200 text-slate-500 dark:bg-slate-700 dark:text-slate-400'}`}>
                                {tabCounts[filter] || 0}
                            </span>
                        </button>
                    ))}
                </div>

                {/* Search */}
                <div className="relative w-full sm:max-w-sm">
                    <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
                    <input
                        className="w-full bg-transparent pl-9 pr-4 py-2 text-sm border-none focus:outline-none placeholder:text-slate-500/70"
                        placeholder="Buscar por pasajero, nombre o ID..."
                        value={searchTerm}
                        onChange={(e) => setSearchTerm(e.target.value)}
                    />
                </div>
            </div>

            {/* Data Table (Desktop) */}
            <div className="hidden md:block">
                <ReservaTable
                    reservas={sortedReservas}
                    onRowClick={(id) => navigate(`/reservas/${id}`)}
                    onArchive={handleArchive}
                />
            </div>

            {/* Mobile Card View */}
            <div className="md:hidden">
                <ReservaMobileList
                    reservas={sortedReservas}
                    onRowClick={(id) => navigate(`/reservas/${id}`)}
                />
            </div>

            {/* Create Modal */}
            <CreateReservaModal
                isOpen={isModalOpen}
                onClose={() => setIsModalOpen(false)}
                onSuccess={refresh}
            />
        </div>
    );
}
