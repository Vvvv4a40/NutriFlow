using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using NutriFlow.Ui.Hosting;

namespace NutriFlow.Web.Services;

public sealed class BrowserMedia(IJSRuntime js)
{
    private Task<IJSObjectReference>? _module;

    public IVoiceRecorder CreateVoiceRecorder() => new WebVoiceRecorder(GetModuleAsync);

    public async Task<PickedPhoto?> ReadPhotoAsync(ElementReference input)
    {
        try
        {
            return await (await GetModuleAsync()).InvokeAsync<PickedPhoto?>("readPhoto", input);
        }
        catch (JSException exception)
        {
            throw new InvalidDataException("The browser could not read the photo.", exception);
        }
    }

    public async Task<PickedBarcode?> ReadBarcodeAsync(ElementReference input)
    {
        try
        {
            return await (await GetModuleAsync()).InvokeAsync<PickedBarcode?>("readBarcode", input);
        }
        catch (JSException exception)
        {
            throw new InvalidDataException("The browser could not read the photo of the barcode.", exception);
        }
    }

    private Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/media.mjs").AsTask();
}
