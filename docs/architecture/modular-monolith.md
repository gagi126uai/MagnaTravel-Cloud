# Monolito Modular Hexagonal

## Objetivo

`TravelApi` se mantiene como backend único en esta etapa. La prioridad es separar responsabilidades por contexto y reforzar la arquitectura hexagonal antes de evaluar cualquier extracción a microservicios.

## Módulos Internos

- `Usuarios/Auth`
  Maneja autenticación, autorización, roles, permisos y sesiones.

- `CRM`
  Maneja leads, actividades, pipeline comercial y conversión a cliente.

- `Reservas/Operaciones`
  Maneja reservas, pasajeros, servicios, vouchers y seguimiento operativo.

- `Finanzas/Facturación`
  Maneja cobranzas, tesorería, comprobantes, AFIP y controles financieros.

- `Catálogo/Publicación`
  Maneja países, destinos, paquetes, tarifas y contenido público.

- `Mensajería/WhatsApp`
  Maneja conversaciones, configuración del bot, entregas y captura de leads por WhatsApp.

## Reglas de Dependencia

- `TravelApi` actúa como adaptador de entrada HTTP y SignalR.
- `Application` define puertos, DTOs y casos de uso.
- `Domain` contiene reglas y entidades del negocio.
- `Infrastructure` implementa persistencia e integraciones externas.
- Los controllers no deben depender de `AppDbContext` ni de helpers concretos de persistencia.
- La resolución de referencias públicas/internas debe pasar por `IEntityReferenceResolver`.
- Las integraciones de tiempo real deben pasar por puertos de aplicación, no por hubs concretos desde `Infrastructure`.

## Estado Actual

- El módulo `Mensajería/WhatsApp` ya fue encapsulado detrás de servicios de aplicación dedicados.
- `NotificationHub` salió de `Application`; el despacho en tiempo real ahora usa `INotificationRealtimeDispatcher`.
- Se agregaron tests de arquitectura para evitar regresiones en las dependencias entre capas.
