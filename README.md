# UltraCompressor

NĂ©n hĂ ng loáº¡t áº£nh, video, Ă¢m thanh, GIF vĂ  PDF theo thÆ° má»¥c. LĂ  báº£n viáº¿t láº¡i cá»§a
â€œUltraCompressor Pro v12â€, giá»¯ nguyĂªn tham sá»‘ nĂ©n cÅ© nhÆ°ng sá»­a háº¿t nhá»¯ng chá»— báº£n gá»‘c
lĂ m máº¥t dá»¯ liá»‡u hoáº·c bĂ¡o lá»—i sai.

- **.NET 10**, khung chá»§ WinForms, giao diá»‡n lĂ  web (HTML/CSS/JS thuáº§n) cháº¡y trong WebView2
- KhĂ´ng cáº§n bÆ°á»›c build giao diá»‡n, khĂ´ng phá»¥ thuá»™c npm
- Dá»¯ liá»‡u ngÆ°á»i dĂ¹ng Ä‘áº·t á»Ÿ `%LOCALAPPDATA%\UltraCompressor`, nĂ¢ng cáº¥p khĂ´ng máº¥t dá»¯ liá»‡u

---

## CĂ i Ä‘áº·t

```powershell
# CĂ´ng cá»¥ ngoĂ i láº¥y tá»« thÆ° má»¥c tools\ (Ä‘áº·t ffmpeg.exe vĂ  gifsicle.exe vĂ o Ä‘Ă³)
.\setup.ps1

# Hoáº·c chá»‰ Ä‘á»‹nh tÆ°á»ng minh
.\setup.ps1 -FFmpeg 'D:\tools\ffmpeg.exe' -Gifsicle 'D:\tools\gifsicle.exe'

# CĂ i kĂ¨m runtime .NET Ä‘á»ƒ cháº¡y Ä‘Æ°á»£c trĂªn mĂ¡y chÆ°a cĂ i .NET (tá»‡p lá»›n hÆ¡n nhiá»u)
.\setup.ps1 -SelfContained
```

á»¨ng dá»¥ng Ä‘Æ°á»£c cĂ i vĂ o `%LOCALAPPDATA%\UltraCompressor` vĂ  táº¡o lá»‘i táº¯t trĂªn desktop.

### CĂ´ng cá»¥ ngoĂ i

