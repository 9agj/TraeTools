# TraeTools 账号联动同步 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 账号管理(设置页) — 仪表盘 — 签到 三处账号状态完全同步，并把一键签到改为签全部启用账号。

**Architecture:** 新增共享工具 `ViewModels/AccountHelpers.cs`（EnsureDeviceId / EnsureValidTokenAsync），仪表盘新增真实验证刷新 `RefreshAllAsync()`，签到提取统一内核 `CheckinAllAccountsAsync()`，设置页账号状态标记 `RefreshAccountStatesAsync()`，选中高亮改用 `$parent` 绑定式样式。对齐源 TraeCheckin 的 RefreshAllAsync / DoCheckinAsync / _stateMarks 机制。

**Tech Stack:** Avalonia 12.1.2 / .NET 9 / CommunityToolkit.Mvvm。本仓库无测试项目，采用「构建 + 运行验证」代替 TDD（不为本计划新建测试工程，遵循 YAGNI）。

**写入方式重要提示：** 桌面路径文件 Write/Edit 工具受限，所有文件修改必须用 PowerShell `Set-Content -Encoding UTF8`（单引号 here-string `@'...'@` 防止 `{Binding}` 花括号被解析）。

**验证命令：**
```powershell
Get-Process -Name "TraeTools" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
dotnet build "C:\Users\星梦\Desktop\项目开发\TraeTools\TraeTools.csproj" -c Release
# 预期：0 错误 0 警告；之后 Start-Process exe 运行 4 秒不崩溃
```

---

### Task 1: 新建 ViewModels/AccountHelpers.cs（共享：DeviceId 补发 + token 换新）

**Files:**
- Create: `C:\Users\星梦\Desktop\项目开发\TraeTools\ViewModels\AccountHelpers.cs`

- [ ] **Step 1: 写文件**

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using TraeCheckin;
using TraeTools.Models;

namespace TraeTools.ViewModels;

/// <summary>账号相关跨 VM 共享工具（DeviceId 补发 / Token 会话换新）。</summary>
public static class AccountHelpers
{
    /// <summary>缺号则生成不与其它账号重复的 16 位数字设备号（风控要求）。</summary>
    public static void EnsureDeviceId(TraeAccount acc)
    {
        if (!string.IsNullOrWhiteSpace(acc.DeviceId)) return;
        var cfg = MainViewModel.AppConfig;
        var used = new System.Collections.Generic.HashSet<string>(
            cfg?.Accounts.Where(a => a.Id != acc.Id).Select(a => a.DeviceId)
               .Where(d => !string.IsNullOrWhiteSpace(d)) ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        string id;
        do { id = Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString(); }
        while (used.Contains(id));
        acc.DeviceId = id;
    }

    /// <summary>校验/换新 Token：失效时用 Session 静默换新并保存，返回是否持有可用 token。</summary>
    public static async Task<bool> EnsureValidTokenAsync(TraeAccount acc)
    {
        var cfg = MainViewModel.AppConfig;
        var api = MainViewModel.CheckinApi;
        if (cfg == null || api == null) return false;
        if (!string.IsNullOrEmpty(acc.Token))
        {
            var st = await api.GetStatusAsync(acc.Token, acc.DeviceId);
            if (st != null && st.code == 0) return true;
        }
        if (!string.IsNullOrEmpty(acc.Session))
        {
            var renewed = await api.GetUserTokenAsync(acc.Session);
            if (!string.IsNullOrEmpty(renewed))
            {
                acc.Token = renewed;
                acc.AccountUid = TokenUtils.ParseAccountUid(renewed);
                acc.TokenUpdatedAt = DateTime.Now;
                try { cfg.Save(); } catch { }
                return true;
            }
        }
        return false;
    }
}
```

- [ ] **Step 2: 构建验证（此时 AccountHelpers 未使用也允许，先确认可编译）**

Run: `dotnet build "C:\Users\星梦\Desktop\项目开发\TraeTools\TraeTools.csproj" -c Release`
Expected: 0 错误 0 警告（未使用类型不影响）

- [ ] **Step 3: 让 CheckinViewModel 复用（删除其私有 EnsureDeviceId / EnsureValidTokenAsync 重复实现）**

Modify: `ViewModels/CheckinViewModel.cs`
- 删除私有 `EnsureDeviceId(TraeAccount)` 方法
- 删除私有 `EnsureValidTokenAsync(TraeAccount)` 方法
- 把 `EnsureDeviceId(acc)` 调用改为 `AccountHelpers.EnsureDeviceId(acc)`
- 把 `EnsureValidTokenAsync(acc)` 调用改为 `AccountHelpers.EnsureValidTokenAsync(acc)`

- [ ] **Step 4: 构建**

Run: `dotnet build ... -c Release` → 0 错误 0 警告

---

### Task 2: DashboardViewModel.RefreshAllAsync（真实验证状态/积分，对齐源 RefreshAllAsync）

**Files:**
- Modify: `ViewModels/DashboardViewModel.cs`

- [ ] **Step 1: 在 `Reload()` 方法前新增 `RefreshAllAsync()`**

```csharp
    /// <summary>对齐 TraeCheckin.RefreshAllAsync：按激活账号真实拉取状态/积分/单日奖励。</summary>
    public async Task RefreshAllAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var api = MainViewModel.CheckinApi;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg?.Accounts.FirstOrDefault();
            if (acc == null)
            {
                CurrentAccount = "未添加账号";
                CheckinStatus = "未登录";
                return;
            }
            CurrentAccount = string.IsNullOrEmpty(acc.Name)
                ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}"
                : acc.Name!;
            IsMember = acc.IsMember;
            AccountHelpers.EnsureDeviceId(acc);

            double remaining = RemainingCredits;          // 失败保留旧值，不清零
            Models.CheckinStatus? status = null;
            if (api != null && !string.IsNullOrEmpty(acc.Token))
            {
                bool valid = await AccountHelpers.EnsureValidTokenAsync(acc);
                if (valid)
                {
                    var st = await api.GetStatusAsync(acc.Token ?? "", acc.DeviceId);
                    if (st != null && st.code == 0) status = st;
                    var r = await api.GetRemainingCreditsAsync(acc.Token ?? "", acc.DeviceId);
                    if (r >= 0 && string.IsNullOrEmpty(api.LastError))
                    {
                        remaining = r;
                        if (cfg != null) { cfg.LastRemaining = r; try { cfg.Save(); } catch { } }
                    }
                }
            }

            RemainingCredits = (int)remaining;
            if (status != null)
            {
                CheckinStatus = status.checked_in ? "今日已签到 ✓" : "今日可签到";
                TodayReward = "+" + (int)(status.credits + (acc.IsMember ? status.extra_credits : 0));
            }
            else
            {
                CheckinStatus = string.IsNullOrEmpty(acc.Token) ? "未登录" : "需重登";
            }
        }
        catch { /* 失败保留旧值 */ }
    }
