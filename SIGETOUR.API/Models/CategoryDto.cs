using System;
using System.ComponentModel.DataAnnotations;

namespace SIGETOUR.API.Models
{
    public class CategoryDto
    {
        public Guid Id { get; set; }

        [Required(ErrorMessage = "El nombre de la categoría es obligatorio.")]
        [StringLength(100, ErrorMessage = "El nombre no puede exceder los 100 caracteres.")]
        [Display(Name = "Nombre de la Categoría")]
        public string Name { get; set; } = string.Empty;

        [StringLength(300, ErrorMessage = "La descripción no puede exceder los 300 caracteres.")]
        [Display(Name = "Descripción")]
        public string? Description { get; set; }

        [Display(Name = "Icono Material")]
        public string? Icon { get; set; } = "landscape";

        [Display(Name = "Estado")]
        public bool IsActive { get; set; } = true;

        [Range(1, 999, ErrorMessage = "El orden de visualización debe ser un número positivo.")]
        [Display(Name = "Orden")]
        public int DisplayOrder { get; set; } = 1;

        public int AssociatedToursCount { get; set; }
    }
}
