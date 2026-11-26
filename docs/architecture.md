# CADFlow Smart Drawing cho Creo — Kiến trúc (draft)

> Trạng thái: **draft kiến trúc, chưa triển khai** (2026-11-25).
> Thứ tự đọc sơ đồ (mở bằng excalidraw.com → Open, hoặc extension Excalidraw trong VS Code):
> 1. [`01-tong-quan.excalidraw`](01-tong-quan.excalidraw): luồng tổng quan các app
> 2. [`02-luong-nghiep-vu.excalidraw`](02-luong-nghiep-vu.excalidraw): luồng nghiệp vụ chi tiết, không nói tới công nghệ
> 3. [`architecture.excalidraw`](architecture.excalidraw): kiến trúc kỹ thuật (4 app, 4 DB, trao đổi qua API)
>
> Kế hoạch triển khai: [`03-ke-hoach-trien-khai.md`](03-ke-hoach-trien-khai.md) (thay cho mục 9 "Lộ trình" bên dưới).
>
> **Cập nhật quyết định: mỗi app dùng một DB riêng** (App Quản trị, Smart Vault, Smart Drawing Inventor, Smart Drawing Creo).
> Các app trao đổi với nhau qua API, không đọc DB của nhau. Các mục 3–6 đã được viết lại theo quyết định này.

## 1. Bối cảnh

`inventor-generator-box` (CADFlow / "Smart Drawing") là hệ configure-to-order:
web form sinh từ định nghĩa sản phẩm → job vào hàng đợi PostgreSQL → add-in trong Inventor poll job, chạy script `.gea`
dựng model → xuất STEP, 4 PNG, bản vẽ PDF/DWG, BOM CSV → hiển thị trên web và lưu vào Smart Vault.
Ngôn ngữ `.gea` vốn mô phỏng SmartAssembly (một tool của Creo).

Repo này đưa cùng concept đó sang **Creo 10.0.1.0**, với trọng tâm lâu dài là **bản vẽ thông minh**
(section view, show dim của model, cleanup dim, annotate). Smart Vault và phần quản trị được dùng chung giữa Inventor và Creo.

## 2. Quyết định đã chốt

| Chủ đề | Quyết định |
|---|---|
| Repo Creo | Backend + web order riêng (copy từ inventor-generator-box), thay lớp Inventor bằng lớp Creo, giữ nguyên giao thức worker ↔ server |
| Cơ sở dữ liệu | **Mỗi app một DB riêng**; các app trao đổi qua API |
| Smart Vault | Kho chung cho Inventor + Creo, là một app riêng có DB riêng; Smart Drawing gửi kết quả job sang Vault |
| Dự án ↔ CAD | Mỗi dự án cố định 1 CAD (`inventor` \| `creo`); chỉ đổi được khi dự án chưa có job/file |
| `fe-plm` (Vault UI) | Tạm giữ ở repo Inventor, sẽ move sau; cách chia sẻ code (submodule/NuGet) quyết định lúc đó |
| App quản trị | Repo/service riêng **`cadflow-admin`** (backend + web) |
| Đăng nhập | **Microsoft Entra ID** (SSO, OIDC) |
| Phân quyền | Role hệ thống + role theo dự án |
| Giao tiếp Creo | **C# (.NET 10) + C++ bridge dùng Creo Toolkit C (async)**, C# gọi qua P/Invoke (C ABI phẳng + JSON) |
| Dựng hình | **Template + parameter/UDF** (kiểu SmartAssembly), không tạo feature bằng element tree |
| MVP (khi làm app) | Sản phẩm **Box** chạy end-to-end |

## 3. Kiến trúc tổng

Bốn app, **mỗi app một DB PostgreSQL riêng**. App chỉ đọc/ghi DB của chính nó; muốn dữ liệu của app khác thì gọi API.
Sơ đồ: [`architecture.excalidraw`](architecture.excalidraw).

| App | Repo | Giữ dữ liệu gì (DB riêng) |
|---|---|---|
| App Quản trị | `cadflow-admin` (mới) | dự án ↔ CAD, user, role hệ thống, thành viên dự án, sản phẩm theo CAD, máy CAD (worker), nhật ký thay đổi, setting không bí mật |
| Smart Drawing Inventor | `inventor-generator-box` | đơn hàng, hàng đợi job, artifact tạm |
| Smart Vault | tách từ `inventor-generator-box` (`be/.../Vault`, `fe-plm`) | thư mục, file, phiên bản, thuộc tính, trạng thái duyệt; kho file (R2 / thư mục) của riêng Vault |
| Smart Drawing Creo | `creo-smart-drawing` (repo này) | đơn hàng, hàng đợi job, artifact tạm |

