import React, { useState, useEffect } from "react";
import { X, Search, Loader2, User, Building, MapPin } from "lucide-react";
import { api } from "../api";
import { showError } from "../alerts";

export default function AfipSearchModal({ isOpen, onClose, onSelect, initialQuery = "" }) {
    const [query, setQuery] = useState(initialQuery);
    const [results, setResults] = useState([]);
    const [loading, setLoading] = useState(false);

    useEffect(() => {
        if (isOpen && initialQuery) {
            handleSearch(initialQuery);
        }
    }, [isOpen]);

    const handleSearch = async (searchQuery = query) => {
        if (!searchQuery.trim()) return;
        setLoading(true);
        try {
            const data = await api.get(`/fiscal/search?q=${encodeURIComponent(searchQuery)}`);
            setResults(data);
            if (data.length === 0) {
                showError("No se encontraron resultados en AFIP.");
            }
        } catch (error) {
            console.error(error);
            showError("Error al consultar AFIP.");
        } finally {
            setLoading(false);
        }
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-[100] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
            <div className="w-full max-w-2xl bg-white dark:bg-slate-900 rounded-xl shadow-2xl overflow-hidden border border-slate-200 dark:border-slate-800 animate-in zoom-in-95 duration-200">
                {/* Header */}
                <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center justify-between bg-slate-50/50 dark:bg-slate-900/50">
                    <div>
                        <h3 className="text-lg font-bold text-slate-900 dark:text-white flex items-center gap-2">
                            <Search className="h-5 w-5 text-indigo-500" />
                            Consultar AFIP (Padrón)
                        </h3>
                        <p className="text-sm text-slate-500">Busca por CUIT, DNI o Nombre y Apellido</p>
                    </div>
                    <button onClick={onClose} className="p-2 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-full transition-colors font-bold text-slate-400">
                        <X className="h-5 w-5" />
                    </button>
                </div>

                {/* Body */}
                <div className="p-6">
                    <form 
                        onSubmit={(e) => { e.preventDefault(); handleSearch(); }}
                        className="flex gap-2 mb-6"
                    >
                        <div className="relative flex-1">
                            <Search className="absolute left-3 top-3 h-4 w-4 text-slate-400" />
                            <input
                                type="text"
                                autoFocus
                                className="w-full pl-10 pr-4 py-2.5 rounded-lg border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-800 text-slate-900 dark:text-white focus:ring-2 focus:ring-indigo-500 outline-none transition-all shadow-sm"
                                placeholder="Escribe el nombre, CUIT o DNI..."
                                value={query}
                                onChange={(e) => setQuery(e.target.value)}
                            />
                        </div>
                        <button
                            type="submit"
                            disabled={loading || !query.trim()}
                            className="px-6 py-2.5 bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 text-white font-medium rounded-lg transition-all shadow-md flex items-center gap-2"
                        >
                            {loading ? <Loader2 className="h-4 w-4 animate-spin" /> : "Buscar"}
                        </button>
                    </form>

                    <div className="max-h-[400px] overflow-y-auto">
                        {loading ? (
                            <div className="py-12 text-center text-slate-500">
                                <Loader2 className="h-8 w-8 animate-spin mx-auto mb-3 text-indigo-500" />
                                <p>Consultando base de datos de AFIP...</p>
                            </div>
                        ) : results.length > 0 ? (
                            <div className="space-y-2">
                                {results.map((p, idx) => (
                                    <button
                                        key={idx}
                                        onClick={() => onSelect(p)}
                                        className="w-full text-left p-4 rounded-xl border border-slate-100 dark:border-slate-800 hover:border-indigo-200 dark:hover:border-indigo-900 hover:bg-indigo-50/30 dark:hover:bg-indigo-900/20 transition-all group flex items-start gap-4"
                                    >
                                        <div className="p-3 rounded-lg bg-slate-100 dark:bg-slate-800 group-hover:bg-indigo-100 dark:group-hover:bg-indigo-900/50 transition-colors">
                                            {p.tipoPersona === "JURIDICA" ? <Building className="h-5 w-5 text-indigo-600" /> : <User className="h-5 w-5 text-indigo-600" />}
                                        </div>
                                        <div className="flex-1">
                                            <div className="font-bold text-slate-900 dark:text-white group-hover:text-indigo-600 dark:group-hover:text-indigo-400 transition-colors">
                                                {p.razonSocial || `${p.apellido} ${p.nombre}`}
                                            </div>
                                            <div className="text-sm text-slate-500 dark:text-slate-400 flex items-center gap-4 mt-1">
                                                <span>CUIT: <span className="font-medium text-slate-700 dark:text-slate-200">{p.id}</span></span>
                                                <span className="flex items-center gap-1">
                                                    <span className="inline-block w-1 h-1 rounded-full bg-slate-300"></span>
                                                    {p.taxCondition}
                                                </span>
                                            </div>
                                        </div>
                                    </button>
                                ))}
                            </div>
                        ) : query && !loading ? (
                            <div className="py-12 text-center text-slate-500 bg-slate-50 dark:bg-slate-800/50 rounded-xl border border-dashed border-slate-200 dark:border-slate-700">
                                <p>No se encontraron resultados para "{query}"</p>
                            </div>
                        ) : (
                            <div className="py-12 text-center text-slate-400">
                                <p>Ingresa un nombre o documento para buscar</p>
                            </div>
                        )}
                    </div>
                </div>

                <div className="px-6 py-4 border-t border-slate-100 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-900/50 flex justify-end">
                    <button
                        onClick={onClose}
                        className="px-4 py-2 text-sm font-medium text-slate-600 dark:text-slate-400 hover:text-slate-800 dark:hover:text-slate-200 transition-colors"
                    >
                        Cerrar
                    </button>
                </div>
            </div>
        </div>
    );
}
