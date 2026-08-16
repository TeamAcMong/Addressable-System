# Bàn giao từ Session A sang Session B

> **Session A dừng. Toàn bộ việc còn lại nằm trong file này.** B sẽ làm mà không hỏi lại A được,
> nên file này viết để đọc một lần rồi làm, không phải để tra cứu.

| | |
|---|---|
| Worktree | `.claude/worktrees/cdn-system-package-review-5545d3` · nhánh `feat/cdn-system` |
| Package | `com.game.addressables` **4.1.0-pre.5**, `unity: 2023.1` |
| HEAD lúc bàn giao | `4d6a6ca` — *compile the UniTask half of the package for the first time* |
| Nguồn của file này | 5 reviewer mở **từng file ở trạng thái hiện tại** và đối chiếu lại toàn bộ work order |

## Vì sao phải viết lại thay vì đưa thẳng work order cũ

Work order ở `REFACTOR_TASKS.md §0.2` được viết theo audit của nhánh `main` **cũ**, rồi rebase một
phần. Wave 1 sau đó viết lại `AssetLoader.cs` (+1000 dòng) và `Runtime/Core/*`. Nên **mọi số dòng
trong work order cũ đều phải nghi ngờ**.

Kết quả đối chiếu — và đây là phần đáng ngạc nhiên:

| Vùng | Số dòng của work order cũ | Vì sao |
|---|---|---|
| `Runtime/Core/TieredCache.cs`, `ThreadSafeCacheManager.cs` | **còn đúng nguyên** | Wave 1 chưa từng mở hai file này |
| `Runtime/Loaders/TieredAssetLoader.cs`, `Progress/ProgressiveAssetLoader.cs` | **còn đúng nguyên** | Không nằm trong blast radius Wave 1 |
| `Runtime/Pooling/*` | **còn đúng nguyên** | `git log` dừng ở `3a3bd5c` (v3.5.1) |
| `Runtime/API/*`, `Facade/*`, `Scopes/*` | lệch nhỏ — chỉ `SimpleAPI.cs` đổi (`43,55,196,203` → `43,54,203,211`) | |
| `Editor/` ngoài `Cdn/` | chưa ai đo lại | Wave 2E chưa bắt đầu |

**Số dòng thì sống, nhưng NGHĨA thì chết.** Ba đơn thuốc trong work order cũ giờ **sai** và làm theo
sẽ tạo bug nặng hơn hiện tại — xem §7.2. Đó mới là lý do thật của lần verify này.

**Thống kê:** 66 mục được verify. **0 mục ALREADY_FIXED.** 5 mục đổi hình dạng (`CHANGED`), phần còn
lại `PRESENT` đúng như mô tả. Tầng API/scope/facade, pooling và caches hoàn toàn chưa ai đụng tới.

---

## 1. Bắt đầu từ đâu

### Nghi thức trước mỗi lần bắt tay (PARALLEL_SESSIONS.md §5)

```bash
cd "e:/Git Pj/Addressable-System/.claude/worktrees/cdn-system-package-review-5545d3"
git status --short                      # xem có ai đang sửa dở không
bash Tools/CompileGate/run.sh           # phải PASS TRƯỚC khi mình sửa
```

Thấy file lạ đang sửa dở thì **không đụng vào** — ghi vào §3 của `PARALLEL_SESSIONS.md` rồi làm việc
khác. Xong việc: chạy lại gate, cập nhật §2, ghi vào §6.

> **Cảnh báo về baseline:** **không reviewer nào chạy gate** trong đợt verify này, cả 5 người cùng
> một lý do — lúc đó B đang sửa dở `AssetLoader.cs` + `Runtime/Threading/`, nên đỏ thì không quy được
> trách nhiệm, mà xanh thì cũng không chứng minh gì. Việc của B đã commit (`9a19790`, `4d6a6ca`),
> nên **lần chạy gate đầu tiên của B là lần đầu tiên baseline được xác nhận sau khi registry landed.**
> Nếu nó đỏ, đó là nợ có sẵn chứ không phải do mục nào dưới đây.

### Ba việc đầu tiên, đúng thứ tự

**1 — Trả vùng ở `PARALLEL_SESSIONS.md §2`, rồi claim vùng mình sắp làm.**

`§2` vẫn ghi `Runtime/Loaders/AssetLoader.cs` + `Runtime/Threading/` là **ĐANG GIỮ** bởi B, trong khi
`git status --short` đã sạch và việc đã commit. Bốn nhóm việc bên dưới bị chặn đúng ở dòng đó
(A-11 cần cache-probe API, L-8 cần progress seam, P-5 tuỳ chọn (b) cần entry point mới). Đây là việc
rẻ nhất trong cả file và nó mở khoá nhiều nhất.

Đồng thời claim: `Runtime/Core/`, `Runtime/Loaders/`, `Runtime/Pooling/`, `Runtime/API/`,
`Runtime/Facade/`, `Runtime/Scopes/`, `Runtime/Progress/` — toàn bộ là vùng của A, giờ không còn ai
giữ. `Tests/` và `Editor/` (ngoài `Cdn/`) vẫn là **chưa ai** — phải claim trước khi đụng.

**2 — Gói nguyên tử refcount của cache (§4.1, C-1…C-6 + C-11).**

Đứng đầu vì hai lý do, và lý do thứ hai quan trọng hơn:

- Đây là use-after-free **duy nhất với tới được từ API public có ghi trong README**
  (`README.md:339` dạy gọi `Advanced.ForceEviction(loader)` → `AdvancedAPI.cs:213` →
  `TieredAssetLoader.cs:368-375` → `TieredCache.ForceEviction` `:349-355` → `PerformEviction` →
  `entry.Handle?.Release()` trên reference **của caller**).
- Nó là **tiền đề** của ba nhóm khác. Chừng nào cache chưa sở hữu reference của chính nó thì không
  ai viết được ngữ nghĩa đúng cho teardown (L-1), cho ClearPool (P-5), hay cho ngân sách dùng chung
  (L-4). Sửa nhóm kia trước là sửa lên một nền chưa có.

**Sáu mục này là MỘT patch, không phải sáu.** Chi tiết vì sao ở §5.

**3 — Cặp critical của facade/scope (§4.3, A-1 + A-2) — một agent, một change.**

`AddressablesFacade.cs:361` và `GlobalAssetScope.cs:56`. Xếp thứ ba vì sau lần facade teardown đầu
tiên thì **mọi** `Simple.*` và mọi `Facade.GetGlobalScope().Loader` NullReference vĩnh viễn — tức là
mọi fix khác không test được trong play mode. Phải một agent làm cả hai: một trong hai phương án của
A-2 là **xoá** `AddressablesFacade.cs:367`, chính là dòng mà A-1 vừa di chuyển. Tách ra thì người
thứ hai xoá mất tiền đề của người thứ nhất.

---

## 2. Ràng buộc bắt buộc

### 2.1 Routing — không viết C# trong main conversation

`CLAUDE.md §Agent Routing Rules` cấm tuyệt đối. Mọi mục ở §4 đã ghi sẵn agent phải dùng. Bảng quyền
ghi, để không route nhầm vào agent read-only rồi mất một vòng:

| Nhóm | Agent | Ghi được? |
|---|---|---|
| Plan / phân tích | `unity-tech-lead-orchestrator`, `unity-project-analyst`, `Explore` | ❌ |
| Review / chẩn đoán | `unity-code-reviewer`, `unity-performance-optimizer` | ❌ |
| Implement | `unity-gameplay-programmer`, `unity-tools-programmer`, `unity-build-engineer`, `unity-qa-engineer`, … | ✅ |
| Tài liệu | `documentation-specialist` | ✅ (không có Bash) |

Ánh xạ cho file này:

| Vùng việc | Agent |
|---|---|
| `Runtime/**` (cache, loader, pool, scope, API, facade, progress) | `unity-gameplay-programmer` |
| `Editor/Filters`, `Editor/Rules`, `Editor/Automation`, `Editor/Windows`, `Editor/Inspectors` | `unity-tools-programmer` |
| `Editor/CLI/AddressableCLI.cs`, `Tools/CompileGate/` | `unity-build-engineer` |
| `README.md`, `Documentation/*` | `documentation-specialist` |
| Test EditMode/PlayMode | `unity-qa-engineer` |
| Quyết định đa tầng (L-7, A-12, P-5, W4-05) | `unity-tech-lead-orchestrator` **để plan trước**, rồi implementer |
| Đo `GetRuntimeMemorySizeLong` (L-5) | `unity-performance-optimizer` đo (read-only) → `unity-gameplay-programmer` áp |

**Bước 4 của flow không được bỏ:** `unity-code-reviewer` là read-only, finding của nó phải quay lại
đúng agent đã viết đoạn code đó — không sửa ở main.

**Kiểm đĩa trước khi đọc báo cáo.** Ở Phase 1 wave 1, **4/6 agent báo hoàn thành mà không tạo ra
file nào**. Sau mỗi lần delegate:

```bash
git status --short
grep -rn "<TênSymbolMongDoi>" --include=*.cs Packages/
```

### 2.2 Chữ ký kép UniTask/Task (invariant 3)

Mọi API async public phải có **cả** nhánh `#if UNITASK_PRESENT` (`UniTask<T>`) **và** nhánh `Task<T>`.
Đây là quy ước sẵn có, không phải lựa chọn.

Vấn đề: **gate không define `UNITASK_PRESENT`**, nên 97 chỗ rẽ trên 16 file chưa từng được compile
bởi gate mặc định. Cụ thể trong phạm vi file này: `ProgressiveAssetLoader.cs` có 7 khối `#if`,
`MonitoredAssetLoader.cs` có 4, `AddressablePoolManager.cs` có cặp `#if` ở `:88-100` và `:189-201`.
**Sửa trong nhánh đó phải đọc bằng mắt và giữ hai nhánh đối xứng tuyệt đối** — hoặc chạy chế độ
`--min-unity` sau khi làm xong §6.

`TieredAssetLoader.cs` hiện **vi phạm thẳng**: `grep -c UNITASK_PRESENT` = **0**, cả hai load method
hard-code `public async Task<IAssetHandle<T>>` (`:87`, `:191`). Xem L-7.

### 2.3 Không sentinel value (invariant 4)

Thao tác có thể thất bại thì trả `LoadResult<T>` / `CdnResult<T>`. Không dùng `null`/`false`/`0` để
ám chỉ hai chuyện khác nhau. Đang vi phạm: `ProgressiveAssetLoader.cs:76/78` (trả `null`), `:251`
(trả `false`), và toàn bộ đường fail của `TieredAssetLoader` (trả `null`).

Áp dụng trực tiếp vào P-1: bản thay thế cho `Spawn` **không được** trả `null` để vừa nghĩa "pool chưa
sẵn sàng" vừa nghĩa "load hỏng".

### 2.4 `[Obsolete]` chứ không xoá, tới 5.0.0 (invariant 6)

Chi phối L-7 (`TieredAssetLoader`), A-8 (`LoadScene<T>`), A-9 (`Simple.Release`), A-12
(`Advanced.GetGlobalScope`), E-nhóm (`PoolConfiguration` inert). Hệ quả cụ thể: **một class đã đánh
`[Obsolete]` mà vẫn rò rỉ toàn bộ asset thì không ship được ở 4.x** — nên L-1/L-8 vẫn phải sửa dù
L-7 quyết định khai tử cả class.

Cũng cấm luôn việc "sửa cho gọn" chữ ký public: không đổi `void Pin(string)` thành `bool`
(C-10), không đổi `void Set(...)` thành `bool` (C-6).

### 2.5 ⚠️ BẪY — `#pragma warning disable CS0618` ở `SceneAssetScope.cs`

**Đã xác minh còn nguyên tại chỗ, ở worktree hiện tại:**

```
Runtime/Scopes/SceneAssetScope.cs:159   #pragma warning disable CS0618
Runtime/Scopes/SceneAssetScope.cs:160   var all = FindObjectsByType<SceneAssetScope>(
                                            FindObjectsInactive.Include, FindObjectsSortMode.None);
Runtime/Scopes/SceneAssetScope.cs:161   #pragma warning restore CS0618
```

kèm 24 dòng comment giải thích ngay phía trên (`:136-158`).

**Đoạn code này ĐÚNG. Cảnh báo CS0618 ở đây là cảnh báo phải để nguyên.**

Unity gợi ý thay bằng `FindObjectsByType<T>()` hoặc `FindObjectsByType<T>(FindObjectsInactive)`.
**Cả hai overload đó chỉ tồn tại từ Unity 6.** `package.json` khai `"unity": "2023.1"`. Trên
2022.3/2023.x, overload một tham số cho ra:

```
SceneAssetScope.cs(136,58): error CS1503: cannot convert from
'UnityEngine.FindObjectsInactive' to 'UnityEngine.FindObjectsSortMode'
```

**Chuyện này đã ship rồi.** `4.1.0-pre.4` xanh toàn tập trên 6000.5.7f1 — 0 error CS, 0 warning CS,
57/57 test xanh — và **vỡ ngay khi người dùng mở bằng editor cũ hơn**. `4.1.0-pre.5` (`22d762d`) là
bản vá đúng chỗ này.

Vì sao bẫy này nguy hiểm với B chứ không chỉ với A: **A-8 sửa vào `SceneAssetScope.GetOrCreate`**,
tức là đúng method chứa đoạn trên. Agent nhận A-8 sẽ thấy cảnh báo và rất có thể "dọn" nó.
**Prompt gửi agent phải nói thẳng: để nguyên byte-for-byte cả ba dòng `:159-161` lẫn comment
`:136-158`.**

Và đây là loại lỗi mà gate mặc định **không bắt được** — nó ghim ref `6000.5.7f1`. Xem §6.

### 2.6 Commit và phối hợp

- **Invariant 7 / global rule:** không `git add -A`, không `git add .`, không `commit -a`. Liệt kê
  tay từng đường dẫn thuộc phiên đang chạy. Hai session dùng chung branch này và việc đã từng mất.
- File dùng chung phải **ghi vào §2 trước khi sửa, trả ngay sau khi sửa**: `package.json`,
  `CHANGELOG.md`, `README.md`, `*.asmdef`, `Tools/CompileGate/`. C-10 và P-1 đều đụng `README.md`.
- Lưu ý trạng thái tracking hiện tại: `Tools/CompileGate/`, `Documentation/PARALLEL_SESSIONS.md`,
  `Documentation/REFACTOR_TASKS.md` và file này đều **chưa được track** (`??` trong `git status`).
  Khi commit nhớ add tường minh, đừng để rơi.

---

## 3. Hợp đồng refcount Wave 1 — đọc kỹ, đây là chỗ dễ hiểu sai nhất

Wave 1 (`3c6017e`) đổi ngữ nghĩa release. Code cũ viết theo ngữ nghĩa cũ **vẫn compile** — nên trình
biên dịch không cứu được ai ở đây. Mọi mục ở §4 chạm vào handle đều phụ thuộc bảng này.

### 3.1 Bốn thay đổi

| Trước | Sau |
|---|---|
| `Dispose()`/`Release()` = hard release, gọi thẳng `Addressables.Release` | **decrement**. Chỉ khi count về 0 mới `Addressables.Release` |
| — | `ForceRelease()` (`internal`, qua `IOwnedHandle`) = hard release, **chỉ owner** |
| `Retain()` khi đã chết → warn | `Retain()` khi đã chết → **throw `ObjectDisposedException`** |
| cache giữ handle mượn | **cache giữ reference của chính nó** |

Nguồn: `AssetHandle.cs:61-64` (`Release()` → `if (_references.Release()) ReleaseOperation()`),
`:47-54` (`Retain()` throw), `:93-98` (`Addressables.Release` ở count 0),
`IAssetHandle.cs:79-91` (`IOwnedHandle`).

### 3.2 Ai giữ reference nào

Quy tắc gốc, trích nguyên văn `Runtime/Core/AssetHandle.cs:10-14`:

> *a handle is born with one reference, owned by the caller that received it. Every further owner —
> the loader's cache, another caller served from that cache — takes its own reference through
> `TryRetain()`.*

Diễn ra thật, trên `AssetLoader` (đã landed, đúng):

| Bước | Ai | Làm gì | refcount |
|---|---|---|---|
| 1 | `AssetHandle` ctor | sinh ra với 1 reference cho người nhận (`AssetHandle.cs:44`) | 1 |
| 2 | `AssetLoader.CacheHandle` | `handle.Retain()` rồi `_assetCache[key] = handle` (`:319-320`) | **2** |
| 3 | caller thứ hai, cache hit | `TryRetain()` | 3 |
| 4 | caller thứ nhất `Dispose()` | decrement | 2 |
| 5 | `InvalidateAddress` (`:1711`) | `Dispose()` — chỉ trả reference **của cache** | 1 |
| 6 | caller thứ hai `Dispose()` | về 0 → `Addressables.Release` chạy | 0 |

**Câu một dòng:** *ai lấy reference thì người đó trả; trả bằng `Dispose()`/`Release()`; chỉ owner
lúc teardown mới được `ForceRelease()`.*

### 3.3 Hai hệ quả người ta hay làm ngược

**(a) `if (h.IsValid) h.Retain()` — pattern hai bước — giờ là bug.**

Dùng `h.TryRetain()`. Vị trí: `AssetHandleExtensions.cs:24` (public extension) hoặc
`internal IRetainableHandle` (`IAssetHandle.cs:66-73`). `Runtime/Core` và `Runtime/Loaders` cùng
assembly `AddressableManager` nên gọi kiểu nào cũng được.

Một đính chính so với `PARALLEL_SESSIONS.md §4` mục 2 (viết là "giờ là crash"): trên **main thread**
nó **không** crash tất định. Wave 1 làm `IsValid` đọc refcount **trước** (`AssetHandle.cs:31`:
`_references.IsAlive && _handle.IsValid() && _handle.Status == Succeeded`) và comment ở `:29-30` nói
rõ hai hàm cùng đọc một word, không có khe. Khe còn lại là **cross-thread**: finalizer của
`SmartAssetHandle` (`SmartAssetHandle.cs:145`, regression đã biết, `REFACTOR_TASKS §10`) có thể thả
reference cuối từ finalizer thread đúng giữa hai bước. Vẫn phải sửa (hai call site nằm **ngoài**
`try` nên exception thoát thẳng ra `await` của caller), nhưng **đừng brief nó là P0** — đuổi theo một
crash không tái hiện được là cách nhanh nhất làm B mất tin vào file này.

**(b) `TryRetain()` KHÔNG có trên `public IAssetHandle<T>` — cố ý.**

Nó nằm ở `internal IRetainableHandle` + extension method, để `IAssetHandle<T>` **byte-compatible với
4.1.0-pre.4**. Thêm vào interface public là source-breaking và phải bump 5.0.0. **Đừng thêm.**

### 3.4 Chỗ hợp đồng này ĐÃ được tôn trọng — đừng "sửa"

`AddressablePoolManager._templateHandles` lưu handle ở `:167`/`:245` rồi trả bằng `Dispose()` ở
`:479`. Handle tới tay pool ở refcount 2 (caller + cache của loader), pool sở hữu đúng phần của
caller, và trả đúng một lần. **Đây là kỷ luật đúng.** Work order cũ (row 4) bảo thêm `TryRetain()` —
làm thế thành giữ 2 trả 1, biến "asset còn nằm trong cache" thành **rò rỉ vĩnh viễn không có đường
release**. Xem P-5.

---

## 4. Việc cần làm

Trong mỗi nhóm, xếp theo **thiệt hại nếu bỏ qua**, không theo file. Thứ tự **giữa** các nhóm ở §5.

Mọi số dòng dưới đây là số dòng **hiện tại**, do reviewer mở file ra đọc. Chỗ nào không có số dòng
đã xác minh thì ghi rõ là không có.

