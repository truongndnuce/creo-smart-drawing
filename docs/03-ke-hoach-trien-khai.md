# Kế hoạch triển khai `creo-smart-drawing`

> Trạng thái: **kế hoạch, chưa bắt đầu code** (2026-11-26).
> Đọc kèm: [`01-tong-quan.excalidraw`](01-tong-quan.excalidraw), [`02-luong-nghiep-vu.excalidraw`](02-luong-nghiep-vu.excalidraw), [`architecture.md`](architecture.md).

## 1. Mục tiêu và phạm vi

**Mục tiêu:** app Smart Drawing Creo chạy được bước 4 → 16 của luồng nghiệp vụ:
kỹ sư chọn dự án Creo, chọn sản phẩm, nhập thông số, gửi đơn → máy Creo dựng model, tạo bản vẽ, xuất file →
kết quả hiện trên web và được gửi sang Smart Vault. MVP là sản phẩm **Box**.

**Trong phạm vi repo này:** web order (`fe/`), backend + DB riêng của app Creo, worker, runner, bridge C++,
bộ asset Creo (`creo-lib/`), test, tài liệu.

**Ngoài phạm vi (app khác, làm song song hoặc sau):** App Quản trị (`cadflow-admin`), tách Smart Vault thành app riêng,
sửa repo Inventor. Repo Creo **không chờ** các app này: dùng adapter tạm (mục 3) và chỉ cần thống nhất hợp đồng API.

## 2. Nguyên tắc

1. **Rủi ro lớn nhất làm trước.** Bridge Toolkit C với Creo 10 chưa ai thử, nên spike nó ở giai đoạn 0 trước khi copy cả stack.
2. **Luôn chạy được không cần Creo.** Backend `sim` cho phép dev web/backend/test trên máy không cài Creo.
3. **Mỗi app một DB, nói chuyện qua API.** App Creo có DB `cadflow_creo` riêng; dữ liệu dự án/quyền lấy từ API App Quản trị, file kết quả gửi qua API Smart Vault.
4. **Copy, không link chéo repo.** Code `.gea` và model tham số được copy từ `inventor-generator-box`; ghi lại commit nguồn để đồng bộ về sau.
5. **Giữ giao thức worker ↔ server** (`api/worker/heartbeat|claim|jobs/{id}/progress|artifacts|complete|fail`) để worker Creo và Inventor đối xử như nhau.
6. Mỗi giai đoạn kết thúc bằng một **demo chạy được** và test tự động tương ứng.

## 3. Ranh giới với các app khác

App Creo làm việc qua 3 interface, mỗi interface có bản tạm để không phải chờ:

| Interface | Dùng cho | Bản tạm (giai đoạn 1–7) | Bản thật (giai đoạn 8) |
|---|---|---|---|
| `IIdentity` | Ai đang đăng nhập | Dev login: chọn user giả trong `appsettings.Development.json` | Token Microsoft Entra ID (MSAL ở web, JWT bearer ở backend) |
| `IProjectDirectory` | Dự án Creo, vai trò của user trong dự án, sản phẩm được bật, worker hợp lệ | File `seed/projects.json` | Gọi API App Quản trị, cache ngắn |
| `IVaultClient` | Gửi kết quả job sang Vault | Ghi vào thư mục `data/vault-outbox/<project>/<product>-<jobId>/…` | Gọi API Smart Vault |

**Hợp đồng API cần thống nhất với 2 app kia.** Đây là bản nháp phía Creo đề xuất, viết chi tiết ở `docs/contracts/` trong giai đoạn 2:

- **App Quản trị** (Creo chỉ đọc):
  - `GET /api/projects?cad=creo&member=me` trả về dự án và vai trò của tôi.
  - `GET /api/projects/{id}/products` trả về sản phẩm được bật.
  - `POST /api/workers/verify` kiểm worker key, trả về `cad_system`.
- **Smart Vault**:
  - `POST /api/vault/jobs` tạo hoặc tìm folder `<project>/<product>-<jobId>`.
  - `POST /api/vault/jobs/{id}/files` upload file (idempotent theo hash, gửi lại không tạo bản mới).
  - `PUT /api/vault/jobs/{id}/properties` ghi thuộc tính part.
- **Xác thực giữa các app:** service-to-service bằng Entra client credentials.

## 4. Các giai đoạn

Ước lượng tính theo ngày công cho 1 người. Đây là con số thô để sắp thứ tự ưu tiên, không phải cam kết.

### Giai đoạn 0. Chuẩn bị và spike Creo (3–5 ngày)

Mục đích: chứng minh C# điều khiển được Creo 10 qua Toolkit C async trước khi đầu tư phần còn lại.

