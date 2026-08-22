using System.Text;

namespace TextPicker;

/// <summary>文本规范化（v6.1 冻结）：\r\n→\n、去首尾空白、滤全不可见字符（滤后为空 → 取消发布信号）、上限截断、
/// Text 与 LocalContext.Text 完全相同则置 null LocalContext。</summary>
public static class TextNormalizer
{
    /// <summary>规范化结果；原始文本规范化后为空时返回 null（调用方据此走 EmptyText 失败路径）。</summary>
    public sealed record NormalizedText(string Text, bool Truncated);

    public static NormalizedText? Normalize(string? raw, int maxLength)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            // 换行统一为 \n（\r\n 与孤立 \r）。
            if (ch == '\r')
            {
                continue;    // \r\n 中的 \n 保留为换行；孤立 \r 丢弃
            }

            if (IsInvisible(ch))
            {
                continue;
            }

            builder.Append(ch);
        }

        var text = builder.ToString().Trim();

        // 孤立 \r 丢弃后，需要把 \n 前缺失的语义补齐吗——不：\r\n 已由 \n 代表换行。
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > maxLength)
        {
            return new NormalizedText(text[..maxLength], Truncated: true);
        }

        return new NormalizedText(text, Truncated: false);
    }

    /// <summary>LocalContext 构建：规范化 + 上限 + 与正文去重（完全相同 → null）。</summary>
    public static LocalTextContext? BuildLocalContext(string? raw, ContextKind kind, string normalizedText, int maxLength)
    {
        var normalized = Normalize(raw, maxLength);
        if (normalized is not { } context || context.Text.Length == 0 || context.Text == normalizedText)
        {
            return null;
        }

        return new LocalTextContext { Text = context.Text, Kind = kind };
    }

    /// <summary>全不可见字符：控制字符与格式字符（保留 \n 与 \t —— 换行与制表是语义空白，不是不可见垃圾）。</summary>
    private static bool IsInvisible(char ch)
    {
        if (ch == '\n' || ch == '\t')
        {
            return false;
        }

        return char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format;
    }
}
