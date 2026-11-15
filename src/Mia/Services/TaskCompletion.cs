using System.Text.RegularExpressions;

namespace Mia.Services;

internal static class TaskCompletion
{
    // File delivery details stay in the results page; chat offers only clickable cards.
    internal static string Display(string reply, bool hasFiles)
    {
        string clean = Regex.Replace(reply, @"(?im)^.*(?:[a-z]:[\\/]|file://|(?:jobs|output)[\\/]|成果路徑|檔案位置|工作編號|任務編號|(?:job|session)[_ ]?id|\b[0-9a-f]{32}\b|\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b).*(?:\r?\n|$)", "").Trim();
        if (hasFiles)
        {
            // Remove orphan technical headings/fences left after stripping path lines.
            clean = Regex.Replace(clean, @"(?im)^\s*(?:```[^\r\n]*|#{0,6}\s*(?:成果路徑|成果檔案|工作資訊|檔案位置)[：:]?)\s*$", "").Trim();
            return SpeechTags.HasWords(clean) ? clean : "[audio: happy] 已處理完成，成果可以從下方檔案連結開啟。";
        }
        return SpeechTags.HasWords(clean) ? clean : "[audio: peaceful] 任務已完成，詳細內容可在任務成果中查看。";
    }
}
