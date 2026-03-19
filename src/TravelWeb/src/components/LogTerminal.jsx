import { useEffect, useRef } from "react";
import { Terminal } from "lucide-react";

export default function LogTerminal({ logs, category }) {
  const scrollRef = useRef(null);

  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [logs]);

  return (
    <div className="flex flex-col h-[500px] bg-slate-950 rounded-xl border border-slate-800 overflow-hidden shadow-2xl">
      <div className="flex items-center gap-2 px-4 py-2 bg-slate-900 border-b border-slate-800">
        <Terminal className="h-4 w-4 text-slate-400" />
        <span className="text-xs font-mono text-slate-400 uppercase tracking-wider">{category} Console</span>
      </div>
      
      <div 
        ref={scrollRef}
        className="flex-1 p-4 font-mono text-[11px] overflow-y-auto custom-scrollbar space-y-1"
      >
        {logs.length === 0 ? (
          <div className="text-slate-700 italic">Esperando actividad...</div>
        ) : (
          logs.map((log, i) => (
            <div key={i} className="flex gap-2">
              <span className="text-slate-500 shrink-0 select-none">[{i+1}]</span>
              <span className={
                log.includes("ERR") || log.includes("❌") ? "text-rose-400" :
                log.includes("WRN") || log.includes("⚠️") ? "text-amber-400" :
                log.includes("INF") || log.includes("✅") ? "text-emerald-400" :
                "text-slate-300"
              }>
                {log.replace(/^\[(API|BOT|DB|WEB)\]\s*/, "")}
              </span>
            </div>
          ))
        )}
      </div>
    </div>
  );
}
