using Volo.Abp.Reflection;

namespace Dignite.Paperbase.Permissions;

public class PaperbasePermissions
{
    public const string GroupName = "Paperbase";

    public static class Documents
    {
        public const string Default = GroupName + ".Documents";
        public const string Upload = Default + ".Upload";
        public const string Delete = Default + ".Delete";
        public const string PermanentDelete = Default + ".PermanentDelete";
        public const string Restore = Default + ".Restore";
        public const string Export = Default + ".Export";
        public const string ConfirmClassification = Default + ".ConfirmClassification";

        public static class Pipelines
        {
            public const string Default = Documents.Default + ".Pipelines";
            public const string Retry = Default + ".Retry";
        }
    }

    // 文件柜（#194）——人工组织维度，与 Documents 同级权限组。
    public static class Cabinets
    {
        public const string Default = GroupName + ".Cabinets";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    public static string[] GetAll()
    {
        return ReflectionHelper.GetPublicConstantsRecursively(typeof(PaperbasePermissions));
    }
}