- **Môi trường:**
  - Cài Git (hiện chưa có trên PATH).
  - Xác nhận license TOOLKIT có trong license server.
  - Đặt biến `PRO_COMM_MSG_EXE` trỏ tới `Common Files\x86e_win64\obj\pro_comm_msg.exe`.
- **Spike 1 – exe mẫu:** build exe C theo `make_async_md` (`/MD`, `protkmd_NU.lib` + `ptasyncmd.lib`). Exe này gọi `ProEngineerStart`, mở một part, `ProParameterValueWithUnitsSet`, `ProSolidRegenerate`, `ProMdlSave`, rồi `ProEngineerDisconnect`.
- **Spike 2 – DLL gọi từ C#:** đóng cùng code thành `CreoBridge10.dll` (C ABI phẳng) rồi gọi từ console C# qua P/Invoke.
  - **Rủi ro:** makefile của PTC chỉ build exe async; DLL async chưa được kiểm chứng.
  - **Phương án dự phòng:** Runner viết bằng C++ thành exe riêng, nói chuyện với worker C# bằng JSON-lines qua stdin/stdout. Kiến trúc tổng không đổi, chỉ đổi ngôn ngữ của Runner.
- **Spike 3:** đo thời gian khởi động/kết nối Creo, thử chạy 2 job liên tiếp trong cùng phiên, thử kill runner giữa chừng rồi kết nối lại.

**Xong khi:**
- Có 1 trang `docs/spike-toolkit.md` ghi kết quả.
- Đã chốt dùng DLL hay exe.
- Một part được đổi tham số và lưu lại, điều khiển từ C#.

### Giai đoạn 1. Khung repo và Core (2–3 ngày)

- `CadflowCreo.sln`, `Directory.Build.props` (.NET 10, nullable).
- `src/Cadflow.Core/`: copy từ `InventorAddin/InventorAddin.Core/`
  - `Gea/*.cs`: lexer, parser, checker, interpreter, `IGeaHost`, simulation host.
  - `Models/*Params.cs`, `BomWriter.cs`.
  - `Scripts/box.gea`, `Scripts/lib/*.gea`.
  - Ghi commit nguồn vào `docs/upstream.md`.
- `tests/Cadflow.Core.Tests`: chạy `box.gea` bằng `GeaSimulationHost`, kiểm checker/parser.

**Xong khi:** `dotnet test` xanh, không dính tham chiếu Inventor nào.

### Giai đoạn 2. Backend + web order chạy với sim (5–7 ngày)

- **Backend theo Clean Architecture** (quyết định 2026-11-26), không copy nguyên cấu trúc phẳng của `be/Cadflow.Api`:
  - `Cadflow.Domain`: thực thể và quy tắc nghiệp vụ (Job, trạng thái job, Project, cấu hình sản phẩm). Không phụ thuộc gì.
  - `Cadflow.Application`: use case (tạo job, nhận job cho worker, báo tiến độ/hoàn tất/thất bại, kiểm tra thông số) và các interface cổng: `IJobRepository`, `IIdentity`, `IProjectDirectory`, `IVaultClient`, `IArtifactStore`. Phụ thuộc Domain và `Cadflow.Core` (`.gea`).
  - `Cadflow.Infrastructure`: cài đặt cổng: PostgreSQL (`FOR UPDATE SKIP LOCKED`), lưu artifact (thư mục/R2), client API Admin/Vault, bản tạm (seed JSON, vault-outbox).
  - `Cadflow.Api`: lớp ngoài cùng, chỉ có endpoint, auth, DI; gọi use case, không chứa logic.
  - Quy tắc phụ thuộc: Api → Infrastructure → Application → Domain; Infrastructure và Api phụ thuộc vào Application, không có chiều ngược lại. Test Domain/Application không cần DB hay mạng.
  - Code cũ (`JobStore`, `JobService`, `Program.cs`) được **tách và port** theo từng tầng, không dán nguyên.
- `src/Cadflow.Api/`: lớp Api của cấu trúc trên.
  - Tham chiếu `Cadflow.Core` thay cho link file.
  - DB riêng `cadflow_creo` (PostgreSQL), giữ hàng đợi `FOR UPDATE SKIP LOCKED`.
  - Thêm `project_id` vào job.
  - Thêm 3 interface ở mục 3 với bản tạm.
  - Chỉ nhận dự án `creo`; kiểm vai trò `Designer` khi tạo job.
- Tính năng phụ (Chat/Anthropic, MCP, Designer, APS) vẫn copy nhưng **tắt bằng cấu hình**, bật lại ở giai đoạn 9.
- `fe/`: copy web order, thêm bước chọn dự án (lọc theo `IProjectDirectory`).
- `tests/Cadflow.Api.Tests`: copy, sửa theo `project_id`/quyền.
- Viết `docs/contracts/admin-api.md`, `docs/contracts/vault-api.md` để gửi cho 2 app kia.

