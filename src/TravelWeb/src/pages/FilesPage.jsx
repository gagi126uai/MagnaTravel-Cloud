import { useEffect, useState } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import {
    Plus,
    Search,
    FolderOpen,
    User,
    Archive
} from "lucide-react";
import { Button } from "../components/ui/button";
import { useNavigate } from "react-router-dom";
import CreateFileModal from "../components/CreateFileModal";

export default function FilesPage() {
    const [files, setFiles] = useState([]);
    const [loading, setLoading] = useState(false);
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
            // Silent fail or empty
            setFiles([]);
        } finally {
            setLoading(false);
        }
    };

    const handleCreateSuccess = () => {
        setIsCreateModalOpen(false);
        loadFiles();
    };

    // Status Helpers
    const getStatusBadge = (status) => {
        const styles = {
            'Presupuesto': 'bg-blue-50 text-blue-700 border-blue-200 dark:bg-blue-900/20 dark:text-blue-300 dark:border-blue-800',
            'Reservado': 'bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/20 dark:text-amber-300 dark:border-amber-800',
            'Operativo': 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/20 dark:text-emerald-300 dark:border-emerald-800',
            'Cerrado': 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700',
            'Cancelado': 'bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-900/20 dark:text-rose-300 dark:border-rose-800',
        };
        return (
            <span className={`px-2.5 py-0.5 rounded-full text-xs font-medium border ${styles[status] || styles['Presupuesto']}`}>
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
            loadFiles(); // Refresh list to remove it from current view (unless view is 'archived')
        } catch (error) {
            showError(error.message || "Error al archivar");
        }
    };

    const filteredFiles = files.filter(f => {
        // Search Filter
        const searchMatch =
            f.name?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            f.fileNumber?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            f.payer?.fullName?.toLowerCase().includes(searchTerm.toLowerCase());

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

    return (
        <div className="space-y-4 md:space-y-6">
            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-3">
                <div>
                    <h2 className="text-xl md:text-2xl font-bold tracking-tight">Gestión de Viajes</h2>
                    <p className="text-sm text-muted-foreground">Administra tus expedientes, presupuestos y ventas.</p>
                </div>
                <Button onClick={() => setIsCreateModalOpen(true)} className="w-full sm:w-auto shadow-sm">
                    <Plus className="h-4 w-4 mr-2" /> Nuevo Expediente
                </Button>
            </div>

            {/* Toolbar */}
            <div className="flex flex-col sm:flex-row gap-4 items-center justify-between bg-white dark:bg-slate-900/50 p-2 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm">
                {/* Status Filter Tabs */}
                <div className="flex p-1 bg-slate-100 dark:bg-slate-800 rounded-lg self-start sm:self-auto overflow-x-auto">
                    {["all", "Presupuesto", "Reservado", "Operativo", "archived"].map((filter) => (
                        <button
                            key={filter}
                            onClick={() => setViewFilter(filter)}
                            className={`px-3 py-1.5 text-sm font-medium rounded-md transition-all whitespace-nowrap ${viewFilter === filter ? 'bg-white text-slate-900 shadow dark:bg-slate-700 dark:text-white' : 'text-slate-500 hover:text-slate-700 dark:text-slate-400'}`}
                        >
                            {filter === "all" ? "Todos" : filter === "archived" ? "Cerrados" : filter + "s"}
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

            {/* Data Table */}
            <div className="rounded-xl border border-slate-200 bg-white shadow-sm overflow-hidden dark:bg-slate-900 dark:border-slate-800">
                <div className="overflow-x-auto">
                    <table className="w-full text-left text-sm">
                        <thead className="bg-slate-50 border-b border-slate-200 dark:bg-slate-950 dark:border-slate-800">
                            <tr>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">Expediente</th>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">Cliente</th>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">Inicio Viaje</th>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">Estado</th>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400 text-right">Saldo</th>
                                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400 w-[50px]"></th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                            {filteredFiles.map((file) => (
                                <tr
                                    key={file.id}
                                    onClick={() => navigate(`/files/${file.id}`)}
                                    className="group hover:bg-slate-50 dark:hover:bg-slate-800/50 cursor-pointer transition-colors"
                                >
                                    <td className="px-6 py-4">
                                        <div className="flex items-center gap-3">
                                            <div className="h-9 w-9 rounded-full bg-indigo-50 text-indigo-600 flex items-center justify-center shrink-0 dark:bg-indigo-900/20 dark:text-indigo-400">
                                                <FolderOpen className="h-4 w-4" />
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
                                            {file.payer?.fullName || "Sin Asignar"}
                                        </div>
                                    </td>
                                    <td className="px-6 py-4 text-slate-600 dark:text-slate-400">
                                        {file.startDate ? new Date(file.startDate).toLocaleDateString() : "-"}
                                    </td>
                                    <td className="px-6 py-4">
                                        {getStatusBadge(file.status)}
                                    </td>
                                    <td className="px-6 py-4 text-right">
                                        <div className={`font-mono font-medium ${file.balance > 0 ? 'text-indigo-600 dark:text-indigo-400' : 'text-slate-600 dark:text-slate-400'}`}>
                                            ${file.balance?.toLocaleString()}
                                        </div>
                                    </td>
                                    <td className="px-6 py-4 text-right">
                                        <Button
                                            variant="ghost"
                                            size="icon"
                                            className="opacity-0 group-hover:opacity-100 transition-opacity"
                                            onClick={(e) => handleArchive(e, file.id)}
                                            title="Archivar Expediente"
                                        >
                                            <Archive className="h-4 w-4 text-slate-400 hover:text-slate-600" />
                                        </Button>
                                    </td>
                                </tr>
                            ))}
                            {filteredFiles.length === 0 && (
                                <tr>
                                    <td colSpan={6} className="px-6 py-12 text-center text-muted-foreground">
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

            {/* Create Modal */}
            <CreateFileModal
                isOpen={isCreateModalOpen}
                onClose={() => setIsCreateModalOpen(false)}
                onSuccess={handleCreateSuccess}
            />
        </div>
    );
}
