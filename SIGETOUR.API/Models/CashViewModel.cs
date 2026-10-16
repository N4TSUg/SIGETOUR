using System;
using System.Collections.Generic;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Models
{
    public class CashViewModel
    {
        public string ActiveTab { get; set; } = "general"; // "general" o "petty"
        
        // Caja General
        public CashRegisterShift? ActiveShift { get; set; }
        public List<CashRegisterShift> RecentShifts { get; set; } = new();
        public List<CashTransaction> PagedTransactions { get; set; } = new();
        
        // Caja Chica
        public PettyCashAccount? PettyAccount { get; set; }
        public List<PettyCashMovement> PagedMovements { get; set; } = new();
        
        // Paginación activa para la tabla visible
        public PaginationViewModel Pagination { get; set; } = new();

        // Métricas Rápidas
        public decimal TotalGeneralIncome { get; set; }
        public decimal TotalGeneralExpense { get; set; }
        public decimal GeneralEffectiveBalance { get; set; }
        public decimal PettyBalance { get; set; }
        public decimal PettySpent { get; set; }
        public decimal PettyFund { get; set; }

        // Filtros actuales
        public string? SearchTerm { get; set; }
        public string? FilterType { get; set; }
        public string? FilterCategory { get; set; }
        public string? FilterPaymentMethod { get; set; }
    }
}
