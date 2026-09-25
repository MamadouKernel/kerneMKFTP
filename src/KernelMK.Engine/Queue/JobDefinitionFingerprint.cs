using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KernelMK.Core.Entities;

namespace KernelMK.Engine.Queue;

/// <summary>Stable fingerprint of execution-affecting settings; notifications and display metadata are excluded.</summary>
public static class JobDefinitionFingerprint
{
    public static string Compute(Job job)
    {
        var definition = new
        {
            job.TimeoutSeconds,
            job.MaxRetries,
            job.RetryDelaySeconds,
            job.AllowConcurrentExecution,
            job.ExecutionAccount,
            Steps = job.Steps.OrderBy(step => step.Order).ThenBy(step => step.Id).Select(step => new
            {
                step.Id,
                step.Order,
                step.Type,
                step.ConfigJson,
                step.CredentialId,
                step.TimeoutSeconds,
                step.MaxRetries,
                step.RetryDelaySeconds,
                step.OnErrorAction,
                step.OnSuccessGoToOrder,
                step.OnFailureGoToOrder
            })
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(definition))));
    }
}
