using NutriFlow.Infrastructure.LabelPhotos;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Hosting;
using NutriFlow.Ui.Pages;

namespace NutriFlow.Ui.Catalog;

public sealed record LabelReading(LabelCapture Capture, PickedPhoto Photo);

// a photo becomes a draft of a product and a file of the profile; the file reaches durable storage in the same step,
// otherwise the database would point at a photo that a closed tab never wrote
public sealed class LabelReader(LocalMealClient meals, IProfileSettings settings, IAppHost host)
{
    public async Task<LabelReading> ReadAsync(Guid profileId, PickedPhoto photo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        try
        {
            LabelPhotoValidator.Validate(photo.Content, photo.MediaType);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(LabelText.UnreadablePhoto, exception);
        }

        GroqSettings groq = await settings.GetGroqAsync(profileId);
        LabelCapture capture = await meals.ReadLabelAsync(profileId, photo.Content, photo.MediaType, groq, cancellationToken);
        await host.SaveProfileFilesAsync();
        return new LabelReading(capture, photo);
    }
}
