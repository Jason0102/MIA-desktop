# 共通素材與預覽維護

main 維護專案首頁、共通素材與離線預覽。應用程式開發、建置、執行及版本測試請前往 [3.1](https://github.com/Jason0102/MIA-desktop/tree/3.1)、[3.2](https://github.com/Jason0102/MIA-desktop/tree/3.2) 或 [3.3](https://github.com/Jason0102/MIA-desktop/tree/3.3) 分支。

角色圖集位於 `art/character/`，示範語音位於 `art/speech-audio/`。移動素材時，需同步更新 `art/v39-timed/catalog.json`、`catalog.js` 及根目錄情緒預覽頁的相對路徑。JSON 與 JavaScript 動畫目錄應保持一致。

在儲存庫根目錄執行 `python scripts/check_public.py`，檢查動畫數量、預覽依賴、示範音訊及公開檔案邊界。此檢查不需要 API 金鑰，也不取代完整秘密掃描。

用瀏覽器開啟根目錄 `動畫預覽.html` 可查看兩類預覽。維護時應確認分頁切換、動畫播放與語音試聽。保留現有素材製作編號，以利追溯來源。

main 的素材搬移不會同步修改各版本分支；需要更新某版本素材時，請在該分支依其程式使用的路徑整合。
