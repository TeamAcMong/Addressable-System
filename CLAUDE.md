# Addressable System — AI Team Configuration

Repo này là một **UPM package** (`com.game.addressables`), không phải game. Không có gameplay, physics,
AI hay animation. Công việc chia thành bốn mảng: **runtime infrastructure C#**, **Editor tooling**,
**build pipeline / CI**, và **tài liệu**.

Workstream đang chạy: **CDN system**, nhánh `feat/cdn-system`, 6 phase — xem
[Documentation/CDN_SYSTEM.html](Documentation/CDN_SYSTEM.html) (hoặc bản
[tiếng Việt](Documentation/CDN_SYSTEM_VI.html)).

---

## Agent Routing Rules (BẮT BUỘC)

**KHÔNG BAO GIỜ tự viết hoặc sửa code C# trong main conversation.**
Mọi thay đổi code PHẢI được delegate cho agent chuyên biệt.

### Quyền ghi của agent — đọc bảng này trước khi route

Route sai vào agent read-only thì nó trả về phân tích rồi dừng, mất một vòng.

| Nhóm | Agent | Ghi được? |
|------|-------|-----------|
| Plan / phân tích | `unity-tech-lead-orchestrator`, `unity-project-analyst`, `Plan`, `Explore`, `code-archaeologist` | ❌ read-only |
| Review / chẩn đoán | `unity-code-reviewer`, `code-reviewer`, `unity-performance-optimizer` | ❌ read-only |
| Implement | `unity-build-engineer`, `unity-tools-programmer`, `unity-cloud-engineer`, `unity-data-engineer`, `unity-gameplay-programmer`, `unity-qa-engineer`, `unity-security-engineer`, `unity-mobile-developer` | ✅ |
| Tài liệu | `documentation-specialist` | ✅ (không có Bash) |

### Routing table

| Loại task | Agent PHẢI dùng | KHÔNG được làm |
|-----------|-----------------|----------------|
| Task đa bước, mở đầu một phase | `unity-tech-lead-orchestrator` để plan | Nhảy vào code ngay |
| Build pipeline, CLI batchmode, content update, CI/CD | `unity-build-engineer` | Main tự config |
| Editor window / tab / inspector / UXML + USS | `unity-tools-programmer` | Main tự code UI |
| Runtime CDN layer (`Runtime/Cdn/**`) | `unity-cloud-engineer` | Main tự code |
| Tích hợp vào code package cũ (`AssetLoader`, scope, `ProgressInfo`) | `unity-gameplay-programmer` | Sửa xuyên tầng không hỏi |
| Serialization, `content_state.bin`, `build-manifest.json` | `unity-data-engineer` | Main tự code |
| Auth header, signed URL, WAF, threat model | `unity-security-engineer` | Tự quyết định bảo mật |
| Test EditMode/PlayMode, fault injection, local server | `unity-qa-engineer` | Ship không test |
| Ma trận thiết bị, 4G/WiFi, dung lượng đầy | `unity-mobile-developer` | Bỏ qua ràng buộc platform |
| Đo alloc vòng progress, throughput download | `unity-performance-optimizer` | Đoán mò không profile |
| Review trước khi merge | `unity-code-reviewer` | Merge thẳng |
| Sửa tài liệu CDN | `documentation-specialist` | Sửa một bản ngôn ngữ (xem §Invariants) |
| Khảo sát rộng, tìm code nhiều nơi | `Explore` | Grep thủ công từng file trong main |

### Flow bắt buộc cho một phase mới

```
Bước 1: unity-tech-lead-orchestrator  → đọc doc phase, ra plan + danh sách task
Bước 2: implementer agent phù hợp     → code theo plan (song song được thì fan-out)
Bước 3: unity-code-reviewer           → review, đối chiếu Tiêu chí thoát của phase
Bước 4: implementer agent             → vá theo finding của reviewer
Bước 5: COMPILE                       → chạy Unity batchmode, phải 0 error CS
Bước 6: CHẠY THẬT                     → thực thi và kiểm tác dụng phụ mong đợi
```

**Bước 4 không được bỏ và không được làm ở main.** `unity-code-reviewer` là read-only — nó chỉ ra lỗi,
không sửa được. Finding phải quay lại đúng agent đã viết đoạn code đó.

**Bước 5 và 6 không phải tuỳ chọn.** Xem §Ba tầng kiểm chứng. "Agent báo xong" và "xong" là hai
trạng thái khác nhau.

---

## Ba tầng kiểm chứng (rút từ Phase 0)

Mỗi tầng bắt đúng loại lỗi mà tầng trước để lọt. Bỏ tầng nào là mù loại lỗi đó.

| Tầng | Bắt được | Ví dụ thật ở Phase 0 |
|------|----------|----------------------|
| Đọc code | Sai logic, sai giá trị, thiếu liên kết | Token `[PlayerVersion]`, cờ default `true`, `Instance` không ai dùng |
| **Compile** | API bịa, shadow, thiếu `using` | 5 API không tồn tại, 1 CS0136, 2 CS0246 — **review 6 vòng không thấy** |
| **Chạy thật** | API đúng mà vô dụng, config sai, false green | `RemoteCatalogHashFilePath` luôn `null`; content build được mà không load nổi |

