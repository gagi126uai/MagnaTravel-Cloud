import React, { useState, useEffect, useCallback } from 'react';
import { api } from '../api';
import { FileText, Image as ImageIcon, Trash2, Download, UploadCloud, Eye, Loader2, File, Paperclip } from 'lucide-react';
import Swal from 'sweetalert2';
import { toast } from 'sonner';

// Componente para fila de lista con lógica de preview
const AttachmentRow = ({ file, onDelete, onDownload }) => {
    const [previewUrl, setPreviewUrl] = useState(null);
    const [loadingPreview, setLoadingPreview] = useState(false);

    // Normalizar propiedades
    const id = file.id || file.Id;
    const fileName = file.fileName || file.FileName;
    const fileSize = file.fileSize || file.FileSize;
    const contentType = file.contentType || file.ContentType;
    const uploadedBy = file.uploadedBy || file.UploadedBy;
    const uploadedAt = file.uploadedAt || file.UploadedAt;

    const isImage = contentType?.includes('image');
    const isPdf = contentType?.includes('pdf');

    // Cargar preview solo si es imagen y el usuario interactúa (opcional, aquí cargamos para thumbnail)
    useEffect(() => {
        let active = true;

        const loadThumbnail = async () => {
            if (!isImage) return;

            try {
                // No mostramos loading en la fila para no ensuciar la UI, cargamos silenciosamente
                const blob = await api.get(`/attachments/${id}/download`, { responseType: 'blob' });
                if (active) {
                    const url = URL.createObjectURL(blob);
                    setPreviewUrl(url);
                }
            } catch (err) {
                console.warn("Could not load thumbnail for", fileName);
            }
        };

        loadThumbnail();

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
            try {
                const blob = await api.get(`/attachments/${id}/download`, { responseType: 'blob' });
                const url = URL.createObjectURL(blob);
                window.open(url, '_blank');
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
        <div className="group flex items-center p-3 bg-white dark:bg-slate-800 border-b border-gray-100 dark:border-slate-700 hover:bg-gray-50 dark:hover:bg-slate-700/50 transition-colors">
            {/* Icon / Thumbnail */}
            <div
                className="flex-shrink-0 w-12 h-12 mr-4 flex items-center justify-center bg-gray-100 dark:bg-slate-900 rounded-lg overflow-hidden cursor-pointer border border-gray-200 dark:border-slate-600"
                onClick={handlePreviewClick}
            >
                {isImage && previewUrl ? (
                    <img src={previewUrl} alt={fileName} className="w-full h-full object-cover" />
                ) : isPdf ? (
                    <FileText className="w-6 h-6 text-red-500" />
                ) : (
                    <File className="w-6 h-6 text-gray-400" />
                )}
            </div>

            {/* File Info */}
            <div className="flex-1 min-w-0 mr-4 cursor-pointer" onClick={handlePreviewClick}>
                <p className="text-sm font-medium text-gray-900 dark:text-gray-100 truncate" title={fileName}>
                    {fileName}
                </p>
                <div className="flex items-center text-xs text-gray-500 dark:text-gray-400 mt-0.5 space-x-3">
                    <span>{formatFileSize(fileSize)}</span>
                    <span>•</span>
                    <span>{new Date(uploadedAt).toLocaleDateString()} {new Date(uploadedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>
                    <span>•</span>
                    <span>Subido por {uploadedBy}</span>
                </div>
            </div>

            {/* Actions */}
            <div className="flex items-center space-x-1 transition-opacity">
                {(isImage || isPdf) && (
                    <button
                        onClick={handlePreviewClick}
                        className="p-2 text-gray-500 hover:text-blue-600 hover:bg-blue-50 dark:hover:bg-blue-900/30 rounded-full transition-colors"
                        title="Ver"
                    >
                        <Eye className="w-4 h-4" />
                    </button>
                )}
                <button
                    onClick={(e) => { e.stopPropagation(); onDownload(id, fileName); }}
                    className="p-2 text-gray-500 hover:text-green-600 hover:bg-green-50 dark:hover:bg-green-900/30 rounded-full transition-colors"
                    title="Descargar"
                >
                    <Download className="w-4 h-4" />
                </button>
                <button
                    onClick={(e) => { e.stopPropagation(); onDelete(id, fileName); }}
                    className="p-2 text-gray-500 hover:text-red-600 hover:bg-red-50 dark:hover:bg-red-900/30 rounded-full transition-colors"
                    title="Eliminar"
                >
                    <Trash2 className="w-4 h-4" />
                </button>
            </div>
        </div>
    );
};

export const ReservaAttachmentsTab = ({ reservaId }) => {
    const [attachments, setAttachments] = useState([]);
    const [loading, setLoading] = useState(true);
    const [isDragging, setIsDragging] = useState(false);
    const [uploading, setUploading] = useState(false);

    const fetchAttachments = useCallback(async () => {
        try {
            setLoading(true);
            const data = await api.get(`/attachments/reserva/${reservaId}`);
            setAttachments(Array.isArray(data) ? data : []);
        } catch (error) {
            console.error("Error loading attachments:", error);
            toast.error("No se pudieron cargar los adjuntos.");
        } finally {
            setLoading(false);
        }
    }, [reservaId]);

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
        if (files.length > 0) await handleUpload(files[0]);
    };

    const handleFileInput = async (e) => {
        const files = e.target.files;
        if (files.length > 0) await handleUpload(files[0]);
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
            await api.post(`/attachments/upload/${reservaId}`, formData);
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
        const loadingToast = toast.loading("Preparando descarga...");
        try {
            console.log(`Attempting to download file ID: ${id}, Name: ${fileName}`);

            const blob = await api.get(`/attachments/${id}/download`, {
                responseType: 'blob',
            });

            if (!blob || blob.size === 0) {
                throw new Error("El archivo descargado está vacío o corrupto.");
            }

            console.log("Blob received:", blob);

            const url = window.URL.createObjectURL(blob);
            const link = document.createElement('a');
            link.href = url;
            link.setAttribute('download', fileName);
            document.body.appendChild(link);
            link.click();
            link.remove();

            // Aumentar timeout por si acaso el navegador tarda en procesar
            setTimeout(() => window.URL.revokeObjectURL(url), 1000);

            toast.dismiss(loadingToast);
            toast.success("Descarga iniciada");
        } catch (error) {
            console.error("Download error details:", error);
            toast.dismiss(loadingToast);
            toast.error(`Error al descargar: ${error.message || "Error desconocido"}`);
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

            {/* Attachments List */}
            <div className="bg-white dark:bg-slate-800 rounded-xl border border-gray-200 dark:border-slate-700 shadow-sm overflow-hidden">
                <div className="px-4 py-3 border-b border-gray-200 dark:border-slate-700 bg-gray-50 dark:bg-slate-900/50 flex items-center justify-between">
                    <h3 className="text-sm font-semibold text-gray-700 dark:text-gray-200 flex items-center">
                        <Paperclip className="w-4 h-4 mr-2" />
                        Documentos Adjuntos <span className="text-xs font-normal text-gray-500 ml-1">({attachments?.length || 0})</span>
                    </h3>
                </div>

                {loading ? (
                    <div className="p-8 flex justify-center">
                        <Loader2 className="w-8 h-8 animate-spin text-blue-500" />
                    </div>
                ) : (attachments?.length || 0) === 0 ? (
                    <div className="text-center py-12 text-gray-500 text-sm">
                        <FileText className="w-10 h-10 mx-auto text-gray-300 mb-3" />
                        <p>No hay documentos cargados aún.</p>
                    </div>
                ) : (
                    <div className="divide-y divide-gray-100 dark:divide-slate-700">
                        {attachments.map((file) => (
                            <AttachmentRow
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
