# Task Manual — Khắc phục & tái cấu trúc `com.game.addressables`

> **File này là nguồn sự thật duy nhất về tiến độ.** Mọi task đều có ID, file bị đụng,
> dependency, tiêu chí nghiệm thu và trạng thái. Cập nhật trạng thái ngay khi đổi, không gom.

**Nguồn gốc:** báo cáo review kiến trúc (162 finding — 9 critical, 60 high, 69 medium, 24 low)
do 8 reviewer + 8 verifier đối kháng + 4 hướng research sinh ra. Bản đầy đủ:
[artifact](https://claude.ai/code/artifact/92f56b85-54b3-4285-84bf-25411c5752ff).

---

## 0. Bảng điều khiển

| | |
|---|---|
| **Package** | `com.game.addressables` **v4.1.0-pre.5** |
| **Nhánh** | **`feat/cdn-system`** — worktree `.claude/worktrees/cdn-system-package-review-5545d3` |
| **Bắt đầu** | 2026-08-15 |
| **Trạng thái tổng** | 🟠 Đang rebase kế hoạch sang nhánh đúng — xem §0.1 |
| **Build** | ✅ `PASS` cả hai assembly trên `feat/cdn-system` (baseline sạch) |
| **Finding cũ còn sống trên nhánh đúng** | **149 / 162** |
| **Finding mới (code chưa từng review)** | 46, trong đó 3 critical — đang verify |
| **Wave 1 trên nhánh đúng** | ✅ landed + fix pass — gate `PASS` |

> Wave 1 (refcount foundation) đã hoàn tất trên `feat/cdn-system`: 5 file, **+1.465 / −278**,
> 42 issue từ verify → 25 actionable → đã áp. Xem §0.2 để biết blast radius sang wave sau.

---

## 0.1. ⚠️ Đính chính lớn — đã làm sai nhánh

Nguồn sự thật là **`feat/cdn-system`**, không phải `main`. Nhánh đó **hơn `main` 70 commit**;
`main` hơn nó **0 commit**, tức không chứa gì riêng. Nguyên nhân: lúc mở phiên chỉ đọc dòng
"Current branch: main", không chạy `git worktree list`, và còn **chủ động loại trừ** worktree
khỏi phạm vi review vì tưởng là bản copy cũ còn sót.

| | main (đã audit + đã sửa) | feat/cdn-system (thật) |
|---|---|---|
| version | 4.0.1 | **4.1.0-pre.5** |
| `unity` | 2022.3 | **2023.1** |
| com.unity.addressables | 2.3.1 | **2.9.1** |
| tests | không có | `Tests/Editor` + `Tests/Runtime` + asmdef |
| samples | không có | `Samples~/ApiExamples`, `CdnBoot`, `RuleAutomation` |
| CDN | không có | `Runtime/Cdn/`, `Editor/Cdn/` |

206 file khác nhau, **+20.348 / −1.381**.

### Triage 162 finding cũ trên nhánh đúng

| Trạng thái | Số lượng |
|---|---|
| `STILL_VALID` — còn nguyên | **149** |
| `CHANGED_SHAPE` — vẫn sai nhưng đổi hình dạng | 10 |
| `ALREADY_FIXED` — nhánh đã sửa | 3 |

**92% công sức audit không mất.** Toàn bộ tầng kiến trúc còn nguyên, đã xác minh trực tiếp:
`Runtime/Core/AssetHandle.cs:61` vẫn hard-release bỏ qua refcount;
`Runtime/Pooling/AddressablePoolManager.cs:289` vẫn block main thread;
`Runtime/Loaders/AssetLoader.cs:143` vẫn không có in-flight map;
`Runtime/Threading/UnityMainThreadDispatcher.cs:101` vẫn gọi Unity API trên thread của caller.

**Finding đã CHẾT — không được hành động theo:** "zero test, zero CI"; "không có `Samples~`";
và cụm liên quan tới pin Addressables 2.3.1.

### Hệ quả với kế hoạch

- **W3-01 đổi bản chất.** Test suite đã tồn tại nên nó không còn là task "phải viết từ đầu" —
  nhưng audit mới tìm được **2 critical trong chính test suite**, nên thành "phải sửa".
- **D-01 (upgrade Addressables) phần lớn mất ý nghĩa** — nhánh đã ở 2.9.1.
- **Bảng task Wave 1–5 bên dưới viết theo cây file của `main`.** Phải rebase theo cây file nhánh
  mới trước khi dùng làm worklist: số dòng đã đổi, một số đường dẫn đã đổi, và có thêm
  `Runtime/Cdn` + `Editor/Cdn` chưa có task nào phủ.
- Code Wave 1/2/2E trên `main`: giữ nguyên, dùng làm tham khảo khi viết lại trên nhánh đúng.
  Patch đã lưu: `scratchpad/main-work-package.patch` (417KB).

### Ký hiệu trạng thái

| Ký hiệu | Nghĩa |
|---|---|
| `TODO` | Chưa bắt đầu |
| `WIP` | Đang làm |
| `REVIEW` | Code xong, chờ verify (compile gate + đọc lại) |
| `DONE` | Đã verify — compile sạch + tiêu chí nghiệm thu đạt |
| `BLOCKED` | Bị chặn bởi task khác hoặc bởi quyết định cần user |
| `DROPPED` | Quyết định không làm — **phải ghi lý do** |

### Quyết định đã chốt

| # | Quyết định | Ngày | Hệ quả |
|---|---|---|---|
| D-01 | **Upgrade Addressables 2.3.1 → 4.0.x** | 2026-08-15 | W4-04 (CancellationToken tay trên 2.3.1) bị **huỷ** — không làm nữa, xem D-01 blocker bên dưới |
| D-02 | **Hợp nhất error convention theo hướng throw** (W3-03), chấp nhận breaking change; đồng bộ lại toàn bộ cho chuẩn thiết kế | 2026-08-15 | Bump **5.0.0**. Xoá đường return-null hẳn, không giữ shim. Docs/CHANGELOG/Examples phải sync cùng lúc |

#### D-01 — đang bị chặn ⛔

Upgrade **không thực hiện được trong môi trường hiện tại**:

- `Library/PackageCache` chỉ có `com.unity.addressables@2.3.1`.
- Global cache `%LOCALAPPDATA%/Unity/cache/packages/` trống.
- Không có network tới `packages.unity.com`.

**Chưa xác minh được** (phải confirm trước khi commit vào hướng này):
1. Addressables 4.x tồn tại ở version number nào, và
2. sàn Unity của nó — nhiều khả năng là Unity 6000.x (vì có `ToAwaitable`, kiểu `Awaitable` chỉ có
   từ Unity 6). Nếu đúng thì `package.json` **phải** bỏ `"unity": "2022.3"`, và compile gate phải
   đổi reference sang Unity 6.

**Cách gỡ chặn** (cần người có network làm):
```
# Unity Package Manager > com.unity.addressables > update
# hoặc sửa Packages/manifest.json rồi để Unity resolve
node Tools/CompileGate/gate.js --clean     # xác nhận lại sau khi resolve xong
```
Sau khi resolve xong, gate nên đổi luôn `ADDR_GATE_REF_UNITY` sang bản Unity 6 — lý do duy nhất
gate đang ref 2022.3 là vì Addressables **2.3.1** không compile được với ref 6000.5.

**Trong lúc chờ:** mọi wave khác chạy bình thường. Chỉ W4-04 dừng.

---

## 0.2. Wave 1 đã landed — blast radius sang wave sau

**Đã làm** (`Runtime/Core/AssetHandle.cs`, `IAssetHandle.cs`, `SmartAssetHandle.cs`,
`AssetHandleExtensions.cs`, `Runtime/Loaders/AssetLoader.cs`):
refcount thật (`Dispose()` = decrement, `ForceRelease()` cho owner, `TryRetain()` atomic),
cache tự giữ reference, in-flight single-flight map, guard sau **cả 11** await site,
`AssertMainThread()` ở mọi public method trừ `Dispose()`, cache key `(address, Type)`,
track instantiate, và `sharedTracker.Release()` còn thiếu trong `LoadAssetsByLabelAsyncSafe`.

Hai quyết định thiết kế đáng ghi lại:

- **`TryRetain()` KHÔNG được thêm vào `public IAssetHandle<T>`.** Verify chỉ ra đó là
  source-breaking trên một bản pre-release. Nó nằm ở `internal IRetainableHandle`, còn consumer
  dùng qua extension `AssetHandleExtensions.TryRetain<T>()` — handle trong package đi đường atomic,
  implementation lạ degrade về two-step có guard. `IAssetHandle<T>` **byte-compatible với
  4.1.0-pre.4**.
- **`_count > 0` là tín hiệu sống duy nhất.** Bản đầu dùng hai từ (`_count` và `_released`) nên tồn
  tại khe `IsValid == true` mà `TryRetain() == false`. Không hợp nhất được hai từ bằng CAS mà không
  mở lại race resurrection → xoá hẳn một từ. `_releaseClaimed` giờ chỉ phân xử *ai* gọi
  `Addressables.Release`, không quyết định handle còn sống hay không.

### ⚠️ Bốn thay đổi hành vi — phải vào CHANGELOG

1. `Dispose()`/`Release()` là **decrement**, không còn hard-release → `ClearPool`,
   `TieredCache` eviction và `BaseAssetScope` **không còn giải phóng** như trước.
2. `AssetLoader.Dispose()` giờ destroy mọi GameObject loader đã instantiate.
3. `Retain()` **throw** thay vì warn.
4. `AssertMainThread()` lan tới thêm 11 entry point → nhiều method trước đây không bao giờ throw thì
   nay có thể.

### Work order cho wave sau — theo mức thiệt hại nếu bỏ qua

| # | File | Việc |
|---|---|---|
| 1 | `Core/TieredCache.cs:42,161-166,273`, `Core/ThreadSafeCacheManager.cs:37,245` | `Set()` phải `TryRetain()`; `Remove`/`Clear`/`Dispose` phải release. `PerformEviction` chạy **re-entrant từ trong `Set()`** — tức trong chính lần load sinh ra handle đó. **Đây là prerequisite, không phải follow-up.** |
| 2 | `Loaders/TieredAssetLoader.cs:113-116, 218-220` | Hai chỗ `if (IsValid) Retain()` cuối cùng còn sót — `Retain()` giờ throw, và cả hai nằm ngoài `try` |
| 3 | `API/SimpleAPI.cs:43,55,196,203` | `Load/TryLoad/Preload/PreloadBatch` trả `handle.Asset` rồi vứt handle → mồ côi 1 reference mỗi lần gọi |
| 4 | `Pooling/AddressablePoolManager.cs:166,245,462,479` | `_templateHandles` trả reference bằng `Dispose()` — giờ chỉ giảm 1 trong khi cache giữ reference thứ hai |
| 5 | `Cdn/Services/CatalogService.cs:296` | Phải gọi `AssetLoader.InvalidateAddresses(...)` sau `UpdateCatalogs`. **Ghi nhận:** không file nào trong 18 file `Runtime/Cdn/` giữ `IAssetHandle` — chúng dùng `AsyncOperationHandle` thô + `SafeRelease` riêng, nên contract mới không đổi hành vi CDN. Coupling là một chiều và hiện chưa xử lý |
| 6 | `Threading/LoadOperation.cs:62,66,70,...`, `ThreadSafeAssetLoader.cs:129,133` | Nhánh UniTask dùng `TrySet*`, nhánh Task dùng `Set*` throwing → `Cancel()` rồi `Execute()` throw trong `try`, `catch` gọi `SetException` throw tiếp, thoát ra `async void` |
| 7 | `Progress/ProgressiveAssetLoader.cs:69,233` | Vẫn dựng `new AssetHandle<T>` thẳng từ `Addressables`, bypass cache + ledger + single-flight |
| 8 | `Threading/ThreadSafeAssetLoader.cs` | Không có wrapper dispatch cho `ReleaseAsset`, `ReleaseInstance`, `DownloadDependenciesAsync`, `GetDownloadSizeAsync` — cả 4 giờ throw off-thread, không có escape hatch |

### ~~Ngoài phạm vi~~ — đã truy ra: là session khác, KHÔNG phải agent của wave này

`Editor/Cdn/CatalogReader.cs` (+78) và `Editor/Cdn/CatalogInspectCLI.cs` (+18) —
`FindRemoteEntriesNeedingLocalBundles()` + `CrossBoundaryEntry`. Ban đầu tưởng agent Wave 1 vi
phạm phạm vi sở hữu. **Sai.** Đối chiếu mtime: hai file đó sửa lúc `08-16 12:48–12:49`, còn Wave 1
ghi file lúc `13:56–14:05` — cách nhau hơn một tiếng, và Wave 1 khởi động khoảng 13:15.

Đó là **session làm song song**, đang chạy CDN Phase 5.9 ("Catalog Inspector đọc catalog nhị phân
thật"). Không được revert. Xem [`PARALLEL_SESSIONS.md`](PARALLEL_SESSIONS.md).

---

### Quy tắc bất di bất dịch

1. **Không task nào lên `DONE` nếu chưa qua compile gate.** Xem §1.
2. **Sửa `AssetHandle` xong mới được đụng tới thứ phụ thuộc nó.** Refcount là gốc của 7 finding.
3. **Không sửa `#if UNITASK_PRESENT` mà không đọc lại bằng mắt** — nhánh đó không compile được
   trong môi trường hiện tại (UniTask chưa cài), gate không bảo vệ.
4. **Không `git add -A`.** Chỉ stage file thuộc task đang làm.
5. Feature inert (`AssetValidator`, `PoolConfiguration`, …): **wire vào hoặc xoá cả API lẫn mục docs.**
   Không để nguyên trạng — false confidence tệ hơn không có feature.

---

## 1. Compile gate

Không mở Unity Editor bằng batch-mode. Thay vào đó gọi thẳng Roslyn của Unity với đúng reference
set, dựng dependency từ `Library/PackageCache` source.

**Trên `feat/cdn-system` (nhánh đúng)** — worktree khai `6000.5.7f1`, đúng bản Unity đang cài,
nên compiler và reference **cùng một version, không lệch**:

```bash
bash <scratchpad>/gate-cdn.sh            # Runtime + Editor
bash <scratchpad>/gate-cdn.sh --clean    # build lại toàn bộ
```

Trên `main` (chỉ còn dùng để đối chiếu bản WIP cũ): `node Tools/CompileGate/gate.js`.
Cơ chế + giới hạn: [`Tools/CompileGate/README.md`](../Tools/CompileGate/README.md).

**Giới hạn đã biết — phải nhớ:**

| Giới hạn | Hệ quả | Bù bằng cách nào |
|---|---|---|
| `UNITASK_PRESENT` không được set (UniTask chưa cài) | Toàn bộ nhánh UniTask **không** được gate | Đọc tay từng `#if`; giữ 2 nhánh đối xứng tuyệt đối |
| Gate ref theo 6000.5.7f1, `package.json` khai sàn **2023.1** | Không chứng minh được package compile trên đúng sàn nó khai | Phải suy từ source. Commit mới nhất của nhánh là *"pre.4 could not compile on the Unity it claims to support"* — lớp lỗi này đã cắn một lần |
| Gate không chạy code | Không bắt lỗi logic/runtime | Test suite đã tồn tại trên nhánh này — nhưng audit tìm được 2 critical trong chính nó |

### Hai bug của gate đã sửa — cả hai đều thuộc loại nguy hiểm

1. **PASS giả.** Cơ chế fallback "dùng dll build sẵn khi build-from-source thất bại" áp cả cho
   *target*, nên gate lấy `AddressableManager.dll` cũ trong `Library/ScriptAssemblies` rồi báo
   xanh **mà không compile code đang xét**. Một gate biến lỗi compile thành xanh còn tệ hơn không
   có gate. Đã chặn: target không bao giờ được fallback.
2. **Sinh define sai version.** Gate phát `UNITY_2022_3` trong khi ref assembly là 6000.5, nên
   compile nhầm nhánh `#if`. Hậu quả: nó "tìm ra" lỗi `GetInstanceID()` obsolete ở
   `HierarchyAssetScope.cs:67` — **finding hoàn toàn không có thật**; code ở đó có guard
   `#if UNITY_6000_5_OR_NEWER → GetEntityId()` kèm comment giải thích. Define giờ được suy từ
   đúng version của reference Unity.

> **Quy tắc rút ra:** trước khi report bất cứ thứ gì nằm trong `#if`, phải xác định nhánh nào
> thực sự compile cho sàn đang xét.

**Baseline trên `feat/cdn-system`:** `PASS AddressableManager` + `PASS AddressableManager.Editor`,
compile từ source. Mọi lỗi sau đây đều là do mình gây ra.

---

## 2. Wave 0 — Hạ tầng verify

Không có wave này thì mọi wave sau là mù.

| ID | Việc | File | Deps | Status |
|---|---|---|---|---|
| W0-01 | Compile gate gọi thẳng Roslyn (không mở Editor) | `Tools/CompileGate/gate.js` | — | `DONE` |
| W0-02 | Resolver asmdef đệ quy dựng dependency từ PackageCache (theo tên **và** `GUID:`), topo-sort, cache theo mtime; fallback dll Unity build sẵn cho assembly cần internal-access | `Tools/CompileGate/gate.js` | W0-01 | `DONE` |
| W0-03 | Baseline compile sạch trước khi sửa | — | W0-02 | `DONE` |
| W0-04 | File task manual này | `Documentation/REFACTOR_TASKS.md` | — | `DONE` |
| W0-05 | `.gitignore` cho cache của gate | `.gitignore` | W0-01 | `DONE` |

---

## 3. Wave 1 — Nền móng (serial, không parallel được)

Mọi thứ ở Wave 2 phụ thuộc ownership model ở đây. Làm sai chỗ này thì sửa lại toàn bộ.

### W1-01 · Refcount thật cho `AssetHandle` — `TODO`

**Finding:** §3.1 CRITICAL. `Dispose()` gọi thẳng `Addressables.Release()`, bỏ qua
`_referenceCount`; loader trả **cùng một instance** cho mọi caller nên `Retain()` vô nghĩa.

**File:** `Runtime/Core/AssetHandle.cs`, `IAssetHandle.cs`, `SmartAssetHandle.cs`

**Làm gì:**
- `Dispose()` → `Release()` (giảm refcount). Thêm `internal ForceRelease()` cho owner.
- `IsValid` phải gồm `!_disposed`.
- Thêm `bool TryRetain()` — atomic try-increment, mô hình `weak_ptr::lock()`. Không được
  check-then-increment (2 bước) như `AssetLoader.cs:115-118` hiện tại.
- `Retain()` khi count đã về 0 → **throw**, không warn (mô hình `AsyncOperationBase`).
- Refcount dùng `Interlocked`; `_disposed` dùng `CompareExchange`.
- Cân nhắc version stamp `(index, version)` — nhưng đó là W4-08, đừng làm sớm.

**Nghiệm thu:**
- `Retain(); Dispose();` → asset **còn sống**. `Dispose(); Dispose();` → release đúng 1 lần.
- Handle đã release: `IsValid == false` kể cả khi owner khác giữ operation sống.

---

### W1-02 · `AssetLoader`: ownership, teardown, guard sau await — `TODO`

**Finding:** §3.7 (a)(b)(c)(d) HIGH, §3.8 HIGH, §3.12 HIGH. Phụ thuộc W1-01.

**File:** `Runtime/Loaders/AssetLoader.cs`

**Làm gì:**
- Cache **phải `Retain()`** khi insert → cache sở hữu +1 thật. Khi refcount về 0, callback về
  loader xoá khỏi **cả** `_assetCache` **và** `_activeHandles`.
- `ClearCache()` (`:1011`) phải `Dispose()` từng phần tử `_activeHandles` trước khi `Clear()`.
  Hiện label-load chỉ vào `_activeHandles` → teardown rò rỉ toàn bộ bundle.
- Thêm `sharedTracker.Release();` vào `LoadAssetsByLabelAsyncSafe` sau vòng `foreach` (`:748`) —
  bản non-Safe có ở `:353`, bản Safe thiếu. **1 dòng.**
- Guard `_disposed` sau **mọi** `await operation.Task` — hiện chỉ 3/9. Thiếu ở `:500`, `:638`,
  `:740`. Tách thành helper để không sót nữa.
- Track instantiate: `List<GameObject> _instances`, `ReleaseInstance` khi teardown (`:843/:879`).
- Gọi `AssertMainThread()` ở đầu **mọi** public method (hiện chỉ 3 method `*Safe`) **và** ngay
  sau mỗi await — thread resume không nhất thiết là thread gọi.
- Cache key `(address, Type)` thật, không `typeof(T).Name` (mất namespace).
- `ReleaseAsset` (`:1022`) đang prefix-match → so sánh address chính xác.

**Nghiệm thu:** load 40 asset qua label → `Dispose()` → đúng 40 lần `Addressables.Release`.
Destroy owner giữa lúc load → không ghi vào loader đã chết.

---

### W1-03 · In-flight map (single-flight) — `TODO`

**Finding:** §3.2 CRITICAL. Phụ thuộc W1-01, W1-02.

**File:** `Runtime/Loaders/AssetLoader.cs`

**Làm gì:** đăng ký task vào map **đồng bộ trước await đầu tiên**; caller đến sau await task có
sẵn rồi `TryRetain()`. Mô hình Caffeine `AsyncCache` — future *chính là* cache entry, không có
khe hở giữa "miss" và "insert".

**Hai ràng buộc bắt buộc (làm sai là hỏng im lặng):**
1. Key là `(address, Type)` — key address trần gộp nhầm `Load<Sprite>` và `Load<Texture2D>`.
2. Kiểu lưu là `Task`/`UniTaskCompletionSource`, **tuyệt đối không `UniTask` trần** — UniTask kế
   thừa ràng buộc `ValueTask`: await 2 lần trên cùng instance là undefined behavior. Bug này chỉ
   nổ **trong build có UniTask**, tức gate hiện tại không bắt được.
3. Fail thì **xoá entry** (không cache task lỗi) — khác `AsyncLazy`.

**Ghi chú đính chính:** Unity `ResourceManager` **đã** dedup ở tầng `(location, type)`, nên đây
không phải fix "N lần I/O". Nó fix **rò rỉ refcount**: ResourceManager đếm N, wrapper release 1.

**Nghiệm thu:** 10 caller đồng thời cùng address → 1 lần gọi `Addressables.LoadAssetAsync`,
`ClearCache()` release đúng 1 lần, không handle mồ côi.

---

## 4. Wave 2 — Song song, phân vùng theo file

Chạy được đồng thời vì **không chia sẻ file**. Tất cả phụ thuộc Wave 1.

| ID | Việc | File sở hữu | Finding | Status |
|---|---|---|---|---|
| W2-01 | Xoá sync-over-async trong `Spawn`; thêm `SpawnAsync`; `Spawn` thiếu pool → log + null | `Pooling/AddressablePoolManager.cs` | §3.3 CRIT | `TODO` |
| W2-02 | Pool: `preloadCount` tạo đúng n instance; `DynamicPool` không phá kế toán inner pool; validity check trong `Get()`; pool root `DontDestroyOnLoad`; `IPoolable` reset | `Pooling/DynamicPool.cs`, `Adapters/*` | §3.17 HIGH | `TODO` |
| W2-03 | `UnityMainThreadDispatcher`: capture `_mainThreadId` từ `RuntimeInitializeOnLoadMethod`; bỏ `var _ = Instance` khỏi `Enqueue`; `EnqueueAndWait` có timeout; `OnDestroy` fault queue | `Threading/*` | §3.5 CRIT | `TODO` |
| W2-04 | Facade: early-return `OnDestroy` khi `_instance != this`; bỏ cache `_sessionLoader`; guard quitting/edit-mode ở `Instance` getter | `Facade/AddressablesFacade.cs` | §3.4 CRIT, §3.10 HIGH | `TODO` |
| W2-05 | `GlobalAssetScope.Dispose()` null `_scope`+`_instance`; `HybridScope` domain-reload reset + `Deactivate`≠`Dispose`; đăng ký scope vào `ScopeManager` | `Scopes/*`, `Managers/ScopeManager.cs` | §3.11 HIGH, §3.9 HIGH | `TODO` |
| W2-06 | `TieredCache`/`ThreadSafeCacheManager`: `Set()` phải `Retain()`; `Clear`/`Remove`/`Dispose` phải release; eviction skip `ReferenceCount > 1` | `Core/TieredCache.cs`, `Core/ThreadSafeCacheManager.cs` | §3.6 CRIT | `TODO` |
| W2-07 | Byte accounting: `Profiler.GetRuntimeMemorySizeLong` tại lúc load; **một** ngân sách dùng chung cho mọi `TieredCache<T>`; pump eviction từ Facade + `Application.lowMemory` | `Loaders/TieredAssetLoader.cs`, `Core/TieredCacheConfig.cs` | §3.15 HIGH | `TODO` |
| W2-08 | Monitoring sống lại: bỏ `ResetOnLoad→Clear()` (hoặc đăng ký lại ở `EnteredPlayMode`); gọi `ReportAssetReleased` khi refcount về 0; `DisplayName` tới được Dashboard | `Monitoring/*`, `Editor/Data/*` | §3.19 HIGH | `TODO` |
| W2-09 | `SmartAssetHandle` finalizer: **không** release trong finalizer — marshal qua dispatcher; bỏ `catch {}` trần | `Core/SmartAssetHandle.cs` | §3.14 HIGH | `TODO` |
| W2-10 | Progress subsystem đi qua loader thật (hiện `ProgressiveAssetLoader` nhận `loader` rồi không dùng); `LoadMultipleWithProgressAsync` trả handle thay vì `bool` | `Progress/ProgressiveAssetLoader.cs` | §3.7(d) HIGH | `TODO` |
| W2-11 | Packaging: tách `AddressableManager.UniTask.asmdef` (versionDefines **+** defineConstraints trên cùng asmdef); bỏ `"UniTask"` khỏi asmdef core; bỏ TMP khỏi `dependencies`, map cả `com.unity.ugui` 2.0 | `Runtime/*.asmdef`, `package.json` | §3.21 HIGH | `TODO` |
| W2-12 | Logging: `[Conditional]` cho log helper (xoá cả call site lẫn argument expression); route log `*Safe`/Tiered qua `LogVerbose`; `DebugSettings.Instance` bỏ `#if UNITY_EDITOR` | `Configs/DebugSettings.cs`, các loader | §5 M | `TODO` |

---

## 5. Wave 3 — Bề mặt API + test (sau Wave 2)

| ID | Việc | File | Finding | Status |
|---|---|---|---|---|
| W3-01 | **Test assembly + 5 test đầu tiên** trên `ResourceManager` + `MockProvider` viết tay (mô hình test suite của chính Addressables — không chạm static `Addressables`): dedup, release-balance, fail-không-cache, dispose-mid-load, weight-eviction | `Tests/Editor/` (mới) | §3.20 HIGH | `TODO` |
| W3-02 | `Simple.Destroy` → `ReleaseInstance` trước; implement hoặc xoá `Simple.Release` / `Standard.ClearCache(scope)` / `Simple.IsLoaded` / `Simple.GetStats` | `API/SimpleAPI.cs`, `API/StandardAPI.cs` | §4.2, §4.3 | `TODO` |
| W3-03 | **[D-02 — breaking, bump 5.0.0]** Hợp nhất convention báo lỗi: một implementation **throw** `AssetLoadException` (mang key/type/scope) + một adapter `SuppressThrow()` trả tuple deconstruct được; **xoá hẳn** đường return-null, không giữ shim; `LoadResult` → `readonly struct`, xoá `implicit operator bool` (nó map `null`→`false` nên result null không phân biệt được với load fail). Cancel luôn là `OperationCanceledException`, **không bao giờ** là một variant trong error enum | `Core/LoadResult.cs`, `Core/LoadError.cs`, `API/*`, `Facade/*` | §4.1 | `TODO` |
| W3-09 | **[D-02]** Đồng bộ hậu-breaking-change: `package.json` → 5.0.0, CHANGELOG viết đủ mục Removed/Changed kèm bảng migration cũ→mới, README + 4 file guide + `Assets/Examples` sửa theo API mới | `package.json`, `CHANGELOG.md`, `README.md`, `Documentation/*`, `Assets/Examples/*` | §5 | `TODO` |
| W3-04 | Đổi tên `LoadScene<T>` → `LoadIntoSceneScope<T>`; `Assets.LoadScene` bind đúng scene của caller (dùng `GetOrCreate(Scene)` đã có từ 4.0.0), bỏ quét `FindObjectsByType` mỗi lần | `API/*`, `Facade/*`, `Scopes/SceneAssetScope.cs` | §4.4 | `TODO` |
| W3-05 | UniTask đối xứng: `Simple`/`Standard`/`*Safe`/`TieredAssetLoader` đang hard-code `Task`. Sửa sample `Assets/Examples` (dùng `task.IsCompleted`/`.Result` → vỡ khi cài UniTask) | `API/*`, `Assets/Examples/*` | §4.5 | `TODO` |
| W3-06 | Ergonomics: bỏ `async void` (7 chỗ) → `UniTaskVoid`+`Forget()`; preload dùng đúng type thay vì `<object>`; `Spawn` set transform **trước** `SetActive`; `Despawn` có marker component | `API/SimpleAPI.cs`, `Pooling/*` | §4.7 | `TODO` |
| W3-07 | Wire hoặc xoá feature inert: `AssetValidator`, `PoolConfiguration`, `AddressablePreloadConfig`, version-expression filter, `AppendToExisting`, `AutoApplyOnModified` | nhiều | §3.19, §5 | `TODO` |
| W3-08 | Docs đồng bộ code: install tag `#3.5.0`→4.0.1, xoá bảng performance % không có benchmark, xoá `README.backup.md`, sửa bảng scope-name trong `MONITORING_GUIDE`, guard version trong `deploy.sh` | `README.md`, `MONITORING_GUIDE.md`, `deploy.sh` | §5 M/L | `TODO` |

---

## 6. Wave 2E — Editor & CI (song song, độc lập Runtime)

| ID | Việc | File | Finding | Status |
|---|---|---|---|---|
| E-01 | 5 rule template ship kèm đều inert (`filters: []`, provider path rỗng); `ImportFromJson` chỉ resolve provider theo asset path, bỏ qua `addressProviderType` mà chính nó export | `Editor/Templates/*.json`, `Editor/Rules/RuleSerializer.cs` | §3.18 | `TODO` |
| E-02 | Mọi JSON report của CLI là `{}` — `JsonUtility.ToJson` trên anonymous type | `Editor/CLI/AddressableCLI.cs` | §3.18 | `TODO` |
| E-03 | Tắt một filter biến rule thành match-all toàn project (`AssetFilterBase.cs:56` `return true`) | `Editor/Filters/AssetFilterBase.cs` | §3.18 | `TODO` |
| E-04 | `TypeFilter` default `"UnityEngine.GameObject"` không bao giờ resolve, fail im lặng | `Editor/Filters/TypeFilter.cs` | §3.18 | `TODO` |
| E-05 | `AutoApplyOnImport` mặc định → **false**; không `SaveAssets` khi 0 thay đổi; `FindAssets("")` giới hạn `new[]{"Assets"}` + loại `AddressableAssetsData` | `Editor/Automation/*`, `Editor/Rules/LayoutRuleProcessor.cs` | §3.18 | `TODO` |
| E-06 | `PathFilter`: hỗ trợ glob thật (`**`) — 15 ví dụ trong guide dùng `**` mà filter không hiểu; sửa rò rỉ `_cachedPattern` gây recompile regex mỗi asset | `Editor/Filters/PathFilter.cs` | §3.18 | `TODO` |
| E-07 | CLI exit code: `DetectConflicts` exit 2 khi settings null (hiện exit 0 → CI xanh giả); `-warningAsError` đọc `result.Warnings`; parser nuốt flag làm value; `BuildPlayerContent` dùng overload `out` | `Editor/CLI/AddressableCLI.cs` | §3.18, §6 | `TODO` |
| E-08 | Undo + cancelable progress cho mọi mutation rule; dialog 2 nút → Esc = "Replace" (mất hết rule) | `Editor/Windows/*`, `Editor/Automation/*` | §5 M | `TODO` |

---

## 7. Wave 4 — Refactor kiến trúc (thứ tự bắt buộc)

Mỗi bước là điều kiện của bước sau. **Không đảo thứ tự.**

| ID | Việc | Vì sao ở vị trí này | Status |
|---|---|---|---|
| W4-01 | Adapter seam cho Editor (`IAddressableAssetSettingsAdapter`, `IAssetDatabaseAdapter` + Fake) | Mở khoá test cho rule engine — phần gần như pure logic, rẻ nhất để đạt coverage thật (mô hình SmartAddresser) | `TODO` |
| W4-02 | Provider registry hợp nhất: `_assetCache` → map chứa **cả** in-flight lẫn completed, key `op+[address][Type]`, `CreateHandle()` cấp handle riêng mỗi caller trên storage chung (mô hình YooAsset) | Giải quyết §3.2 + phần lớn §3.1 cùng lúc, và cho facade một thứ duy nhất để delegate | `TODO` |
| W4-03 | Gộp registry scope: `ScopeManager` là owner duy nhất; `GlobalAssetScope` → root scope; `HybridScope` → view hoặc xoá. Thêm hierarchy (`Parent`, resolve đi lên, teardown child trước parent) + linked CTS | Phải sau W4-02 vì cần một registry để gộp *vào* | `TODO` |
| W4-04 | ~~`CancellationToken` bằng tay với ngữ nghĩa abandonment trên Addressables 2.3.1~~ → **thay bằng**: dùng thẳng `ToAwaitable(CancellationToken)` của Addressables 4.x. Vẫn phải tự thiết kế phần per-waiter trên coalesced load (`IsCanceled` vs `IsAllCanceled` — CatAsset): huỷ chung khi waiter đầu tiên huỷ sẽ giết load của các waiter còn lại | **BLOCKED bởi D-01.** Làm tay trên 2.3.1 rồi upgrade là vứt việc đi — user đã chốt upgrade | `BLOCKED` |
| W4-05 | `IAssetCache` strategy + `IAssetLoader` interface; xoá `TieredAssetLoader` như một fork — tiering thành config của loader duy nhất | Hiện `Advanced.CreatePoolManager(CreateTieredLoader(...))` **không compile** dù README ghép | `TODO` |
| W4-06 | Layout model cho Editor rules: `BuildLayout → Validate → ApplyLayout`, một model nuôi Viewer + CI report + apply. Gộp `RuleValidator` + `RuleConflictDetector` | Sau W4-01 | `TODO` |
| W4-07 | Byte accounting thật: ingest `Library/com.unity.addressables/buildReports` (bật "Debug Build Layout") — Unity đã tính sẵn per-asset + per-bundle, kể cả "Data from Other Assets" | Sau W2-07 (bản tạm) | `TODO` |
| W4-08 | Generational handle `(index, version)` thay refcount thuần (mô hình slotmap / `AsyncOperationBase.m_Version`) | Sau W4-02 | `TODO` |
| W4-09 | CI: GameCI `unity-test-runner@v4` `packageMode` matrix [editmode, playmode] × [Default, UniTask] + `dotnet test` cho phần engine-free (mô hình VContainer) | Sau W3-01 | `TODO` |

---

## 8. Wave 5 — Nice-to-have

| ID | Việc | Status |
|---|---|---|
| W5-01 | **[D-01 — đã chốt UPGRADE, đang BLOCKED]** Nâng `com.unity.addressables` lên 4.0.x. Sau khi resolve được: (a) xác nhận version + sàn Unity thật, (b) sửa `package.json` `unity` field nếu buộc lên Unity 6, (c) đổi `ADDR_GATE_REF_UNITY` sang Unity 6, (d) mở khoá W4-04, (e) rà `ReferenceCount` public của upstream có thay được phần bookkeeping tự viết ở W1-01 không | `BLOCKED` |
| W5-02 | Scene loading thật (`LoadSceneAsync(key, LoadSceneMode)` + `UnloadSceneAsync` tracked trong scene scope) | `TODO` |
| W5-03 | Live-ops: `CatalogManager` (check/update catalog, `ClearDependencyCacheAsync`, `CleanBundleCache`), download progress từ `GetDownloadStatus()` (byte thật) | `TODO` |
| W5-04 | Retry/backoff **surface qua group schema** (`AssetBundleRequestOptions`) thay vì tự viết layer — retry tự viết đua với retry của provider và làm hỏng refcount | `TODO` |
| W5-05 | Admission filter kiểu TinyLFU (~100 dòng, chỉ lấy admission — bỏ window/SLRU/hill-climbing); chuyển tier evaluation sang batched drain | `TODO` |
| W5-06 | `Samples~/` + `Documentation~/` + `samples[]` trong `package.json`; chạy `com.unity.asset-store-validation` | `TODO` |
| W5-07 | Address provider dạng template regex (`${PATH[0]}`, `${filename}` — mô hình unity-addressable-importer) thay class hierarchy | `TODO` |
| W5-08 | `content-update` command cho CLI (`ContentUpdateScript`, archive `addressables_content_state.bin` per-platform-per-release) | `TODO` |

---

## 10. ⚠️ Điểm dừng hiện tại — đọc trước khi làm tiếp

**Ngày 2026-08-15, hết weekly quota (reset 19/08 07:00 Asia/Saigon).** Nhiều agent chết
*giữa lúc đang sửa file*, để lại repo ở trạng thái không compile. Đã sửa tay và build xanh trở lại.

### Cái gì thực sự đã landed

| Vùng | File | Tình trạng |
|---|---|---|
| W1 (refcount) | `AssetHandle.cs`, `IAssetHandle.cs`, `SmartAssetHandle.cs`, `AssetLoader.cs` | Landed, compile sạch, **31 finding verify mới áp dụng 4** |
| W2 caches | `TieredCache.cs`, `CacheEntry.cs`, `ThreadSafeCacheManager.cs` | Landed **một phần** — agent chết giữa chừng |
| W2 threading | `UnityMainThreadDispatcher.cs` (+344 dòng) | Landed một phần, chưa verify |
| W2 scopes | `IAssetScope.cs`, `BaseAssetScope.cs`, `GlobalAssetScope.cs`, `ScopeManager.cs` | Landed một phần |
| W2 progress | `ProgressiveAssetLoader.cs` | Landed một phần |
| W2E | CLI, Filters, Processor, Templates, Windows, Tools | 3/5 vùng landed; **rules-data + windows chết giữa chừng** |
| W2 pooling / packaging / tiered-loader | — | **Không có gì landed** |

### Sửa tay để build xanh trở lại

1. `IAssetScope` được thêm `ScopeId`/`DisplayName`/`DisposedToken` nhưng 3 implementer chưa cập nhật
   → thêm `DisposedToken` cho `HierarchyAssetScope`/`SceneAssetScope` (forward, trả token **đã
   cancel** khi inner scope chưa/không còn — không bao giờ `CancellationToken.None`), thêm cả 3
   member cho `HybridScope`.
2. `BaseAssetScope.DeadScopeToken` — token đã-cancel dùng chung cho wrapper.
3. `HybridScope.Dispose()` giờ null static instance tương ứng (`ForgetInstance`) — trước đó
   singleton bị đầu độc vĩnh viễn, `Loader` throw mãi (§3.11 của báo cáo).
4. `HybridScope.ScopeId` được namespace `Hybrid:` — trước đó trùng tên monitoring với
   `GlobalAssetScope`/Session nên Dashboard gộp hai cache độc lập thành một dòng.
5. `ThreadSafeCacheManager.Set` chuyển sang `CacheEntry.TryCreate` (cache tự giữ reference) và
   trả `bool`; nhánh key trùng không còn im lặng nuốt handle mới.

### 4 finding blocker đã áp dụng

| # | Vấn đề | Sửa |
|---|---|---|
| 1 | `ClearCache()` giờ huỷ **mọi GameObject đã instantiate** — mà nó reachable từ `Assets.ClearCache()` như eviction thường ngày | Tách: `ClearCache()` = eviction (chỉ nhả reference của cache), `TearDownAll()` private = teardown thật, chỉ `Dispose()` gọi |
| 2 | Join thất bại rồi rơi thẳng xuống `NewInFlight` mà không kiểm tra lại map → **N load trùng** cho một key | Đổi `if` → `while` ở cả 4 join site |
| 3 | `CacheHandle` gỡ handle **còn sống** khỏi `_activeHandles` → teardown không bao giờ với tới nó | Chỉ gỡ khi `!IsAlive` |
| 4 | `Dispose()` không hoàn tất `_inFlightLoads` → joiner treo vĩnh viễn | Thêm `_inFlightSources`, `Dispose()` `TrySetResult(null)` toàn bộ |

### 12 finding actionable CÒN LẠI — phải làm trước khi Wave 1 lên `DONE`

| Sev | File:line | Vấn đề |
|---|---|---|
| major | `AssetLoader.cs:1190` | 3 `*Safe` variant leak `Addressables` operation khi load fail — bản non-Safe có release, bản Safe không |
| major | `AssetLoader.cs` | Không có callback khi refcount về 0 → `_activeHandles` không bao giờ được prune tự nhiên (W1-02 yêu cầu, chưa implement) |
| major | `AssetLoader.cs:104` | `StillAliveAfterAwait` gọi `AssertMainThread()` **đầu tiên**, nằm trong `catch(Exception)` của 10 call site → vi phạm thread bị nuốt và báo nhầm thành "load failed" |
| major | `AssetLoader.cs:646` | Join block nằm **ngoài** `try` của `*Safe` → API "không bao giờ throw" giờ throw được |
| major | `AddressablePoolManager.cs:479` | Pool không còn release template prefab (Dispose giờ chỉ giảm 1, cache giữ ref thứ 2) — **W2-01 phải xử lý** |
| major | `TieredAssetLoader.cs:116` | Còn 2 chỗ `if (IsValid) Retain()` — đúng pattern W1-01 xoá bỏ, và `Retain()` giờ **throw** |
| major | `AssetHandle.cs:113` | `ReleaseCore` zero refcount **trước** khi set `_disposed` → tồn tại khe `IsValid==true` mà `TryRetain()==false` |
| major | `IAssetHandle.cs:48` | `TryRetain()` thêm vào interface public = source-breaking → bắt buộc bump 5.0.0 + CHANGELOG |
| major | `AssetHandle.cs:90` | `using var handle` trong README giờ là no-op — docs phải viết lại theo ownership model mới |
| major | `SmartAssetHandle.cs:145` | Wave 1 làm finalizer **dễ gọi `Addressables.Release` từ finalizer thread hơn trước** — regression, W2-09 phải sửa |
| minor | `SmartAssetHandle.cs:83` | `Release()` không đánh dấu consumed → `Release()` + `Dispose()` giảm 2 lần cho 1 reference |
| minor | `AssetLoader.cs:1386` | `AssertMainThread()` trong `Dispose()` → `Dispose` throw được, vi phạm hợp đồng `IDisposable` |

### Việc tiếp theo, đúng thứ tự

1. Áp 12 finding trên (W1 fix pass bị chết).
2. Chạy lại W2E cho `rules-data` + `windows` (chết giữa chừng) rồi verify chỗ giáp ranh.
3. Chạy W2 pooling / packaging / tiered-loader (chưa có gì landed) + verify toàn Wave 2.
4. W2-08, W2-09, W2-12 (bị giữ lại vì trùng file).
5. W3 → W4 → W5.

---

## 9. Nhật ký

| Ngày | Wave | Ghi chú |
|---|---|---|
| 2026-08-15 | W1/W2/W2E | **Hết weekly quota giữa chừng.** W1 implement + 3 verify xong (31 finding), fix pass chết. W2E 3/5 vùng landed. W2 agent chết trước lúc return nhưng **đã kịp ghi file** — 44 file, +4911/−1283. Repo để lại ở trạng thái vỡ build (`IAssetScope` thêm member, 3 implementer chưa cập nhật; `CacheEntry` ctor thành private, `ThreadSafeCacheManager` chưa đổi). Sửa tay → build xanh. Áp 4/16 finding blocker. Chi tiết §10. |
| 2026-08-15 | W0 | Lập task manual. Dựng compile gate: Unity Editor **không** mở batch-mode (tránh upgrade project 6000.2.8f1 → 6000.5.7f1); gọi thẳng Roslyn của Unity, dependency build từ `Library/PackageCache` source. Phát hiện `Library/ScriptAssemblies` chỉ có 35 dll và **thiếu** `Unity.Addressables`/`Unity.ResourceManager` → phải tự dựng. |
