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
- 啟動會在 Windows「文件」下建立 `MIA Workspace`。工作副本與產物放在 `jobs/<任務識別>/`，啟動器將任務的暫存路徑指向 `temp/`。可把待處理檔案放入 `input/` 或直接指定 Windows 帳號有權讀取的 C 槽絕對路徑。對話橫幅可拖入任意格式的 C 槽檔案，單次最多 16 份；32 MiB 以內優先複製到該次工作資料夾，超過副本預算的檔案以已授權的 C 槽原始路徑供任務讀取。新增程式與測試仍留在工作資料夾，任務相關文件可依要求在 C 槽原位讀寫。
- 在米亞說「請使用 `mia_install_extension` 安裝內附診斷擴充」即可請 Hermes 自動安裝已隨專案提供的無依賴範例，並檢查註冊結果。第三方插件可在此 Hermes profile 使用其原生安裝功能，並依來源及套件需求確認授權、登入或重新載入。
- MIA 停用通用命令工具；桌面操作透過 `mia_computer_use` 接入，PowerPoint 驗證透過 `mia_powerpoint_check` 在主機執行。`mia_list_directory` 與 `mia_codex_run` 的來源路徑檢查會拒絕 D 槽、其他磁碟與重新導向的路徑。Codex CLI 保留 Windows 工作區沙箱，以 `--add-dir C:/` 開放 C 槽任務檔案讀寫；Windows 系統本身的 ACL 仍有效。C 槽限制適用於米亞的資料工具，現有 Windows 沙箱無法保證 Codex 子程序也有整顆 D 槽的讀取隔離。

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

## 排程、瀏覽器與行事曆

- 在對話中輸入「每天台北時間 16:00 提醒我喝水」或「每週五 17:00 執行指定任務」。也可以查詢、修改、暫停、恢復或刪除排程。純文字提醒不需要在到期時呼叫模型。從系統匣的「排程結果與提醒」可讀取通知與歷史。桌寵及 Gateway 須運行才能在到期時執行，排程可能受約一分鐘的檢查間隔影響。重啟後依 Hermes 的補跑規則處理。
- 請米亞操作網站時，瀏覽器可開頁、讀取文字與控制項、點選、填寫及下載。下載檔案放在工作區的 `downloads`。首次需要手動登入時，可用 `./Start-Mia.ps1 -BrowserLogin` 開啟可見瀏覽器，登入資料保存在米亞專用的 Chromium 設定檔。若安裝時略過 Chromium，可重跑 `Install-Hermes.ps1` 補裝。
- 在對話中可以請米亞新增、查詢、預覽、修改或取消個人事件。日曆事件存入 MIA Workspace 的 SQLite，並支援全天和重複事件。衝突檢查先比較時間，再用 Google Maps 的網頁路線查詢估算前後交通。缺少地點或網頁無法查指定時段，米亞會說明該段交通尚未確認。此行事曆流程暫無提前提醒。
- 如需在一次 Python 執行中組合 Hermes 工具，先安裝並啟動使用 Linux 容器的 Docker Desktop，再用 `./Start-Mia.ps1 -EnableIsolatedCode` 啟動。腳本在獨立容器執行，限制 60 秒與 12 次工具呼叫，容器無主機目錄掛載及直接網路。經 Hermes 提供的工具呼叫仍適用個別工具的權限及網路設定。需要下載容器映像時，由 Docker 主機完成下載。

## MCP 外部工具

內附本機行事曆伺服器會在啟動時註冊。要設定其他 MCP 伺服器，先結束 MIA，再從專案目錄執行下列命令。新增時使用服務提供的 URL 或受信任的 stdio 命令，依服務要求選 OAuth 或標頭授權。重新啟動 MIA 後，啟用的工具可用於米亞任務。

```powershell
./Start-Mia.ps1 -McpCommand @('list')
./Start-Mia.ps1 -McpCommand @('add','example','--url','https://example.org/mcp','--auth','oauth')
./Start-Mia.ps1 -McpCommand @('test','example')
./Start-Mia.ps1 -McpCommand @('login','example')
./Start-Mia.ps1 -McpCommand @('configure','example')
./Start-Mia.ps1 -McpCommand @('disable','example')
```

使用 `enable` 可重新啟用，使用 `remove` 可移除外部設定。已停用的內附行事曆可用 `enable mia_calendar` 恢復。

## 工作看板與 Webhook

已移除獨立看板頁。直接在主對話要求米亞拆分工作並建立前置依賴，透過 `mia_work_cards` 保存到 Hermes 看板資料庫；工作中心統一顯示排隊、執行、待審核與阻塞狀態。任務結果會先進入審核，通過後才派發依賴它的卡片。在對話說「這張通過」、「退回，請補上來源」或「解除阻塞，使用這份資料」即可操作，指涉不明時會先釐清。工具只接受主對話的變更，背景任務不能自行審核。重啟後，先前中斷的任務會標為阻塞，避免自動重複執行。

Webhook 僅接收經簽章驗證的事件。先在米亞建立排程並取得排程 ID，再建立規則檔。以下範例在 `report-ready` 收到 `ready` 事件時執行指定排程。

```json
{"report-ready":{"event":"ready","job":"替換為排程 ID","secret_env":"MIA_WEBHOOK_SECRET_REPORT"}}
```

在啟動用的環境設定至少 32 個字元的 `MIA_WEBHOOK_SECRET_REPORT`，執行 `./Start-Mia.ps1 -WebhookRules .\rules.json`。本機端點預設為 `http://127.0.0.1:8644/webhooks/report-ready`，可用 `-WebhookPort` 改埠。遠端來源可經使用者配置的 HTTPS 轉送服務接入。

POST 請求的 JSON 本文需包含 `{"event":"ready"}`。以 Unix 秒填入 `X-MIA-Timestamp`，以穩定且唯一的事件 ID 填入 `X-MIA-Delivery`。用規則的密鑰對 `時間戳.事件ID.原始本文` 計算 HMAC-SHA256，將小寫十六進位結果填入 `X-MIA-Signature`。本文的其他欄位不會傳入任務。重新送出同一事件 ID 與本文可查詢執行狀態，並須以有效時間戳重新簽章。排程忙碌時會延遲重試，已開始但被關機中斷的事件會標為狀態不確定。排程結果可從米亞的系統匣查看。

## 桌面與長期模板

`mia_templates` 將使用者要求重用的檔案保存至 `MIA Workspace/templates/<版本ID>/`，不覆蓋既有版本。主代理可列出模板，Codex 每次交接也會收到索引；這是按需求選擇模板，並非無條件套用任何檔案。

`mia_powerpoint_check` 在 Windows 使用者桌面上唯讀開啟簡報、停用文件巨集並匯出 PNG 至工作區 `checks/`。完成回條只確認成功渲染，代理仍需檢視圖片才能確認版面；不會將 XML 檢查冒充視覺驗證。檔案副本、程式和測試仍位於工作目錄。

`mia_computer_use` 包裝原生 Hermes computer use，保留 session 隔離、審批與取消。主對話先建立背景工作，避免路由階段直接操作桌面。先 capture/list_windows 確認目標，操作後再次驗證；驅動缺失或權限拒絕會如實回報。Windows UAC、受保護目錄及應用程式自身的限制仍然適用。

`啟動米亞.vbs` 維持原入口。進度視窗依工作區、工具設定、Gateway 就緒及人物載入階段更新；人物首幀繪製後才關閉。更新前使用 `退出米亞.vbs` 正常結束舊版本，再建置及重開。
