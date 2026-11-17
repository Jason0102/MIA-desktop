# MIA 的 Hermes 與 Codex 整合

MIA 維持桌面角色、對話與語音，透過只聽本機的 Hermes API 提交任務。Hermes 管理會話並選擇工具。`mia_codex_run` 交給本機 Codex CLI 執行工作，Hermes 再把文字與產物資訊回傳給米亞。

## 安裝與執行

需要 Windows x64、.NET 8 SDK（建置時）、原生 Codex CLI，以及 Hermes 支援的模型帳號。從 `3.3` 分支目錄開啟 PowerShell：

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

金鑰會以 Windows DPAPI 儲存在「文件／MIA Workspace／state」，綁定目前使用者。請選擇自己的帳號可用且支援工具的模型。完成首次設定後，可使用 `啟動米亞.vbs` 或直接執行 `Start-Mia.ps1`。米亞結束時，啟動器會關閉這次啟動的 Gateway。`.mia-runtime` 提供 Hermes、Python 與 Codex 執行依賴，桌寵建置成品在 `dist/MIA-3.3-win-x64`。在另一台電腦請重新安裝及登入。

## 使用方式

Discord 與 PC 共用人物設定檔。Discord 使用文字表現米亞的活潑、好奇與溫暖，包含確認、任務結果、追問及異常提示；依情境自然使用語尾、幽默與適量表情符號，使用者要求正式或精簡時配合調整。語音與動畫控制標籤仍由介面處理，不直接顯示在訊息內。

同一工作目錄已有 `MIA-desktop-3.2/.mia-runtime` 時，3.3 啟動器會在本版尚未安裝 Hermes 的情況下沿用該安裝。Python 虛擬環境保持原路徑，載入的 MIA 整合程式則來自 3.3；不要搬動虛擬環境。兩版沿用「文件／MIA Workspace／state」的設定、金鑰及使用者綁定，同一時間只啟動一版。若 3.3 有自己的安裝則優先使用。另一台電腦仍需重新安裝。

Discord 私訊的一般追問會延續同一使用者、同一頻道最近 30 分鐘內已完成回覆或因服務重啟而中斷的工作，保留原生 Hermes 對話上下文。使用「新任務：」前綴明確另開工作；上一工作尚在進行時，新訊息維持獨立並行。補充進行中的工作、切回較早工作或跨越 30 分鐘時，請使用 Discord「回覆」選取原訊息。未綁定使用者與其他頻道不會沿用此上下文。若重啟時上一回合仍在執行，Hermes 會暫停會話，避免重複操作；回覆提示並傳送 `/discard` 可略過中斷回合、保留前文並繼續已排隊訊息。近期會話也可直接傳送 `/discard`；不會自動重做未確認是否完成的操作。

本機桌面目前綁定唯一使用者佳勳（Jason）。隨程式的 `users.json` 保存使用者與來源綁定，可在 `state/mia/users.json` 放置自訂設定覆寫。兩種對話模式都會附帶解析後的身分。來源與帳號須由傳輸端指定，未綁定來源不會沿用桌面身分；Discord 帳號綁定與跨使用者記憶隔離詳見下方說明。

送出新訊息會立即中斷目前及排隊中的舊語音，保留回覆文字與背景工作，新回覆可正常播放。行事曆活動支援 `description` 說明／備註，可查詢、更新或清空，多行文字最多 10000 字元。

