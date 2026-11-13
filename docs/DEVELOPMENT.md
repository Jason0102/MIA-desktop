# 開發與發佈

`src/Mia` 為 WPF 應用程式；`tests` 是不依賴測試框架的檢查執行檔；`art` 保留公開預覽所需的最終資產。歷史 raw 圖、生成中間檔、個人設定、驗證截圖與舊 ZIP 未收錄。

`build.ps1 -Test` 執行模擬服務檢查後，publish 自帶 runtime 的 win-x64 程式到 `dist/MIA-3.1-win-x64`。若專案內有 `.tools/dotnet/dotnet.exe` 就使用它，否則使用 PATH 中的 dotnet。

發佈前執行 `python scripts/check_public.py`，檢查金鑰範例、敏感檔名、預覽依賴及素材數量。此檢查不取代完整秘密掃描。外部維護者不需要任何真實金鑰即可建置與跑一般檢查。

首次發佈流程：建立乾淨 Git 歷史、提交 main、建立 3.1 分支並推送兩者。不要把本機舊版 dist、私人 api_key 或測試資料用 `git add -f` 加入。

若需公開二進位包，從本公開版重建，不使用原工作目錄的舊封裝；封裝前檢查內容及私密資料。角色範例與 api_key.example 可以附帶，真實 api_key 不可附帶。
