using NutriFlow.Infrastructure.LabelPhotos;

namespace NutriFlow.Infrastructure.Tests;

public sealed class LabelPhotoStoreTests
{
    [Fact]
    public async Task SaveAsync_UsesGeneratedReferenceAndPersistsContent()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"nutriflow-labels-{Guid.NewGuid():N}");

        try
        {
            LabelPhotoStore store = new LabelPhotoStore(directoryPath);
            byte[] content = { 0xFF, 0xD8, 0xFF, 0x00 };
            ValidatedLabelPhoto photo = new ValidatedLabelPhoto(
                content,
                "image/jpeg",
                ".jpg");

            string reference = await store.SaveAsync(photo);

            Assert.StartsWith("label-photo:", reference);
            Assert.EndsWith(".jpg", reference);
            Assert.True(store.Contains(reference));
            Assert.False(store.Contains("label-photo:../secret.jpg"));
            Assert.Equal(
                content,
                await File.ReadAllBytesAsync(
                    Assert.Single(Directory.GetFiles(directoryPath))));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
