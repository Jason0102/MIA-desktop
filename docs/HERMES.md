# MIA 3.2 的 Hermes 與 Codex 整合

MIA 維持桌面角色、對話與語音，透過只聽本機的 Hermes API 提交任務。Hermes 管理會話並選擇工具。`mia_codex_run` 交給本機 Codex CLI 執行工作，Hermes 再把文字與產物資訊回傳給米亞。`computer_use` 由 Hermes 的 Windows 驅動執行。

## 安裝與執行

需要 Windows x64、.NET 8 SDK（建置時）、原生 Codex CLI，以及 Hermes 支援的模型帳號。從 `3.2` 分支目錄開啟 PowerShell：

```powershell
./Install-Hermes.ps1
./build.ps1 -Test
./Start-Mia.ps1 -Setup
```

安裝腳本固定 Hermes 0.23.1 及原始安裝器雜湊，選用本機資料夾，並略過原廠安裝器的使用者 PATH 登錄。若有可用的原生 `codex.exe`，會把它與相符的 Windows 輔助程式複製到 `.mia-runtime/bin`；也可指定 `-CodexPath`。Codex 使用目前 Windows 帳號的 Codex 登入或設定的 OpenAI API 金鑰。

若已有 OpenAI 金鑰檔，也可以使用以下方式代替 `-Setup`：

```powershell
./Start-Mia.ps1 -OpenAiKeyFile 'C:\path\to\api_key' -Model 'your-openai-model'
```

金鑰會以 Windows DPAPI 儲存在 3.2 執行目錄，綁定目前使用者。請選擇自己的帳號可用且支援工具的模型。完成首次設定後，可使用 `啟動米亞.vbs` 或直接執行 `Start-Mia.ps1`。米亞結束時，啟動器會關閉這次啟動的 Gateway。3.2 的設定、會話與工具資料位於 `.mia-runtime`，桌寵建置成品在 `dist/MIA-3.2-win-x64`。在另一台電腦請重新安裝及登入。

## 使用方式

- 將想交給 Codex 的檔案放入 `.mia-runtime/work/input`。在米亞描述任務及相對檔名，例如「請用 Codex 把 `week.txt` 整理成一頁工作摘要」。Codex 專用工作目錄與輸出置於 `.mia-runtime/work/jobs/<任務識別>/`。
- 在米亞說「請使用 `mia_install_extension` 安裝內附診斷擴充」即可請 Hermes 自動安裝已隨專案提供的無依賴範例，並檢查註冊結果。第三方插件可在此 Hermes profile 使用其原生安裝功能，並依來源及套件需求確認授權、登入或重新載入。
- 請米亞操作特定本機應用程式時，先說明目標視窗與要做的事。Windows 桌面必須可用，應用程式支援程度視控制項而定。

## 驗證

`integration/hermes/` 包含 Codex 交接、插件安裝與桌面驗證程式。啟動器的 `-SelfTestTaskFile` 可將 UTF-8 任務檔送入真正的米亞對話入口，匯出工具事件及最終文字至 `.mia-runtime/checks/`，並使用獨立測試會話資料。
