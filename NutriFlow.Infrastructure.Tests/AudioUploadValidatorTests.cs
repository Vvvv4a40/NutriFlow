using System.Buffers.Binary;
using NutriFlow.Infrastructure.Audio;

namespace NutriFlow.Infrastructure.Tests;

public sealed class AudioUploadValidatorTests
{
    [Theory]
    [MemberData(nameof(SupportedFormats))]
    public void Validate_WithSupportedHeader_ReturnsCanonicalFormatAndOriginalContent(
        byte[] content,
        string mediaType,
        string extension)
    {
        ValidatedAudio audio = AudioUploadValidator.Validate(content, mediaType);

        Assert.Equal(mediaType, audio.MediaType);
        Assert.Equal(extension, audio.FileExtension);
        Assert.True(audio.Content.Span.SequenceEqual(content));
    }

    [Theory]
    [InlineData("audio/x-wav", "audio/wav")]
    [InlineData("audio/wave", "audio/wav")]
    [InlineData("audio/vnd.wave", "audio/wav")]
    [InlineData(" AUDIO/WAV ; codecs=pcm", "audio/wav")]
    [InlineData("audio/mp3", "audio/mpeg")]
    [InlineData("audio/x-mp3", "audio/mpeg")]
    [InlineData("audio/mpeg3", "audio/mpeg")]
    [InlineData("audio/x-mpeg", "audio/mpeg")]
    [InlineData("audio/webm;codecs=opus", "audio/webm")]
    [InlineData("video/webm", "audio/webm")]
    [InlineData("audio/m4a", "audio/mp4")]
    [InlineData("audio/x-m4a", "audio/mp4")]
    [InlineData("video/mp4", "audio/mp4")]
    [InlineData("audio/mp4; codecs=mp4a.40.2", "audio/mp4")]
    [InlineData("audio/x-ogg", "audio/ogg")]
    [InlineData("application/ogg", "audio/ogg")]
    [InlineData("audio/ogg;codecs=opus", "audio/ogg")]
    public void Validate_WithAliasOrParameters_ReturnsCanonicalMediaType(
        string declaredMediaType,
        string canonicalMediaType)
    {
        byte[] content = GetContent(canonicalMediaType);

        ValidatedAudio audio = AudioUploadValidator.Validate(content, declaredMediaType);

        Assert.Equal(canonicalMediaType, audio.MediaType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("application/octet-stream")]
    [InlineData("Application/Octet-Stream; charset=binary")]
    public void Validate_WithoutSpecificMediaType_UsesEachDetectedFormat(
        string? declaredMediaType)
    {
        foreach (object[] format in SupportedFormats())
        {
            byte[] content = (byte[])format[0];
            string mediaType = (string)format[1];

            ValidatedAudio audio = AudioUploadValidator.Validate(content, declaredMediaType);

            Assert.Equal(mediaType, audio.MediaType);
        }
    }

    [Theory]
    [InlineData("audio/mpeg")]
    [InlineData("audio/webm")]
    [InlineData("image/png")]
    [InlineData("text/plain")]
    [InlineData("audio/unknown")]
    public void Validate_WhenMediaTypeDoesNotMatchContent_ThrowsInvalidDataException(
        string declaredMediaType)
    {
        Assert.Throws<InvalidDataException>(
            () => AudioUploadValidator.Validate(GetContent("audio/wav"), declaredMediaType));
    }

    [Theory]
    [MemberData(nameof(InvalidHeaders))]
    public void Validate_WithEmptyTruncatedOrUnsupportedHeader_ThrowsInvalidDataException(
        byte[] content)
    {
        Assert.Throws<InvalidDataException>(
            () => AudioUploadValidator.Validate(content, null));
    }

    [Theory]
    [InlineData(0xF9, 0x90)]
    [InlineData(0xEB, 0x90)]
    [InlineData(0xFB, 0xF0)]
    [InlineData(0xFB, 0x9C)]
    public void Validate_WithReservedMpegHeaderValues_ThrowsInvalidDataException(
        byte secondByte,
        byte thirdByte)
    {
        byte[] content = { 0xFF, secondByte, thirdByte, 0x64, 0x00 };

        Assert.Throws<InvalidDataException>(
            () => AudioUploadValidator.Validate(content, "audio/mpeg"));
    }

    [Fact]
    public void Validate_WithMpegAudioWithoutId3_ReturnsMp3()
    {
        byte[] content = { 0xFF, 0xFB, 0x90, 0x64, 0x00 };

        ValidatedAudio audio = AudioUploadValidator.Validate(content, "audio/mpeg");

        Assert.Equal(".mp3", audio.FileExtension);
    }

    [Fact]
    public void Validate_WithRecognizedCompatibleMp4Brand_ReturnsM4a()
    {
        byte[] content = GetContent("audio/mp4");
        "test"u8.CopyTo(content.AsSpan(8, 4));

        ValidatedAudio audio = AudioUploadValidator.Validate(content, "audio/mp4");

        Assert.Equal(".m4a", audio.FileExtension);
    }

    [Fact]
    public void Validate_WithTruncatedId3Tag_ThrowsInvalidDataException()
    {
        byte[] content = GetContent("audio/mpeg");
        content[9] = 20;

        Assert.Throws<InvalidDataException>(
            () => AudioUploadValidator.Validate(content, "audio/mpeg"));
    }

    [Fact]
    public void Validate_WithInvalidMp4BoxSize_ThrowsInvalidDataException()
    {
        byte[] content = GetContent("audio/mp4");
        BinaryPrimitives.WriteUInt32BigEndian(content, 64);

        Assert.Throws<InvalidDataException>(
            () => AudioUploadValidator.Validate(content, "audio/mp4"));
    }

    [Fact]
    public void Validate_WithMaximumSize_AcceptsContent()
    {
        byte[] content = new byte[AudioUploadValidator.MaximumFileSizeInBytes];
        GetContent("audio/wav").CopyTo(content, 0);

        ValidatedAudio audio = AudioUploadValidator.Validate(content, "audio/wav");

        Assert.Equal(AudioUploadValidator.MaximumFileSizeInBytes, audio.Content.Length);
    }

    [Fact]
    public void Validate_WithOversizedContent_ThrowsInvalidDataException()
    {
        byte[] content = new byte[AudioUploadValidator.MaximumFileSizeInBytes + 1];
        GetContent("audio/wav").CopyTo(content, 0);

        Assert.Throws<InvalidDataException>(
            () => AudioUploadValidator.Validate(content, "audio/wav"));
    }

    public static IEnumerable<object[]> SupportedFormats()
    {
        yield return new object[] { GetContent("audio/wav"), "audio/wav", ".wav" };
        yield return new object[] { GetContent("audio/mpeg"), "audio/mpeg", ".mp3" };
        yield return new object[] { GetContent("audio/webm"), "audio/webm", ".webm" };
        yield return new object[] { GetContent("audio/mp4"), "audio/mp4", ".m4a" };
        yield return new object[] { GetContent("audio/ogg"), "audio/ogg", ".ogg" };
    }

    public static IEnumerable<object[]> InvalidHeaders()
    {
        yield return new object[] { Array.Empty<byte>() };
        yield return new object[] { "not audio"u8.ToArray() };
        yield return new object[] { "RIFF0000WAVE"u8.ToArray() };
        yield return new object[] { "RIFF0000WEBP0000"u8.ToArray() };
        yield return new object[] { "ID3"u8.ToArray() };
        yield return new object[] { new byte[] { 0xFF, 0xFB, 0x90, 0x64 } };
        yield return new object[] { new byte[] { 0x1A, 0x45, 0xDF, 0xA3 } };
        yield return new object[] { new byte[] { 0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70 } };
        yield return new object[] { "OggS"u8.ToArray() };

        byte[] invalidOgg = GetContent("audio/ogg");
        invalidOgg[4] = 1;
        yield return new object[] { invalidOgg };

        byte[] unknownMp4Brand = GetContent("audio/mp4");
        "test"u8.CopyTo(unknownMp4Brand.AsSpan(8, 4));
        "test"u8.CopyTo(unknownMp4Brand.AsSpan(16, 4));
        "test"u8.CopyTo(unknownMp4Brand.AsSpan(20, 4));
        yield return new object[] { unknownMp4Brand };
    }

    private static byte[] GetContent(string mediaType)
    {
        return mediaType switch
        {
            "audio/wav" => new byte[]
            {
                0x52, 0x49, 0x46, 0x46, 38, 0, 0, 0,
                0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20,
                16, 0, 0, 0, 1, 0, 1, 0, 0x80, 0x3E, 0, 0,
                0, 0x7D, 0, 0, 2, 0, 16, 0, 0x64, 0x61, 0x74, 0x61,
                2, 0, 0, 0, 0, 0
            },
            "audio/mpeg" => new byte[]
            {
                0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0,
                0xFF, 0xFB, 0x90, 0x64, 0
            },
            "audio/webm" => new byte[]
            {
                0x1A, 0x45, 0xDF, 0xA3, 0x8B,
                0x42, 0x86, 0x81, 1, 0x42, 0x82, 0x84,
                0x77, 0x65, 0x62, 0x6D
            },
            "audio/mp4" => new byte[]
            {
                0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70,
                0x4D, 0x34, 0x41, 0x20, 0, 0, 0, 0,
                0x4D, 0x34, 0x41, 0x20, 0x69, 0x73, 0x6F, 0x6D,
                0, 0, 0, 9, 0x6D, 0x64, 0x61, 0x74, 0
            },
            "audio/ogg" => new byte[]
            {
                0x4F, 0x67, 0x67, 0x53, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0,
                1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
        };
    }
}