### 4.1 Caches — `Runtime/Core/TieredCache.cs`, `ThreadSafeCacheManager.cs`

> **C-1…C-6 + C-11 là MỘT patch.** Land từng phần sẽ biến rò rỉ thành double-decrement (chi tiết §5).
> Agent cho cả nhóm: `unity-gameplay-programmer`.

#### C-1 · `Set()` lưu handle mà không retain, `PerformEviction` lại release nó — `critical`

`TieredCache.cs:61-62` · `:271-274` · song sinh ở `ThreadSafeCacheManager.cs:58` · `:245`

```csharp
// TieredCache.cs:61-62 — không lấy reference nào
var entry = new CacheEntry<T>(key, handle, estimatedSize);
_cache[key] = entry;

// TieredCache.cs:271-274 — trả một reference chưa từng lấy
var entry = _cache[key];
entry.Handle?.Release();   // Release the handle
```

`TieredAssetLoader.cs:147-169` chứng minh chỉ tồn tại **đúng một** reference:
`var handle = new AssetHandle<T>(operation); … cache.Set(cacheKey, handle, …); … return handle;` —
cùng một object vừa nhét vào cache vừa đưa cho caller, và nó sinh ra ở count 1
(`AssetHandle.cs:44`).

**Hỏng ra sao:** eviction tiêu reference của caller, `Addressables.Release` chạy, caller còn cầm
handle trỏ vào asset đã unload — mà `Dispose()` sau đó của chính caller lại **no-op**
(`AssetReferenceCounter.Release` trả `false` ở count 0, `IAssetHandle.cs:165`). Với tới được từ
README (`:339` → `Advanced.ForceEviction`). Trước Wave 1 chỗ này chỉ sai; giờ nó là use-after-free.

**Sửa:** trong **cả hai** overload `Set()`, lấy reference của cache trước khi lưu, và coi thất bại là
**từ chối cache** chứ không phải throw: `if (!handle.TryRetain()) return;`
(`AssetHandleExtensions.cs:24`, cùng assembly). Sau đó `Release()` ở `:273` / `:245` trở thành đúng
như đang viết. **Không đổi sang `ForceRelease()`** — đó chính là hard-release-dưới-chân-holder mà A
vừa gỡ khỏi `EvictAddress`.

#### C-2 · `TryGet` phát handle ra ngoài mà không retain — `high`

`TieredCache.cs:89-93` · `ThreadSafeCacheManager.cs:97-101` (`handle = entry.Handle;` ở `:100`)

```csharp
if (_cache.TryGetValue(key, out var entry))
{
    entry.RecordAccess();
    handle = entry.Handle;      // :92 — không TryRetain, không kiểm IsValid
    _cacheHits++;
```

**Hỏng ra sao:** caller gốc và **mọi** caller cache-hit dùng chung một reference. Ai `Dispose()`
trước thì unload asset cho tất cả. Cache hit cũng có thể phát ra handle đã chết. Đây là nửa gây
use-after-free **không cần memory pressure**.

**Sửa:** chỉ trả reference mình đã lấy, entry chết coi như miss:
`if (_cache.TryGetValue(key, out var entry) && entry.Handle.TryRetain()) { … }` — else remove entry,
trả `false`. Handle trả ra **mang theo một reference caller phải `Release()`**. Việc này **viết lại**
hai call site ở `TieredAssetLoader` → phải sửa cùng patch, xem C-6.

#### C-3 · `Clear()`/`Dispose()` xoá dictionary mà không release gì — `high`

`TieredCache.cs:161-166` (`Clear`), `:357-363` (`Dispose` chỉ gọi `Clear`) ·
`ThreadSafeCacheManager.cs:190`, `:336`

```csharp
public void Clear()
{
    _cache.Clear();           // :163 — không đụng tới một handle nào
    _currentCacheSize = 0;
    ResetStatistics();
}
```

Bốn method, không có `Release()`, không có `ForceRelease()`, không có vòng lặp nào trên `Values`.

**Hỏng ra sao:** hiện tại **không tầng nào trong stack này release cả**. `TieredAssetLoader.ClearCache`
(`:392-393`) cũng chỉ `_tieredCaches.Clear(); _activeHandles.Clear();` — trong khi log ở `:427` ghi
*"Disposing loader and releasing all assets"*. Dispose loader để lại mọi operation Addressables sống
tới hết process.

**Sửa:** sau khi C-1 làm `Set()` retain, duyệt entry và `entry.Handle?.Release()` trước
`_cache.Clear()` ở **cả bốn** method. `Dispose()` vẫn forward sang `Clear()`. Với
`ThreadSafeCacheManager` phải theo ràng buộc lock ở C-8.

#### C-4 · `Remove()` bỏ entry mà không release — `medium`

`TieredCache.cs:124-133` · `ThreadSafeCacheManager.cs:125-141` (`:132` chỉ trừ size)

Cùng họ với C-3 nhưng dễ sót nhất, và **tần suất gọi cao nhất**: `TieredAssetLoader.cs:134` và `:237`
gọi `cache.Remove(cacheKey)` trên đường dọn entry chết. Sửa chung patch với C-3. Trong
`ThreadSafeCacheManager`, `Remove` đã giữ write lock (`:127`) nên thừa hưởng ràng buộc C-8.

#### C-5 · `Set()` trên key đã có: im lặng vứt handle vào — `medium`

`TieredCache.cs:53-58` · `ThreadSafeCacheManager.cs:52-55`

```csharp
if (_cache.TryGetValue(key, out var existingEntry))
{
    existingEntry.RecordAccess();
    return;                      // handle tham số: không lưu, không release, không so sánh
}
```

`Set()` lại là `void` (`:42`) nên caller không có cách nào biết handle của mình bị vứt.

**Hôm nay** hậu quả nhẹ (duplicate load). **Sau C-1** thì đây đúng là chỗ patch dở dang sẽ rò rỉ:
đường này phải quyết ai sở hữu reference nó không lấy.

**Sửa:** hoặc `if (!ReferenceEquals(existingEntry.Handle, handle)) handle.Release();`, hoặc thay hẳn
entry (release cái cũ, retain + lưu cái mới). Chọn một, **ghi vào XML doc của `Set()`**. Không đổi
chữ ký sang `bool` (§2.4).

#### C-6 · Hai chỗ `if (IsValid) Retain()` cuối cùng, nằm trên đường cache hit — `high`

`TieredAssetLoader.cs:111-116` và `:216-220`

```csharp
if (cache.TryGet(cacheKey, out var cachedHandle))
{
    if (cachedHandle.IsValid)
    {
        Debug.Log($"[TieredAssetLoader] Cache hit for: {address}");
        cachedHandle.Retain();      // :116  (và :220)
```

Cả hai nằm **ngoài** `try` (mở ở `:139` / `:242`) nên `ObjectDisposedException` thoát thẳng ra
`await` của caller, không rơi vào `catch (Exception ex)` ở `:177` / `:274`.

Đây đúng là hai chỗ work order cũ row 2 gọi là "hai chỗ cuối cùng còn sót", và **số dòng
`113-116`/`218-220` của nó vẫn resolve đúng**.

**Sửa (sau C-2):** xoá cả `cachedHandle.Retain()` lẫn `if (cachedHandle.IsValid)` bao ngoài —
`TryRetain()` bên trong `TryGet` trở thành phép kiểm tính hợp lệ, và `false` chính là cache miss rơi
xuống load:

```csharp
if (cache.TryGet(cacheKey, out var cachedHandle) && cachedHandle.TryRetain())
{
    … báo cache hit …
    return cachedHandle;
}
cache.Remove(cacheKey);   // gộp luôn nhánh else ở :134 / :237, đúng cho cả ca "có mà chết"
```

Nếu vì lý do gì C-2 bị hoãn: thay tạm bằng `if (cachedHandle.TryRetain())` và **chuyển khối vào
trong `try`**. Tuyệt đối không để lại `Retain()` trần. Để nguyên sau khi C-2 land = mỗi cache hit rò
rỉ một reference vĩnh viễn.

#### C-7 · `ThreadSafeCacheManager` vẫn quảng cáo "any thread" trong khi mọi thao tác chạm Unity API — `high`

`ThreadSafeCacheManager.cs:9-12` (class summary) và tag `(thread-safe)` ở `:35`, `:82`, `:123`,
`:144`, `:163`, `:183`, `:261`.

Mọi đường đó dẫn tới main-thread-only API qua `CacheEntry`:
`Set` → ctor → `CacheEntry.cs:58-59` `Time.realtimeSinceStartup`;
`TryGet` → `RecordAccess()` → `CacheEntry.cs:72`;
`PerformEviction` → `CalculateTierScore()` → `CacheEntry.cs:80`, `:88`.
Nặng hơn: eviction chạm `Addressables` off-thread — `:245` → `AssetHandle.cs:63` → `:97`.

Hai chỗ sai nữa cùng vị trí: doc ghi *"lock-free data structures"* trong khi class dựng trên
`ReaderWriterLockSlim` (`:17`, `:30`); và `TryGet`/`Pin`/`Unpin` **mutate** state trong khi chỉ giữ
**read** lock.

**Hỏng ra sao:** chính cái doc này cho phép việc dùng sai — `README.md:419-420` demo
`Advanced.CreateThreadSafeLoader(...)` chạy từ `Task.Run`. Không lượng lock nào làm cho một API
main-thread-only gọi được từ thread khác. Class này thread-*consistent*, không thread-*safe*.

**Sửa — tách hai nửa:**
- **(a) Doc, làm ngay, không phụ thuộc gì:** sửa class summary + các tag `(thread-safe)` để nói rõ
  bookkeeping và eviction cần main thread; bỏ chữ "lock-free".
- **(b) Code, để doc thành sự thật:** bỏ lấy thời gian từ Unity — dùng `Stopwatch` /
  `Environment.TickCount64` trong `CacheEntry`; đẩy release ở `:245` qua main-thread dispatcher.
  Nửa dispatcher chạm `Runtime/Threading/` → xem §2.6 về claim vùng.

#### C-8 · Release handle trong lúc đang giữ **write lock** — `medium`

`ThreadSafeCacheManager.cs:243-247`, gọi từ `Set()` ở `:69`, giữa `EnterWriteLock` (`:48`) và
`ExitWriteLock` (`:77`); header của chính nó ghi *"must be called within write lock"* (`:201`).

Ở count 0 nó thành `Addressables.Release` (`AssetHandle.cs:93-98`) — **unload bundle đồng bộ trong
critical section**, vòng lặp không chặn trên (`:238`). Không deadlock (Addressables không re-enter
cache này), nhưng mọi thread khác đứng chờ — đúng lúc memory pressure là lúc đông thread nhất.
`TieredCache` không có lock nên không dính.

**Sửa:** gom handle bị evict vào list cục bộ **trong** lock, thoát lock, rồi release. Tức là
`PerformEviction` trả về danh sách nạn nhân và `Set()` drain trong `finally` sau `ExitWriteLock`.
Không hạ xuống read lock để release.

#### C-9 · `Pin()` im lặng no-op với key chưa cache — và README dạy pin trước khi load — `medium`

`TieredCache.cs:138-145` · `ThreadSafeCacheManager.cs:146-161`. Không `else`, không giá trị trả về,
không log. Đường public cũng im: `TieredAssetLoader.cs:288-293` → `AdvancedAPI.cs:188-191`.

README dạy sai thứ tự **hai lần**: `README.md:326-332` (pin `"UI/CoreIcon"` rồi mới load `"UI/Icon1"`
— `"UI/CoreIcon"` không được load ở đâu cả) và `README.md:416-417` (pin ngay sau
`Advanced.CreateTieredLoader`, trên loader chưa load gì).

**Hỏng ra sao:** người dùng tin asset UI trọng yếu đã được bảo vệ, `GetStatistics().PinnedEntries`
báo 0 (`TieredCache.cs:294`) và không ai nhìn, rồi asset bị evict — đúng kịch bản pin sinh ra để
chặn. Cộng với C-10: `IsPinned` là **miễn trừ eviction duy nhất**, nên pin trượt = mất trắng bảo vệ.

**Sửa — hai nửa, hai agent:**
- Code (`unity-gameplay-programmer`): làm cho miss quan sát được mà không đổi chữ ký — lưu pending-pin
  để `Set()` sau đó áp dụng (đúng ý README), và/hoặc log warning dưới `_config.LogTierOperations`.
- Docs (`documentation-specialist`): sửa cả hai ví dụ README thành pin **sau** load, viết rõ ràng
  buộc thứ tự. **Quyết nửa code trước** — nếu làm pending-pin thì README đang đúng và chỉ cần chú
  thích. `README.md` là file dùng chung, claim ở §2 trước.

#### C-10 · Eviction chỉ lọc `IsPinned`, không đọc `ReferenceCount` — **nhưng ĐỪNG thêm guard đó** — `high`

`TieredCache.cs:240-244` · `ThreadSafeCacheManager.cs:217-223`. Grep cả hai file: **không có** một
lần xuất hiện nào của `ReferenceCount` hay `IsAlive`.

Mô tả thì đúng, **nhưng đơn thuốc của W2-06 ("eviction skip `ReferenceCount > 1`") sai** và phải nói
rõ để B không implement:

- **Racy** — count có thể tụt giữa lúc đọc và lúc evict, và tăng giữa lúc đọc và lúc release.
- **Thừa** — sau C-1, eviction là decrement, holder còn sống thì asset còn sống, tự nhiên.
- **Có hại** — "còn holder khác" thành **miễn trừ eviction vĩnh viễn**: một asset phổ biến luôn có
  người giữ sẽ ghim cache ở trần dung lượng và eviction quay vòng mà không giải phóng được byte nào.

Đây đúng kết luận A đã rút ở tầng dưới: `InvalidateAddress` evict bằng `Dispose()` và để holder sống
(`PARALLEL_SESSIONS.md §3` bổ sung 15:24, `AssetLoader.cs:1711`), không hỏi count.

**Sửa:** C-1 **chính là** cách sửa mục này. Giữ `IsPinned` làm miễn trừ duy nhất. Muốn chẩn đoán thì
log `ReferenceCount` dưới `_config.LogTierOperations` — **đọc, đừng rẽ nhánh theo nó**.

#### C-11 · `TryGet`/`Pin`/`Unpin` mutate state khi chỉ giữ **read** lock — `low`

`ThreadSafeCacheManager.cs:94-101` (`RecordAccess()` ở `:99` → `CacheEntry.cs:69-73` viết hai field
không atomic), `Pin` mutate `IsPinned`/`Tier` dưới `EnterReadLock` (`:148` → `:153-154`), `Unpin`
tương tự (`:168` → `:173`). Trong khi eviction **đọc** đúng những field đó dưới **write** lock
(`:219`, `:230`, `:241`) — tức là bên ghi mới là bên nằm ngoài vùng loại trừ.

Triệu chứng nhìn thấy được không phải counter rách mà là **một entry bị evict dù caller tin đã pin
nó** — không phân biệt được với C-9 nhưng tới bằng đường khác. Ai sửa C-9 rồi test pin thấy chạy được
sẽ không biết đường thứ hai này tồn tại.

**Sửa:** write lock cho `Pin`/`Unpin`; `TryGet` thì hoặc write lock hoặc làm bookkeeping
interlocked/xấp xỉ (nên chọn cách sau, `TryGet` là hot path). Gộp vào lượt C-7 vì cùng đụng những
field đó.

#### C-12 · `estimatedSize` mặc định 0 — chỉ đúng cho `ThreadSafeCacheManager` — `medium` · `CHANGED`

`TieredCache.cs:42` và `ThreadSafeCacheManager.cs:37` đều `long estimatedSize = 0`.

**Nhưng "không ai truyền cân nặng thật" là SAI với `TieredCache`.** Caller duy nhất trong package
truyền đủ cả hai đường: `TieredAssetLoader.cs:150-153` và `:250-252`, qua `EstimateAssetSize`
(`:403-417`) — luôn khác 0 với asset non-null. Nên `_currentCacheSize` có cộng dồn, và phép so tỷ lệ
ở `TieredCache.cs:68-69` **có** kích hoạt so với mặc định 100MB (`TieredCacheConfig.cs:14`).

Đúng với `ThreadSafeCacheManager`: grep cả package **không có caller nào** gọi `Set()` của nó. Nơi
duy nhất tạo nó là factory `AdvancedAPI.cs:181-184`, đưa thẳng cho user code — user không truyền size
thì `_currentCacheSize` đứng ở 0 và `PerformEviction` không bao giờ chạy tới.

**Vì sao phải nói rõ:** báo phẳng "eviction theo size không bao giờ chạy" sẽ khiến B đi lắp lại đường
ống đã có, và che mất chuyện **eviction của `TieredCache` đang sống thật hôm nay** — chính đó là thứ
làm C-1 khai thác được trong thực tế chứ không phải trên lý thuyết.

**Sửa — tách đôi:** `TieredCache` không cần gì (ước lượng có hơi lạc quan vì bỏ mipmap/compression,
đáng một comment chứ không đáng một change). `ThreadSafeCacheManager`: hoặc cấp cho nó một producer,
hoặc ghi lên class rằng consumer phải tự truyền `estimatedSize`, hoặc bỏ hẳn đường eviction theo
size. **Đây là quyết định thiết kế — hỏi user, đừng để agent tự chọn.** Và lưu ý: sửa nó là
**kích hoạt** đường eviction của class đó, nên C-1/C-7/C-8 phải land trước, nếu không chính lần kích
hoạt đó sẽ làm nổ crash.

#### C-13 · `PerformEviction` có chạy re-entrant từ trong `Set()` — nhưng KHÔNG evict nổi entry vừa chèn — `medium` · `CHANGED`

Nửa đầu của claim đúng: `TieredCache.cs:66-73` (ngay sau lệnh chèn ở `:62`) và
`ThreadSafeCacheManager.cs:64-71`, tới từ giữa một lần load (`TieredAssetLoader.cs:153`).

**Nửa sau sai.** Entry vừa chèn bắt đầu ở `Tier = Hot`, `AccessCount = 1` (`CacheEntry.cs:60-61`),
nên `CalculateTierScore()` (`:104-118`) tính với `timeSinceAccess = 0` → recency = 1, `age = 0` →
frequency = `Mathf.Log(2)` ≈ 0.693 → **score ≈ 69.3**. Cổng evict ở `TieredCache.cs:256` /
`ThreadSafeCacheManager.cs:241` là `Tier == Cold || score < EvictionScoreThreshold`, mà threshold là
1.0 / 3.0 / 0.5 (`TieredCacheConfig.cs:44`, `:82`, `:102`, `:122`). Không vế nào đúng với entry mới.

Cũng không có nguy cơ lock recursion: `PerformEviction` của `ThreadSafeCacheManager` không tự lấy
lock nên `ReaderWriterLockSlim` `NoRecursion` (`:30`) không bị vào lại.

**Vì sao đáng ghi:** work order (`§0.2` row 1) viết là *"trong chính lần load sinh ra handle đó"* và
đặt nó làm prerequisite. Re-entrancy có thật, nhưng vế đáng sợ thì không — handle của chính lần load
đó sống sót qua `Set()` của nó. Đưa claim mạnh cho B là mời B đi tái hiện một kịch bản không tái
hiện được, rồi mất tin vào phần còn lại. Thiệt hại thật ở call site này là thiệt hại của C-1: nó
release handle của **entry khác**.

**Sửa:** mang call graph đi, bỏ cách diễn đạt kia, và ghi lại điều kiện thật. Sau khi C-1 land thì
eviction đồng bộ trong `Set()` là chấp nhận được, không cần đổi cấu trúc — chính phép tính score ở
trên là thứ giữ nó an toàn, nên ai chỉnh `EvictionScoreThreshold` lên cao là đang tiêu vào biên an
toàn đó. Muốn sạch thì nhấc phép kiểm eviction ra sau khi `Set()` return — vệ sinh, không phải sửa
lỗi.