- 右鍵點人物依序切換輸入橫幅、工作中心、收起。主對話持續承接上下文，由 Hermes 判斷閒聊、新任務或既有任務的補充。新任務可並行執行，補充優先送入原任務，暫時無法接收時等待目前步驟結束後接續原會話。最近 50 項任務索引存於工作目錄，可供主對話辨認已完成成果。工作中心與輸入橫幅同寬，只顯示進行中的任務、狀態、個別停止與必要的回覆控制。成果附上任務標題，語音依序播放。
- 主對話從 Hermes 串流抽取 `message`，不顯示分派 JSON。啟用語音時逐句生成，音訊開始播放才逐步顯示該句文字；關閉語音時直接顯示收到的文字，不呼叫 ElevenLabs。任務完成通知立即發出，成果文字共用相同播放流程；語音失敗或停止仍保留完整文字。
- 每句回應的 `[audio: 情緒]` 同時驅動語音與 EMO 動畫，標籤不顯示在聊天文字中。沒有可辨識標籤時使用 EMO-028（peaceful）。陪伴模式下，有執行中的任務便持續播放 ACT-008（thinking），EMO 說話動畫優先，說完後若仍有任務則恢復 ACT-008。動畫狀態不受原本 120 秒心情期限影響。
- `state/hermes` 儲存米亞專用的 Hermes 會話，`state/mia` 儲存桌寵設定、聊天顯示副本與依身分保存的記憶，`state/codex` 儲存米亞啟動的 Codex 狀態。設定頁的「重置米亞記憶」須再次確認。連接 Hermes 時，重置會清除該使用者的本機和綁定 Discord 對話、共用記憶及互動資料，並保留已交付的成果檔案。若有工作進行中，請先完成或停止工作。
- 啟動會在 Windows「文件」下建立 `MIA Workspace`。工作副本與產物放在 `jobs/<任務識別>/`，啟動器將任務的暫存路徑指向 `temp/`。可把待處理檔案放入 `input/` 或直接指定 Windows 帳號有權讀取的 C 槽絕對路徑。對話橫幅可拖入任意格式的 C 槽檔案，單次最多 16 份；32 MiB 以內優先複製到該次工作資料夾，超過副本預算的檔案以已授權的 C 槽原始路徑供任務讀取。新增程式與測試仍留在工作資料夾，任務相關文件可依要求在 C 槽原位讀寫。
- 在米亞說「請使用 `mia_install_extension` 安裝內附診斷擴充」即可請 Hermes 自動安裝已隨專案提供的無依賴範例，並檢查註冊結果。第三方插件可在此 Hermes profile 使用其原生安裝功能，並依來源及套件需求確認授權、登入或重新載入。
- MIA 停用通用命令工具；桌面操作透過 `mia_computer_use` 接入，PowerPoint 驗證透過 `mia_powerpoint_check` 在主機執行。`mia_list_directory` 與 `mia_codex_run` 的來源路徑檢查會拒絕 D 槽、其他磁碟與重新導向的路徑。Codex CLI 保留 Windows 工作區沙箱，以 `--add-dir C:/` 開放 C 槽任務檔案讀寫；Windows 系統本身的 ACL 仍有效。C 槽限制適用於米亞的資料工具，現有 Windows 沙箱無法保證 Codex 子程序也有整顆 D 槽的讀取隔離。

## 驗證

### 技能與任務回顧

MIA 對話可查詢、建立及修訂 Hermes 技能。可以說「把這次驗證過的報告流程保存成技能」，再開新任務要求套用它。米亞用原生 `skills_list`、`skill_view` 與 `skill_manage` 查找、讀取及保存流程。使用者要求維護技能時由背景任務執行，已保存的技能可在不同任務間重用。

Hermes 原生背景回顧會在符合工具迭代門檻的回合完成後，檢查執行過程並保存可重用的做法。門檻沿用 profile 的 `skills.creation_nudge_interval`（預設 15 次工具迭代），也可透過 `auxiliary.background_review.enabled` 設定是否自動回顧。技能預設保存在 `state/hermes/skills`，啟動器只在缺少行事曆技能時補上內附版本。

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

## Discord 身分與資料分區

Discord 連線準備工具已接入啟動器，實際啟用須有 Bot Token 與完整的數字 User ID。先退出米亞，再執行：

```powershell
./Start-Mia.ps1 -DiscordUserId '完整數字UserID' -DiscordTokenFile 'C:\private\mia-discord-token.txt'
```

預設綁定到 `local-jason`（佳勳／Jason）。新增其他使用者時指定 `-DiscordMiaUserId` 及 `-DiscordDisplayName`。電子郵件和使用者名稱不作為身分驗證。設定保存到 `state/mia/users.json`，Bot Token 使用 Windows DPAPI 保存到 `state/discord.key`，綁定目前 Windows 使用者。以原本的 VBS 啟動時會讀取該設定。

PC 和 Discord 共用同一個 Hermes Gateway 與代理人執行架構。PC 直接與人物互動。Discord 使用同一份 `role.json`，以文字訊息回傳最終結果與附件。Discord 關閉工具進度、中間訊息與自動語音。

