import { useEffect, useState } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import {
    Plus,
    Search,
    FolderOpen,
    User,
    Archive,
    Calendar,
    DollarSign,
    AlertCircle,
    CheckCircle2,
    Plane,
    TrendingUp
} from "lucide-react";
import { Button } from "../components/ui/button";
import { useNavigate } from "react-router-dom";
import CreateFileModal from "../components/CreateFileModal";
import { formatCurrency, formatDate } from "../lib/utils";
import { FilesPageSkeleton } from "../components/ui/skeleton";

export default function FilesPage() {
    const [files, setFiles] = useState([]);
    const [loading, setLoading] = useState(true);
    const [searchTerm, setSearchTerm] = useState("");
    const [viewFilter, setViewFilter] = useState("all"); // all, Presupuesto, Reservado, Operativo, archived
    const navigate = useNavigate();

    // Create Modal State
    const [isCreateModalOpen, setIsCreateModalOpen] = useState(false);

    useEffect(() => {
        loadFiles();
    }, []);

    const loadFiles = async () => {
        setLoading(true);
        try {
            const data = await api.get("/travelfiles");
            setFiles(data);
        } catch (error) {
            console.error(error);
            showError("Error cargando files: " + (error.response?.data?.Error || error.message));
            setFiles([]);
        } finally {
            setLoading(false);
        }
    };

    const handleCreateSuccess = () => {
        setIsCreateModalOpen(false);
        loadFiles();
    };

    // Status config
    const statusConfig = {
        'Presupuesto': { color: 'bg-blue-50 text-blue-700 border-blue-200 dark:bg-blue-900/20 dark:text-blue-300 dark:border-blue-800', icon: '📋' },
        'Reservado': { color: 'bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/20 dark:text-amber-300 dark:border-amber-800', icon: '📌' },
        'Operativo': { color: 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/20 dark:text-emerald-300 dark:border-emerald-800', icon: '✈️' },
        'Cerrado': { color: 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700', icon: '✅' },
        'Cancelado': { color: 'bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-900/20 dark:text-rose-300 dark:border-rose-800', icon: '❌' },
    };

    const getStatusBadge = (status) => {
        const cfg = statusConfig[status] || statusConfig['Presupuesto'];
        return (
            <span className={`px-2.5 py-0.5 rounded-full text-xs font-medium border ${cfg.color}`}>
                {status}
            </span>
        );
    };

    // Archive Logic
    const handleArchive = async (e, id) => {
        e.stopPropagation();
        if (!confirm("¿Archivar este expediente? Desaparecerá de la lista principal.")) return;

        try {
            await api.put(`/travelfiles/${id}/archive`);
            showSuccess("Expediente archivado");
            loadFiles();
        } catch (error) {
            showError(error.message || "Error al archivar");
        }
    };

    const filteredFiles = files.filter(f => {
        // Search Filter
        const searchMatch =
            f.name?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            f.fileNumber?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            f.customerName?.toLowerCase().includes(searchTerm.toLowerCase());

        // View Filter
        let viewMatch = true;
        if (viewFilter === 'all') {
            viewMatch = !['Cerrado', 'Cancelado', 'Archived'].includes(f.status);
        } else if (viewFilter === 'archived') {
            viewMatch = ['Cerrado', 'Cancelado', 'Archived'].includes(f.status);
        } else {
            viewMatch = f.status === viewFilter;
        }

        return searchMatch && viewMatch;
    });

    // KPI summary
    const activeFiles = files.filter(f => !['Cerrado', 'Cancelado', 'Archived'].includes(f.status));
    const totalSaleActive = activeFiles.reduce((sum, f) => sum + (f.totalSale || 0), 0);
    const totalPendingBalance = activeFiles.reduce((sum, f) => sum + (f.balance > 0 ? f.balance : 0), 0);
    const operativeCount = files.filter(f => f.status === 'Operativo').length;

    // Filter tab counts
    const tabCounts = {
        all: files.filter(f => !['Cerrado', 'Cancelado', 'Archived'].includes(f.status)).length,
        Presupuesto: files.filter(f => f.status === 'Presupuesto').length,
        Reservado: files.filter(f => f.status === 'Reservado').length,
        Operativo: files.filter(f => f.status === 'Operativo').length,
        archived: files.filter(f => ['Cerrado', 'Cancelado', 'Archived'].includes(f.status)).length,
    };

    if (loading && files.length === 0) {
        return <FilesPageSkeleton />;
    }

    return (
        <div className="space-y-4 md:space-y-6 animate-in fade-in duration-500">
            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-3">
                <div>
                    <h2 className="text-xl md:text-2xl font-bold tracking-tight">Gestión de Viajes</h2>
                    <p className="text-sm text-muted-foreground">Administra tus expedientes, presupuestos y ventas.</p>
                </div>
                <Button onClick={() => setIsCreateModalOpen(true)} className="w-full sm:w-auto shadow-sm">
                    <Plus className="h-4 w-4 mr-2" /> Nuevo Expediente
                </Button>
            </div>

            {/* Quick KPI Strip */}
            <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
                <div className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                    <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 mb-1">
                        <FolderOpen className="h-3.5 w-3.5" />
                        Expedientes Activos
                    </div>
                    <div className="text-xl font-bold text-slate-900 dark:text-white">{activeFiles.length}</div>
                </div>
                <div className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                    <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 mb-1">
                        <Plane className="h-3.5 w-3.5" />
                        Operativos
                    </div>
                    <div className="text-xl font-bold text-emerald-600 dark:text-emerald-400">{operativeCount}</div>
                </div>
                <div className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                    <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 mb-1">
                        <TrendingUp className="h-3.5 w-3.5" />
                        Venta Total
                    </div>
                    <div className="text-xl font-bold text-indigo-600 dark:text-indigo-400">{formatCurrency(totalSaleActive)}</div>
                </div>
                <div className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                    <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 mb-1">
                        <AlertCircle className="h-3.5 w-3.5" />
                        Por Cobrar
                    </div>
                    <div className={`text-xl font-bold ${totalPendingBalance > 0 ? 'text-rose-600 dark:text-rose-400' : 'text-emerald-600 dark:text-emerald-400'}`}>
                        {formatCurrency(totalPendingBalance)}
                    </div>
                </div>
            </div>

            {/* Toolbar */}
            <div className="flex flex-col sm:flex-row gap-4 items-center justify-between bg-white dark:bg-slate-900/50 p-2 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm">
                {/* Status Filter Tabs */}
                <div className="flex p-1 bg-slate-100 dark:bg-slate-800 rounded-lg self-start sm:self-auto overflow-x-auto">
                    {["all", "Presupuesto", "Reservado", "Operativo"].map((filter) => (
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
                    <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
                    <input
                        className="w-full bg-transparent pl-9 pr-4 py-2 text-sm border-none focus:outline-none placeholder:text-muted-foreground/70"
                        placeholder="Buscar por pasajero, nombre o ID..."
                        value={searchTerm}
                        onChange={(e) => setSearchTerm(e.target.value)}
                    />
                </div>
            </div>

            {/* Data Table (Desktop) */}
            <div className="hidden md:block rounded-xl border border-slate-200 bg-white shadow-sm overflow-hidden dark:bg-slate-900 dark:border-slate-800">
                <div className="overflow-x-auto">
                    <table className="w-full text-left text-sm">
                        <thead className="bg-slate-50 border-b border-slate-200 dark:bg-slate-950 dark:border-slate-800">
                            <tr>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">Expediente</th>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">Cliente</th>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">Inicio Viaje</th>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">Estado</th>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400 text-right">Venta</th>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400 text-right">Saldo</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                            {filteredFiles.map((file) => {
                                const hasPendingBalance = file.balance > 0;
                                const isPaid = file.totalSale > 0 && file.balance <= 0;

                                return (
                                    <tr
                                        key={file.id}
                                        onClick={() => navigate(`/files/${file.id}`)}
                                        className="group hover:bg-slate-50 dark:hover:bg-slate-800/50 cursor-pointer transition-colors"
                                    >
                                        <td className="px-6 py-4">
                                            <div className="flex items-center gap-3">
                                                <div className={`h-9 w-9 rounded-full flex items-center justify-center shrink-0 ${hasPendingBalance
                                                    ? 'bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-400'
                                                    : isPaid
                                                        ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-400'
                                                        : 'bg-indigo-50 text-indigo-600 dark:bg-indigo-900/20 dark:text-indigo-400'
                                                    }`}>
                                                    {hasPendingBalance
                                                        ? <AlertCircle className="h-4 w-4" />
                                                        : isPaid
                                                            ? <CheckCircle2 className="h-4 w-4" />
                                                            : <FolderOpen className="h-4 w-4" />
                                                    }
                                                </div>
                                                <div>
                                                    <div className="font-semibold text-slate-900 dark:text-slate-100">{file.name}</div>
                                                    <div className="text-xs text-slate-500 font-mono">{file.fileNumber}</div>
                                                </div>
                                            </div>
                                        </td>
                                        <td className="px-6 py-4">
                                            <div className="flex items-center gap-2 text-slate-600 dark:text-slate-300">
                                                <User className="h-3.5 w-3.5 opacity-70" />
                                                {file.customerName || "Sin Asignar"}
                                            </div>
                                        </td>
                                        <td className="px-6 py-4 text-right">
                                            <Button
                                                variant="ghost"
                                                size="icon"
                                                className="text-slate-400 hover:text-slate-600 dark:text-slate-500 dark:hover:text-slate-300 transition-colors"
                                                onClick={(e) => handleArchive(e, file.id)}
                                                title="Archivar Expediente"
                                            >
                                                <Archive className="h-4 w-4" />
                                            </Button>
                                        </td>
                                        <td className="px-6 py-4 text-slate-600 dark:text-slate-400">
                                            {file.startDate ? (
                                                <div className="flex items-center gap-1.5">
                                                    <Calendar className="h-3.5 w-3.5 opacity-50" />
                                                    {formatDate(file.startDate)}
                                                </div>
                                            ) : (
                                                <span className="text-slate-400">-</span>
                                            )}
                                        </td>
                                        <td className="px-6 py-4">
                                            {getStatusBadge(file.status)}
                                        </td>
                                        <td className="px-6 py-4 text-right">
                                            <div className="font-mono font-medium text-slate-700 dark:text-slate-300">
                                                {formatCurrency(file.totalSale)}
                                            </div>
                                        </td>
                                        <td className="px-6 py-4 text-right">
                                            {hasPendingBalance ? (
                                                <div className="flex flex-col items-end">
                                                    <span className="font-mono font-bold text-rose-600 dark:text-rose-400">
                                                        {formatCurrency(file.balance)}
                                                    </span>
                                                    <span className="text-[10px] text-rose-500 font-semibold uppercase">Pendiente</span>
                                                </div>
                                            ) : isPaid ? (
                                                <div className="flex items-center justify-end gap-1 text-emerald-600 dark:text-emerald-400">
                                                    <CheckCircle2 className="h-3.5 w-3.5" />
                                                    <span className="text-xs font-semibold">Pagado</span>
                                                </div>
                                            ) : (
                                                <span className="text-sm text-slate-400">-</span>
                                            )}
                                        </td>
                                    </tr>
                                );
                            })}
                            {filteredFiles.length === 0 && (
                                <tr>
                                    <td colSpan={7} className="px-6 py-12 text-center text-muted-foreground">
                                        <div className="mx-auto h-12 w-12 rounded-full bg-slate-50 flex items-center justify-center mb-3 dark:bg-slate-900">
                                            <Search className="h-6 w-6 opacity-50" />
                                        </div>
                                        <p>No se encontraron expedientes en esta vista.</p>
                                    </td>
                                </tr>
                            )}
                        </tbody>
                    </table>
                </div>
            </div>

            {/* Mobile Card View */}
            <div className="md:hidden space-y-3">
                {filteredFiles.length === 0 ? (
                    <div className="text-center py-12 bg-slate-50 dark:bg-slate-900 rounded-xl border border-dashed border-slate-200 dark:border-slate-800">
                        <div className="mx-auto h-12 w-12 rounded-full bg-white dark:bg-slate-800 flex items-center justify-center mb-3 shadow-sm border border-slate-100 dark:border-slate-700">
                            <Search className="h-5 w-5 opacity-50" />
                        </div>
                        <p className="text-muted-foreground text-sm">No se encontraron expedientes.</p>
                    </div>
                ) : (
                    filteredFiles.map((file) => {
                        const hasPendingBalance = file.balance > 0;
                        const isPaid = file.totalSale > 0 && file.balance <= 0;
                        return (
                            <div
                                key={file.id}
                                onClick={() => navigate(`/files/${file.id}`)}
                                className="bg-white dark:bg-slate-900 rounded-xl p-4 border border-slate-200 dark:border-slate-800 shadow-sm active:scale-[0.98] transition-transform"
                            >
                                <div className="flex justify-between items-start mb-3">
                                    <div className="flex items-center gap-3">
                                        <div className={`h-10 w-10 rounded-full flex items-center justify-center shrink-0 ${hasPendingBalance
                                            ? 'bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-400'
                                            : isPaid
                                                ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-400'
                                                : 'bg-indigo-50 text-indigo-600 dark:bg-indigo-900/20 dark:text-indigo-400'
                                            }`}>
                                            {hasPendingBalance
                                                ? <AlertCircle className="h-5 w-5" />
                                                : isPaid
                                                    ? <CheckCircle2 className="h-5 w-5" />
                                                    : <FolderOpen className="h-5 w-5" />
                                            }
                                        </div>
                                        <div>
                                            <div className="font-semibold text-slate-900 dark:text-white leading-tight">{file.name}</div>
                                            <div className="text-xs text-slate-500 font-mono mt-0.5">{file.fileNumber}</div>
                                        </div>
                                    </div>
                                    {getStatusBadge(file.status)}
                                </div>

                                <div className="grid grid-cols-2 gap-y-2 gap-x-4 text-sm mb-3">
                                    <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                                        <User className="h-3.5 w-3.5 opacity-70" />
                                        <span className="truncate">{file.customerName || "Sin Asignar"}</span>
                                    </div>
                                    <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                                        <Calendar className="h-3.5 w-3.5 opacity-70" />
                                        <span>{file.startDate ? formatDate(file.startDate) : "-"}</span>
                                    </div>
                                </div>

                                <div className="flex justify-between items-center pt-3 border-t border-slate-100 dark:border-slate-800">
                                    <div className="text-xs text-slate-500">
                                        Venta: <span className="font-medium text-slate-900 dark:text-slate-200">{formatCurrency(file.totalSale)}</span>
                                    </div>
                                    <div className="text-right">
                                        <span className={`text-sm font-bold ${hasPendingBalance ? 'text-rose-600 dark:text-rose-400' : 'text-emerald-600 dark:text-emerald-400'}`}>
                                            {hasPendingBalance ? "Saldo: " + formatCurrency(file.balance) : "Pagado"}
                                        </span>
                                    </div>
                                </div>
                            </div>
                        );
                    })
                )}
            </div>

            {/* Create Modal */}
            <CreateFileModal
                isOpen={isCreateModalOpen}
                onClose={() => setIsCreateModalOpen(false)}
                onSuccess={handleCreateSuccess}
            />
        </div>
    );
}
