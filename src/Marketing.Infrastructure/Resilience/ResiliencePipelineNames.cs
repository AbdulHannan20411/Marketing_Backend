namespace Marketing.Infrastructure.Resilience;

/// <summary>Keys under which resilience pipelines are registered.</summary>
public static class ResiliencePipelineNames
{
    /// <summary>Pipeline guarding calls to the Meta WhatsApp Cloud API.</summary>
    public const string WhatsAppCloudApi = "resilience:whatsapp-cloud-api";

    /// <summary>Pipeline guarding non-HTTP dependencies such as blob storage.</summary>
    public const string GeneralOutbound = "resilience:general-outbound";
}
