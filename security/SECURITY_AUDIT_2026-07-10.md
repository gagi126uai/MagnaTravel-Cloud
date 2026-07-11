# Auditoria de seguridad MagnaTravel — 2026-07-10

## Alcance

Revision estatica y pruebas HTTP del API, autenticacion/sesiones, autorizacion por permiso y ownership, UUID/IDOR, webhooks, archivos, frontend, bot de WhatsApp, dependencias NuGet/npm y Docker Compose. No se realizo pentest contra produccion ni se usaron datos reales.

## Resultado ejecutivo

Se encontraron y corrigieron accesos que dependian solo de `[Authorize]`: un usuario autenticado podia invocar directamente con Postman endpoints que la UI podia ocultar. Los modulos afectados eran mensajes/WhatsApp, paises/destinos, cotizaciones y tarifario.

Tambien se corrigio BOLA/IDOR en mensajeria operativa: cambiar el UUID ya no permite a un vendedor listar destinatarios, enviar mensajes ni abrir conversaciones de una reserva ajena. Admin y roles con `reservas.view_all` conservan el acceso global.

## UUID en URL

Un UUID visible en una URL no es una vulnerabilidad por si mismo ni se considera un secreto. OWASP incluye UUID/GUID entre los identificadores normales y exige validar autorizacion sobre el objeto en cada endpoint. Microsoft Dataverse/Dynamics y SAP OData documentan GUID en URLs de recursos.

La vulnerabilidad real es BOLA/IDOR: que un usuario reemplace el UUID por otro y obtenga informacion o ejecute una accion sobre un objeto ajeno. Los UUID aleatorios reducen enumeracion, pero nunca reemplazan la autorizacion.

Fuentes:

- https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/
- https://owasp.org/www-project-web-security-testing-guide/latest/4-Web_Application_Security_Testing/12-API_Testing/02-API_Broken_Object_Level_Authorization
- https://learn.microsoft.com/en-us/power-apps/developer/data-platform/webapi/retrieve-entity-using-web-api
- https://help.sap.com/doc/83adce4bf4514737884c9d420cafe7ee/LBN/en-US/SAP_LBN_GTT_Interface_Ref_Guide.pdf

## Correcciones aplicadas

- Permisos server-side en mensajes, conversaciones WhatsApp, paises, destinos, cotizaciones y tarifario.
- Ownership en destinatarios, envio de texto/voucher y conversaciones operativas por reserva.
- Pruebas HTTP de regresion que simulan llamadas directas tipo Postman con rol sin permisos.
- Coleccion Postman no destructiva en `security/MagnaTravel-Security.postman_collection.json`.
- AutoMapper actualizado a 15.1.1 y Newtonsoft.Json a 13.0.3; se elimino la supresion `NU1903`.
- Dependencias npm corregidas: auditoria en cero para frontend y bot.
- Consolas RabbitMQ y MinIO limitadas a `127.0.0.1` en el host.
- Health readiness anonimizado: no devuelve endpoint de storage, nombres de migraciones ni errores internos.
- Limites de tamano y validacion de longitud para webhooks y formulario publico de leads.
- El bot autentica el secret antes de parsear cuerpos grandes y usa comparacion de tiempo constante.
- Telefonos y contenido de mensajes removidos/enmascarados en logs.
- Rate limiter de ASP.NET activado en el pipeline, junto con las mejoras previas de sesion/refresh y XSS.

## Verificacion ejecutada

- `dotnet build src/MagnaTravel.sln --no-restore`: correcto.
- Pruebas dirigidas de seguridad/autenticacion/rate limiting: 15/15 correctas.
- Frontend: 1922/1922 pruebas correctas.
- Build Vite de produccion: correcto.
- `npm audit`: 0 vulnerabilidades en frontend y bot.
- NuGet: 0 vulnerabilidades conocidas en proyectos de produccion.
- Docker Compose: configuracion valida.
- Sintaxis del bot Node: valida.
- Suite .NET completa: los tests sin contenedores avanzaron; los tests Testcontainers fallaron sin Docker en sandbox. La repeticion con acceso elevado quedo bloqueada sin producir salida y se detuvo tras mas de 12 minutos. No se interpreta como fallo del producto, pero debe ejecutarse en CI/host con Docker operativo antes del deploy.

## Riesgos pendientes y acciones de despliegue

### Alta prioridad operativa

1. Confirmar TLS real de punta a punta. El compose publica `3000:80`; debe existir un reverse proxy externo HTTPS, redireccion HTTP→HTTPS y HSTS efectivo. No publicar el puerto directamente a Internet.
2. Cifrar backups, restringir permisos del directorio `backups/postgres`, mantener copia off-site y probar restauracion periodica.
3. Ejecutar la suite completa con Docker operativo antes del deploy.

### Prioridad media

1. Retirar gradualmente IDs numericos legacy de rutas publicas. Hoy varios resolvers aun aceptan entero por compatibilidad. Mantener UUID; eliminar el fallback numerico cuando los clientes antiguos hayan migrado.
2. Revisar la confianza de `X-Forwarded-For`. ASP.NET debe confiar solo en el proxy real; una configuracion incorrecta puede agrupar clientes bajo la IP de Nginx o aceptar spoofing. Validar en el entorno final con IPs/subredes conocidas.
3. Ejecutar contenedores API/bot como usuario no root despues de preparar permisos de volumen. No se cambio automaticamente para evitar romper uploads, logs y la sesion de Chromium.
4. Evolucionar el webhook interno de secret estatico a HMAC con timestamp/nonce o mTLS para reducir replay si el secret se filtra.
5. El unico advisory NuGet restante pertenece a `SQLitePCLRaw.lib.e_sqlite3` de la suite de tests, no al runtime productivo. GitHub aun no publica version corregida del paquete para ese advisory; monitorear y actualizar al existir parche.

## Criterio de salida antes de publicar

- CI completo verde, incluida integracion PostgreSQL/Testcontainers.
- `npm audit` y `dotnet list package --vulnerable --include-transitive` sin vulnerabilidades de produccion.
- Coleccion Postman verde usando una reserva propia y otra ajena.
- HTTPS verificado desde Internet y consolas 15672/9001 inaccesibles remotamente.
- Secrets rotados y distintos por entorno; ningun valor real en repositorio/logs.
- Backup restaurado con exito en un entorno aislado.