```

- [ ] **Step 2: `Reload()` 保留（仅同步展示字段，供轻量刷新用），构建**

Run: build → 0 错误 0 警告

---

### Task 3: MainViewModel 接通完整刷新（通知 + 切页兜底）

**Files:**
- Modify: `ViewModels/MainViewModel.cs`

- [ ] **Step 1: NotifyActiveAccountChanged 改为完整刷新 + 账号状态**

```csharp
    /// <summary>账号管理切换激活账号后：仪表盘完整刷新 + 签到页 + 账号状态同步。</summary>
    public static async void NotifyActiveAccountChanged()
    {
        if (Instance is not { } vm) return;
        await vm._dashboard.RefreshAllAsync();
        vm._checkin.Reload();
        await vm._settings.RefreshAccountStatesAsync();
    }
```

- [ ] **Step 2: Navigate 切页兜底（dashboard / settings 用完整刷新）**

```csharp
            case "dashboard":
                _ = _dashboard.RefreshAllAsync();
                CurrentPage = _dashboard;
                CurrentPageKey = "dashboard";
                break;
            case "settings":
                _ = _settings.RefreshAccountStatesAsync();
                CurrentPage = _settings;
                CurrentPageKey = "settings";
                break;
```

- [ ] **Step 3: 构建**

Run: build → 0 错误（SettingsViewModel.RefreshAccountStatesAsync 尚未定义，会在 Task 5 定义；若为编译顺序，先临时在 Task 5 前跳过——建议按 Task 顺序执行，Task5 完成后再整体构建）

---

### Task 4: 一键签到改为签全部启用账号（统一内核 CheckinAllAccountsAsync）

**Files:**
- Modify: `ViewModels/CheckinViewModel.cs`

- [ ] **Step 1: 新增统一签到内核**

```csharp
    /// <summary>对齐 TraeCheckin.DoCheckinAsync：遍历全部 Enabled 账号签到，单败不阻断。</summary>
    private async Task<(bool Any, List<(string Name, bool Ok, double Gained)> Results)> CheckinAllAccountsAsync()
    {
        var cfg = MainViewModel.AppConfig;
        var results = new List<(string Name, bool Ok, double Gained)>();
        bool any = false;
        if (cfg == null) return (false, results);
        foreach (var acc in cfg.Accounts.Where(a => a.Enabled))
        {
            if (acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today) continue;
            var display = string.IsNullOrEmpty(acc.Name)
                ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id)
                : acc.Name!;
            if (string.IsNullOrEmpty(acc.Token))
            {
                results.Add((display, false, 0));       // 未登录，计入失败汇总
                continue;
            }
            try
            {
                double g = await CheckinOneAccountAsync(acc);
                if (g > 0) any = true;
                results.Add((display, g > 0, g));
            }
            catch { results.Add((display, false, 0)); } // 单账号失败继续下一个
        }
        return (any, results);
    }
