import { useState, useEffect, useRef } from "react";
import { AlertCircle, X, AlertTriangle } from "lucide-react";
import * as signalR from "@microsoft/signalr";
import { api } from "../api";

export default function UrgentBannerStack() {
  const [banners, setBanners] = useState([]);
  const connectionRef = useRef(null);

  // Load urgent notifications on mount
  useEffect(() => {
    const fetchUrgent = async () => {
      try {
        const data = await api.get("/notifications/urgent");
        setBanners(Array.isArray(data) ? data : []);
      } catch {
        // Silently fail if endpoint not available yet
      }
    };

    fetchUrgent();
  }, []);

  // SignalR: listen for new urgent banners
  useEffect(() => {
    const url = import.meta.env.VITE_API_URL
      ? `${import.meta.env.VITE_API_URL}/hubs/notifications`
      : "/hubs/notifications";

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(url)
      .withAutomaticReconnect()
      .build();

    connection.on("ReceiveUrgentBanner", (notification) => {
      setBanners((prev) => {
        // Avoid duplicates
        if (prev.some((b) => b.id === notification.id)) return prev;
        return [notification, ...prev];
      });
    });

    connection.start().catch((err) => console.warn("UrgentBanner SignalR error:", err));
    connectionRef.current = connection;

    return () => {
      if (connectionRef.current) {
        connectionRef.current.stop();
      }
    };
  }, []);

  const dismiss = async (id) => {
    try {
      await api.post(`/notifications/${id}/dismiss`);
      setBanners((prev) => prev.filter((b) => b.id !== id));
    } catch (error) {
      console.error("Error dismissing banner:", error);
    }
  };

  if (banners.length === 0) return null;

  const getGradient = (type) => {
    switch (type) {
      case "Error":
        return "from-rose-600 via-red-500 to-rose-600 border-rose-400/20";
      case "Warning":
        return "from-amber-500 via-orange-500 to-amber-500 border-amber-400/20";
      default:
        return "from-amber-500 via-orange-500 to-amber-500 border-amber-400/20";
    }
  };

  const getIcon = (type) => {
    return type === "Error" ? AlertTriangle : AlertCircle;
  };

  return (
    <div className="flex flex-col">
      {banners.map((banner) => {
        const Icon = getIcon(banner.type);
        return (
          <div
            key={banner.id}
            className={`bg-gradient-to-r ${getGradient(banner.type)} text-white text-[9px] md:text-[10px] py-1.5 px-4 text-center font-black uppercase tracking-[0.2em] shadow-lg z-[59] flex items-center justify-center gap-3 border-b animate-in slide-in-from-top-1 duration-300`}
          >
            <Icon className="h-3.5 w-3.5 animate-pulse flex-shrink-0" />
            <span className="drop-shadow-sm flex-1 text-center">{banner.message}</span>
            <button
              onClick={() => dismiss(banner.id)}
              className="p-0.5 rounded hover:bg-white/20 transition-colors flex-shrink-0"
              title="Descartar"
            >
              <X className="h-3 w-3" />
            </button>
          </div>
        );
      })}
    </div>
  );
}