> **Test cho cả nhóm (`unity-qa-engineer`):** không file test nào trong `Tests/` chạm tới hai cache
> này — grep `TieredCache`/`ThreadSafeCacheManager` chỉ ra CHANGELOG, README, `AdvancedAPI`,
> `TieredAssetLoader` và chính chúng. Đổi refcount sắc như vậy với zero coverage thì đáng **một**
> EditMode test: evict một entry đang có holder sống → `handle.IsValid == true`.

### 4.2 Loaders — `TieredAssetLoader.cs`, `ProgressiveAssetLoader.cs`

Agent mặc định: `unity-gameplay-programmer`. Ngoại lệ ghi ở từng mục.

`MonitoredAssetLoader.cs` **đã kiểm, sạch** — giờ chỉ là forwarder mỏng với chữ ký kép UniTask/Task
đúng. Một chi tiết mỹ phẩm không đáng thành finding: `Release<T>(handle, address = null)` (`:64-68`)
bỏ qua hẳn tham số `address`, đúng theo ngữ nghĩa decrement mới, nhưng để lại một tham số chết trong
chữ ký public không xoá được trước 5.0.0.

#### L-1 · Teardown KHÔNG release gì cả — mô tả trong work order là ngược — `critical` · `CHANGED`

`TieredAssetLoader.cs:380-394`

```csharp
foreach (var cache in _tieredCaches.Values)
{
    if (cache is IDisposable disposable)
        disposable.Dispose();     // :388 — đây là TieredCache<T>.Dispose, KHÔNG phải handle
}
_tieredCaches.Clear();
_activeHandles.Clear();           // :393 — List<IDisposable> bị xoá, không cái nào được Dispose
```

Đi tiếp: `TieredCache<T>.Dispose()` (`TieredCache.cs:357-363`) → `Clear()` (`:161-166`) → chỉ
`_cache.Clear()`. **Không handle nào bị chạm trong cả chuỗi.**

**Vì sao mô tả cũ nguy hiểm:** work order giả định cache đang giữ reference của nó, nên `Dispose()`
decrement sẽ để lại một reference lơ lửng. **Cả hai vế đều sai:** (a) không có gì trong đường teardown
gọi `Dispose()`/`Release()`/`ForceRelease()` — nên đây là **rò rỉ cứng**, nặng hơn cái được báo;
(b) `TieredCache<T>.Set()` (`:61`) không `TryRetain()`, nên cache **không** sở hữu reference nào —
tức C-1 là **tiền đề, không phải follow-up**. Cùng một luật thiếu sinh ra hai bug ngược chiều: chỗ
này không huỷ gì, còn eviction (`TieredCache.cs:273`) thì huỷ nhầm reference của caller. Ai sửa một
bên mà không sửa bên kia sẽ làm bên còn lại tệ hơn.

**Sửa (sau C-1):**
- `TieredCache<T>.Remove` + vòng eviction: giữ `Release()` (decrement — cache chỉ trả phần của mình,
  holder phải sống sót, đúng ngữ nghĩa A đã land cho `AssetLoader.InvalidateAddress`).
- `TieredCache<T>.Clear`/`Dispose` và `TieredAssetLoader.ClearCache`/`Dispose`: `ForceRelease()`
  (teardown của owner).
