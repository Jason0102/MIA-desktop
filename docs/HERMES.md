# MIA 3.2 的 Hermes 與 Codex 整合

MIA 維持桌面角色、對話與語音，透過只聽本機的 Hermes API 提交任務。Hermes 管理會話並選擇工具。`mia_codex_run` 交給本機 Codex CLI 執行工作，Hermes 再把文字與產物資訊回傳給米亞。

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

金鑰會以 Windows DPAPI 儲存在「文件／MIA Workspace／state」，綁定目前使用者。請選擇自己的帳號可用且支援工具的模型。完成首次設定後，可使用 `啟動米亞.vbs` 或直接執行 `Start-Mia.ps1`。米亞結束時，啟動器會關閉這次啟動的 Gateway。`.mia-runtime` 提供 Hermes、Python 與 Codex 執行依賴，桌寵建置成品在 `dist/MIA-3.2-win-x64`。在另一台電腦請重新安裝及登入。

## 使用方式

- 右鍵點人物依序切換輸入橫幅、工作中心、收起。主對話持續承接上下文，由 Hermes 判斷閒聊、新任務或既有任務的補充。新任務可並行執行，補充優先送入原任務，暫時無法接收時等待目前步驟結束後接續原會話。最近 50 項任務索引存於工作目錄，可供主對話辨認已完成成果。工作中心與輸入橫幅同寬，只顯示進行中的任務、狀態、個別停止與必要的回覆控制。成果附上任務標題，語音依序播放。
- 主對話從 Hermes 串流抽取 `message`，不顯示分派 JSON。啟用語音時逐句生成，音訊開始播放才逐步顯示該句文字；關閉語音時直接顯示收到的文字，不呼叫 ElevenLabs。任務完成通知立即發出，成果文字共用相同播放流程；語音失敗或停止仍保留完整文字。
- 每句回應的 `[audio: 情緒]` 同時驅動語音與 EMO 動畫，標籤不顯示在聊天文字中。沒有可辨識標籤時使用 EMO-028（peaceful）。陪伴模式下，有執行中的任務便持續播放 ACT-008（thinking），EMO 說話動畫優先，說完後若仍有任務則恢復 ACT-008。動畫狀態不受原本 120 秒心情期限影響。
- `state/hermes` 儲存米亞專用的 Hermes 會話與內建記憶，`state/mia` 儲存桌寵設定及聊天顯示副本，`state/codex` 儲存米亞啟動的 Codex 狀態。舊版米亞退出後，第一次啟動會移轉既有資料。設定頁的「清除 Hermes 的記憶」須再次確認，會清除米亞的 Hermes 對話、內建記憶及顯示副本，保留工作成果。
- 啟動會在 Windows「文件」下建立 `MIA Workspace`。工作副本與產物放在 `jobs/<任務識別>/`，啟動器將任務的暫存路徑指向 `temp/`。可把待處理檔案放入 `input/` 或直接指定 Windows 帳號有權讀取的 C 槽絕對路徑。單次可交付 16 份來源檔，合計不超過 32 MiB。來源會複製到該次工作資料夾。
- 在米亞說「請使用 `mia_install_extension` 安裝內附診斷擴充」即可請 Hermes 自動安裝已隨專案提供的無依賴範例，並檢查註冊結果。第三方插件可在此 Hermes profile 使用其原生安裝功能，並依來源及套件需求確認授權、登入或重新載入。
- MIA 使用的 Hermes 工具組停用通用本機檔案、命令及桌面操作工具。`mia_list_directory` 與 `mia_codex_run` 的來源路徑檢查會拒絕 D 槽、其他磁碟與重新導向的路徑。Codex CLI 仍使用原有的 Windows 工作區沙箱。C 槽限制適用於米亞的資料工具，現有 Windows 沙箱無法保證 Codex 子程序也有整顆 D 槽的讀取隔離。

## 驗證

### 記憶與歷史續接

- 主對話允許使用 `session_search` 按需搜尋／讀取 Hermes 舊對話，`mia_conversation_search` 補上中文片語比對（包括同一主會話中的舊訊息），以及 `mia_task_search` 搜尋完整成果索引。最近 50 筆僅是主對話的即時任務摘要，不是歷史續接的上限；完整索引支援分頁查詢。
- 明確的「請記住」要求透過路由回傳 `memory.content`，由 `mia_memory.py` 呼叫 Hermes 原生記憶工具，寫入 `state/hermes/memories/USER.md`。寫入後重新載入核對，成功才追加「已記住」確認；重複偏好不重複儲存，更新使用 `old_text` 比對，容量不足或寫入失敗不宣稱成功。
- 歷史任務續接會使用原 Hermes session ID，並帶入完整的已保存委託、完成回覆及檔案清單。缺少原 session 的早期移轉成果，則使用穩定會話搭配已保存的任務資料續接，無法還原從未保存的工具步驟。
- 完成訊息與重開米亞時的歷史顯示隱藏路徑及工作編號，檔案卡仍能點擊開啟。任務成果頁保留原始完成回覆與檔案資訊。
- `check_memory_history.py` 使用隔離的暫存 profile 驗證原生記憶的新增、更新、去重、重讀與失敗回報，以及舊對話搜尋和 75 筆任務分頁。測試不改動使用者的記憶。

`integration/hermes/` 包含 Codex 交接、插件安裝與桌面驗證程式。啟動器的 `-SelfTestTaskFile` 可將 UTF-8 任務檔送入真正的米亞對話入口，在工作目錄的 `checks/` 寫入測試用聊天副本、工具事件及最終文字。

`MIA.exe --reply-self-test` 在指定的 `MIA_DATA_DIR` 輸出 `reply-check.json`，使用本機合成 PCM 驗證 Windows 播放、逐步文字、情緒切換、停用語音與 ACT-008 恢復，不呼叫雲端語音服務。執行前先退出正常的米亞實例，並將測試資料目錄設於工作範圍的 `checks/`。

### 自動推理強度

主對話路由以 low 執行，Hermes 依任務複雜度回傳 low／medium／high，背景任務透過 model_options.reasoning_effort 覆寫全域設定。深入問答亦可作為 high 背景任務；沒有有效分類時採 medium。這是每個請求的強度選擇，不是在同一推理過程中動態切換。

Codex 委派工具接受 reasoning_effort，透過 model_reasoning_effort 傳給 CLI，未提供時採 medium。補充任務若改變強度，等待目前步驟結束後續接原會話；同強度可即時補充。

### 任務待確認

Hermes 的 clarify／approval 詢問會在對話顯示任務名稱與問題，工作中心顯示「待確認」。可直接回答；有多項詢問時先點「回覆這項詢問」指定對象。授權只接受明確的允許／拒絕文字或按鈕，不將條件式或含糊答覆視為許可。答案送至原 run、request 與 generation；重複事件不重複提問，送出失敗保留待確認狀態，任務結束後停用舊問題。

`MIA.exe --questions-self-test` 在隔離的 MIA_DATA_DIR 輸出 questions-check.json，驗證對話顯示、指定回覆、授權按鈕、重複事件和多項待確認狀態，不送出真實授權。
