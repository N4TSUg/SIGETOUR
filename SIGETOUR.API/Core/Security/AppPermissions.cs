using System.Collections.Generic;

namespace SIGETOUR.API.Core.Security
{
    public static class AppPermissions
    {
        // Capacidades operativas (Gerente / Admin / Operador)
        public const string ToursView = "Tours.View";
        public const string ToursManage = "Tours.Manage";
        public const string BookingsView = "Bookings.View";
        public const string BookingsManage = "Bookings.Manage";
        public const string CashView = "Cash.View";
        public const string CashManage = "Cash.Manage";
        public const string ReceiptsView = "Receipts.View";
        public const string ReceiptsManage = "Receipts.Manage";
        public const string ManifestView = "Manifest.View";
        public const string ManifestManage = "Manifest.Manage";

        // Capacidades exclusivas de auditoría, control y seguridad (SuperAdmin)
        public const string SupervisionView = "Supervision.View";
        public const string SupervisionManage = "Supervision.Manage";
        public const string UsersManage = "Users.Manage";
        public const string RolesManage = "Roles.Manage";
        public const string IncidentsManage = "Incidents.Manage";

        public static readonly IReadOnlyList<string> AllPermissions = new[]
        {
            ToursView, ToursManage,
            BookingsView, BookingsManage,
            CashView, CashManage,
            ReceiptsView, ReceiptsManage,
            ManifestView, ManifestManage,
            SupervisionView, SupervisionManage,
            UsersManage, RolesManage,
            IncidentsManage
        };

        public static IReadOnlyList<string> GetDefaultPermissionsForRole(string roleName)
        {
            return roleName switch
            {
                "SuperAdmin" => AllPermissions,
                "Admin" => new[]
                {
                    ToursView, ToursManage,
                    BookingsView, BookingsManage,
                    CashView, CashManage,
                    ReceiptsView, ReceiptsManage,
                    ManifestView, ManifestManage
                },
                "Operador" => new[]
                {
                    ToursView,
                    BookingsView, BookingsManage,
                    CashView,
                    ReceiptsView,
                    ManifestView
                },
                _ => System.Array.Empty<string>() // Roles nuevos inician con 0 permisos implícitos
            };
        }
    }
}
