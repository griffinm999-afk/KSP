using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

/// <summary>ConfigNode value encoding contracts; disk-backed ConfigNode roundtrip remains a runtime check.</summary>
public sealed class ColonySaveEncodingTests
{
    [Fact]
    public void RepeatedFfBytesEncodeWithoutSlashCommentTokensAndRoundTrip()
    {
        var bytes = Enumerable.Repeat((byte)0xff, 8_192).ToArray();
        var standard = Convert.ToBase64String(bytes);

        Assert.Contains("//", standard);

        var encoded = ColonyStateCodec.EncodeSaveValue(bytes);

        Assert.DoesNotContain("/", encoded);
        Assert.DoesNotContain("//", encoded);
        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("\r", encoded);
        Assert.DoesNotContain("\n", encoded);
        Assert.Equal(bytes, ColonyStateCodec.DecodeSaveValue(encoded));
    }

    [Theory]
    [InlineData("AA/=")]
    [InlineData("AA?=")]
    public void DecoderRejectsCharactersOutsideSaveAlphabet(string value)
    {
        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.DecodeSaveValue(value));
    }

    [Fact]
    public void DecoderRejectsNoncanonicalBase64PadBits()
    {
        // This decodes to the same byte as "AA==" but carries nonzero unused pad bits.
        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.DecodeSaveValue("AB=="));
    }

    [Fact]
    public void DecoderRejectsTruncatedPayloadAsInvalidSaveData()
    {
        // "_w==" is the canonical one-byte encoding; removing its required padding truncates it.
        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.DecodeSaveValue("_w"));
    }

    [Fact]
    public void DecoderRejectsEncodedValueAboveMaximumPayloadBound()
    {
        var encodedLimit = (ColonyLimits.MaxBytes + 2) / 3 * 4;
        var oversized = new string('A', encodedLimit + 4);

        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.DecodeSaveValue(oversized));
    }
}
