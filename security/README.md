# Pruebas de seguridad de API

Importar `MagnaTravel-Security.postman_collection.json` en Postman. La coleccion no guarda usuarios, contrasenas, tokens ni cookies y no incluye operaciones destructivas.

1. Definir `baseUrl` contra un entorno de prueba.
2. Ejecutar la carpeta **Sin sesion** con el cookie jar vacio.
3. Iniciar sesion manualmente con un rol de permisos limitados.
4. Completar `ownReservaUuid` con una reserva de ese usuario y `foreignReservaUuid` con una reserva de otro usuario.
5. Para la unica escritura simulada, copiar el valor de la cookie CSRF a `csrfToken`. El payload vacio no crea datos, pero debe quedar bloqueado por permiso antes de validarlo.

Los UUID no se consideran secretos. El criterio de aprobacion es que cambiar un UUID nunca permita leer u operar un objeto ajeno: debe responder 403 o 404.
