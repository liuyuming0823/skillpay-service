namespace SkillPay.Service.Services;

/// <summary>产出文本的归一化工具。</summary>
internal static class TextNormalization
{
    /// <summary>把换行符统一成 LF。</summary>
    /// <remarks>
    /// 必须做的事：Windows 上 <see cref="System.Text.StringBuilder"/> 的 AppendLine 产出 CRLF，
    /// 而下游技能工厂用 Python 文本模式写盘时会再把 <c>\n</c> 翻成 <c>os.linesep</c>，
    /// CRLF 会变成 <c>\r\r\n</c>，在 Markdown 与 YAML frontmatter 里都会出问题。
    /// 因此服务端只吐 LF，换行风格交给调用方决定。
    /// </remarks>
    public static string ToLf(string value) =>
        string.IsNullOrEmpty(value)
            ? value
            : value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