```

- [ ] **Step 2: 一键签到 DoCheckin 改为调用统一内核 + 汇总文案**

原单账号逻辑（登录弹窗→今日已签判断→claim→写历史）中「今日已签判断」与「写历史」保留在 CheckinOneAccountAsync；DoCheckin 重构为：

```csharp
    [RelayCommand]
    private async Task DoCheckin()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var api = MainViewModel.CheckinApi;
            if (cfg == null || api == null) { StatusMessage = "服务未初始化"; return; }
            if (cfg.Accounts.Count == 0) { StatusMessage = "没有可用账号，请先在「设置」中添加"; return; }

            var anyEnabledNoToken = cfg.Accounts.Any(a => a.Enabled && string.IsNullOrEmpty(a.Token));
            if (anyEnabledNoToken)
            {
                StatusMessage = "检测到未登录账号，正在打开登录窗口…";
                var target = cfg.Accounts.First(a => a.Enabled && string.IsNullOrEmpty(a.Token));
                bool logged = await PromptLoginAsync(target);
                if (!logged) { StatusMessage = "已取消登录"; return; }
                StatusMessage = "登录成功，正在为所有账号签到…";
            }

            var (any, results) = await CheckinAllAccountsAsync();
            int ok = results.Count(r => r.Ok);
            double total = results.Sum(r => r.Gained);
            StatusMessage = results.Count == 0
                ? "今日所有账号均已签到"
                : $"共 {results.Count} 个账号，成功 {ok} 个，获得 {total:0} 积分";
            ReloadCalendar();
            LoadHistory();
            await NotifyFeishuBatchAsync(results);
            if (any)
            {
                await MainViewModel.NotifyAsyncRefresh();
            }
        }
        catch (Exception ex) { StatusMessage = $"签到异常：{ex.Message}"; }
    }
```

说明：`CheckinOneAccountAsync` 已内含 TodayReward 更新（仅激活账号时）。原有单账号的登录弹窗/今日已签/历史写在重构中由统一内核取代，请一并删除重复段落。

- [ ] **Step 3: 自动签到 TryAutoCheckinAsync 复用统一内核**

```csharp
    public async Task<bool> TryAutoCheckinAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || !cfg.AutoCheckinEnabled) return false;
            if (string.IsNullOrWhiteSpace(cfg.AutoCheckinTime)) return false;
            if (!TimeSpan.TryParse(cfg.AutoCheckinTime, out var due)) return false;
            if (DateTime.Now.TimeOfDay < due) return false;
            if (MainViewModel.CheckinApi == null) return false;

            var (any, results) = await CheckinAllAccountsAsync();
            if (any) { StatusMessage = "自动签到完成 ✓"; ReloadCalendar(); }
            if (results.Count > 0) await NotifyFeishuBatchAsync(results);
            return any;
        }
        catch { return false; }
    }
```

- [ ] **Step 4: MainViewModel 新增 `NotifyAsyncRefresh`（供签到后同步仪表盘/账号状态）**

```csharp
    /// <summary>签到完成后同步仪表盘与账号状态。</summary>
    public static async Task NotifyAsyncRefresh()
    {
        if (Instance is not { } vm) return;
        await vm._dashboard.RefreshAllAsync();
        await vm._settings.RefreshAccountStatesAsync();
    }
```

- [ ] **Step 5: 构建**

Run: build → 0 错误 0 警告（若 SettingsViewModel.RefreshAccountStatesAsync 未定义先跳过，Task5 补齐后统一构建）

---

### Task 5: SettingsViewModel 账号状态实时同步（RefreshAccountStatesAsync）

**Files:**
- Modify: `Models/AccountInfo.cs`（Status/StatusType 改为可通知）
- Modify: `ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: AccountInfo 改为可通知（CommunityToolkit）**

`Models/AccountInfo.cs`：类声明改 `public partial class AccountInfo : ObservableObject`（using `CommunityToolkit.Mvvm.ComponentModel`），`Status` 与 `StatusType` 改 `[ObservableProperty]` 私有字段 + 自动属性——为最小改动，仅这两个字段需要通知，其余保持普通属性。实现：

