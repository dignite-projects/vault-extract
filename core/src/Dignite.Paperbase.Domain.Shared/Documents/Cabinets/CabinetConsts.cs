namespace Dignite.Paperbase.Documents.Cabinets;

public static class CabinetConsts
{
    public static int MaxNameLength { get; set; } = 128;

    /// <summary>
    /// <see cref="Cabinet.Description"/> 长度上限。Description 是可选的选柜辅助文本（#273），仅喂入 #265 选柜 prompt
    /// ——一两句足矣，过长稀释信号且增加 token，故上限远小于文档正文。
    /// </summary>
    public static int MaxDescriptionLength { get; set; } = 512;
}
