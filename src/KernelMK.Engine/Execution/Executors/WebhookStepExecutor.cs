using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Appel HTTP/webhook sortant (section 4.2 "Communication" et déclencheurs API).</summary>
public class WebhookStepExecutor : IStepExecutor
{
    private static readonly HttpClient HttpClient = new();

    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[] { StepType.Webhook, StepType.TransfertHttp };

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<WebhookStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration webhook invalide.");

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(config.Method), config.Url);
            if (!string.IsNullOrEmpty(config.BodyJson))
            {
                request.Content = new StringContent(config.BodyJson, Encoding.UTF8, "application/json");
            }
            if (config.Headers is not null)
            {
                foreach (var (key, value) in config.Headers)
                {
                    request.Headers.TryAddWithoutValidation(key, value);
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

            var response = await HttpClient.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(context.CancellationToken);

            return response.IsSuccessStatusCode
                ? StepExecutionResult.Ok(body, (int)response.StatusCode)
                : StepExecutionResult.Fail($"HTTP {(int)response.StatusCode} : {body}", (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            return StepExecutionResult.Fail(ex.Message);
        }
    }
}