```
            Microsoft Entra ID (cấp token; mọi app tự kiểm token)
   ┌───────────────┬──────────────────┬──────────────┬──────────────────┐
   ▼               ▼                  ▼              ▼
 App Quản trị   Smart Drawing      Smart Vault    Smart Drawing
 [DB Quản trị]  Inventor [DB Inv]  [DB Vault +    Creo [DB Creo]
      ▲               │                kho file]        │
      │ gọi API Admin │ gửi kết quả ▶       ◀ gửi kết quả│
      └───────────────┴─────────────────┴────────────────┘
                      │ worker poll                │ worker poll
               Máy CAD Inventor              Máy CAD Creo
```

### 3.1 Các lời gọi API giữa app

| Từ → Đến | Mục đích | Ghi chú |
|---|---|---|
| Smart Drawing → Admin | dự án của user (lọc theo CAD của app), vai trò trong dự án, sản phẩm được bật, kiểm khóa worker | chỉ đọc; cache ngắn; Admin sập thì dùng cache, không tạo job mới cho dự án chưa có trong cache |
| Smart Vault → Admin | dự án, vai trò (ai được xem, duyệt, phát hành) | như trên |
| Smart Drawing → Vault | tạo thư mục job, upload file, ghi thuộc tính part | idempotent theo mã job và hash file: gửi lại không tạo bản trùng; lỗi thì tự thử lại |
| Web → API của chính app | mọi thao tác của user | kèm token Entra |
| Worker → Smart Drawing | heartbeat, nhận job, báo tiến độ, upload artifact, hoàn tất/thất bại | worker luôn là bên gọi |

Giữa các app dùng **token Entra kiểu service-to-service** (client credentials); khi gọi thay mặt user thì chuyển tiếp user id để kiểm quyền. Hợp đồng API chi tiết viết ở `docs/contracts/` (xem [`03-ke-hoach-trien-khai.md`](03-ke-hoach-trien-khai.md), giai đoạn 2).

### 3.2 Phía Creo (repo này)

```
src/Cadflow.Api (Creo)  ^ HTTP poll (worker key do Admin cấp)
src/Cadflow.CreoWorker (C#)  ── stdin/stdout JSON-lines ──┐
┌ Runner process (cô lập crash native) ───────────────────▼───────┐
│ src/Cadflow.CreoRunner (C# x64, 1 thread gọi Creo)               │
│   CreoJobExecutor · CreoGeaHost · DrawingBuilder                 │
│   ICreoSession = NativeCreoSession | SimCreoSession (test)       │
│ native/CreoBridge10.dll (C++ · Toolkit C async · C ABI + JSON)   │
└───────────────────────────────┬──────────────────────────────────┘
                                │ RPC qua pro_comm_msg.exe
                         Creo 10.0.1.0 (có graphics) ◄── creo-lib/ (asset)
```

- Worker luôn gọi server, server không gọi worker.
- Runner chạy thành process riêng: crash trong code native chỉ làm chết Runner. Creo vẫn sống, worker restart Runner, job retry nhờ heartbeat/reaper có sẵn.
- Toolkit chỉ cho gọi từ một thread, nên mọi lệnh gọi bridge chạy trên một thread riêng. Mỗi Creo chạy một job tại một thời điểm.
- Backend `sim` (`SimCreoSession` + `GeaSimulationHost`) để chạy và test khi không có Creo.
- Spike giai đoạn 0 xác nhận DLL async gọi được từ .NET; nếu không thì Runner là exe C++ (kiến trúc tổng không đổi).

## 4. App quản trị `cadflow-admin`

**Phạm vi:** dự án ↔ CAD · user/role/quyền · sản phẩm theo CAD · máy CAD (worker) và hệ thống.

