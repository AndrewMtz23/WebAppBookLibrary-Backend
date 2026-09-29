# Fase 10: reservas, recogida, espera y renovaciones

Fecha: 2026-09-29. Estado: propuesta para revisión; sin implementación ni activación.
Requisito de origen: `Docs/sprints/sprint1/fase-10-reservas-y-prestamos-avanzados.md` del workspace BookLibrary.

## Objetivo

El lector puede reservar un físico disponible o entrar en espera. El personal registra la entrega y desde ese momento comienza el préstamo. Si nadie recoge una reserva, la unidad pasa a la siguiente persona elegible. El lector puede solicitar una ampliación que el personal resuelve con reglas verificadas por el servidor.

Se conserva la semántica digital y las fechas de préstamos anteriores. Se trabaja en los dos repositorios sobre `main` local, sin worktrees. Los ajustes visuales locales existentes y la eliminación local de `.env.example` quedan fuera de este diseño.

## Decisiones propuestas

| Regla | Propuesta |
|---|---|
| Recogida | 48 horas transcurridas desde que se asigna la unidad |
| Préstamo nuevo | 14 días transcurridos desde la entrega confirmada |
| Renovación | Una ampliación de 7 días desde el vencimiento vigente |
| Elegibilidad para renovar | Préstamo físico de la nueva política, activo, no vencido y sin espera elegible |
| Resolución | Bibliotecario o administrador; nunca el propio lector como tal |
| Préstamos anteriores | Política `legacy`, fechas intactas; sin renovación bajo esta nueva política |
| Calendario | Horas continuas, incluidos fines de semana; sin calendario de festivos en esta fase |

Estas decisiones deben validarse antes de activar el flujo. Cada reserva conserva un snapshot de la política y su versión `circulation-v1`; la entrega utiliza ese snapshot. Cambiar la configuración afecta únicamente a operaciones nuevas. Las fechas se guardan en UTC y se muestran con hora y zona explícitas.

## Alternativas y selección

1. Ampliar `Loan.Status` para incluir espera y recogida. Reduce entidades, pero convierte reservas sin entrega en préstamos y cambia consultas, permisos y métricas existentes.
2. **Entidades separadas y transacciones compartidas, opción recomendada.** Esperas, retenciones y solicitudes tienen su propio historial; `Loan` nace al entregar. La infraestructura de notificaciones de fase 9 se reutiliza dentro de la transacción.
3. Servicio independiente con sincronización eventual. Añade despliegue y compensaciones distribuidas sin necesidad para el tamaño actual de la aplicación.

## Contrato y compatibilidad

Se añade `POST /api/pickup-reservations` con `{ bookId }` para reservar un físico. Devuelve una reserva de recogida, nunca un `LoanResponse`. La espera continúa siendo una decisión explícita del lector mediante `POST /api/waitlist`.

`GET /api/circulation/policy` expone modo, versión y plazos públicos para que el cliente elija el flujo correcto. No expone configuración interna ni datos de usuarios. El servidor vuelve a validar el modo al ejecutar cada comando.

En modo activo, `POST /api/loans` mantiene el contrato digital; para físicos devuelve `409 physical_pickup_required` sin descontar inventario. El frontend actualizado usa el comando nuevo. Un cliente anterior recibe un error controlado que exige actualizar; no puede saltarse la cola mediante el endpoint anterior.

Rutas adicionales según el documento de fase:

- `POST /api/waitlist`, `GET /api/waitlist/my`, `DELETE /api/waitlist/{id}`.
- `GET /api/pickup-reservations/my`, `GET /api/pickup-reservations` para personal.
- `PUT /api/pickup-reservations/{id}/collect` y `/{id}/cancel`, con `expectedVersion`.
- `POST /api/loans/{id}/renewal-requests`.
- `GET /api/renewal-requests/my` para consultar resultado propio y `GET /api/renewal-requests` para personal.
- `PUT /api/renewal-requests/{id}/decision`, con versión, decisión y motivo.

Listados paginados: 20 por defecto, máximo 100; orden estable por fecha e ID y filtros validados. Fechas absolutas, no estimaciones de disponibilidad. Identidades de otras personas nunca se incluyen en la respuesta de espera propia.

Las operaciones de creación llevan una clave de idempotencia acotada y vinculada al actor, operación y contenido. Repetir la misma operación devuelve el resultado original; reutilizar la clave con contenido distinto produce `409`. Una repetición de recogida devuelve el mismo préstamo, incluso si la primera respuesta se perdió. Una transición competidora produce `409`; un recurso ajeno no revela datos. Validaciones usan Problem Details.

## Modelo y reglas de integridad