### Quy tắc cứng

1. **Không commit code Editor/runtime chưa qua một lượt compile.** Không có ngoại lệ.

2. **Không tin exit code của Unity.** `-executeMethod` mà assembly hỏng thì method không chạy và Unity
   **vẫn exit 0**. Mọi lệnh batchmode phải: grep `error CS` trong log, **và** kiểm tác dụng phụ mong đợi
   có thật không (file sinh ra? `git status` đổi?).

3. **Mọi CLI phải gate trên `EditorUtility.scriptCompilationFailed`** và tự kiểm tác dụng của chính nó
   trước khi báo thành công.

4. **Khẳng định phải có bằng chứng độc lập với cơ chế tạo ra nó.** Test HTTP không được chỉ kiểm
   "load thành công" — phải kiểm server *thực sự nhận được request*. Nếu không, Addressables ở Fast Mode
   đọc AssetDatabase và test xanh mà chứng minh số không.

5. **Test không được tự vá điều kiện tiên quyết của nó.** Vá được nghĩa là không kiểm được. `[SetUp]`
   phải **khẳng định** môi trường đúng và fail to tiếng kèm lệnh cần chạy, không được tự sửa.

6. **Test phải chạy hai lần liên tiếp ra cùng kết quả.** State của Unity (bundle cache, play mode script)
   sống xuyên qua các lần chạy. Xanh một lần không có nghĩa gì.

### Khi delegate task đụng API

Dặn "hãy xác minh" **không có tác dụng ổn định** — đã thất bại 5 lần. Cái có tác dụng:

- **Dán sẵn chữ ký API vào prompt**, kèm `file:line` **và namespace**. Thiếu namespace vẫn ra CS0246.
- **Bắt chỉ ra dòng khai báo kèm access modifier**, không phải "có tồn tại không".
  `AddAssetEntry` có thật — nhưng `internal`. Tên đúng, dùng sai.
- **Bắt chỉ ra *cơ chế*, không phải bằng chứng.** "Hàm nào expand token này?" ra kết quả đúng;
  "tìm bằng chứng token này đúng" ra một trích dẫn đúng chuỗi nhưng sai cơ chế.

### Kiểu thất bại đắt nhất

Một **bảng đảm bảo viết ra để bảo vệ quyết định đã chọn**, thay vì để thử phá nó. Nó trông giống
verification, đọc rất thuyết phục, và đã gây 4 quyết định sai trong Phase 0. Dấu hiệu nhận biết: toàn ✅,
không có dòng nào nói "chỗ này tôi chưa chắc".

Khi agent nộp bảng như vậy, đừng đọc bảng — chạy thử.

### Flow cho bug / compile error

```
Bước 1: unity-code-reviewer          → chẩn đoán nguyên nhân (read-only)
Bước 2: implementer agent tương ứng  → áp fix
Bước 3: nếu là vấn đề hiệu năng      → unity-performance-optimizer đo (read-only) → implementer vá
```

### CRITICAL: sau khi nhận kết quả từ agent

- **KHÔNG** tự implement trong main conversation.
- **PHẢI** delegate tiếp cho implementer phù hợp.
- Plan chạm nhiều tầng → tách theo tầng, mỗi tầng một agent, đừng dồn một agent làm hết.
- Agent trả về báo cáo cho tôi, không phải cho user — main conversation chịu trách nhiệm tóm tắt lại.

### Khi nào ĐƯỢC xử lý trực tiếp ở main

- Trả lời câu hỏi kiến thức (Addressables API, giải thích concept, so sánh phương án).
- Giải thích code hiện có mà không sửa.
- Đề xuất approach khi chưa viết code.
- Sửa typo / comment / tên biến đơn lẻ trong **một** file.
- File config, `.meta`, `.asset`, `.gitignore`, workflow YAML.
- **Bug phát sinh trong cùng conversation** khi main đã có đủ context về code vừa viết.
- Compile error từ code mà agent vừa viết trong cùng session.
- Thao tác git, kiểm tra trạng thái repo, chạy lệnh chẩn đoán.

---

## Chạy song song để rút ngắn wall-clock

Fan-out nhiều agent trong **một message** khi các task độc lập. Chỉ tuần tự khi có phụ thuộc thật.

