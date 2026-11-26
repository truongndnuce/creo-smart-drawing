# Nguồn copy từ inventor-generator-box

Repo nguồn: `C:\Users\Admin\Documents\GitHub\inventor-generator-box` (chưa có commit hash vì máy chưa có Git; ghi lại khi có).
Ngày copy: 2026-11-26. Giữ nguyên namespace `InventorAddin.Gea` / `InventorAddin.Models` để dễ đồng bộ; đổi tên sau.

| Đích (repo Creo) | Nguồn (`InventorAddin/InventorAddin.Core/`) |
|---|---|
| `src/Cadflow.Core/Gea/*.cs` | `Gea/*.cs` |
| `src/Cadflow.Core/Models/BoxParams.cs`, `BomWriter.cs` | `Models/` cùng tên |
| `src/Cadflow.Core/Scripts/box.gea`, `Scripts/lib/*.gea` | `Scripts/` cùng tên |
| `src/Cadflow.Core/Models/TransformerParams.cs` | `Models/TransformerParams.cs` (sản phẩm `transformer`, dựng từ builder nguyên thủy, không cần mô hình Creo có sẵn) |
| `src/Cadflow.Core/Scripts/transformer.gea` | `Scripts/transformer.gea` |

Chưa copy: `Scripts/transformer-hv.gea` (bản chi tiết hơn: 2 hàng sứ cao/hạ áp, giàn tản nhiệt kiểu cánh, van xả; dùng prefix `TX_`, tham số khai báo ngay trong script, không có lớp C# riêng).
