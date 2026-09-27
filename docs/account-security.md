# Seguridad de cuentas — fase 7, operación local

Login actualizado por petición del usuario: `POST /api/auth/login` recibe `{ email, password }`. Correo con trim y comparación sin mayúsculas; el nombre de usuario sigue en registro, perfil y claims, pero ya no es credencial de acceso. Respuesta genérica 401 para correo desconocido/clave incorrecta/cuenta inactiva. Correos legacy ambiguos se rechazan para conciliación, nunca se elige una cuenta arbitraria. Frontend y backend deben actualizarse juntos.

Primera entrega local: `PUT /api/profile/me/password` autenticado, body `{ currentPassword, newPassword }`. Devuelve 204 y revoca todas las sesiones, incluida la actual. Validación/clave incorrecta: 400 con código; actualización concurrente/sesión cambiada: 409. Reutiliza rate limit de origen `auth`.

`CredentialVersion` es aditiva y comienza en cero para usuarios existentes. Login incluye claim `credential_version`; `CurrentAccountValidator` compara con el documento actual. JWT legacy sin claim solo acepta cuentas en cero. Cambio de hash e incremento de versión son una misma actualización condicional; no se emite token nuevo automáticamente.

El usuario debe volver a iniciar sesión. No existe mecanismo para editar credenciales de otra cuenta mediante este endpoint. Campos de perfil ajenos se conservan; solo se audita actor/resultado/código, nunca contraseñas.

Rollback debe conservar validación de `CredentialVersion` y hashes nuevos. No regresar a backend anterior que ignore la versión una vez que haya cambios de contraseña.

Actualización 27/09/2026: recuperación, verificación, retos y outbox implementados y probados localmente desde `C:\Proyectos\BookLibrary`. No hay proveedor ni envío externo habilitado.

## Recuperación y verificación

| Endpoint | Acceso / respuesta |
|---|---|
| `POST /api/auth/password-reset/request` `{email}` | Público; 202 genérico para conocido, desconocido, inactivo y solicitud repetida durante el enfriamiento; 503 deshabilitado. |
| `POST /api/auth/password-reset/confirm` `{token,newPassword}` | Público; 204; 400 `invalid_challenge` si inválido/vencido/usado o contraseña rechazada. No inicia sesión. |
| `POST /api/auth/email-verification/request` `{}` | Autenticado, destinatario propio; 202 encolado, 429 `request_cooldown`, 503 deshabilitado. |
| `POST /api/auth/email-verification/confirm` `{token}` | Público; 204 o 400 `invalid_challenge`. |

Limitador por origen `auth` y enfriamiento durable de un minuto por correo normalizado/propósito o usuario/propósito. El host QA elimina solo el límite por origen; conserva el enfriamiento por cuenta. La solicitud pública recorre la misma cola para correo conocido/desconocido; no busca la cuenta síncronamente ni confirma entrega.

JWT revocado en un endpoint público se descarta y la petición continúa como anónima. Endpoints privados siguen devolviendo 401, sin conservar privilegios del JWT anterior.

`EmailVerifiedAt` comienza nulo. Cambios de correo en perfil y administración lo limpian e incrementan `EmailVersion`. Registro/perfil/admin comprueban colisiones también contra correos antiguos sin `NormalizedEmail`, con mayúsculas o espacios periféricos; los índices normalizados resuelven carreras entre escrituras nuevas. Ambigüedades preexistentes requieren conciliación operativa; no se migran automáticamente.

Retos: 32 bytes aleatorios, token base64url de 43 caracteres y solo hash SHA-256 en `AccountChallenges`. ID único por usuario/propósito, índice único de hash y TTL de vencimiento. Emitir el reto nuevo sustituye al anterior de ese propósito cuando el worker procesa la solicitud; encolar no lo invalida aún. Confirmación transaccional comprueba propósito, consumo, expiración, cuenta activa, correo y versiones, y actualiza cuenta/auditoría/reto juntos. TTL limpia; la aplicación decide la validez. Mongo requiere replica set y transacciones.

## Cola y transporte local

`AccountMailOutbox` guarda payload cifrado con Data Protection, propósito `BookLibrary.AccountMail.v1`. Etapas `request → delivery → done` o `dead`. Worker revisa cada tres segundos cuando no encuentra trabajo.