- **Xác thực:** Entra ID cấp token. Mỗi app tự validate token (ASP.NET Core JWT bearer / `Microsoft.Identity.Web`); SPA dùng MSAL. Không lưu mật khẩu. User được tạo trong DB Quản trị ở lần đăng nhập đầu (theo Entra `oid`), có thể giới hạn theo nhóm Entra.
- **Phân quyền:** Admin là nơi duy nhất sở hữu và sửa dữ liệu này; các app khác lấy qua API (mục 3.1).
  - Role hệ thống: `SystemAdmin`, `User`.
  - Role theo dự án:

    | Role | Quyền |
    |---|---|
    | `Viewer` | Xem job, xem file trong Vault |
    | `Designer` | + đặt hàng, tạo job |
    | `Reviewer` | + duyệt / phát hành trong Vault |
    | `ProjectAdmin` | + quản lý thành viên dự án |

  - User chỉ thấy dự án mình là thành viên. Mỗi app tự từ chối thao tác khi thiếu quyền, dựa trên vai trò lấy từ Admin.
- **Dự án ↔ CAD:** dự án có `cad_system` (`inventor` | `creo`). Không cho đổi CAD khi dự án đã có job hoặc file. Vì job và file nằm ở app khác, Admin hỏi API của Smart Drawing và Vault ("dự án này có dữ liệu chưa?") trước khi cho đổi. Mỗi app Smart Drawing chỉ liệt kê và nhận dự án đúng CAD của nó. Job và thư mục Vault lưu `project_id` (chỉ là mã tham chiếu, không có khóa ngoại xuyên DB).
- **Sản phẩm theo CAD:** bảng sản phẩm bật/tắt, phiên bản script, cấu hình mặc định theo CAD. Form đặt hàng chỉ hiện sản phẩm đang bật cho CAD của dự án. Script và builder vẫn nằm trong repo CAD tương ứng.
- **Máy CAD (worker):** Admin lưu tên, CAD, hash khóa, bật/tắt. Worker mang khóa tới Smart Drawing; Smart Drawing hỏi Admin để kiểm. Smart Drawing báo `last_seen` và phiên bản worker cho Admin để hiển thị máy nào đang online.
- **Setting và secret:** setting không bí mật (thời gian lưu giữ, hàng đợi, tự lưu vào Vault…) ở DB Quản trị. **Secret (khóa kho file, khóa AI, secret Entra) không lưu trong DB**, để ở biến môi trường hoặc Key Vault của từng app.
- **Nhật ký:** ghi mọi thay đổi quyền và cấu hình.
- **Tính nhất quán dữ liệu giữa app:** mỗi app giữ `project_id` và vai trò dưới dạng bản sao/cache, nguồn đúng luôn là Admin. Xóa dự án trong Admin chỉ là "lưu trữ" (không xóa cứng) để không làm mồ côi job và file.
- **An toàn schema:** mỗi app có bảng version riêng cho DB của mình, migrate trong `pg_advisory_lock`; app gặp DB có version mới hơn mức nó biết thì từ chối khởi động.
- **Công nghệ:** cùng stack ASP.NET Core .NET 10 + React/Vite/Radix.

## 5. Smart Vault dùng chung

- Là **app riêng, DB riêng, kho file riêng**. Smart Drawing Inventor và Creo chỉ gửi kết quả qua API, không ghi thẳng DB hay kho file của Vault.
- Cấu trúc thư mục: `<project>/<product>-<jobId>/{image,drawing,model,bom}`.
- Trạng thái file: Đang làm → Chờ duyệt → Đã duyệt → Đã phát hành. Chỉ `Reviewer` trở lên được duyệt và phát hành.
- Cần tách khỏi `inventor-generator-box`: `VaultStore`, `VaultFiles`, `VaultJobSaver` và `fe-plm` chuyển sang repo Vault riêng; phần Inventor chỉ còn gọi API gửi file. Repo Creo **không copy** các lớp này.
- Viewer APS: cần kiểm chứng APS hiển thị được file Creo (`.prt`); nếu không thì dùng STEP thay thế.
- Tạm thời (trước khi Vault tách): `fe-plm` ở lại repo Inventor; Creo dùng `IVaultClient` bản tạm ghi vào thư mục `vault-outbox`.

## 6. Việc phát sinh ngoài repo này (làm riêng)

- **Repo mới `cadflow-admin`:** App Quản trị (backend + web + DB riêng).
- **Tách Smart Vault** khỏi `inventor-generator-box` thành app riêng có DB và kho file riêng. Bảng `vault_*` hiện nằm chung DB với job của Inventor nên cần chuyển dữ liệu cũ sang DB Vault.
- **Repo `inventor-generator-box`:** thêm đăng nhập Entra trong `fe`, kiểm quyền qua API Admin, gắn `project_id` cho job, đổi phần lưu Vault từ ghi trực tiếp sang gọi API Vault.
- **Hợp đồng API** giữa các app (Admin, Vault): thống nhất trước giai đoạn 8 của kế hoạch triển khai.

