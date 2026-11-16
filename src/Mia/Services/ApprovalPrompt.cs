using System.Text.RegularExpressions;

namespace Mia.Services;

internal static class ApprovalPrompt
{
    internal static string Describe(string command, string description)
    {
        if (description.StartsWith("我想刪除這些檔案（", StringComparison.Ordinal) ||
            description.StartsWith("我想登入這個帳號（", StringComparison.Ordinal) ||
            description is "這項工作包含刪除檔案，可以嗎？" or "這項工作包含登入帳號，可以嗎？")
            return description;
        var desktopCommand = Regex.Match(command, @"^computer_use:\s*(?<action>[a-z_]+)\b");
        var desktopDescription = Regex.Match(description, @"^Allow computer_use to perform `(?<action>[a-z_]+)`\?");
        if (!desktopCommand.Success && !desktopDescription.Success)
            return "這個步驟需要你的允許，但我無法確認具體會做什麼。";

        string action = (desktopDescription.Success ? desktopDescription : desktopCommand).Groups["action"].Value;
        string operation = action switch
        {
            "click" => "點一下畫面上的項目",
            "double_click" => "在畫面上的項目連點兩下",
            "right_click" => "在畫面上的項目按右鍵",
            "middle_click" => "在畫面上的項目按滑鼠中鍵",
            "drag" => "拖動畫面上的項目",
            "scroll" => "捲動畫面",
            "type" => "在視窗裡輸入文字",
            "key" => "在視窗裡按鍵",
            "set_value" => "更改視窗裡的欄位內容",
            "focus" or "focus_app" or "bring_to_front" => "切換到這項任務要用的視窗",
            _ => "操作桌面"
        };
        string foreground = command.EndsWith(" [FOREGROUND — briefly raises the window / changes focus]", StringComparison.Ordinal)
            && action is not ("focus" or "focus_app" or "bring_to_front") ? "把視窗切到前面，再" : "";
        return $"我想{foreground}{operation}，可以嗎？";
    }
}