- `WaitlistEntry`: usuario, libro, fecha de ingreso, versión, estado `queued/offered/fulfilled/cancelled/expired`, oferta asociada y causa de cierre.
- `PickupReservation`: usuario, libro, estado `ready/collected/cancelled/expired`, fechas de disponibilidad y vencimiento, versión, snapshot de política, espera de origen opcional y préstamo resultante.
- `RenewalRequest`: préstamo y lector, versión y vencimiento originales, vencimiento solicitado calculado en servidor, estado `pending/approved/rejected/cancelled`, motivo de hasta 500 caracteres y resolución con actor y fecha.
- `Loan`: incorpora `CheckedOutAt`, `PolicyVersion`, snapshot de política, `RenewalCount` y versión de circulación; conserva el versionado de notificaciones.
- Historial de circulación: identificador de transición, entidad, actor o worker, fecha, estado anterior y posterior, causa y fechas relevantes. Se guarda en la misma transacción y se consulta con permisos por recurso.

Índices únicos parciales impiden dos esperas activas por usuario/libro, dos retenciones listas por usuario/libro, dos solicitudes pendientes por préstamo y dos préstamos derivados de una misma recogida. Índices de libro/estado/fecha/ID sirven FIFO; estado/vencimiento/ID sirve expiración. Los índices por colección no bastan para impedir duplicados entre colecciones: todos los comandos físicos comparten la escritura de `Book.ReferenceVersion` y vuelven a comprobar préstamo, retención y espera dentro de la transacción. Se conserva la protección de referencias de usuario.

Por libro físico: `0 <= disponibles`, `0 <= retenidos`, `0 <= prestados`, y su suma no supera el total. Una oferta consume una unidad; entregar consume la retención sin segundo descuento; devolver o cancelar/expirar una retención libera una unidad exactamente una vez. La conciliación informa diferencias, nunca aumenta disponibilidad silenciosamente.

## Operaciones atómicas

### Reservar y entrar en espera

La transacción verifica usuario y libro activos, política, inventario conciliado y ausencia de relación activa duplicada. El orden FIFO es fecha de ingreso más ID generado por el servidor. Antes de una reserva directa se atiende a las personas elegibles anteriores; si quedan personas por procesar en un lote limitado, no se permite adelantarles.

Entrar en espera con una unidad disponible puede producir una oferta inmediata después de respetar a los anteriores. El resultado distingue `queued` de `offered`. Cancelar una espera ofrecida también cancela su retención en la misma transacción. Una oferta vencida cierra la posición; volver a entrar genera una posición nueva al final.

### Recoger, cancelar y devolver

Solo el personal confirma entrega. Se exige estado `ready`, versión vigente, usuario y libro elegibles y `now < pickupExpiresAt`. Se crea un único préstamo y se cierran oferta/espera en la misma transacción, junto con historial y notificación.

Al vencer exactamente el plazo ya no se puede recoger, aunque el worker aún no haya procesado la reserva. La condición se revisa con el reloj del servidor en cada intento transaccional. Entrega y expiración concurrentes tienen un único ganador.

Cancelar o expirar entrega la unidad al siguiente elegible dentro de la transacción; si no existe, incrementa disponibilidad. La devolución de cualquier físico, incluido legacy, utiliza esta misma asignación para no hacer visible temporalmente una unidad que pertenece a la cola.

En los préstamos creados por recogida, el lector no puede liberar inventario declarando una devolución o cancelación desde un cliente antiguo: la devolución física requiere personal y después de entregar ya no existe cancelación de reserva. El personal registra la devolución aun si el lector fue desactivado. Los préstamos legacy conservan sus reglas de cierre, con la asignación de disponibilidad integrada.

### Renovar

Solicitar no modifica vencimiento. El servidor calcula la ampliación y permite una sola solicitud pendiente. Al aprobar vuelve a verificar política, estado, versión y vencimiento originales, límite de renovaciones, usuario/libro activos y ausencia de espera elegible bajo el mismo bloqueo transaccional del libro.

La aprobación modifica `DueAt`, incrementa versiones y contador, registra historial y notificación. La aprobación tardía de una solicitud que dejó de ser elegible devuelve conflicto sin modificar fechas; se puede rechazar con motivo. Al devolver el préstamo se cancelan sus solicitudes pendientes en la misma transacción. Un rechazo tampoco cambia fechas.

## Worker y notificaciones

Worker con lotes acotados, cursor y lease persistentes para expirar retenciones y asignar unidades pendientes. La corrección depende de condiciones transaccionales y claves únicas, no de tener una sola instancia. Tras caída se puede repetir el lote sin duplicar préstamos, ofertas, inventario ni eventos.

Generalizar el emisor de fase 9 para eventos de recogida y renovación sin fabricar préstamos ficticios. Clave de deduplicación por entidad/transición. La bandeja y el outbox se escriben junto con la transición; el correo se envía después del commit. Antes de enviar un aviso de recogida se comprueba que siga listo y vigente. Una renovación invalida los recordatorios del vencimiento anterior mediante el mecanismo existente de fecha y versión. Se mantienen preferencias, comprobación de correo y limitaciones de entrega del proveedor.

Métricas sin datos personales: tamaño y antigüedad de cola, ofertas vencidas, conflictos, duración y fallos del worker, retraso del lote. No usar IDs de usuarios o libros como etiquetas.

## Operación administrativa y casos reales

