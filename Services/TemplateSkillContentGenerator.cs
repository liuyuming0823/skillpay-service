using System.Text;

namespace SkillPay.Service.Services;

/// <summary>
/// 确定性技能内容生成器。
/// </summary>
/// <remarks>
/// 不依赖任何外部服务，按 12 段规格把请求参数铺成完整的 <c>SKILL.md</c> 与配套文件。
/// 两条用途：
/// <list type="bullet">
///   <item>没有大模型凭据时的兜底，保证付费用户**永远拿得到东西**（已收款却交付失败不可接受）。</item>
///   <item>大模型产出解析失败时的降级路径。</item>
/// </list>
/// 产出完全由请求参数决定，同一请求两次调用结果一致。
/// </remarks>
public sealed class TemplateSkillContentGenerator : ISkillContentGenerator
{
    public string Name => "template-v1";

    public Task<SkillContent> GenerateAsync(
        SkillGenerationRequest request,
        decimal unitPriceCny,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(Build(request, unitPriceCny));
    }

    private SkillContent Build(SkillGenerationRequest request, decimal unitPriceCny)
    {
        string display = request.EffectiveDisplayName;
        string[] triggers = request.EffectiveTriggers.ToArray();
        string triggerText = string.Join('、', triggers);

        return new SkillContent
        {
            // 统一 LF：Windows 上 AppendLine 产出 CRLF，下游 Python 文本模式写盘会再翻一次。
            SkillMd = TextNormalization.ToLf(BuildSkillMd(request, display, triggers, triggerText)),
            References = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["io-contract.md"] = TextNormalization.ToLf(BuildIoContract(request, display)),
                ["quality-checklist.md"] = TextNormalization.ToLf(BuildQualityChecklist(display))
            },
            Templates = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["output-template.md"] = TextNormalization.ToLf(BuildOutputTemplate(display))
            },
            Meta = new SkillContentMeta
            {
                Model = Name,
                Generator = "template",
                Tokens = 0,
                UnitPriceCny = unitPriceCny
            }
        };
    }

    private static string BuildSkillMd(
        SkillGenerationRequest request,
        string display,
        string[] triggers,
        string triggerText)
    {
        string name = request.SkillName!;
        string descZh = Shorten(request.EffectiveDescZh, 30);
        string descEn = request.EffectiveDescEn;
        string category = request.EffectiveCategory;

        var sb = new StringBuilder();

        sb.AppendLine("---");
        sb.AppendLine($"name: {name}");
        sb.AppendLine($"description: {request.EffectiveDescZh} 触发词：{triggerText}");
        sb.AppendLine("version: 1.0.0");
        sb.AppendLine($"description_zh: {descZh}");
        sb.AppendLine($"description_en: {descEn}");
        sb.AppendLine($"displayName: {display}");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  display_name: {display}");
        sb.AppendLine($"  display_name_en: {ToTitleWords(name)}");
        sb.AppendLine($"  category: {category}");
        sb.AppendLine("  author: 技能工厂");
        sb.AppendLine($"  slug: {name}");
        sb.AppendLine($"  tags: [{BuildTags(name, category)}]");
        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine($"# {display}");
        sb.AppendLine();

        sb.AppendLine("## 这解决什么问题");
        sb.AppendLine();
        sb.AppendLine($"{request.EffectiveDescZh}。这类工作手工做一遍并不难，难的是每次都按同一套标准做全 ——");
        sb.AppendLine("漏掉的那一步往往要到交付后才被发现。本技能把这套固定流程固化下来，让每次产出都落在同一条质量线上。");
        sb.AppendLine();

        sb.AppendLine("## 什么时候用它 / 不用它");
        sb.AppendLine();
        sb.AppendLine($"**用它**：用户说出「{triggerText}」中的任意一句时。");
        sb.AppendLine("**不用它**：用户只是随口提到相关词、但并没有要产出交付物时 —— 先确认意图，不要抢着执行。");
        sb.AppendLine();

        sb.AppendLine("## 输入契约");
        sb.AppendLine();
        sb.AppendLine("| 字段 | 必填 | 说明 | 缺失时 |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine("| 任务对象 | 是 | 本次要处理的具体对象 | 追问一句，不要猜 |");
        sb.AppendLine("| 背景资料 | 否 | 相关文件、记录、数据 | 走「缺资料降级路径」，只输出待补充清单 |");
        sb.AppendLine("| 输出偏好 | 否 | 格式、篇幅、交付位置 | 用默认值，并在产出里注明用了默认 |");
        sb.AppendLine();
        sb.AppendLine("字段级细则见 `references/io-contract.md`（在输入字段与预期不符、或用户质疑取值时读它）。");
        sb.AppendLine();

        sb.AppendLine("## 执行步骤");
        sb.AppendLine();
        sb.AppendLine("### 阶段 1 · 收集与校验");
        sb.AppendLine();
        sb.AppendLine("1. 提取任务对象与背景资料，逐项对照输入契约。");
        sb.AppendLine("2. 缺必填字段时，向用户追问一次，并给出可选范围。追问后仍缺失 → 停止执行并说明缺什么。");
        sb.AppendLine("3. 背景资料与任务对象明显不匹配时，先指出矛盾点，不要带着矛盾继续。");
        sb.AppendLine();
        sb.AppendLine("### 阶段 2 · 处理");
        sb.AppendLine();
        sb.AppendLine("1. 按任务对象逐条处理，每条产出必须能追溯到输入中的具体位置。");
        sb.AppendLine("2. 遇到判断类问题时，先写判定依据，再给结论；依据不成立的条目单独列出。");
        sb.AppendLine("3. 处理完成后自检：对照 `references/quality-checklist.md` 逐条打勾，未通过项回到上一步重做。");
        sb.AppendLine();
        sb.AppendLine("### 阶段 3 · 交付");
        sb.AppendLine();
        sb.AppendLine("1. 按输出格式落盘，文件名固定，不随日期变化。");
        sb.AppendLine("2. 在回复里给出产物路径与一句话结论。");
        sb.AppendLine("3. 自检未通过或存在缺资料的条目，在产物末尾单列「待确认」小节，不要混进正文。");
        sb.AppendLine();

        sb.AppendLine("## 输出格式");
        sb.AppendLine();
        sb.AppendLine($"产物写到工作目录下的 `{name}-output.md`（运行时产物 / 输出文件），结构：");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("# " + display + "产出");
        sb.AppendLine("## 结论");
        sb.AppendLine("## 明细");
        sb.AppendLine("## 待确认");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("空模板见 `templates/output-template.md`。");
        sb.AppendLine();

        sb.AppendLine("## 状态与存储");
        sb.AppendLine();
        sb.AppendLine("默认不跨次记忆 —— 每次执行都从当前输入重新开始，产物覆盖同名文件，避免新旧结果混在一起。");
        sb.AppendLine("需要「继续上次」时，读取上一次的 `" + name + "-output.md`，以其中的「待确认」小节作为本次输入。");
        sb.AppendLine();

        sb.AppendLine("## 依赖与降级");
        sb.AppendLine();
        sb.AppendLine("| 依赖 | 用途 | 缺失时的降级 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine("| 背景资料文件 | 提供处理依据 | 只输出待补充清单，不产出结论 |");
        sb.AppendLine("| 规则参考文件 | 提供判定标准 | 用正文默认标准，并在产物里注明 |");
        sb.AppendLine();

        sb.AppendLine("## 隐私与安全");
        sb.AppendLine();
        sb.AppendLine("产物只写工作目录，不上传外部服务。");
        sb.AppendLine("输入含身份证号、手机号、银行账号等个人敏感信息时，产物中一律做掩码处理，只保留用于核对的后四位。");
        sb.AppendLine("不把原始敏感数据写进日志、不写进产物正文。");
        sb.AppendLine();

        sb.AppendLine("## 常见坑");
        sb.AppendLine();
        sb.AppendLine("1. **现象**：技能装好后从不触发。**原因**：触发词写成了书面语，用户口语说法对不上。**规避**：把用户真实说过的话原样填进描述。");
        sb.AppendLine("2. **现象**：技能在商店里显示英文 slug。**原因**：frontmatter 只写了下划线 `display_name`，平台读的是驼峰 `displayName`。**规避**：两个都写。");
        sb.AppendLine("3. **现象**：上架校验报 frontmatter 解析失败。**原因**：`description` 写成了 YAML 折叠块（块标量写法），平台只认单行标量。**规避**：这四个展示字段一律写成单行。");
        sb.AppendLine("4. **现象**：交付物里留着待填标记。**原因**：生成时图快没填。**规避**：发版前全文搜一遍尖括号与花括号。");
        sb.AppendLine("5. **现象**：换台机器就找不到文件。**原因**：正文写了相对路径。**规避**：产物路径全部相对工作目录，并在正文注明工作目录是什么。");
        sb.AppendLine("6. **现象**：用户说结果和预期不符。**原因**：能力边界只写能做什么，没写做不到什么。**规避**：边界小节先写做不到的。");
        sb.AppendLine("7. **现象**：用户缺一份资料就整条卡死。**原因**：没有降级路径。**规避**：每个必需输入都要定义缺了怎么办。");
        sb.AppendLine();

        sb.AppendLine("## 能力边界");
        sb.AppendLine();
        sb.AppendLine("本技能**做不到**：");
        sb.AppendLine();
        sb.AppendLine("- 不联网检索资料，只用用户提供的内容；");
        sb.AppendLine("- 不代替人工做最终决策，只给带依据的建议；");
        sb.AppendLine("- 不处理用户没有提供的对象，不凭常识补全；");
        sb.AppendLine("- 不保证结论正确，只保证流程完整、依据可追溯。");
        sb.AppendLine();
        sb.AppendLine("能做：按固定流程把输入整理成结构化、可追溯的产出，并在交付前完成自检。");
        sb.AppendLine();

        sb.AppendLine("## 验收用例");
        sb.AppendLine();
        sb.AppendLine("| 场景 | 输入 | 预期结果 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| 正常 | 任务对象 + 完整背景资料 | 产出 `{name}-output.md`，「明细」逐条有依据，「待确认」为空 |");
        sb.AppendLine("| 缺资料 | 只给任务对象 | 追问一次；仍缺则只输出待补充清单，不产出结论 |");
        sb.AppendLine("| 异常 | 背景资料与任务对象不匹配 | 先指出矛盾点并停止执行，不产出结论 |");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string BuildIoContract(SkillGenerationRequest request, string display)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {display} · 输入契约细则");
        sb.AppendLine();
        sb.AppendLine("在输入字段与预期不符、或用户质疑取值来源时读本文件。");
        sb.AppendLine();
        sb.AppendLine("## 字段定义");
        sb.AppendLine();
        sb.AppendLine("| 字段 | 类型 | 必填 | 取值来源 | 校验规则 |");
        sb.AppendLine("|---|---|---|---|---|");
        sb.AppendLine("| 任务对象 | 文本 | 是 | 用户首轮消息 | 非空；与背景资料能对应上 |");
        sb.AppendLine("| 背景资料 | 文件或文本 | 否 | 用户提供或工作目录 | 能读取；与任务对象相关 |");
        sb.AppendLine("| 输出偏好 | 文本 | 否 | 用户指定 | 落在支持的格式内 |");
        sb.AppendLine();
        sb.AppendLine("## 缺字段时的行为");
        sb.AppendLine();
        sb.AppendLine("1. **缺任务对象**：追问一次，给出从上下文推断出的候选项供选择。仍无法确定则停止，不放行。");
        sb.AppendLine("2. **缺背景资料**：不阻断。产出改为「待补充清单」，逐条写清缺什么、为什么需要。");
        sb.AppendLine("3. **缺输出偏好**：用默认格式（Markdown 单文件），并在产物开头注明使用了默认值。");
        sb.AppendLine();
        sb.AppendLine("## 冲突判定");
        sb.AppendLine();
        sb.AppendLine("任务对象与背景资料不匹配时，**先指出矛盾，不带着矛盾继续**。");
        sb.AppendLine("判定顺序：字段名不一致 → 取值明显越界 → 时间范围对不上。命中任一条即停止并报告。");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string BuildQualityChecklist(string display)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {display} · 交付前自查表");
        sb.AppendLine();
        sb.AppendLine("阶段 2 结束时逐条打勾，未通过项回到对应步骤重做。");
        sb.AppendLine();
        sb.AppendLine("1. 每条明细都能追溯到输入中的具体位置，没有凭空补全的内容；");
        sb.AppendLine("2. 「待确认」小节与正文分离，缺资料的条目没有被当成结论；");
        sb.AppendLine("3. 结论与明细一致，没有互相矛盾的数字或说法；");
        sb.AppendLine("4. 产物路径与正文描述一致，文件名没有随日期变化；");
        sb.AppendLine("5. 个人敏感信息已完成掩码；");
        sb.AppendLine("6. 能力边界中提到做不到的事，产物里确实没有越界尝试。");
        sb.AppendLine();
        sb.AppendLine("## 不合格的典型表现");
        sb.AppendLine();
        sb.AppendLine("- 明细里出现了输入中没有的实体；");
        sb.AppendLine("- 用「等等」「若干」等模糊词代替具体条目；");
        sb.AppendLine("- 结论段比明细段长，像是先有结论再补依据。");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string BuildOutputTemplate(string display)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {display}产出");
        sb.AppendLine();
        sb.AppendLine("## 结论");
        sb.AppendLine();
        sb.AppendLine("一到三句话说明本次处理了什么、得出什么。");
        sb.AppendLine();
        sb.AppendLine("## 明细");
        sb.AppendLine();
        sb.AppendLine("| # | 条目 | 依据 | 结论 |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine("| 1 |  |  |  |");
        sb.AppendLine();
        sb.AppendLine("## 待确认");
        sb.AppendLine();
        sb.AppendLine("列出因缺资料或依据不足而未能得出结论的条目；没有则写「无」。");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string BuildTags(string name, string category)
    {
        var tags = new List<string> { category };

        tags.AddRange(name
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !w.Equals("ym", StringComparison.OrdinalIgnoreCase))
            .Take(3));

        return string.Join(", ", tags.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string ToTitleWords(string name) =>
        string.Join(' ', name
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string Shorten(string value, int max)
    {
        string flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();

        return flat.Length <= max ? flat : flat[..max];
    }
}