每個 Discord 帳號使用 `state/hermes/profiles/mia-discord-<UserID>` 保存對話與技能，`identity.json` 記錄 MIA 使用者與管道。PC 對話與技能保存在 `state/hermes`。同一使用者的長期記憶及互動資料可跨 PC 與 Discord 共用，並保留來源。啟動時將既有 Hermes 記憶匯入一次。僅登錄的帳號可使用 Bot。

可以私訊米亞「用這個 YouTube 連結的字幕整理摘要，做成 PDF 和 Word 傳給我」。公開字幕可用時會直接讀取，摘要會標示來源及字幕類型。遇到無字幕或影片限制存取時，可以上傳字幕檔供整理。文件支援 PDF、DOCX、TXT 和 Markdown，並以 Discord 附件交付。現有安裝若缺套件，先執行 `Install-Hermes.ps1` 再以原捷徑啟動。

Discord 使用 Hermes 原生圖片、音訊、影片及文件附件傳輸，單個接收檔案上限 32 MiB，傳送仍受 Discord 的伺服器限制。附件傳輸與內容理解依模型及格式能力而定。已綁定帳號載入 PC 的工具集、Codex 插件與 MCP 設定，依 Hermes 相同的操作審批及檔案規則執行。

同一 Discord 私訊中的每則新訊息會建立獨立 Hermes 任務會話，可同時執行並各自回覆原始訊息。這沿用 PC 工作中心每件工作一個會話的執行方式。要接續或修改某件工作，使用 Discord「回覆」選取原要求或米亞的結果；停止任務時回覆原要求並傳送 `/stop`。直接另發訊息會建立新工作。任務路由按帳號及私訊頻道保存，重啟後仍可回覆舊要求續接。DC 關閉 busy 轉向提示與工具進度，保留最終結果及必要的授權詢問。

Discord 私訊工作會同步出現在 PC 工作中心，標示 `Discord` 來源、原始要求及執行狀態。完成後移入「任務成果」，可查看回覆並開啟本機仍存在的附件。畫面開啟時每兩秒更新；關閉工作中心不影響執行。狀態以原生 Hermes 執行／傳送完成事件寫入 `state/mia/discord-work`，重啟前未完成的工作會標為中斷。桌面只顯示目前本機身分綁定的 Discord 工作，不顯示其他使用者資料。DC 工作的補充與停止仍透過 Discord 回覆原訊息操作。

2026-11-17 實測：正式啟動器的 Discord Gateway 上線成功；產生的文字、PNG、WAV 附件傳送後重新下載，比對內容一致；綁定使用者私訊傳入圖片後，原生視覺路由成功，模型約 18 秒回覆圖片描述及綁定身分，會話保存在該 Discord profile。第二個真實帳號、影片及音訊轉錄尚未實測；多使用者路由與記憶持久隔離已通過離線測試。

安裝器會安裝 `discord.py==2.7.1` 與 `audioop-lts==0.2.2`。Bot 須在 Discord Developer Portal 開啟 Message Content Intent，邀請時允許檢視頻道、讀取歷史、傳送訊息及附加檔案。Token 檔可僅包含 Token，或使用既有 `api_key` 的 `[discord]` 區段與 `token = ...` 欄位；不要提交至版本庫。Discord 分區在記憶體中取得明確授權的模型 API 金鑰，不會繼承 Bot Token 或其他桌面憑證。

伺服器頻道中請標記 MIA Bot 本身（APP），不是同名身分組；也可私訊 Bot。音訊附件與語音頻道通話不同，目前整合的是附件傳輸。

離線隔離測試：使用 Hermes Python 執行 `integration/hermes/check_discord_identity.py`。實際附件測試可執行 `integration/hermes/check_discord_live.py --token-file api_key --channel <頻道ID>`；這會在指定頻道傳送產生的文字、PNG 和 WAV 檔，重新下載並比對內容。使用者傳入附件與模型回覆須另外驗證。

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

若整合測試提示 Windows asyncio 無法啟動，請在允許本機迴路的 PowerShell 環境重跑 `integration/hermes/check_mcp_configuration.py`（使用 MIA 的 Hermes Python）。Windows 的事件迴圈需要本機迴路建立喚醒 socket，受限環境可能在 MCP 連線計時開始前就卡住。

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
