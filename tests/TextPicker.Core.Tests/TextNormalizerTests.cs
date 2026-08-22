namespace TextPicker.Core.Tests;

public sealed class TextNormalizerTests
{
    [Fact]
    public void NormalizesLineEndings_AndTrims()
    {
        var result = TextNormalizer.Normalize("  hello\r\nworld\r ", 100);

        Assert.NotNull(result);
        Assert.Equal("hello\nworld", result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void RemovesInvisibleCharacters_KeepsTabAndNewline()
    {
        var result = TextNormalizer.Normalize("a\u0000b\u0007c\u200Bd\uFEFFe\tf\ng", 100);

        Assert.NotNull(result);
        Assert.Equal("abcde\tf\ng", result.Text);
    }

    [Fact]
    public void EmptyAfterFiltering_ReturnsNull_CancelSignal()
    {
        Assert.Null(TextNormalizer.Normalize(null, 100));
        Assert.Null(TextNormalizer.Normalize(string.Empty, 100));
        Assert.Null(TextNormalizer.Normalize("   \r\n  ", 100));
        Assert.Null(TextNormalizer.Normalize("\u200B\u200B", 100));    // 全不可见字符
    }

    [Fact]
    public void TruncatesAtMaxLength_AndFlags()
    {
        var result = TextNormalizer.Normalize("abcdefgh", 5);

        Assert.NotNull(result);
        Assert.Equal("abcde", result.Text);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void ExactLength_IsNotTruncated()
    {
        var result = TextNormalizer.Normalize("abcde", 5);

        Assert.NotNull(result);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void LocalContext_DeduplicatedAgainstText()
    {
        Assert.Null(TextNormalizer.BuildLocalContext("hello\nworld", ContextKind.Paragraph, "hello\nworld", 100));
        Assert.Null(TextNormalizer.BuildLocalContext("   ", ContextKind.Paragraph, "text", 100));

        var context = TextNormalizer.BuildLocalContext("para one says hello", ContextKind.Paragraph, "hello", 100);
        Assert.NotNull(context);
        Assert.Equal(ContextKind.Paragraph, context.Kind);
        Assert.Equal("para one says hello", context.Text);
    }

    [Fact]
    public void LocalContext_TruncatedAtItsOwnLimit()
    {
        var context = TextNormalizer.BuildLocalContext("0123456789", ContextKind.Line, "text", 4);

        Assert.NotNull(context);
        Assert.Equal("0123", context.Text);
    }
}