| Phase | Chạy song song được | Phải tuần tự vì |
|-------|---------------------|-----------------|
| 0 | `0.3/0.5/0.6` (config, profile, path) ∥ `0.7` (local server) ∥ `0.9/0.10` (Validator, Server tab) | `0.1` nâng Addressables chặn tất cả — làm trước, một mình |
| 1 | `1.1–1.3` (CLI, content-state) ∥ `1.6/1.7` (verifier, manifest) | `1.9–1.11` (tab UI) cần `ContentStateManager` và `ContentDiff` có mặt trước |
| 2 | `2.1/2.2` (result, settings) ∥ `2.8` (network policy) | `2.3` decorator phải xong trước `2.5` init — ràng buộc thứ tự cài hook |
| 3 | `3.1` (retry policy, thuần C#) ∥ `3.2/3.3` (download) | `3.7` error mapping cần verify exception type trên bản Addressables đã ghim |
| 4 | `4.1–4.3` (cache) ∥ `4.4` (telemetry) ∥ `4.6/4.8` (tab UI) | — |
| 5 | Test ∥ device matrix ∥ docs ∥ `5.9` Catalog Inspector | `5.7` gỡ `[Obsolete]` làm cuối cùng |

Không fan-out các agent cùng sửa **một file** — sẽ đè nhau. Cùng file thì tuần tự, hoặc dùng
`isolation: "worktree"`.

---

## Handoff contract

Mỗi lần delegate, prompt gửi cho agent PHẢI mang đủ context sau — agent không thấy được conversation này:

```
Package:      com.game.addressables 4.0.1 → target 4.1.0
Unity:        2022.3+  |  Addressables: 2.3.1 (Phase 0 nâng lên 2.9.x)
Phase:        <số> — <tên>
Task:         <mã task, vd 1.9> — <mô tả>
Doc governing: Documentation/CDN_SYSTEM.html#<anchor>   ← đọc mục này trước khi code
Ràng buộc:    <trích ràng buộc liên quan, vd §9 settings contract>
Tiêu chí thoát: <copy nguyên văn từ doc, đây là thứ reviewer sẽ đối chiếu>
Files được phép chạm: <liệt kê tường minh>
```

Thiếu `Doc governing` và `Tiêu chí thoát` thì agent sẽ tự bịa yêu cầu. Luôn trích, đừng tóm tắt lại.

---

## Invariants của repo — vi phạm là phải sửa ngay

1. **Tài liệu CDN có hai bản ngôn ngữ.** Mọi thay đổi phải sửa **cả** `CDN_SYSTEM.html` **và**
   `CDN_SYSTEM_VI.html`, và **tập `id` của các mục phải giống hệt nhau** — nút chuyển ngôn ngữ giữ vị trí
   đọc bằng URL fragment. Kiểm tra:
   ```
   diff <(grep -o 'id="[a-z0-9-]*"' Documentation/CDN_SYSTEM.html    | sort -u) \
        <(grep -o 'id="[a-z0-9-]*"' Documentation/CDN_SYSTEM_VI.html | sort -u)
   ```

2. **Settings contract chỉ có một nguồn.** Luật ở doc §9 được mã hoá trong `SettingsContract.cs`, dùng
   chung cho `CatalogVerifier` (CI) và `SettingsValidatorTab` (GUI). Không viết luật lần thứ hai.

3. **`#if UNITASK_PRESENT` phải có chữ ký kép.** Mọi API async public đều cần cả nhánh `UniTask` và `Task`.
   Đây là quy ước sẵn có của package, không phải lựa chọn.

4. **Không sentinel value.** Thao tác có thể thất bại thì trả `CdnResult<T>` / `LoadResult<T>`. Không trả
   `0`, `null`, `false` để ám chỉ hai chuyện khác nhau — đây chính là bug đang có ở
   `GetDownloadSizeAsync`.

5. **UI Editor dùng control của Unity.** `Toolbar`, `HelpBox`, `ListView`, `ProgressBar`, `EnumField`;
   màu lấy từ `--unity-colors-*`. USS **không có** grid, `::before`, `box-shadow`, `rem`, `@media`.
   Chi tiết ở doc §5.11.

6. **API cũ giữ tới 5.0.0.** Đánh `[Obsolete]` ở 4.x, không xoá. Xem doc §11.

7. **Commit chỉ phần việc của phiên đang chạy.** Không `git add -A`, không `git add .`, không `commit -a`
   — liệt kê tay từng đường dẫn. Repo này có nhiều phiên chạy song song trên cùng branch.

---

## Docs

| Doc | Nội dung |
|-----|----------|
| [CDN System](Documentation/CDN_SYSTEM.html) · [VI](Documentation/CDN_SYSTEM_VI.html) | Thiết kế kỹ thuật, kế hoạch 6 phase, hạ tầng & vận hành. **Nguồn chân lý cho workstream CDN.** |
| [Automation Guide](Documentation/ADDRESSABLE_AUTOMATION_GUIDE.md) | Hệ rule-based: filter, provider, workflow |
| [Rule Examples](Documentation/RULE_SYSTEM_EXAMPLES.md) | 8 ví dụ dùng ngay |
| [Editor Tools](Documentation/EDITOR_TOOLS_GUIDE.md) | Các editor window hiện có |
| [Troubleshooting](Documentation/TROUBLESHOOTING.md) | Lỗi thường gặp |
| [CHANGELOG](Packages/com.game.addressables/CHANGELOG.md) | Lịch sử phiên bản |

Anchor hay dùng khi delegate: `#d-config` (settings contract §9) · `#d-errors` (error model §8) ·
`#d-modules` (đặc tả module §5) · `#p-phase0`…`#p-phase5` · `#i-layout` (bố cục CDN §2).