| CĂ´ng cá»¥ | Cáº§n cho | Ghi chĂº |
|---|---|---|
| `ffmpeg.exe` | áº£nh, video, Ă¢m thanh, GIF | Báº¯t buá»™c. Láº¥y tá»« [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) |
| `gifsicle.exe` | tá»‘i Æ°u GIF thĂªm | KhĂ´ng cĂ³ váº«n nĂ©n GIF Ä‘Æ°á»£c, chá»‰ kĂ©m hiá»‡u quáº£ hÆ¡n |
| `gswin64c.exe` | nĂ©n PDF | CĂ i [Ghostscript](https://ghostscript.com/releases/) báº£n **Ä‘áº§y Ä‘á»§** |
| `ffplay.exe` | xem trÆ°á»›c trÆ°á»›c/sau | Tuá»³ chá»n |

> **LÆ°u Ă½ vá» Ghostscript.** Báº£n `gswin64c.exe` Ä‘i kĂ¨m báº£n v12 cÅ© chá»‰ lĂ  tá»‡p stub 93 KB
> thiáº¿u DLL, cháº¡y lĂªn bĂ¡o `Can't load Ghostscript DLL`. Báº£n nĂ y nháº­n ra tĂ¬nh tráº¡ng Ä‘Ă³
> ngay khi má»Ÿ á»©ng dá»¥ng vĂ  hÆ°á»›ng dáº«n cĂ i báº£n Ä‘áº§y Ä‘á»§, thay vĂ¬ Ă¢m tháº§m Ä‘Ă¡nh dáº¥u má»i tá»‡p
> PDF lĂ  â€œgiá»¯ nguyĂªnâ€.

á»¨ng dá»¥ng **cháº¡y thá»­** tá»«ng cĂ´ng cá»¥ chá»© khĂ´ng chá»‰ Ä‘á»c `--version`: nĂ³ táº¡o má»™t áº£nh GIF
nhá» rá»“i nĂ©n tháº­t, vĂ  cháº¡y má»™t lá»‡nh PostScript tá»‘i thiá»ƒu cho Ghostscript. LĂ½ do: trĂªn
chĂ­nh mĂ¡y nĂ y, `gifsicle.exe` in cáº£nh bĂ¡o â€œCan't load DLLâ€ rá»“i váº«n cháº¡y tá»‘t, cĂ²n
`gswin64c.exe` bĂ¡o lá»—i tÆ°Æ¡ng tá»± vĂ  há»ng tháº­t â€” chuá»—i cáº£nh bĂ¡o khĂ´ng Ä‘Ă¡ng tin.

---

## CĂ¡ch dĂ¹ng

1. **ThĂªm thÆ° má»¥c** â€” báº¥m nĂºt, báº¥m `Ctrl+O`, hoáº·c **kĂ©o tháº£ tá»« Explorer vĂ o cá»­a sá»•**.
   Tháº£ Ä‘Æ°á»£c cáº£ thÆ° má»¥c láº«n tá»‡p láº». ThÆ° má»¥c con Ä‘Æ°á»£c quĂ©t tá»± Ä‘á»™ng. Tá»‡p láº» Ä‘Æ°á»£c gom vá»
   thÆ° má»¥c chá»©a nĂ³, vĂ¬ á»©ng dá»¥ng luĂ´n lĂ m viá»‡c theo thÆ° má»¥c.
2. Chá»n **má»©c nĂ©n** vĂ  **cĂ¡ch ghi**.
3. **Báº¯t Ä‘áº§u**. Xem má»©c tiáº¿t kiá»‡m á»Ÿ cá»™t tÆ°Æ¡ng á»©ng.
4. Má»Ÿ **Chi tiáº¿t** Ä‘á»ƒ xem tá»«ng tá»‡p, tiáº¿n Ä‘á»™ tá»«ng tá»‡p, tá»‡p nĂ o bá»‹ giá»¯ nguyĂªn vĂ  vĂ¬ sao.
5. Báº¥m **â—«** trĂªn má»™t tá»‡p Ä‘á»ƒ **so sĂ¡nh song song** báº£n gá»‘c vá»›i báº£n Ä‘Ă£ nĂ©n.
6. **Duyá»‡t** Ä‘á»ƒ Ă¡p dá»¥ng, hoáº·c **HoĂ n tĂ¡c** Ä‘á»ƒ tráº£ báº£n gá»‘c vá».

### Tiáº¿n Ä‘á»™ tá»«ng tá»‡p

DĂ²ng thÆ° má»¥c hiá»‡n tĂªn tá»‡p Ä‘ang nĂ©n vĂ  pháº§n trÄƒm cá»§a nĂ³. Má»Ÿ **Chi tiáº¿t** sáº½ tháº¥y thanh
tiáº¿n Ä‘á»™ riĂªng cho tá»‡p Ä‘ang cháº¡y. Cáº§n cáº£ hai: thanh cá»§a thÆ° má»¥c chá»‰ Ä‘áº¿m sá»‘ tá»‡p, nĂªn má»™t
táº­p video 20 phĂºt káº¹t sáº½ trĂ´ng giá»‘ng há»‡t má»™t tá»‡p áº£nh nhá».

### So sĂ¡nh trÆ°á»›c / sau

Báº¥m **â—«** á»Ÿ má»™t tá»‡p Ä‘á»ƒ má»Ÿ mĂ n hĂ¬nh so sĂ¡nh: báº£n gá»‘c bĂªn trĂ¡i, báº£n Ä‘Ă£ nĂ©n bĂªn pháº£i, cĂ¹ng
má»™t khung hĂ¬nh á»Ÿ cĂ¹ng má»™t thá»i Ä‘iá»ƒm, kĂ¨m dung lÆ°á»£ng, khung hĂ¬nh, thá»i lÆ°á»£ng vĂ  bitrate.
Video vĂ  Ă¢m thanh cĂ³ nĂºt phĂ¡t tá»«ng bĂªn.

Nguá»“n hai bĂªn Ä‘Æ°á»£c Ä‘oĂ¡n nhÆ° sau:

| TĂ¬nh huá»‘ng | Báº£n gá»‘c | Báº£n nĂ©n |
|---|---|---|
| ÄĂ£ nĂ©n tháº­t | tá»‡p `.bak` | tá»‡p hiá»‡n táº¡i |
| Xuáº¥t ra thÆ° má»¥c khĂ¡c | tá»‡p trong thÆ° má»¥c gá»‘c | tá»‡p trong thÆ° má»¥c Ä‘Ă­ch |
| Cháº¡y thá»­, hoáº·c bá»‹ giá»¯ nguyĂªn | tá»‡p hiá»‡n táº¡i | *chÆ°a cĂ³* â€” mĂ n hĂ¬nh nĂ³i rĂµ vĂ¬ sao |

Viá»‡c láº¥y khung hĂ¬nh pháº£i dĂ² hai láº§n: nháº£y nhanh tá»›i gáº§n Ä‘Ă­ch rá»“i má»›i tinh chá»‰nh. Chá»‰
nháº£y má»™t láº§n thĂ¬ báº£n gá»‘c vĂ  báº£n nĂ©n rÆ¡i vĂ o hai thá»i Ä‘iá»ƒm khĂ¡c nhau (cáº¥u trĂºc GOP khĂ¡c
nhau) vĂ  so khĂ´ng Ä‘Æ°á»£c.

### Ba cĂ¡ch ghi káº¿t quáº£

| CĂ¡ch | Tá»‡p gá»‘c | Báº£n sao lÆ°u | Khi nĂ o dĂ¹ng |
|---|---|---|---|
| **Thá»­ trÆ°á»›c** (máº·c Ä‘á»‹nh) | khĂ´ng Ä‘á»¥ng | khĂ´ng cĂ³ | Xem trÆ°á»›c sáº½ tiáº¿t kiá»‡m bao nhiĂªu |
| **NĂ©n tháº­t** | thay tháº¿ | `.bak` cĂ¹ng thÆ° má»¥c | ÄĂ£ hĂ i lĂ²ng vá»›i káº¿t quáº£ |
| **Xuáº¥t thÆ° má»¥c khĂ¡c** | khĂ´ng Ä‘á»¥ng | khĂ´ng cĂ³ | Giá»¯ nguyĂªn thÆ° má»¥c gá»‘c, láº¥y káº¿t quáº£ Ä‘i nÆ¡i khĂ¡c |

á» cháº¿ Ä‘á»™ thá»­, á»©ng dá»¥ng **nĂ©n tháº­t tá»«ng tá»‡p Ä‘á»ƒ Ä‘o** rá»“i xoĂ¡ káº¿t quáº£, nĂªn báº¡n tháº¥y Ä‘Ăºng
nhá»¯ng gĂ¬ sáº½ xáº£y ra chá»© khĂ´ng pháº£i con sá»‘ Æ°á»›c lÆ°á»£ng. Báº¥m **Duyá»‡t** Ä‘á»ƒ nĂ©n tháº­t vĂ  thay tháº¿.

### PhĂ­m táº¯t

| PhĂ­m | TĂ¡c dá»¥ng |
|---|---|
| `Ctrl+O` | ThĂªm thÆ° má»¥c |
| `Ctrl+Enter` | Báº¯t Ä‘áº§u |
| `Space` | Táº¡m dá»«ng / Tiáº¿p tá»¥c |
| `?` | HÆ°á»›ng dáº«n |
| `L` | Nháº­t kĂ½ |
| `T` | Äá»•i giao diá»‡n sĂ¡ng/tá»‘i |
| `Esc` | ÄĂ³ng báº£ng chi tiáº¿t |

---

## Tham sá»‘ nĂ©n

Giá»¯ nguyĂªn nhÆ° báº£n gá»‘c. Xem `docs/PHASE0-REFERENCE.md` Ä‘á»ƒ Ä‘á»‘i chiáº¿u tá»«ng dĂ²ng vá»›i
code Ä‘Ă£ decompile.

| | Nháº¹ | CĂ¢n báº±ng | Máº¡nh |
|---|---|---|---|
| Video CRF / preset | 20 / slow | 23 / medium | 28 / veryfast |
| Video chiá»u rá»™ng tá»‘i Ä‘a | 3840px | 1920px | 1080px |
| áº¢nh `-q:v` | 3 | 5 | 10 |
| Ă‚m thanh | 320k | 192k | 128k |
| GIF `--lossy` | 20 | 40 | 80 |
| PDF `-dPDFSETTINGS` | /prepress | /ebook | /screen |

**NgÆ°á»¡ng tiáº¿t kiá»‡m tá»‘i thiá»ƒu** lĂ  con sá»‘ Ä‘á»™c láº­p trong CĂ i Ä‘áº·t, khĂ´ng gáº¯n vá»›i má»©c nĂ©n.
Báº£n gá»‘c gá»™p nháº§m hai thá»© nĂ y: ngÆ°á»¡ng tÄƒng dáº§n theo má»©c, nĂªn má»©c â€œMáº¡nhâ€ láº¡i khĂ³ Ä‘áº¡t
nháº¥t (file pháº£i nhá» hÆ¡n 98% má»›i Ä‘Æ°á»£c nháº­n) â€” ngÆ°á»£c vá»›i Ă½ Ä‘á»“.

---

## An toĂ n dá»¯ liá»‡u

- **Ghi Ä‘Ă¨ lĂ  nguyĂªn tá»­.** Sao chĂ©p báº£n gá»‘c sang `.bak` *trÆ°á»›c*, rá»“i má»›i thay tháº¿ tá»‡p.
  Báº£n gá»‘c dĂ¹ng `Move` hai láº§n; náº¿u láº§n hai há»ng thĂ¬ báº£n gá»‘c náº±m láº¡i trong `.bak` vĂ  tá»‡p
  chĂ­nh biáº¿n máº¥t.
- **KhĂ´ng bao giá» ghi Ä‘Ă¨ má»™t `.bak` Ä‘Ă£ cĂ³.** Báº£n `.bak` Ä‘áº§u tiĂªn lĂ  báº£n gá»‘c nguyĂªn váº¹n;
  ghi Ä‘Ă¨ nĂ³ nghÄ©a lĂ  máº¥t kháº£ nÄƒng quay láº¡i báº£n tháº­t.
- **PhiĂªn ghi nguyĂªn tá»­** (tá»‡p táº¡m rá»“i thay tháº¿) vĂ  bĂ¡o lá»—i Ä‘á»c thay vĂ¬ nuá»‘t im láº·ng.
  Treo mĂ¡y giá»¯a lĂºc lÆ°u khĂ´ng lĂ m máº¥t danh sĂ¡ch job.
- **Bá»™ lá»c loáº¡i trá»« máº·c Ä‘á»‹nh** bá» qua `*.bak`. Báº£n gá»‘c khĂ´ng lá»c gĂ¬, nĂªn cháº¡y láº§n hai
  sáº½ nĂ©n tiáº¿p chĂ­nh tá»‡p `.bak` mĂ  nĂ³ vá»«a táº¡o.
- **Bá» qua tá»‡p káº¿t quáº£ lá»›n hÆ¡n báº£n gá»‘c**, kĂ¨m lĂ½ do hiá»ƒn thá»‹ rĂµ.
- Kiá»ƒm tra dung lÆ°á»£ng á»• Ä‘Ä©a trÆ°á»›c khi nĂ©n hĂ ng loáº¡t.

---

## Nháº­t kĂ½

`%LOCALAPPDATA%\UltraCompressor\logs\ultra-YYYYMMDD.log`, xoay vĂ²ng á»Ÿ 4 MB.

á» má»©c `Debug` (Ä‘áº·t trong CĂ i Ä‘áº·t) nháº­t kĂ½ ghi **nguyĂªn vÄƒn tá»«ng lá»‡nh ffmpeg** kĂ¨m mĂ£
thoĂ¡t vĂ  20 dĂ²ng stderr cuá»‘i. ÄĂ¢y lĂ  thá»© cáº§n Ä‘á»ƒ cháº©n Ä‘oĂ¡n khi káº¿t quáº£ nĂ©n ká»³ láº¡ â€” báº£n
gá»‘c khĂ´ng lÆ°u láº¡i gĂ¬ nĂªn khĂ´ng tra Ä‘Æ°á»£c.

Xem ngay trong á»©ng dá»¥ng: nĂºt **Nháº­t kĂ½**.

---

## PhĂ¡t triá»ƒn

```powershell
dotnet build                                   # build cáº£ solution
dotnet test tests\UltraCompressor.Core.Tests   # 123 test
dotnet run --project src\UltraCompressor.App   # cháº¡y thá»­, khĂ´ng cáº§n publish
```

### Bá»‘ cá»¥c

```
src/UltraCompressor.Core/     LĂµi, khĂ´ng phá»¥ thuá»™c giao diá»‡n
  Models/                     Job, JobItem, AppConfig, cĂ¡c enum
  Pipelines/                  Má»™t pipeline cho má»—i loáº¡i media
  Processes/                  Cháº¡y tiáº¿n trĂ¬nh ngoĂ i, Ä‘á»c output theo dĂ²ng
  Scheduling/                 Engine, bá»™ quĂ©t thÆ° má»¥c, cá»•ng táº¡m dá»«ng, Æ°á»›c lÆ°á»£ng ETA
  Storage/                    Giao dá»‹ch tá»‡p, hoĂ n tĂ¡c, phiĂªn, kiá»ƒm tra dung lÆ°á»£ng
  Toolchain/                  TĂ¬m vĂ  kiá»ƒm tra kháº£ nÄƒng cháº¡y cá»§a cĂ´ng cá»¥ ngoĂ i
  Media/                      PhĂ¢n tĂ­ch output cá»§a ffmpeg
  Diagnostics/                Nháº­t kĂ½ theo ngĂ y

src/UltraCompressor.App/      Khung chá»§ + giao diá»‡n web
  Bridge/                     Cáº§u postMessage hai chiá»u, DTO, AppHost
  wwwroot/                    index.html, styles.css, app.js

src/UltraCompressor.Core/Compare/   Dá»¯ liá»‡u cho mĂ n hĂ¬nh so sĂ¡nh trÆ°á»›c/sau
tests/UltraCompressor.Core.Tests/   123 test cho lĂµi

docs/PHASE0-REFERENCE.md     Báº£ng tham sá»‘ trĂ­ch tá»« báº£n gá»‘c + 23 lá»—i Ä‘Ă£ tĂ¬m ra
reference/original-csharp/   MĂ£ nguá»“n báº£n gá»‘c Ä‘Ă£ decompile báº±ng ilspycmd, chá»‰ Ä‘á»ƒ Ä‘á»‘i chiáº¿u
artifacts/                   áº¢nh chá»¥p mĂ n hĂ¬nh, media máº«u, script kiá»ƒm thá»­ (khĂ´ng commit)
tools/                       ffmpeg.exe, gifsicle.exe â€” setup.ps1 chĂ©p sang thÆ° má»¥c cĂ i
publish/                     ThÆ° má»¥c staging mĂ  setup.ps1 xuáº¥t báº£n vĂ o (khĂ´ng commit)
```

Má»i thá»© liĂªn quan tá»›i dá»± Ă¡n Ä‘á»u náº±m trong thÆ° má»¥c nĂ y. RiĂªng dá»¯ liá»‡u lĂºc cháº¡y
(`config.json`, phiĂªn lĂ m viá»‡c, nháº­t kĂ½) Ä‘á»ƒ á»Ÿ `%LOCALAPPDATA%\UltraCompressor` â€” Ä‘Ă³ lĂ 
quy Æ°á»›c cá»§a Windows, Ä‘á»ƒ nĂ¢ng cáº¥p app khĂ´ng Ä‘á»¥ng máº¥t cáº¥u hĂ¬nh cá»§a ngÆ°á»i dĂ¹ng.

### VĂ i quyáº¿t Ä‘á»‹nh thiáº¿t káº¿ Ä‘Ă¡ng ghi

**VĂ¬ sao WinForms chá»© khĂ´ng WPF.** Giao diá»‡n lĂ  100% web nĂªn khung chá»§ chá»‰ lĂ  nÆ¡i Ä‘áº·t
WebView2. Báº£n Ä‘áº§u tiĂªn dĂ¹ng WPF vĂ  gáº·p lá»—i khĂ³ chá»‹u: cá»­a sá»• con WebView2 bá»‹ cáº¥p kĂ­ch
thÆ°á»›c theo Ä‘Æ¡n vá»‹ logic cĂ²n bá» máº·t váº½ theo Ä‘iá»ƒm áº£nh tháº­t, lá»‡ch Ä‘Ăºng há»‡ sá»‘ 1,25 trĂªn
mĂ n hĂ¬nh 125% â€” mĂ©p pháº£i giao diá»‡n bá»‹ cáº¯t máº¥t. WinForms + `ApplicationHighDpiMode`
xá»­ lĂ½ viá»‡c nĂ y Ä‘Ăºng, vĂ  Ä‘Ă¢y cÅ©ng lĂ  tá»• há»£p Ä‘Æ°á»£c kiá»ƒm thá»­ ká»¹ nháº¥t.

Náº¿u chuyá»ƒn khung chá»§ sang cĂ´ng nghá»‡ khĂ¡c, khai bĂ¡o DPI trong manifest.

**VĂ¬ sao Ä‘o tá»« bĂªn trong tiáº¿n trĂ¬nh.** Chá»¥p mĂ n hĂ¬nh tá»« PowerShell bá»‹ Windows áº£o hĂ³a theo
DPI, chá»‰ láº¥y Ä‘Æ°á»£c ~80% cá»­a sá»•, ráº¥t dá»… khiáº¿n tÆ°á»Ÿng giao diá»‡n bá»‹ cáº¯t trong khi thá»±c táº¿
khĂ´ng. `LogGeometryAsync` Ä‘o trong tiáº¿n trĂ¬nh Ä‘Ă£ khai bĂ¡o DPI-aware nĂªn con sá»‘ Ä‘Ă¡ng tin.
`UC_CAPTURE` + `UC_EVAL_JS` + `UC_CAPTURE_DELAY` (biáº¿n mĂ´i trÆ°á»ng) cho phĂ©p chá»¥p vĂ  thao
tĂ¡c giao diá»‡n khi kiá»ƒm thá»­.

**Cáº§u webâ†”lĂµi.** `postMessage` + JSON, khĂ´ng dĂ¹ng COM. Trang web khĂ´ng thá»ƒ tá»± gá»i hĂ m tuá»³
Ă½ trĂªn mĂ¡y ngÆ°á»i dĂ¹ng; bá» máº·t chá»‰ gá»“m nhá»¯ng lá»‡nh khai bĂ¡o sáºµn.

---

## Nhá»¯ng lá»—i tĂ¬m Ä‘Æ°á»£c á»Ÿ báº£n gá»‘c

Báº£ng Ä‘áº§y Ä‘á»§ 23 má»¥c á»Ÿ `docs/PHASE0-REFERENCE.md`. VĂ i lá»—i Ä‘Ă¡ng chĂº Ă½:

- **NhĂ¡nh GIF bá»‹ Ä‘áº£o ngÆ°á»£c.** `if (File.Exists(gifsicle))` nghÄ©a lĂ  *cĂ³* gifsicle thĂ¬ gá»i
  gifsicle (khĂ´ng giáº£m fps, khĂ´ng resize, khĂ´ng palettegen), *thiáº¿u* gifsicle má»›i gá»i ffmpeg.
  Káº¿t quáº£: filter `fps=15`/`fps=20` lĂ  code cháº¿t. Báº£n nĂ y luĂ´n cháº¡y ffmpeg trÆ°á»›c rá»“i má»›i
  gifsicle â€” má»©c Máº¡nh giáº£m Ä‘Æ°á»£c 44% trĂªn áº£nh kiá»ƒm thá»­.
- **NgÆ°á»¡ng cháº¥p nháº­n tÄƒng theo má»©c nĂ©n**, khiáº¿n má»©c Máº¡nh khĂ³ Ä‘áº¡t nháº¥t.
- **Há»§y job lĂºc Ä‘ang táº¡m dá»«ng sáº½ treo vĂ´ háº¡n** â€” vĂ²ng chá» khĂ´ng nhĂ¬n tháº¥y
  `CancellationToken`.
- **Ghostscript há»ng nhÆ°ng bĂ¡o lá»—i im láº·ng** â€” khĂ´ng kiá»ƒm tra mĂ£ thoĂ¡t, má»i PDF bá»‹ Ä‘Ă¡nh
  dáº¥u â€œgiá»¯ nguyĂªnâ€ mĂ  khĂ´ng cĂ³ lĂ½ do.
- **NĂºt â€œDuyá»‡tâ€ chá»‰ xoĂ¡ `.bak`**, khĂ´ng pháº£i duyá»‡t káº¿t quáº£; há»™p thoáº¡i xĂ¡c nháº­n cĂ²n ghi
  sai ná»™i dung.
- **ETA luĂ´n trá»…** vĂ¬ â€œÄ‘Ă£ xá»­ lĂ½â€ chá»‰ cá»™ng dá»“n sau khi tá»‡p xong.
- **Máº¥t EXIF/orientation** khi nĂ©n áº£nh (thiáº¿u `-map_metadata 0`).
- **EPNG cĂ³ thá»ƒ treo** vĂ¬ khĂ´ng cĂ³ `-nostdin`.
- Báº£n gá»‘c dĂ¹ng Ä‘Æ°á»ng dáº«n tÆ°Æ¡ng Ä‘á»‘i cho cĂ´ng cá»¥ ngoĂ i, nĂªn phá»¥ thuá»™c thÆ° má»¥c lĂ m viá»‡c
  hiá»‡n táº¡i.

---

## Káº¿t quáº£ Ä‘o tháº­t

CĂ¡c con sá»‘ dÆ°á»›i Ä‘Ă¢y Ä‘o trĂªn má»™t thÆ° má»¥c tháº­t: 2 táº­p video 1080p (19â€“20 phĂºt, h.264
~1500 kb/s) vĂ  2 áº£nh JPEG, tá»•ng 442 MB.

| Má»©c | Video | áº¢nh JPEG | Ghi chĂº |
|---|---|---|---|
| CĂ¢n báº±ng (CRF 23) | âˆ’5,5% | giá»¯ nguyĂªn | chá»‰ Ä‘Æ°á»£c Ă¢m thanh 249kâ†’128k |
| Máº¡nh (CRF 28) | âˆ’36,6% | giá»¯ nguyĂªn | Ä‘o trĂªn 2 phĂºt Ä‘áº§u |

**VĂ¬ sao má»©c CĂ¢n báº±ng gáº§n nhÆ° khĂ´ng Ä‘Æ°á»£c gĂ¬.** Táº­p gá»‘c Ä‘Ă£ nĂ©n sáºµn á»Ÿ bitrate tháº¥p
(~1374 kb/s video), nĂªn mĂ£ hoĂ¡ láº¡i á»Ÿ CRF 23 cho ra thĂ nh pháº©m lá»›n hÆ¡n báº£n gá»‘c. á»¨ng dá»¥ng
nháº­n ra Ä‘iá»u Ä‘Ă³ vĂ  **giá»¯ nguyĂªn báº£n gá»‘c** thay vĂ¬ ghi Ä‘Ă¨ báº±ng thá»© tá»‡ hÆ¡n. ÄĂ¢y lĂ  hĂ nh vi
Ä‘Ăºng, khĂ´ng pháº£i lá»—i. Muá»‘n tiáº¿t kiá»‡m tháº­t thĂ¬ dĂ¹ng má»©c Máº¡nh, hoáº·c nĂ¢ng ngÆ°á»¡ng cháº¥p
nháº­n trong CĂ i Ä‘áº·t.

Chi tiáº¿t vá» quy trĂ¬nh kiá»ƒm thá»­ á»Ÿ [`docs/TESTING.md`](docs/TESTING.md).
Kiáº¿n trĂºc á»Ÿ [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

