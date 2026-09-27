using System.Buffers.Binary;

namespace NutriFlow.Infrastructure.Audio;

public static class AudioUploadValidator
{
    public const int MaximumFileSizeInBytes = 8 * 1024 * 1024;

    public static ValidatedAudio Validate(
        ReadOnlyMemory<byte> content,
        string? declaredMediaType)
    {
        if (content.IsEmpty)
        {
            throw new InvalidDataException("The uploaded audio is empty.");
        }

        if (content.Length > MaximumFileSizeInBytes)
        {
            throw new InvalidDataException("The uploaded audio exceeds 8 MiB.");
        }

        (string mediaType, string extension) = DetectFormat(content.Span);
        string normalizedDeclaredMediaType = declaredMediaType?
            .Split(';', 2)[0]
            .Trim()
            .ToLowerInvariant() ?? string.Empty;

        if (normalizedDeclaredMediaType.Length > 0 &&
            normalizedDeclaredMediaType != "application/octet-stream" &&
            !MatchesMediaType(normalizedDeclaredMediaType, mediaType))
        {
            throw new InvalidDataException(
                "The declared media type does not match the audio content.");
        }

        return new ValidatedAudio(content, mediaType, extension);
    }

    private static (string MediaType, string Extension) DetectFormat(
        ReadOnlySpan<byte> content)
    {
        if (content.Length > 12 &&
            content[..4].SequenceEqual("RIFF"u8) &&
            content.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return ("audio/wav", ".wav");
        }

        if (HasId3Header(content) || HasMpegAudioHeader(content))
        {
            return ("audio/mpeg", ".mp3");
        }

        if (content.Length > 4 &&
            content[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }))
        {
            return ("audio/webm", ".webm");
        }

        if (HasMp4Header(content))
        {
            return ("audio/mp4", ".m4a");
        }

        if (content.Length > 27 &&
            content[..4].SequenceEqual("OggS"u8) &&
            content[4] == 0)
        {
            return ("audio/ogg", ".ogg");
        }

        throw new InvalidDataException(
            "Only WAV, MP3, WebM, M4A, and Ogg audio containers are supported.");
    }

    private static bool HasId3Header(ReadOnlySpan<byte> content)
    {
        if (content.Length <= 10 ||
            !content[..3].SequenceEqual("ID3"u8) ||
            content[3] is < 2 or > 4)
        {
            return false;
        }

        int tagSize = 0;
        for (int index = 6; index < 10; index++)
        {
            if ((content[index] & 0x80) != 0)
            {
                return false;
            }

            tagSize = (tagSize << 7) | content[index];
        }

        return tagSize <= content.Length - 10;
    }

    private static bool HasMpegAudioHeader(ReadOnlySpan<byte> content)
    {
        return content.Length > 4 &&
            content[0] == 0xFF &&
            (content[1] & 0xE0) == 0xE0 &&
            (content[1] & 0x18) != 0x08 &&
            (content[1] & 0x06) != 0 &&
            (content[2] & 0xF0) != 0xF0 &&
            (content[2] & 0x0C) != 0x0C;
    }

    private static bool HasMp4Header(ReadOnlySpan<byte> content)
    {
        if (content.Length < 17 ||
            !content.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return false;
        }

        uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(content[..4]);
        if (boxSize < 16 || boxSize >= content.Length || (boxSize - 16) % 4 != 0)
        {
            return false;
        }

        if (IsMp4Brand(content.Slice(8, 4)))
        {
            return true;
        }

        for (int offset = 16; offset < boxSize; offset += 4)
        {
            if (IsMp4Brand(content.Slice(offset, 4)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMp4Brand(ReadOnlySpan<byte> brand)
    {
        return brand.SequenceEqual("M4A "u8) ||
            brand.SequenceEqual("M4B "u8) ||
            brand.SequenceEqual("M4P "u8) ||
            brand.SequenceEqual("F4A "u8) ||
            brand.SequenceEqual("F4B "u8) ||
            brand.SequenceEqual("isom"u8) ||
            brand.SequenceEqual("iso2"u8) ||
            brand.SequenceEqual("mp41"u8) ||
            brand.SequenceEqual("mp42"u8);
    }

    private static bool MatchesMediaType(string declaredMediaType, string mediaType)
    {
        return mediaType switch
        {
            "audio/wav" => declaredMediaType is "audio/wav" or "audio/x-wav" or
                "audio/wave" or "audio/vnd.wave",
            "audio/mpeg" => declaredMediaType is "audio/mpeg" or "audio/mp3" or
                "audio/x-mp3" or "audio/mpeg3" or "audio/x-mpeg",
            "audio/webm" => declaredMediaType is "audio/webm" or "video/webm",
            "audio/mp4" => declaredMediaType is "audio/mp4" or "audio/m4a" or
                "audio/x-m4a" or "video/mp4",
            "audio/ogg" => declaredMediaType is "audio/ogg" or "audio/x-ogg" or
                "application/ogg",
            _ => false
        };
    }
}

public sealed record ValidatedAudio(
    ReadOnlyMemory<byte> Content,
    string MediaType,
    string FileExtension);
