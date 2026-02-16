import React, { useState, useEffect, useCallback } from 'react';
import { api } from '../api';
import { FileText, Image as ImageIcon, Trash2, Download, UploadCloud, Eye, X, Loader2, File } from 'lucide-react';
import Swal from 'sweetalert2';
import { toast } from 'sonner';

// Componente para tarjeta individual con lógica de preview
const AttachmentCard = ({ file, onDelete, onDownload }) => {
    const [previewUrl, setPreviewUrl] = useState(null);
    const [loadingPreview, setLoadingPreview] = useState(false);

    // Normalizar propiedades (PascalCase vs camelCase)
    const id = file.id || file.Id;
    const fileName = file.fileName || file.FileName;
    const fileSize = file.fileSize || file.FileSize;
    const contentType = file.contentType || file.ContentType;
    const uploadedBy = file.uploadedBy || file.UploadedBy;
    const uploadedAt = file.uploadedAt || file.UploadedAt;

    const isImage = contentType?.includes('image');
    const isPdf = contentType?.includes('pdf');

    useEffect(() => {
        let active = true;

        const loadPreview = async () => {
            if (!isImage) return;

            try {
                setLoadingPreview(true);
                const blob = await api.get(`/attachments/${id}/download`, { responseType: 'blob' });
                if (active) {
                    const url = URL.createObjectURL(blob);
                    setPreviewUrl(url);
                }
            } catch (err) {
                console.error("Error loading preview", err);
            } finally {
                if (active) setLoadingPreview(false);
            }
        };

        loadPreview();

        return () => {
            active = false;
            if (previewUrl) URL.revokeObjectURL(previewUrl);
        };
    }, [id, isImage]);

    const handlePreviewClick = async (e) => {
        e.stopPropagation();

        if (isImage && previewUrl) {
            Swal.fire({
                imageUrl: previewUrl,
                imageAlt: fileName,
                showConfirmButton: false,
                showCloseButton: true,
                customClass: {
                    popup: 'w-auto max-w-[90vw] max-h-[90vh] p-0 overflow-hidden bg-transparent shadow-none',
                    image: 'max-h-[85vh] object-contain rounded-lg shadow-2xl bg-white'
                },
                backdrop: `rgba(0,0,0,0.8)`
            });
        } else if (isPdf) {
            // Abrir PDF en nueva pestaña
            try {
                const blob = await api.get(`/attachments/${id}/download`, { responseType: 'blob' });
                const url = URL.createObjectURL(blob);
                window.open(url, '_blank');
                // Nota: Idealmente revocar URL después, pero en nueva pestaña es complejo.
                // El navegador maneja la limpieza eventualmente.
            } catch (error) {
                toast.error("Error al abrir PDF");
            }
        }
    };

    const formatFileSize = (bytes) => {
        if (bytes === 0) return '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
    };

    return (
        <div
            className="group relative bg-white dark:bg-slate-800 rounded-xl border border-gray-200 dark:border-slate-700 shadow-sm hover:shadow-md transition-all duration-200 overflow-hidden flex flex-col"
        >
            {/* Thumbnail Area */}
            <div
                className="aspect-square bg-gray-100 dark:bg-slate-900 flex items-center justify-center cursor-pointer overflow-hidden relative"
                onClick={handlePreviewClick}
            >
                {isImage ? (
                    loadingPreview ? (
                        <Loader2 className="w-8 h-8 text-gray-400 animate-spin" />
                    ) : (
                        <img
                            src={previewUrl}
                            alt={fileName}
                            className="w-full h-full object-cover transition-transform duration-300 group-hover:scale-105"
                        />
                    )
                ) : isPdf ? (
                    <FileText className="w-16 h-16 text-red-500" />
                ) : (
                    <File className="w-16 h-16 text-gray-400" />
                )}

                {/* Hover Overlay */}
                <div className="absolute inset-0 bg-black/40 opacity-0 group-hover:opacity-100 transition-opacity flex items-center justify-center gap-2">
                    <button
                        onClick={handlePreviewClick}
                        className="p-2 bg-white/20 hover:bg-white/40 text-white rounded-full backdrop-blur-sm transition-colors"
                        title="Ver"
                    >
                        <Eye className="w-5 h-5" />
                    </button>
                    <button
                        onClick={(e) => { e.stopPropagation(); onDownload(id, fileName); }}
                        className="p-2 bg-white/20 hover:bg-white/40 text-white rounded-full backdrop-blur-sm transition-colors"
                        title="Descargar"
                    >
                        <Download className="w-5 h-5" />
                    </button>
                    <button
                        onClick={(e) => { e.stopPropagation(); onDelete(id, fileName); }}
                        className="p-2 bg-red-500/80 hover:bg-red-600 text-white rounded-full backdrop-blur-sm transition-colors"
                        title="Eliminar"
                    >
                        <Trash2 className="w-5 h-5" />
                    </button>
                </div>
            </div>

            {/* Info Area */}
            <div className="p-3">
                <p className="text-sm font-medium text-gray-900 dark:text-gray-100 truncate" title={fileName}>
                    {fileName}
                </p>
                <div className="flex justify-between items-center mt-1">
                    <span className="text-xs text-gray-500 dark:text-gray-400">{formatFileSize(fileSize)}</span>
                    <span className="text-xs text-gray-400 dark:text-gray-500">{new Date(uploadedAt).toLocaleDateString()}</span>
                </div>
            </div>
        </div>
    );
};

