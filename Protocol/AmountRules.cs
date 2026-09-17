using System.Globalization;
using System.Text.RegularExpressions;

namespace SkillPay.Service.Protocol;

/// <summary>
/// 金额规范化与比较。协议要求金额为「数字，最多两位小数」的字符串。
/// </summary>
public static partial class AmountRules
{
    [GeneratedRegex(@"^\d+(?:\.\d{1,2})?$")]
    private static partial Regex AmountPattern();

    /// <summary>
    /// 规范化金额为两位小数的字符串（如 <c>0.01</c>、<c>12.00</c>）。
    /// 格式非法时抛出 <see cref="ArgumentException"/>。
    /// </summary>
    public static string Normalize(string? value)
    {
        if (!TryParse(value, out var amount))
        {
            throw new ArgumentException($"金额格式非法：{value}", nameof(value));
        }

        return amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>按数值语义比较两个金额字符串是否相等。</summary>
    public static bool AreEqual(string? left, string? right) =>
        TryParse(left, out var leftAmount)
        && TryParse(right, out var rightAmount)
        && leftAmount == rightAmount;

    public static bool TryParse(string? value, out decimal amount)
    {
        amount = 0m;

        if (string.IsNullOrWhiteSpace(value) || !AmountPattern().IsMatch(value.Trim()))
        {
            return false;
        }

        return decimal.TryParse(
                   value.Trim(),
                   NumberStyles.AllowDecimalPoint,
                   CultureInfo.InvariantCulture,
                   out amount)
               && amount >= 0m;
    }
}
