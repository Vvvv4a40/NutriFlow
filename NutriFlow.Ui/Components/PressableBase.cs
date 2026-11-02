using Microsoft.AspNetCore.Components;

namespace NutriFlow.Ui.Components;

// a second tap while the first action is still running would open the same form twice
public abstract class PressableBase : ComponentBase
{
    private bool _running;

    [Parameter]
    public EventCallback OnClick { get; set; }

    [Parameter]
    public string? TestId { get; set; }

    protected async Task ClickAsync()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        try
        {
            await OnClick.InvokeAsync();
        }
        finally
        {
            _running = false;
        }
    }

    internal static string ToneClass(Tone tone) => tone switch
    {
        Tone.Accent => "nf-tone-accent",
        Tone.Muted => "nf-tone-muted",
        Tone.Success => "nf-tone-success",
        Tone.Warning => "nf-tone-warning",
        Tone.Danger => "nf-tone-danger",
        Tone.Protein => "nf-tone-protein",
        Tone.Fat => "nf-tone-fat",
        Tone.Carbs => "nf-tone-carbs",
        _ => throw new ArgumentOutOfRangeException(nameof(tone))
    };
}
