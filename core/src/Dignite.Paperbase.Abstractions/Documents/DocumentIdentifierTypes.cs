namespace Dignite.Paperbase.Documents;

/// <summary>
/// Issue #115 L2: 跨业务模块的标准化标识符类型常量。
///
/// <para>
/// 业务模块在实现 <c>IDocumentIdentifierProvider</c> 时把自己的字段映射到这些标准类型。
/// 例如合同模块把 <c>Contract.ContractNumber</c> 映射为 <see cref="ContractNumber"/>，
/// 把 <c>Contract.PartyAName</c> + <c>Contract.PartyBName</c> 都映射为 <see cref="PartyName"/>。
/// </para>
///
/// <para>
/// L2 关系发现 Pipeline 用这套类型在 fan-out 时筛选 provider，避免对不相关 type 走无效查询。
/// 新业务模块需要新增类型时在此添加常量；不在此处的字符串视为非约定类型，L2 不会处理。
/// </para>
/// </summary>
public static class DocumentIdentifierTypes
{
    /// <summary>合同编号（contract number, 框架合同/采购合同等的唯一编号）。</summary>
    public const string ContractNumber = "ContractNumber";

    /// <summary>采购订单号（purchase order number）。</summary>
    public const string PoNumber = "PoNumber";

    /// <summary>发票号（invoice number）。</summary>
    public const string InvoiceNumber = "InvoiceNumber";

    /// <summary>
    /// 当事人名称（甲方 / 乙方 / 客户 / 供应商等）。同一文档可能有多个 PartyName 值，
    /// 同一 PartyName 值可能跨多个文档（这是 L2 发现关系的核心信号之一）。
    /// </summary>
    public const string PartyName = "PartyName";

    /// <summary>项目代号（project code，跨多份业务文档共享的项目标识）。</summary>
    public const string ProjectCode = "ProjectCode";
}
