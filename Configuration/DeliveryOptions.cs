namespace SkillPay.Service.Configuration;

/// <summary>
/// 资源交付配置。
/// </summary>
public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";

    /// <summary>
    /// 单次履约的时间预算（秒）。默认 600。
    /// </summary>
    /// <remarks>
    /// 履约一旦开始就与买家的 HTTP 连接脱钩（客户端断开不会取消履约），
    /// 因此需要一个独立上限，防止任务无限挂着。
    /// 如果交付物需要现场生成（例如调用大模型），把它调大。
    /// </remarks>
    public int FulfillmentTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// 交付文件根目录，相对应用内容根。默认 <c>payloads</c>。
    /// </summary>
    /// <remarks>
    /// 只允许交付这个目录内的文件，防止配置写错导致任意文件被读出。
    /// </remarks>
    public string PayloadRoot { get; set; } = "payloads";

    /// <summary>
    /// 单次内联交付的字节上限。默认 8 MiB。
    /// </summary>
    /// <remarks>
    /// 交付物以 Base64 内联在响应体里，体积会膨胀约 1/3。
    /// 超过上限时应改用对象存储直链交付，而不是继续内联。
    /// </remarks>
    public long MaxInlinePayloadBytes { get; set; } = 8L * 1024 * 1024;
}
