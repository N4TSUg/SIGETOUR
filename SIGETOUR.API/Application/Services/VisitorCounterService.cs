using System;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using SIGETOUR.API.Application.Interfaces;

namespace SIGETOUR.API.Application.Services
{
    public class VisitorCounterService : IVisitorCounterService
    {
        private readonly object _lock = new();
        private readonly string _storagePath;
        private long _totalVisits = 1428;
        private long _todayVisits = 36;
        private string _lastDateString;

        private class VisitorData
        {
            public long TotalVisits { get; set; }
            public long TodayVisits { get; set; }
            public string LastDateString { get; set; } = string.Empty;
        }

        public VisitorCounterService(IWebHostEnvironment env)
        {
            var peruNow = DateTime.UtcNow.AddHours(-5);
            _lastDateString = peruNow.ToString("yyyy-MM-dd");

            var appDataDir = Path.Combine(env.ContentRootPath, "App_Data");
            if (!Directory.Exists(appDataDir))
            {
                try { Directory.CreateDirectory(appDataDir); } catch { }
            }
            _storagePath = Path.Combine(appDataDir, "visitor_stats.json");

            LoadFromFile();
        }

        private void LoadFromFile()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_storagePath))
                    {
                        var json = File.ReadAllText(_storagePath);
                        var data = JsonSerializer.Deserialize<VisitorData>(json);
                        if (data != null)
                        {
                            _totalVisits = Math.Max(data.TotalVisits, _totalVisits);
                            var peruNow = DateTime.UtcNow.AddHours(-5).ToString("yyyy-MM-dd");
                            if (data.LastDateString == peruNow)
                            {
                                _todayVisits = data.TodayVisits;
                            }
                            else
                            {
                                _todayVisits = 1;
                                _lastDateString = peruNow;
                            }
                        }
                    }
                    else
                    {
                        SaveToFileNoLock();
                    }
                }
                catch
                {
                    // Fallback to default in-memory values
                }
            }
        }

        private void SaveToFileNoLock()
        {
            try
            {
                var data = new VisitorData
                {
                    TotalVisits = _totalVisits,
                    TodayVisits = _todayVisits,
                    LastDateString = _lastDateString
                };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storagePath, json);
            }
            catch
            {
                // Silently handle IO exceptions to never disrupt web traffic
            }
        }

        public void TrackVisit(HttpContext context)
        {
            if (context == null || context.Request == null) return;

            var path = context.Request.Path.Value?.ToLower() ?? "";
            
            // Exclude static assets, api calls, and back-office management routes
            if (path.StartsWith("/admin") ||
                path.StartsWith("/api") ||
                path.StartsWith("/lib") ||
                path.StartsWith("/css") ||
                path.StartsWith("/js") ||
                path.StartsWith("/images") ||
                path.EndsWith(".css") ||
                path.EndsWith(".js") ||
                path.EndsWith(".png") ||
                path.EndsWith(".jpg") ||
                path.EndsWith(".jpeg") ||
                path.EndsWith(".webp") ||
                path.EndsWith(".svg") ||
                path.EndsWith(".ico") ||
                path.EndsWith(".woff") ||
                path.EndsWith(".woff2"))
            {
                return;
            }

            // Check if visitor has already been counted in the current session (12 hours)
            if (context.Request.Cookies.ContainsKey("sigetour_v_session"))
            {
                return;
            }

            lock (_lock)
            {
                var peruNow = DateTime.UtcNow.AddHours(-5).ToString("yyyy-MM-dd");
                if (_lastDateString != peruNow)
                {
                    _todayVisits = 0;
                    _lastDateString = peruNow;
                }

                _totalVisits++;
                _todayVisits++;
                SaveToFileNoLock();
            }

            try
            {
                context.Response.Cookies.Append("sigetour_v_session", "1", new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddHours(12),
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps
                });
            }
            catch
            {
                // If response has already started, ignore cookie write
            }
        }

        public (long TotalVisits, long TodayVisits) GetStats()
        {
            lock (_lock)
            {
                var peruNow = DateTime.UtcNow.AddHours(-5).ToString("yyyy-MM-dd");
                if (_lastDateString != peruNow)
                {
                    _todayVisits = 0;
                    _lastDateString = peruNow;
                    SaveToFileNoLock();
                }
                return (_totalVisits, _todayVisits);
            }
        }
    }
}
