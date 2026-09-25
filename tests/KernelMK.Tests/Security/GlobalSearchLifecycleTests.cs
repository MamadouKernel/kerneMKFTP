using System.Reflection;
using KernelMK.Web.Components.Shared;
using Microsoft.JSInterop;

namespace KernelMK.Tests.Security;

public sealed class GlobalSearchLifecycleTests
{
    [Fact]
    public async Task PrerenderedSearchDoesNotCallJavascriptOnDisposal()
    {
        var js = new RecordingJsRuntime { Prerendering = true };
        var component = CreateComponent(js);

        await component.DisposeAsync();

        Assert.Empty(js.Calls);
    }

    [Fact]
    public async Task InteractiveSearchUnregistersOnceAfterRegistration()
    {
        var js = new RecordingJsRuntime();
        var component = CreateComponent(js);
        await component.AfterRenderAsync();

        await component.DisposeAsync();
        await component.DisposeAsync();

        Assert.Equal(["kernelMK.globalSearch.register", "kernelMK.globalSearch.unregister"], js.Calls);
    }

    [Fact]
    public async Task DisposalWaitsForPendingRegistrationBeforeUnregistering()
    {
        var registration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var js = new RecordingJsRuntime { Registration = registration.Task };
        var component = CreateComponent(js);
        var render = component.AfterRenderAsync();

        var dispose = component.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        registration.SetResult();
        await Task.WhenAll(render, dispose);

        Assert.Equal(["kernelMK.globalSearch.register", "kernelMK.globalSearch.unregister"], js.Calls);
    }

    [Fact]
    public async Task DisconnectedCircuitCanStillDisposeSearch()
    {
        var js = new RecordingJsRuntime { DisconnectOnUnregister = true };
        var component = CreateComponent(js);
        await component.AfterRenderAsync();

        await component.DisposeAsync();

        Assert.Equal(2, js.Calls.Count);
    }

    private static TestSearch CreateComponent(IJSRuntime js)
    {
        var component = new TestSearch();
        typeof(GlobalSearch).GetProperty("JS", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, js);
        return component;
    }

    private sealed class TestSearch : GlobalSearch
    {
        public Task AfterRenderAsync() => OnAfterRenderAsync(firstRender: true);
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public List<string> Calls { get; } = [];
        public bool Prerendering { get; init; }
        public bool DisconnectOnUnregister { get; init; }
        public Task? Registration { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add(identifier);
            if (Prerendering)
                throw new InvalidOperationException("JavaScript interop is unavailable during static rendering.");
            if (identifier == "kernelMK.globalSearch.register" && Registration is not null)
                await Registration.WaitAsync(cancellationToken);
            if (identifier == "kernelMK.globalSearch.unregister" && DisconnectOnUnregister)
                throw new JSDisconnectedException("The circuit disconnected.");
            return default!;
        }
    }
}