- Incrementar existencias provoca asignación a la cola respetando FIFO; la reserva directa sigue protegida mientras el worker procesa el cambio.
- No se permite reducir total por debajo de retenidos más prestados ni cambiar a digital con relaciones físicas activas.
- Retirar un libro impide nuevas asignaciones y recogidas. Sus esperas y retenciones se cierran con causa registrada mediante el worker; las unidades liberadas no se ofrecen mientras permanezca retirado. Se aceptan devoluciones pendientes.
- Desactivar un lector impide nuevas operaciones y recogidas. Sus esperas y retenciones se cierran y reasignan con causa registrada. La elegibilidad se verifica aunque el worker no haya llegado todavía.
- Borrado definitivo de libro o usuario debe contemplar las referencias nuevas. Se bloquea mientras existan relaciones o historial de circulación que deban conservarse, igual que la protección de préstamos existente.
- El borrado de préstamos no puede servir para liberar inventario fuera de la transacción ni destruir la relación de una recogida. Los préstamos de la nueva política se conservan como historial; las rutas legacy se revisan para impedir bypass.

## Interfaz

Ficha: `Reservar para recoger` si existe disponibilidad; `Entrar en lista de espera` si no. Se muestran plazo de recogida y estado propio confirmado. Un conflicto actualiza disponibilidad y ofrece la acción correcta. Favoritos conserva su función independiente.

Mi biblioteca: secciones En espera, Listo para recoger, Prestados y acceso digital; historial accesible. Espera muestra posición orientativa sin nombres ni fecha prometida. Recogida muestra fecha y hora límite, zona y cancelación. Préstamo muestra vencimiento y solicitud de renovación cuando corresponda, con resultado y motivo visible al propietario.

Personal: colas paginadas de recogidas y renovaciones, filtros persistentes, detalle, confirmación centrada y accesible, foco restaurado y mensajes de resultado. Un `409` refresca el registro sin perder filtros. Estados de carga, error y vacío; móvil, escritorio y ambos temas.

Dashboards: conteos separados de espera, retenciones pendientes y préstamos entregados. No sumar dos veces una oferta convertida en préstamo ni reinterpretar métricas históricas sin cambiar su etiqueta. `/help` y `/loan-guide` muestran las reglas del modo activo.

## Activación y reversión

Modos persistentes de circulación: `legacy`, `active` y `draining`. Todas las instancias consultan la misma fuente de modo; no se permite que una instancia vuelva al flujo legacy mientras otra mantiene retenciones.

1. Desplegar backend compatible con modo legacy y frontend capaz de ambos flujos.
2. Ejecutar conciliación en copia aislada con respaldo: inventario, referencias, duplicados, préstamos legacy y construcción de índices. Marcar política legacy sin cambiar fechas ni descontar copias. Libros inconsistentes quedan bloqueados para el flujo nuevo hasta corregirse explícitamente.
3. Ensayar activación y reversión, ejecutar suites y validar políticas.
4. Activar mediante cambio controlado y transaccional del modo; todos los comandos físicos comparten la guarda del modo durante la transición para evitar una operación legacy en vuelo.
5. Si hay que detener altas, pasar a draining: no nuevas esperas, reservas ni renovaciones ni promociones de cola; sí entrega/cancelación de retenciones existentes, expiración y devoluciones. Esperas ya existentes permanecen visibles y cancelables, sin promesa de asignación durante el drenado.

No volver al binario anterior mientras existan retenciones, esperas o préstamos de la nueva política que dependan de sus reglas. Una reversión de aplicación conserva el backend compatible o requiere una migración de reversión explícita. Esta fase no autoriza mutar datos reales durante las pruebas.

## Comprobación de aceptación

| Requisito | Evidencia necesaria |
|---|---|
| RF-CIRC-01 | Altas concurrentes, duplicado entre préstamo/retención/espera, cancelación y privacidad |
| RF-CIRC-02 | FIFO con fecha empatada, última unidad, alta directa concurrente, usuarios inactivos y aumento de existencias |
| RF-CIRC-03 | Doble recogida y respuesta perdida generan el mismo préstamo, plazo desde entrega y un solo descuento |
| RF-CIRC-04 | Carrera recogida/expiración, reinicio de worker, reasignación y conciliación exacta |
| RF-CIRC-05 | Solicitud sin cambio de fecha, rechazo, doble aprobación, nueva espera durante aprobación, devolución y avisos obsoletos |
| RF-CIRC-06 | Préstamos anteriores con fechas iguales, digital concurrente, rutas antiguas sin bypass y modos activo/drenado |

Pruebas de integración con MongoDB replica set aislado, reloj controlable y concurrencia real. Jornada en navegador con lector y personal, recarga, dos pestañas, pérdida de respuesta, móvil/escritorio y accesibilidad. Verificar autorización en HTTP, no solo botones ocultos. Ejecutar suites completas backend/frontend y builds, además del ensayo de migración, recuperación y conciliación.

## Siguiente entregable

Una vez revisado este diseño, preparar `Docs/sprints/sprint1/planes/fase-10-plan-implementacion.md` con archivos, interfaces, tareas y pruebas concretas; después implementar y registrar resultados en `evidencias/fase-10-verificacion.md`. Este documento define el comportamiento propuesto y no acredita funcionalidad terminada.
