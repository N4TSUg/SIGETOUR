# Hallazgos relevantes de revisión de SIGETOUR

**Crítico (confirmado):**
- `SIGETOUR.API/appsettings.json` mantiene credenciales PostgreSQL públicas. Rotar inmediatamente; evitar repositorios/historial/logs.
- `SIGETOUR.API/Infrastructure/Data/IdentitySeeder.cs` siembra dos cuentas privilegiadas con contraseñas codificadas; cambiarlas y eliminar esas constantes del código. Las cuentas existentes NO se reparan solo borrando el archivo fuente.
- `SIGETOUR.API/Program.cs` contiene valor JWT por defecto si no hay configuración y tiene `ValidateIssuer=false` y `ValidateAudience=false`.

**Recomendado:**
- Proteger endpoints de administración usando `Authorize(Roles=...)`; que tener un JWT cualquiera no baste para crear/editar tours.
- Auditar uso de `IdentitySeeder` en startup y evitar bloqueos `.Wait()` (mejor `await` al inicializar).
- Sustituir `EnsureCreated` por migraciones controladas tras baselining.
- Comprobar autorización por recurso en reservas, datos personales, documentos y descargas.
- Evitar logs de DNI, CVV, token, contraseñas o payloads de checkout; clasificar y minimizar PII.
- HTTPS, cabeceras de seguridad, CORS estricto, rate limiting en crear pagos y reservas, antifraude y reporte de errores centralizado.
- SAST, SCA, secret scanning y pruebas automáticas en CI.
