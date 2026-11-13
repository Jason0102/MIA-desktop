# 實作盤點

本文件依 2026-11-13 的原始碼與素材建立；歷史 README 的計畫與舊版測試數字不視為本次測試結果。

| 子系統 | 實作位置（src/Mia） | 判讀 |
| --- | --- | --- |
| 啟動／設定 | App.cs、Services/SettingsStore.cs、SettingsWindow.cs | 單一執行個體、設定保存、金鑰匯入、DPAPI、右鍵介面導航 |
| 置頂與桌面 | NativeDesktop.cs、PetWindow.cs、FloatingSurface.cs | 角色每 150 ms 檢查置頂需要，避免搶焦點 |
| 移動／探索 | PetWindow.Exploration.cs、ExplorationRules.cs、LocomotionRules.cs | 走跑節奏、窗頂可達性、坐下／跳下與原地動作 |
| 游標／螢幕 | CursorAttention.cs、ScreenRules.cs、PetWindow.Screens.cs | 機率性視線反應、活動螢幕限制與拔除回復 |
| 玩球 | ToyBallWindow*.cs、PetWindow.WindowBall.cs、BallPlayRules.cs、WindowBallRules.cs | 球體物理、窗頂支撐、追逐、抱球、拋球與頂球 |
| 時段陪伴 | TimedCompanion.cs、PetWindow.TimedCompanion.cs | 四個時段、八個動作；冷卻、優先權、跨時段中斷 |
| 聊天／截圖 | ChatWindow.cs、CaptureService.cs、RegionPickerWindow.cs | 浮動橫幅、框選多圖、取消與個別移除 |
| 模型與記憶 | Services/AiService.cs、Services/ConversationStore.cs | SSE、搜尋引用、推理規則、用量快取資訊、本地成功問答記錄 |
| 語音 | Services/VoiceService.cs、PcmStreamPlayer.cs、SpeechTags.cs | 分句、串流、播放取消、標籤解析與顯示過濾 |
| 動畫 | PetAnimationBank.cs、SpeechAnimation.cs、Assets | 一般圖集、互動姿態、時段與 42 種情緒圖集 |

時段動作只在地面陪伴且無互動時嘗試。對話／設定開啟會中止，思考、語音、拖曳、摸頭與物理狀態優先。一般動作預覽共 40 組 124 格；情緒庫另有 42 種共 84 格。預設說話重用既有素材，不另算新圖。

未實作或尚未驗證：非 Windows 平台、音素同步嘴型、歷史對話語意檢索、永久服務端快取、所有帳號的模型／聲線支援、最新版本完整原生多螢幕人工驗收。程式保留舊 FocusClock 規則測試，不代表設定介面仍提供第三種模式；目前使用者模式為陪伴與探索。
