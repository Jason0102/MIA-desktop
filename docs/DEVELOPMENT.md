# 開發與發佈

`src/Mia` 為 WPF 應用程式；`tests` 是不依賴測試框架的檢查執行檔；`art` 保留公開預覽所需的最終資產。歷史 raw 圖、生成中間檔、個人設定、驗證截圖與舊 ZIP 未收錄。

`build.ps1 -Test` 執行模擬服務檢查後，publish 自帶 runtime 的 win-x64 程式到 `dist/MIA-3.2-win-x64`。可用 `-DotnetPath` 指定 SDK，或使用專案內 `.tools/dotnet/dotnet.exe` 或 PATH 中的 dotnet。

發佈前執行 `python scripts/check_public.py`，檢查金鑰範例、敏感檔名、預覽依賴及素材數量。此檢查不取代完整秘密掃描。外部維護者不需要任何真實金鑰即可建置與跑一般檢查。

3.2 延續公開 Git 歷史，在獨立目錄開發並推送至 `3.2` 分支。使用 Git 發佈源碼時，請保留 `.mia-runtime`、`dist`、金鑰與測試資料的忽略規則。

若需公開二進位包，從本公開版重建，不使用原工作目錄的舊封裝；封裝前檢查內容及私密資料。角色範例與 api_key.example 可以附帶，真實 api_key 不可附帶。
