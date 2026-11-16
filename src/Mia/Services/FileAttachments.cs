using System.IO;

namespace Mia.Services;

public static class FileAttachments
{
    public static string Validate(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new IOException("附件已不存在或不是檔案，請重新拖入。");
        if (!string.Equals(Path.GetPathRoot(file.FullName), @"C:\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("檔案附件需位於已授權的 C 槽。");
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("附件不可使用重新導向的路徑。");
        for (var folder = file.Directory; folder is not null; folder = folder.Parent)
            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("附件不可來自重新導向的資料夾。");
        return file.FullName;
    }
}