## 7. Cấu trúc repo Creo (khi làm app)

```
src/Cadflow.Core/        Gea/*, Models/*Params.cs, BomWriter, Scripts/*.gea
src/Cadflow.Domain/      Clean Architecture: thực thể + quy tắc nghiệp vụ (không phụ thuộc)
src/Cadflow.Application/ use case + interface cổng (IJobRepository, IProjectDirectory, IVaultClient...)
src/Cadflow.Infrastructure/ PostgreSQL, artifact store, client API Admin/Vault, bản tạm
src/Cadflow.Api/         endpoint + auth Entra + DI (không chứa logic); port từ be/Cadflow.Api
src/Cadflow.CreoWorker/  port JobWorker / ServerClient / WorkerSettings
src/Cadflow.CreoRunner/  CreoJobExecutor, CreoGeaHost, DrawingBuilder, ICreoSession (Native|Sim)
native/CreoBridge10/     C ABI + Toolkit C async (/MD, theo make_async_md)
creo-lib/                start part, UDF .gph, drawing template .drw, format, config.pro
tests/                   Api.Tests (copy), CreoRunner.Tests (sim)
fe/                      web order: chọn dự án Creo, MSAL
docs/
```

## 8. Mapping Inventor → Creo Toolkit C

Tên hàm đã đối chiếu với header trong `Creo 10.0.1.0\Common Files\protoolkit\includes`.

| Inventor | Creo Toolkit C |
|---|---|
| Kết nối / khởi động | `ProEngineerStart`, `ProEngineerConnect`, `ProEngineerDisconnect` |
| `Documents.Add(template)` | `ProMdlnameCopy` từ start part → `<PRODUCT>-<jobId>` |
| Transaction / Abort | Mỗi job làm trên bản copy mới; lỗi thì `ProMdlErase` |
| AttributeSet lưu script | `ProExtdata*` (sau MVP) |
| User Parameters | `ProParameterCreate`, `ProParameterValueWithUnitsSet`, relations |
| `CREATE_FEATURE builder:X` | `ProUdfCreate` với UDF `creo-lib/udf/<x>.gph` |
| `Update` + health check | `ProSolidRegenerate` + kiểm tra feature lỗi |
| Mass / RangeBox | `ProSolidMassPropertyGet`, `ProSolidOutlineGet` |
| STEP | `ProIntf3DFileWrite` |
| PNG | `ProMdlDisplay`, `ProViewRetrieve`, `ProWindowRefit`, `ProRasterFileWrite` |
| Bản vẽ | `ProDrawingFromTmpltCreate`, `ProDrawingGeneralviewCreate`, `ProDrawingProjectedviewCreate`, `ProDrawingView2DSectionSet`, `ProAnnotationShow`, `ProDrawingDimensionsCleanup`, `ProDtlnoteCreate` |
| PDF / DWG | `ProPDFExport` / `ProOutputFileMdlnameWrite` |

### Vì sao chọn Toolkit C thay vì VB API

VB API (COM) dùng được trực tiếp từ C# nhưng **không có**: tạo section view, detail/auxiliary view,
show model annotations, cleanup dimension. Những thao tác này cần cho bản vẽ thông minh, và Toolkit C / OTK đều có.
Cái giá phải trả: cần license Creo TOOLKIT, build bằng MSVC, quản lý ranh giới C#↔C++ và xử lý crash native
(đã giải quyết bằng process Runner riêng và bridge giữ mỏng, logic nằm ở C#).

## 9. Lộ trình làm app (chưa bắt đầu)

1. Scaffold + copy (Core, Api, fe, tests), chọn dự án Creo, DB `cadflow_creo` riêng.
2. Worker + Runner + backend sim, chạy end-to-end với sim.
3. Bridge skeleton (connect, copy, params, regen, save) + smoke test với Creo 10.
4. Asset Creo cho Box (làm tay theo hướng dẫn `docs/creo-assets.md`).
5. `CreoGeaHost` (giữ nguyên `box.gea`).
6. Export STEP / PNG / mass / BOM.
7. Bản vẽ: template A4–A0, 3 view + iso + section A-A, show dim + cleanup, PDF/DWG.
8. End-to-end thật: web → job (dự án Creo, kiểm quyền) → Creo → artifact → Vault, mở được từ `fe-plm`.

