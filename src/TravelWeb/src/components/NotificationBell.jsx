import React, { useState, useEffect, useRef } from "react";
import { Bell, Checkbox, CheckCircle2 } from "lucide-react";
import * as signalR from "@microsoft/signalr";
import { api } from "../api";
import { Link } from "react-router-dom";
import { formatDistanceToNow } from "date-fns";
import { es } from "date-fns/locale";

export default function NotificationBell() {
    const [notifications, setNotifications] = useState([]);
    const [unreadCount, setUnreadCount] = useState(0);
    const [isOpen, setIsOpen] = useState(false);
    const containerRef = useRef(null);
    const connectionRef = useRef(null);

    // Initial load
    useEffect(() => {
        const fetchNotifications = async () => {
            try {
                // API exposes usually GET /api/notifications
                const data = await api.get("/notifications?unreadOnly=true");
                setNotifications(data || []);
                setUnreadCount(data?.length || 0);
            } catch (error) {
                console.error("Error fetching notifications:", error);
            }
        };

        fetchNotifications();
    }, []);

    // SignalR Setup
    useEffect(() => {
        const url = import.meta.env.VITE_API_URL 
            ? `${import.meta.env.VITE_API_URL}/hubs/notifications` 
            : "/hubs/notifications";

        const newConnection = new signalR.HubConnectionBuilder()
            .withUrl(url)
            .withAutomaticReconnect()
            .build();

        newConnection.on("ReceiveNotification", (notification) => {
            setNotifications((prev) => [notification, ...prev]);
            setUnreadCount((prev) => prev + 1);
        });

        newConnection.start().catch((err) => console.error("SignalR Connection Error: ", err));
        connectionRef.current = newConnection;

        return () => {
            if (connectionRef.current) {
                connectionRef.current.stop();
            }
        };
    }, []);

    // Click outside to close
    useEffect(() => {
        const handleClickOutside = (event) => {
            if (containerRef.current && !containerRef.current.contains(event.target)) {
                setIsOpen(false);
            }
        };
        document.addEventListener("mousedown", handleClickOutside);
        return () => document.removeEventListener("mousedown", handleClickOutside);
    }, []);

    const markAsRead = async (id, e) => {
        if (e) {
            e.preventDefault();
            e.stopPropagation();
        }
        try {
            await api.post(`/notifications/${id}/read`);
            setNotifications((prev) => prev.filter((n) => n.id !== id));
            setUnreadCount((prev) => Math.max(0, prev - 1));
        } catch (error) {
            console.error("Error marking notification as read:", error);
        }
    };

    const markAllAsRead = async () => {
        try {
            // Assuming an endpoint exists or we do it locally iterating
            // Let's iterate locally for now if batch endpoint is missing
            await Promise.all(notifications.map(n => api.post(`/notifications/${n.id}/read`)));
            setNotifications([]);
            setUnreadCount(0);
            setIsOpen(false);
        } catch (error) {
            console.error("Error marking all as read:", error);
        }
    };

    return (
        <div className="relative" ref={containerRef}>
            <button
                onClick={() => setIsOpen(!isOpen)}
                className="relative p-2 text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200 transition-colors focus:outline-none"
                title="Notificaciones"
            >
                <Bell className="h-5 w-5" />
                {unreadCount > 0 && (
                    <span className="absolute top-1 right-1 flex h-4 w-4 items-center justify-center rounded-full bg-rose-500 text-[10px] font-bold text-white ring-2 ring-white dark:ring-slate-900 animate-pulse">
                        {unreadCount > 9 ? "9+" : unreadCount}
                    </span>
                )}
            </button>

            {isOpen && (
                <div className="absolute right-0 mt-2 w-80 md:w-96 rounded-lg bg-white dark:bg-slate-900 shadow-xl ring-1 ring-slate-200 dark:ring-slate-800 z-50 animate-in fade-in slide-in-from-top-2">
                    <div className="flex items-center justify-between border-b border-slate-100 dark:border-slate-800 px-4 py-3">
                        <h3 className="font-semibold text-slate-800 dark:text-slate-200">Notificaciones</h3>
                        {unreadCount > 0 && (
                            <button
                                onClick={markAllAsRead}
                                className="text-xs font-medium text-indigo-600 dark:text-indigo-400 hover:underline"
                            >
                                Marcar todas leídas
                            </button>
                        )}
                    </div>
                    
                    <div className="max-h-[60vh] overflow-y-auto">
                        {notifications.length === 0 ? (
                            <div className="px-4 py-8 text-center text-slate-500 dark:text-slate-400">
                                <Bell className="mx-auto h-8 w-8 opacity-20 mb-2" />
                                <p className="text-sm">No tienes nuevas notificaciones</p>
                            </div>
                        ) : (
                            <div className="divide-y divide-slate-100 dark:divide-slate-800/50">
                                {notifications.map((notification) => (
                                    <div 
                                        key={notification.id} 
                                        className="relative flex items-start gap-3 p-4 hover:bg-slate-50 dark:hover:bg-slate-800/50 transition-colors group cursor-default"
                                    >
                                        <div className="mt-1">
                                            <div className={`h-2 w-2 rounded-full ${notification.type === 'Error' ? 'bg-red-500' : notification.type === 'Success' ? 'bg-emerald-500' : 'bg-indigo-500'}`}></div>
                                        </div>
                                        <div className="flex-1 space-y-1">
                                            <p className="text-sm text-slate-700 dark:text-slate-300">
                                                {notification.message}
                                            </p>
                                            <p className="text-xs text-slate-400">
                                                {formatDistanceToNow(new Date(notification.createdAt), { addSuffix: true, locale: es })}
                                            </p>
                                        </div>
                                        <button 
                                            onClick={(e) => markAsRead(notification.id, e)}
                                            className="opacity-0 group-hover:opacity-100 p-1 text-slate-400 hover:text-indigo-600 transition-opacity"
                                            title="Marcar como leída"
                                        >
                                            <CheckCircle2 className="h-4 w-4" />
                                        </button>
                                    </div>
                                ))}
                            </div>
                        )}
                    </div>
                    
                    <div className="border-t border-slate-100 dark:border-slate-800 px-4 py-2">
                        <Link 
                            to="/notifications" 
                            onClick={() => setIsOpen(false)}
                            className="block text-center text-xs font-medium text-slate-500 hover:text-slate-800 dark:hover:text-slate-300 py-1"
                        >
                            Ver todas las notificaciones
                        </Link>
                    </div>
                </div>
            )}
        </div>
    );
}
