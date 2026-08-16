# Compile Gate

Kiểm tra compile cho `com.game.addressables` **không cần mở Unity Editor**.

```bash
node Tools/CompileGate/gate.js            # Runtime + Editor
node Tools/CompileGate/gate.js --clean    # bỏ cache, build lại toàn bộ
node Tools/CompileGate/gate.js AddressableManager    # chỉ một assembly
```

Exit code `0` = sạch, `1` = có lỗi. Chạy trong ~10 giây sau lần đầu (có cache theo mtime).

## Vì sao không dùng `Unity -batchmode`

Mở project bằng Editor đã cài (6000.5.7f1) sẽ **upgrade** project từ 6000.2.8f1 và mutate
`ProjectSettings/`. Gate này gọi thẳng Roslyn nên không đụng gì vào repo.

## Cách nó hoạt động

| Thành phần | Lấy từ đâu | Vì sao |
|---|---|---|
| Compiler | Roslyn bundled trong Unity **6000.5.7f1** | Bản Unity duy nhất trên máy có `DotNetSdk` |
| Reference assemblies | Unity **2022.3.62f3** | Đúng sàn `package.json` khai (`"unity": "2022.3"`). Không dùng được ref của 6000.5: ở đó `Object.GetInstanceID()` là obsolete-**as-error** nên Addressables 2.3.1 không compile, mà CS0619 thì `-nowarn` không tắt được |
| Dependency assemblies | Build từ source trong `Library/PackageCache` | `Library/ScriptAssemblies` được build bằng 6000.5 — trộn ABI sinh lỗi ma |

Resolver đọc `.asmdef` đệ quy (theo cả tên lẫn `GUID:`), topo-sort rồi build từng cái.

## Biến môi trường

| Biến | Mặc định |
|---|---|
| `ADDR_GATE_ROOT` | hai cấp trên file này |
| `ADDR_GATE_CSC_UNITY` | `C:\Program Files\Unity\Hub\Editor\6000.5.7f1\Editor\Data` |
| `ADDR_GATE_REF_UNITY` | `C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Data` |
| `ADDR_GATE_OUT` | `Tools/CompileGate/.deps` |

## Giới hạn — phải nhớ khi dựa vào gate này

1. **`UNITASK_PRESENT` không được set** (UniTask chưa cài trong project). Toàn bộ nhánh
   `#if UNITASK_PRESENT` **không** được compile. Sửa code trong nhánh đó phải đọc tay.
2. **Không chạy code.** Gate chỉ bắt lỗi compile, không bắt lỗi logic/runtime — đó là việc của
   test suite (`Tests/`).
3. **`versionDefines` được xấp xỉ**: symbol được define nếu package có mặt, không kiểm tra
   version range.
4. **`autoReferenced` được xấp xỉ**: chỉ auto-ref `UnityEngine.UI`, `Unity.TextMeshPro`,
   `UnityEditor.UI` cho assembly của package này (Unity thật auto-ref tất cả).
5. Vài assembly của Unity (`Unity.InternalAPIEngineBridge.004`, `Unity.TextMeshPro`,
   `UnityEngine.UI`) cần internal-access flag không tái tạo được → gate dùng dll Unity đã build
   sẵn trong `Library/ScriptAssemblies`. Gate sẽ in ra danh sách này mỗi lần chạy.