- Claim atómico con `LeaseId`, lease de un minuto y contador; tras caída puede reclamar otro worker cuando vence.
- Máximo cinco intentos por job, incluida preparación; backoff entre intentos de 20, 40, 80 y 160 segundos. Al agotar, `dead` y payload vacío. Caída durante el quinto intento: se termina tras vencer el lease.
- Reintentar `delivery` conserva el token cifrado. Antes de entregar comprueba reto vigente, cuenta activa, correo y versiones. Enlaces reemplazados, consumidos o vencidos se descartan.
- Transporte local escribe temporal y mueve atómicamente a `<jobId>.json`. Repetir tras perder confirmación no crea otro archivo. Un proveedor futuro deberá deduplicar por job ID; el lease por sí solo no garantiza entrega única externa.
- `done`/`dead` borran payload. TTL elimina jobs a los dos días; un job expirado no se reclama aunque aún no se haya limpiado. Archivos del buzón contienen enlaces en claro como cualquier correo de prueba: acceso restringido, limpieza y exclusión de Git.
- Logs no incluyen payloads, direcciones, tokens ni cuerpos de excepciones del proveedor. La advertencia del worker es genérica. No hay replay ilimitado: resolver causa y solicitar enlace nuevo.

## Configuración

Variables ASP.NET (también disponibles como claves `AccountRecovery:...`):

| Variable | Default / validación |
|---|---|
| `AccountRecovery__Enabled` | `false` |
| `AccountRecovery__PublicBaseUrl` | `http://localhost:4200`; HTTPS o HTTP loopback, sin credenciales/query/fragmento. Nunca Host/returnUrl. |
| `AccountRecovery__LocalMailDirectory` | Vacío; al habilitar exige ruta absoluta privada con permiso de escritura. |
| `AccountRecovery__ResetMinutes` | 30; rango 1–60 |
| `AccountRecovery__VerificationMinutes` | 1440; rango 1–1440 |

Opciones validadas al arrancar. La UI comunica las vigencias predeterminadas; cambiar vigencias requiere actualizar esos textos. Esta entrega local usa los defaults.

### Persistencia de claves

Application name estable `BookLibrary.AccountSecurity`. Se usa la persistencia predeterminada de ASP.NET bajo la identidad del proceso. En esta máquina existe el key ring Windows en `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`; mover el código no debe borrar ni mover esas claves. No copiar su contenido a logs/evidencias.

Prueba de reinicio con directorio persistido y nuevo proveedor: descifra la entrega pendiente, conserva token y deduplica. Esto no certifica otro host o varias instancias.

Antes de activar otra instalación/proveedor: configurar explícitamente repositorio persistente compartido entre instancias, cifrado en reposo (DPAPI/certificado/KMS según entorno), identidad/permisos mínimos y respaldo/restauración de datos y claves. No existe variable propia para cambiar el repositorio en el código actual: requiere configurar Data Protection en arranque. No activar en hosts efímeros confiando en defaults. Sin claves los jobs pendientes resultan ilegibles; hashes no permiten recuperar tokens.

## QA aislado

1. Mongo loopback 27184, replica set `booklibraryqa`; `BOOK_LIBRARY_TEST_MONGO_URI=mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa`.
2. Desde este repositorio: `dotnet run --project tools/BookLibrary.QaHost --no-launch-profile`. Usa 7184, base `booklibrary_ui_test_<guid>`, JWT aleatorio y buzón temporal. No usa Atlas para las pruebas.
3. Desde `C:\Proyectos\BookLibrary\FrontEnd\Book-Library-Client`: `node node_modules/@angular/cli/bin/ng.js serve --port 4284 --proxy-config proxy.e2e.json`.
4. Verificar rutas de procesos y marcador `/__qa`. `/__qa/mail` solo existe en ese host local, con cuentas ficticias `.invalid`.
5. Detener con Ctrl+C para ejecutar limpieza de base/buzón. Verificar ausencia de la base exacta y listeners. Tras cierre forzado limpiar solo los recursos registrados de esa sesión.

## Rollback y activación externa

`AccountRecovery__Enabled=false` impide solicitudes, confirmaciones y procesamiento nuevos. Conservar colecciones, hashes nuevos, versiones y retos consumidos. No volver a backend que ignore `CredentialVersion` ni reactivar sesiones revocadas. Desactivar no borra archivos ya entregados en el buzón.

Proveedor, remitente y dominio reales pendientes de elección/autorización. Activación externa requiere adaptador con idempotencia/reintentos comprobados, secretos privados, configuración de claves y prueba del entorno de despliegue. No se envió correo a personas reales.

Verificación backend: 293 pruebas sin omisiones y build Release con cero errores/advertencias. Suites `AccountRecoveryTests`, `AccountMailReliabilityTests`, `AccountRecoveryConfigurationTests`, `EmailLoginTests`, `PasswordSecurityTests`. Se preservó `.env.example` eliminado, sin restaurarlo ni prepararlo para commit. Sin push.
