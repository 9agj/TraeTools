# TraeTools 账号联动同步设计（2026-09-16）

## 背景与问题

用户反馈三处不一致：

1. **账号切换联动不同步**：设置页账号管理点击账号后，仪表盘只更新展示字段（账号名/会员/奖励），未重新拉取该账号的真实状态与积分，导致「账号管理的签到状态」与「仪表盘的账号切换状态」不一致。
2. **多账号签到不完整**：一键签到只签当前激活账号；自动签到虽遍历 Enabled，但用户测试（旧版）只看到单账号结果，需要显式对齐源项目。
3. **账号管理选中高亮未落在卡片本体**：`.acct-card` 的子代选择器 `ListBoxItem:selected .acct-card` 未生效，选中时仍是卡片外圈变蓝。

目标：对照源项目 TraeCheckin（`Forms/MainForm.cs` 的 `RefreshAllAsync` / `DoCheckinAsync` / `_stateMarks` / `RefreshAccountCombo`），把账号管理—仪表盘—签到三处状态完全同步。

## 设计

### A. 账号切换联动 → 对齐源 `RefreshAllAsync`

- `DashboardViewModel` 新增异步 `RefreshAllAsync()`：
  1. 读 `cfg.ActiveAccountId` 对应账号（无则显示占位并返回）
  2. `EnsureDeviceId`（缺号补发，复用 CheckinViewModel 逻辑 → 提取为静态工具 AccountHelpers.EnsureDeviceId）
  3. `GetStatusWithValidTokenAsync`（复用 token 换新：status 探测 → Session 静默换新 → 保存）
  4. `GetRemainingCreditsAsync` 更新 `RemainingCredits`（失败保留旧值，不清零）
  5. 更新 `CheckinStatus`（今日已签到/今日可签到/签到未开启/获取失败）、`TodayReward`、`CurrentAccount`、`IsMember`
- 触发链：设置页 `OnSelectedAccountChanged` → `cfg.ActiveAccountId = id; Save` → `MainViewModel.NotifyActiveAccountChanged()` → 调 `_dashboard.RefreshAllAsync()`（async void 触发）+ `_checkin.Reload()` + 账号状态刷新
- `MainViewModel.Navigate("dashboard")` 时亦调用 `RefreshAllAsync()`，兜底任何入口都同步

### B. 多账号签到 → 对齐源 `DoCheckinAsync`

- 一键签到 `DoCheckin` 改为遍历**全部 Enabled 账号**：
  - 每个账号：`EnsureDeviceId` → `EnsureValidTokenAsync` → `ClaimAsync` → 计分 → 写历史 → 刷新剩余积分
  - 单账号失败不阻断；未登录/会话失效账号跳过并计入结果
  - 汇总结果飞书批量推送（复用 `NotifyFeishuBatchAsync`）
  - `StatusMessage` 汇总：`"共 N 个账号，成功 M 个，共获得 X 积分"`
- 自动签到 `TryAutoCheckinAsync` 已是该逻辑（遍历+推送），保持；本次确保其与一键签到共用同一签到内核 `CheckinAllAccountsAsync(results)`，消除重复实现。

### C. 账号管理选中高亮 → 卡片本体变蓝

- `SettingsView`：弃用 `ListBoxItem:selected .acct-card` 子代选择器
- 改用绑定式：
  - 卡片 Border 加 `Classes.selected="{Binding $parent[ListBoxItem].IsSelected}"`
  - `ListBox.Styles` 增加 `Border.acct-card.selected`（蓝底 `#1A3B82F6`、蓝框 `AccentBrush`）
  - 同时隐藏 ListBoxItem 默认选中底色：`ListBoxItem:selected /template/ ContentPresenter { Background=Transparent }`（需 `x:SetterTargetType="ContentPresenter"`）
- 编译绑定注意：`$parent[ListBoxItem].IsSelected` 在 `x:DataType` 下需显式 `x:CompileBindings="False"` 或确认可解析；若校验失败则用 `{ReflectionBinding ...}`

### D. 账号签到状态与仪表盘同步（对齐源 `_stateMarks`）

- `SettingsViewModel` 新增 `RefreshAccountStatesAsync()`：
  - 对每个账号：本地 `LastCheckinDate == 今天` → 「已签到」；否则探测 token 有效性（`GetStatusAsync` 或快速/缓存判定）：
    - token 有效 → 「待签到」
    - token 失效但 Session 可换新 → 换新后按 已签/待签 显示
    - 双双失效 → 「需重登」
  - 更新 Accounts 集合中对应身份证 `Status`/`StatusType`
- 触发：账号切换后、进入设置页时、签到完成后调用。保证「账号管理显示」与「仪表盘当前账号」一致。

## 涉及文件

- `ViewModels/DashboardViewModel.cs`（新增 RefreshAllAsync）
- `ViewModels/CheckinViewModel.cs`（一键签到改遍历；提取 CheckinAllAccountsAsync；AccountHelpers 工具）
- `ViewModels/SettingsViewModel.cs`（OnSelectedAccountChanged 触发完整刷新；RefreshAccountStatesAsync；账号状态标记）
- `ViewModels/MainViewModel.cs`（Navigate 兜底刷新；NotifyActiveAccountChanged 调完整刷新）
- `Views/SettingsView.axaml`（选中高亮绑定式 + 隐藏默认选中底色）
- 新增 `Services/AccountHelpers.cs`（EnsureDeviceId 等跨 VM 复用）

## 数据流

设置页点选账号 → ActiveAccountId 变化 → NotifyActiveAccountChanged →
  ├─ DashboardViewModel.RefreshAllAsync（真实验证状态/积分/奖励）
  ├─ CheckinViewModel.Reload（会员/日历/记录）
  └─ SettingsViewModel.RefreshAccountStatesAsync（账号状态标记）

一键/自动签到 → CheckinAllAccountsAsync（全部 Enabled）→ 历史+飞书汇总 → RefreshAccountStatesAsync + ReloadCalendar。

## 错误处理

- 网络/解析失败：仪表盘保留旧展示值，不清零（沿用现有守卫）
- 单个账号签到失败：不阻断其余，进入结果汇总
- 无账号：仪表盘显示占位「未添加账号 / 未登录」
- token 与 session 均失效：标记「需重登」，不阻塞其它账号

## 验证

- 构建 0 错误 0 警告
- 设置页选中账号 A / B：仪表盘账号名、今日状态、剩余积分、单日奖励随账号变化且来自真实接口
- 账号管理显示状态与仪表盘一致
- 一键签到：全部 Enabled 账号均被签，返回汇总文案与飞书汇总
- 选中账号卡片本体变蓝（非外圈）