export const FileAttachmentsTab = ({ travelFileId }) => {
    const [attachments, setAttachments] = useState([]);
    const [loading, setLoading] = useState(true);
    const [isDragging, setIsDragging] = useState(false);
    const [uploading, setUploading] = useState(false);

    const fetchAttachments = useCallback(async () => {
        try {
            setLoading(true);
            const data = await api.get(`/attachments/file/${travelFileId}`);
            console.log("Attachments Data:", data);
            setAttachments(Array.isArray(data) ? data : []);
        } catch (error) {
            console.error("Error loading attachments:", error);
            toast.error("No se pudieron cargar los adjuntos.");
        } finally {
            setLoading(false);
        }
    }, [travelFileId]);

    useEffect(() => {
        fetchAttachments();
    }, [fetchAttachments]);

    const handleDragOver = (e) => {
        e.preventDefault();
        setIsDragging(true);
    };

    const handleDragLeave = (e) => {
        e.preventDefault();
        setIsDragging(false);
    };

    const handleDrop = async (e) => {
        e.preventDefault();
        setIsDragging(false);

        const files = e.dataTransfer.files;
        if (files.length > 0) {
            await handleUpload(files[0]);
        }
    };

    const handleFileInput = async (e) => {
        const files = e.target.files;
        if (files.length > 0) {
            await handleUpload(files[0]);
        }
    };

    const handleUpload = async (file) => {
        if (!file) return;

        if (file.size > 25 * 1024 * 1024) {
            toast.error("El archivo es demasiado grande (Máx 25MB).");
            return;
        }

        const formData = new FormData();
        formData.append('file', file);

        try {
            setUploading(true);
            await api.post(`/attachments/upload/${travelFileId}`, formData);
            toast.success("Archivo subido correctamente.");
            fetchAttachments();
        } catch (error) {
            console.error("Error uploading:", error);
            toast.error("Error al subir el archivo.");
        } finally {
            setUploading(false);
        }
    };

    const handleDelete = async (id, fileName) => {
        const result = await Swal.fire({
            title: '¿Eliminar archivo?',
            text: `Se eliminará "${fileName}" permanentemente.`,
            icon: 'warning',
            showCancelButton: true,
            confirmButtonText: 'Sí, eliminar',
            cancelButtonText: 'Cancelar',
            confirmButtonColor: '#ef4444',
        });

        if (result.isConfirmed) {
            try {
                await api.delete(`/attachments/${id}`);
                toast.success("Archivo eliminado.");
                setAttachments(prev => prev.filter(a => (a.id || a.Id) !== id));
            } catch (error) {
                console.error("Error deleting:", error);
                toast.error("No se pudo eliminar el archivo.");
            }
        }
    };

    const handleDownload = async (id, fileName) => {
        try {
            const blob = await api.get(`/attachments/${id}/download`, {
                responseType: 'blob',
            });

            const url = window.URL.createObjectURL(blob);
            const link = document.createElement('a');
            link.href = url;
            link.setAttribute('download', fileName);
            document.body.appendChild(link);
            link.click();
            link.remove();
            setTimeout(() => window.URL.revokeObjectURL(url), 100);
        } catch (error) {
            console.error("Download error:", error);
            toast.error("Error al descargar el archivo.");
        }
    };

    return (
        <div className="space-y-6">
            {/* Upload Zone */}
            <div
                className={`border-2 border-dashed rounded-xl p-8 text-center transition-all cursor-pointer relative overflow-hidden
          ${isDragging
                        ? 'border-blue-500 bg-blue-50 dark:bg-blue-900/20'
                        : 'border-gray-300 dark:border-slate-700 hover:border-gray-400 bg-gray-50 dark:bg-slate-800/50'
                    }`}
                onDragOver={handleDragOver}
                onDragLeave={handleDragLeave}
                onDrop={handleDrop}
                onClick={() => document.getElementById('fileInput').click()}
            >
                <input
                    id="fileInput"
                    type="file"
                    className="hidden"
                    onChange={handleFileInput}
                    accept="image/*,application/pdf,.doc,.docx,.xls,.xlsx"
                />
                <div className="flex flex-col items-center justify-center space-y-3 relative z-10">
                    {uploading ? (
                        <Loader2 className="w-12 h-12 text-blue-500 animate-spin" />
                    ) : (
                        <div className="p-3 bg-white dark:bg-slate-800 rounded-full shadow-sm">
                            <UploadCloud className={`w-8 h-8 ${isDragging ? 'text-blue-500' : 'text-gray-400'}`} />
                        </div>
                    )}
                    <div>
                        <div className="text-base font-medium text-gray-900 dark:text-gray-100">
                            {uploading ? 'Subiendo archivo...' : 'Haz clic o arrastra archivos aquí'}
                        </div>
                        <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                            Soporta imágenes, PDF y documentos Office (Máx 25MB)
                        </p>
                    </div>
                </div>
            </div>

            {/* Attachments Grid */}
            <div>
                <div className="flex items-center justify-between mb-4">
                    <h3 className="text-sm font-semibold text-gray-700 dark:text-gray-200">
                        Documentos Adjuntos <span className="text-xs font-normal text-gray-500 ml-1">({attachments?.length || 0})</span>
                    </h3>
                </div>

                {loading ? (
                    <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-5 gap-4">
                        {[1, 2, 3, 4].map((n) => (
                            <div key={n} className="aspect-square bg-gray-100 dark:bg-slate-800 rounded-xl animate-pulse" />
                        ))}
                    </div>
                ) : (attachments?.length || 0) === 0 ? (
                    <div className="text-center py-12 text-gray-500 text-sm bg-white dark:bg-slate-800 rounded-xl border border-gray-200 dark:border-slate-700 border-dashed">
                        <FileText className="w-10 h-10 mx-auto text-gray-300 mb-3" />
                        <p>No hay documentos cargados aún.</p>
                    </div>
                ) : (
                    <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-4">
                        {attachments.map((file) => (
                            <AttachmentCard
                                key={file.id || file.Id}
                                file={file}
                                onDelete={handleDelete}
                                onDownload={handleDownload}
                            />
                        ))}
                    </div>
                )}
            </div>
        </div>
    );
};
