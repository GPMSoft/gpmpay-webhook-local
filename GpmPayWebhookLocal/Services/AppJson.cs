using System.Text.Json.Serialization;

namespace GpmPayWebhookLocal.Services;

/// <summary>JSON source generator (bắt buộc cho Native AOT — không dùng reflection).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<TxRecord>))]
[JsonSerializable(typeof(WebhookPayload))]
internal sealed partial class AppJson : JsonSerializerContext;

/// <summary>Body gửi lên REST API GPM Pay (camelCase).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CreateWebhookRequest))]
[JsonSerializable(typeof(UpdateWebhookRequest))]
internal sealed partial class ApiJson : JsonSerializerContext;
