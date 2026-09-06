using NutriFlow.Infrastructure.LabelPhotos;

namespace NutriFlow.Infrastructure.Tests;

public sealed class LabelPhotoValidatorTests
{
    [Fact]
    public void Validate_WithPngSignature_ReturnsDetectedFormat()
    {
        byte[] content =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00
        };

        ValidatedLabelPhoto photo = LabelPhotoValidator.Validate(
            content,
            "image/png");

        Assert.Equal("image/png", photo.MediaType);
        Assert.Equal(".png", photo.FileExtension);
    }

    [Fact]
    public void Validate_WithJpegSignature_ReturnsDetectedFormat()
    {
        byte[] content = { 0xFF, 0xD8, 0xFF, 0x00 };

        ValidatedLabelPhoto photo = LabelPhotoValidator.Validate(
            content,
            "image/jpeg");

        Assert.Equal("image/jpeg", photo.MediaType);
        Assert.Equal(".jpg", photo.FileExtension);
    }

    [Fact]
    public void Validate_WithWebPHeader_ReturnsDetectedFormat()
    {
        byte[] content = "RIFF0000WEBP"u8.ToArray();

        ValidatedLabelPhoto photo = LabelPhotoValidator.Validate(
            content,
            "image/webp");

        Assert.Equal("image/webp", photo.MediaType);
        Assert.Equal(".webp", photo.FileExtension);
    }

    [Fact]
    public void Validate_WhenMediaTypeDoesNotMatchContent_ThrowsInvalidDataException()
    {
        byte[] content = { 0xFF, 0xD8, 0xFF, 0x00 };

        Assert.Throws<InvalidDataException>(
            () => LabelPhotoValidator.Validate(content, "image/png"));
    }

    [Fact]
    public void Validate_WithUnsupportedContent_ThrowsInvalidDataException()
    {
        byte[] content = "not-an-image"u8.ToArray();

        Assert.Throws<InvalidDataException>(
            () => LabelPhotoValidator.Validate(content, "image/png"));
    }

    [Fact]
    public void Validate_WithOversizedContent_ThrowsInvalidDataException()
    {
        byte[] content = new byte[
            LabelPhotoValidator.MaximumFileSizeInBytes + 1];

        Assert.Throws<InvalidDataException>(
            () => LabelPhotoValidator.Validate(content, "image/png"));
    }
}
