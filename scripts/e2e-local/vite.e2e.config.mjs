// Config E2E local: igual que el del proyecto + proxy /api -> API .NET (misma
// topologia same-origin que produccion detras de nginx). No toca el repo.
import { defineConfig } from "file:///D:/Documentos/MagnaTravel/MagnaTravel-Cloud/src/TravelWeb/node_modules/vite/dist/node/index.js";
import react from "file:///D:/Documentos/MagnaTravel/MagnaTravel-Cloud/src/TravelWeb/node_modules/@vitejs/plugin-react/dist/index.js";

export default defineConfig({
  root: "D:/Documentos/MagnaTravel/MagnaTravel-Cloud/src/TravelWeb",
  plugins: [react()],
  server: {
    host: true,
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": {
        // Puerto configurable: Windows a veces reserva rangos (error 10013)
        // tras un reinicio; si 60663 cae en un rango excluido, exportar
        // E2E_API_PORT con uno libre (netsh interface ipv4 show excludedportrange).
        target: "http://localhost:" + (process.env.E2E_API_PORT || "60663"),
        changeOrigin: true,
      },
    },
  },
  build: { target: "es2015" },
});
