import React, { useState, useEffect, useCallback } from 'react';
import { api } from '../api';
import { FileText, Image as ImageIcon, Trash2, Download, UploadCloud, X, Loader2 } from 'lucide-react';
import Swal from 'sweetalert2';
import { toast } from 'sonner';

export const FileAttachmentsTab = ({ travelFileId }) => {
    const [attachments, setAttachments] = useState([]);
    const [loading, setLoading] = useState(true);
    const [isDragging, setIsDragging] = useState(false);
    const [uploading, setUploading] = useState(false);

    const fetchAttachments = useCallback(async () => {
        try {
            setLoading(true);
            const response = await api.get(`/attachments/file/${travelFileId}`);
            console.log("Attachments Data:", response.data); // Debugging
            setAttachments(Array.isArray(response.data) ? response.data : []);
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

        // Validate size (e.g. 25MB)
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
                setAttachments(prev => prev.filter(a => a.id !== id));
            } catch (error) {
                console.error("Error deleting:", error);
                toast.error("No se pudo eliminar el archivo.");
            }
        }
    };

    const handleDownload = async (id, fileName) => {
        try {
            const response = await api.get(`/attachments/${id}/download`, {
                responseType: 'blob',
            });

            const url = window.URL.createObjectURL(new Blob([response.data]));
            const link = document.createElement('a');
            link.href = url;
            link.setAttribute('download', fileName);
            document.body.appendChild(link);
            link.click();
            link.remove();
        } catch (error) {
            console.error("Download error:", error);
            toast.error("Error al descargar el archivo.");
        }
    };

    const formatFileSize = (bytes) => {
        if (bytes === 0) return '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
    };

    const getFileIcon = (contentType) => {
        if (contentType?.includes('image')) return <ImageIcon className="w-8 h-8 text-blue-500" />;
        if (contentType?.includes('pdf')) return <FileText className="w-8 h-8 text-red-500" />;
        return <FileText className="w-8 h-8 text-gray-500" />;
    };

    return (
        <div className="space-y-6">
            {/* Upload Zone */}
            <div
                className={`border-2 border-dashed rounded-lg p-8 text-center transition-colors cursor-pointer
          ${isDragging
                        ? 'border-blue-500 bg-blue-50'
                        : 'border-gray-300 hover:border-gray-400 bg-gray-50'
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
                />
                <div className="flex flex-col items-center justify-center space-y-2">
                    {uploading ? (
                        <Loader2 className="w-10 h-10 text-blue-500 animate-spin" />
                    ) : (
                        <UploadCloud className={`w-10 h-10 ${isDragging ? 'text-blue-500' : 'text-gray-400'}`} />
                    )}
                    <div className="text-sm font-medium text-gray-900">
                        {uploading ? 'Subiendo...' : 'Haz clic o arrastra un archivo aquí'}
                    </div>
                    <p className="text-xs text-gray-500">PDF, Imágenes, Word (Máx 25MB)</p>
                </div>
            </div>

            {/* Attachments List */}
            <div>
                <h3 className="text-sm font-semibold text-gray-700 mb-3">Documentos ({attachments?.length || 0})</h3>

                {loading ? (
                    <div className="flex justify-center p-4">
                        <Loader2 className="w-6 h-6 animate-spin text-gray-400" />
                    </div>
                ) : (attachments?.length || 0) === 0 ? (
                    <div className="text-center py-8 text-gray-500 text-sm bg-white rounded-lg border border-gray-100">
                        No hay documentos cargados aún.
                    </div>
                ) : (
                    <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
                        {attachments.map((file) => {
                            // Handle both camelCase and PascalCase
                            const id = file.id || file.Id;
                            const fileName = file.fileName || file.FileName;
                            const fileSize = file.fileSize || file.FileSize;
                            const contentType = file.contentType || file.ContentType;
                            const uploadedBy = file.uploadedBy || file.UploadedBy;
                            const uploadedAt = file.uploadedAt || file.UploadedAt;

                            return (
                                <div key={id} className="bg-white p-4 rounded-lg border border-gray-200 shadow-sm hover:shadow-md transition-shadow flex items-start space-x-3 group">
                                    <div className="flex-shrink-0">
                                        {getFileIcon(contentType)}
                                    </div>
                                    <div className="flex-1 min-w-0">
                                        <p className="text-sm font-medium text-gray-900 truncate" title={fileName}>
                                            {fileName}
                                        </p>
                                        <p className="text-xs text-gray-500 flex items-center mt-1">
                                            {formatFileSize(fileSize)} • {new Date(uploadedAt).toLocaleDateString()}
                                        </p>
                                        <p className="text-xs text-gray-400 mt-0.5">
                                            Por: {uploadedBy}
                                        </p>
                                    </div>
                                    <div className="flex space-x-1 opacity-0 group-hover:opacity-100 transition-opacity">
                                        <button
                                            onClick={(e) => { e.stopPropagation(); handleDownload(id, fileName); }}
                                            className="p-1.5 text-gray-500 hover:text-blue-600 hover:bg-blue-50 rounded"
                                            title="Descargar"
                                        >
                                            <Download className="w-4 h-4" />
                                        </button>
                                        <button
                                            onClick={(e) => { e.stopPropagation(); handleDelete(id, fileName); }}
                                            className="p-1.5 text-gray-500 hover:text-red-600 hover:bg-red-50 rounded"
                                            title="Eliminar"
                                        >
                                            <Trash2 className="w-4 h-4" />
                                        </button>
                                    </div>
                                </div>
                            )
                        })}
                    </div>
                )}
            </div>
        </div>
    );
};
