namespace SIGETOUR.API.Application.Options
{
    public class CloudinaryOptions
    {
        public string? CloudName { get; set; }
        public string? ApiKey { get; set; }
        public string? ApiSecret { get; set; }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(CloudName) &&
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(ApiSecret);
    }
}
