# MagnaTravel-Cloud

## Deploy con Docker en VPS

Este repo queda preparado para levantar todo con Docker Compose:

- `db`: PostgreSQL
- `api`: ASP.NET Core + migraciones EF automaticas al arrancar
- `web`: frontend Vite servido por Nginx
- `whatsapp-bot`: bot de WhatsApp con sesion persistida en volumen

### Variables

1. Crear `.env` a partir de [.env.example](./.env.example).
2. Completar como minimo:

- `POSTGRES_PASSWORD`
- `JWT_KEY`
- `WHATSAPP_WEBHOOK_SECRET`
- `WEB_ORIGIN`

`VITE_API_URL` puede quedar vacio. En ese caso el frontend usa mismo origen y Nginx proxya `/api`, `/hubs` y `/hangfire` al backend.

### Levantar o actualizar en VPS

```bash
git pull
docker compose up -d --build
```

### Que hace automaticamente

- El contenedor `api` espera a que PostgreSQL este saludable.
- El backend aplica las migraciones EF al arrancar, incluida `AddLeadReservaWhatsappTraceability`.
- El frontend espera al backend saludable antes de iniciar.
- El bot espera al backend saludable antes de iniciar.
- Los healthchecks quedan definidos para `db`, `api`, `web` y `whatsapp-bot`.

### Primer arranque del bot

El volumen `whatsapp_auth` persiste la sesion de WhatsApp. Si el bot no tiene sesion iniciada, hay que vincularlo una vez y luego la sesion queda guardada.

La autenticacion de `whatsapp-web.js` se guarda en `LocalAuth` usando `WHATSAPP_AUTH_PATH=/app/.wwebjs_auth`. En Docker Compose ese path esta montado en el volumen persistente `whatsapp_auth:/app/.wwebjs_auth`, por lo que no queda dentro del filesystem temporal del contenedor.

Al reiniciar Docker, el contenedor vuelve a montar `whatsapp_auth`, el bot limpia locks temporales y recupera la sesion existente desde `/app/.wwebjs_auth`. Solo deberia volver a pedir QR si el volumen fue eliminado, si WhatsApp invalido la sesion o si se ejecuto logout del bot.

### Comandos utiles

```bash
docker compose ps
docker compose logs -f api
docker compose logs -f whatsapp-bot
docker compose logs -f web
docker compose logs -f db
```
