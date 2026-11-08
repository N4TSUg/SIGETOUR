# SIGETOUR - Turismo Chilón S.A.C.
> **Sistema Integrado de Gestión de Tours, Despacho Operativo y Control de Flota**  
> *Cajamarca, Perú • Acreditado por MINCETUR & DIRCETUR (Certificado N° 4412)*  
> 🌐 **Plataforma Oficial en Producción:** [https://sigetour.onrender.com](https://sigetour.onrender.com)

[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16%2B-4169E1?logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![Tailwind CSS](https://img.shields.io/badge/Tailwind_CSS-3.4-38B2AC?logo=tailwind-css&logoColor=white)](https://tailwindcss.com/)
[![Render](https://img.shields.io/badge/Render-Production-46E3B7?logo=render&logoColor=white)](https://sigetour.onrender.com)
[![Cloudinary](https://img.shields.io/badge/Cloudinary-Integrated-3448C5?logo=cloudinary&logoColor=white)](https://cloudinary.com/)
[![Tests Passing](https://img.shields.io/badge/Tests-171%2F171%20Passing%20(100%25)-brightgreen?logo=xunit)]()
[![SonarQube Quality Gate](https://img.shields.io/badge/SonarQube-Passed-4C9BD4?logo=sonarqube&logoColor=white)]()

---

## 📌 1. Descripción General del Proyecto
**SIGETOUR** es una plataforma web empresarial integral diseñada y desarrollada a medida para **Turismo Chilón S.A.C.** (RUC: 20601248901), empresa líder de transporte turístico con sede central en Jr. 2 de Mayo 569, Cajamarca, Perú.

La plataforma resuelve y unifica la cadena de valor turística en dos grandes frentes:

```mermaid
flowchart LR
    A["Turista / Pasajero"] --> B["Portal Comercial Web"]
    B --> C["Catálogo Interactivo de Tours & Flota"]
    C --> D["Checkout Guiado en 3 Pasos"]
    D --> E["Pago Online Culqi / Tarjetas"]
    E --> F["Voucher Digital QR + WhatsApp + PDF"]
    
    F --> G["Panel de Operaciones SIGETOUR"]
    G --> H["Despacho en Vivo & Semáforo de Salidas"]
    H --> I["Asignación Eficiente de Flota & Chofer MTC"]
    I --> J["Manifiesto Oficial en Excel (DIRCETUR/SUTRAN)"]
    G --> K["Caja Chica & Arqueo de Turnos Diarios"]
    G --> L["Facturación Electrónica SUNAT (Nubefact)"]
```

---

## 🔐 2. Accesos Oficiales y Credenciales del Sistema

### URLs del Ecosistema
| Entorno | Servicio | URL |
| :--- | :--- | :--- |
| **Producción** | Portal Web Principal | [https://sigetour.onrender.com](https://sigetour.onrender.com) |
| **Producción** | Panel de Operaciones | [https://sigetour.onrender.com/Admin/Dashboard](https://sigetour.onrender.com/Admin/Dashboard) |
| **Producción** | Inicio de Sesión | [https://sigetour.onrender.com/Account/Login](https://sigetour.onrender.com/Account/Login) |
| **Local (Dev)** | Portal Web Principal | `https://localhost:7103` (o `http://localhost:5047`) |
| **Local (Dev)** | Inicio de Sesión | `https://localhost:7103/Account/Login` |

### Credenciales de Acceso Sembradas por Defecto
El sistema cuenta con un seeder criptográfico automático (`IdentitySeeder`) que inicializa los roles y credenciales administrativas si no existen en la base de datos:

| Rol | Usuario | Correo Electrónico | Contraseña Inicial | Nivel de Privilegios |
| :--- | :--- | :--- | :--- | :--- |
| **SuperAdmin** | `superadmin` | `superadmin@sigetour.com` | `[Definida en IdentitySeeder]` | Control maestro total: gestión de usuarios, roles, auditoría y configuración global. |
| **Admin** | `admin` | `admin@sigetour.com` | `[Definida en IdentitySeeder]` | Administración de tours, despacho operativo, control de flota, caja chica y reservas. |

---

## 🏛️ 3. Módulos y Capacidades del Sistema

### 🚍 3.1. Módulo de Flota Vehicular y Conductores
* **Ficha Técnica Oficial:** Placa, modelo, año de fabricación, número de chasis (VIN), motor, combustible y capacidad de 14 a 19 pasajeros.
* **Semáforo Normativo de Vencimientos:** Monitoreo y alertas visuales de vigencia para:
  * SOAT Turístico.
  * CITV (Certificado de Inspección Técnica Vehicular).
  * TUC (Tarjeta Única de Circulación emitida por el MTC).
* **Unidad Insignia (Flagship):** Marcación con botón estelar de la unidad emblemática para representarla en el banner principal del catálogo público.
* **Galería Multimedia con Visor Lightbox:**
  * Subida múltiple con soporte **drag-and-drop**.
  * Selección de foto de portada (`IsCover`).
  * Visor en pantalla completa con soporte táctil (*swipe*), navegación con flechas del teclado (`←`, `→`), tecla `Esc`, contador dinámico y carrusel inferior de miniaturas.
* **Padrón de Choferes:** Registro de conductores profesionales con categoría de brevete (A-IIIa, A-IIIc), vigencia y unidad habitual.

### 🧭 3.2. Módulo de Despacho en Vivo y Manifiesto de Pasajeros (DIRCETUR / SUTRAN)
* **Tablero de Salidas Diarias:** Monitoreo en tiempo real de salidas filtrables por turno (Mañana / Tarde) y fecha.
* **Semáforo de Estados de Despacho:**
  * 🟡 `Pendiente de Asignación`: Pasajeros confirmados sin vehículo ni conductor asignados.
  * 🟠 `Abordando`: Unidad en base recibiendo pasajeros y verificando identidad.
  * 🟢 `En Ruta`: Vehículo en trayecto hacia el circuito turístico.
  * ⚪ `Completado`: Tour finalizado con retorno a la base operativa.
* **Asignación Justa de Capacidad:** Permite asignar unidades de 14 o 19 plazas según la demanda real del turno, reduciendo sobrecostos de combustible.
* **Exportación de Manifiesto Oficial en Excel (`.xlsx`):** Generación automática mediante ClosedXML del formato reglamentario exigido por DIRCETUR y SUTRAN, conteniendo datos de la empresa, placa, chofer, número de brevete y nómina completa de turistas con documento de identidad y asiento.

### 🏞️ 3.3. Módulo de Catálogo Turístico y Paquetes Destacados
* **Buscador & Filtros Inteligentes:** Búsqueda en vivo por destino/circuito, fecha de viaje, turno (Mañana/Tarde), presupuesto y nivel de exigencia física.
* **Paquetes Estrella en Portada:** Gestor de paquetes destacados con porcentaje de descuento, tarifa promocional y badge de oferta con fecha de expiración.
* **Ficha de Tour & Lightbox:** Itinerario detallado con paradas y tiempos estimados, lista de inclusiones/exclusiones (guía, transporte, tickets) y galería interactiva en pantalla completa.
* **Moderación de Reseñas:** Panel para revisar y aprobar testimonios de turistas antes de su publicación en el portal.

### 💳 3.4. Módulo de Checkout, Pagos Online y Vouchers de Viaje
* **Proceso de Compra en 3 Pasos:** Validación en tiempo real de datos del titular, desglose de recojo en hotel céntrico de Cajamarca y políticas de viaje.
* **Pasarela de Pagos Culqi:** Integración de tarjeta de crédito/débito con tokenización segura, validación 3DS y webhooks con verificación de firma HMAC e idempotencia.
* **Voucher Oficial de Reserva:**
  * Código único de reserva con código QR para validación rápida en el abordaje.
  * Optimizado para impresión limpia en una sola hoja A4.
  * Botón de compartir directo a WhatsApp con mensaje preconfigurado.
* **Descarga de Voucher en PDF:** Motor vectorial integrado con `QuestPDF` para generación instantánea de comprobantes descargables.
* **Notificaciones por Correo:** Envío automatizado de confirmación con voucher adjunto mediante `EmailService`.

### 💰 3.5. Módulo de Caja Chica y Arqueo Diario de Turnos
* **Fondo Fijo de Caja Chica:** Registro de egresos operativos menores (combustible, peajes, refrigerios, mantenimiento de ruta) con comprobantes digitalizados y cálculo en tiempo real del gasto pendiente de reposición (`PendingSpent`).
* **Turnos de Caja Diaria:** Apertura con saldo inicial en efectivo, captura automática de cobros por reservas web/ventanilla, arqueo físico y conciliación de diferencias.
* **Liquidación Financiera en Excel:** Exportación completa del reporte de caja diaria en formato `.xlsx`.

### 🧾 3.6. Facturación Electrónica SUNAT (Nubefact)
* Emisión automática de **Boletas de Venta Electrónicas (`BBB1`)** y **Facturas Electrónicas (`FFF1`)** bajo estándar UBL 2.1 de SUNAT.
* Validación automática de DNI ante RENIEC y RUC ante SUNAT.

### ☁️ 3.7. Almacenamiento Multimedia Permanente (Cloudinary)
* Almacenamiento desacoplado en la nube en carpetas organizadas (`sigetour/vehicles` y `sigetour/tours`).
* Optimización automática al vuelo con conversión a formatos ligeros (`.webp`, `.avif`) y compresión inteligente (`q_auto, f_auto`).
* Eliminación sincronizada: al eliminar una foto en el panel, se remueve de Cloudinary liberando espacio.
* **Fallback local transparente:** Si no hay credenciales de Cloudinary configuradas, la aplicación guarda automáticamente en el disco local sin interrumpir el funcionamiento.

---

## 🛠️ 4. Stack Tecnológico Integral

El sistema está construido bajo una arquitectura moderna de capas desacopladas, utilizando el ecosistema oficial de **.NET 10** y las mejores prácticas de ingeniería de software:

```
SIGETOUR/
├── SIGETOUR.API/                     # Proyecto Web ASP.NET Core 10 (MVC + Web API)
│   ├── Application/                  # Lógica de aplicación, DTOs, Mappings y Servicios
│   │   ├── DTOs/                     # Objetos de transferencia de datos
│   │   ├── Interfaces/               # Contratos e interfaces de servicios
│   │   ├── Invoicing/                # Servicio de facturación electrónica Nubefact
│   │   ├── Mappings/                 # Perfiles de AutoMapper
│   │   ├── Options/                  # Clases de configuración fuertemente tipadas (Cloudinary)
│   │   ├── Payments/                 # Integración Culqi (Tokens, Cargos, Webhooks HMAC)
│   │   └── Services/                 # Implementaciones (Cloudinary, PDF, Email, Excel)
│   ├── Controllers/                  # Controladores RESTful API y Controladores UI MVC
│   │   └── UI/                       # AdminController, BookingController, TourController, etc.
│   ├── Core/Entities/                # Entidades de dominio y modelo relacional
│   ├── Infrastructure/Data/          # AppDbContext, configuraciones de EF Core y Seeders
│   └── Views/                        # Vistas Razor (.cshtml) con Tailwind CSS
└── SIGETOUR.Tests/                   # Suite de pruebas automatizadas (xUnit + Moq)
    ├── Audit/                        # Auditoría de hardening y seguridad
    ├── Controllers/                  # Pruebas de controladores administrativos y públicos
    ├── Helpers/                      # Factories de DbContext InMemory y Mocks
    ├── Payments/                     # Pruebas unitarias e integración de Culqi
    └── Services/                     # Pruebas de Cloudinary, PDF, Despacho y Contacto
```

### Desglose por Capas de Tecnología

| Capa / Dominio | Tecnología / Herramienta | Versión | Propósito en el Proyecto |
| :--- | :--- | :--- | :--- |
| **Frontend SSR** | **Razor Views (.cshtml)** | .NET 10 | Motor de renderizado del lado del servidor de alto rendimiento con layouts modulares. |
| **Estilos & UI** | **Tailwind CSS** | v3.4 | Framework utilitario para diseño responsivo mobile-first con paleta esmeralda/turquesa. |
| **Iconografía** | **Material Symbols Outlined** | v2.0 | Tipografía vectorial moderna de Google Fonts para iconografía de alta nitidez. |
| **Interactividad** | **JavaScript ES6+ Vanilla** | Nativo | Visor Lightbox con navegación por teclado y swipe táctil, carga drag-and-drop y validaciones. |
| **Accesibilidad** | **W3C WCAG 2.1 AA** | Nivel AA | Cumplimiento auditado por SonarQube Cloud con etiquetas semánticas nativas (`<button>`, `<label>`). |
| **Lenguaje Core** | **C# 13** | C# 13 | Lenguaje fuertemente tipado con pattern matching, records y null-safety activado. |
| **Framework Backend**| **ASP.NET Core** | 10.0 | Plataforma web unificada para controladores MVC y endpoints RESTful Web API con Kestrel. |
| **ORM Relacional** | **Entity Framework Core** | 10.0.12 | Mapeo objeto-relacional con consultas divididas (`SplitQuery`) y 14 migraciones Code-First. |
| **Driver PostgreSQL**| **Npgsql.EntityFrameworkCore**| 10.0.3 | Proveedor ADO.NET / EF Core de alto rendimiento para PostgreSQL. |
| **Base de Datos** | **PostgreSQL** | 16+ / 18 | RDBMS relacional transaccional (ACID) con 30 tablas normalizadas e integridad referencial estricta. |
| **Cloud Database** | **Neon Lakebase Postgres** | Serverless | Base de datos cloud serverless con pooling de conexiones y alta disponibilidad. |
| **Seguridad & Auth** | **ASP.NET Core Identity** | 10.0.12 | Gestión de usuarios, roles (RBAC), hashing PBKDF2 y cookies de sesión persistentes. |
| **Tokens API** | **JWT Bearer Authentication** | 10.0.12 | Autenticación stateless mediante tokens JSON Web Tokens firmados con HMAC-SHA256. |
| **Mapeo de Objetos** | **AutoMapper** | 16.2.0 | Transformación desacoplada entre Entidades de Dominio y Objetos de Transferencia (DTOs). |
| **Planillas Excel** | **ClosedXML** | 0.105.1 | Generación de Manifiestos de Pasajeros MTC y Liquidaciones de Caja en formato `.xlsx`. |
| **Documentos PDF** | **QuestPDF** | 2026.9.1 | Motor de maquetación vectorial para generación de Boletos Oficiales y Vouchers en PDF. |
| **Multimedia Cloud** | **CloudinaryDotNet** | 1.29.3 | SDK oficial para subida, optimización WebP/AVIF (`q_auto, f_auto`) y CDN global. |
| **Documentación API**| **OpenAPI / Swagger** | 10.0.10 | Especificación y documentación de endpoints RESTful vía `Microsoft.AspNetCore.OpenApi`. |
| **Testing** | **xUnit** & **Moq** | 2.x / 4.x | Suite de **171 pruebas unitarias e integración** con simulación de servicios y DbContext InMemory. |
| **Calidad de Código**| **SonarQube Cloud** | Cloud | Análisis estático de código continuo (SAST), Quality Gate A, 0 vulnerabilidades y 0 hot-spots. |
| **Contenedores** | **Docker & Docker Compose** | v26+ | Empaquetado en contenedores Linux multi-etapa con `docker-compose.yml` para despliegue local. |
| **Plataforma Cloud** | **Render Web Services** | Cloud | Alojamiento en contenedores Linux con despliegue continuo (CI/CD) conectado a GitHub. |

---

## 🔌 5. APIs e Integraciones Externas

SIGETOUR se conecta con servicios externos líderes en la industria para garantizar pagos seguros, facturación electrónica certificada, almacenamiento multimedia y notificaciones automáticas:

```mermaid
flowchart TD
    SIGETOUR["SIGETOUR Platform (.NET 10)"]
    
    SIGETOUR -->|"Tokenización & Cobros 3DS (v2)"| CULQI["Pasarela Culqi API"]
    CULQI -->|"Webhooks HMAC-SHA256"| SIGETOUR
    
    SIGETOUR -->|"Emisión UBL 2.1 (Boleta/Factura)"| NUBEFACT["Nubefact API (PSE / SUNAT)"]
    NUBEFACT -->|"Validación RUC / DNI"| RENIEC_SUNAT["SUNAT & RENIEC"]
    
    SIGETOUR -->|"Upload & Optimización WebP"| CLOUDINARY["Cloudinary Media API"]
    CLOUDINARY -->|"CDN Global (Akamai/Fastly)"| USUARIO["Navegador del Turista"]
    
    SIGETOUR -->|"Vouchers & Notificaciones PDF"| BREVO_SMTP["Brevo & Google SMTP API"]
    SIGETOUR -->|"Click-to-Chat con Voucher"| WHATSAPP["WhatsApp Business API"]
```

### Detalle de Integraciones

1. 💳 **Pasarela de Pagos Online Culqi API v2:**
   * **Endpoint Base:** `https://api.culqi.com/v2`
   * **Tokenización en Cliente:** Captura segura de datos de tarjeta (Visa, Mastercard, Amex, Diners) mediante `Culqi.js` sin almacenar números de tarjeta en el servidor (cumplimiento PCI-DSS).
   * **Cargos y 3D Secure (3DS):** Procesamiento de cargos directos con soporte para autenticación de dos factores bancaria.
   * **Webhooks Criptográficos:** Endpoint receptor `/api/culqipayments/webhook` protegido con validación de firma HMAC-SHA256, verificación de timestamp e idempotencia para eventos `charge.succeeded` y `charge.failed`.

2. 🧾 **Facturación Electrónica Nubefact (PSE Acreditado ante SUNAT):**
   * **Endpoint Base:** `https://api.nubefact.com/api/v1/`
   * **Estándar:** UBL 2.1 oficial de SUNAT para Boletas Electrónicas (`BBB1`) y Facturas Electrónicas (`FFF1`).
   * **Funcionalidades:** Cálculo automático de IGV (18%), generación de código Hash, código QR oficial, descarga de XML firmado y Constancia de Recepción (CDR).
   * **Validación de Identidad:** Consulta en línea de DNI ante RENIEC y RUC ante SUNAT para autocompletado y validación de clientes.

3. ☁️ **Almacenamiento Multimedia Cloudinary Media API:**
   * **SDK Integrado:** `CloudinaryDotNet 1.29.3`
   * **Directorio Organizado:** Las imágenes se canalizan en carpetas designadas (`sigetour/vehicles`, `sigetour/tours`).
   * **Optimización Inteligente:** Parámetros automáticos `q_auto, f_auto` que transforman imágenes pesadas tomadas con celular (8-10 MB) a formatos de última generación (`.webp`, `.avif`) de menos de 100 KB sin pérdida visual.
   * **Eliminación Sincronizada:** Al borrar un vehículo o tour en el panel administrativo, el servicio invoca la API de Cloudinary para eliminar el recurso en la nube liberando espacio.
   * **Fallback Transparente:** En ausencia de credenciales cloud, el sistema conmuta automáticamente al almacenamiento local en `wwwroot/images/` sin interrumpir la operación.

4. 📧 **Notificaciones Transaccionales (Brevo API v3 & Google SMTP):**
   * Envío automatizado de correos electrónicos transaccionales con confirmación de compra y el Voucher Oficial en PDF adjunto.
   * Soporte dual: API Key de Brevo (`BREVO_API_KEY`) y servidor SMTP TLS en puerto 587 (`smtp.gmail.com`).

5. 📱 **Integración con WhatsApp Business (Click-to-Chat API):**
   * Generación dinámica de hipervínculos estructurados (`https://wa.me/51928876259?text=...`) que permiten al pasajero recibir su voucher y comunicarse con la central de despacho en un solo clic.

---

## 🤖 6. Ecosistema de Agentes y Habilidades Autónomas (.agents / Antigravity AI)

El ciclo de vida, la arquitectura y el control de calidad de SIGETOUR han sido concebidos y acelerados utilizando el **Ecosistema de Agentes de Inteligencia Artificial de Google DeepMind (Antigravity)** junto con las habilidades autónomas almacenadas en el directorio `.agents/`:

```mermaid
graph TD
    subgraph Antigravity_Ecosystem ["Ecosistema de Agentes Antigravity"]
        LeadAgent["Antigravity Lead Agent (Planificador y Orquestador)"]
        SubagentVehicle["Subagente: vehicle-module-rebuilder"]
        SubagentFeatured["Subagente: featured-packages-developer"]
        SubagentSecurity["Subagente: security-hardening-agent"]
        
        LeadAgent --> SubagentVehicle
        LeadAgent --> SubagentFeatured
        LeadAgent --> SubagentSecurity
    end
    
    subgraph Agent_Skills [".agents/skills/ (Habilidades Especializadas)"]
        SkillNeon["neon (Orquestador de Backend)"]
        SkillPostgres["neon-postgres (Pooling, Branching, Egress)"]
        SkillAIGateway["neon-ai-gateway (Proxy LLMs & Embeddings)"]
        SkillFunctions["neon-functions (Cómputo Serverless)"]
        SkillStorage["neon-object-storage (S3-Compatible)"]
        SkillRealtime["neon-realtime (Consultas Reactivas en Vivo)"]
    end
    
    LeadAgent --> Agent_Skills
```

### Agentes Especializados de Desarrollo
* **`Antigravity Lead Agent`:** Agente autónomo principal encargado de la planificación, ejecución en sandbox, análisis reactivo, compilación continua y despliegue automatizado.
* **`vehicle-module-rebuilder`:** Subagente especializado responsable de la reingeniería completa del módulo de flota vehicular, control normativo MTC (SOAT, CITV, TUC), asignación de unidades insignias (`IsFlagship`) y desarrollo del visor Lightbox en pantalla completa.
* **`featured-packages-developer`:** Subagente dedicado a la implementación de la lógica comercial de paquetes destacados, badges promocionales, descuentos dinámicos y banners de oferta.
* **`security-hardening-agent`:** Subagente auditor enfocado en la resolución de issues de SonarQube Cloud, eliminación de vulnerabilidades y cumplimiento riguroso de accesibilidad semántica WCAG 2.1 AA.

### Habilidades Autónomas de la Plataforma (`.agents/skills/`)
El repositorio incluye habilidades listas para que agentes de IA operen sobre la infraestructura del proyecto:

| Habilidad (Skill) | Ruta en el Repositorio | Capacidades Autónomas |
| :--- | :--- | :--- |
| **`neon`** | `.agents/skills/neon/` | Orquestador integral de primitivas cloud para Lakebase Postgres (bases de datos efímeras, ramas y configuración). |
| **`neon-postgres`** | `.agents/skills/neon-postgres/` | Optimización de conexiones agrupadas (pooled vs direct), branching por PR, mitigación de sobrecostos de transferencia (egress) y afinamiento de índices. |
| **`neon-ai-gateway`** | `.agents/skills/neon-ai-gateway/` | Enrutamiento unificado para modelos de frontera (Gemini, Claude, GPT) con una sola credencial y gestión de embeddings. |
| **`neon-functions`** | `.agents/skills/neon-functions/` | Ejecución de funciones serverless Node.js de larga duración desplegadas junto a los datos. |
| **`neon-object-storage`** | `.agents/skills/neon-object-storage/` | Almacenamiento de objetos compatible con S3 que se bifurca automáticamente con las ramas de base de datos. |
| **`neon-realtime`** | `.agents/skills/neon-realtime/` | Reactividad y sincronización de consultas SQL en vivo para tableros operacionales sin necesidad de websockets manuales. |
| **`neon-auth`** | `.agents/skills/neon-auth/` | Primitivas para gestión de identidades y enrutamiento seguro de sesiones. |

---

## ⚙️ 7. Configuración y Puesta en Marcha Local

### Prerrequisitos
* [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) instalado.
* [PostgreSQL](https://www.postgresql.org/) v14 o superior (o una base de datos en Neon/Cloud).
* Git instalado.

### Pasos de Instalación
1. **Clonar el repositorio:**
   ```bash
   git clone https://github.com/N4TSUg/SIGETOUR.git
   cd SIGETOUR
   ```

2. **Configurar la base de datos:**
   En `SIGETOUR.API/appsettings.Development.json` (o en un archivo `.env` en la raíz), configura tu conexión:
   ```json
   {
     "ConnectionStrings": {
       "DefaultConnection": "Host=localhost;Port=5432;Database=sigetour_db;Username=postgres;Password=tu_password"
     }
   }
   ```

3. **Restaurar dependencias y compilar:**
   ```bash
   dotnet restore
   dotnet build
   ```

4. **Ejecutar la suite de pruebas unitarias:**
   ```bash
   dotnet test SIGETOUR.Tests/SIGETOUR.Tests.csproj
   ```
   *(Verificar que se ejecuten las 171 pruebas con 0 errores).*

5. **Iniciar la aplicación:**
   ```bash
   dotnet run --project SIGETOUR.API/SIGETOUR.API.csproj
   ```
   Abre tu navegador en `https://localhost:7103` o `http://localhost:5047`.  
   *Las tablas de la base de datos y los usuarios se sembrarán automáticamente al iniciar.*

---

## 🌐 8. Despliegue en Producción (Render)

El servicio está configurado para integración y despliegue continuo (CI/CD) automático desde la rama `main` en **Render**:

* **Entorno:** Web Service Linux (.NET 10).
* **Build Command:**
  ```bash
  cd SIGETOUR && dotnet publish SIGETOUR.API/SIGETOUR.API.csproj -c Release -o out
  ```
* **Start Command:**
  ```bash
  cd SIGETOUR/out && ./SIGETOUR.API
  ```

### Matriz Completa de Variables de Entorno (.env / Render)

A continuación se detalla la nómina completa de variables de entorno requeridas por la aplicación, organizadas por módulo funcional. Todos los valores sensibles están enmascarados con marcadores para proteger las credenciales:

| Categoría | Variable | Descripción | Valor / Formato de Ejemplo |
| :--- | :--- | :--- | :--- |
| **Servidor** | `ASPNETCORE_ENVIRONMENT` | Entorno de ejecución de ASP.NET Core | `Production` / `Development` |
| **Servidor** | `PORT` | Puerto HTTP dinámico del contenedor | `80` (o asignado por Render) |
| **Base de Datos** | `DATABASE_URL` | Cadena de conexión URI a PostgreSQL | `postgresql://<user>:<password>@<host>/<database>?sslmode=require` |
| **Seguridad** | `Jwt__Key` | Clave secreta para firma de tokens JWT | `<clave_secreta_jwt_minimo_256_bits>` |
| **Seguridad** | `Payments__CheckoutSigningKey` | Llave HMAC para firma de checkout | `<clave_secreta_checkout_base64_32bytes>` |
| **Pagos Culqi** | `Culqi__PublicKey` | Llave pública de integración Culqi | `pk_test_<tu_public_key>` |
| **Pagos Culqi** | `Culqi__SecretKey` | Llave privada secreta de Culqi | `sk_test_<tu_secret_key>` |
| **Pagos Culqi** | `Culqi__WebhookUser` | Usuario para autenticación de Webhook | `<usuario_webhook_culqi>` |
| **Pagos Culqi** | `Culqi__WebhookPassword` | Contraseña para autenticación de Webhook | `<password_webhook_culqi>` |
| **Facturación** | `Nubefact__Ruta` | Endpoint API del PSE Nubefact | `https://api.nubefact.com/api/v1/<tu_identificador>` |
| **Facturación** | `Nubefact__Token` | Token de autorización de Nubefact | `<tu_token_autorizacion_nubefact>` |
| **Facturación** | `Nubefact__Mode` | Modo de facturación SUNAT | `Demo` / `Production` |
| **Facturación** | `Nubefact__BoletaSeries` | Serie oficial para Boletas Electrónicas | `BBB1` |
| **Facturación** | `Nubefact__FacturaSeries` | Serie oficial para Facturas Electrónicas | `FFF1` |
| **Multimedia** | `Cloudinary__CloudName` | Nombre de cuenta Cloudinary | `<tu_cloud_name>` |
| **Multimedia** | `Cloudinary__ApiKey` | Clave API pública de Cloudinary | `<tu_api_key>` |
| **Multimedia** | `Cloudinary__ApiSecret` | Clave API secreta de Cloudinary | `<tu_api_secret>` |
| **Multimedia** | `CLOUDINARY_URL` *(Opcional)* | Conexión en una sola línea | `cloudinary://<api_key>:<api_secret>@<cloud_name>` |
| **Correos SMTP** | `SMTP_HOST` | Servidor SMTP para envío de vouchers | `smtp.gmail.com` |
| **Correos SMTP** | `SMTP_PORT` | Puerto de conexión SMTP TLS | `587` |
| **Correos SMTP** | `SMTP_USER` | Usuario o correo remitente SMTP | `<tu_correo_emisor@gmail.com>` |
| **Correos SMTP** | `SMTP_PASSWORD` | Contraseña de aplicación SMTP | `<tu_contraseña_aplicacion_smtp>` |
| **Correos SMTP** | `SMTP_ENABLE_SSL` | Habilitación de cifrado SSL/TLS | `true` |
| **Correos SMTP** | `SMTP_FROM_NAME` | Nombre visible del remitente | `"Turismo Chilón Cajamarca"` |
| **Correos Brevo** | `BREVO_API_KEY` | Llave API para transaccional Brevo | `<tu_api_key_de_brevo>` |
| **Empresa** | `Company__TradeName` | Nombre comercial de la agencia | `"TURISMO CHILÓN"` |
| **Empresa** | `Company__LegalName` | Razón social oficial | `"TRANSPORTE & TURISMO CAJAMARCA S.A.C."` |
| **Empresa** | `Company__Ruc` | Registro Único de Contribuyente | `20601248901` |
| **Empresa** | `Company__FiscalAddress` | Domicilio fiscal y base operativa | `"Jr. 2 de Mayo 569, Cajamarca - Perú"` |
| **Empresa** | `Company__Phone` | Central telefónica de atención | `"+51 928 876 259 / +51 944 746 364"` |
| **Empresa** | `Company__WhatsApp` | Número WhatsApp principal | `928876259` |
| **Empresa** | `Company__WhatsAppSecondary` | Número WhatsApp de soporte | `944746364` |
| **Empresa** | `Company__Email` | Correo corporativo institucional | `informes@turismochilon.com` |

---

### Plantilla de Archivo `.env` (Para Entorno Local / Desarrollo)

Puedes crear un archivo `.env` o `.env.local` en la raíz del proyecto copiando esta estructura y completando los valores entre `<...>`:

```ini
# --- ENTORNO Y HOSTING ---
ASPNETCORE_ENVIRONMENT=Production

# --- BASE DE DATOS POSTGRESQL ---
DATABASE_URL="postgresql://<db_user>:<db_password>@<db_host>/<db_name>?sslmode=require"

# --- SEGURIDAD Y TOKENS ---
Jwt__Key="<clave_secreta_jwt_min_256_bits>"
Payments__CheckoutSigningKey="<clave_firma_hmac_checkout_32bytes>"

# --- PASARELA DE PAGOS CULQI ---
Culqi__PublicKey="pk_test_<tu_public_key>"
Culqi__SecretKey="sk_test_<tu_secret_key>"
Culqi__WebhookUser="<usuario_webhook_culqi>"
Culqi__WebhookPassword="<password_webhook_culqi>"

# --- FACTURACIÓN ELECTRÓNICA SUNAT (NUBEFACT) ---
Nubefact__Ruta="https://api.nubefact.com/api/v1/<tu_identificador>"
Nubefact__Token="<tu_token_autorizacion_nubefact>"
Nubefact__Mode="Demo"
Nubefact__BoletaSeries="BBB1"
Nubefact__FacturaSeries="FFF1"

# --- ALMACENAMIENTO MULTIMEDIA (CLOUDINARY) ---
Cloudinary__CloudName="<tu_cloud_name>"
Cloudinary__ApiKey="<tu_api_key>"
Cloudinary__ApiSecret="<tu_api_secret>"

# --- NOTIFICACIONES Y CORREOS (SMTP / GMAIL / BREVO) ---
SMTP_HOST="smtp.gmail.com"
SMTP_PORT=587
SMTP_USER="<tu_correo_emisor@gmail.com>"
SMTP_PASSWORD="<tu_contraseña_aplicacion_smtp>"
SMTP_ENABLE_SSL=true
SMTP_FROM_NAME="Turismo Chilón Cajamarca"
BREVO_API_KEY="<tu_api_key_de_brevo>"

# --- IDENTIDAD INSTITUCIONAL (TURISMO CHILÓN S.A.C.) ---
Company__TradeName="TURISMO CHILÓN"
Company__LegalName="TRANSPORTE & TURISMO CAJAMARCA S.A.C."
Company__Ruc="20601248901"
Company__FiscalAddress="Jr. 2 de Mayo 569, Cajamarca - Perú"
Company__Phone="+51 928 876 259 / +51 944 746 364"
Company__WhatsApp="928876259"
Company__WhatsAppSecondary="944746364"
Company__Email="informes@turismochilon.com"
```

---

## 📸 9. Banco de Imágenes Oficiales (Recursos de Gerencia)

Carpetas oficiales facilitadas por la **Gerencia de Turismo Chilón S.A.C.** para la alimentación fotográfica del catálogo:

1. 🌲 **Granja Porcón:** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/1TuNSSZLvPQlfJ7igR9T0TO_moiK2Vent)
2. 🎸 **Namora (Guitarras y Laguna San Nicolás):** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/1htID0HCeG4kGQoMv1KxF-8qyeTplrWOQ)
3. 🏰 **Castillo de Chancay / Alrededores:** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/1LQ2gorrwzbYdFOGAE00Ots9aQ7gBBECQ)
4. ♨️ **Yumagual (Aguas Termomedicinales):** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/1hUdgCg6Xr_2sl1ULMB8cY_LESFa5BWGv)
5. 👑 **Baños del Inca (Complejo Termal Histórico):** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/15e4IdtH_wQdJtxMyC92zoUvXd4qx_DGS)
6. 🏔️ **Lagunas de Alto Perú:** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/13vp35CtrAfWhom2fVmaQnBI_PfyP3_rC)
7. ⛪ **Polloc (Santuario Virgen del Rosario):** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/1rgW1OBWHTJzp5EvkdUiXk5SAYCmf01_e)

---

## 📋 10. Resumen de Circuitos Comerciales Oficiales

| N° | Circuito / Tour | Subtítulo / Badge | Horario / Turno | Tarifa Sugerida | Atractivos Principales |
| :-: | :--- | :--- | :--- | :-: | :--- |
| **1** | **Ventanillas de Otuzco** | Cementerio pre-inca | 3:30 PM – 6:30 PM | S/ 35.00 | Ventanillas pre-incas, Jardín de Hortensias, Artesanía, Fundo Tres Molinos. |
| **2** | **Collpa y Llacanora** | Llamado de vacas | 3:30 PM – 7:00 PM | S/ 35.00 | Ex-hacienda La Collpa, laguna artificial, establo, degustación de lácteos, cascadas. |
| **3** | **Castillo de Yanamarca** | Estilo medieval | Mañana / Tarde | S/ 40.00 | Castillo medieval, esculturas en piedra/madera, vistas panorámicas. |
| **4** | **City Tour Peatonal** | Tradición e historia | 10:00 AM / 3:30 PM | S/ 25.00 | Plaza de Armas, Cuarto del Rescate, Catedral, San Francisco, Belén, Santa Apolonia. |
| **5** | **Namora y San Nicolás** | Guitarras y totora | 9:30 AM – 3:00 PM | S/ 45.00 | Talleres de guitarras en Namora, Laguna San Nicolás, botes de totora, gastronomía. |
| **6** | **Cumbe Mayo** | 3ra. Maravilla del Perú | 9:30 AM – 2:00 PM | S/ 40.00 | Mirador Bellavista, Frailones, piedra de sacrificios, acueducto milenario (3,000 años). |
| **7** | **Granja de Porcón** | La Suiza peruana | 9:30 AM – 2:00 PM | S/ 40.00 | Huambocancha, bosque de pinos, zoológico andino (otorongos, pumas, osos), textilería. |

---

## 📄 11. Licencia y Derechos de Autor
Desarrollado para **Turismo Chilón S.A.C.** como parte del Proyecto de Tesis / Capstone de Ingeniería de Software.  
Todos los derechos reservados © 2025-2026.