**Xong khi:**
- Mở web, chọn dự án Creo, chọn Box, nhập thông số. Lỗi kiểm tra hiện đúng từng thông số (bước 4–8).
- Job vào hàng đợi.

### Giai đoạn 3. Worker + Runner + sim end-to-end (4–5 ngày)

- `src/Cadflow.CreoWorker/`: port `JobWorker`, `ServerClient`, `WorkerSettings` (giữ giao thức). Chạy như Windows service hoặc console.
- `src/Cadflow.CreoRunner/`: process con, nhận lệnh JSON-lines.
  - `CreoJobExecutor` chạy `.gea` qua `CreoGeaHost`, `CreoGeaHost` gọi `ICreoSession`.
  - Session có 2 bản: `SimCreoSession` (sinh artifact giả: STEP rỗng, PNG placeholder, PDF mẫu, BOM thật) và `NativeCreoSession` (giai đoạn 4).
  - Mọi lệnh Creo chạy trên **một thread**.
- **Worker giám sát Runner:**
  - Runner chết thì worker báo `fail` kèm `retryable=true` và khởi động lại Runner.
  - Job đang chạy dở được server trả lại hàng đợi nhờ heartbeat/reaper có sẵn.
- `IVaultClient` (bản tạm) nhận kết quả khi job xong.

**Xong khi:**
- Web → job → worker → runner (sim) → artifact hiện trên web và có trong `vault-outbox`. Đây là bước 9–16 ở chế độ sim.
- Test tích hợp chạy không cần Creo.

### Giai đoạn 4. Bridge thật + phiên Creo (4–6 ngày)

- `native/CreoBridge10/`: CMake hoặc `.vcxproj` dùng cờ của `make_async_md`.
  - Nhận lệnh dạng `bridge_call(const char* json_in, char** json_out)` cùng `bridge_free`.
  - Mọi lỗi Toolkit (`ProError`) trả về thành JSON, không để exception vượt qua ranh giới C.
- **Lệnh tối thiểu:**
  - `connect`/`start`/`disconnect`
  - `copy_model` (`ProMdlnameCopy`), `open`, `set_params`, `regenerate`, `save`, `erase`
  - `mass_props`, `outline`
- `NativeCreoSession` gọi bridge. `creo.ini` hoặc cấu hình worker chỉ đường dẫn Creo, `config.pro`, `creo-lib`.
- Smoke test thủ công trên máy có Creo, chạy qua lệnh `CreoRunner --smoke`.

**Xong khi:** runner thật mở start part, đổi tham số, regen, lưu `BOX-<jobId>.prt` vào thư mục job.

### Giai đoạn 5. Asset Creo cho Box + `CreoGeaHost` (5–8 ngày)

- `creo-lib/`:
  - start part
  - UDF `.gph` cho các builder Box dùng
  - start drawing / template `.drw` A4–A0, format, `config.pro`
  - Hướng dẫn làm tay ở `docs/creo-assets.md`.
  - **Cần người dựng mô hình Creo.**
- `CreoGeaHost` map các lệnh của `box.gea` sang `ProUdfCreate` (bridge thêm `create_udf`), tham số và relation.
- So sánh kết quả với bản Inventor: kích thước bao, khối lượng, số part trong BOM.

**Xong khi:** đơn Box từ web dựng ra model Creo đúng thông số. Model sai thì job thất bại với thông báo rõ ràng (bước 10–11).

### Giai đoạn 6. Xuất file (3–4 ngày)

- STEP (`ProIntf3DFileWrite`).
- 4 ảnh PNG theo hướng nhìn chuẩn (`ProViewRetrieve`, `ProWindowRefit`, `ProRasterFileWrite`).
- Khối lượng, BOM CSV (`BomWriter`).
- Upload qua `artifacts/presign|commit` như worker Inventor.

**Xong khi:** web hiển thị đủ ảnh, file tải được và mở được (bước 13–15).

### Giai đoạn 7. Bản vẽ (8–12 ngày, trọng tâm dài hạn)

- **Template và khung:** `ProDrawingFromTmpltCreate` với template theo khổ giấy. Khổ giấy chọn theo kích thước bao.
- **View:**
  - general view (`ProDrawingGeneralviewCreate`)
  - 2 projected view (`ProDrawingProjectedviewCreate`)
  - iso
  - section A-A (`ProDrawingView2DSectionSet`, cần mặt cắt đặt sẵn trong start part)
- **Kích thước và ghi chú:**
  - Hiện kích thước của model (`ProAnnotationShow`), dọn kích thước (`ProDrawingDimensionsCleanup`).
  - Ghi chú và khung tên (`ProDtlnoteCreate`, tham số drawing).
