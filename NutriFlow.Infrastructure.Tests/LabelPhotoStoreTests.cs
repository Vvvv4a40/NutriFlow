using System.Buffers;
using NutriFlow.Infrastructure.LabelPhotos;

namespace NutriFlow.Infrastructure.Tests;

public sealed class LabelPhotoStoreTests
{
    [Fact]
    public async Task SaveAsync_WhenWritingFails_RemovesOnlyCreatedFile()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"nutriflow-labels-{Guid.NewGuid():N}");

        try
        {
            LabelPhotoStore store = new(directoryPath);
            string existingFilePath = Path.Combine(directoryPath, "existing.jpg");
            byte[] existingContent = { 0xFF, 0xD8, 0xFF, 0x00 };
            await File.WriteAllBytesAsync(existingFilePath, existingContent);
            using UnreadablePhotoMemory memory = new();
            ValidatedLabelPhoto photo = new(memory.Content, "image/jpeg", ".jpg");

            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(photo));

            Assert.Equal(existingFilePath, Assert.Single(Directory.GetFiles(directoryPath)));
            Assert.Equal(existingContent, await File.ReadAllBytesAsync(existingFilePath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_WithCancelledRequest_DoesNotCreateFile()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"nutriflow-labels-{Guid.NewGuid():N}");

        try
        {
            LabelPhotoStore store = new(directoryPath);
            ValidatedLabelPhoto photo = new(
                new byte[] { 0xFF, 0xD8, 0xFF, 0x00 },
                "image/jpeg",
                ".jpg");
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.SaveAsync(photo, cancellation.Token));

            Assert.Empty(Directory.GetFiles(directoryPath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

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

    private sealed class UnreadablePhotoMemory : MemoryManager<byte>
    {
        public ReadOnlyMemory<byte> Content => CreateMemory(4);

        public override Span<byte> GetSpan() => throw new IOException("Photo content is unavailable.");

        public override MemoryHandle Pin(int elementIndex = 0) =>
            throw new IOException("Photo content is unavailable.");

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
