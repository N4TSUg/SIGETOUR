using Microsoft.AspNetCore.Http;

namespace SIGETOUR.API.Application.Interfaces
{
    public interface IVisitorCounterService
    {
        void TrackVisit(HttpContext context);
        (long TotalVisits, long TodayVisits) GetStats();
    }
}