- **Xuất file:** PDF (`ProPDFExport`) và DWG (`ProOutputFileMdlnameWrite`).
- Đánh giá chất lượng bản vẽ bằng checklist với kỹ sư thật.

**Xong khi:** bản vẽ Box PDF/DWG đạt checklist (bước 12).

### Giai đoạn 8. Tích hợp đăng nhập, App Quản trị, Smart Vault (4–6 ngày phía Creo)

Phụ thuộc: App Quản trị và Smart Vault đã có API theo `docs/contracts/`.

- **Đăng nhập Entra ID:** đăng ký app trong tenant, MSAL ở `fe/`, JWT bearer ở API.
- **Thay bản tạm bằng bản thật:**
  - `IProjectDirectory` đọc API Admin, có cache.
  - Worker key do Admin cấp và kiểm.
  - `IVaultClient` gọi API Vault.
  - Job xong thì tự gửi kết quả, gửi lỗi thì retry; gửi lại không tạo bản trùng.
- **Kiểm quyền end-to-end:** Viewer không đặt hàng được; người ngoài dự án không thấy dự án.

**Xong khi:** đi trọn bước 1 → 16 với dữ liệu thật. File hiện trong Vault, trạng thái "Đang làm", người duyệt mở được.

### Giai đoạn 9. Vận hành và mở rộng (liên tục)

- **Cài đặt máy CAD:**
  - Script cài worker thành service.
  - Kiểm tra license khi khởi động.
  - Log ra file và đẩy heartbeat lên Admin.
- Nhiều máy Creo cùng nhận job (đã có sẵn nhờ `SKIP LOCKED`). Mỗi máy 1 job tại một thời điểm.
- Bật lại Chat/AI assistant, cấu hình mẫu, viewer 3D (kiểm chứng APS với `.prt`, nếu không được thì dùng STEP).
- Port thêm sản phẩm: Transformer, Generator, … theo nhu cầu.

## 5. Thứ tự và song song

```
GĐ0 spike ─► GĐ1 Core ─► GĐ2 Api+fe ─► GĐ3 Worker/Runner (sim) ─┐
                                                                ├─► GĐ6 Xuất file ─► GĐ7 Bản vẽ ─► GĐ8 Tích hợp
                   GĐ4 Bridge thật ─► GĐ5 Asset Box + GeaHost ──┘
Song song bên ngoài: App Quản trị + tách Smart Vault (theo docs/contracts/) ───────────────────► sẵn sàng trước GĐ8
```

- **Nhánh web/backend (GĐ1–3)** và **nhánh Creo (GĐ4–5)** chạy song song được, sau khi GĐ0 xong.
- **Asset Creo (GĐ5)** cần người có kỹ năng Creo. Nên bắt đầu dựng start part/template sớm, ngay trong GĐ1.
- **Tổng ước lượng phía repo Creo:** khoảng 40–55 ngày công tới hết GĐ8. Chưa tính App Quản trị và Smart Vault.

## 6. Rủi ro chính

| Rủi ro | Ảnh hưởng | Cách xử lý |
|---|---|---|
| Toolkit async không chạy được trong DLL load từ .NET | Phải đổi cách đóng gói bridge | Spike ở GĐ0; dự phòng Runner là exe C++ |
| Crash native trong Toolkit | Mất job đang chạy | Runner là process riêng, worker restart, job retry |
| Section/dim tự động ra bản vẽ xấu | Chất lượng bản vẽ không đạt | Đặt sẵn mặt cắt, dim, annotation trong start part/UDF; checklist với kỹ sư; bước dọn dim |
| Thiếu người dựng asset Creo | GĐ5, GĐ7 bị chặn | Xác định người sớm, viết `docs/creo-assets.md` rõ |
| App Quản trị / Vault chưa sẵn sàng | GĐ8 bị chặn | Adapter tạm; chốt hợp đồng API ở GĐ2 |
| Code `.gea` bị lệch với bản Inventor | Cùng script cho kết quả khác nhau | Ghi commit nguồn; test chung bằng sim host |
| License TOOLKIT/Creo trên máy worker | Worker không chạy | Kiểm tra license lúc khởi động, báo lên heartbeat |

## 7. Cần chốt trước khi bắt đầu

1. **Người dựng asset Creo** (start part, UDF, template bản vẽ) cho Box: ai làm?
2. **Máy chạy worker Creo:** máy dev hiện tại hay một máy CAD riêng?
3. **PostgreSQL:** mỗi app một database trên cùng một server được không, hay cần server riêng?
4. **Thứ tự với 2 app kia:** App Quản trị và tách Smart Vault có làm song song không, ai làm?
5. **Git:** cài Git và commit tài liệu hiện có làm commit đầu tiên của repo.
