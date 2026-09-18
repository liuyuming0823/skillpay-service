namespace SkillPay.Service.Configuration;

/// <summary>
/// 技能包自更新目录。
/// </summary>
/// <remarks>
/// 「技能包」与「交付物」是两件不同的东西，别混：
/// <list type="bullet">
///   <item><b>技能包</b> —— 装在本地的 <c>SKILL.md</c> 与脚本，是**取货器**。
///         它本身不收费、谁都能更新：取货器旧了会取不到货，卡住的是用户而不是收入。</item>
///   <item><b>交付物</b> —— 付费买到的那份产物。它按订单冻结，见
///         <see cref="SkillDefinition.PayloadVersion"/>。</item>
/// </list>
/// 因此本节的接口全部免鉴权，不需要 <c>Payment-Proof</c>。
/// </remarks>
public sealed class SkillUpdateOptions
{
    public const string SectionName = "SkillUpdate";

    /// <summary>技能包根目录，相对应用内容根。默认 <c>skills</c>。</summary>
    public string PackageRoot { get; set; } = "skills";

    /// <summary>
    /// 对外公布的服务地址（形如 <c>https://example.com</c>）。
    /// </summary>
    /// <remarks>
    /// 用来拼 <c>package_url</c>。留空时按请求的 <c>Host</c> 推断 ——
    /// 这在反向代理后面会拿到内网地址或错误协议，因此生产上应当显式配置。
    /// </remarks>
    public string? PublicBaseUrl { get; set; }

    /// <summary>已登记的可自更新技能，key 为技能编码。</summary>
    public Dictionary<string, SkillPackageDefinition> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按技能编码取定义；未配置时返回 <c>null</c>。</summary>
    public SkillPackageDefinition? Resolve(string skillCode) =>
        Skills.TryGetValue(skillCode, out var definition) ? definition : null;
}

/// <summary>
/// 单个技能包的版本与文件。
/// </summary>
public sealed class SkillPackageDefinition
{
    /// <summary>当前最新版本号，形如 <c>1.4.0</c>。比较采用点分数字序，不要写 <c>v</c> 前缀。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>包文件路径，相对 <see cref="SkillUpdateOptions.PackageRoot"/>。</summary>
    public string PackageFile { get; set; } = string.Empty;

    /// <summary>对外展示的包文件名；留空时取 <see cref="PackageFile"/> 的文件名。</summary>
    public string? PackageFileName { get; set; }

    /// <summary>
    /// 最低可接受版本；客户端低于它就**必须**更新才能继续使用。
    /// </summary>
    /// <remarks>
    /// 这是「分级处置」的开关：
    /// 客户端版本不低于它 → 软提示（可以继续用旧版）；
    /// 低于它 → 判定为协议已不兼容，硬性要求更新。
    /// 留空表示永不硬性阻断。
    /// </remarks>
    public string? MinSupportedVersion { get; set; }

    /// <summary>版本说明，用于告诉用户「这次更新了什么」。</summary>
    public string? Notes { get; set; }

    /// <summary>对外展示的包文件名。</summary>
    public string ResolveFileName() =>
        !string.IsNullOrWhiteSpace(PackageFileName)
            ? PackageFileName!.Trim()
            : Path.GetFileName(PackageFile);
}
