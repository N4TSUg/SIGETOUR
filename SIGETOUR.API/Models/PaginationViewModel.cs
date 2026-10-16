using System;
using System.Collections.Generic;

namespace SIGETOUR.API.Models
{
    public class PaginationViewModel
    {
        public string Action { get; set; } = string.Empty;
        public string Controller { get; set; } = "Admin";
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
        public int TotalItems { get; set; } = 0;
        public int PageSize { get; set; } = 12;
        public Dictionary<string, string> RouteParams { get; set; } = new();

        public int FromItem => TotalItems == 0 ? 0 : ((CurrentPage - 1) * PageSize) + 1;
        public int ToItem => Math.Min(CurrentPage * PageSize, TotalItems);
        public bool HasPrevious => CurrentPage > 1;
        public bool HasNext => CurrentPage < TotalPages;
    }
}