- `_activeHandles` (khai `List<IDisposable>` ở `:28`) phải được duyệt và force-release **trước**
  `Clear()`. `ForceRelease()` không nằm trên `IDisposable` → đổi field sang `List<IOwnedHandle>`;
  `IOwnedHandle` (`IAssetHandle.cs:79-91`) sinh ra đúng cho việc này (*"Lets a teardown path release
  a handle without knowing its asset type"*) và được ghi là idempotent — cần đúng tính chất đó vì
  `_activeHandles` và các cache alias cùng một handle.
- Hoặc bỏ hẳn `_activeHandles` vì cache đã sở hữu lifetime. **Chọn một, đừng để cả hai list cùng
  release.**
- Chú ý `TieredAssetLoader.Dispose()` `:423-430` đặt `_disposed = true` **sau** `ClearCache()`, nên
  đường re-entrant nhìn thấy một loader còn sống giữa lúc teardown.

#### L-2 · `ProgressiveAssetLoader` vẫn bypass loader hoàn toàn; `LoadMultipleWithProgressAsync` vứt mọi handle — `critical`

`ProgressiveAssetLoader.cs:50`, `:69`, `:224-246`

Tham số `loader` được khai rồi **không đọc lần nào** (`:24-33`):

```csharp
operation = Addressables.LoadAssetAsync<T>(address);   // :50
…
return new AssetHandle<T>(operation);                  // :69  ← số dòng work order cũ vẫn chính xác
```

```csharp
var tasks = new Task<IAssetHandle<T>>[addresses.Length];   // :224
tasks[i] = loader.LoadAssetWithProgressAsync<T>(address, …); // :233
await Task.WhenAll(tasks);                                  // :242
return true;                                                // :246 — tasks[i].Result không ai đọc
```

**Cái ĐÃ đổi trên nhánh này** (để B khỏi phải đoán): `finally` ở `:80-93` giờ có release operation
thô khi nó chưa kịp được bọc (`:89-91`), và `DownloadWithProgressAsync` (`:121-195`) đã được dựng lại
trên `Cdn.DownloadService`/`CdnManager` và không tạo handle nào. **Cả hai thay đổi không chạm đường
load.** Hai defect gốc còn nguyên.

**Bằng chứng mới work order không có:** đường này **với tới được từ facade mặc định**, không phải từ
một extension không ai gọi — `AddressablesFacade.cs:104`
`return await _globalScope.Loader.LoadAssetWithProgressAsync<T>(address, onProgress);`. Người dùng
"load kèm progress bar" nhận về một handle **trông như** thuộc global scope nhưng thực tế không nằm
trong cache nào, không trong ledger `_activeHandles`, không trong single-flight map.
`Assets.ClearCache()` không thấy nó, teardown global scope không thấy nó, và
`AssetLoader.InvalidateAddresses` vừa land **cũng không với tới** — tức là một lỗ thủng ngay trong cơ
chế mà `PARALLEL_SESSIONS.md §3` sinh ra để bịt. Sau catalog update, handle này tiếp tục phục vụ
bundle cũ, vĩnh viễn.

`LoadMultipleWithProgressAsync` là nửa nặng hơn: handle **tồn tại, đang giữ reference, và không thể
với tới được về mặt cấu trúc**. Không có API nào để caller release. Mười address = mười bundle ghim
tới hết process.

Kèm vi phạm invariant 4: `catch` `:75-79` trả `null`, `:248-252` trả `false`.

**Sửa (W2-10):**
1. `LoadAssetWithProgressAsync` phải delegate sang `loader.LoadAssetAsync<T>(address)` và lấy
   progress từ operation của chính loader thay vì mở operation thứ hai. **Đây là phần cần cẩn thận** —
   loader giờ sở hữu single-flight, nên vòng poll progress không đọc `operation.PercentComplete` từ
   một handle nó tự tạo được nữa. Cần **một seam quan sát progress trên `AssetLoader`**.
2. `LoadMultipleWithProgressAsync` trả `LoadResult<List<IAssetHandle<T>>>` — type đã có, và ba biến
   thể `*Safe` của `AssetLoader` (`:826`, `:1001`, `:1163`) đã lập sẵn khuôn mẫu. Trả `bool` thì
   không thể không rò rỉ.
3. Giữ hai nhánh UniTask/Task đối xứng — file này có 7 khối `#if` và gate không compile nửa UniTask,
   phải đọc mắt.
4. **Không đụng thân `DownloadWithProgressAsync`** — đó là việc CDN của B và đang đúng.

#### L-3 · Không có gì bơm eviction hay tier evaluation — `high`

Call site đầy đủ, grep toàn package:

- `PerformEviction()` ← `Set()` (`TieredCache.cs:71`, trong nhánh `currentRatio >= EvictionTriggerRatio`)
  và `ForceEviction()` (`:353`). Hết.
- `EvaluateAndAdjustTiers()` ← `TryGet()` (`:101`) và `ForceEvaluateTiers()` (`:341`). Hết.

Hai pump thủ công `TieredAssetLoader.EvaluateTiers()` (`:356`) / `ForceEviction()` (`:368`) chỉ với
tới được từ `AdvancedAPI.cs:205` và `:213` — và **không call site nào trong cả package**. Grep
`Runtime/`: không `Application.lowMemory`, không chèn `PlayerLoop`, đúng hai `Update()`
(`UnityMainThreadDispatcher.cs:59`, `AddressableProgressBar.cs:95`) và không cái nào chạm cache.

**Hệ quả — phần đáng viết ra:** bộ nhớ chỉ được thu hồi **trong lúc còn đang cấp phát thêm**. Một pha
gameplay load xong rồi đứng yên — level đã stream xong, menu người chơi đang đứng, giữa trận boss —
đúng là lúc cần rút Cold tier, và đúng là lúc không có gì chạy. `_lastEvaluationTime` (`:36`, `:102`)
vẫn trôi theo wall-clock trong khi tier state đóng băng, nên `Set()` đầu tiên sau quãng lặng evict
dựa trên score cũ.

**Bẫy chẩn đoán:** nếu reflection ở L-8 bị IL2CPP strip thì `ForceEviction()`/`EvaluateTiers()` thành
no-op im lặng với **cùng triệu chứng**. Ai điều tra "cache không bao giờ evict" trong player build
phải loại trừ L-8 trước, nếu không sẽ sửa nhầm chỗ.

**Sửa:** hai pump, đều rẻ. (1) drain định kỳ **do facade sở hữu**, không do cache —
`AddressablesFacade` đã có lifetime MonoBehaviour, một coroutine hoặc `Update()` có cổng interval gọi
`EvaluateTiers()` + `ForceEviction()`, tốn 0 khi cache rảnh. (2)
`Application.lowMemory += …` đăng ký một lần lúc facade init, gọi `ForceEviction()` trên mọi loader
sống — đây là cái thật sự quan trọng trên iOS, nơi OS chỉ cảnh báo một lần trước khi kill.
**Không** đặt pump trong `TieredCache<T>` (không có Unity lifetime, không có đường unsubscribe), và
**không** làm một MonoBehaviour cho mỗi loader. Khi ngân sách dùng chung của L-4 có rồi thì pump phải
evict theo tổng chung, không theo từng type.

#### L-4 · Ngân sách bộ nhớ tính theo từng `Type` → trần thật = `MaxCacheSizeBytes` × số type — `high`

`TieredAssetLoader.cs:54-63` — mỗi Type một cache, tất cả nhận **cùng một** object config:

```csharp
cache = new TieredCache<T>(_config);   // :59
```

Mỗi cache đếm và gác độc lập (`TieredCache.cs:63`, `:66-72`). `MaxCacheSizeBytes` mặc định 100MB
(`TieredCacheConfig.cs:14`, `:76`). Load Texture2D + GameObject + AudioClip + Mesh + Sprite +
ScriptableObject là trần 600MB, mà mỗi cache tự báo mình đang ở 16% và không bao giờ evict.

`GetCombinedStats` (`:321-351`) vừa xác nhận trần đó có thật vừa báo cáo sai nó:

```csharp
var combined = new TieredCacheStats { MaxSizeBytes = _config.MaxCacheSizeBytes };  // :325 — MỘT ngân sách
combined.TotalSizeBytes += stats.TotalSizeBytes;                                    // :339 — TỔNG của tất cả
```

**Hỏng ra sao:** một tựa mobile cấu hình `TieredCacheConfig.Mobile` (50MB,
`TieredCacheConfig.cs:96`) tin mình mua trần 50MB, thực tế mua 50MB × số type nó tình cờ load — một
con số không ai chọn và thay đổi theo content. Nửa khó chịu hơn là `UsageRatio` (`TieredCache.cs:385`)
= `TotalSizeBytes / MaxSizeBytes`, nên combined stats báo ví dụ **480%** trong khi từng cache nằm dưới
`EvictionTriggerRatio` 90% và không evict gì. Người bảo trì nhìn 480%-mà-không-evict sẽ đi truy code
eviction, tìm thấy một bug thật khác ở đó (L-3), và **vẫn không tìm ra bug này**.

**Sửa (W2-07):** một ngân sách dùng chung cho mọi `TieredCache<T>` trong một loader. Cụ thể: nhấc
phần kế toán ra khỏi cache per-type — `internal sealed class CacheBudget { long _current; long _max;
bool TryAdmit(long); void Give(long); }`, `TieredAssetLoader` tạo một lần và truyền cho mọi
`TieredCache<T>` cạnh `_config`; `_currentCacheSize` thành read-through; cổng ở `:66-72` đọc tổng
chung. Khi đó eviction phải evict được sang cache anh em — **đúng bằng cái interface không generic
mà L-8 cần**, nên làm chung một lượt, đừng tách.

#### L-5 · `EstimateAssetSize` trả số bịa — `high` · đo bằng `unity-performance-optimizer`, áp bằng `unity-gameplay-programmer`

`TieredAssetLoader.cs:403-417`

```csharp
Texture2D texture => texture.width * texture.height * 4,   // :410 — RGBA thô, không mip, không format
AudioClip audio   => (long)(audio.samples * audio.channels * 2),
Mesh mesh         => mesh.vertexCount * 32,
GameObject go     => 4096,                                  // :413 — mọi prefab, bất kể nội dung
ScriptableObject  => 1024,
_                 => 1024
```

Đây là **producer duy nhất** của `estimatedSize` truyền vào `cache.Set(...)` (`:153`, `:252`), thành
`CacheEntry.EstimatedSize` (`CacheEntry.cs:40`, `:56`), rồi lái `_currentCacheSize`, cổng eviction,
mục tiêu eviction và mọi con số byte trong `TieredCacheStats`.

**Hai chiều sai, đều lớn:** một texture 2048×2048 ASTC 6x6 thật sự chiếm ~0.9MB, ở đây báo 16MB —
sai 18 lần, nên vài texture là đủ chạm cổng 90% của ngân sách mobile 50MB và cache thrash trong khi
RAM thật gần như trống; ngược lại mip chain (+33%) và `TextureFormat` bị bỏ qua hoàn toàn nên RGBA32
có mip lại bị đếm thiếu. Prefab phẳng 4096 bất kể nội dung — một prefab kéo theo mesh 40MB và một
prefab rỗng không phân biệt được, mà prefab là payload chính của Addressables ở hầu hết project.

**Không phải bug, ghi để người sau đừng "sửa":** `texture.width * texture.height * 4` là
`int×int×int` rồi mới nới sang `long`, nhưng cạnh tối đa của Unity là 16384 → 16384²×4 =
1.073.741.824 < `int.MaxValue`, không tràn. Vẫn nên viết `(long)texture.width * …` cho sạch.

**Sửa (W2-07):** `UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(Object)` đo tại lúc load —
đúng hướng vì nó tính cả format, mip và biểu diễn thật. **Một cảnh báo phải kiểm chứ không được giả
định:** `GetRuntimeMemorySizeLong` được ghi nhận là trả **0** trong non-development build trên một số
platform. Nếu đúng thì release build lặng lẽ nhận `estimatedSize == 0` cho mọi thứ, `_currentCacheSize`
đứng 0, eviction không bao giờ chạy, cache phình vô hạn — **tệ hơn hẳn** số sai-nhưng-khác-0 hiện tại.
Người implement phải (a) xác nhận hành vi release-build trên platform đích, (b) nếu nó trả 0 thì giữ
heuristic làm fallback sau guard `size <= 0`, đừng tin profiler vô điều kiện. W4-07 (ingest
`Library/com.unity.addressables/buildReports`) mới là đáp án cuối; W2-07 là bản tạm.

#### L-6 · Hai chỗ `if (IsValid) Retain()` — xem **C-6**

`TieredAssetLoader.cs:116` và `:220`. Không tách khỏi C-2 được, nên đã viết chung ở nhóm caches.
Đọc thêm §3.3(a) về việc **đừng** brief nó là P0.

#### L-7 · `TieredAssetLoader` không có method release nào, và là một fork thiếu gần hết `AssetLoader` — `high` · plan bằng `unity-tech-lead-orchestrator`

Toàn bộ bề mặt public của `TieredAssetLoader` (434 dòng): `LoadAssetAsync<T>(string)` `:87`,
`LoadAssetAsync<T>(AssetReference)` `:191`, `PinAsset<T>` `:288`, `UnpinAsset<T>` `:298`,
`GetCacheStats<T>` `:308`, `GetCombinedStats` `:321`, `EvaluateTiers` `:356`, `ForceEviction` `:368`,
`ClearCache` `:380`, `Dispose` `:423`.

**Không có `ReleaseAsset`, không có `ReleaseInstance`, không có bất kỳ đường release theo address
nào.** Cách duy nhất trả lại thứ gì là bỏ nguyên cache.

Thiếu so với `AssetLoader`: `LoadAssetsByLabelAsync` (`:715`/`:717`), ba biến thể `*Safe`/`LoadResult`
(`:826`/`:828`, `:1001`/`:1003`, `:1163`/`:1165`), `InstantiateAsync` ×2 (`:1340`/`:1342`,
`:1386`/`:1388`), `ReleaseInstance` (`:1448`), `DownloadDependenciesAsync` (`:1509`/`:1511`),
`GetDownloadSizeAsync` (`:1568`/`:1570`), `ReleaseAsset` (`:1811`), `GetCacheStats` (`:1824`).

UniTask: `grep -c UNITASK_PRESENT` = **0** (so với 11 ở `AssetLoader.cs`, 4 ở `MonitoredAssetLoader.cs`).
Vi phạm invariant 3, và **compile gate không thấy được** — lỗi ở đây là ngược với compile error: nó
compile ngon trong project có UniTask rồi trả `Task` trong khi cả package trả `UniTask`, nên
`await Advanced.CreateTieredLoader(…).LoadAssetAsync<T>(…)` alloc và đi vòng qua sync context đúng
trong những project cài UniTask để tránh chuyện đó. W3-05 đã gọi tên file này.

**Hỏng ra sao:** caller load 200MB texture qua loader này và muốn lấy lại 190MB có đúng một cần gạt —
`ClearCache()` — hôm nay release **không gì** (L-1) và sau khi sửa L-1 sẽ release **tất cả**. Không
có ở giữa. `PinAsset`/`UnpinAsset` diễn đạt được "giữ cái này" mà không có vế "tôi xong với cái này".

**Sửa — ĐỪNG vá từng feature cho bằng `AssetLoader`.** Làm thế là nhân đôi một fork vốn đã là 434
dòng logic load trùng lặp mà **không có** guard nào của Wave 1 (không single-flight, không
`StillAliveAfterAwait`, không re-check thread sau await, không `AssetCacheKey`). W4-05 đã chốt:
*"xoá `TieredAssetLoader` như một fork — tiering thành config của loader duy nhất"*. Đường tôn trọng
invariant 6:

1. Thêm tiering thành config opt-in trên `AssetLoader` (nó đã có cache, ledger và single-flight map
   mà tiering cần).
2. `[Obsolete]` `TieredAssetLoader` + năm wrapper `Advanced.*Tiered*`/`Pin`/`Unpin`, trỏ sang config
   mới.
3. **Giữ class compile được và ĐÚNG cho tới 5.0.0** — nghĩa là C-1, L-1 và L-8 vẫn phải sửa, vì một
   class `[Obsolete]` mà rò rỉ mọi asset thì không ship được ở 4.x.

Đa bước, xuyên tầng → `unity-tech-lead-orchestrator` ra plan **trước khi viết dòng code nào**.

#### L-8 · Dispatch cache generic bằng reflection ở ba chỗ — `medium` · **nhưng nên làm TRƯỚC**

`TieredAssetLoader.cs:330`, `:360`, `:372` (vì `_tieredCaches` là `Dictionary<Type, object>`, `:25`)

```csharp
var method = cache.GetType().GetMethod("GetStatistics");        // :330
var stats  = (TieredCacheStats)method.Invoke(cache, null);      // :333 — box rồi unbox
…
var method = cache.GetType().GetMethod("ForceEvaluateTiers");   // :360
method?.Invoke(cache, null);                                     // :361 — null thì im lặng
…
var method = cache.GetType().GetMethod("ForceEviction");        // :372
method?.Invoke(cache, null);                                     // :373 — null thì im lặng
```

**Nguy cơ strip mới là finding thật, và nó nặng hơn "boxing mỗi lần gọi".**
`TieredCache<T>.GetStatistics`/`ForceEvaluateTiers`/`ForceEviction` **không có static call site nào
trong cả package** — tham chiếu duy nhất là ba string literal này. Managed code stripping của Unity
và phân tích generic-sharing của IL2CPP đều đi từ static reachability, nên không có `link.xml` giữ
lại thì đây đúng là thứ stripping sinh ra để xoá. Khi đó `GetMethod` trả null, `method?.Invoke`
không làm gì, và `EvaluateTiers()`/`ForceEviction()` **return bình thường sau khi không làm gì**.
Không exception, không log, triệu chứng duy nhất là cache ngừng evict — không phân biệt được với
L-3, trong player build, chỗ khó gắn debugger nhất.

**Sửa — xoá hẳn reflection**, không có lý do gì để nó tồn tại. Thêm interface không generic trong
`Runtime/Core/`:

```csharp
internal interface ITieredCache : IDisposable
{
    TieredCacheStats GetStatistics();
    void ForceEvaluateTiers();
    void ForceEviction();
    void ForceReleaseAll();   // cấp cho L-1 cái hook teardown có kiểu
}
```

`TieredCache<T> : IDisposable, ITieredCache` — các member đã tồn tại đúng chữ ký ở
`TieredCache.cs:289`, `:337`, `:349`, nên đây là **đổi khai báo, không phải viết lại**. Đổi
`_tieredCaches` sang `Dictionary<Type, ITieredCache>` (`:25`), ba vòng lặp thành gọi trực tiếp.

Một change này gỡ luôn: nguy cơ strip, boxing, nhánh null im lặng, **và** cấp sẵn teardown type-erased
cho L-1 lẫn tầm với cross-cache cho ngân sách chung của L-4. Đòn bẩy cao nhất trong nhóm — xếp trước.

#### L-9 · BONUS — cache key là string dựng từ `typeof(T).Name`, đúng lỗi Wave 1 đã gỡ khỏi `AssetLoader` — `medium`

`TieredAssetLoader.cs:108`, `:213`, `:291`, `:301` — bốn chỗ cùng
`string cacheKey = $"{address}_{typeof(T).Name}";`

Wave 1 giải bài này ở `AssetLoader` bằng key thật: `AssetCacheKey` (`AssetLoader.cs:1896-1923`),
`readonly struct` trên `(string Address, Type Type)` có `Equals`/`GetHashCode`.

`Type.Name` rụng namespace, nên `UnityEngine.Sprite` và một `Sprite` của project ra cùng key. Thiệt
hại thực tế hẹp hơn nghe ban đầu (dictionary đã tách theo type nên va chạm chỉ nằm **trong** từng
cache) — cụ thể là `Pin`/`Unpin` trỏ nhầm entry, và chuỗi address-kèm-tên mơ hồ trong log/stats. Chi
phí chắc chắn hơn là **alloc**: một string interpolation mỗi lần load, mỗi lần pin, mỗi lần unpin,
trên main thread, cho một key băm xong rồi vứt.

**Sửa:** dùng lại `AssetCacheKey` — nó `public`, cùng assembly. `TieredCache<T>` đổi
`Dictionary<string, CacheEntry<T>>` (`TieredCache.cs:14`) sang `Dictionary<AssetCacheKey, …>`, bốn
interpolation thành `new AssetCacheKey(address, typeof(T))`. `CacheEntry<T>.Key`
(`CacheEntry.cs:45`) đang kiểu `string` và chỉ được đọc để log (`TieredCache.cs:210`, `:218`, `:258`,
`:264`) nên đổi kiểu theo hoặc giữ `ToString()`. **`AssetCacheKey` hiện nằm trong `AssetLoader.cs`** —
đọc và dùng lại thì thoải mái; **dời nó ra file riêng dưới `Runtime/Core/` là quyết định của chủ
vùng `Runtime/Loaders/`**, không tự làm nếu vùng đang có người giữ.

#### L-10 · BONUS — `_mainThreadId` là `static` và chốt theo thread nào dựng instance đầu tiên — `medium`

`TieredAssetLoader.cs:32`, `:45-48`

```csharp
private static int? _mainThreadId;                 // :32 — static, dùng chung MỌI instance
…
if (_mainThreadId == null)                          // :45
    _mainThreadId = Thread.CurrentThread.ManagedThreadId;   // :47
```

`AssetLoader` chốt đúng cách ở `AssetLoader.cs:86-90`
(`[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` + `Volatile.Write`) và fail-open khi chưa
set (`:113-118`).

**Hỏng ra sao:** `TieredAssetLoader` dựng được từ bất cứ đâu — `Advanced.CreateTieredLoader`
(`AdvancedAPI.cs:52-55`) đưa thẳng cho caller, không đòi thread nào. Nếu lần dựng đầu tiên trong
process rơi vào worker thread (warm-up nền, continuation của Task, test fixture chạy ngoài main),
`_mainThreadId` chốt vào worker đó cho cả domain: từ đó `AssertMainThread()` (`:68-80`) **throw trên
mọi lời gọi main-thread thật** và **im lặng cho qua** lời gọi của worker đó. Check không hỏng — nó
**đảo ngược**. `static` làm chuyện đó vĩnh viễn vì `:45` chỉ ghi khi null. Domain reload xoá nó trong
editor — đúng lý do vì sao nó trông ổn khi test editor và sai trong player build.

Phụ: `int?` đọc/ghi không rào chắn = nguy cơ torn read, trong khi `AssetLoader` dùng
`Volatile`/`Interlocked.CompareExchange` (`:89`, `:101`, `:117`).

**Sửa:** copy nguyên pattern của `AssetLoader` — `private static int _mainThreadId;` +
`[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`, `IsMainThread` fail-open khi `latched == 0`
để edit-mode và lời gọi trước init không throw. Xoá hẳn latch ở `:45-48`. **Nếu L-7 quyết khai tử
class này trước thì mục này thành vô nghĩa** — xếp sau quyết định đó.

### 4.3 API / Scopes / Facade — `Runtime/API/`, `Runtime/Facade/`, `Runtime/Scopes/`, `Managers/ScopeManager.cs`

Agent mặc định: `unity-gameplay-programmer`. Ngoại lệ ghi ở từng mục.

Vùng này **Wave 1 chưa từng chạm** (mtime: `SimpleAPI`/`AdvancedAPI`/`ScopeManager`/`BaseAssetScope`/
`GlobalAssetScope`/`HybridScope` ngày 10/08; `StandardAPI`/`Assets`/`AddressablesFacade`/
`SceneAssetScope` ngày 14/08). Cảnh báo "+1000 dòng viết lại" áp cho `Runtime/Loaders` và
`Runtime/Core`, không phải ở đây. Reviewer cũng **không tìm thấy** một `if (h.IsValid) h.Retain()`
nào còn sót trong vùng này (những guard `handle != null && handle.IsValid` ở `SimpleAPI.cs:47`, `:60`
và `StandardAPI.cs:118` là phép **đọc**, không phải retain).

#### A-1 · `Facade.OnDestroy`: `EndSession()` và `_poolManager.Dispose()` vẫn nằm NGOÀI guard `_instance` — `critical`

`AddressablesFacade.cs:355-370`

```csharp
private void OnDestroy()
{
    _poolManager?.Dispose();          // :357 — ngoài guard
    _poolManager = null;              // :358
    EndSession();                     // :361 — ngoài guard
    if (_instance == this)            // :365 — guard bắt đầu Ở ĐÂY
    {
        _globalScope?.Dispose();
        _globalScope = null;
        _instance = null;
    }
}
```

Phần `GlobalAssetScope` **đã** được đưa vào guard trên nhánh này; hai phần kia thì chưa.

**Hỏng ra sao — đã truy hết đường:** `Awake` (`:57-61`) làm
`if (_instance != null && _instance != this) { Destroy(gameObject); return; }`. Facade trùng bị
destroy → `OnDestroy` chạy trên nó → nó chưa từng `Initialize()` nên `_poolManager` null (lời gọi đó
vô hại). **`EndSession()` thì không vô hại:** nó chạm `ScopeManager.Instance` — singleton toàn
process — và gọi `ClearScope("Session")`, tức là `loader.ClearCache(); loader.Dispose();
_loaders.Remove(scopeId)` (`ScopeManager.cs:89-91`). Nên **một component `AddressablesFacade` thứ hai
rơi vào một scene load additive sẽ dispose loader của session ĐANG SỐNG** — mọi asset session bị
release, mọi handle session chết — trong khi facade thật vẫn giữ `_sessionLoader` trỏ vào loader đã
dispose (`:133` set `_sessionLoader = null` trên **bản field của cái trùng**, không phải của cái thật).

**Sửa:** đưa cả `_poolManager?.Dispose(); _poolManager = null;` lẫn `EndSession();` vào trong
`if (_instance == this)`, giữ nguyên thứ tự teardown (pool → session → global scope). **Không thứ gì
ngoài guard được chạm vào static/singleton state.** Cân nhắc thêm: cho nhánh từ chối trùng ở `Awake`
set cờ `_isDuplicate` và `OnDestroy` early-return theo cờ đó — tường minh hơn là dựa vào so sánh
`_instance`.

#### A-2 · `GlobalAssetScope.Dispose()` để `_scope` khác null → `Loader` trả null tới hết process — `critical`

`GlobalAssetScope.cs:12`, `:15`, `:41`, `:56-59`

```csharp
private BaseAssetScope _scope;                       // :12
public Loaders.AssetLoader Loader => _scope?.Loader;  // :15
…
_scope = new InternalScope("Global");                 // :41 — chỗ gán DUY NHẤT, trong Awake
…
public void Dispose() { _scope?.Dispose(); }          // :56-59 — KHÔNG null _scope
```

`BaseAssetScope.Dispose` (`BaseAssetScope.cs:95-111`) kết thúc bằng `_loader?.Dispose(); _loader = null;`.

**Nặng hơn "getter lazy không dựng lại" — không có getter lazy nào cả.** Sau `Dispose()`: `_scope` là
object còn sống nhưng `_loader` null → `Loader` trả **null**; `ScopeName` vẫn trả `"Global"`,
`IsActive` trả false, nên object trông như chỉ bị tắt chứ không phải đã chết. Getter `Instance`
(`:18-30`) chỉ dựng lại khi `_instance == null`, mà `Facade.OnDestroy` (`AddressablesFacade.cs:367`)
gọi `_globalScope?.Dispose()` **mà không destroy GameObject**, nên `_instance` không bao giờ bị null
bởi đường đó — `GlobalAssetScope.OnDestroy` (`:61-68`) là chỗ duy nhất null nó.

Kết quả: **mọi** `Simple.*` (`SimpleAPI.cs:45`, `:58`, `:95`, `:104`, `:113`) và **mọi**
`Facade.GetGlobalScope().Loader` (`StandardAPI.cs:71`, `:80`, `:102`, `:112`, `:135`, `:159`, `:279`,
`:291`, `:310`, `:322`) NullReference vĩnh viễn sau lần facade teardown đầu tiên. **Sửa A-1 không sửa
được mục này** — facade tự huỷ đúng quy trình vẫn đi qua đường này.

**Sửa — cần cả hai nửa:** (a) null `_scope` sau khi dispose nó; (b) thêm getter private `Scope` dựng
lại `_scope = new InternalScope("Global"); _scope.Activate();` khi null, và route
`Loader`/`Activate`/`Deactivate`/`ScopeName` qua nó — MonoBehaviour sống sót qua `Dispose` nên getter
là cơ hội dựng lại duy nhất.

> **⚠️ QUYẾT ĐỊNH CẦN NGƯỜI, KHÔNG PHẢI AGENT.** Phương án thay thế: **thôi để Facade dispose một
> singleton toàn process** (xoá `AddressablesFacade.cs:367`) và để `GlobalAssetScope.OnDestroy` là
> owner duy nhất. Change nhỏ hơn, nhưng scope sẽ sống xuyên qua các lần facade restart — có thể đúng
> ý, có thể không. **Chốt trước khi delegate**; để implementer tự chọn thì nó sẽ chọn cái ít gõ nhất.

#### A-3 · `Simple.Destroy` gọi `Object.Destroy`, rò rỉ refcount instance của Addressables — `high`

`SimpleAPI.cs:122-127`

```csharp
public static void Destroy(GameObject instance)
{
    if (instance != null) { UnityEngine.Object.Destroy(instance); }   // :124
}
```

Trong khi `Spawn` (`:93-115`) đi qua `scope.Loader.InstantiateAsync(...)`, được track bởi
`TrackInstance` (`AssetLoader.cs:1437-1444`) và **chờ được trả qua** `ReleaseInstance`
(`AssetLoader.cs:1448-1467`, `Addressables.ReleaseInstance(instance)`).

Addressables không bao giờ biết instance đã biến mất → reference tới bundle của prefab bị giữ tới hết
process. Chính docstring của `TrackInstance` gọi tên đúng ca này (*"an instance the game destroys with
`Object.Destroy` leaves a fake-null entry here"*). Đối tác đúng **đã tồn tại và đã lộ ra ở tầng trên**
— `Assets.ReleaseInstance` (`Facade/Assets.cs:151-152`) — nên `Simple` là bề mặt duy nhất còn thiếu.

**Sửa:**
```csharp
if (!GlobalAssetScope.Instance.Loader.ReleaseInstance(instance))
    UnityEngine.Object.Destroy(instance);
```
Fallback là bắt buộc: `ReleaseInstance` trả `false` với GameObject mà Addressables không sở hữu (ví
dụ lấy từ `Simple.Pool`), và những cái đó vẫn cần destroy. Đi qua đúng loader mà `Spawn` đã dùng
(`GlobalAssetScope.Instance.Loader`, `SimpleAPI.cs:95`/`:104`/`:113`).

#### A-4 · `Simple.Load`/`TryLoad`/`Preload`/`PreloadBatch` mồ côi một reference mỗi lần gọi — `high`

`SimpleAPI.cs:43` (`Load`, thân `:46-47`), `:54` (`TryLoad`, thân `:59-63`), `:203-206` (`Preload`),
`:211-217` (`PreloadBatch`)

```csharp
var handle = await scope.Loader.LoadAssetAsync<T>(address);
return handle != null && handle.IsValid ? handle.Asset : default;   // :47
```

`Preload` là `await Load<T>(address);` — vứt kết quả. `PreloadBatch` là `await Load<object>(address);`
trong vòng foreach. **Không có một `Dispose()`/`Release()` nào trong cả `SimpleAPI.cs`.**

*(Số dòng work order cũ `43,55,196,203` → nay `43,54,203,211`; cặp Preload trôi 7-8 dòng.)*

**Hỏng ra sao:** Wave 1 làm reference sinh-kèm của caller thành thật — `CacheHandle`
(`AssetLoader.cs:304-321`) `Retain()` cho cache, nên một lần load mới để refcount ở **2**: một của
cache, một của caller. `Simple.*` vứt phần của caller đi. Refcount không bao giờ xuống dưới 2, nên
không gì ngoài `ClearCache`/`Dispose` giải phóng được nữa.

**Sửa:** vẫn trả `handle.Asset`, nhưng trả reference lại: giữ asset ra biến, `handle.Dispose()`
(**decrement**, không phải hard release — reference của cache giữ asset sống), rồi return.
`Preload`/`PreloadBatch` cũng phải `Dispose` — chúng chỉ tồn tại để hâm nóng cache.

> **⚠️ Ràng buộc chéo work order không nhắc, và nó quan trọng.** Docstring của
> `AssetLoader.ClearCache` (`AssetLoader.cs:1611-1621`) **lấy chính bug này làm lý do** cho việc nó
> `ForceRelease` vô điều kiện (*"Simple.Load and the rest of the return-the-asset API family hand back
> `handle.Asset` and keep no handle…"*), và `PARALLEL_SESSIONS.md §3` 15:24 dùng đúng lý do đó cho
> việc `EvictAddress` giữ `ForceRelease`. **Sửa `SimpleAPI` là làm tiền đề đó thành sai.** Phải xem
> lại comment và cặp `ForceRelease`/`Dispose` trong `AssetLoader` **trong cùng change**, nếu không
> lý lẽ viết trong code trở thành lời nói dối. Đây là điểm phối hợp — ghi vào
> `PARALLEL_SESSIONS.md §3`, không phải một dòng brief cho agent.

#### A-5 · `Standard.PreloadAsync` mắc đúng lỗi A-4 — và **không có trong work order** — `high`

`StandardAPI.cs:293-299`

```csharp
public static async Task PreloadAsync(params string[] addresses)
{
    var loader = Facade.GetGlobalScope().Loader;
    foreach (var address in addresses) { await loader.LoadAssetAsync<object>(address); }   // :295
}
```

`Simple.LoadAsync<T>` (`SimpleAPI.cs:77-84`) cũng thừa hưởng qua việc delegate sang `Simple.Load`.

Cùng lớp lỗi với A-4 nhưng nằm trên bề mặt `Standard` — bề mặt mà docs trỏ người dùng production
vào. Work order chỉ liệt kê `SimpleAPI.cs`, nên ai đọc nó theo nghĩa đen và sửa đúng bốn method sẽ
**bỏ sót chỗ này**. Sửa: `Dispose` handle sau mỗi await, y như `Simple.Preload`.

#### A-6 · `HybridScope`: static sống xuyên domain reload; `Deactivate()` dispose mà không null static — `high`

`HybridScope.cs:36-38`, `:249-258`, `:293-296`, `:340-348`

```csharp
private static HybridScope _globalInstance;                                   // :36
private static HybridScope _sessionInstance;                                  // :37
private static readonly Dictionary<string, HybridScope> _namedInstances = …;  // :38
…
public void Deactivate() { Dispose(); }                                       // :293-296
public void Dispose() { if (_disposed) return; …; _loader?.Dispose(); _disposed = true; }  // :340-348
public AssetLoader Loader { get { if (_disposed) throw new ObjectDisposedException(…); return _loader; } }  // :249-258
```

**Không có `[RuntimeInitializeOnLoadMethod]` nào trong cả file** — trong khi `ScopeManager.cs:170-179`
**có** `ResetOnLoad(SubsystemRegistration)` và `AssetLoader.cs:85-89` có `CaptureMainThread` cùng loại.

**(a)** Với domain reload tắt (Enter Play Mode Options — nhiều team bật mặc định),
`_globalInstance`/`_sessionInstance`/`_namedInstances` cùng mọi `AssetLoader` và handle sống mà chúng
đang giữ **sống sang Play session kế tiếp**. `ScopeManager` giải đúng bài này cách đó 40 dòng và để
lại comment giải thích, nên đây là **thiếu nhất quán**, không phải ẩn số.
**(b)** `Deactivate()` → `Dispose()` để static trỏ vào instance đã dispose. `HybridScope.Global`
(`:49-63`) chỉ dựng lại khi `_globalInstance == null`, nên `Advanced.GetGlobalScope().Loader` tiếp
theo **throw `ObjectDisposedException` vĩnh viễn**. `ClearSessionSingleton` (`:188-201`) làm đúng mẫu
(Dispose rồi `= null`) mà `Deactivate` không theo.

**Sửa:** (a) thêm `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)] ResetOnLoad()` gọi
`ClearAll()` trong try/catch rồi null hai singleton + clear `_namedInstances`; theo mẫu
`ScopeManager.cs:170-179` **kèm cả comment**. `ClearAll` (`:207-229`) đã dispose và null hai
singleton nên `ResetOnLoad` phần lớn chỉ cần delegate. (b) hoặc route `Deactivate` qua
`ClearSessionSingleton`/một `ClearGlobalSingleton` tương ứng bằng cách xét `_scopeType`/`_instanceName`,
hoặc cho getter Global/Session coi instance đã dispose là vắng mặt
(`if (_globalInstance == null || _globalInstance._disposed)`) — cách sau nhỏ hơn và sửa luôn ca
named-instance, nơi `ClearNamed` gỡ entry còn `Deactivate` thì không.

#### A-7 · Scope vẫn không đăng ký với `ScopeManager` — nhưng registry của B đã giải xong nửa invalidation — `high` · `CHANGED`

`BaseAssetScope.cs:54-62` — ctor kết thúc bằng `AssetMonitorBridge.ReportScopeRegistered(_scopeId, false);`,
một lời gọi **monitoring**. Grep `ScopeManager` trên `Runtime/`: không tham chiếu nào trong
`Runtime/Scopes/`. `ScopeManager._loaders` (`:30`) chỉ được điền bởi `GetOrCreateScope` (`:48-58`),
mà caller Runtime duy nhất là `AddressablesFacade.cs:118` và `:149`, **cả hai với
`SessionScopeId = "Session"`**.

Hệ quả cụ thể: `ClearAll()` chỉ thấy đúng entry `"Session"`, còn `ClearAllExceptGlobal()`
(`:122-125` = `ClearAllExcept("Global")`) **bảo vệ một key không có chỗ nào tạo ra** — grep
`GetOrCreateScope("Global")` toàn package: **0 hit**. Tức là nó **giống hệt `ClearAll()` trong mọi
trạng thái với tới được**, trong khi tên hứa rằng Global đang được chừa ra.

> **PHẦN ĐÃ LỖI THỜI CỦA WORK ORDER — ĐỌC KỸ.** Tiền đề *"session B cần một registry `AssetLoader`"*
> **KHÔNG CÒN ĐÚNG.** `Runtime/Loaders/AssetLoaderRegistry.cs` đã tồn tại (165 dòng, `internal static`,
> WeakReference, đã commit `9a19790`), đã wire đầy đủ: `AssetLoader.cs:107` đăng ký trong ctor,
> `:1873` gỡ trong `Dispose`, `CatalogService.cs:436` tiêu thụ qua `InvalidateAll(keys)`. Docstring
> của nó (`:21-26`) có sẵn bảng 6 dòng liệt kê đúng năm nguồn trước đây không với tới được.
> **Đừng giao cho ai task dựng lại nó.** Hai bài toán giờ đã tách: catalog invalidation **xong**,
> scope lifecycle **chưa**.

**Sửa — cần quyết định trước:**
- **(i)** `BaseAssetScope` đăng ký loader với `ScopeManager` theo `ScopeId` (ctor) và gỡ ở `Dispose` →
  `ClearAll`/`ClearAllExceptGlobal` đúng như tên, **và** `Standard.ClearCache` (A-10) với tới được
  Global/Scene/Hierarchy.
- **(ii)** Chấp nhận `ScopeManager` chỉ sở hữu thứ nó tạo, rồi đổi tên/`[Obsolete]`
  `ClearAllExceptGlobal` vì phần bảo vệ Global của nó không implement được như đang viết.

Nếu chọn (i): nó tạo ra một registry **strong-reference** bên cạnh registry **weak** của B — phải làm
cho đường dispose của `ScopeManager` và `Unregister` của registry thống nhất; và lưu ý `InternalScope`
của `GlobalAssetScope` mang id `"Global"` (`GlobalAssetScope.cs:41` → `BaseAssetScope.cs:41`), nên
đăng ký nó sẽ **lần đầu tiên** làm key `"Global"` có thật. **Lấy quyết định trước khi delegate.**

#### A-8 · `LoadScene<T>` load một asset chứ không phải scene, và bind vào scene ACTIVE chứ không phải scene của caller — `medium`

`Assets.cs:70-76` → `Manager.LoadSceneAsync<T>` · `StandardAPI.cs:61-64` → `Facade.LoadSceneAsync<T>`
· `AddressablesFacade.cs:171-178`:
`var scope = GetOrCreateSceneScope(); return await scope.Loader.LoadAssetAsync<T>(address);` ·
`GetOrCreateSceneScope` (`:161-165`) → `SceneAssetScope.GetOrCreate()` → `SceneAssetScope.cs:172`
`=> GetOrCreate(SceneManager.GetActiveScene());`

Grep `Addressables.LoadSceneAsync` / `SceneInstance` / `LoadSceneMode` toàn package: **0 hit** — package
**không có** khả năng load scene nào cả. Tên thì cách `Addressables.LoadSceneAsync` của Unity đúng một
ký tự, nên không ai phân biệt được bằng cách đi tìm bản thật. Sample của chính package chứng minh sự
nhầm lẫn: `Samples~/ApiExamples/AddressablesExamples.cs:224`
`var task = Assets.LoadScene<Material>("Scene/SpecialMaterial");`.

Nửa binding cũng đúng: một MonoBehaviour trong scene load additive gọi `Assets.LoadScene<T>` nhận
scope của **scene nào đang active**, nên asset của nó bị release khi một scene nó không hề sống trong
đó unload (`SceneAssetScope.OnSceneUnloaded`, `:68-74`). Overload an toàn **đã tồn tại** —
`SceneAssetScope.GetOrCreate(Scene)` (`:134-167`), docstring ghi *"Cross-scene safe — unlike the
parameterless overload, this never returns a scope from a different loaded scene"* — facade vẫn gọi
cái không an toàn.

**Sửa — hai phần tách được:** (a) đặt tên: thêm `LoadIntoSceneScope<T>` trên `Standard`/`Assets`,
`[Obsolete]` `LoadScene<T>` trỏ sang (invariant 6 — giữ tới 5.0.0). (b) binding: thêm overload
`LoadSceneAsync<T>(string address, Scene scene)` trên Facade gọi `SceneAssetScope.GetOrCreate(scene)`,
và bản không tham số phải ghi to trong doc rằng nó dùng scene active. Mặc định "scene của caller"
nghe hay nhưng **không implement được từ một static API** — không có caller GameObject — nên đáp án
trung thực là overload tường minh + docstring, không phải một mặc định thông minh. Sửa luôn
`Samples~/ApiExamples/AddressablesExamples.cs:224` trong cùng change, nếu không sample vẫn dạy sai mô
hình tư duy.

> **⚠️ BẪY:** fix này chạm `SceneAssetScope.GetOrCreate` — đúng method chứa khối
> `#pragma warning disable CS0618` ở `:159-161` + comment `:136-158`. **Prompt gửi agent phải ghi rõ:
> để nguyên byte-for-byte.** Xem §2.5.

#### A-9 · `Simple.Release<T>` là một `Debug.Log` no-op — `medium`

`SimpleAPI.cs:180-186`

```csharp
public static void Release<T>(T asset)
{
    // In Simple API, we don't expose handles directly
    Debug.Log($"[Simple.Release] Release hint for asset of type {typeof(T).Name}");   // :184
}
```

Docstring của class (`:19`) quảng cáo nó là đối tác của `Load`: `Simple.Release(sprite);`. Nó log ở
mức Debug **trong build shipping** và giải phóng con số không. Cộng với A-4, đây là toàn bộ lý do bộ
nhớ của Simple API không có trần: API ghi tài liệu cho một đường release không tồn tại.

**Sửa:** hoặc wire thật (cần một map ngược asset→handle trên `AssetLoader`, hôm nay **chưa có**),
hoặc `[Obsolete]` theo invariant 6 và trỏ sang `Simple.ClearAll`/`Standard.LoadGlobal`. **Không xoá
lặng lẽ** — 4.x giữ API tới 5.0.0. Dòng log phải đi trong mọi phương án; đã có sẵn cơ chế gate
`DebugSettings.IsVerbose` (`AssetLoader.cs:1879`).

#### A-10 · `Standard.ClearCache(scopeName)` là stub log-rồi-return — `medium`

`StandardAPI.cs:267-273`

```csharp
public static void ClearCache(string scopeName)
{
    // Implementation depends on scope manager
    Debug.Log($"[Standard] Clearing cache for scope: {scopeName}");   // :271
}
```

Im lặng không làm gì — tệ hơn throw, vì caller đang clear một scope dưới memory pressure nhận về một
dòng log trông như thành công. Comment *"Implementation depends on scope manager"* giờ **sai**:
`ScopeManager.GetScope(scopeId)` đã tồn tại (`ScopeManager.cs:74-78`) và trả về `AssetLoader`.

**Sửa:** `ScopeManager.Instance.GetScope(scopeName)?.ClearCache();` — chú ý `ClearCache` **chứ không
phải** `ClearScope`: `ClearScope` (`ScopeManager.cs:83-100`) còn `Dispose` và gỡ entry, đó là ngữ
nghĩa EndSession. **Caveat phải ghi vào docstring chứ đừng ám chỉ phủ hết:** nó chỉ với tới scope đã
đăng ký với `ScopeManager`, hôm nay đúng một cái là `"Session"` (xem A-7).

#### A-11 · `Simple.IsLoaded(address)` không bao giờ đọc tham số của nó — `medium`

`SimpleAPI.cs:227-232`

```csharp
public static bool IsLoaded(string address)
{
    var (_, activeHandles) = Facade.GetGlobalScope().Loader.GetCacheStats();
    return activeHandles > 0; // Simplified check                    // :229-230
}
```

Trả `true` cho **mọi** address ngay khi bất kỳ asset nào được load ở global scope. Caller viết
`if (!Simple.IsLoaded(a)) await Simple.Load(a);` sẽ bỏ qua lần load rồi null-deref.

**Sửa — cần API mới ở tầng loader:** `AssetLoader` **không có** `IsCached`/`IsLoaded` public nào (grep
`public bool Is` chỉ ra `IsValid`/`IsAlive` trên các handle type, `:1939`/`:2008`). Cache key theo
`AssetCacheKey(address, Type)`, nên chữ ký trung thực là **`Simple.IsLoaded<T>(string address)`** —
một phép kiểm chỉ-theo-address không trả lời được câu hỏi mà cache đang key. Việc này **xuyên vùng**:
nửa loader thuộc `Runtime/Loaders/`, nửa API thuộc đây. Xếp sau khi `Runtime/Loaders/` được trả ở §2.

#### A-12 · Sáu entry point dẫn tới bốn storage khác nhau; hai cái trùng TÊN — `medium` · plan bằng `unity-tech-lead-orchestrator`

- **A** — loader `InternalScope` của `GlobalAssetScope`, id `"Global"`. Được dùng bởi:
  `Simple.Load/TryLoad/Spawn/Preload` (`SimpleAPI.cs:45,58,95,104,113`),
  `Simple.IsLoaded/GetStats/ClearAll` (`:229,238,193`), **mọi** `Standard.*` global
  (`StandardAPI.cs:71,80,102,112,135,159,279,291,310,322`), `Facade.LoadGlobalAsync`
  (`AddressablesFacade.cs:92`), `Assets.Load/Load(progress)/ReleaseInstance`
  (`Assets.cs:39,51,152`), và pool manager (`AddressablesFacade.cs:75`
  `new AddressablePoolManager(_globalScope.Loader, …)` → `Simple.Pool/Recycle`,
  `Standard.Spawn/Despawn`, `Assets.Spawn/Despawn` dùng chung A). **Nhóm này thật sự chia sẻ.**
- **B** — entry `"Session"` của `ScopeManager` (`AddressablesFacade.cs:118,149,331,130,348` + các
  `Standard.*Session*`, `Assets.*Session*`). Nhất quán.
- **C** — loader của `SceneAssetScope`, **một cái cho mỗi scene handle**, id
  `Scene-{name}#h{handle}` (`SceneAssetScope.cs:55`). `Standard.LoadScene` + `Assets.LoadScene` dùng
  chung C — nhưng **C nào thì tuỳ scene đang active lúc gọi** (xem A-8).
- **D** — loader riêng của `HybridScope` (`HybridScope.cs:243`), **chỉ** với tới qua
  `Advanced.GetGlobalScope()/GetSessionScope()/GetNamedScope` (`AdvancedAPI.cs:64-83`). **Không chia
  sẻ gì với A hay B.**
- **Vô chủ** — `Advanced.CreateLoader/CreateThreadSafeLoader/CreateTieredLoader`
  (`AdvancedAPI.cs:36,44,52`) mỗi lời gọi trả một `AssetLoader` mới cho caller.

**Cạnh sắc hẹp hơn nhưng tệ hơn "sáu entry point chồng nhau":** `Advanced.GetGlobalScope()` và
`Facade.GetGlobalScope()` là **hai method CÙNG TÊN trả về HAI storage khác nhau** (A vs D), và cả hai
tự khai là `"Global"` với monitoring — `BaseAssetScope.cs:61` báo `"Global"` cho A,
`HybridScope.cs:58` báo `"Global"` cho D, `HybridScope.cs:77` báo `"Session"` cho D còn
`ScopeManager.cs:57` báo `"Session"` cho B. Dashboard hiển thị **một kênh cho mỗi tên** trong khi tồn
tại hai cache độc lập, và người đọc `Advanced.GetGlobalScope().Loader.ClearCache()` tin rằng mình vừa
clear thứ mà `Simple.Load` đã điền. Không phải.

**Sửa — tối thiểu, không gộp storage trong bản 4.x** (gộp là breaking change với người đang dùng
`Advanced`): (1) đổi tên `Advanced.GetGlobalScope`/`GetSessionScope` →
`GetHybridGlobalScope`/`GetHybridSessionScope`, `[Obsolete]` tên cũ, để va chạm nhìn thấy được ngay
tại call site; (2) tách id monitoring — `HybridScope` báo `"Hybrid:Global"`/`"Hybrid:Session"`;
(3) ghi bản đồ storage vào docstring của các class.

> **⚠️ Phương án chiến lược, đưa user quyết trước khi viết code:** `HybridScope` (D) là **cơ chế scope
> thứ ba** bên cạnh `ScopeManager` (B) và `BaseAssetScope` (A/C), **không có năng lực riêng nào**,
> không có reset hook (A-6), **0 chỗ dựng ngoài `AdvancedAPI`**, và va chạm tên monitoring với cả hai
> storage thật. Khai tử nó để chuyển sang `ScopeManager` **xoá hẳn một storage** thay vì dán lại
> nhãn — và làm A-6 thành không cần thiết. Đây là quyết định sản phẩm, không phải của implementer.

#### A-13 · `Simple.GetStats()` đếm đúp mọi asset đã cache — `low`

`SimpleAPI.cs:236-241`

```csharp
var (cached, active) = Facade.GetGlobalScope().Loader.GetCacheStats();
return (cached + active, 0); // Simplified                             // :239-240
```

`GetCacheStats` (`AssetLoader.cs:1824-1829`) trả `(_assetCache.Count, _activeHandles.Count)`, và
`CacheHandle` (`:319-321`) làm `_assetCache[key] = handle; TrackHandle(handle);` với `TrackHandle`
(`:1332`) = `_activeHandles.Add(handle)`. **Mọi handle đã cache đều nằm trong CẢ HAI collection**, nên
`cached + active` đếm mỗi asset cache hai lần, cộng thêm handle load-theo-label (chỉ nằm trong
`_activeHandles`). Phần tử thứ hai của tuple hard-code `0` trong khi một pool manager có số thật chỉ
cách một lời gọi (`Facade.GetPoolManager()`).

**Sửa:** trả `(active, pooled)` với `active = _activeHandles.Count` (tập cha), hoặc lộ ra một số đã
khử trùng lặp từ loader. Với `pooledObjects`, cộng `AddressablePoolManager.GetPoolStats` trên các pool
đã biết thay vì ship một literal `0`.

### 4.4 Pooling — `Runtime/Pooling/`

Agent mặc định: `unity-gameplay-programmer`; P-5 và các mục thiết kế mở bằng
`unity-tech-lead-orchestrator`.

**Provenance:** `git log -- Runtime/Pooling/` dừng ở `3a3bd5c` (v3.5.1). Wave 1 (`3c6017e`), các
commit CDN (`46bf74c`, `9a19790`) và lượt UniTask (`4d6a6ca`) **không chạm** `Runtime/Pooling/` hay
`Runtime/Configs/PoolConfiguration.cs`. Code ở đây đúng như v3.5.1.

#### P-1 · Auto-create pool block main thread trên một lần load Addressables — `critical`

`AddressablePoolManager.cs:282-289`

```csharp
// Create pool synchronously (blocking).
// GetAwaiter().GetResult() works for both Task<bool> and UniTask<bool> awaiters,
// so the call site stays valid regardless of whether UNITASK_PRESENT is set.
var task = _autoCreateDefaultConfig != null
    ? CreateDynamicPoolAsync(address, _autoCreateDefaultConfig, preloadCount: 0)
    : CreatePoolAsync(address, preloadCount: 0, maxSize: 50);

bool created = task.GetAwaiter().GetResult();          // :289
```

Cả hai method đều tới `await _loader.LoadAssetAsync<GameObject>(address)` (`:125`, `:216`) → `await
operation.Task` trên một `AsyncOperationHandle` (`AssetLoader.cs:529-530`), thứ chỉ hoàn tất khi
**main thread bơm `ResourceManager`**.

**Đường tới từ dòng one-liner cho người mới trong README, liền mạch:**
`README.md:52` `var enemy = Simple.Pool("Enemies/Orc");` → `SimpleAPI.cs:135-146`
(`if (!poolManager.IsAutoCreateEnabled) poolManager.EnableAutoCreatePools();` rồi `Spawn`) →
`AddressablePoolManager.cs:332-335` → `:275-289`. **Lần gọi `Simple.Pool` đầu tiên cho bất kỳ address
nào đều đi đường này**, vì `Simple.Pool` không tạo pool bằng cách nào khác.

**Hỏng khác nhau theo nhánh compile — và đây là chỗ phải cẩn thận:**

| Nhánh | Hành vi |
|---|---|
| `UNITASK_PRESENT` (**cấu hình đang ship** — `Packages/manifest.json:3` khai `com.cysharp.unitask`) | trả `UniTask<bool>`; UniTask **không có** blocking wait — `GetResult` trên source chưa xong **throw `InvalidOperationException` ngay**, lần gọi đầu, mọi lần |
| không UniTask | `Task.GetAwaiter().GetResult()` **block main thread** chờ một operation cần chính main thread để tiến → **treo cứng, không log gì** |

Cả hai đều chí mạng, nhưng **fix verify trên một nhánh không chứng minh gì cho nhánh kia**. Comment ở
`:283-284` đúng về việc call site **compile** được và sai về việc nó **chạy** được trên cả hai — xoá
comment cùng với code để nó thôi trấn an người đọc sau.

**Sửa:** **xoá hẳn đường auto-create đồng bộ.** `Spawn()` là API đồng bộ và không thể trung thực chờ
một lần load asset, nên làm cho hình dạng khớp thực tế: pool miss + auto-create bật → khởi động lần
create async, ghi address vào danh sách pending, **trả về cho lần gọi này** kèm đúng một dòng log rõ
ràng, rồi phục vụ instance thật khi create xong. Đường trung thực phải được ghi tài liệu: một
`SpawnAsync(address, …)` **mang chữ ký kép `#if UNITASK_PRESENT`** (invariant 3), và viết lại
`README.md:52` + `:396` thành await tạo pool trước lần Spawn đầu.
**Không trả sentinel** gộp "đang load" với "load hỏng" (invariant 4) — cần phân biệt thì đi qua
`LoadResult`/`CdnResult` sẵn có. Chặn lần `Spawn` thứ hai re-enter việc create cho address đang bay.
**Verify trên CẢ HAI nhánh:** chế độ ma trận UniTask của gate (§6) phủ nửa đang throw, build
non-UniTask phủ nửa đang treo.

#### P-2 · Instance pool nằm trong scene active còn pool sống trên `DontDestroyOnLoad` — `high`

`AddressablePoolManager.cs:484-489`

```csharp
private GameObject CreateInstance(GameObject prefab, Transform parent)
{
    var instance = UnityEngine.Object.Instantiate(prefab, parent);   // :486
    instance.SetActive(false);
    return instance;
}
```

`parent` là `poolRoot` truyền xuống, và nó **mặc định null ở mọi tầng**: `:93`, `:99`, `:194`
(`Transform poolRoot = null`), dùng ở `:136`, `:157`, `:227`. Đường auto-create **không bao giờ**
cấp một cái (`:286-287`). `Instantiate` với parent null đặt object vào **scene đang active**.

Pool thì sống lâu hơn scene đó: `AddressablesFacade.cs:47-50` và `:64` gọi `DontDestroyOnLoad` cho
GameObject facade, và `:75` dựng pool manager làm field thường trên nó.

Sau `LoadSceneMode.Single`, các GameObject trong pool bị destroy nhưng **reference C# vẫn nằm trong
free list**. `Get()` kế tiếp trả về một object đã destroy, và callback `onGet` của chính manager
deref nó **trước khi** caller kịp kiểm: `:137` và `:228` `onGet: (obj) => obj.SetActive(true)` →
`MissingReferenceException` ném ra **từ trong ruột pool**. Guard `if (instance != null)` ở `:312` nằm
sau chỗ throw nên **không bao giờ chạy** — nó làm code trông như đã phòng thủ đúng ca này và sẽ dẫn
người debug đi sai hướng.

**Sửa:** cho manager một root của riêng nó — tạo lười một GameObject `[Pools]`, `DontDestroyOnLoad`
nó, parent các transform con theo address dưới đó, và dùng nó **bất cứ khi nào caller truyền
`poolRoot == null`**, để instance chia sẻ lifetime với pool **theo cấu trúc** chứ không nhờ caller
nhớ. Destroy root đó trong `Dispose()`. Giữ tham số `poolRoot` cho ai muốn tự đặt chỗ, nhưng ghi rõ
là một `poolRoot` thuộc scene sẽ tái tạo lại đúng vấn đề này. Độc lập với việc trên — và nên làm dù
đã có root — **làm `Get()` phòng thủ**: gặp instance null-equal (đã bị Unity destroy) thì bỏ nó, gỡ
khỏi tracking, tạo cái thay thế, **không** gọi `onGet` lên nó. Verify trong **PlayMode** qua ranh
giới `LoadSceneMode.Single`; EditMode không tái hiện được việc destroy.

#### P-3 · `preloadCount` tạo đúng MỘT instance bất kể số lượng, rồi log con số đã yêu cầu như thể thành công — `high`

`AddressablePoolManager.cs:249-257` (và thân y hệt trong `CreateDynamicPoolAsync` ở `:173-180`)

```csharp
Debug.Log($"[PoolManager] Preloading {preloadCount} instances for {address}");   // :251
for (int i = 0; i < preloadCount; i++)
{
    var instance = pool.Get();       // :254
    pool.Release(instance);          // :255
}
```

Vòng 0 tạo instance qua `createFunc` rồi trả ngay về pool. Vòng 1 `Get()` trên một pool mà free list
vừa có đúng cái đó → `ObjectPool.Get` **pop từ inactive list thay vì gọi `createFunc`**. Mọi vòng sau
tái sử dụng cùng một object. `preloadCount = 50` → **một** GameObject, năm mươi vòng khứ hồi, và dòng
log `:251` khẳng định năm mươi.

Cả bề mặt config đều hứa hành vi mà vòng lặp không làm được: `PoolConfiguration.cs:24-26` expose
`preloadCount` với `Range(0,100)` và tooltip *"Number of instances to preload on pool creation"*,
`Validate` (`:140-143`) còn kiểm `preloadCount <= maxSize`.

**Hỏng ra sao:** preload tồn tại để đẩy chi phí instantiate ra khỏi frame đầu cần tới object. Pool
preload một cái rồi báo năm mươi giao lại đúng cú giật mà tính năng này được mua để tránh, vào đúng
thời điểm tệ nhất (đợt spawn đầu tiên), trong khi log nói mọi thứ ổn. Thiếu-hụt-im-lặng cộng
log-xác-nhận khó chẩn đoán hơn hỏng thẳng, vì chỗ đầu tiên người ta nhìn lại bảo là đã chạy.

**Sửa:** `Get()` đủ N **trước**, rồi `Release()` cả N — gom vào list tạm ở vòng một, release ở vòng
hai, để mỗi `Get()` đều trượt free list và gọi `createFunc`. Với đường `DynamicPool` (`:173-180`) thì
**nên đi qua primitive pre-populate mà P-4 dựng ra** thay vì cặp Get/Release, để preload không làm
nhiễu tracking peak-active vốn lái auto-resize — 50 lời gọi `Get()` sẽ đẩy `_peakActiveCount` lên 50
(`DynamicPool.cs:139-142`) và bóp méo quyết định shrink đầu tiên. Chuyển log xuống **sau** vòng lặp
và báo số **thật sự tạo được**. Regression test: pool với `preloadCount 5` →
`GetStats().pooledCount == 5` và `activeCount == 0`.

#### P-4 · `DynamicPool` phá kế toán của inner pool theo **cả hai chiều**, đẩy `activeCount` xuống âm và ra tới API public — `high`

`DynamicPool.cs:249-257` (`GrowPool` — release thứ chưa bao giờ `Get()`) và `:275-284` (`ShrinkPool` —
`Get()` rồi destroy, không bao giờ release)

```csharp
var item = _createFunc();
if (item != null) { _innerPool.Release(item); }      // :256

// Get from pool and destroy instead of releasing back
var item = _innerPool.Get();                          // :280
if (item != null) { _onDestroy?.Invoke(item); }       // :283
```

Với `UnityPoolAdapter` (mặc định — `AddressablesFacade.cs:75` truyền `new UnityPoolFactory()`), stats
lấy thẳng từ `ObjectPool`: `UnityPoolAdapter.cs:67-71` `return (_unityPool.CountActive,
_unityPool.CountInactive);`. `ObjectPool` suy `CountActive = CountAll - CountInactive` và chỉ tăng
`CountAll` **bên trong `Get()`**. Nên `GrowPool` phình inactive list mà không đụng `CountAll` →
`CountActive` **âm**; `ShrinkPool` làm ngược lại → `CountActive` **phồng vĩnh viễn** một đơn vị mỗi
slot bị shrink.

Cả hai ra thẳng tới caller: `DynamicPool.GetStats` (`:109-112`) passthrough →
`AddressablePoolManager.GetPoolStats` (`:367-375`) → `StandardAPI.cs:211-215` `Standard.GetPoolStats`.
`GetDynamicStats` (`:297-311`) nuôi cùng những con số đó vào `DynamicPoolStats.ActiveCount`, mà
`UsageRatio` (`:359`) lái quyết định resize kế tiếp.

Kích hoạt tự động: `CheckForGrowth` (`:158`) và `CheckForShrinkage` (`:216`) chạy trên **mọi**
Get/Release khi `EnableAutoResize` — mặc định `true` (`DynamicPoolConfig.cs:59`, và cả ba preset
Default/Conservative/Aggressive đều bật).

**Hỏng ra sao:** số hỏng **quay lại nuôi chính bộ điều khiển sinh ra chúng**. `usageRatio` (`:145`,
`:184`) tính từ `activeCount`, nên một khi `activeCount` âm thì nhánh grow (`:148`) **không bao giờ
kích hoạt lại** (tỷ lệ âm luôn dưới `GrowThreshold`) còn nhánh shrink (`:187`) kích hoạt vô điều
kiện — kéo pool về `MinSize` **đúng lúc tải cao nhất**. Auto-resize tự lộn ngược. Và vì
`CheckForGrowth` chạy mỗi `Get()`, hỏng bắt đầu từ sự kiện grow đầu tiên chứ không phải ở góc hiếm.

**Nghiêm trọng hơn với `CustomPoolAdapter` — và đây là divergence thứ tư mà work order không có:**
`GrowPool` gọi `_innerPool.Release(item)` trên instance chưa từng qua `Get()`, nên nó đâm vào guard
`!_activeObjects.Remove(obj)` ở `CustomPoolAdapter.cs:80`, **log warning rồi `return` TRƯỚC cả push
lẫn `_onDestroy`** — instance không vào pool, cũng không bị destroy. **Mỗi slot auto-grow dưới
`CustomPoolFactory` rò rỉ một GameObject.** Một nguyên nhân gốc, hai triệu chứng trông không liên
quan.

**Sửa:** thôi diễn đạt "pre-populate" và "evict" bằng cặp Get/Release — chúng là hai thao tác khác
nhau và inner pool không có cách nào phân biệt. Thêm thành member tường minh của `IObjectPool<T>`
(`Runtime/Pooling/IObjectPool.cs`), ví dụ `void Prewarm(int count)` và `int TrimExcess(int count)`,
implement theo từng adapter để mỗi cái tự giữ counter đúng: `CustomPoolAdapter` push/pop `_pool` trực
tiếp không đụng `_activeObjects`; `UnityPoolAdapter` không có API seed `ObjectPool` mà không qua
`Get()`, nên cần một cặp Get-rồi-Release thực thi như **một** thao tác prewarm nguyên tử (đúng, vì
đường đó `CountAll` **có** tăng) hoặc một counter shim riêng. Rồi `GrowPool` gọi `Prewarm`,
`ShrinkPool` gọi `TrimExcess`. **Lưu ý đây là mở rộng một interface public** — invariant 6 nghĩa là
implementer `IObjectPool` bên thứ ba không được vỡ: cấp default implementation hoặc đưa phần thêm
sang một interface dẫn xuất. Regression test khẳng định `GetStats().activeCount >= 0` sau
`ResizeTo` lên rồi xuống, **trên cả hai adapter** — một test đó bắt luôn cả P-7.

#### P-5 · `ClearPool` để prefab bundle nằm lại — nhưng đơn thuốc `TryRetain` của work order giờ SAI và sẽ rò rỉ vĩnh viễn — `high` · `CHANGED`

`AddressablePoolManager.cs:167`, `:245` (lưu) · `:475-482` (trả)

```csharp
_templateHandles[address] = handle;      // :167  và :245

private void ReleaseTemplate(string address)
{
    if (_templateHandles.TryGetValue(address, out var template))
    {
        template?.Dispose();             // :479
        _templateHandles.Remove(address);
    }
}
```

Triệu chứng work order quan sát được là thật — `ClearPool` giữa game **không** giải phóng prefab
bundle. **Nhưng nguyên nhân và đơn thuốc đều đã cũ, và làm theo nó tệ hơn không làm gì.**

Handle tới `:167`/`:245` ở **refcount 2**: `AssetLoader.CacheHandle` (`:304-322`) đã `Retain()` cho
cache (`:319-320`), docstring `:300-302` nói rõ *"The caller keeps the reference the handle was born
with; the cache holds a second one"*. Pool sở hữu đúng phần của caller, `ReleaseTemplate` hạ 2→1.
**Reference còn lại là của cache loader** — đó mới là thứ giữ prefab resident. Theo work order thêm
`TryRetain()` → pool giữ 2 mà `ReleaseTemplate` vẫn chỉ trả 1 → biến "asset còn trong cache" thành
**rò rỉ vĩnh viễn không có đường release nào**. Xem §3.4.

**Sửa — giữ nguyên kỷ luật store-and-Dispose hiện tại, KHÔNG thêm `TryRetain()`.** Việc phải quyết là
`ClearPool` *nghĩa là gì*, và có hai đáp án chính đáng:

- **(a)** `ClearPool` phá pool nhưng để asset nằm trong cache cho pool sau của cùng prefab → **hành vi
  hiện tại đã đúng**, chỉ cần thêm docstring nói rõ prefab cố ý ở lại và chỉ ra
  `AssetLoader.ClearCache()` là đường thu hồi.
- **(b)** `ClearPool` phải thu hồi bộ nhớ → phải hạ thêm reference của cache loader bằng
  `AssetLoader.ReleaseAsset(address)` (`AssetLoader.cs:1811`, public). **Đọc kỹ trước khi chọn (b):**
  `ReleaseAsset` route sang `EvictAddress` (`:1752`), **hard release cố ý**
  (`PARALLEL_SESSIONS.md §3` ghi nhận là chủ ý cho đường `Simple.Load`) — nó sẽ giết asset dưới chân
  **bất kỳ holder sống nào khác** của cùng prefab. (b) chỉ an toàn khi pool là holder duy nhất, điều
  mà manager không thể biết.

**Khuyến nghị: (a) + tài liệu.** Muốn (b) thì cần một entry point eviction tôn trọng refcount trên
`AssetLoader` — vùng của `Runtime/Loaders/`, nêu ở `PARALLEL_SESSIONS.md §3` trước, đừng sửa thẳng.

#### P-6 · `ClearPool` release template trong khi instance đang được mượn vẫn sống, và không có gì track chúng — `medium`

`AddressablePoolManager.cs:432-443`

```csharp
pool.Clear();
pool.Dispose();
_pools.Remove(address);      // :439
ReleaseTemplate(address);    // :441
```

`pool.Clear()` chỉ với tới **free list**: `UnityPoolAdapter.Clear` (`:61-65`) forward sang
`ObjectPool.Clear` (destroy inactive list); `CustomPoolAdapter.Clear` (`:100-113`) nói thẳng ra ở
`:111` *"We don't clear active objects as they're still in use"*. Nên mọi instance đang được mượn
**sống sót qua lần clear**, vẫn tham chiếu prefab, trong khi address đã rời `_pools` ở `:439`. Lần
`Despawn` sau cho chúng rơi vào `:354-359` và bị destroy từng cái kèm warning (`:356`).
`ClearAllPools` (`:448-465`) cùng hình dạng ở quy mô lớn; `Dispose()` (`:467-473`) đi qua nó. Manager
**không giữ collection nào** của instance đang mượn — `_pools` và `_templateHandles` (`:22`, `:26`)
là toàn bộ state.

**Hỏng ra sao:** reference prefab bị thả trong khi GameObject sống vẫn phụ thuộc nó. Hiện tại
reference thứ hai của cache loader (P-5) **che mất** hậu quả — prefab còn nạp nên instance mồ côi vẫn
render. **Lớp che đó là tình cờ, không phải thiết kế:** bất kỳ `AssetLoader.ClearCache()` hay
`ReleaseAsset()` sau đó cho cùng address sẽ unload prefab **dưới chân** những instance còn trong
scene → mesh/material biến mất mà không có error nào tại điểm hỏng. Nghĩa là bug này **hiện vô hình
khi test** và chỉ lộ ra khi có người bắt đầu quản lý cache cho tử tế — thời điểm phát hiện tệ nhất
có thể.

**Sửa:** track instance đang mượn theo address — manager **dù sao cũng cần** cấu trúc này cho marker
nhận dạng pool của P-7, nên dựng một lần dùng cho cả hai. Rồi cho `ClearPool` tuyên bố hợp đồng và
thực thi nó: hoặc **(a)** từ chối clear khi còn instance chưa trả, log số lượng và address; hoặc
**(b)** destroy các instance còn lại như một phần của clear (teardown trung thực), rồi mới release
template. Chọn (b) cho `Dispose()`/`ClearAllPools` (đó là shutdown thật), (a) cho một lời gọi
`ClearPool` đơn lẻ nơi caller có thể không biết cái gì còn sống. Dù chọn gì, **release template phải
đến SAU khi các instance đã được tính sổ**, không phải trước. Xếp sau P-5 vì `ReleaseTemplate` nên làm
gì là do P-5 quyết.

#### P-7 · `Despawn` chỉ key theo string address, không marker trên instance — ba hành vi khác nhau tuỳ adapter — `medium`

`AddressablePoolManager.cs:354-361`

```csharp
if (!_pools.TryGetValue(address, out var pool))
{
    Debug.LogWarning($"[PoolManager] No pool found for {address}, destroying instance instead");
    UnityEngine.Object.Destroy(instance);
    return;
}
pool.Release(instance);      // :361 — không kiểm danh tính gì
```

`CreateInstance` (`:484-489`) không đóng dấu gì lên instance: không component, không marker, không
entry dictionary.

| Ca | `CustomPoolAdapter` | `UnityPoolAdapter` |
|---|---|---|
| Sai pool | từ chối: guard `!_activeObjects.Remove(obj)` (`:79-84`) log warning rồi return | **nhận im lặng** (`:58` `_unityPool.Release(obj)`) — `collectionCheck` chỉ quét inactive list của chính nó, nên object của pool khác **bị nhận nuôi** và sau này được phát ra cho sai address |
| Despawn hai lần | cùng guard `:80` → warning + return | `collectionCheck` (bật ở `:27`) → **throw `InvalidOperationException`** |
| `maxSize == 0` | coi là vô hạn (`:89` `if (_maxSize <= 0 \|\| _pool.Count < _maxSize)`) | truyền thẳng vào ctor `ObjectPool` (`:22-30`), **ctor từ chối** |

Cùng một hành vi caller, ba kết cục khác nhau tuỳ factory nào được cài — mà factory được chọn ở tận
`AddressablesFacade.cs:75`, xa chỗ gọi.

**Sửa:** đóng dấu danh tính pool lên instance lúc tạo (marker component ghi address, hoặc dictionary
instance→address dùng chung với P-6) và cho `Despawn` kiểm nó trước khi `Release`, trả về cùng một kết
quả bất kể adapter. Chuẩn hoá cả ba ca trên: sai pool và despawn hai lần phải cho **cùng** một hành vi
ở cả hai adapter, và `maxSize == 0` phải có một nghĩa duy nhất được ghi tài liệu. P-4 nên làm trước —
primitive prewarm/trim của nó dọn sẵn phần kế toán mà việc chuẩn hoá này dựa vào.

> **Nguồn chưa xác minh, phải kiểm trước khi trích như sự thật** (xem §8): ba hành vi của
> `UnityEngine.Pool.ObjectPool` mà P-3/P-4/P-7 dựa vào — ctor throw khi `maxSize <= 0`; `CountAll`
> chỉ tăng trong `Get()`; `collectionCheck` chỉ throw với object đã nằm trong inactive list — đến từ
> hiểu biết về source Unity, **không** từ một bản copy trong repo này. Rẻ nhất là khẳng định chúng
> một lần bằng EditMode test, và test đó dù sao cũng là regression guard cho cả ba mục.

### 4.5 Editor / Tests / Packaging — `Editor/` (ngoài `Cdn/`), `Samples~/`, `Tests/`

> **✅ ĐÃ VÁ 17:52 — số dòng của nhóm này đã được khôi phục.** Bản dựng đầu của file này thiếu
> chúng: dữ liệu reviewer bị cắt trước khi tới agent viết, nên §8 mô tả nhóm này là "không có số
> dòng đã xác minh". Dữ liệu vẫn còn nguyên trong journal và đã được lấy lại — bảng ngay dưới đây
> là **19 mục** (không phải 14), **4 trong đó là `critical`**, tất cả đều mở file ở trạng thái hiện
> tại của nhánh này mà lấy ra.
>
> Vẫn giữ nguyên cảnh báo cũ ở một điểm: **đừng lấy số dòng từ `REFACTOR_TASKS.md §6`**
> (E-01…E-08) — đó là số của audit `main` cũ, đã lệch.
>
> #### Bảng số dòng đã xác minh — nhóm Editor / Tests / Packaging
>
> Sắp theo mức thiệt hại. Phần diễn giải chi tiết cho từng cụm nằm ngay bên dưới bảng.
>
| Sev | Vị trí (đã xác minh trên nhánh này) | Vấn đề | Agent |
|---|---|---|---|
| **CRIT** | `Runtime/Monitoring/AssetMonitorBridge.cs:120` | `ResetOnLoad` xoá danh sách monitor **sau** khi Editor đăng ký, và không ai đăng ký lại → Play mode không nhận gì | `unity-tools-programmer` |
| **CRIT** | `Samples~/ApiExamples/AddressablesExamples.cs:70` | Sample **không compile** với consumer có UniTask — `task.IsCompleted` / `task.Result` không tồn tại trên `UniTask<T>` | `unity-tools-programmer` |
| **CRIT** | `Runtime/AddressableManager.asmdef:8` | UniTask là hard reference ở **cả bốn** assembly trong khi vắng mặt ở `package.json` → consumer không cài UniTask thì package không resolve được | `unity-build-engineer` |
| **CRIT** | `Editor/Rules/RuleConflictDetector.cs:52` | `DetectConflicts` exit 0 khi settings null → CI báo xanh trên project chưa init | `unity-build-engineer` |
| high | `Editor/Templates/BasicAddressRules.json:17` | Cả 5 template ship kèm đều inert — filter rỗng, provider path rỗng | `unity-data-engineer` |
| high | `Editor/Filters/PathFilter.cs:13` | Không có glob mode, trong khi **32** ví dụ trong doc dùng `**`; ở Regex mode `**` throw **mỗi asset quét qua** | `unity-tools-programmer` |
| high | `Editor/Filters/TypeFilter.cs:16` | Default `"UnityEngine.GameObject"` resolve null qua cả 3 lần thử rồi match rỗng, im lặng | `unity-tools-programmer` |
| high | `Editor/Rules/LayoutRuleProcessor.cs:169` | `FindAssets("")` — trùm cả `Packages/` lẫn `AddressableAssetsData/`, ở **3 chỗ** riêng biệt | `unity-tools-programmer` |
| high | `Editor/Filters/AssetFilterBase.cs:56` | Filter disabled trả `true` → rule tắt hết filter thành match-all | `unity-tools-programmer` |
| high | `Editor/Rules/LayoutRuleData.cs:34` | `AutoApplyOnImport`/`AutoApplyOnModified` mặc định `true`, và processor dirty + save settings **kể cả khi không đổi gì** | `unity-tools-programmer` |
| high | `Editor/CLI/AddressableCLI.cs:334` | Report JSON luôn là `{}` — `JsonUtility` không serialize được anonymous type | `unity-build-engineer` |
| high | `Editor/Inspectors/ScopeInspectors/SceneAssetScopeInspector.cs:10` | Khớp literal `"Scene"`/`"Hierarchy"` trong khi v4.0.0 sinh id instance-qualified → panel rỗng vĩnh viễn *(đổi hình dạng — đọc kỹ E-CHAIN mục 3)* | `unity-tools-programmer` |
| high | `Runtime/Monitoring/AssetMonitorBridge.cs:71` | `ReportAssetReleased` **0 producer** → cột "Refs" chỉ tăng | `unity-gameplay-programmer` |
| high | `Editor/Data/AssetTrackerService.cs:203` | `DetectPotentialLeaks` **không bao giờ** flag được asset over-retained — predicate loại đúng chúng ra | `unity-tools-programmer` |
| high | `Tests/Editor/AddressRuleGroupSchemaTests.cs:1` | Test suite hoàn toàn CDN-scoped — **không** test nào phủ 4 đảm bảo refcount của Wave 1 | `unity-qa-engineer` |
| medium | `Editor/CLI/AddressableCLI.cs:91` | `-warningAsError` không đọc warning của processor, chỉ áp cho pre-flight validation | `unity-build-engineer` |
| medium | `Editor/CLI/AddressableCLI.cs:308` | Parser nuốt flag kế tiếp làm value → boolean flag không giá trị đọc thành `false` | `unity-build-engineer` |
| medium | `package.json:34` | `com.unity.textmeshpro` là hard dependency cho đúng một component đã được guard sẵn | `unity-build-engineer` |
| medium | `Editor/Templates/README.md:102` | Doc hướng dẫn lệnh CI gọi `AddressableCLI.ImportRules` — hàm không tồn tại | `documentation-specialist` |
>
> **Ba mục trong bảng nằm ngoài `Editor/`** và cần claim ở §2 trước:
> `Runtime/Monitoring/AssetMonitorBridge.cs` (2 mục) và `Runtime/AddressableManager.asmdef`.
> Mục `ReportAssetReleased` còn cần producer đặt ở `Runtime/Core`/`Runtime/Loaders` — xem E-CHAIN.
>
> Vùng này `PARALLEL_SESSIONS.md §1` ghi **"chưa ai — Wave 2E chưa bắt đầu"** và code khớp với điều
> đó: Wave 1 chạm `Runtime/Core` + `Runtime/Loaders`, các commit 4.1.0-pre.x chạm `Runtime/Scopes`,
> `Runtime/Threading`, `Runtime/UI` — `Editor/` ngoài `Cdn/` chưa bao giờ nằm trong bán kính nào.
> **Claim ở §2 trước khi đụng.**
>
> Agent: `unity-tools-programmer` cho Filters/Rules/Automation/Windows/Inspectors;
> `unity-build-engineer` cho `Editor/CLI/AddressableCLI.cs`.

#### E-CHAIN · Chuỗi monitoring — thứ tự ở đây là thứ duy nhất sẽ làm B mất thời gian nếu bỏ qua

Bốn mục nối thành chuỗi và **ba trong bốn cái không quan sát được cho tới khi cái đầu được sửa**:

1. **`AssetMonitorBridge.ResetOnLoad` xoá danh sách monitor ở `SubsystemRegistration`, chạy SAU khi
   `[InitializeOnLoad]` đăng ký.** Nên trong Play mode **toàn bộ pipeline monitoring không giao gì
   cả**. Sửa cái này **trước**.
2. **`ReportAssetReleased` không có producer nào** — không ai gọi nó khi refcount về 0.
   ⚠️ **Cần bàn giao §2:** producer phải nằm ở `Runtime/Core` hoặc `Runtime/Loaders` — không viết
   được từ vùng Editor.
3. **Scope inspector khớp tên scope bằng literal** — mục `CHANGED` duy nhất của nhóm. Audit cũ gọi là
   "sai chuỗi hard-code" và ám chỉ cứ sửa chuỗi là xong. **Sai:** v4.0.0 làm scope id
   **instance-qualified và không chặn trên** — `Scene-{sceneName}#h{handle}`,
   `Hierarchy-{goName}#{instanceTag}` — nên **không literal nào khớp được**; cách sửa là đọc
   `_targetScope.ScopeName` **lúc refresh**. Hai chi tiết nữa: `"Global"` **vẫn khớp chính xác**
   (`GlobalAssetScope.cs:41` dùng literal `"Global"`), nên chỉ Scene và Hierarchy hỏng; và mục
   `"Session"` trong dropdown **KHÔNG chết** dù `SessionAssetScope` đã bị xoá — `HybridScope.cs:77`
   vẫn sinh ra key đó. **Ai sửa bằng cách xoá `"Session"` sẽ phá đường HybridScope.**
4. **`DisplayName` chưa tới được Dashboard.**

Sửa sai thứ tự thì agent sẽ đổi code đúng, thấy Dashboard không đổi gì, rồi bắt đầu nghi ngờ chính
finding.

#### E-PAIR-1 · Rule tắt hết filter thành match-all, chạy trên một lần quét không chặn trên — `high`

Hai bug riêng lẻ, nhưng ghép lại thì **một checkbox bị bỏ quên viết lại layout addressable xuyên biên
giới package**: một rule mà mọi filter đều disabled trở thành match-all, và phép quét nó chạy lên là
`FindAssets("")` — không chặn trên, trùm cả `Packages/` lẫn `AddressableAssetsData/`. `ApplyAddressRule`
sau đó gọi `CreateOrMoveEntry` lên bất cứ thứ gì khớp.

**Chính xác — chỗ dễ sửa nhầm:** `AssetFilterBase.IsMatch` trả `true` khi disabled là **hợp lý khi
đứng riêng**; defect chỉ xuất hiện ở phép AND trong `AddressRule.IsMatch`. **Sửa ở tầng rule, đừng
lật giá trị trả về của base** — làm thế sẽ lặng lẽ đảo ngược mọi filter đã cấu hình trên một bản
pre-release.

#### E-PAIR-2 · Docs dạy `**` ở 32 chỗ, `PathFilter` không có chế độ glob — `high`

`PathFilter` không hiểu `**`; một pattern `**` đặt ở chế độ Regex thì **throw một lần cho mỗi asset**
vì đường pattern-không-hợp-lệ không bao giờ gán `_cachedPattern`. Người dùng làm theo đúng tài liệu
đã ship sẽ ngập console một error mỗi asset trong project.

**Chính xác:** `PathFilter` **KHÔNG** recompile regex mỗi asset trong ca lành — pattern hợp lệ compile
một lần và `_cachedPattern` chặn recompile đúng như thiết kế. Cơn bão per-asset **chỉ** thuộc đường
pattern không hợp lệ. Đừng "tối ưu" phần đang chạy đúng.

#### E-PAIR-3 · Template ship kèm fail validation — và chính điều đó đang che một `SaveAssets` vô điều kiện — `high`

5 rule template ship kèm đều inert nên trượt validation, và **đó là thứ duy nhất đang ngăn**
`SaveAssets` vô điều kiện bắn ở mỗi lần import. **Sửa template mà không đồng thời gác lệnh save ở
`LayoutRuleProcessor.cs:134-135` sẽ biến một nguồn merge-conflict đang ngủ thành đang chạy.**

**Chính xác:** `LayoutRuleProcessor.ApplyRulesToAssets` validate **trước** khi save, nên `SaveAssets`
vô điều kiện **không** bắn với rule set không hợp lệ — chỉ bắn với rule set **hợp lệ mà không khớp gì**,
tức là trạng thái thường ngày.

#### E-OTHER · Các mục còn lại của nhóm

- `TypeFilter` mặc định `"UnityEngine.GameObject"` không bao giờ resolve, fail im lặng.
- Mọi JSON report của CLI là `{}` — `JsonUtility.ToJson` trên anonymous type.
- CLI exit code: `DetectConflicts` exit 0 khi settings null (CI xanh giả); `-warningAsError` không
  đọc `result.Warnings`; parser nuốt flag làm value; `BuildPlayerContent` sai overload.
- `ImportFromJson` chỉ resolve provider theo asset path, bỏ qua `addressProviderType` mà chính nó
  export.
- `AutoApplyOnImport` mặc định phải thành **false**; `FindAssets("")` phải giới hạn `new[]{"Assets"}`
  và loại `AddressableAssetsData`.
- Undo + cancelable progress cho mọi mutation rule; dialog 2 nút để Esc = "Replace" (mất hết rule).
- `AssetTrackerService.RegisterAssetRelease` **tự nó đúng** — nó decrement chuẩn. Defect nằm ở
  thượng nguồn: **không ai gọi nó** (đây chính là E-CHAIN mục 2).

#### E-NEW-1 · `Samples~/RuleAutomation` ship **hiểm hoạ sống**, không phải ví dụ — `high`

- `ExampleLayoutRules.asset:46-47` có `_autoApplyOnImport: 1` và `_autoApplyOnModified: 1` → **import
  sample là arm postprocessor lên toàn bộ project của người dùng, không hỏi.**
- `TypeFilter.asset:18` có `_typeName: UnityEngine.SceneAsset` — cùng lỗi resolve như default của
  `TypeFilter`, và sai gấp đôi vì `SceneAsset` là `UnityEditor.SceneAsset`.

Đây là mục "TypeFilter default" được xác nhận trên một **artifact đã ship** chứ không phải trên giả
thuyết. *(Hai file `.asset` — theo `CLAUDE.md` thì file config/`.asset` được sửa trực tiếp ở main.)*

#### E-NEW-2 · `AddressableCLI` không có gate `EditorUtility.scriptCompilationFailed` ở cả 4 entry point — `high`

`CLAUDE.md` §Quy tắc cứng mục 3 bắt buộc điều này. **Đã có sẵn mẫu trong repo để copy chứ không phải
tự nghĩ** — bốn CLI của Cdn đều có: `CatalogInspectCLI.cs:37`, `CdnBuildCLI.cs:130`,
`CdnBuildPipeline.cs:289`, `CdnTabProbeCLI.cs:25`. Gộp vào lượt sửa CLI (`unity-build-engineer`).

#### Vì sao compile gate không thấy gì trong nhóm này

Đã xác minh trực tiếp trong `Tools/CompileGate/run.sh`: nó ghim `ADDR_GATE_REF_UNITY` 6000.5.7f1
(`:14-15`) và **không có chỗ nào** define `UNITASK_PRESENT`. Ngoài hai giới hạn đó, ba defect ở đây
**vô hình với mọi compile gate về mặt cấu trúc**: `Samples~` có hậu tố `~` nên Unity không bao giờ
compile nó tại chỗ; asmdef reference resolution không bao giờ xảy ra vì gate tự cấp reference set;
và dependency resolution của `package.json` không bao giờ được chạy. **Nếu §6 mở rộng gate, một lượt
`UNITASK_PRESENT` có compile luôn `Samples~` sẽ bắt được cái đầu tiên — rẻ nhất trong ba cái.**

---

## 5. Thứ tự bắt buộc

Không phải danh sách ưu tiên. Mỗi cạnh dưới đây có một lý do kỹ thuật, và đảo cạnh nào thì hỏng ở
chính chỗ đó.

```
  C-1…C-6 + C-11  ─┬─→  L-1 (teardown)  ──→  L-7 (khai tử fork, quyết định)
   (một patch)      │
                    ├─→  P-5 → P-6      (ClearPool)
                    │
                    └─→  L-4 (ngân sách chung)  ←──  L-8 (bỏ reflection)  ←── làm TRƯỚC
                                  ↑
                              L-5 (byte thật)

  A-1 + A-2  (một change, một agent)  ──→  A-3/A-4/A-5  ──→  A-11
                                                              ↑
                                                     Runtime/Loaders trả ở §2

  A-7 (quyết định) ──→ A-10          P-1 ──→ P-2, P-3 ──→ P-4 ──→ P-7
  E-CHAIN-1 ──→ E-CHAIN-2 ──→ E-CHAIN-3 ──→ E-CHAIN-4
```

### Vì sao nhóm caches là TIỀN ĐỀ chứ không phải follow-up

Đây là cạnh dễ hiểu ngược nhất trong cả file, nên viết dài hơn một chút.

Bản năng đầu tiên khi đọc *"`Clear()` không release gì → rò rỉ"* là đi thêm `Release()` vào `Clear()`.
**Làm thế trước C-1 là biến một vụ rò rỉ thành một vụ giải phóng dưới chân người đang giữ.** Lý do:
hôm nay cache **không sở hữu reference nào** — nó lưu chính object mà caller đang cầm, ở refcount 1.
Thêm `Release()` vào `Clear()` nghĩa là trả một reference chưa từng lấy → count về 0 →
`Addressables.Release` chạy → caller còn sống cầm handle trỏ vào asset đã unload, và `Dispose()` sau
đó của chính họ **no-op** (`IAssetHandle.cs:165` trả `false` ở count 0) nên không có gì báo động.

Chiều ngược lại cũng đúng: thêm retain vào `Set()` (C-1) **mà không** sửa `TryGet` (C-2) thì cache
vẫn phát ra handle chưa retain, và mọi cache hit vẫn dùng chung một reference. Còn sửa `TryGet`
(C-2) mà không xoá `Retain()` ở `TieredAssetLoader` (C-6) thì **mỗi cache hit rò rỉ một reference
vĩnh viễn**.

**→ C-1, C-2, C-3, C-4, C-5, C-6 land trong MỘT patch, hoặc không land cái nào.** Mỗi tập con đều
tạo ra một trạng thái tệ hơn hiện tại.

Hệ quả: eviction **hôm nay đang chạy thật** trên `TieredCache` (C-12 chứng minh: `TieredAssetLoader`
có truyền `estimatedSize` khác 0 ở cả hai đường load, nên ngưỡng 90% trên 100MB **có** chạm). Nghĩa
là mỗi ngày trôi qua chưa sửa là mỗi ngày `Advanced.ForceEviction` và memory pressure đang giải phóng
asset dưới chân holder sống.

### Các cạnh còn lại

| Cạnh | Vì sao |
|---|---|
| **caches → L-1** | Teardown không thể có ngữ nghĩa đúng khi cache chưa sở hữu reference. `Release()` ở đâu, `ForceRelease()` ở đâu — cả hai câu hỏi chỉ trả lời được sau C-1 |
| **caches → P-5** | `ClearPool` nghĩa là gì phụ thuộc ai đang giữ reference thứ hai. Quyết trước khi biết là quyết mù |
| **P-5 → P-6** | `ReleaseTemplate` nên làm gì là do P-5 chốt; P-6 chỉ sắp lại thứ tự quanh nó |
| **L-8 → L-1** | `ITieredCache` cấp cái hook teardown **có kiểu** (`ForceReleaseAll()`) mà L-1 cần. Không có nó thì L-1 phải tự bịa reflection thứ tư |
| **L-8 → L-4** | Ngân sách chung buộc eviction phải với sang cache anh em — đúng bằng interface không generic mà L-8 dựng. Làm rời là làm hai lần |
| **L-8 → L-3** | Nếu reflection bị IL2CPP strip thì pump mới thêm cũng thành no-op im lặng **với cùng triệu chứng**. Thêm pump trước khi gỡ reflection = không biết mình đã sửa được hay chưa |
| **L-5 → L-3, L-4** | Ngân sách chung và pump dựng trên số byte bịa vẫn là số bịa. Đo đúng trước, rồi mới quyết dựa trên số đo |
| **A-1 + A-2 cùng lúc** | Một trong hai phương án của A-2 là **xoá** `AddressablesFacade.cs:367` — chính dòng A-1 vừa dời. Hai agent nối tiếp thì người sau xoá mất tiền đề của người trước |
| **A-4 → xem lại `AssetLoader.ClearCache`** | Docstring `:1611-1621` và cặp `ForceRelease`/`Dispose` **lấy bug A-4 làm lý do tồn tại**. Sửa A-4 xong mà không đụng tới chúng thì lý lẽ viết trong code thành sai |
| **§2 trả vùng → A-11** | Cần một cache-probe API public trên `AssetLoader` — chưa tồn tại |
| **§2 trả vùng → L-2** | Cần một progress seam trên `AssetLoader` — là thay đổi trên file của chủ vùng, phải xin qua §3 chứ không sửa |
| **P-1 trước P-2/P-3** | P-1 viết lại đường auto-create; P-2 và P-3 đều nằm trong đúng đường `Spawn`/`CreateInstance` đó |
| **P-4 → P-7** | Primitive `Prewarm`/`TrimExcess` của P-4 dọn phần kế toán mà việc chuẩn hoá adapter của P-7 dựa vào. Còn có một cạnh phụ **P-4 → P-3**: đường `DynamicPool` của P-3 nên dùng lại primitive đó thay vì cặp Get/Release |
| **A-7 → A-10** | `Standard.ClearCache(scopeName)` chỉ với tới được scope đã đăng ký. Quyết định A-7 định nghĩa cái mà A-10 có thể hứa trong docstring |
| **E-CHAIN 1→2→3→4** | Monitoring không giao gì cho tới khi mục 1 xong. Sửa sai thứ tự = agent đổi đúng code, thấy Dashboard đứng yên, rồi nghi ngờ finding |
| **L-7 sau khi có quyết định** | Nếu chốt khai tử `TieredAssetLoader` thì L-10 thành vô nghĩa và L-9 gần như vậy. Nhưng **C-1/L-1/L-8 vẫn phải làm** — `[Obsolete]` mà rò rỉ mọi asset thì không ship được ở 4.x |
| **C-12 sau C-1/C-7/C-8** | Cấp producer `estimatedSize` cho `ThreadSafeCacheManager` là **kích hoạt** đường eviction đang chết của nó. Kích hoạt trước khi sửa = chính lần kích hoạt đó làm nổ crash |

---

## 6. Bàn giao lại `Tools/CompileGate/` — A nhận rồi không làm

`PARALLEL_SESSIONS.md §2` ghi A nhận vùng này lúc **15:10** với việc *"Gộp năng lực
`check-min-unity-api.sh` (min-Unity + ma trận UniTask) vào `run.sh`"*. **A không làm.**
`Tools/CompileGate/run.sh` hiện vẫn 18 dòng, hard-code, không tham số:

```bash
export ADDR_GATE_REF_UNITY="C:\\Program Files\\Unity\\Hub\\Editor\\6000.5.7f1\\Editor\\Data"   # :14
export ADDR_GATE_CSC_UNITY="C:\\Program Files\\Unity\\Hub\\Editor\\6000.5.7f1\\Editor\\Data"   # :15
```

**Đây là task của B từ bây giờ.** `unity-build-engineer`. Vùng dùng chung → claim ở §2 trước, trả
ngay sau khi xong.

### Vì sao nó đáng làm chứ không phải việc dọn dẹp

Gate mặc định trả lời *"cái này có compile không"*. Nó **không** trả lời *"cái này có compile trên
bản Unity tối thiểu mà package tự khai không"*. Hai câu hỏi khác nhau, và **`4.1.0-pre.4` hỏng vì câu
thứ hai**: xanh toàn tập trên 6000.5.7f1 — 0 error, 0 warning, tab probe xanh, 57/57 test xanh — rồi
vỡ `CS1503` trên 2022.3 ở `FindObjectsByType<T>(FindObjectsInactive)`, một overload chỉ có từ Unity 6,
trong khi `package.json` khai `2023.1`.

`Tools/check-min-unity-api.sh` của B **đã chứng minh được thứ gate của A không chứng minh được**:
Runtime **sạch** trên 2022.3 với assembly thật của một project 2022.3, **cả nhánh `UNITASK_PRESENT`
— 97 chỗ rẽ trên 16 file, trước đó chưa từng compile ở đâu**. Ngược lại, gate của A dựng được
`AddressableManager.Editor` — thứ tool của B không dựng nổi bộ reference. **Mỗi bên biết một nửa.**

### Kế hoạch A đã đồng ý — làm đúng như vậy

1. **`run.sh` nhận `--editor <root>` và `--refs <dir>`.** Ánh xạ thẳng sang hai tham số của
   `check-min-unity-api.sh` (`./Tools/check-min-unity-api.sh <editor-root> [reference-assembly-dir]`).
   Tham số thứ hai là dạng mạnh nhất của phép kiểm: trỏ vào `ScriptAssemblies` của **project đích** →
   package được compile với Unity thật, Addressables thật, UniTask thật của project đó.
2. **Preset `--min-unity` chạy đúng bốn tổ hợp**, vì package không có một dạng source mà có bốn:

   | # | Assembly | Nhánh | Phủ cái gì |
   |---|---|---|---|
   | 1 | Runtime | player, **no** UniTask | chữ ký `Task<T>` |
   | 2 | Runtime | player, `UNITASK_PRESENT` | chữ ký `UniTask<T>` (invariant 3) |
   | 3 | Runtime | editor, `UNITASK_PRESENT` | các nhánh `#if UNITY_EDITOR` trong Runtime |
   | 4 | Editor | `UNITASK_PRESENT` | assembly Editor |

3. **GIỮ đường nhanh mặc định.** `bash Tools/CompileGate/run.sh` không tham số phải vẫn ~10s và vẫn
   cache theo mtime — **nó chạy sau mỗi lần agent sửa file**. Ma trận 4 tổ hợp là chế độ riêng, chạy
   trước release. Hai câu hỏi khác nhau, hai tần suất khác nhau. Đây là điểm A giữ khác đề xuất ban
   đầu của B, và nó vẫn đúng.
4. **KHÔNG xoá `check-min-unity-api.sh` cho tới khi bản gộp cho ra KẾT QUẢ GIỐNG HỆT trên cùng input.**
   Không đổi một cái đang chạy được lấy một cái chưa chứng minh. Xoá sau khi đối chiếu xong.

### Ghi vào README của gate khi xong

`Tools/CompileGate/README.md` §Giới hạn hiện liệt kê hai hạn chế này như "đã biết" (mục 1:
`UNITASK_PRESENT` không set; và bảng ở `:34` còn ghi `ADDR_GATE_REF_UNITY` mặc định là 2022.3.62f3
trong khi `run.sh:14` ghi đè bằng 6000.5.7f1 — **README và script đang lệch nhau, sửa luôn**). Khi
ma trận chạy được thì hai mục đó thôi là hạn chế và phải được viết lại thành hướng dẫn dùng.

**Cái ma trận vẫn KHÔNG bắt được**, phải ghi rõ để đừng đọc quá lời cái màu xanh: nó là compile với
reference assembly, không phải một bản build Unity; nó không chạy code; và ba defect ở §4.5 vô hình
với nó về mặt cấu trúc (`Samples~` không được compile tại chỗ, asmdef reference resolution không xảy
ra, `package.json` dependency resolution không được chạy). Một lượt có compile `Samples~` sẽ bắt được
cái đầu.

---

## 7. Đã xong — đừng làm lại

### 7.1 Đã landed

Trong 66 mục được verify, **0 mục ALREADY_FIXED** — không defect nào tự biến mất. Nhưng những việc
sau **đã xong bằng đường khác** và work order cũ vẫn còn liệt kê chúng:

- **`AssetLoaderRegistry`** — `Runtime/Loaders/AssetLoaderRegistry.cs`, 165 dòng, `internal static`,
  `WeakReference`, commit `9a19790`. Đăng ký ở ctor `AssetLoader.cs:107`, gỡ ở `Dispose` `:1873`,
  tiêu thụ ở `CatalogService.cs:436` `InvalidateAll(keys)`. Phủ cả 6 nguồn. **Đừng giao ai dựng lại.**
- **Tách `InvalidateAddress` / `EvictAddress`** — `AssetLoader.cs:1711` (`Dispose()`, decrement, cho
  catalog update, holder sống sót) và `:1746` (`ForceRelease()`, hard, cho `ReleaseAsset`).
  `PARALLEL_SESSIONS.md §3` bổ sung 15:24. Gate PASS. **Đừng phát minh ngữ nghĩa thứ ba** — hai cache
  ở §4.1 copy ngữ nghĩa `InvalidateAddress`.
- **Call site `InvalidateAddresses` sau `UpdateCatalogs`** — `CatalogService.ApplyUpdateAsync`, thu
  key **trước** `SafeRelease`, gọi invalidate **sau**, gate trên `handle.Result != null` chứ không
  trên `succeeded`. Đã landed, có lý do ghi kèm.
- **`autoCleanBundleCache: true → false`** — trước đó kết quả dọn cache quyết định status của catalog
  update, nên trên WebGL (không `ENABLE_CACHING`) **mọi update thành công đều bị báo Failure**.
- **`SceneAssetScope.cs` `FindObjectsByType(Inactive, SortMode.None)` + pragma CS0618** — chính là
  fix của `4.1.0-pre.5` (`22d762d`). Còn nguyên, đúng, **và phải để nguyên**. Xem §2.5.
- **Lượt compile UniTask** (`4d6a6ca`) — lần đầu tiên nửa UniTask của package được compile.
- **`MonitoredAssetLoader.cs`** — đã kiểm, sạch, chữ ký kép đúng. Không có việc.
- **Wave 1** — refcount thật, single-flight map, guard sau cả 11 await site, `AssertMainThread` mở
  rộng, cache key `(address, Type)`, track instantiate. Đã landed + fix pass, gate PASS.
- **`UnityMainThreadDispatcher.cs:33`** `FindObjectOfType` → `FindAnyObjectByType`; **và các sửa
  Editor của B** (`LayoutRuleDataInspector`/`CompositeLayoutRuleDataInspector` `DrawHeader` →
  `DrawTitleSection`, `ContextMenus.cs:203`, `MemoryGraphView.cs:116` `Clear()` → `ClearSamples()`,
  `Editor/AssemblyInfo.cs` với `InternalsVisibleTo`). Xem `PARALLEL_SESSIONS.md §7`.

### 7.2 Ba đơn thuốc trong work order cũ giờ SAI — làm theo là tạo bug nặng hơn

Đây là lý do thật của đợt verify này. **Đọc trước khi mở `REFACTOR_TASKS.md §0.2`.**

| Work order cũ nói | Thực tế | Nếu làm theo |
|---|---|---|
| **Row 4** — `Pooling/AddressablePoolManager.cs:166,245,462,479`: `_templateHandles` nên `TryRetain()` | Handle tới pool ở **refcount 2** (caller + cache loader). Store-and-`Dispose()` hiện tại **đã đúng** hợp đồng Wave 1 | Pool giữ 2, trả 1 → **rò rỉ vĩnh viễn không có đường release**. Tệ hơn hiện trạng. Xem P-5 và §3.4 |
| **W2-06** — eviction *"skip `ReferenceCount > 1`"* | Racy (count đổi giữa đọc và evict), thừa (sau C-1 evict là decrement, holder tự sống), và **có hại**: "còn holder" thành miễn trừ eviction vĩnh viễn | Asset phổ biến ghim cache ở trần dung lượng, eviction quay vòng không giải phóng byte nào. **C-1 mới là fix.** Xem C-10 |
| **Row 1** — `PerformEviction` chạy re-entrant *"trong chính lần load sinh ra handle đó"* | Re-entrancy có thật; **evict chính entry vừa chèn thì KHÔNG** — entry mới có score ≈ 69.3, mọi threshold preset là 0.5/1.0/3.0 | B đi tái hiện một kịch bản không tái hiện được, rồi mất tin vào phần còn lại của tài liệu. Xem C-13 |

Hai đính chính nhỏ hơn cùng loại:

- **Row 2 / `TieredAssetLoader.cs:116`, `:220` không phải "live crash".** Wave 1 làm `IsValid` đọc
  refcount trước (`AssetHandle.cs:31`), comment `:29-30` nói rõ `IsValid` và `TryRetain` không còn
  khe. Trên main thread `Retain()` **không throw tất định**. Khe còn lại là cross-thread qua finalizer
  `SmartAssetHandle`. Vẫn sửa (nó nằm ngoài `try`), nhưng **đừng brief là P0**. Xem §3.3(a).
- **`Loaders/TieredAssetLoader.cs` teardown không phải "`Dispose()` giờ chỉ decrement".** Đường
  teardown **không gọi `Dispose()`/`Release()`/`ForceRelease()` lên handle nào cả** — rò rỉ cứng, tệ
  hơn cái được báo. Và cache **chưa từng retain**, nên eviction đang tiêu reference của caller. Hai
  bug ngược chiều, một luật thiếu. Xem L-1.

Và một tiền đề đã chết: **"session B cần một registry `AssetLoader`"** — B đã dựng xong, xem §7.1.

---

## 8. Chưa xác minh được

> **✅ Cập nhật 17:52 — lỗ lớn nhất trong mục này đã được lấp.** Bản dựng đầu của file này báo rằng
> nhóm Editor/Tests/Packaging không có số dòng đã xác minh, vì dữ liệu reviewer bị cắt trước khi tới
> agent viết. Dữ liệu vẫn còn nguyên và đã được khôi phục: **19 mục, 4 critical**, bảng đầy đủ ở
> §4.5. Phần còn lại của mục 8 dưới đây vẫn đúng nguyên.

Ghi thẳng thay vì làm mượt. Chỗ nào dưới đây cũng là chỗ B nên tự kiểm trước khi dựa vào.

### 8.1 Phần của bản bàn giao này bị thiếu

Reviewer verify **66 mục**; bản tóm tắt tới tay tôi **bị cắt sau mục thứ 43**. Hệ quả cụ thể:

| Nhóm | Có gì | Thiếu gì |
|---|---|---|
| caches (13), api-scopes-facade (13), loaders (10) | đầy đủ từng mục, có số dòng + code trích | — |
| pooling | 7 mục đầu đầy đủ | mục cuối (`Despawn`, P-7) bị cắt giữa phần evidence — phần bảng adapter tôi dựng từ những gì đọc được; ghi chú vùng cho thấy nhóm này còn **ít nhất 3-4 mục nữa** (ghi chú nhắc "items 9, 10 là design gap cần quyết định sản phẩm: `PoolConfiguration` được wire vào hay `[Obsolete]`?", và một "item 11" về divergence adapter) |
| editor-tests-packaging (14) | **chỉ có ghi chú vùng** | **toàn bộ số dòng hiện tại của từng mục.** §4.5 viết từ ghi chú vùng, và mọi số dòng ở đó là số tôi đọc trực tiếp từ ghi chú |

**Việc cần làm để gỡ:** trước khi delegate §4.5, cho một agent read-only (`unity-code-reviewer` hoặc
`Explore`) mở từng file `Editor/` và tự lấy số dòng hiện tại. **Đừng lấy số dòng từ
`REFACTOR_TASKS.md §6` (E-01…E-08)** — đó là số của audit `main` cũ, đúng cái loại số mà cả đợt verify
này sinh ra để thay thế. Cũng vậy với 3-4 mục pooling còn lại.

### 8.2 Reviewer đánh dấu không kiểm được

| Cái gì | Vì sao chưa chắc | Gỡ bằng cách nào |
|---|---|---|
| `Profiler.GetRuntimeMemorySizeLong` trả **0** trong non-development build trên một số platform (L-5) | Là hành vi được ghi nhận, **không** kiểm được từ repo này | Kiểm trên platform đích **trước khi** thay heuristic. Nếu đúng thì giữ heuristic làm fallback sau guard `size <= 0` — nếu không, release build lặng lẽ nhận `estimatedSize == 0`, eviction không bao giờ chạy, cache phình vô hạn: **tệ hơn hiện tại** |
| `UniTaskCompletionSourceCore.GetResult` throw thay vì block (P-1, nhánh `UNITASK_PRESENT`) | UniTask resolve từ git URL, **không vendored trong worktree này** — reviewer không có source để đọc | Đối chiếu với package đã resolve trước khi trích như sự thật. **Nửa non-UniTask (deadlock) thì kiểm được hoàn toàn từ repo này** |
| Ba hành vi của `UnityEngine.Pool.ObjectPool` (P-3, P-4, P-7): ctor throw khi `maxSize <= 0`; `CountAll` chỉ tăng trong `Get()`; `collectionCheck` chỉ throw với object đã ở inactive list | Từ hiểu biết về source Unity, không từ bản copy trong repo | Một EditMode test khẳng định cả ba — dù sao cũng là regression guard cho ba mục đó |
| P-2 (qua ranh giới scene load) và P-6 (teardown giữa game) | Đọc code chứng minh được **đường đi**, không chứng minh được **thời điểm** | PlayMode test qua `LoadSceneMode.Single`. EditMode không tái hiện được việc destroy |
| **Baseline gate** | **Không reviewer nào chạy gate**, cả 5 cùng lý do: lúc đó B đang sửa dở `AssetLoader.cs` + `Runtime/Threading/`, đỏ thì không quy được, xanh thì không chứng minh gì | Chạy `bash Tools/CompileGate/run.sh` **ngay bây giờ** — việc của B đã commit (`9a19790`, `4d6a6ca`) nên lần chạy đó là lần đầu baseline được xác nhận sau khi registry landed |

### 8.3 Quyết định cần NGƯỜI, không phải agent

Để implementer tự chọn thì nó chọn cái ít gõ nhất, và mấy cái này có hệ quả về lifetime chứ không
phải về số dòng code.

| # | Quyết định | Hai phía |
|---|---|---|
| A-2 | `GlobalAssetScope` sống lại kiểu gì | dựng lại lười trong getter **vs** Facade thôi sở hữu singleton (xoá `AddressablesFacade.cs:367`) — khác nhau ở việc scope có sống xuyên các lần facade restart hay không |
| A-7 | Scope có đăng ký với `ScopeManager` không | đăng ký (làm `ClearAll`/`ClearAllExceptGlobal` đúng như tên, mở khoá A-10) **vs** `[Obsolete]` `ClearAllExceptGlobal` vì phần bảo vệ Global của nó không implement được. Phía đăng ký tạo một registry strong-reference **bên cạnh** registry weak của B |
| A-12 | `HybridScope` có sống tiếp không | Nó là cơ chế scope **thứ ba**, không năng lực riêng, không reset hook, 0 chỗ dựng ngoài `AdvancedAPI`, va chạm tên monitoring với hai storage thật. Khai tử **xoá hẳn một storage** và làm A-6 thành không cần |
| P-5 | `ClearPool` nghĩa là gì | (a) pool đi, asset ở lại cache — **hành vi hiện tại đã đúng**, chỉ cần docstring; **vs** (b) thu hồi bộ nhớ — cần `ReleaseAsset` (hard, giết dưới chân holder khác) hoặc một entry point mới tôn trọng refcount trên `AssetLoader` |
| C-12 | `ThreadSafeCacheManager` và `estimatedSize` | cấp producer **vs** ghi lên class rằng consumer phải tự truyền **vs** bỏ hẳn đường eviction theo size. Chọn cách một là **kích hoạt** đường eviction đang chết → C-1/C-7/C-8 phải xong trước |
| L-7 | `TieredAssetLoader`: vá cho bằng hay khai tử | W4-05 đã chốt hướng khai tử (tiering thành config của loader duy nhất). Cần plan từ `unity-tech-lead-orchestrator` trước khi viết code. Dù chọn gì, **C-1/L-1/L-8 vẫn phải làm** |
| E-OTHER | `PoolConfiguration` và các feature inert | wire vào **vs** `[Obsolete]`. `REFACTOR_TASKS` §Quy tắc bất di bất dịch mục 5: không để nguyên trạng — false confidence tệ hơn không có feature |

### 8.4 Ba chuyện tôi không kiểm và cũng không nên kiểm

- **`Runtime/Loaders/AssetLoader.cs` và `Runtime/Threading/`** — B đang sửa dở lúc verify. `AssetLoader`
  chỉ được đọc để lập hợp đồng refcount (`CacheHandle` `:304-321`, `TrackHandle` `:1332`,
  `GetCacheStats` `:1824`, `ReleaseInstance` `:1448`, `ClearCache` `:1625`) và để copy pattern
  (`CaptureMainThread` `:86-90`, `AssetCacheKey` `:1896`) — những vùng đó là Wave-1-landed, không phải
  phần B đang sửa. **Không có finding nào ở đây nhắm vào việc đang làm dở của B.**
- **`AssetLoaderRegistry.cs`** — không mở để đánh giá. Một quan sát để B tự cân nhắc trong thiết kế
  của mình, **không phải finding**: `TieredAssetLoader` giữ cache per-type của riêng nó và
  **không đăng ký** với registry, nên nó là **nguồn thứ bảy** không với tới được của
  `InvalidateAddresses`, ngoài sáu nguồn B đã liệt kê — asset cache ở đó tiếp tục phục vụ catalog cũ
  sau update. Nếu L-7 chốt khai tử fork thì vấn đề này tự biến mất.
- **`Runtime/Cdn/`, `Editor/Cdn/`** — vùng của B, ngoài phạm vi.
