# Kết quả spike Toolkit C với Creo 10.0.1.0

Máy thử: Windows 11, VS Build Tools 2022, .NET 10.0.14, Creo 10.0.1.0 (Creo 12.4.0.0 cũng có trên máy, chưa dùng).
Mã: `native/spike/` (`spike.c`, `spike2.c`, `build.cmd`, `out/run*.cmd`), `native/CreoBridge10/` (DLL), `native/spike/dllhost/` (host C#).

## Kết luận

| Việc | Kết quả |
|---|---|
| Build exe Toolkit async bằng cờ của `make_async_md` | Được |
| `ProEngineerStart` khởi động Creo 10 | Được, **nhưng phải chạy bằng quyền Administrator** |
| Nạp part, tái sinh, đọc khối lượng và hộp bao | Được (`d6_rectangular_beam_1.prt`: thể tích 9.5625e6 mm³, khối lượng 75.16 kg, hộp 150×50×1275) |
| Xuất STEP AP203 bằng `ProIntf3DFileWriteWithDefaultProfile` | Được (8.7 KB, đúng tên và kích thước) |
| `ProEngineerEnd` đóng Creo sạch | Được, các process `parametric`, `xtop`, `pro_comm_msg` đều thoát |

## Spike 3: DLL gọi từ C#

`native/CreoBridge10/` có C ABI phẳng (`cb_start`, `cb_set_workdir`, `cb_measure_part`, `cb_end`, `cb_last_error`), gọi từ console .NET 10 x64 qua P/Invoke.

| Việc | Kết quả |
|---|---|
| Toolkit async trong DLL nạp từ .NET 10 | **Được.** Không cần Runner C++; kiến trúc C# Runner + `CreoBridge10.dll` giữ nguyên |
| Khởi động Creo (`cb_start`) | khoảng 12 s |
| Một job trong phiên đang mở (nạp, tái sinh, khối lượng, xuất STEP) | khoảng 50–60 ms |
| 2 job liên tiếp trong cùng phiên | Được, kết quả giống nhau |
| Gọi từ thread khác | Cầu nối từ chối (`-100`), Creo không bị ảnh hưởng. Mọi lệnh gọi phải đi qua một thread riêng |
| Đóng (`cb_end`) | 11 ms, Creo thoát sạch |

Cần cạnh `DllHost.exe`: `CreoBridge10.dll`, `ucore68.dll`, `udata68.dll`. DLL build bằng `native/CreoBridge10/build.cmd`.

## Điều kiện bắt buộc

1. **Quyền Administrator.** Không có quyền này Creo khởi động rồi thoát ngay. Worker và Runner phải chạy cùng quyền với Creo trên máy CAD.
2. **Dòng lệnh khởi động:** `"…\Parametric\bin\parametric.exe" "…\Parametric\bin\parametric.psf"`, truyền qua biến môi trường vì tham số có dấu cách bị tách.
3. **`PRO_COMM_MSG_EXE`** trỏ tới `Common Files\x86e_win64\obj\pro_comm_msg.exe` bằng đường dẫn 8.3 (đường dẫn có dấu cách làm spawn lỗi).
4. Cạnh exe cần `ucore68.dll` và `udata68.dll` (lấy từ `Common Files\applications\simulate\x86e_win64\bin`).
5. Cờ build: `/MD`, `-DPT_ASYNC_MD`, thư viện `protkmd_NU.lib ptasyncmd.lib ucore.lib udata.lib`.

## Spike 4: dựng hình (Block, Cylinder)

Part mới = bản sao template `mmns_part_solid_abs.prt`; feature extrude tạo bằng cây element (feature không hoàn chỉnh, lấy section, vẽ, rồi `ProFeatureWithoptionsRedefine`). Sketch cần chiếu 2 datum tham chiếu (`ProSectionEntityFromProjection`) rồi `ProSectionAutodim` mới tái sinh được.

| Phát hiện | Chi tiết |
|---|---|
| Độ cao nền | Giá trị FROM của extrude bị đảo dấu và FROM âm làm Creo treo. Thay bằng datum plane lệch khỏi mặt phác (`make_offset_datum`); đo lại: nền 0/+30/−30, cao 50 → Y 0..50 / 30..80 / −30..20 |
| Hướng extrude | `PRO_EXT_CR_IN_SIDE_TWO` (flip=0) đi về phía **+** của trục (Y với TOP, X với RIGHT, Z với FRONT); flip=1 đi về phía − |
| Ánh xạ toạ độ phác → thế giới | TOP: X = −sx, Z = sy. RIGHT: Z = −sx, Y = sy. FRONT: X = sx, Y = sy. Lớp host phải đổi dấu theo bảng này |
| Hai khối trong một part | Khối thứ hai nối vào khối đầu (hộp bao 0..70 như dự kiến) |
| Khối lượng | Chưa đọc được (`-1`) vì template không có vật liệu; hộp bao đọc được |

`CreoGeaHost` (`src/Cadflow.CreoWorker/Creo/`) làm Block và Cylinder (trục H/X/D) qua `ICreoModeler` (bản thật `NativeCreoModeler`, bản ghi lại cho test). Smoke test trên Creo thật (`tools/CreoGeaSmoke`: thân 200×100×50 + 3 hình trụ) ra đúng hộp bao (−150,0,−100)–(150,90,100). Tank (vòng có lỗ + tấm đáy, bo góc bằng cung) và Cover (nắp + gờ) dựng bằng extrude cộng, không dùng cut; `box.gea` mặc định chạy trọn trên Creo thật, hộp bao (−50,0,−30)–(50,42,30) đúng (40 thân + 2 nắp). Thân và nắp nằm chung một part (chưa tách body/part). Chưa làm: Bushing, Radiator; chưa kiểm được rỗng bên trong bằng số (cần vật liệu để đọc thể tích).

Chạy không cần UAC (đặt tên script cần chạy trong `out/task-next.cmd`): Scheduled Task `CreoSpike` (quyền cao nhất, tạo một lần bằng terminal Administrator) trỏ tới `native/spike/out/run4.cmd`; `schtasks /run /tn CreoSpike` rồi đọc `out/run4-task-*.log`.

## Còn lại

- Dòng cảnh báo `Files\PTC\Creo was unexpected at this time` vẫn in ra lúc khởi động nhưng không chặn gì. Chưa rõ nguồn.
- Chưa thử: kill Runner giữa chừng rồi kết nối lại; đặt tham số (`ProParameterValueWithUnitsSet`); dựng hình bằng template/UDF; lắp assembly; bản vẽ.
- Chưa thử Creo 12.
