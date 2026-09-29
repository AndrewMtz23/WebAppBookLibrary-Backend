# Notificaciones personales (fase 9)

## Contrato y garantías

`/api/notifications/my` requiere una cuenta activa. Cada consulta/mutación usa el identificador autenticado; admin y bibliotecario tampoco pueden abrir otro buzón. `GET` admite `pageSize` (20, máximo 100), `before` (secuencia positiva) y `read` (booleano). Devuelve `items`, `nextCursor`, `readThrough` y `unreadCount`. El contador considera todas las páginas. `PUT /{id}/read` es idempotente. `PUT /read-all` recibe `{through: readThrough}` y deja pendientes los avisos posteriores. La secuencia por propietario se asigna dentro de la transacción: evita consumir avisos que todavía no habían confirmado su escritura al abrir el centro.

`GET/PUT /preferences`: `{reminders, email}`; la respuesta añade `emailVerified`. Por defecto, recordatorios activados y correo apagado. Las confirmaciones permanecen en la aplicación. Activar correo requiere dirección verificada. Cambiar preferencias no afecta los correos de seguridad.

Las reservas, devoluciones y cancelaciones escriben aviso y referencia al outbox en la transacción del préstamo. Las devoluciones físicas incluyen inventario y reintentan conflictos transitorios. Las claves únicas `(UserId, EventKey)` y `(UserId, Sequence)` evitan duplicar avisos. `AccountMailOutbox._id = notification-{id}` evita duplicar el canal de correo.

No se almacenan enlaces privados de libros, correos ni payloads del proveedor en los avisos. Los enlaces son destinos semánticos a fichas que revalidan disponibilidad y acceso. La eliminación definitiva de cuenta limpia preferencias, avisos y sus jobs en la misma transacción; las cuentas con préstamos mantienen la restricción preexistente de borrado.

## Activación y rollback

- `Notifications:Enabled` (predeterminado `true`) controla productores, recordatorios y entrega opcional. Requiere reiniciar el servicio al cambiar configuración. Desactivarlo conserva los avisos y jobs pendientes; el dispatcher excluye esos jobs y sigue atendiendo recuperación/verificación.
- `EarlyHours=72`, `SoonHours=24`, `PollSeconds=30`; validación de inicio: `0 < SoonHours < EarlyHours <= 720` y `3 <= PollSeconds <= 3600`.
- No se importan confirmaciones antiguas. Los préstamos físicos activos preexistentes reciben solamente su umbral vigente cuando el worker los recorre. Registrar la hora de despliegue como inicio operativo y avisar de este tratamiento antes de activar.
- MongoDB debe soportar transacciones (replica set). El arranque crea índices. Desplegar backend antes del frontend.
- Para rollback: desactivar `Notifications:Enabled`, reiniciar, verificar que los jobs opcionales no cambian y que un correo de seguridad de prueba sigue procesándose; conservar las colecciones para reanudar.

## Recordatorios y entrega

El worker recorre lotes de 100 préstamos en orden de ID, con cursor durable, lease de dos minutos y plazo de 45 segundos por lote. Un reinicio recupera el lease vencido. Puede repetir un lote tras una caída: las claves por préstamo, fecha de vencimiento y umbral deduplican sus efectos. Se selecciona un solo umbral vigente por pasada; tras una parada larga no se envían todos los umbrales atrasados. La escritura del préstamo y el guard de usuario dentro de la transacción serializan devoluciones/cancelaciones/cambios de fecha y desactivación con la creación del recordatorio.

Antes de enviar se revisan cuenta activa, dirección verificada, preferencias, fecha y estado actual del préstamo. Se suprimen recordatorios obsoletos. Una entrega externa no puede ser atómica con un cambio posterior en MongoDB: el límite es la última revalidación inmediatamente anterior al envío. Un proveedor debe admitir la clave estable de idempotencia; sin esa capacidad existe una ventana de repetición si entrega y pierde la confirmación.

Se reutiliza `IAccountMailTransport` y `AccountMailDispatcher` de fase 7: lease de un minuto, plazo de 30 segundos para transporte de avisos, cinco intentos y backoff `10 * 2^intentos` segundos. `done` significa aceptado por el transporte o suprimido, **no entrega final a un buzón externo**. Las métricas distinguen ambas situaciones. Los errores se sanitizan; no se registran mensajes del proveedor ni direcciones.

El transporte incluido sigue siendo el buzón local explícito de fase 7. El correo requiere `AccountRecovery:Enabled`, URL pública validada y directorio absoluto de buzón. Las pruebas no envían a destinatarios reales. La activación de un proveedor real queda pendiente de un adaptador validado, límites de envío, gestión de 429/Retry-After e idempotencia del proveedor; no presentar este transporte local como correo operativo en producción.

## Retención y observabilidad

No hay TTL sobre notificaciones, preferencias ni checkpoint. Los jobs opcionales usan expiración máxima, de modo que el TTL de seguridad no destruya la cola durante un rollback largo. Conservar pendientes/dead hasta conciliación. Política inicial: revisión operativa mensual; archivar trabajos terminales después de 90 días con respaldo verificado, sin borrar las claves de deduplicación de avisos mientras sobreviva el préstamo. No ejecutar purgas automáticas hasta validar volumen y política de retención del despliegue. Expirar avisos nunca borra historial de préstamos.

Meter `BookLibrary.Notifications`:

| Instrumento | Significado |
|---|---|
| `booklibrary.notifications.delivery.attempts` | Contador etiquetado `outcome=accepted/suppressed/retry/dead` |
| `booklibrary.notifications.pending` | Muestra del total pendiente por pasada |
| `booklibrary.notifications.oldest_pending` | Edad en segundos del pendiente más antiguo |
| `booklibrary.notifications.dead` | Muestra de trabajos agotados |

Conectar un colector a este Meter en el despliegue real. Umbrales iniciales: cualquier dead; edad pendiente >15 min durante 5 min; ausencia de muestras >3 intervalos cuando el worker está habilitado. Investigar sin volcar contenido de avisos ni correos. `Notifications:Enabled=false` pausa muestras y debe silenciar únicamente su alerta de ausencia. No hay endpoint público de reenvío.

Capacidad del barrido predeterminado: hasta 100 préstamos por 30 segundos, más duración de procesamiento. Medir el ciclo completo con el volumen de staging; ajustar intervalo o particionar el worker antes de exceder la ventana operativa. La implementación mantiene un cursor acotado y prioriza consistencia, sin prometer inmediatez ni tiempo real.

## Verificación

`BOOK_LIBRARY_TEST_MONGO_URI=mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa` y `dotnet test`. Las pruebas crean y eliminan bases `booklibrary_review_test_*` aisladas. Cubren privacidad HTTP en tres roles, frontera de lectura, preferencias, reserva fallida, doce devoluciones simultáneas, umbrales, renovación/devolución, cuenta inactiva, lease/cursor/reinicio, pérdida de confirmación e independencia del correo de seguridad durante rollback. Las suites previas verifican cinco reintentos y agotamiento del dispatcher compartido.