```csharp
public partial class AccountInfo : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Initial { get; set; } = string.Empty;
    public string Color { get; set; } = "#3B82F6";
    public string CreatedAt { get; set; } = string.Empty;
    public int Carriers { get; set; }
    public string Similarity { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _statusType = "ok";
    // 其余 StatusFgBrush/StatusBgBrush 读取 StatusType，保持不变
}
```

注意：`Status`/`StatusType` 的赋值点（PopulateAccounts 等）不变（仍用 `Status = ...`，生成属性兼容）。

- [ ] **Step 2: RefreshAccountStatesAsync 实现**

```csharp
    /// <summary>对齐源 _stateMarks：刷新每个账号的会话/签到状态标记（已签到/待签到/需重登/未登录）。</summary>
    public async Task RefreshAccountStatesAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || cfg.Accounts.Count == 0) return;
            foreach (var acc in cfg.Accounts)
            {
                var item = Accounts.FirstOrDefault(a => a.Id == acc.Id);
                if (item == null) continue;
                if (acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today)
                {
                    item.Status = "已签到"; item.StatusType = "ok"; continue;
                }
                bool valid = string.IsNullOrEmpty(acc.Token)
                    ? false
                    : await AccountHelpers.EnsureValidTokenAsync(acc);
                if (!valid)
                {
                    item.Status = "需重登"; item.StatusType = "warn"; continue;
                }
                bool checkedIn = false;
                var st = await MainViewModel.CheckinApi?.GetStatusAsync(acc.Token ?? "", acc.DeviceId);
                if (st is { } s2 && s2.code == 0) checkedIn = s2.checked_in;
                item.Status = checkedIn ? "已签到" : "待签到";
                item.StatusType = checkedIn ? "ok" : "info";
            }
        }
        catch { /* 状态刷新失败保留原样 */ }
    }
```

- [ ] **Step 3: OnSelectedAccountChanged 已触发 NotifyActiveAccountChanged（它内部会调 RefreshAccountStatesAsync），无需重复；构建**

Run: build → 0 错误 0 警告

---

### Task 6: SettingsView 选中高亮落在卡片本体（绑定式）

**Files:**
- Modify: `Views/SettingsView.axaml`

- [ ] **Step 1: 替换 ListBox.Styles 与卡片样式**

删除现有：
```xml
<Style Selector="ListBoxItem:selected .acct-card" x:SetterTargetType="Border">
    <Setter Property="Background" Value="#1A3B82F6" />
    <Setter Property="BorderBrush" Value="{StaticResource AccentBrush}" />
</Style>
```

替换为：
```xml
<Style Selector="ListBoxItem:selected /template/ ContentPresenter" x:SetterTargetType="ContentPresenter">
    <Setter Property="Background" Value="Transparent" />
</Style>
<Style Selector="Border.acct-card.selected" x:SetterTargetType="Border">
    <Setter Property="Background" Value="#1A3B82F6" />
    <Setter Property="BorderBrush" Value="{StaticResource AccentBrush}" />
</Style>
```

- [ ] **Step 2: 卡片 Border 加选中类绑定（用 ReflectionBinding 规避编译绑定 $parent 校验）**

```xml
<Border CornerRadius="10" Classes="acct-card"
        Classes.selected="{ReflectionBinding $parent[ListBoxItem].IsSelected, Mode=OneWay}"
        Background="#FFF8FAFC" BorderBrush="#FFE2E8F0" BorderThickness="1"
        Padding="10,8" Margin="2,4" HorizontalAlignment="Stretch">
```

- [ ] **Step 3: 构建验证**

Run: build → 0 错误 0 警告（若 `Classes.selected` 绑定语法报错，改用绑定的辅助属性：在 AccountInfo 增加 `[ObservableProperty] private bool _isSelected;` 并在 View 的 ListBox.SelectionChanged 事件里同步，作为后备方案写入本 Task 注释）

---

### Task 7: 端到端构建 + 运行验证

- [ ] **Step 1: 完整构建**

Run: `Get-Process TraeTools -ErrorAction SilentlyContinue | Stop-Process -Force; dotnet build "...\TraeTools.csproj" -c Release` → 0 错误 0 警告

- [ ] **Step 2: 运行冒烟**

Run: `Start-Process "C:\Users\星梦\Desktop\项目开发\TraeTools输出\TraeTools.exe"; Start-Sleep 4; (Get-Process TraeTools) 存活` → 进程存活不崩溃

- [ ] **Step 3: 手动验收清单（告知用户）**
- 设置页选中账号 A→仪表盘：账号名/今日状态/剩余积分/单日奖励随 A 且来自真实接口
- 账号管理显示状态与仪表盘一致（已签到/待签到/需重登）
- 一键签到：全部 Enabled 账号均被签，状态栏出现"共 N 个账号，成功 M 个"
- 选中账号卡片本体变蓝（不再外圈变蓝）

